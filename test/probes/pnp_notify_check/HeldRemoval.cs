// A removal while another program holds a HID child open, run through
// DeviceManager.RemoveDevice's own order with no device: the full cleanup's
// fast removal (CM_Disable_DevNode, then CM_Uninstall_DevNode) and the
// removal by SetupDiRemoveDevice that the sweeps and a create make. On the
// Celeron on 2026-10-25 the full cleanup's disable of a held DualShock 4's
// HID child was refused (Kernel-PnP 225), and its CM_Uninstall_DevNode
// deleted the child's record 18 ms later (Kernel-PnP 420) while the holder
// still held it: the same holder refused the parent's disable 5.5 s after.
// Learn's order for CM_Uninstall_DevNode removes a present device with
// CM_Query_And_Remove_SubTree first. A refused SetupDiRemoveDevice deleted a
// held child's Driver and Service on 2026-10-19, and every later child of
// that identity stopped with problem 19.
//
// The rule the checks hold every removal to: no call that deletes a record
// (SetupDiRemoveDevice, CM_Uninstall_DevNode, pnputil /remove-device, devcon
// remove) reaches a devnode that is in the tree and started. The removal
// query comes first. When a program refuses it, the device and its records
// stay, unless the driver has failed its device and Windows has taken it
// down by surprise removal, which no handle can refuse.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using Microsoft.Win32.SafeHandles;

internal static class HeldRemoval
{
    const string Parent = @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_HELD";
    const string Child = @"HID\HIDCLASS\1&5e1d&0&0000";
    const uint CR_SUCCESS = 0x00, CR_NO_SUCH_DEVINST = 0x0D;
    const uint CM_LOCATE_DEVNODE_NORMAL = 0x0, CM_LOCATE_DEVNODE_CANCELREMOVE = 0x2;

    /// <summary>A devnode as the checks see it.</summary>
    sealed class Node
    {
        public bool InTree = true, Started = true, Record = true;
        public override string ToString() =>
            (InTree ? Started ? "started" : "in the tree, not started" : "out of the tree") + (Record ? ", record kept" : ", record gone");
    }

    /// <summary>A ROOT parent and its HID child, and a holder that may keep
    /// the child open with no device notification, as Steam keeps a Sony
    /// pad's. Each hooked call is logged in order, and so is every record
    /// deletion made on a started devnode.</summary>
    sealed class Machine
    {
        public readonly Dictionary<string, Node> Nodes = new(StringComparer.OrdinalIgnoreCase)
        {
            [Parent] = new Node(),
            [Child] = new Node(),
        };
        public readonly List<string> Calls = new();
        public readonly List<string> Violations = new();
        public readonly List<string> Lines = new();
        public int CancelLocates;
        public bool Holder;
        /// <summary>The query fails although the subtree leaves the tree, as
        /// one whose devnode left between the locate and the call.</summary>
        public bool QueryFailsAnyway;
        /// <summary>What the removal added to DeviceManager.RemovalsLeftHere,
        /// the count a dispose keeps its lease on.</summary>
        public long Left;
        readonly Dictionary<string, List<Action>> _removed = new(StringComparer.OrdinalIgnoreCase);
        // A driver's failure can come from another thread (Fail).
        readonly object _gate = new();

        public Machine(bool holder) => Holder = holder;

        public IReadOnlyList<string> ChildrenOf(string id) =>
            string.Equals(id, Parent, StringComparison.OrdinalIgnoreCase) ? new[] { Child } : Array.Empty<string>();

        public bool IsStarted(string id)
        {
            lock (_gate) return Nodes.TryGetValue(id, out var n) && n.InTree && n.Started;
        }

        public uint Locate(string id, uint flags)
        {
            lock (_gate)
            {
                if ((flags & CM_LOCATE_DEVNODE_CANCELREMOVE) != 0) CancelLocates++;
                if (!Nodes.TryGetValue(id, out var n)) return CR_NO_SUCH_DEVINST;
                bool found = flags == CM_LOCATE_DEVNODE_NORMAL ? n.InTree : n.InTree || n.Record;
                return found ? CR_SUCCESS : CR_NO_SUCH_DEVINST;
            }
        }

