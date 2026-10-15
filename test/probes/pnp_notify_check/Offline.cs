// The removal test's and the registration gate's decisions, fed recorded
// sequences. No device, no elevation and no PnP call: every locate, REMOVED
// registration and install-queue wait the SDK makes here goes through
// DeviceManager's hooks, which answer from a sequence on one clock.
//
// The sequence the 0x139 investigation asked for: the devnode leaves the
// device tree at 0 ms, its DEVICEINSTANCEREMOVED notification arrives at
// 200 ms, and its PHANTOM record goes at 300 ms. A removal is complete, and
// the id may be registered again, only at 300 ms. Before v1.13.0 the wait's
// test was "REMOVED delivered, or a NORMAL locate fails" and the gate had no
// test of its own, so both answered at 0 ms. No check here may see a
// CM_LOCATE_DEVNODE_CANCELREMOVE locate, which cancels a removal in progress.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using HIDMaestro.Internal;

internal static class Offline
{
    const uint CR_SUCCESS = 0x00;
    const uint CR_NO_SUCH_DEVINST = 0x0D;
    const uint CM_LOCATE_DEVNODE_NORMAL = 0x0;
    const uint CM_LOCATE_DEVNODE_PHANTOM = 0x1;
    const uint CM_LOCATE_DEVNODE_CANCELREMOVE = 0x2;
    const uint WAIT_OBJECT_0 = 0x00000000;
    const uint WAIT_TIMEOUT = 0x00000102;
    const uint WAIT_FAILED = 0xFFFFFFFF;

    const string Id = @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_OFFLINE";

    // A finished wait is seen within one 50 ms re-check of its last change.
    // This bound only catches a wait that does not end.
    const long Slack = 1000;

    static int s_total, s_failures;
    static readonly List<uint> s_flags = new();

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    /// <summary>One device instance's recorded removal, on its own clock.
    /// Before <see cref="Begin"/> the devnode is in the tree with its record,
    /// unless <see cref="AbsentBefore"/>. Times are in ms from Begin, and
    /// null means never.</summary>
    sealed class Sequence
    {
        public bool AbsentBefore;
        public long? LeavesTree = 0, RemovedAt, RecordGoneAt;
        public long? InstallsIdleAt = 0;
        public bool InstallWaitFails, RefuseRegistration;

        public readonly Stopwatch Clock = new();
        readonly object _lock = new();
        readonly List<Action> _subscribers = new();
        bool _fired;
        Timer? _timer;

        public long Now => Clock.IsRunning ? Clock.ElapsedMilliseconds : -1;

        public void Begin()
        {
            Clock.Start();
            if (RemovedAt is long at)
                _timer = new Timer(_ => Fire(at), null, at, Timeout.Infinite);
        }

        /// <summary>The timer runs on another clock than <see cref="Clock"/>
        /// and can fire a millisecond early by it, so the delivery waits for
        /// the sequence's own time.</summary>
        void Fire(long at)
        {
            while (Now < at) Thread.Sleep(1);
            lock (_lock)
            {
                _fired = true;
                foreach (var a in _subscribers) a();
            }
        }

        bool InTree(long now) => !AbsentBefore && (now < 0 || LeavesTree is not long t || now < t);
        bool Record(long now) => !AbsentBefore && (now < 0 || RecordGoneAt is not long t || now < t);

        public uint Locate(string id, uint flags)
        {
            lock (s_flags) s_flags.Add(flags);
            long now = Now;
            bool found = flags switch
            {
                CM_LOCATE_DEVNODE_NORMAL => InTree(now),
                CM_LOCATE_DEVNODE_PHANTOM => InTree(now) || Record(now),
                _ => InTree(now),
            };
            return found ? CR_SUCCESS : CR_NO_SUCH_DEVINST;
        }

        /// <summary>A registration made after the event fired never sees it,
        /// as with CM_Register_Notification, and none is called after its
        /// Dispose returns, as CM_Unregister_Notification waits for callbacks
        /// in flight.</summary>
        public IDisposable? Register(string id, Action onRemoved)
        {
            if (RefuseRegistration) return null;
            lock (_lock)
            {
                if (!_fired) _subscribers.Add(onRemoved);
            }
            return new Registration(this, onRemoved);
        }

        sealed class Registration : IDisposable
        {
            readonly Sequence _s;
            readonly Action _a;
            public Registration(Sequence s, Action a) { _s = s; _a = a; }
            public void Dispose() { lock (_s._lock) _s._subscribers.Remove(_a); }
        }

