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
using System.Linq;
using HIDMaestro.Internal;

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
        readonly Dictionary<string, List<Action>> _removed = new(StringComparer.OrdinalIgnoreCase);

        public Machine(bool holder) => Holder = holder;

        public IReadOnlyList<string> ChildrenOf(string id) =>
            string.Equals(id, Parent, StringComparison.OrdinalIgnoreCase) ? new[] { Child } : Array.Empty<string>();

        public bool IsStarted(string id) => Nodes.TryGetValue(id, out var n) && n.InTree && n.Started;

        public uint Locate(string id, uint flags)
        {
            if ((flags & CM_LOCATE_DEVNODE_CANCELREMOVE) != 0) CancelLocates++;
            if (!Nodes.TryGetValue(id, out var n)) return CR_NO_SUCH_DEVINST;
            bool found = flags == CM_LOCATE_DEVNODE_NORMAL ? n.InTree : n.InTree || n.Record;
            return found ? CR_SUCCESS : CR_NO_SUCH_DEVINST;
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
            return true;
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
    }

    sealed class StandInLock : IDisposable
    {
        public void Dispose() { }
    }

    /// <summary>DeviceManager.RemoveDevice on <paramref name="id"/> with
    /// the fallbacks forced, as the full cleanup and the sweeps call it,
    /// every PnP call answered by <paramref name="m"/>.</summary>
    static (bool Removed, long Ms) Remove(Machine m, string id, bool fast)
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
        try
        {
            var sw = Stopwatch.StartNew();
            bool removed = DeviceManager.RemoveDevice(id, timeoutMs: 2000, fast: fast, forceFallbacks: true);
            return (removed, sw.ElapsedMilliseconds);
        }
        finally
        {
            DeviceManager.RemovalCallsHook = null;
            DeviceManager.LocateHook = null;
            DeviceManager.RemovedHook = null;
            DeviceManager.PnpLockHook = null;
            DeviceOrchestrator.DiagTap = null;
        }
    }

    static string Describe(Machine m, (bool Removed, long Ms) r) =>
        $"calls [{string.Join(", ", m.Calls)}], " +
        (m.Violations.Count == 0 ? "no record deleted while started" : string.Join("; ", m.Violations)) +
        $", removed {r.Removed} in {r.Ms} ms, child {m.Nodes[Child]}, parent {m.Nodes[Parent]}";

    static bool Kept(Node n) => n.InTree && n.Started && n.Record;

    internal static void Run(Action<string, bool, string> check)
    {
        int cancels = 0;
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

            // No holder: the query takes the subtree out, and the records go
            // after it, child first.
            var free = new Machine(holder: false);
            var r2 = Remove(free, Parent, fast);
            check($"{kind}, no holder: the removal query takes the parent and its child out of the tree, then the child's record goes and the parent's, none while started",
                  free.Violations.Count == 0 && r2.Removed
                  && free.Calls.SequenceEqual(new[] { $"query {Parent}", $"{delete} {Child}", $"{delete} {Parent}" })
                  && !free.Nodes[Parent].Record && !free.Nodes[Child].Record,
                  Describe(free, r2));

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

        check("no CM_LOCATE_DEVNODE_CANCELREMOVE locate in the held removals", cancels == 0, $"{cancels}");
    }
}