        /// <summary>A REMOVED registration, delivered when the devnode leaves
        /// the tree, as CM_Register_Notification delivers it.</summary>
        public IDisposable? Register(string id, Action onRemoved)
        {
            if (!_removed.TryGetValue(id, out var list)) _removed[id] = list = new List<Action>();
            list.Add(onRemoved);
            return new Registration(() => list.Remove(onRemoved));
        }

        sealed class Registration : IDisposable
        {
            readonly Action _end;
            public Registration(Action end) => _end = end;
            public void Dispose() => _end();
        }

        void Leave(string id)
        {
            var n = Nodes[id];
            if (!n.InTree) return;
            n.InTree = false;
            n.Started = false;
            if (_removed.TryGetValue(id, out var list))
                foreach (var a in list.ToArray()) a();
        }

        IEnumerable<string> Below(string id) => ChildrenOf(id).SelectMany(c => Below(c).Append(c));

        /// <summary>CM_Query_And_Remove_SubTreeW: the holder refuses while it
        /// holds a started child in the subtree, and a refusal changes
        /// nothing. Otherwise the subtree leaves the tree, children first,
        /// and the records stay.</summary>
        public bool Query(string id)
        {
            Calls.Add($"query {id}");
            var subtree = Below(id).Append(id).ToList();
            if (Holder && subtree.Exists(x => string.Equals(x, Child, StringComparison.OrdinalIgnoreCase)) && IsStarted(Child))
                return false;
            foreach (string x in subtree) Leave(x);
            return !QueryFailsAnyway;
        }

        /// <summary>SetupDiRemoveDevice or CM_Uninstall_DevNode. A started
        /// devnode is a breach of the rule, and it leaves the tree with its
        /// record gone, as the Celeron's CM_Uninstall_DevNode left the
        /// child.</summary>
        public bool Delete(string kind, string id)
        {
            Calls.Add($"{kind} {id}");
            if (!Nodes.TryGetValue(id, out var n)) return true;
            if (IsStarted(id)) Violations.Add($"{kind} {id} while it was started");
            Leave(id);
            n.Record = false;
            return true;
        }

        public void Fallbacks(string id)
        {
            Calls.Add($"fallbacks {id}");
            if (IsStarted(id)) Violations.Add($"pnputil and devcon on {id} while it was started");
        }

        /// <summary>The driver fails its device on request: Windows takes
        /// the children out of the tree by surprise removal, held or not, and
        /// the parent stays in it, no longer started (DN_WILL_BE_REMOVED),
        /// until the last handle closes.</summary>
        public bool Fail(string id)
        {
            lock (_gate)
            {
                Calls.Add($"fail {id}");
                foreach (string x in Below(id)) Leave(x);
                Nodes[id].Started = false;
                return true;
            }
        }
    }

    sealed class StandInLock : IDisposable
    {
        public void Dispose() { }
    }

    /// <summary>Runs <paramref name="act"/> with every PnP call the SDK
    /// makes answered by <paramref name="m"/>.</summary>
    static T Hooked<T>(Machine m, Func<T> act)
    {
        DeviceManager.LocateHook = m.Locate;
        DeviceManager.RemovedHook = m.Register;
        DeviceManager.PnpLockHook = () => new StandInLock();
        DeviceOrchestrator.DiagTap = line => { lock (m.Lines) m.Lines.Add(line); };
        DeviceManager.RemovalCallsHook = new DeviceManager.RemovalCalls
        {
            Children = m.ChildrenOf,
            Remove = x => m.Delete("remove", x),
            Uninstall = x => m.Delete("uninstall", x),
            Close = _ => 0,
            Fallbacks = m.Fallbacks,
            Query = m.Query,
            Started = m.IsStarted,
        };
        try { return act(); }
        finally
        {
            DeviceManager.RemovalCallsHook = null;
            DeviceManager.LocateHook = null;
            DeviceManager.RemovedHook = null;
            DeviceManager.PnpLockHook = null;
            DeviceOrchestrator.DiagTap = null;
        }
    }