        /// <summary>CMP_WaitNoPendingInstallEvents: WAIT_OBJECT_0 once no
        /// install is pending, WAIT_TIMEOUT when the timeout ends first.</summary>
        public uint WaitInstalls(uint timeoutMs)
        {
            if (InstallWaitFails) return WAIT_FAILED;
            var waited = Stopwatch.StartNew();
            while (true)
            {
                if (InstallsIdleAt is long idle && Now >= idle) return WAIT_OBJECT_0;
                if (waited.ElapsedMilliseconds >= timeoutMs) return WAIT_TIMEOUT;
                Thread.Sleep(1);
            }
        }

        public void Hook()
        {
            DeviceManager.LocateHook = Locate;
            DeviceManager.RemovedHook = Register;
            DeviceManager.InstallQueueHook = WaitInstalls;
        }

        public void End()
        {
            DeviceManager.LocateHook = null;
            DeviceManager.RemovedHook = null;
            DeviceManager.InstallQueueHook = null;
            _timer?.Dispose();
        }
    }

    /// <summary>A removal issued while the devnode is in the tree: the
    /// watch is started first, as RemoveDevice starts it before its removal
    /// call, then the sequence begins.</summary>
    static (bool Done, long Ms) WatchedRemoval(Sequence s, bool uninstalls, int budgetMs)
    {
        s.Hook();
        try
        {
            using var watch = DeviceManager.RemovalWatch.Start(Id);
            s.Begin();
            bool done = watch.Wait(uninstalls, budgetMs);
            return (done, s.Now);
        }
        finally { s.End(); }
    }

    static (DeviceManager.RegistrationGate Gate, long Ms, int Removals) Gate(Sequence s, bool owned = true,
                                                                            bool removalBegins = false, int installWaitMs = 30_000)
    {
        int removals = 0;
        s.Hook();
        try
        {
            if (!removalBegins) s.Begin();
            var gate = DeviceManager.AwaitRegistrationGate(Id, () =>
            {
                removals++;
                if (!owned) return false;
                if (removalBegins) s.Begin();
                return true;
            }, timeoutMs: 5000, installWaitMs: installWaitMs);
            return (gate, s.Now, removals);
        }
        finally { s.End(); }
    }

    static bool Within(long ms, long from) => ms >= from && ms < from + Slack;

