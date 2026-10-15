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

    const long RemovedBoundMs = DeviceManager.RemovedBoundMs;

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
        /// <summary>Before Begin the devnode is out of the tree, its record
        /// still there: a phantom.</summary>
        public bool OutOfTreeBefore;
        public long? LeavesTree = 0, RemovedAt, RecordGoneAt;
        public long? InstallsIdleAt = 0;
        public bool InstallWaitFails, RefuseRegistration;
        /// <summary>REMOVED registrations the SDK asked for.</summary>
        public int Registrations;
        /// <summary>Pending installs that a 0 ms test under the first hold
        /// of the lock sees, and nothing else does.</summary>
        public bool BusyUnderFirstLock;
        /// <summary>The stand-in lock: held now, times taken, and what ran
        /// under it that must not.</summary>
        public bool LockHeld;
        public int LockTakes, WaitsUnderLock;

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

        bool InTree(long now) => !AbsentBefore && !OutOfTreeBefore && (now < 0 || LeavesTree is not long t || now < t);
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
            Interlocked.Increment(ref Registrations);
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
            if (LockHeld && timeoutMs > 0) WaitsUnderLock++;
            if (InstallWaitFails) return WAIT_FAILED;
            if (BusyUnderFirstLock && LockHeld && LockTakes == 1) return WAIT_TIMEOUT;
            var waited = Stopwatch.StartNew();
            while (true)
            {
                if (InstallsIdleAt is long idle && Now >= idle) return WAIT_OBJECT_0;
                if (waited.ElapsedMilliseconds >= timeoutMs) return WAIT_TIMEOUT;
                Thread.Sleep(1);
            }
        }

        public IDisposable Lock()
        {
            LockHeld = true;
            LockTakes++;
            return new Release(this);
        }

        sealed class Release : IDisposable
        {
            readonly Sequence _s;
            public Release(Sequence s) => _s = s;
            public void Dispose() => _s.LockHeld = false;
        }

        public void Hook()
        {
            DeviceManager.LocateHook = Locate;
            DeviceManager.RemovedHook = Register;
            DeviceManager.InstallQueueHook = WaitInstalls;
            DeviceManager.PnpLockHook = Lock;
        }

        public void End()
        {
            DeviceManager.LocateHook = null;
            DeviceManager.RemovedHook = null;
            DeviceManager.InstallQueueHook = null;
            DeviceManager.PnpLockHook = null;
            _timer?.Dispose();
        }
    }

    /// <summary>A removal issued while the devnode is in the tree: the
    /// watch is started first, as RemoveDevice starts it before its removal
    /// call, then the sequence begins.</summary>
    static (DeviceManager.RemovalOutcome Outcome, long Ms) WatchedRemoval(Sequence s, bool uninstalls, int budgetMs,
                                                                          int removedMs = DeviceManager.RemovedBoundMs)
    {
        s.Hook();
        try
        {
            using var watch = DeviceManager.RemovalWatch.Start(Id);
            s.Begin();
            var outcome = watch.Wait(uninstalls, budgetMs, removedMs);
            return (outcome, s.Now);
        }
        finally { s.End(); }
    }

    /// <summary>Removals, prefix checks and install waits seen while the
    /// stand-in lock was held, over every gate run.</summary>
    static int s_underLock;

    /// <summary>One gate run. It returns when the gate would let the caller
    /// register: Held says whether the stand-in lock is held then, as it
    /// must be.</summary>
    static (DeviceManager.RegistrationGate Gate, long Ms, int Removals, bool Held, int Takes) Gate(Sequence s, bool owned = true,
        bool removalBegins = false, int installWaitMs = 30_000, Func<bool, bool>? rivals = null,
        DeviceManager.RegistrationKind kind = DeviceManager.RegistrationKind.Root)
    {
        int removals = 0;
        s.Hook();
        try
        {
            if (!removalBegins) s.Begin();
            Func<bool, bool>? checkedRivals = rivals == null ? null : remove =>
            {
                if (s.LockHeld && remove) s_underLock++;
                return rivals(remove);
            };
            using var hold = DeviceManager.EnterRegistrationGate(Id, kind, () =>
            {
                removals++;
                if (s.LockHeld) s_underLock++;
                if (!owned) return null;
                // As DeviceManager.RemovePriorLife does it: the watch starts
                // before the removal, and its test is the one wait.
                using var watch = DeviceManager.RemovalWatch.Start(Id);
                if (removalBegins) s.Begin();
                return watch.Wait(uninstalls: kind == DeviceManager.RegistrationKind.Root, 5000);
            }, checkedRivals, out var decision, installWaitMs);
            bool held = hold != null && s.LockHeld;
            s_underLock += s.WaitsUnderLock;
            return (decision, s.Now, removals, held, s.LockTakes);
        }
        finally { s.End(); }
    }

    static bool Within(long ms, long from) => ms >= from && ms < from + Slack;

    internal static int Run()
    {
        Console.WriteLine("=== Removal test and registration gate, offline ===");

        // The decision itself, at the sequence's event times: a devnode that
        // was present when the wait registered, removed by an uninstall.
        const DeviceManager.RemovalOutcome Removed = DeviceManager.RemovalOutcome.Removed;
        const DeviceManager.RemovalOutcome Pending = DeviceManager.RemovalOutcome.Pending;
        const DeviceManager.RemovalOutcome GoneWithout = DeviceManager.RemovalOutcome.GoneWithoutRemoved;
        const DeviceManager.RemovalOutcome NotGone = DeviceManager.RemovalOutcome.NotGone;
        DeviceManager.RemovalOutcome At(long t, bool overdue = false) => DeviceManager.RemovalComplete(new DeviceManager.RemovalObservation(
            presentAtStart: true, registered: true, removedDelivered: t >= 200, inTree: t < 0, record: t < 300), uninstalls: true, overdue);
        var answers = new[] { 0L, 199, 200, 299, 300 };
        string table = string.Join(", ", Array.ConvertAll(answers, t => $"{t} ms {At(t)}"));
        Check("the decision: removed only once REMOVED has arrived and the PHANTOM record is gone",
              At(0) == Pending && At(199) == Pending && At(200) == Pending && At(299) == Pending && At(300) == Removed, table);

        // Without the notification: gone only by what the locates prove, and
        // only once its bound has passed. Out of the tree with the record
        // still there is never gone, however late.
        var noNote = new DeviceManager.RemovalObservation(presentAtStart: true, registered: true, removedDelivered: false, inTree: false, record: false);
        var recordLeft = new DeviceManager.RemovalObservation(presentAtStart: true, registered: true, removedDelivered: false, inTree: false, record: true);
        var cameLater = new DeviceManager.RemovalObservation(presentAtStart: false, registered: false, removedDelivered: false, inTree: false, record: false);
        var dNoNote = DeviceManager.RemovalComplete(noNote, uninstalls: true, removedOverdue: false);
        var dNoNoteLate = DeviceManager.RemovalComplete(noNote, uninstalls: true, removedOverdue: true);
        var dRecord = DeviceManager.RemovalComplete(recordLeft, uninstalls: true, removedOverdue: true);
        var dSoftware = DeviceManager.RemovalComplete(recordLeft, uninstalls: false, removedOverdue: true);
        var dAbsent = DeviceManager.RemovalComplete(cameLater, uninstalls: true, removedOverdue: false);
        Check("the decision: gone without REMOVED only past its bound, a record left by an uninstall never gone, an absent devnode removed at once",
              dNoNote == Pending && dNoNoteLate == GoneWithout && dRecord == Pending && dSoftware == GoneWithout && dAbsent == Removed,
              $"{dNoNote}, {dNoNoteLate}, {dRecord}, {dSoftware}, {dAbsent}");

        // A fast removal confirms within its own budget, whatever its caller
        // passed: a teardown's surviving child passes 120 s, the full cleanup
        // 3 or 5 s.
        int f120 = DeviceManager.ParentRemovalBudget(fast: true, 120_000);
        int f3 = DeviceManager.ParentRemovalBudget(fast: true, 3000);
        int f1 = DeviceManager.ParentRemovalBudget(fast: true, 1000);
        int s120 = DeviceManager.ParentRemovalBudget(fast: false, 120_000);
        Check("budgets: a fast removal waits at most QuickRemovalMs, a SetupDiRemoveDevice or close removal its caller's budget",
              f120 == DeviceManager.QuickRemovalMs && f3 == DeviceManager.QuickRemovalMs && f1 == 1000 && s120 == 120_000,
              $"fast 120000 -> {f120}, fast 3000 -> {f3}, fast 1000 -> {f1}, not fast 120000 -> {s120}");

        // The waits, through the SDK's own loop.
        var s1 = new Sequence { LeavesTree = 0, RemovedAt = 200, RecordGoneAt = 300 };
        var w1 = WatchedRemoval(s1, uninstalls: true, 5000);
        Check("removal: out of the tree at 0, REMOVED at 200, record gone at 300 -> removed at 300",
              w1.Outcome == Removed && Within(w1.Ms, 300), $"{w1.Outcome} at {w1.Ms} ms");
        Check("removal of a devnode in the tree opens one REMOVED registration", s1.Registrations == 1,
              $"{s1.Registrations} registration(s)");

        // A phantom (out of the tree, record still there) has no removal for
        // a notification to prove: no registration, and its record decides.
        var sPh = new Sequence { OutOfTreeBefore = true, RecordGoneAt = 100 };
        var wPh = WatchedRemoval(sPh, uninstalls: true, 5000);
        Check("removal of a phantom record gone at 100 -> removed at 100, with no REMOVED registration",
              wPh.Outcome == Removed && Within(wPh.Ms, 100) && sPh.Registrations == 0,
              $"{wPh.Outcome} at {wPh.Ms} ms, {sPh.Registrations} registration(s)");

        var w2 = WatchedRemoval(new Sequence { LeavesTree = 0, RecordGoneAt = 100, RemovedAt = 200 }, uninstalls: true, 5000);
        Check("removal: record gone at 100 before REMOVED at 200 -> removed at 200",
              w2.Outcome == Removed && Within(w2.Ms, 200), $"{w2.Outcome} at {w2.Ms} ms");

        var w3 = WatchedRemoval(new Sequence { LeavesTree = 0, RemovedAt = 200, RecordGoneAt = null }, uninstalls: false, 5000);
        Check("removal that keeps its record (SwDeviceClose): REMOVED at 200 -> removed at 200",
              w3.Outcome == Removed && Within(w3.Ms, 200), $"{w3.Outcome} at {w3.Ms} ms");

        var w4 = WatchedRemoval(new Sequence { LeavesTree = 50, RecordGoneAt = 150, RemovedAt = null, RefuseRegistration = true },
                                uninstalls: true, 5000);
        Check("removal whose REMOVED registration Windows refused: the tree stands in -> removed at 150 (record)",
              w4.Outcome == Removed && Within(w4.Ms, 150), $"{w4.Outcome} at {w4.Ms} ms");

        var w5 = WatchedRemoval(new Sequence { AbsentBefore = true }, uninstalls: true, 5000);
        Check("removal of an id with no devnode and no record -> removed at once",
              w5.Outcome == Removed && w5.Ms < 50, $"{w5.Outcome} at {w5.Ms} ms");

        var w6 = WatchedRemoval(new Sequence { LeavesTree = 0, RecordGoneAt = 50, RemovedAt = null }, uninstalls: true, 5000, removedMs: 300);
        Check("removal whose REMOVED never arrives, record gone at 50 -> gone without it when its 300 ms bound ends, at 350",
              w6.Outcome == GoneWithout && Within(w6.Ms, 350), $"{w6.Outcome} at {w6.Ms} ms");

        var w6b = WatchedRemoval(new Sequence { LeavesTree = 0, RecordGoneAt = null, RemovedAt = 100 }, uninstalls: true, 400);
        Check("removal that uninstalls but whose record never goes -> not gone when the 400 ms budget ends",
              w6b.Outcome == NotGone && Within(w6b.Ms, 400), $"{w6b.Outcome} at {w6b.Ms} ms");

        var s7 = new Sequence { LeavesTree = 100, RemovedAt = 250, RecordGoneAt = 300 };
        s7.Hook();
        DeviceManager.RemovalOutcome w7;
        try
        {
            s7.Begin();
            w7 = DeviceManager.WaitForDeviceRemoval(Id, uninstalls: true, timeoutMs: 5000);
        }
        finally { s7.End(); }
        long w7Ms = s7.Clock.ElapsedMilliseconds;
        Check("WaitForDeviceRemoval on a present devnode: out at 100, REMOVED at 250, record gone at 300 -> removed at 300",
              w7 == Removed && Within(w7Ms, 300), $"{w7} at {w7Ms} ms");

        // The registration gate.
        var sG1 = new Sequence { LeavesTree = 0, RemovedAt = 200, RecordGoneAt = 300 };
        var g1 = Gate(sG1);
        Check("gate: prior life out of the tree at 0, REMOVED at 200, record gone at 300 -> allowed at 300, under the lock",
              g1.Gate == DeviceManager.RegistrationGate.Allowed && Within(g1.Ms, 300) && g1.Held,
              $"{g1.Gate} at {g1.Ms} ms, {g1.Removals} prior-life removal(s)");
        Check("gate: the prior life's removal ran once", g1.Removals == 1, $"{g1.Removals}");
        Check("gate: a prior life out of the tree at the gate opens no REMOVED registration", sG1.Registrations == 0,
              $"{sG1.Registrations} registration(s)");

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

        // A prior life whose devnode and record are gone is gone, whether or
        // not its REMOVED notification arrives. Its absence costs at most the
        // notification's own bound, and the create goes ahead.
        var g7 = Gate(new Sequence { LeavesTree = 50, RecordGoneAt = 100, RemovedAt = null }, removalBegins: true);
        Check("gate: a prior life it removes leaves the tree and its record goes at 100, REMOVED never comes -> allowed within the notification bound",
              g7.Gate == DeviceManager.RegistrationGate.Allowed && g7.Ms >= 100 && g7.Ms < 100 + RemovedBoundMs + Slack,
              $"{g7.Gate} at {g7.Ms} ms after the removal began");

        // An id with no devnode and no record has nothing whose removal a
        // notification could prove, so the gate registers for none.
        var s8 = new Sequence { AbsentBefore = true };
        var g8 = Gate(s8);
        Check("gate: an id with no devnode and no record opens no REMOVED registration",
              g8.Gate == DeviceManager.RegistrationGate.Allowed && s8.Registrations == 0,
              $"{g8.Gate}, {s8.Registrations} registration(s)");

        // The source of the SDK beside the probe: every call that registers,
        // removes or uninstalls a devnode is under the PnP lock.
        var outside = LockCoverage.Violations(out int sites);
        if (outside == null)
            Console.WriteLine("  info: the SDK source is not beside the probe, so the lock coverage check did not run");
        else
            Check("every call that registers, removes or uninstalls a devnode is under the PnP lock",
                  outside.Count == 0 && sites > 0,
                  outside.Count == 0 ? $"{sites} call sites" : string.Join(" | ", outside));

        // Under the lock nothing is removed and nothing waited for: what came
        // back sends the gate round again, without the lock.
        int rivalChecks = 0;
        var g9 = Gate(new Sequence { AbsentBefore = true }, rivals: remove => remove || ++rivalChecks > 1);
        Check("gate: a rival back when the lock is taken -> the lock let go, removed outside, allowed on the second take",
              g9.Gate == DeviceManager.RegistrationGate.Allowed && g9.Held && g9.Takes == 2,
              $"{g9.Gate}, {g9.Takes} takes of the lock");
        var g10 = Gate(new Sequence { AbsentBefore = true }, rivals: remove => remove);
        Check("gate: a rival back every time -> refused after 3 takes of the lock, the lock let go",
              g10.Gate == DeviceManager.RegistrationGate.Refused && !g10.Held && g10.Takes == 3,
              $"{g10.Gate}, {g10.Takes} takes, held {g10.Held}");
        var g11 = Gate(new Sequence { AbsentBefore = true, BusyUnderFirstLock = true });
        Check("gate: an install pending under the lock with time left -> the lock let go, allowed on the second take",
              g11.Gate == DeviceManager.RegistrationGate.Allowed && g11.Held && g11.Takes == 2,
              $"{g11.Gate}, {g11.Takes} takes");

        // A software device's record stays by design: a phantom one is not
        // removed, a prior life in the tree is, and must pass its test.
        var g12 = Gate(new Sequence { OutOfTreeBefore = true, RecordGoneAt = null }, kind: DeviceManager.RegistrationKind.Software);
        Check("gate, software device: a record with no devnode -> allowed without a removal",
              g12.Gate == DeviceManager.RegistrationGate.Allowed && g12.Removals == 0 && g12.Held,
              $"{g12.Gate}, {g12.Removals} removal(s)");
        var g13 = Gate(new Sequence { LeavesTree = 50, RemovedAt = 150, RecordGoneAt = null }, removalBegins: true,
                       kind: DeviceManager.RegistrationKind.Software);
        Check("gate, software device: a prior life in the tree, out at 50, REMOVED at 150, record kept -> allowed at 150",
              g13.Gate == DeviceManager.RegistrationGate.Allowed && Within(g13.Ms, 150) && g13.Removals == 1,
              $"{g13.Gate} at {g13.Ms} ms, {g13.Removals} removal(s)");

        Check("gate: no removal, prefix removal or install wait ran under the lock in any gate run",
              s_underLock == 0, $"{s_underLock}");

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