    /// <summary>DeviceManager.RemoveDevice on <paramref name="id"/> with
    /// the fallbacks forced, as the full cleanup and the sweeps call it, and
    /// <paramref name="fail"/> standing for the request that the driver fail
    /// its device.</summary>
    static (bool Removed, long Ms) Remove(Machine m, string id, bool fast, Func<bool>? fail = null) =>
        Hooked(m, () =>
        {
            long left = DeviceManager.RemovalsLeftHere;
            var sw = Stopwatch.StartNew();
            bool removed = DeviceManager.RemoveDevice(id, timeoutMs: 2000, fast: fast, forceFallbacks: true, failDevice: fail);
            long ms = sw.ElapsedMilliseconds;
            m.Left = DeviceManager.RemovalsLeftHere - left;
            return (removed, ms);
        });

    static string Describe(Machine m, (bool Removed, long Ms) r) =>
        $"calls [{string.Join(", ", m.Calls)}], " +
        (m.Violations.Count == 0 ? "no record deleted while started" : string.Join("; ", m.Violations)) +
        $", removed {r.Removed} in {r.Ms} ms, child {m.Nodes[Child]}, parent {m.Nodes[Parent]}, {m.Left} left";

    static bool Kept(Node n) => n.InTree && n.Started && n.Record;