    internal static int Run()
    {
        Console.WriteLine("=== Removal test and registration gate, offline ===");

        // The decision itself, at the sequence's event times: a devnode that
        // was present when the wait registered, removed by an uninstall.
        bool At(long t) => DeviceManager.RemovalComplete(new DeviceManager.RemovalObservation(
            presentAtStart: true, registered: true, removedDelivered: t >= 200, inTree: t < 0, record: t < 300), uninstalls: true);
        var answers = new[] { 0L, 199, 200, 299, 300 };
        string table = string.Join(", ", Array.ConvertAll(answers, t => $"{t} ms {(At(t) ? "complete" : "pending")}"));
        Check("the decision: complete only once REMOVED has arrived and the PHANTOM record is gone",
              !At(0) && !At(199) && !At(200) && !At(299) && At(300), table);

        // The waits, through the SDK's own loop.
        var w1 = WatchedRemoval(new Sequence { LeavesTree = 0, RemovedAt = 200, RecordGoneAt = 300 }, uninstalls: true, 5000);
        Check("removal: out of the tree at 0, REMOVED at 200, record gone at 300 -> complete at 300",
              w1.Done && Within(w1.Ms, 300), $"returned {w1.Done} at {w1.Ms} ms");

        var w2 = WatchedRemoval(new Sequence { LeavesTree = 0, RecordGoneAt = 100, RemovedAt = 200 }, uninstalls: true, 5000);
        Check("removal: record gone at 100 before REMOVED at 200 -> complete at 200",
              w2.Done && Within(w2.Ms, 200), $"returned {w2.Done} at {w2.Ms} ms");

        var w3 = WatchedRemoval(new Sequence { LeavesTree = 0, RemovedAt = 200, RecordGoneAt = null }, uninstalls: false, 5000);
        Check("removal that keeps its record (SwDeviceClose): REMOVED at 200 -> complete at 200",
              w3.Done && Within(w3.Ms, 200), $"returned {w3.Done} at {w3.Ms} ms");

        var w4 = WatchedRemoval(new Sequence { LeavesTree = 50, RecordGoneAt = 150, RemovedAt = null, RefuseRegistration = true },
                                uninstalls: true, 5000);
        Check("removal whose REMOVED registration Windows refused: the tree stands in -> complete at 150 (record)",
              w4.Done && Within(w4.Ms, 150), $"returned {w4.Done} at {w4.Ms} ms");

        var w5 = WatchedRemoval(new Sequence { AbsentBefore = true }, uninstalls: true, 5000);
        Check("removal of an id with no devnode and no record -> complete at once",
              w5.Done && w5.Ms < 50, $"returned {w5.Done} at {w5.Ms} ms");

        var w6 = WatchedRemoval(new Sequence { LeavesTree = 0, RecordGoneAt = 50, RemovedAt = null }, uninstalls: true, 400);
        Check("removal whose REMOVED never arrives -> not complete when the 400 ms budget ends",
              !w6.Done && w6.Ms >= 400, $"returned {w6.Done} at {w6.Ms} ms");

        var s7 = new Sequence { LeavesTree = 100, RemovedAt = 250, RecordGoneAt = 300 };
        s7.Hook();
        bool w7Done;
        try
        {
            s7.Begin();
            w7Done = DeviceManager.WaitForDeviceRemoval(Id, uninstalls: true, timeoutMs: 5000);
        }
        finally { s7.End(); }
        long w7Ms = s7.Clock.ElapsedMilliseconds;
        Check("WaitForDeviceRemoval on a present devnode: out at 100, REMOVED at 250, record gone at 300 -> 300",
              w7Done && Within(w7Ms, 300), $"returned {w7Done} at {w7Ms} ms");

        // The registration gate.
        var g1 = Gate(new Sequence { LeavesTree = 0, RemovedAt = 200, RecordGoneAt = 300 });
        Check("gate: prior life out of the tree at 0, REMOVED at 200, record gone at 300 -> allowed at 300",
              g1.Gate == DeviceManager.RegistrationGate.Allowed && Within(g1.Ms, 300),
              $"{g1.Gate} at {g1.Ms} ms, {g1.Removals} prior-life removal(s)");
        Check("gate: the prior life's removal ran once", g1.Removals == 1, $"{g1.Removals}");

        var g2 = Gate(new Sequence { AbsentBefore = true, InstallsIdleAt = 400 });
        Check("gate: no prior life, installs pending until 400 -> allowed at 400",
              g2.Gate == DeviceManager.RegistrationGate.Allowed && Within(g2.Ms, 400),
              $"{g2.Gate} at {g2.Ms} ms");

        var g3 = Gate(new Sequence { AbsentBefore = true, InstallsIdleAt = null }, installWaitMs: 300);
        Check("gate: installs still pending when its 300 ms wait ends -> allowed, and says so",
              g3.Gate == DeviceManager.RegistrationGate.AllowedInstallsPending && Within(g3.Ms, 300),
              $"{g3.Gate} at {g3.Ms} ms");

        var g4 = Gate(new Sequence { AbsentBefore = true, InstallWaitFails = true });
        Check("gate: the install wait fails -> refused",
              g4.Gate == DeviceManager.RegistrationGate.Refused, $"{g4.Gate} at {g4.Ms} ms");

        var g5 = Gate(new Sequence { LeavesTree = null, RecordGoneAt = null }, owned: false);
        Check("gate: a present prior life that is not HIDMaestro's -> refused without a wait",
              g5.Gate == DeviceManager.RegistrationGate.Refused && g5.Ms < 50, $"{g5.Gate} at {g5.Ms} ms");

        var g6 = Gate(new Sequence { LeavesTree = 50, RemovedAt = 250, RecordGoneAt = 100 }, removalBegins: true);
        Check("gate: a present prior life it removes, out at 50, record gone at 100, REMOVED at 250 -> allowed at 250",
              g6.Gate == DeviceManager.RegistrationGate.Allowed && Within(g6.Ms, 250),
              $"{g6.Gate} at {g6.Ms} ms after the removal began, {g6.Removals} removal(s)");

        int locates, cancels;
        lock (s_flags)
        {
            locates = s_flags.Count;
            cancels = s_flags.FindAll(f => (f & CM_LOCATE_DEVNODE_CANCELREMOVE) != 0).Count;
        }
        Check("the waits and the gate located through the hook", locates > 0, $"{locates} locates");
        Check("no CM_LOCATE_DEVNODE_CANCELREMOVE locate was issued", cancels == 0, $"{cancels} of {locates}");

        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }
}