    internal static void Run(Action<string, bool, string> check)
    {
        int cancels = 0;
        // Each removal, and what it must add to RemovalsLeftHere.
        var tally = new List<(string Name, Machine M, long Want)>();
        foreach (bool fast in new[] { true, false })
        {
            string kind = fast ? "fast removal (the full cleanup's)" : "SetupDiRemoveDevice removal (the sweeps' and a create's)";
            string delete = fast ? "uninstall" : "remove";

            // The Celeron's shape: the holder keeps the child open and
            // refuses the removal query.
            var held = new Machine(holder: true);
            var r1 = Remove(held, Parent, fast);
            check($"{kind}: a program holds the HID child and refuses the removal query -> no record deleted while started, no pnputil or devcon, the device and both records left, RemoveDevice false",
                  held.Violations.Count == 0 && held.Calls.All(c => c.StartsWith("query ", StringComparison.Ordinal)) && !r1.Removed
                  && Kept(held.Nodes[Parent]) && Kept(held.Nodes[Child]),
                  Describe(held, r1));
            tally.Add(($"{(fast ? "fast" : "SetupDiRemoveDevice")}, refused", held, 1));

            // No holder: the query takes the subtree out, and the records go
            // after it, child first.
            var free = new Machine(holder: false);
            var r2 = Remove(free, Parent, fast);
            check($"{kind}, no holder: the removal query takes the parent and its child out of the tree, then the child's record goes and the parent's, none while started",
                  free.Violations.Count == 0 && r2.Removed
                  && free.Calls.SequenceEqual(new[] { $"query {Parent}", $"{delete} {Child}", $"{delete} {Parent}" })
                  && !free.Nodes[Parent].Record && !free.Nodes[Child].Record,
                  Describe(free, r2));
            tally.Add(($"{(fast ? "fast" : "SetupDiRemoveDevice")}, no holder", free, 0));

            // What a dispose leaves once its driver failed the device: the
            // child taken down by surprise removal and still held, the parent
            // in the tree and no longer started (DN_WILL_BE_REMOVED). Its
            // records go as they did before, with no query.
            var failed = new Machine(holder: true);
            failed.Nodes[Child].InTree = false;
            failed.Nodes[Child].Started = false;
            failed.Nodes[Parent].Started = false;
            var r3 = Remove(failed, Parent, fast);
            check($"{kind}: a parent whose driver failed it, its child taken down by surprise removal and still held -> no query, the child's record and the parent's go, removed",
                  failed.Violations.Count == 0 && r3.Removed
                  && failed.Calls.SequenceEqual(new[] { $"{delete} {Child}", $"{delete} {Parent}" }),
                  Describe(failed, r3));
            tally.Add(($"{(fast ? "fast" : "SetupDiRemoveDevice")}, failed parent", failed, 0));

            cancels += held.CancelLocates + free.CancelLocates + failed.CancelLocates;
        }

        // A HID child removed on its own, as a dispose removes each child it
        // captured and the full cleanup a HID child whose parent is gone.
        var child = new Machine(holder: true);
        var r4 = Remove(child, Child, fast: true);
        check("a HID child removed on its own while a program holds it: the removal query refused -> its record left, RemoveDevice false",
              child.Violations.Count == 0 && child.Calls.SequenceEqual(new[] { $"query {Child}" }) && !r4.Removed && Kept(child.Nodes[Child]),
              Describe(child, r4));
        cancels += child.CancelLocates;
        tally.Add(("HID child, refused", child, 1));

        // A query that fails although the device left anyway refused
        // nothing: the records go.
        var gone = new Machine(holder: false) { QueryFailsAnyway = true };
        var r7 = Remove(gone, Parent, fast: true);
        check("a removal query that fails while the device leaves anyway, as one whose devnode left between the locate and the call -> the records go, removed",
              gone.Violations.Count == 0 && r7.Removed
              && gone.Calls.SequenceEqual(new[] { $"query {Parent}", $"uninstall {Child}", $"uninstall {Parent}" }),
              Describe(gone, r7));
        cancels += gone.CancelLocates;
        tally.Add(("query failed, device gone", gone, 0));

        // The holder refuses, and the caller can ask the driver to fail its
        // device, as the full cleanup and the sweeps can: the child goes by
        // surprise removal, the parent stays in the tree no longer started,
        // and the records go after that.
        foreach (bool fast in new[] { true, false })
        {
            string kind = fast ? "fast removal (the full cleanup's)" : "SetupDiRemoveDevice removal (the sweeps')";
            string delete = fast ? "uninstall" : "remove";
            var m = new Machine(holder: true);
            var r = Remove(m, Parent, fast, () => m.Fail(Parent));
            check($"{kind}: the holder refuses the query and the driver fails its device on request -> the child goes by surprise removal, then its record and the parent's, none while started, removed",
                  m.Violations.Count == 0 && r.Removed
                  && m.Calls.SequenceEqual(new[] { $"query {Parent}", $"fail {Parent}", $"{delete} {Child}", $"{delete} {Parent}" })
                  && !m.Nodes[Parent].Record && !m.Nodes[Child].Record,
                  Describe(m, r));
            cancels += m.CancelLocates;
            tally.Add(($"{(fast ? "fast" : "SetupDiRemoveDevice")}, failed on request", m, 0));
        }

        // The driver answers, and Windows leaves the device started.
        var stuck = new Machine(holder: true);
        var r5 = Remove(stuck, Parent, fast: true, () => { stuck.Calls.Add($"fail {Parent}"); return true; });
        int bound = DeviceManager.SurpriseRemovalWaitMs;
        check($"the driver answers but the device stays started -> no record deleted, the device and its records left when the {bound} ms wait ends, RemoveDevice false",
              stuck.Violations.Count == 0 && !r5.Removed && stuck.Calls.SequenceEqual(new[] { $"query {Parent}", $"fail {Parent}" })
              && r5.Ms >= bound - TimerSlack && Kept(stuck.Nodes[Parent]) && Kept(stuck.Nodes[Child]),
              Describe(stuck, r5));

        // No answer: a driver before protocol 5, or no worker listening.
        var silent = new Machine(holder: true);
        var r6 = Remove(silent, Parent, fast: true, () => { silent.Calls.Add($"fail {Parent}"); return false; });
        check("the driver does not answer -> no record deleted, the device and its records left with no wait for it to go down, RemoveDevice false",
              silent.Violations.Count == 0 && !r6.Removed && silent.Calls.SequenceEqual(new[] { $"query {Parent}", $"fail {Parent}" })
              && Kept(silent.Nodes[Parent]) && Kept(silent.Nodes[Child]),
              Describe(silent, r6));
        cancels += stuck.CancelLocates + silent.CancelLocates;
        tally.Add(("stays started after the answer", stuck, 1));
        tally.Add(("no answer", silent, 1));
        check("a removal that leaves a device counts once on RemovalsLeftHere, which a dispose keeps its lease on, and one that removes it counts nothing",
              tally.TrueForAll(t => t.M.Left == t.Want), string.Join(", ", tally.Select(t => $"{t.Name} {t.M.Left}")));

        // The wait itself: done once every child is out of the tree and the
        // parent no longer started, and not before.
        var late = new Machine(holder: true);
        var lateWait = Hooked(late, () =>
        {
            var failer = Offline.Critical(() => { Thread.Sleep(150); late.Fail(Parent); });
            failer.Start();
            var sw = Stopwatch.StartNew();
            bool down = DeviceManager.WaitSurpriseRemoval(Parent, new[] { Child }, 2000);
            return (down, sw.ElapsedMilliseconds);
        });
        var never = new Machine(holder: true);
        var neverWait = Hooked(never, () =>
        {
            var sw = Stopwatch.StartNew();
            bool down = DeviceManager.WaitSurpriseRemoval(Parent, new[] { Child }, 300);
            return (down, sw.ElapsedMilliseconds);
        });
        check("WaitSurpriseRemoval: the device taken down at 150 ms -> done, not before; a device that stays -> not done when its 300 ms bound ends",
              lateWait.down && lateWait.Item2 >= 150 - TimerSlack
              && !neverWait.down && neverWait.Item2 >= 300 - TimerSlack,
              $"down {lateWait.down} at {lateWait.Item2} ms; {neverWait.down} at {neverWait.Item2} ms");

        check("no CM_LOCATE_DEVNODE_CANCELREMOVE locate in the held removals", cancels == 0, $"{cancels}");

        LeaseChecks(check);
        LeftInPlaceChecks(check);
        RequestChecks(check);
    }

    // ── What the full removal keeps of devices its walk left ──

    /// <summary>The full removal's end (DeviceOrchestrator.LeftInPlace) on a
    /// census taken after its walk, here built from devnodes and records of
    /// the check's own (OwnerCensus.Build, as coexist_check's OF13 builds
    /// one). A device left in place must stay findable by a later sweep once
    /// its holder lets go: its Device Parameters name its index, its dead
    /// owner's record names the device, and its driver host keeps serving
    /// it.</summary>
    static void LeftInPlaceChecks(Action<string, bool, string> check)
    {
        const string Left1 = @"ROOT\HIDCLASS\HM_FE0000000000A001";
        const string Left2 = @"ROOT\HIDCLASS\HM_FE0000000000A002";
        const string Removed = @"ROOT\HIDCLASS\HM_FE0000000000A003";
        var deadNaming = OwnerRecord("pnc-dead-a", live: false, "HM_FE0000000000A001");
        var deadOther = OwnerRecord("pnc-dead-b", live: false, "HM_FE0000000000A003");
        var live = OwnerRecord("pnc-live-c", live: true, "HM_FE0000000000A004");
        var records = new[] { deadNaming, deadOther, live };
        var left = new DeviceOrchestrator.LeftInPlace(Census(new[] { Left1, Left2 }, records));
        check("full removal, two devnodes its walk left in the tree (a dead program's, and one no record names) -> their Device Parameters stay, a removed devnode's go",
              left.KeepsDeviceParameters(Left1) && left.KeepsDeviceParameters(Left2) && !left.KeepsDeviceParameters(Removed),
              $"left {string.Join(", ", left.Devnodes)}; keeps {left.KeepsDeviceParameters(Left1)}, {left.KeepsDeviceParameters(Left2)}, removed one {left.KeepsDeviceParameters(Removed)}");
        check("full removal, devnodes left -> the dead record that names one stays, a dead record that names nothing present goes, a live record stays",
              left.KeepsRecord(deadNaming) && !left.KeepsRecord(deadOther) && left.KeepsRecord(live),
              $"{left.KeepsRecord(deadNaming)}, {left.KeepsRecord(deadOther)}, {left.KeepsRecord(live)}");
        check("full removal, devnodes left -> no driver host is waited for or stopped, and the interface entries of present devnodes stay",
              left.LeavesDriverHosts && left.KeepsPresentInterfaceEntries,
              $"hosts left {left.LeavesDriverHosts}, interface entries kept {left.KeepsPresentInterfaceEntries}");
        var none = new DeviceOrchestrator.LeftInPlace(Census(Array.Empty<string>(), records));
        check("full removal, nothing left -> every dead record goes, a live one stays, and the driver hosts are waited for and drained",
              !none.KeepsRecord(deadNaming) && !none.KeepsRecord(deadOther) && none.KeepsRecord(live) && !none.LeavesDriverHosts,
              $"{none.KeepsRecord(deadNaming)}, {none.KeepsRecord(deadOther)}, {none.KeepsRecord(live)}, hosts left {none.LeavesDriverHosts}");
    }

    /// <summary>A census of <paramref name="present"/> devnodes, none with
    /// Device Parameters, and <paramref name="records"/>, with no v1.12.0
    /// program and no ledger holder.</summary>
    static OwnerCensus Census(string[] present, MachineClaims.OwnerRecord[] records) =>
        OwnerCensus.Build(present, _ => null, records, _ => false, () => Array.Empty<(int Pid, int Index)>(), () => records);

    static MachineClaims.OwnerRecord OwnerRecord(string owner, bool live, string token) =>
        new(owner, Pid: 0, Image: null, StartTime: 0, SdkVersion: null, UsbipPort: 0, Live: live, Self: false,
            Controllers: new[] { new MachineClaims.ControllerEntry(token, -1, "umdf", "dualshock-4-v2", null) });

    // ── The lease of a create that meets a refused removal ──

    const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>A create keeps its lease only for a device it registered and
    /// could not remove, so the record names that device for the next sweep.
    /// A prior life or a rival another program keeps is not its device: the
    /// create registered nothing, and a kept lease would refuse every later
    /// create of the key in this process until it exits ("in use by a
    /// controller of this process").</summary>
    static void LeaseChecks(Action<string, bool, string> check)
    {
        // A prior life of the identity, held by another program, refuses its
        // removal and stays, so the create refuses to register over it.
        var prior = new Machine(holder: true);
        string? tornDown = "";
        var a = CreateLease(
            created =>
            {
                Remove(prior, Parent, fast: false);
                throw new InvalidOperationException($"{Parent} is still there after its removal; refusing to register it");
            },
            id => tornDown = id);
        check("a create refused because another program keeps the identity's prior life (its removal refused) -> the create registered nothing, and its lease goes back",
              a.Released && prior.Left == 1 && tornDown == null && a.Thrown is InvalidOperationException,
              $"lease {(a.Released ? "given back" : "kept")}, the prior life's removal left {prior.Left}, teardown of {tornDown ?? "nothing"}, {a.Thrown?.GetType().Name}");

        // The create registered its devnode, its start failed, and the
        // teardown's removal is refused: the device is the create's.
        var own = new Machine(holder: true);
        var b = CreateLease(
            created =>
            {
                created?.Invoke(Parent);
                throw new InvalidOperationException($"The device {Parent} failed its start, problem code 10.");
            },
            id => { if (id != null) Remove(own, id, fast: false); });
        check("a create whose own devnode another program keeps by refusing its teardown's removal -> the lease stays, so the record names the device",
              !b.Released && own.Left == 1 && Kept(own.Nodes[Parent]),
              $"lease {(b.Released ? "given back" : "kept")}, the teardown left {own.Left}, {Describe(own, (false, 0))}");

        // The same create with no holder: the teardown removes the devnode.
        var free = new Machine(holder: false);
        var c = CreateLease(
            created =>
            {
                created?.Invoke(Parent);
                throw new InvalidOperationException($"The device {Parent} failed its start, problem code 10.");
            },
            id => { if (id != null) Remove(free, id, fast: false); });
        check("a create whose own devnode its teardown removed -> its lease goes back",
              c.Released && free.Left == 0 && !free.Nodes[Parent].Record,
              $"lease {(c.Released ? "given back" : "kept")}, the teardown left {free.Left}");
    }

    /// <summary>HMContext.CreateUnderLease with its machine steps replaced,
    /// as usbip_e2e_check's offline create case replaces them: a lease on a
    /// probe index that holds no machine claim and wrote no record,
    /// <paramref name="setup"/> for SetupController (given the callback that
    /// reports the registered parent) and <paramref name="teardown"/> for
    /// TeardownController. Says whether the create gave its lease back. A
    /// lease the create kept is disposed after.</summary>
    static (bool Released, Exception? Thrown) CreateLease(Func<Action<string>?, string?> setup, Action<string?> teardown)
    {
        var t = typeof(HMContext);
        var acquire = t.GetField("AcquireLease", AnyStatic)!;
        var setupField = t.GetField("SetupDevices", AnyStatic)!;
        var teardownField = t.GetField("TeardownDevices", AnyStatic)!;
        var create = t.GetMethod("CreateUnderLease", AnyInstance)!;
        object?[] saved = { acquire.GetValue(null), setupField.GetValue(null), teardownField.GetValue(null) };
        ControllerLease? lease = null;
        Exception? thrown = null;
        try
        {
            acquire.SetValue(null, (Func<int, string?, string, string, ControllerLease>)((slot, key, backend, id) => lease = OfflineLease(15)));
            setupField.SetValue(null, (Func<int, ControllerProfile, DeviceIdentity?, Action<string>?, string?>)((i, p, identity, created) => setup(created)));
            teardownField.SetValue(null, (Action<int, string?>)((i, instanceId) => teardown(instanceId)));
            var context = (HMContext)RuntimeHelpers.GetUninitializedObject(typeof(HMContext));
            try { create.Invoke(context, new object?[] { 0, Dualshock4(), null }); }
            catch (TargetInvocationException e) { thrown = e.InnerException; }
        }
        finally
        {
            acquire.SetValue(null, saved[0]);
            setupField.SetValue(null, saved[1]);
            teardownField.SetValue(null, saved[2]);
        }
        bool released = lease != null && (int)typeof(ControllerLease).GetField("_released", AnyInstance)!.GetValue(lease)! != 0;
        lease?.Dispose();
        return (released, thrown);
    }

    /// <summary>A lease on <paramref name="index"/> that holds no machine
    /// claim and wrote no record, so its Dispose gives nothing back.</summary>
    static ControllerLease OfflineLease(int index)
    {
        var ctor = typeof(ControllerLease).GetConstructor(AnyInstance, new[]
        {
            typeof(int), typeof(DeviceIdentity), typeof(SafeFileHandle), typeof(SafeFileHandle),
        }) ?? throw new MissingMethodException("ControllerLease's private constructor changed.");
        return (ControllerLease)ctor.Invoke(new object[]
        {
            index, DeviceIdentity.ForIndex(index), new SafeFileHandle(IntPtr.Zero, false), new SafeFileHandle(IntPtr.Zero, false),
        });
    }

    /// <summary>The catalog's dualshock-4-v2, a profile on the UMDF2
    /// driver's path.</summary>
    static HMProfile Dualshock4()
    {
        var asm = typeof(HMContext).Assembly;
        foreach (string res in asm.GetManifestResourceNames().Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal)))
        {
            using var st = asm.GetManifestResourceStream(res)!;
            using var sr = new StreamReader(st, Encoding.UTF8);
            string text = sr.ReadToEnd();
            if (JsonNode.Parse(text) is not JsonObject o || (string?)o["id"] != "dualshock-4-v2") continue;
            var p = ControllerProfile.Parse(Encoding.UTF8.GetBytes(text)) ?? throw new InvalidOperationException("dualshock-4-v2 did not parse");
            p.ValidateDeviceModel();
            return new HMProfile(p);
        }
        throw new InvalidOperationException("no catalog profile dualshock-4-v2");
    }

    // A timed wait can end a timer tick before its time on the Stopwatch.
    const long TimerSlack = 20;

    /// <summary>SharedMemoryIO.RequestFailure, the request a removal makes
    /// of a device another program made, on events of this run's own under
    /// a Local\ prefix, with a thread playing the driver's input worker. The
    /// events exist before each request, as a running program's do, so no
    /// check needs the elevation that making them with this release's
    /// security takes.</summary>
    static void RequestChecks(Action<string, bool, string> check)
    {
        string prefix = $@"Local\pnp-notify-check-{Environment.ProcessId}-";
        int index = 0;
        (EventWaitHandle Request, EventWaitHandle Answer, EventWaitHandle Input, EventWaitHandle Stop) Index(int i) =>
            (new EventWaitHandle(false, EventResetMode.AutoReset, $"{prefix}RemoveEvent{i}"),
             new EventWaitHandle(false, EventResetMode.AutoReset, $"{prefix}RemovedEvent{i}"),
             new EventWaitHandle(false, EventResetMode.AutoReset, $"{prefix}InputEvent{i}"),
             new EventWaitHandle(false, EventResetMode.ManualReset, $"{prefix}StopEvent{i}"));
        static void Close((EventWaitHandle, EventWaitHandle, EventWaitHandle, EventWaitHandle) e)
        {
            e.Item1.Dispose();
            e.Item2.Dispose();
            e.Item3.Dispose();
            e.Item4.Dispose();
        }

        // A worker waiting on the remove event fails its device and answers.
        var e1 = Index(++index);
        var w1 = Offline.Critical(() => { if (e1.Request.WaitOne(5000)) e1.Answer.Set(); });
        w1.Start();
        var sw = Stopwatch.StartNew();
        bool a1 = SharedMemoryIO.RequestFailure(prefix, index, 2000);
        long t1 = sw.ElapsedMilliseconds;
        w1.Join(5000);
        Close(e1);
        check("the request: a worker waiting on the remove event answers -> true", a1, $"{a1} in {t1} ms");

        // No worker: false at the bound, the stop event set at half of it,
        // and the request withdrawn.
        var e2 = Index(++index);
        sw.Restart();
        bool a2 = SharedMemoryIO.RequestFailure(prefix, index, 400);
        long t2 = sw.ElapsedMilliseconds;
        bool left = e2.Request.WaitOne(0), stopped = e2.Stop.WaitOne(0);
        Close(e2);
        check("the request: no worker -> false when its 400 ms bound ends, the stop event set, and the request withdrawn, not left set for a worker that opens it later",
              !a2 && t2 >= 400 - TimerSlack && stopped && !left, $"{a2} in {t2} ms, stop set {stopped}, request left set {left}");

        // An answer left from an earlier request is not this one's.
        var e3 = Index(++index);
        e3.Answer.Set();
        bool a3 = SharedMemoryIO.RequestFailure(prefix, index, 300);
        Close(e3);
        check("the request: an answer left from before it, and no worker -> false", !a3, $"{a3}");

        // A worker that holds an input event made before the remove events
        // takes the request only after the stop event makes it open its
        // events again.
        var e4 = Index(++index);
        var w4 = Offline.Critical(() =>
        {
            if (!e4.Stop.WaitOne(5000)) return;
            e4.Stop.Reset();
            if (e4.Request.WaitOne(1000)) e4.Answer.Set();
        });
        w4.Start();
        sw.Restart();
        bool a4 = SharedMemoryIO.RequestFailure(prefix, index, 2000);
        long t4 = sw.ElapsedMilliseconds;
        w4.Join(5000);
        Close(e4);
        check("the request: a worker that opens the remove event only once the stop event is set -> true after half the 2 s bound, before its end",
              a4 && t4 >= 1000 - TimerSlack, $"{a4} in {t4} ms");

        // A worker that looks only 200 ms after the request, as one with no
        // program on its index looks for the input event every 200 ms.
        var e5 = Index(++index);
        var w5 = Offline.Critical(() =>
        {
            Thread.Sleep(200);
            if (e5.Request.WaitOne(0)) e5.Answer.Set();
        });
        w5.Start();
        sw.Restart();
        bool a5 = SharedMemoryIO.RequestFailure(prefix, index, 2000);
        long t5 = sw.ElapsedMilliseconds;
        w5.Join(5000);
        Close(e5);
        check("the request: a worker that looks 200 ms after it was made finds it still set -> true", a5, $"{a5} in {t5} ms");
    }
}
