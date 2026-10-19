// The removal test's and the registration gate's decisions, fed recorded
// sequences. No device, no elevation and no PnP call: every locate, REMOVED
// registration and install-queue wait the SDK makes here goes through
// DeviceManager's hooks, which answer from a sequence on one clock.
//
// The sequence the 0x139 investigation asked for: the devnode leaves the
// device tree at 0 ms, its DEVICEINSTANCEREMOVED notification arrives at
// 200 ms, and its PHANTOM record goes at 300 ms. A removal is complete, and
// the id may be registered again, only at 300 ms. Before v2.0.0 the wait's
// test was "REMOVED delivered, or a NORMAL locate fails" and the gate had no
// test of its own, so both answered at 0 ms. No check here may see a
// CM_LOCATE_DEVNODE_CANCELREMOVE locate, which cancels a removal in progress.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32.SafeHandles;
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
        /// <summary>A second REMOVED, for a devnode that came back and left
        /// again: delivered to every registration still open.</summary>
        public long? RemovedAgainAt;
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
        Timer? _timer, _timerAgain;

        public long Now => Clock.IsRunning ? Clock.ElapsedMilliseconds : -1;

        public void Begin()
        {
            Clock.Start();
            if (RemovedAt is long at)
                _timer = new Timer(_ => Fire(at), null, at, Timeout.Infinite);
            if (RemovedAgainAt is long again)
                _timerAgain = new Timer(_ => Fire(again), null, again, Timeout.Infinite);
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
            _timerAgain?.Dispose();
        }
    }

    /// <summary>DeviceManager.WaitRemovalComplete on a devnode in the tree,
    /// called as the sequence begins: the watch starts inside the call.</summary>
    static (bool Done, long Ms) Complete(Sequence s, bool uninstalls, int budgetMs)
    {
        s.Hook();
        try
        {
            s.Begin();
            bool done = DeviceManager.WaitRemovalComplete(Id, uninstalls, budgetMs);
            return (done, s.Now);
        }
        finally { s.End(); }
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

    /// <summary>A removal in steps on one devnode, through the SDK's
    /// StepwiseRemoval as RemoveDeviceLocked makes it: each step's call
    /// succeeds and does nothing, and the sequence begins with the first
    /// call. A step whose start time is later than the end of the step
    /// before it waits for it, as pnputil runs between step 2's test and the
    /// fallbacks' (the devnode can leave in between). Returns each step's
    /// outcome, when the last ended, and what the removal added to the
    /// counters S77 reads.</summary>
    static (DeviceManager.RemovalOutcome[] Outcomes, long Ms, long On, long NotGone) Stepped(Sequence s, bool uninstalls,
        params (int BudgetMs, long StartAt)[] steps)
    {
        long on0 = DeviceManager.RemovalsOnNotification, notGone0 = DeviceManager.RemovalsNotGone;
        var outcomes = new DeviceManager.RemovalOutcome[steps.Length];
        s.Hook();
        try
        {
            using (var removal = new DeviceManager.StepwiseRemoval(Id))
            {
                for (int i = 0; i < steps.Length; i++)
                {
                    while (s.Now >= 0 && s.Now < steps[i].StartAt) Thread.Sleep(1);
                    bool first = i == 0;
                    outcomes[i] = removal.Step(() =>
                    {
                        if (first) s.Begin();
                        return true;
                    }, uninstalls, steps[i].BudgetMs);
                }
            }
            return (outcomes, s.Now, DeviceManager.RemovalsOnNotification - on0, DeviceManager.RemovalsNotGone - notGone0);
        }
        finally { s.End(); }
    }

    const string ChildId = @"HID\PNP_NOTIFY_CHECK_OFFLINE\1&7e57&0&0000";

    /// <summary>Devnodes for a run of DeviceManager.RemoveDevice itself,
    /// each id on its own sequence, which begins when the SDK makes that
    /// id's removal call (RemovalCallsHook). An id not listed has no
    /// devnode and no record. Records the calls in order and how many
    /// REMOVED registrations were open at once.</summary>
    sealed class Tree
    {
        public readonly Dictionary<string, Sequence> Ids = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string[]> ChildrenOf = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Calls = new();
        public int Fallbacks, Open, MostOpen;
        /// <summary>How long an id's removal call takes to return, from
        /// when its sequence begins: a call that returns once Windows has
        /// finished with the devnode.</summary>
        public readonly Dictionary<string, int> CallMs = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Ids whose sequences begin with another id's call: a
        /// removal from outside that starts as this one runs.</summary>
        public readonly Dictionary<string, string[]> BeginsWith = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Ids whose removal call throws.</summary>
        public readonly HashSet<string> Throws = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The diag lines the removal wrote (DiagTap).</summary>
        public readonly List<string> Lines = new();

        public IEnumerable<string> LinesOf(string id) =>
            Lines.Where(l => l.Contains(id + " ", StringComparison.OrdinalIgnoreCase)).Select(l => l.Trim());
        readonly Sequence _none = new() { AbsentBefore = true };

        public Sequence Of(string id) => Ids.TryGetValue(id, out var s) ? s : _none;

        public bool Call(string kind, string id)
        {
            lock (Calls) Calls.Add($"{kind} {id}");
            var s = Of(id);
            if (s.Now < 0) s.Begin();
            if (BeginsWith.TryGetValue(id, out var others))
                foreach (var o in others)
                    if (Of(o).Now < 0) Of(o).Begin();
            if (Throws.Contains(id)) throw new InvalidOperationException("the removal call of " + id + " threw");
            if (CallMs.TryGetValue(id, out int ms)) while (s.Now < ms) Thread.Sleep(1);
            return true;
        }

        public IDisposable? Register(string id, Action onRemoved)
        {
            var r = Of(id).Register(id, onRemoved);
            if (r == null) return null;
            lock (Calls)
            {
                Open++;
                MostOpen = Math.Max(MostOpen, Open);
            }
            return new Counted(this, r);
        }

        sealed class Counted : IDisposable
        {
            readonly Tree _t;
            IDisposable? _r;
            public Counted(Tree t, IDisposable r) { _t = t; _r = r; }
            public void Dispose()
            {
                if (_r == null) return;
                _r.Dispose();
                _r = null;
                lock (_t.Calls) _t.Open--;
            }
        }
    }

    sealed class StandInLock : IDisposable
    {
        public void Dispose() { }
    }

    /// <summary>Locates and REMOVED registrations answered by the tree, and
    /// the stand-in lock.</summary>
    static void Hook(Tree tree)
    {
        DeviceManager.LocateHook = (id, flags) => tree.Of(id).Locate(id, flags);
        DeviceManager.RemovedHook = tree.Register;
        DeviceManager.PnpLockHook = () => new StandInLock();
    }

    /// <summary>DeviceManager.RemoveDevice on <paramref name="parentId"/>
    /// through the hooks, so the removal runs in RemoveDeviceLocked's own
    /// order. Returns its answer, when it returned from the call, and what
    /// it added to the counts S77 reads.</summary>
    static (bool Removed, long Ms, long On, long WithParent, long Without, long NotGone) RunRemoval(Tree tree, string parentId,
        bool fast = false, int timeoutMs = 5000)
    {
        long on0 = DeviceManager.RemovalsOnNotification, with0 = DeviceManager.RemovalsWithParent,
             without0 = DeviceManager.RemovalsWithoutNotification, notGone0 = DeviceManager.RemovalsNotGone;
        Hook(tree);
        DeviceOrchestrator.DiagTap = line => { lock (tree.Lines) tree.Lines.Add(line); };
        DeviceManager.RemovalCallsHook = new DeviceManager.RemovalCalls
        {
            Children = id => tree.ChildrenOf.TryGetValue(id, out var c) ? c : Array.Empty<string>(),
            Remove = id => tree.Call("remove", id),
            Uninstall = id => tree.Call("uninstall", id),
            Close = id => tree.Call("close", id) ? 0 : 1,
            Fallbacks = _ => Interlocked.Increment(ref tree.Fallbacks),
        };
        try
        {
            var clock = Stopwatch.StartNew();
            bool removed = DeviceManager.RemoveDevice(parentId, timeoutMs, fast);
            return (removed, clock.ElapsedMilliseconds,
                    DeviceManager.RemovalsOnNotification - on0, DeviceManager.RemovalsWithParent - with0,
                    DeviceManager.RemovalsWithoutNotification - without0, DeviceManager.RemovalsNotGone - notGone0);
        }
        finally
        {
            foreach (var s in tree.Ids.Values) s.End();
            DeviceManager.RemovalCallsHook = null;
            DeviceOrchestrator.DiagTap = null;
        }
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

    /// <summary>OwnerSweep's wait on what a sweep removed, for one id whose
    /// removal the sequence plays from the call: the orphaned HID
    /// children's wait (<paramref name="orphans"/>) or AllGone.</summary>
    static (bool Gone, long Ms) Swept(Sequence s, string id, bool orphans)
    {
        s.Hook();
        try
        {
            s.Begin();
            var ids = new List<string> { id };
            bool gone = orphans ? OwnerSweep.OrphansGone(ids) : OwnerSweep.AllGone(ids);
            return (gone, s.Now);
        }
        finally { s.End(); }
    }

    static bool Within(long ms, long from) => ms >= from && ms < from + Slack;

    // A semaphore's timed wait can end a timer tick before its time on the
    // Stopwatch, so a bound's end is checked to within it.
    const long TimerSlack = 20;

    const int WaitTimeoutHr = unchecked((int)0x80070102);

    /// <summary>SwdDeviceFactory's create attempts on one id, each answered
    /// by the next of <paramref name="hrs"/> (0 for success). The sequence
    /// begins as the first attempt returns: a device that attempt created,
    /// and closed when it timed out, then leaves on the sequence's times.</summary>
    static (bool Success, int HResult, int Attempts, long LastAt, long EndAt, int Registrations) Retries(Sequence s,
        int[] backoffMs, params int[] hrs)
    {
        int attempts = 0;
        long lastAt = -1;
        s.Hook();
        try
        {
            var r = SwdDeviceFactory.CreateWithRetries(@"SWD\HIDMAESTRO\PNP_NOTIFY_CHECK", backoffMs, RetryRemovalTestMs, () =>
            {
                lastAt = s.Now;
                int hr = hrs[attempts++];
                if (attempts == 1) s.Begin();
                return new SwdDeviceFactory.Result(hr == 0, hr == 0 ? @"SWD\HIDMAESTRO\PNP_NOTIFY_CHECK" : null, hr);
            });
            return (r.Success, r.HResult, attempts, lastAt, s.Now, s.Registrations);
        }
        finally { s.End(); }
    }

    /// <summary>The removal test's budget after a timed-out attempt, here in
    /// place of an attempt's 15 s.</summary>
    const int RetryRemovalTestMs = 400;

    static readonly int[] ThreeAttempts = { 0, 1, 1 };

    const string OrphanChild = @"HID\PNP_NOTIFY_CHECK\1&7e57&0&0000";

    /// <summary>What the parent of an orphan does while the sweep waits for
    /// the PnP lock.</summary>
    enum ParentDuringWait { StaysGone, Registered, Created }

    /// <summary>The HID orphan sweep's act on one child, a record out of the
    /// tree whose parent was gone at the sweep's first look, unless
    /// <paramref name="presentFirst"/>. Taking the stand-in lock is when
    /// another program's registration of the same identity, queued ahead of
    /// the sweep, runs.</summary>
    static (bool Removed, int Removals, bool RemovedUnderLock, int LockTakes, int Registrations) Orphan(
        string parentId, ParentDuringWait during, bool presentFirst = false)
    {
        bool parentInTree = presentFirst, parentRecord = presentFirst, childInTree = false, childRecord = true, held = false;
        int removals = 0, takes = 0, registrations = 0;
        bool underLock = false;
        DeviceManager.LocateHook = (id, flags) =>
        {
            lock (s_flags) s_flags.Add(flags);
            bool inTree = id == parentId ? parentInTree : id == OrphanChild && childInTree;
            bool record = id == parentId ? parentRecord : id == OrphanChild && childRecord;
            bool found = flags == CM_LOCATE_DEVNODE_PHANTOM ? inTree || record : inTree;
            return found ? CR_SUCCESS : CR_NO_SUCH_DEVINST;
        };
        DeviceManager.RemovedHook = (id, onRemoved) =>
        {
            registrations++;
            return null;
        };
        DeviceManager.PnpLockHook = () =>
        {
            takes++;
            if (during != ParentDuringWait.StaysGone) parentRecord = true;
            if (during == ParentDuringWait.Created) parentInTree = childInTree = true;
            held = true;
            return new Released(() => held = false);
        };
        try
        {
            bool removed = DeviceManager.RemoveOrphanHidChild(OrphanChild, parentId, () =>
            {
                removals++;
                underLock = held;
                childInTree = childRecord = false;
                return true;
            });
            return (removed, removals, underLock, takes, registrations);
        }
        finally
        {
            DeviceManager.LocateHook = null;
            DeviceManager.RemovedHook = null;
            DeviceManager.PnpLockHook = null;
        }
    }

    /// <summary>A removal on a thread whose PnP lock waits are bounded,
    /// as the exit handler bounds them, while another thread holds the lock
    /// for <paramref name="heldMs"/> (0: nobody holds it). The lock is a
    /// stand-in that honors timeouts. Returns what the removal threw, how
    /// long it took, and how many removal calls it made.</summary>
    static (Exception? Thrown, long Ms, int Removals) BoundedRemoval(int boundMs, int heldMs, bool orphan)
    {
        using var standIn = new SemaphoreSlim(1, 1);
        DeviceManager.PnpLockHook = () => { standIn.Wait(); return new Released(() => standIn.Release()); };
        DeviceManager.PnpLockTryHook = ms => standIn.Wait(ms) ? new Released(() => standIn.Release()) : null;
        bool childRecord = true;
        DeviceManager.LocateHook = (id, flags) =>
            id == OrphanChild && flags == CM_LOCATE_DEVNODE_PHANTOM && childRecord || id == Id ? CR_SUCCESS : CR_NO_SUCH_DEVINST;
        using var held = new ManualResetEventSlim(false);
        // The holder lets go when its hold ends, or once the removal has
        // returned: nothing after that return reads the hold.
        using var letGo = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            standIn.Wait();
            held.Set();
            letGo.Wait(heldMs);
            standIn.Release();
        }) { IsBackground = true };
        if (heldMs > 0)
        {
            holder.Start();
            held.Wait();
        }
        int removals = 0;
        Exception? thrown = null;
        long ms = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            using (DeviceManager.BoundPnpLockWaits(boundMs))
            {
                if (orphan)
                    DeviceManager.RemoveOrphanHidChild(OrphanChild, @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_PARENT", () =>
                    {
                        removals++;
                        childRecord = false;
                        return true;
                    });
                else
                    DeviceManager.RemoveDevice(Id, timeoutMs: 1000);
            }
        }
        catch (Exception ex) { thrown = ex; }
        finally
        {
            ms = sw.ElapsedMilliseconds;
            letGo.Set();
            if (heldMs > 0) holder.Join();
            DeviceManager.PnpLockHook = null;
            DeviceManager.PnpLockTryHook = null;
            DeviceManager.LocateHook = null;
        }
        return (thrown, ms, removals);
    }

    sealed class Released : IDisposable
    {
        readonly Action _a;
        public Released(Action a) => _a = a;
        public void Dispose() => _a();
    }

    /// <summary>Two orphan removals on a thread whose PnP lock waits are
    /// bounded to <paramref name="boundMs"/>: the first one's own work under
    /// the lock takes <paramref name="workMs"/> with no other holder, then
    /// another thread holds the lock for <paramref name="otherHoldMs"/> and
    /// the second removal takes it. The case the round 4 review showed: a
    /// bound that counted the work left the second take nothing.</summary>
    static (Exception? Thrown, int Removals, long SecondMs) WorkThenWait(int boundMs, int workMs, int otherHoldMs)
    {
        using var standIn = new SemaphoreSlim(1, 1);
        DeviceManager.PnpLockHook = () => { standIn.Wait(); return new Released(() => standIn.Release()); };
        DeviceManager.PnpLockTryHook = ms => standIn.Wait(ms) ? new Released(() => standIn.Release()) : null;
        const string C1 = @"HID\PNP_NOTIFY_CHECK\1&7e57&0&0001", C2 = @"HID\PNP_NOTIFY_CHECK\1&7e57&0&0002";
        bool rec1 = true, rec2 = true;
        DeviceManager.LocateHook = (id, flags) =>
            flags == CM_LOCATE_DEVNODE_PHANTOM && (id == C1 && rec1 || id == C2 && rec2) ? CR_SUCCESS : CR_NO_SUCH_DEVINST;
        int removals = 0;
        Exception? thrown = null;
        long second = -1;
        Thread? other = null;
        try
        {
            using (DeviceManager.BoundPnpLockWaits(boundMs))
            {
                DeviceManager.RemoveOrphanHidChild(C1, @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_PARENT", () =>
                {
                    removals++;
                    Thread.Sleep(workMs);
                    rec1 = false;
                    return true;
                });
                using var holding = new ManualResetEventSlim(false);
                other = new Thread(() =>
                {
                    standIn.Wait();
                    holding.Set();
                    Thread.Sleep(otherHoldMs);
                    standIn.Release();
                }) { IsBackground = true };
                other.Start();
                holding.Wait();
                var sw = Stopwatch.StartNew();
                try
                {
                    DeviceManager.RemoveOrphanHidChild(C2, @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_PARENT", () =>
                    {
                        removals++;
                        rec2 = false;
                        return true;
                    });
                }
                finally { second = sw.ElapsedMilliseconds; }
            }
        }
        catch (Exception ex) { thrown = ex; }
        finally
        {
            other?.Join();
            DeviceManager.PnpLockHook = null;
            DeviceManager.PnpLockTryHook = null;
            DeviceManager.LocateHook = null;
        }
        return (thrown, removals, second);
    }

    /// <summary>An orphan removal on a thread with no bound of its own,
    /// with the process's exit signal set or not, while another thread holds
    /// the lock for <paramref name="heldMs"/>: a program that disposes from
    /// its own exit handler. The exit budget is set to
    /// <paramref name="exitBudgetMs"/> in place of the exit's 10 s, and both
    /// go back once the removal ends.</summary>
    static (Exception? Thrown, long Ms, int Removals, long Refused) InExit(bool exiting, int exitBudgetMs, int heldMs)
    {
        var raised = typeof(ProcessExitSignal).GetField("s_raised", BindingFlags.NonPublic | BindingFlags.Static)!;
        var budget = typeof(DeviceManager).GetField("s_exitLockBudget", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var standIn = new SemaphoreSlim(1, 1);
        DeviceManager.PnpLockHook = () => { standIn.Wait(); return new Released(() => standIn.Release()); };
        DeviceManager.PnpLockTryHook = ms => standIn.Wait(ms) ? new Released(() => standIn.Release()) : null;
        bool childRecord = true;
        DeviceManager.LocateHook = (id, flags) =>
            id == OrphanChild && flags == CM_LOCATE_DEVNODE_PHANTOM && childRecord ? CR_SUCCESS : CR_NO_SUCH_DEVINST;
        using var held = new ManualResetEventSlim(false);
        // The holder lets go when its hold ends, or once the removal has
        // returned: nothing after that return reads the hold.
        using var letGo = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            standIn.Wait();
            held.Set();
            letGo.Wait(heldMs);
            standIn.Release();
        }) { IsBackground = true };
        holder.Start();
        held.Wait();
        int removals = 0;
        Exception? thrown = null;
        long refused0 = DeviceManager.LockTakesRefusedHere, ms = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            if (exiting)
            {
                budget.SetValue(null, new DeviceManager.LockWaitBudget(exitBudgetMs, shared: true));
                raised.SetValue(null, true);
            }
            DeviceManager.RemoveOrphanHidChild(OrphanChild, @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_PARENT", () =>
            {
                removals++;
                childRecord = false;
                return true;
            });
        }
        catch (Exception ex) { thrown = ex; }
        finally
        {
            ms = sw.ElapsedMilliseconds;
            raised.SetValue(null, false);
            budget.SetValue(null, null);
            letGo.Set();
            holder.Join();
            DeviceManager.PnpLockHook = null;
            DeviceManager.PnpLockTryHook = null;
            DeviceManager.LocateHook = null;
        }
        return (thrown, ms, removals, DeviceManager.LockTakesRefusedHere - refused0);
    }

    /// <summary>Orphan removals of this process on <paramref name="threads"/>
    /// threads at once, none with a bound of its own, the exit signal set and
    /// the exit's budget at <paramref name="exitBudgetMs"/>: a program that
    /// disposes several controllers in its own exit handler, each teardown on
    /// a thread of its own. Each removal does <paramref name="workMs"/> of
    /// its own work under the lock. Another program holds the lock first for
    /// up to <paramref name="otherHoldMs"/>, let go once the removals end (0:
    /// no other program). Returns the removals, the refusals and how long
    /// each thread took.</summary>
    static (int Removed, int Refused, long[] Ms) ConcurrentInExit(int threads, int exitBudgetMs, int workMs, int otherHoldMs)
    {
        var raised = typeof(ProcessExitSignal).GetField("s_raised", BindingFlags.NonPublic | BindingFlags.Static)!;
        var budget = typeof(DeviceManager).GetField("s_exitLockBudget", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var standIn = new SemaphoreSlim(1, 1);
        DeviceManager.PnpLockHook = () => { standIn.Wait(); return new Released(() => standIn.Release()); };
        DeviceManager.PnpLockTryHook = ms => standIn.Wait(ms) ? new Released(() => standIn.Release()) : null;
        string[] kids = Enumerable.Range(0, threads).Select(i => $@"HID\PNP_NOTIFY_CHECK\1&7e57&0&{20 + i:D4}").ToArray();
        var records = new HashSet<string>(kids);
        DeviceManager.LocateHook = (id, flags) =>
        {
            lock (records) return flags == CM_LOCATE_DEVNODE_PHANTOM && records.Contains(id) ? CR_SUCCESS : CR_NO_SUCH_DEVINST;
        };
        using var held = new ManualResetEventSlim(false);
        using var letGo = new ManualResetEventSlim(false);
        Thread? other = null;
        int removed = 0, refused = 0;
        var ms = new long[threads];
        try
        {
            if (otherHoldMs > 0)
            {
                other = new Thread(() =>
                {
                    standIn.Wait();
                    held.Set();
                    letGo.Wait(otherHoldMs);
                    standIn.Release();
                }) { IsBackground = true };
                other.Start();
                held.Wait();
            }
            budget.SetValue(null, new DeviceManager.LockWaitBudget(exitBudgetMs, shared: true));
            raised.SetValue(null, true);
            using var start = new Barrier(threads);
            var workers = Enumerable.Range(0, threads).Select(i => new Thread(() =>
            {
                start.SignalAndWait();
                var sw = Stopwatch.StartNew();
                long refused0 = DeviceManager.LockTakesRefusedHere;
                try
                {
                    DeviceManager.RemoveOrphanHidChild(kids[i], @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_PARENT", () =>
                    {
                        Thread.Sleep(workMs);
                        lock (records) records.Remove(kids[i]);
                        Interlocked.Increment(ref removed);
                        return true;
                    });
                }
                catch (DeviceManager.PnpLockBusyException) { }
                ms[i] = sw.ElapsedMilliseconds;
                if (DeviceManager.LockTakesRefusedHere != refused0) Interlocked.Increment(ref refused);
            }) { IsBackground = true }).ToList();
            workers.ForEach(t => t.Start());
            workers.ForEach(t => t.Join());
        }
        finally
        {
            raised.SetValue(null, false);
            budget.SetValue(null, null);
            letGo.Set();
            other?.Join();
            DeviceManager.PnpLockHook = null;
            DeviceManager.PnpLockTryHook = null;
            DeviceManager.LocateHook = null;
        }
        return (removed, refused, ms);
    }

    /// <summary>An orphan removal inside the exit handler's own bound of
    /// <paramref name="boundMs"/>, while another thread of this process is
    /// in front of it: with <paramref name="otherHoldMs"/> 0, that thread
    /// holds the lock for <paramref name="ownWorkMs"/> of its own removal.
    /// Otherwise another program holds the lock for up to
    /// <paramref name="otherHoldMs"/>, and the thread in front, with no bound
    /// of its own, waits for it before its removal. Returns what the bounded
    /// removal threw, how long it took, and whether each removal
    /// happened.</summary>
    static (Exception? Thrown, long Ms, bool Bounded, bool InFront) BoundBehindOwnThread(int boundMs, int ownWorkMs, int otherHoldMs)
    {
        using var standIn = new SemaphoreSlim(1, 1);
        using var inMachineWait = new ManualResetEventSlim(false);
        DeviceManager.PnpLockHook = () => { standIn.Wait(); return new Released(() => standIn.Release()); };
        DeviceManager.PnpLockTryHook = ms =>
        {
            inMachineWait.Set();
            return standIn.Wait(ms) ? new Released(() => standIn.Release()) : null;
        };
        const string A = @"HID\PNP_NOTIFY_CHECK\1&7e57&0&0031", B = @"HID\PNP_NOTIFY_CHECK\1&7e57&0&0032";
        var records = new HashSet<string> { A, B };
        DeviceManager.LocateHook = (id, flags) =>
        {
            lock (records) return flags == CM_LOCATE_DEVNODE_PHANTOM && records.Contains(id) ? CR_SUCCESS : CR_NO_SUCH_DEVINST;
        };
        using var held = new ManualResetEventSlim(false);
        using var letGo = new ManualResetEventSlim(false);
        using var working = new ManualResetEventSlim(false);
        bool bounded = false, inFront = false;
        Exception? thrown = null;
        long ms = 0;
        Thread? other = null;
        var front = new Thread(() => DeviceManager.RemoveOrphanHidChild(A, @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_PARENT", () =>
        {
            working.Set();
            Thread.Sleep(ownWorkMs);
            lock (records) records.Remove(A);
            inFront = true;
            return true;
        })) { IsBackground = true };
        try
        {
            if (otherHoldMs > 0)
            {
                other = new Thread(() =>
                {
                    standIn.Wait();
                    held.Set();
                    letGo.Wait(otherHoldMs);
                    standIn.Release();
                }) { IsBackground = true };
                other.Start();
                held.Wait();
                front.Start();
                inMachineWait.Wait();
            }
            else
            {
                front.Start();
                working.Wait();
            }
            var sw = Stopwatch.StartNew();
            try
            {
                using (DeviceManager.BoundPnpLockWaits(boundMs))
                    DeviceManager.RemoveOrphanHidChild(B, @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_PARENT", () =>
                    {
                        lock (records) records.Remove(B);
                        bounded = true;
                        return true;
                    });
            }
            catch (Exception ex) { thrown = ex; }
            ms = sw.ElapsedMilliseconds;
        }
        finally
        {
            letGo.Set();
            other?.Join();
            front.Join();
            DeviceManager.PnpLockHook = null;
            DeviceManager.PnpLockTryHook = null;
            DeviceManager.LocateHook = null;
        }
        return (thrown, ms, bounded, inFront);
    }

    /// <summary>The exit's shared budget spent once by threads that wait
    /// together: with the exit signal set and a budget of
    /// <paramref name="exitBudgetMs"/>, <paramref name="threads"/> removals
    /// of this process at once behind another program's hold of
    /// <paramref name="firstHoldMs"/>, then one more removal behind another
    /// hold of <paramref name="laterHoldMs"/>. Returns how many of the first
    /// removed, whether the later one did, and how long it took.</summary>
    static (int First, bool Later, long LaterMs) SharedBudgetLater(int threads, int exitBudgetMs, int firstHoldMs, int laterHoldMs)
    {
        var raised = typeof(ProcessExitSignal).GetField("s_raised", BindingFlags.NonPublic | BindingFlags.Static)!;
        var budget = typeof(DeviceManager).GetField("s_exitLockBudget", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var standIn = new SemaphoreSlim(1, 1);
        DeviceManager.PnpLockHook = () => { standIn.Wait(); return new Released(() => standIn.Release()); };
        DeviceManager.PnpLockTryHook = ms => standIn.Wait(ms) ? new Released(() => standIn.Release()) : null;
        string[] kids = Enumerable.Range(0, threads + 1).Select(i => $@"HID\PNP_NOTIFY_CHECK\1&7e57&0&{40 + i:D4}").ToArray();
        var records = new HashSet<string>(kids);
        DeviceManager.LocateHook = (id, flags) =>
        {
            lock (records) return flags == CM_LOCATE_DEVNODE_PHANTOM && records.Contains(id) ? CR_SUCCESS : CR_NO_SUCH_DEVINST;
        };
        var holders = new List<Thread>();
        void OtherHolds(int ms)
        {
            using var held = new ManualResetEventSlim(false);
            var t = new Thread(() =>
            {
                standIn.Wait();
                held.Set();
                Thread.Sleep(ms);
                standIn.Release();
            }) { IsBackground = true };
            holders.Add(t);
            t.Start();
            held.Wait();
        }
        bool Remove(string kid)
        {
            try
            {
                DeviceManager.RemoveOrphanHidChild(kid, @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_PARENT", () =>
                {
                    lock (records) records.Remove(kid);
                    return true;
                });
                return true;
            }
            catch (DeviceManager.PnpLockBusyException) { return false; }
        }
        int first = 0;
        bool later = false;
        long laterMs = -1;
        try
        {
            OtherHolds(firstHoldMs);
            budget.SetValue(null, new DeviceManager.LockWaitBudget(exitBudgetMs, shared: true));
            raised.SetValue(null, true);
            using var start = new Barrier(threads);
            var workers = Enumerable.Range(0, threads).Select(i => new Thread(() =>
            {
                start.SignalAndWait();
                if (Remove(kids[i])) Interlocked.Increment(ref first);
            }) { IsBackground = true }).ToList();
            workers.ForEach(t => t.Start());
            workers.ForEach(t => t.Join());
            OtherHolds(laterHoldMs);
            var sw = Stopwatch.StartNew();
            later = Remove(kids[threads]);
            laterMs = sw.ElapsedMilliseconds;
        }
        finally
        {
            raised.SetValue(null, false);
            budget.SetValue(null, null);
            holders.ForEach(t => t.Join());
            DeviceManager.PnpLockHook = null;
            DeviceManager.PnpLockTryHook = null;
            DeviceManager.LocateHook = null;
        }
        return (first, later, laterMs);
    }

    /// <summary>With the exit signal set and a budget of
    /// <paramref name="exitBudgetMs"/>: one removal of this process waits
    /// behind another program's hold of <paramref name="otherHoldMs"/>,
    /// which spends that much of the budget or all of it, and once that hold
    /// has ended <paramref name="threads"/> removals of this process at once
    /// each do <paramref name="workMs"/> of work under the lock. Returns
    /// whether the first removed, and how many of the others removed and
    /// were refused.</summary>
    static (bool First, int Removed, int Refused) SpentThenOwnInExit(int exitBudgetMs, int otherHoldMs, int threads, int workMs)
    {
        var raised = typeof(ProcessExitSignal).GetField("s_raised", BindingFlags.NonPublic | BindingFlags.Static)!;
        var budget = typeof(DeviceManager).GetField("s_exitLockBudget", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var standIn = new SemaphoreSlim(1, 1);
        DeviceManager.PnpLockHook = () => { standIn.Wait(); return new Released(() => standIn.Release()); };
        DeviceManager.PnpLockTryHook = ms => standIn.Wait(ms) ? new Released(() => standIn.Release()) : null;
        string[] kids = Enumerable.Range(0, threads + 1).Select(i => $@"HID\PNP_NOTIFY_CHECK\1&7e57&0&{80 + i:D4}").ToArray();
        var records = new HashSet<string>(kids);
        DeviceManager.LocateHook = (id, flags) =>
        {
            lock (records) return flags == CM_LOCATE_DEVNODE_PHANTOM && records.Contains(id) ? CR_SUCCESS : CR_NO_SUCH_DEVINST;
        };
        bool Remove(string kid, int work)
        {
            try
            {
                DeviceManager.RemoveOrphanHidChild(kid, @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_PARENT", () =>
                {
                    Thread.Sleep(work);
                    lock (records) records.Remove(kid);
                    return true;
                });
                return true;
            }
            catch (DeviceManager.PnpLockBusyException) { return false; }
        }
        using var held = new ManualResetEventSlim(false);
        var other = new Thread(() =>
        {
            standIn.Wait();
            held.Set();
            Thread.Sleep(otherHoldMs);
            standIn.Release();
        }) { IsBackground = true };
        bool first = false;
        int removed = 0, refused = 0;
        try
        {
            budget.SetValue(null, new DeviceManager.LockWaitBudget(exitBudgetMs, shared: true));
            raised.SetValue(null, true);
            other.Start();
            held.Wait();
            first = Remove(kids[0], 0);
            other.Join();
            using var start = new Barrier(threads);
            var workers = Enumerable.Range(1, threads).Select(i => new Thread(() =>
            {
                start.SignalAndWait();
                if (Remove(kids[i], workMs)) Interlocked.Increment(ref removed);
                else Interlocked.Increment(ref refused);
            }) { IsBackground = true }).ToList();
            workers.ForEach(t => t.Start());
            workers.ForEach(t => t.Join());
        }
        finally
        {
            raised.SetValue(null, false);
            budget.SetValue(null, null);
            if (other.IsAlive) other.Join();
            DeviceManager.PnpLockHook = null;
            DeviceManager.PnpLockTryHook = null;
            DeviceManager.LocateHook = null;
        }
        return (first, removed, refused);
    }

    /// <summary>Two removals inside one bound of <paramref name="boundMs"/>
    /// outside the exit: the first behind another thread of this process
    /// that holds the lock <paramref name="ownWorkMs"/> for its removal, the
    /// second behind another program's hold of <paramref name="otherHoldMs"/>.
    /// Returns whether each removed and how long the second took.</summary>
    static (bool First, bool Second, long SecondMs) BoundAfterOwnHold(int boundMs, int ownWorkMs, int otherHoldMs)
    {
        using var standIn = new SemaphoreSlim(1, 1);
        DeviceManager.PnpLockHook = () => { standIn.Wait(); return new Released(() => standIn.Release()); };
        DeviceManager.PnpLockTryHook = ms => standIn.Wait(ms) ? new Released(() => standIn.Release()) : null;
        const string F = @"HID\PNP_NOTIFY_CHECK\1&7e57&0&0050", A = @"HID\PNP_NOTIFY_CHECK\1&7e57&0&0051", B = @"HID\PNP_NOTIFY_CHECK\1&7e57&0&0052";
        var records = new HashSet<string> { F, A, B };
        DeviceManager.LocateHook = (id, flags) =>
        {
            lock (records) return flags == CM_LOCATE_DEVNODE_PHANTOM && records.Contains(id) ? CR_SUCCESS : CR_NO_SUCH_DEVINST;
        };
        using var working = new ManualResetEventSlim(false);
        bool first = false, second = false;
        long secondMs = -1;
        Thread? other = null;
        var front = new Thread(() => DeviceManager.RemoveOrphanHidChild(F, @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_PARENT", () =>
        {
            working.Set();
            Thread.Sleep(ownWorkMs);
            lock (records) records.Remove(F);
            return true;
        })) { IsBackground = true };
        try
        {
            front.Start();
            working.Wait();
            using (DeviceManager.BoundPnpLockWaits(boundMs))
            {
                try
                {
                    DeviceManager.RemoveOrphanHidChild(A, @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_PARENT", () =>
                    {
                        lock (records) records.Remove(A);
                        return true;
                    });
                    first = true;
                }
                catch (DeviceManager.PnpLockBusyException) { }
                using var held = new ManualResetEventSlim(false);
                other = new Thread(() =>
                {
                    standIn.Wait();
                    held.Set();
                    Thread.Sleep(otherHoldMs);
                    standIn.Release();
                }) { IsBackground = true };
                other.Start();
                held.Wait();
                var sw = Stopwatch.StartNew();
                try
                {
                    DeviceManager.RemoveOrphanHidChild(B, @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_PARENT", () =>
                    {
                        lock (records) records.Remove(B);
                        return true;
                    });
                    second = true;
                }
                catch (DeviceManager.PnpLockBusyException) { }
                secondMs = sw.ElapsedMilliseconds;
            }
        }
        finally
        {
            other?.Join();
            front.Join();
            DeviceManager.PnpLockHook = null;
            DeviceManager.PnpLockTryHook = null;
            DeviceManager.LocateHook = null;
        }
        return (first, second, secondMs);
    }

    /// <summary>A removal with no bound of its own, the exit signal set and
    /// the exit's budget at <paramref name="exitBudgetMs"/>, behind another
    /// thread of this process that holds the lock <paramref name="ownHoldMs"/>
    /// for its removal, a call stuck under the lock. No other program holds
    /// it. Returns what the queued removal threw, how long it took, whether
    /// it removed, its refusals, and whether the one in front removed.</summary>
    static (Exception? Thrown, long Ms, bool Removed, long Refused, bool InFront) StuckOwnHoldInExit(int exitBudgetMs, int ownHoldMs)
    {
        var raised = typeof(ProcessExitSignal).GetField("s_raised", BindingFlags.NonPublic | BindingFlags.Static)!;
        var budget = typeof(DeviceManager).GetField("s_exitLockBudget", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var standIn = new SemaphoreSlim(1, 1);
        DeviceManager.PnpLockHook = () => { standIn.Wait(); return new Released(() => standIn.Release()); };
        DeviceManager.PnpLockTryHook = ms => standIn.Wait(ms) ? new Released(() => standIn.Release()) : null;
        const string F = @"HID\PNP_NOTIFY_CHECK\1&7e57&0&0055", Q = @"HID\PNP_NOTIFY_CHECK\1&7e57&0&0056";
        var records = new HashSet<string> { F, Q };
        DeviceManager.LocateHook = (id, flags) =>
        {
            lock (records) return flags == CM_LOCATE_DEVNODE_PHANTOM && records.Contains(id) ? CR_SUCCESS : CR_NO_SUCH_DEVINST;
        };
        using var working = new ManualResetEventSlim(false);
        // The one in front ends its work when its hold ends, or once the
        // queued removal has returned: nothing after that return reads the
        // hold.
        using var letGo = new ManualResetEventSlim(false);
        bool removed = false, inFront = false;
        Exception? thrown = null;
        long ms = 0, refused0 = DeviceManager.LockTakesRefusedHere;
        var front = new Thread(() => DeviceManager.RemoveOrphanHidChild(F, @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_PARENT", () =>
        {
            working.Set();
            letGo.Wait(ownHoldMs);
            lock (records) records.Remove(F);
            inFront = true;
            return true;
        })) { IsBackground = true };
        try
        {
            budget.SetValue(null, new DeviceManager.LockWaitBudget(exitBudgetMs, shared: true));
            raised.SetValue(null, true);
            front.Start();
            working.Wait();
            var sw = Stopwatch.StartNew();
            try
            {
                DeviceManager.RemoveOrphanHidChild(Q, @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_PARENT", () =>
                {
                    lock (records) records.Remove(Q);
                    removed = true;
                    return true;
                });
            }
            catch (Exception ex) { thrown = ex; }
            ms = sw.ElapsedMilliseconds;
        }
        finally
        {
            letGo.Set();
            front.Join();
            raised.SetValue(null, false);
            budget.SetValue(null, null);
            DeviceManager.PnpLockHook = null;
            DeviceManager.PnpLockTryHook = null;
            DeviceManager.LocateHook = null;
        }
        return (thrown, ms, removed, DeviceManager.LockTakesRefusedHere - refused0, inFront);
    }

    /// <summary>The phantom cleanup a first call starts while the process
    /// exits, which runs on the calling thread
    /// (OwnerSweep.StartPhantomCleanupOnce): two phantoms, each uninstall
    /// taking <paramref name="workMs"/> under the PnP lock, the stand-in lock
    /// free. Returns the bound each take of the lock asked for, -1 for a take
    /// with no bound, and how many phantoms the run removed.</summary>
    static (List<int> Takes, int Removed) PhantomCleanupInExit(int workMs)
    {
        var raised = typeof(ProcessExitSignal).GetField("s_raised", BindingFlags.NonPublic | BindingFlags.Static)!;
        var budget = typeof(DeviceManager).GetField("s_exitLockBudget", BindingFlags.NonPublic | BindingFlags.Static)!;
        var started = typeof(OwnerSweep).GetField("s_phantomCleanupStarted", BindingFlags.NonPublic | BindingFlags.Static)!;
        object? startedBefore = started.GetValue(null);
        var takes = new List<int>();
        DeviceManager.PnpLockHook = () => { lock (takes) takes.Add(-1); return new Released(() => { }); };
        DeviceManager.PnpLockTryHook = ms => { lock (takes) takes.Add(ms); return new Released(() => { }); };
        Func<bool> uninstall = () => { Thread.Sleep(workMs); return true; };
        DeviceManager.PhantomSource = () => new (string, Func<bool>)[]
        {
            (@"ROOT\HIDMAESTRO_PNP_NOTIFY_CHECK\0001", uninstall),
            (@"ROOT\HIDMAESTRO_PNP_NOTIFY_CHECK\0002", uninstall),
        };
        DeviceManager.RemovalLocate = _ => false;
        try
        {
            started.SetValue(null, 0);
            budget.SetValue(null, null);
            raised.SetValue(null, true);
            OwnerSweep.StartPhantomCleanupOnce();
            lock (takes) return (new List<int>(takes), OwnerSweep.LastPhantomCleanup?.Removed ?? -1);
        }
        finally
        {
            raised.SetValue(null, false);
            budget.SetValue(null, null);
            started.SetValue(null, startedBefore);
            DeviceManager.PnpLockHook = null;
            DeviceManager.PnpLockTryHook = null;
            DeviceManager.PhantomSource = null;
            DeviceManager.RemovalLocate = null;
        }
    }

    /// <summary>The SDK's tool runs, which the removal paths make under the
    /// PnP lock (DeviceOrchestrator.RunProcess for reg.exe and sc.exe,
    /// DeviceManager's pnputil and devcon fallbacks), end within their
    /// bound when the tool leaves a process holding its output pipe: here
    /// cmd.exe starts ping.exe on the same pipe and ends at once. At
    /// 17e98e16 RunProcess read to the pipe's end and returned only when
    /// ping did, 20 s later. ping is ended in the finally.</summary>
    static void ChildOutputBound()
    {
        var run = typeof(DeviceOrchestrator).GetMethod("RunProcess", BindingFlags.Static | BindingFlags.NonPublic);
        if (run == null) { Check("DeviceOrchestrator.RunProcess exists", false); return; }
        var started = DateTime.Now.AddSeconds(-1);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = run.Invoke(null, new object[] { "cmd.exe", "/c start \"\" /b ping.exe -n 21 127.0.0.191", 30_000 });
            long ms = sw.ElapsedMilliseconds;
            Check("RunProcess returns within its read bound when the tool leaves a process holding its pipe",
                  ms < 8_000, $"{ms} ms, {result}");
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName("PING"))
            {
                using (p)
                {
                    try { if (p.StartTime >= started) { p.Kill(); p.WaitForExit(2000); } }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }
        }
    }

    /// <summary>The part of <see cref="Run"/> this process runs, 0 for every
    /// part in turn.</summary>
    static int s_part;

    static bool Part(int n) => s_part == 0 || s_part == n;

    /// <summary>Run's parts, each in a process of its own.</summary>
    const int Parts = 10;

    /// <summary>The checks in parts that run at once, each in a process of
    /// its own: the sequences run on the wall clock, and the SDK's hooks
    /// and counters are static, so a process holds one part's. Every check
    /// runs as it does in one process, and the output reads in the same
    /// order but for two software-parent checks moved up with the removals
    /// they read. The locate checks add up every part's locates. One process
    /// ran them one after another in about 56 s, the longest part takes
    /// about 13 s.</summary>
    internal static int RunInParts()
    {
        Console.WriteLine("=== Removal test and registration gate, offline ===");
        SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
        var parts = new (Process P, System.Threading.Tasks.Task<string> Out)[Parts];
        for (int n = 1; n <= Parts; n++)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("offline");
            psi.ArgumentList.Add(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var proc = Process.Start(psi)!;
            var err = proc.StandardError.ReadToEndAsync();
            parts[n - 1] = (proc, System.Threading.Tasks.Task.WhenAll(proc.StandardOutput.ReadToEndAsync(), err)
                                      .ContinueWith(t => t.Result[0] + t.Result[1]));
        }
        long locates = 0, cancels = 0;
        for (int n = 1; n <= Parts; n++)
        {
            var (proc, output) = parts[n - 1];
            if (!proc.WaitForExit(300_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                proc.WaitForExit();
            }
            bool finished = false;
            foreach (string line in output.Result.Replace("\r", "").Split('\n'))
            {
                if (line.StartsWith("##PART ", StringComparison.Ordinal))
                {
                    var f = line.Split(' ');
                    s_total += int.Parse(f[2]);
                    s_failures += int.Parse(f[3]);
                    locates += long.Parse(f[4]);
                    cancels += long.Parse(f[5]);
                    finished = true;
                }
                else if (line.Length > 0) Console.WriteLine(line);
            }
            if (!finished || proc.ExitCode != 0)
                Check($"part {n} of the offline checks ran to its end", false, $"exit {proc.ExitCode}");
            proc.Dispose();
            // The locate checks come after the exit's removals, before the
            // tool run, as in one process.
            if (n == Parts - 1)
            {
                Check("the waits and the gate located through the hook", locates > 0, $"{locates} locates");
                Check("no CM_LOCATE_DEVNODE_CANCELREMOVE locate was issued", cancels == 0, $"{cancels} of {locates}");
            }
        }
        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    const uint SEM_FAILCRITICALERRORS = 0x0001, SEM_NOGPFAULTERRORBOX = 0x0002, SEM_NOOPENFILEERRORBOX = 0x8000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint mode);

    /// <summary>The checks, every part in turn when <paramref name="part"/>
    /// is 0, or one part, which ends with a line the parent reads: the part,
    /// its checks and failures, its locates and its CANCELREMOVE
    /// locates.</summary>
    internal static int Run(int part = 0)
    {
        s_part = part;
        if (part == 0) Console.WriteLine("=== Removal test and registration gate, offline ===");

        // The decision itself, at the sequence's event times: a devnode that
        // was present when the wait registered, removed by an uninstall.
        const DeviceManager.RemovalOutcome Removed = DeviceManager.RemovalOutcome.Removed;
        const DeviceManager.RemovalOutcome Pending = DeviceManager.RemovalOutcome.Pending;
        const DeviceManager.RemovalOutcome GoneWithout = DeviceManager.RemovalOutcome.GoneWithoutRemoved;
        const DeviceManager.RemovalOutcome NotGone = DeviceManager.RemovalOutcome.NotGone;
        DeviceManager.RemovalOutcome At(long t, bool overdue = false) => DeviceManager.RemovalComplete(new DeviceManager.RemovalObservation(
            presentAtStart: true, registered: true, removedDelivered: t >= 200, inTree: t < 0, record: t < 300), uninstalls: true, overdue);
        if (Part(1))
        {
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
            long on0 = DeviceManager.RemovalsOnNotification, without0 = DeviceManager.RemovalsWithoutNotification,
                 notGone0 = DeviceManager.RemovalsNotGone, unreg0 = DeviceManager.RemovalsUnregistered;
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

            // A removal in steps on one devnode counts once, by its last step,
            // as present when its first step started. A fast removal's
            // CM_Uninstall_DevNode can leave the devnode in the tree, and the
            // fallbacks then remove it: the first step is not the removal's end.
            var st1 = Stepped(new Sequence { LeavesTree = 300, RemovedAt = 350, RecordGoneAt = 350 }, uninstalls: true,
                              (200, 0), (2000, 0));
            Check("steps: in the tree past step 2's 200 ms, removed by the fallbacks at 350 with REMOVED -> counted once, on the notification",
                  st1.Outcomes[0] == NotGone && st1.Outcomes[1] == Removed && Within(st1.Ms, 350) && st1.On == 1 && st1.NotGone == 0,
                  $"{string.Join(", ", st1.Outcomes)} at {st1.Ms} ms, {st1.On} on the notification, {st1.NotGone} not gone");
            // The devnode leaves the tree between the steps but keeps its
            // record, which the fallbacks cannot delete: the removal ended not
            // gone, and S77 must see it.
            var st2 = Stepped(new Sequence { LeavesTree = 300, RecordGoneAt = null, RemovedAt = null }, uninstalls: true,
                              (200, 0), (400, 0));
            Check("steps: out of the tree at 300 with its record kept through the fallbacks -> counted once, not gone",
                  st2.Outcomes[0] == NotGone && st2.Outcomes[1] == NotGone && st2.NotGone == 1,
                  $"{string.Join(", ", st2.Outcomes)} at {st2.Ms} ms, {st2.NotGone} not gone");
            // Its REMOVED notification arrives between step 2's test and the
            // fallbacks': the watch kept its registration, so it is seen.
            var st3 = Stepped(new Sequence { LeavesTree = 250, RemovedAt = 250, RecordGoneAt = 250 }, uninstalls: true,
                              (200, 0), (2000, 300));
            Check("steps: out of the tree with REMOVED at 250, between step 2's test at 200 and the fallbacks' at 300 -> counted on the notification",
                  st3.Outcomes[0] == NotGone && st3.Outcomes[1] == Removed && st3.On == 1 && st3.NotGone == 0,
                  $"{string.Join(", ", st3.Outcomes)} at {st3.Ms} ms, {st3.On} on the notification, {st3.NotGone} not gone");
            var thenAnswers = new[]
            {
                DeviceManager.Then(NotGone, Removed), DeviceManager.Then(GoneWithout, Removed),
                DeviceManager.Then(Removed, GoneWithout), DeviceManager.Then(Removed, NotGone),
                DeviceManager.Then(GoneWithout, NotGone), DeviceManager.Then(Removed, Removed),
            };
            Check("a removal in two steps ends as its last step, gone without REMOVED when either step was",
                  thenAnswers.SequenceEqual(new[] { Removed, GoneWithout, GoneWithout, NotGone, NotGone, Removed }),
                  string.Join(", ", thenAnswers));

            var w7 = Complete(new Sequence { LeavesTree = 100, RemovedAt = 250, RecordGoneAt = 300 }, uninstalls: true, 5000);
            Check("WaitRemovalComplete on a present devnode: out at 100, REMOVED at 250, record gone at 300 -> finished at 300",
                  w7.Done && Within(w7.Ms, 300), $"{w7.Done} at {w7.Ms} ms");

            // The counts S77's live part asserts on: only watches of a devnode
            // in the tree count, once each. Six ended on the notification (w1,
            // w2, w3, w7, st1, st3), one without it (w6), two not gone (w6b,
            // st2), one with its registration refused (w4). The absent id and
            // the phantom count nothing.
            long dOn = DeviceManager.RemovalsOnNotification - on0, dWithout = DeviceManager.RemovalsWithoutNotification - without0,
                 dNotGone = DeviceManager.RemovalsNotGone - notGone0, dUnreg = DeviceManager.RemovalsUnregistered - unreg0;
            Check("the removal counts: 6 on the notification, 1 without it, 2 not gone, 1 unregistered",
                  dOn == 6 && dWithout == 1 && dNotGone == 2 && dUnreg == 1,
                  $"{dOn}, {dWithout}, {dNotGone}, {dUnreg}");

            // RemoveDevice itself, through the hooks, so the checks reach
            // RemoveDeviceLocked's own order: a ROOT parent and its HID child,
            // each REMOVED shortly after its own removal call.
            Tree Pair()
            {
                var t = new Tree();
                t.Ids[Id] = new Sequence { LeavesTree = 12, RemovedAt = 12, RecordGoneAt = 12 };
                t.Ids[ChildId] = new Sequence { LeavesTree = 0, RecordGoneAt = 0, RemovedAt = 20 };
                t.ChildrenOf[Id] = new[] { ChildId };
                return t;
            }
            var pairTree = Pair();
            var pair = RunRemoval(pairTree, Id);
            Check("RemoveDevice through the hooks: the child's removal call, then the parent's, each once, no fallback, removed",
                  pair.Removed && pairTree.Calls.SequenceEqual(new[] { $"remove {ChildId}", $"remove {Id}" }) && pairTree.Fallbacks == 0,
                  $"{string.Join(", ", pairTree.Calls)}, {pairTree.Fallbacks} fallback run(s), {pair.Ms} ms");
            Check("RemoveDevice through the hooks: one REMOVED registration open at a time, the parent's, the child opening none",
                  pairTree.MostOpen == 1 && pairTree.Ids[Id].Registrations == 1 && pairTree.Ids[ChildId].Registrations == 0,
                  $"at most {pairTree.MostOpen} open, parent {pairTree.Ids[Id].Registrations}, child {pairTree.Ids[ChildId].Registrations}");
            var fastTree = Pair();
            var fastPair = RunRemoval(fastTree, Id, fast: true);
            Check("fast RemoveDevice through the hooks: the child uninstalled, then the parent, one REMOVED registration open at a time",
                  fastPair.Removed && fastTree.Calls.SequenceEqual(new[] { $"uninstall {ChildId}", $"uninstall {Id}" }) && fastTree.MostOpen == 1,
                  $"{string.Join(", ", fastTree.Calls)}, at most {fastTree.MostOpen} open, {fastPair.Ms} ms");
        }

        // A HID child removed while its parent runs, through RemoveDevice.
        // The recorded shape of the Windows Sandbox runs of 2026-10-16: the
        // child leaves the tree and its record goes at its call, its own
        // REMOVED does not come, and the parent's REMOVED comes 12 ms after
        // the parent's call. Before, the child's test waited its own 2 s
        // bound first.
        const string Kid2 = @"HID\PNP_NOTIFY_CHECK_OFFLINE\1&7e57&0&0001";
        const string SwdId = @"SWD\HIDMAESTRO\PNP_NOTIFY_CHECK_OFFLINE";
        Sequence KidGone() => new() { LeavesTree = 0, RecordGoneAt = 0, RemovedAt = null };
        Sequence ParentAt(long at) => new() { LeavesTree = at, RemovedAt = at, RecordGoneAt = at };
        Sequence Stays() => new() { LeavesTree = null, RemovedAt = null, RecordGoneAt = null };
        Tree Shape(Sequence child, Sequence? parent = null, string parentId = Id, Sequence? child2 = null)
        {
            var t = new Tree();
            t.Ids[parentId] = parent ?? ParentAt(12);
            t.Ids[ChildId] = child;
            if (child2 != null) t.Ids[Kid2] = child2;
            t.ChildrenOf[parentId] = child2 == null ? new[] { ChildId } : new[] { ChildId, Kid2 };
            return t;
        }
        const string OnParent = "left with its parent's subtree, on the parent's REMOVED notification";
        bool Says(Tree t, string id, string text) => t.LinesOf(id).SequenceEqual(new[] { $"{id} {text}" });
        // A software parent's survivor held once gone: its one line, the
        // text, then how long it was held.
        long HeldMs(Tree t, string id, string text)
        {
            var lines = t.LinesOf(id).ToList();
            var m = lines.Count == 1 ? System.Text.RegularExpressions.Regex.Match(lines[0],
                        "^" + System.Text.RegularExpressions.Regex.Escape($"{id} {text}") + @", held (\d+) ms under the lock after it left$") : null;
            return m is { Success: true } ? long.Parse(m.Groups[1].Value) : -1;
        }
        string LinesText(Tree t) => string.Join(" | ", t.Lines.Select(l => l.Trim()));

        if (Part(2))
        {

            var sh1 = Shape(KidGone());
            var r1 = RunRemoval(sh1, Id);
            Check("subtree: the child gone at its call with no REMOVED, the parent's REMOVED at +12 -> RemoveDevice returns at about 12 ms, the child on the parent's notification",
                  r1.Removed && Within(r1.Ms, 12) && r1.Ms < RemovedBoundMs / 4 && r1.On == 1 && r1.WithParent == 1 && r1.Without == 0 && r1.NotGone == 0
                  && Says(sh1, ChildId, OnParent),
                  $"{r1.Ms} ms, {r1.On} on the notification, {r1.WithParent} on the parent's, {r1.Without} without, {r1.NotGone} not gone, {LinesText(sh1)}");
            Check("subtree: one REMOVED registration open at a time, the parent's, opened before the child's call: the child opens none",
                  sh1.MostOpen == 1 && sh1.Open == 0 && sh1.Ids[Id].Registrations == 1 && sh1.Ids[ChildId].Registrations == 0,
                  $"at most {sh1.MostOpen} open, {sh1.Open} left, parent {sh1.Ids[Id].Registrations}, child {sh1.Ids[ChildId].Registrations}");

            // The guarantee the 0x139 investigation needs, on the child's own
            // rule: the parent's notification does not end a child whose record
            // an uninstall should delete. RemoveDevice, and the PnP lock it
            // holds, waits for it.
            var r2 = RunRemoval(Shape(new Sequence { LeavesTree = 0, RecordGoneAt = 300, RemovedAt = null }), Id);
            Check("subtree: the child's record gone at 300, the parent's REMOVED at +12 -> RemoveDevice returns at 300, not before, the child on the parent's notification",
                  Within(r2.Ms, 300) && r2.WithParent == 1 && r2.Without == 0, $"{r2.Ms} ms, {r2.WithParent} on the parent's, {r2.Without} without");

            // A child the locates still find after the parent's REMOVED keeps its
            // 2 s budget and ends not gone, and its line says where it is.
            var sh3 = Shape(Stays());
            var r3 = RunRemoval(sh3, Id);
            Check("subtree: the child still in the tree after the parent's REMOVED -> not gone when its 2 s budget ends, its line saying so",
                  r3.NotGone == 1 && r3.WithParent == 0 && Within(r3.Ms, 12 + DeviceManager.QuickRemovalMs)
                  && Says(sh3, ChildId, "not gone after its parent's removal: still in the tree"),
                  $"{r3.Ms} ms, {r3.NotGone} not gone, {LinesText(sh3)}");
            var sh3b = Shape(new Sequence { LeavesTree = 0, RecordGoneAt = null, RemovedAt = null });
            var r3b = RunRemoval(sh3b, Id);
            Check("subtree: the child out of the tree with its record left after the parent's REMOVED -> not gone, its line saying so",
                  r3b.NotGone == 1 && Says(sh3b, ChildId, "not gone after its parent's removal: out of the tree, its record left"),
                  $"{r3b.Ms} ms, {r3b.NotGone} not gone, {LinesText(sh3b)}");

            // No proof from the parent: its own bound runs out, and the child is
            // gone without a notification at once, with no second bound.
            var sh4 = Shape(KidGone(), new Sequence { LeavesTree = 12, RecordGoneAt = 12, RemovedAt = null });
            var r4 = RunRemoval(sh4, Id);
            Check("subtree: the parent's REMOVED never comes -> both gone without one when the parent's 2 s bound ends, at about 2,012 ms, not 4 s",
                  Within(r4.Ms, 12 + RemovedBoundMs) && r4.Without == 2 && r4.WithParent == 0
                  && Says(sh4, ChildId, "left the tree, with no REMOVED notification for its parent"),
                  $"{r4.Ms} ms, {r4.Without} without, {r4.WithParent} on the parent's, {LinesText(sh4)}");

            // A parent that does not leave proves nothing, and its children's
            // lines say so.
            var sh4b = Shape(KidGone(), Stays());
            var r4b = RunRemoval(sh4b, Id, timeoutMs: 300);
            Check("subtree: the parent not gone after its 300 ms and the fallbacks' test -> the child gone without a notification and held for nothing, RemoveDevice returning within 300 ms and the fallbacks' 2 s, its line naming the parent not gone",
                  !r4b.Removed && r4b.Without == 1 && r4b.WithParent == 0 && Says(sh4b, ChildId, "left the tree, its parent not gone")
                  && r4b.Ms < 300 + DeviceManager.QuickRemovalMs + RemovedBoundMs / 4,
                  $"{r4b.Ms} ms, removed {r4b.Removed}, {r4b.Without} without, {LinesText(sh4b)}");

            // The child's own notification is not watched: it has no
            // registration while the parent's is open.
            var sh5 = Shape(new Sequence { LeavesTree = 0, RecordGoneAt = 0, RemovedAt = 5 });
            sh5.CallMs[ChildId] = 40;
            var r5 = RunRemoval(sh5, Id);
            Check("subtree: the child's own REMOVED at 5, during its 40 ms call -> not watched, the child on the parent's notification",
                  r5.WithParent == 1 && r5.On == 1 && sh5.MostOpen == 1 && sh5.Ids[ChildId].Registrations == 0,
                  $"{r5.Ms} ms, {r5.On} on the notification, {r5.WithParent} on the parent's, at most {sh5.MostOpen} open");

            // The parent leaves from outside after the first child's call and
            // before its own: its watch, open since before the first child's
            // call, sees it, and both children are proven by it.
            var sh6 = Shape(KidGone(), new Sequence { LeavesTree = 30, RemovedAt = 30, RecordGoneAt = 30 }, child2: KidGone());
            sh6.BeginsWith[ChildId] = new[] { Id };
            sh6.CallMs[Kid2] = 60;
            var r6 = RunRemoval(sh6, Id);
            Check("subtree: the parent leaving from outside at 30 ms, after the first child's call and before its own -> both children on its REMOVED, no proof lost",
                  r6.WithParent == 2 && r6.Without == 0 && Says(sh6, ChildId, OnParent) && Says(sh6, Kid2, OnParent) && sh6.MostOpen == 1,
                  $"{r6.Ms} ms, {r6.WithParent} on the parent's, {r6.Without} without, at most {sh6.MostOpen} open, {LinesText(sh6)}");

            // A child out of the tree at its call: its record decides.
            var sh7 = Shape(new Sequence { OutOfTreeBefore = true, RecordGoneAt = 0 });
            var r7 = RunRemoval(sh7, Id);
            Check("subtree: a child out of the tree at its call -> its record decides, and its line says so",
                  r7.Removed && Says(sh7, ChildId, "was out of the tree at its removal call, its record decided: Removed"),
                  $"{r7.Ms} ms, {LinesText(sh7)}");

            // The fast path takes the same order.
            var sh8 = Shape(KidGone());
            var r8 = RunRemoval(sh8, Id, fast: true);
            Check("subtree, fast: the child uninstalled, then the parent, the child on the parent's notification, one registration open at a time",
                  r8.WithParent == 1 && r8.Without == 0 && sh8.MostOpen == 1 && sh8.Calls.SequenceEqual(new[] { $"uninstall {ChildId}", $"uninstall {Id}" }),
                  $"{string.Join(", ", sh8.Calls)}, {r8.Ms} ms, {r8.WithParent} on the parent's, at most {sh8.MostOpen} open");
        }

        // Software parents. The close's watch stays open through the
        // children, normal or fast, so a survivor opens no registration of
        // its own while the close's cascade may still be removing it. A
        // close that removed the parent proves nothing for children called
        // after it. A survivor in the tree at its call and not proven is
        // held under the lock for the REMOVED bound once gone, with no
        // registration, as its own test held it for its own notification
        // before: the parent's notification on the one registration open
        // ends the hold. One registration open at a time on every path, none
        // left open.
        Sequence KidSeen() => new() { LeavesTree = 0, RecordGoneAt = 0, RemovedAt = 20 };
        const string BeforeCall = "left the tree, but its parent's REMOVED notification came before its removal call";
        bool HeldBound(Tree t, string id, string text) => HeldMs(t, id, text) >= RemovedBoundMs - TimerSlack;
        if (Part(3))
        {
            var sw1 = Shape(KidSeen(), ParentAt(20), SwdId, KidSeen());
            var q1 = RunRemoval(sw1, SwdId);
            var sw2 = Shape(KidGone(), Stays(), SwdId, KidGone());
            var q2 = RunRemoval(sw2, SwdId, timeoutMs: 300);
            var sw3 = Shape(KidSeen(), ParentAt(20), SwdId, KidSeen());
            var q3 = RunRemoval(sw3, SwdId, fast: true);
            var sw4 = Shape(KidGone(), ParentAt(500), SwdId, KidGone());
            var q4 = RunRemoval(sw4, SwdId, fast: true, timeoutMs: 300);
            var swTrees = new[] { sw1, sw2, sw3, sw4 };
            Check("software parents: closed at 20 ms, normal and fast, and not gone at 300 ms, normal and fast -> one registration open at a time, the close's, the children opening none, none left open",
                  swTrees.All(t => t.MostOpen == 1 && t.Open == 0 && t.Ids[ChildId].Registrations == 0 && t.Ids[Kid2].Registrations == 0),
                  string.Join(" | ", swTrees.Select(t => $"most {t.MostOpen}, left {t.Open}, children {t.Ids[ChildId].Registrations} and {t.Ids[Kid2].Registrations}")));
            Check("software parent closed at 20 ms, normal and fast, two survivors in the tree at their calls -> each held under the lock for the REMOVED bound once gone, with no registration, then gone without one, about 4 s, each line naming the hold",
                  q1.Without == 2 && q3.Without == 2 && Within(q1.Ms, 20 + 2 * RemovedBoundMs) && Within(q3.Ms, 20 + 2 * RemovedBoundMs)
                  && HeldBound(sw1, ChildId, BeforeCall) && HeldBound(sw1, Kid2, BeforeCall)
                  && HeldBound(sw3, ChildId, BeforeCall) && HeldBound(sw3, Kid2, BeforeCall),
                  $"{q1.Ms} ms and {q3.Ms} ms, {q1.Without} and {q3.Without} without, {LinesText(sw1)}");
            Check("software parent, normal, close not gone at 300 ms -> each survivor held its 2 s for the parent's REMOVED, as each child's own test held it before, then gone without one",
                  Within(q2.Ms, 300 + 2 * RemovedBoundMs) && q2.Without == 2 && q2.WithParent == 0
                  && sw2.Calls.SequenceEqual(new[] { $"close {SwdId}", $"remove {ChildId}", $"remove {Kid2}" })
                  && HeldBound(sw2, ChildId, "left the tree, its parent not gone"),
                  $"{string.Join(", ", sw2.Calls)}, {q2.Ms} ms, {q2.Without} without, {q2.WithParent} on the parent's, {LinesText(sw2)}");
            Check("software parent, fast, close not gone at 300 ms, gone at 500 ms after its uninstall -> both children on its REMOVED",
                  q4.WithParent == 2 && q4.Without == 0 && Says(sw4, ChildId, OnParent) && Says(sw4, Kid2, OnParent),
                  $"{q4.Ms} ms, {q4.WithParent} on the parent's, {LinesText(sw4)}");
        }
        // The same survivors with their own REMOVED 1,500 ms after their
        // calls: not heard, so the hold is the bound whenever it comes.
        Sequence KidLate() => new() { LeavesTree = 0, RecordGoneAt = 0, RemovedAt = 1500 };
        if (Part(4))
        {
            var sw1b = Shape(KidLate(), ParentAt(20), SwdId, KidLate());
            var q1b = RunRemoval(sw1b, SwdId);
            Check("software parent closed at 20 ms, survivors whose own REMOVED comes 1,500 ms after their calls -> each held the REMOVED bound, not its own notification, which no registration hears",
                  q1b.Without == 2 && Within(q1b.Ms, 20 + 2 * RemovedBoundMs) && sw1b.Ids[ChildId].Registrations == 0 && sw1b.MostOpen == 1
                  && HeldBound(sw1b, Kid2, BeforeCall),
                  $"{q1b.Ms} ms, {q1b.Without} without, child registrations {sw1b.Ids[ChildId].Registrations}, at most {sw1b.MostOpen} open, {LinesText(sw1b)}");
            // The sandbox's shape (S77 at e79fac3, 2026-10-17): the close's cascade
            // took the child out of the tree before its call and left its record.
            // Its record decides, and nothing is held.
            var swOut = Shape(new Sequence { OutOfTreeBefore = true, RecordGoneAt = 0 }, ParentAt(20), SwdId);
            var qOut = RunRemoval(swOut, SwdId);
            Check("software parent closed at 20 ms, its child out of the tree at its call with its record left -> its record decides at once, nothing held",
                  qOut.Ms < RemovedBoundMs / 4 && qOut.Without == 0 && Says(swOut, ChildId, "was out of the tree at its removal call, its record decided: Removed"),
                  $"{qOut.Ms} ms, {qOut.Without} without, {LinesText(swOut)}");
            var sw2b = Shape(KidGone(), new Sequence { LeavesTree = 1000, RemovedAt = 1000, RecordGoneAt = null }, SwdId, KidGone());
            var q2b = RunRemoval(sw2b, SwdId, timeoutMs: 300);
            Check("software parent, normal, close not gone at 300 ms, its cascade done at 1,000 ms -> both survivors on its REMOVED, at about 1,000 ms",
                  q2b.WithParent == 2 && q2b.Without == 0 && Within(q2b.Ms, 1000) && Says(sw2b, Kid2, OnParent),
                  $"{q2b.Ms} ms, {q2b.WithParent} on the parent's, {q2b.Without} without, {LinesText(sw2b)}");
        }

        bool OnLatest(Tree t, string id) =>
            Says(t, id, OnParent) || HeldMs(t, id, OnParent) is >= 0 and < RemovedBoundMs - TimerSlack;
        // A removal call that throws closes the child's watch and its
        // registration, deferred or tested at its call.
        int ThrowsAndLeaves(Tree t, string parentId)
        {
            try { RunRemoval(t, parentId); }
            catch (InvalidOperationException) { }
            return t.Open;
        }
        if (Part(5))
        {
            // A software parent's REMOVED during its fast close, which then
            // stays until its uninstall: that notification came before the
            // children's calls and proves nothing for them. When the parent then
            // leaves with a second REMOVED, the latest one proves them.
            var sw5 = Shape(KidGone(), new Sequence { LeavesTree = 600, RecordGoneAt = 600, RemovedAt = 100 }, SwdId, KidGone());
            var q5 = RunRemoval(sw5, SwdId, fast: true, timeoutMs: 300);
            Check("software parent, fast: its REMOVED at 100 ms during the close, gone at 600 -> the children held the bound each, then gone without one, their lines naming a REMOVED before their call",
                  q5.WithParent == 0 && q5.Without == 2 && Within(q5.Ms, 600 + 2 * RemovedBoundMs)
                  && HeldBound(sw5, ChildId, BeforeCall),
                  $"{q5.Ms} ms, {q5.WithParent} on the parent's, {q5.Without} without, {LinesText(sw5)}");
            // The parent leaves the tree and sends its second REMOVED at the same
            // 600 ms, so either can reach the SDK first. When the parent's test
            // sees it gone first, it ends on the REMOVED at 100, and the first
            // child's hold then ends on the second one as it arrives. Either way
            // the latest REMOVED proves both children, and neither is held to
            // the bound.
            var sw6 = Shape(KidGone(), new Sequence { LeavesTree = 600, RecordGoneAt = 600, RemovedAt = 100, RemovedAgainAt = 600 }, SwdId, KidGone());
            var q6 = RunRemoval(sw6, SwdId, fast: true, timeoutMs: 300);
            Check("software parent, fast: REMOVED at 100 ms during the close, and again at 600 as it leaves -> both children on the latest, whichever of the two the SDK sees first",
                  q6.WithParent == 2 && q6.Without == 0 && OnLatest(sw6, ChildId) && OnLatest(sw6, Kid2) && Within(q6.Ms, 600),
                  $"{q6.Ms} ms, {q6.WithParent} on the parent's, {q6.Without} without, {LinesText(sw6)}");

            // A close whose registration Windows refused leaves no watch to
            // keep: each survivor registers its own at its call while the
            // cascade may still run, one at a time, as before the subtree test.
            var swRf = Shape(KidSeen(), new Sequence { LeavesTree = 20, RemovedAt = 20, RecordGoneAt = null, RefuseRegistration = true }, SwdId, KidSeen());
            var qRf = RunRemoval(swRf, SwdId);
            Check("software parent whose close's registration Windows refuses -> each survivor on its own registration at its call, one open at a time, none left open",
                  swRf.Ids[ChildId].Registrations == 1 && swRf.Ids[Kid2].Registrations == 1 && swRf.MostOpen == 1 && swRf.Open == 0
                  && qRf.WithParent == 0 && qRf.On == 2,
                  $"children {swRf.Ids[ChildId].Registrations} and {swRf.Ids[Kid2].Registrations}, at most {swRf.MostOpen} open, {swRf.Open} left, {qRf.On} on their own, {qRf.WithParent} on the parent's, {qRf.Ms} ms");

            // A parent whose registration Windows refused has no watch to prove
            // anything: its children are removed directly, each on its own.
            var rf = Shape(KidSeen(), new Sequence { LeavesTree = 12, RemovedAt = 12, RecordGoneAt = 12, RefuseRegistration = true }, child2: KidSeen());
            var qrf = RunRemoval(rf, Id);
            Check("a parent whose registration is refused -> its children tested at their call, each on its own registration, one open at a time",
                  rf.Ids[ChildId].Registrations == 1 && rf.Ids[Kid2].Registrations == 1 && rf.MostOpen == 1 && qrf.On == 2 && qrf.WithParent == 0,
                  $"children {rf.Ids[ChildId].Registrations} and {rf.Ids[Kid2].Registrations}, at most {rf.MostOpen} open, {qrf.On} on their own, {qrf.WithParent} on the parent's");
            var th1 = Shape(KidGone());
            th1.Throws.Add(ChildId);
            var th2 = Shape(KidGone(), new Sequence { LeavesTree = 12, RemovedAt = 12, RecordGoneAt = 12, RefuseRegistration = true });
            th2.Throws.Add(ChildId);
            int left1 = ThrowsAndLeaves(th1, Id), left2 = ThrowsAndLeaves(th2, Id);
            Check("a child's removal call that throws, with the parent's watch open and with none (its registration refused) -> no registration left open",
                  left1 == 0 && left2 == 0 && th2.Ids[ChildId].Registrations == 1,
                  $"left {left1} and {left2}, the direct child registered {th2.Ids[ChildId].Registrations} time(s)");
        }

        // S77's timing of a child's own REMOVED reads each instance event's
        // id from CM_NOTIFY_EVENT_DATA: FilterType and Reserved, then the id
        // at offset 8, read no further than the event's size.
        string eventId = @"HID\HIDCLASS\1&74102611&0&0000";
        const string TimedParent = @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_TIMED", TimedChild = @"HID\HIDCLASS\1&7173&0&0000",
                     TimedSibling = @"HID\HIDCLASS\1&7173&0&0001", Stranger = @"HID\VID_054C&PID_09CC\7&2B1C&0&0000";
        IntPtr held = (IntPtr)0x11, sdkWatch = (IntPtr)0x22;
        if (Part(6))
        {
            IntPtr eventData = System.Runtime.InteropServices.Marshal.AllocHGlobal(8 + (eventId.Length + 1) * 2);
            try
            {
                System.Runtime.InteropServices.Marshal.WriteInt32(eventData, 0, 2);
                System.Runtime.InteropServices.Marshal.WriteInt32(eventData, 4, 0);
                for (int i = 0; i < eventId.Length; i++)
                    System.Runtime.InteropServices.Marshal.WriteInt16(eventData, 8 + i * 2, eventId[i]);
                System.Runtime.InteropServices.Marshal.WriteInt16(eventData, 8 + eventId.Length * 2, 0);
                string whole = DeviceManager.InstanceIdOf(eventData, (uint)(8 + (eventId.Length + 1) * 2));
                string cut = DeviceManager.InstanceIdOf(eventData, (uint)(8 + 8));
                string none = DeviceManager.InstanceIdOf(eventData, 8);
                Check("an instance event's id is read at offset 8, up to its terminator and no further than its size",
                      whole == eventId && cut == eventId.Substring(0, 4) && none == "", $"{whole} | {cut} | '{none}'");
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(eventData); }

            // An event tap that throws costs the SDK's own handler nothing: the
            // tap runs in a try of its own (DeviceManager.Dispatch).
            {
                int handled = 0;
                DeviceManager.CM_NOTIFY_CALLBACK handler = (h, c, a, d, size) => { Interlocked.Increment(ref handled); return 0u; };
                var keep = System.Runtime.InteropServices.GCHandle.Alloc(handler);
                IntPtr data = System.Runtime.InteropServices.Marshal.AllocHGlobal(8 + (eventId.Length + 1) * 2);
                try
                {
                    System.Runtime.InteropServices.Marshal.WriteInt32(data, 0, 2);
                    System.Runtime.InteropServices.Marshal.WriteInt32(data, 4, 0);
                    for (int i = 0; i < eventId.Length; i++)
                        System.Runtime.InteropServices.Marshal.WriteInt16(data, 8 + i * 2, eventId[i]);
                    System.Runtime.InteropServices.Marshal.WriteInt16(data, 8 + eventId.Length * 2, 0);
                    DeviceManager.InstanceEventTap = (r, a, id) => throw new InvalidOperationException("a tap that throws");
                    uint answer = DeviceManager.Dispatch((IntPtr)0x11, System.Runtime.InteropServices.GCHandle.ToIntPtr(keep), 9, data,
                                                         (uint)(8 + (eventId.Length + 1) * 2));
                    Check("an instance event tap that throws -> the SDK's handler still runs, and nothing escapes",
                          handled == 1 && answer == 0, $"handler ran {handled} time(s), answered {answer}");
                }
                finally
                {
                    DeviceManager.InstanceEventTap = null;
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(data);
                    keep.Free();
                }
            }

            // S77's live judgment of that timing, on made-up events. The window's
            // registration made at -200 ms and closed at 9,800, the create at 0,
            // the child enumerated at 10, a second one at 12 and another HID
            // device at 40, the dispose from 90, the child's own REMOVED at 98
            // and the parent's at 100 through the window's registration, the
            // parent's at 100.2 through the SDK's own watch, the PnP lock let go
            // at 140.
            (bool Ok, string Detail) Timed(double? childRemoved = 98, double? earlierRemoved = null, bool childThroughWindow = true,
                                           bool parentThroughWindow = true, bool childEnumerated = true, string[]? children = null,
                                           double? siblingRemoved = null, bool registered = true, bool refused = false,
                                           double openedAt = -200, double closedAt = 9800, double lockReleasedAt = 140,
                                           double? parentAfterClose = null)
            {
                var e = new List<(double Ms, IntPtr Registration, uint Action, string Id)>();
                if (childEnumerated) e.Add((10, held, 7, TimedChild));
                e.Add((12, held, 7, TimedSibling));
                e.Add((40, held, 7, Stranger));
                if (earlierRemoved is double before) e.Add((before, held, 9, TimedChild));
                if (childRemoved is double at) e.Add((at, childThroughWindow ? held : sdkWatch, 9, TimedChild));
                if (siblingRemoved is double sib) e.Add((sib, held, 9, TimedSibling));
                e.Add((100, parentThroughWindow ? held : sdkWatch, 9, TimedParent));
                e.Add((100.2, sdkWatch, 9, TimedParent));
                if (parentAfterClose is double later) e.Add((later, held, 9, TimedParent));
                Program.HeldRegistration? window = registered ? new Program.HeldRegistration(refused ? IntPtr.Zero : held, openedAt, closedAt) : null;
                return Program.ChildRemovedInTime(e.ToArray(), window, TimedParent, children ?? new[] { TimedChild },
                                                  createAt: 0, disposeAt: 90, lockReleasedAt: lockReleasedAt);
            }
            string Show(string name, (bool Ok, string Detail) r) => $"{name} {(r.Ok ? "passed" : "failed")}: {r.Detail}";

            var inTime = Timed();
            var twoInTime = Timed(children: new[] { TimedChild, TimedSibling }, siblingRemoved: 99);
            Check("S77's child timing: passes on each child's own REMOVED through the held-open registration before the PnP lock was let go, another HID device enumerated in the window counting for nothing",
                  inTime.Ok && twoInTime.Ok
                  && inTime.Detail.Contains(@"HID\HIDCLASS\1&7173&0&0000: its own REMOVED 8.0 ms into the dispose, 42.0 ms before the PnP lock was let go, -2.0 ms from the parent's"),
                  Show("one child", inTime) + " || " + Show("two", twoInTime));

            // Judged failures: the inference shown false.
            var judged = new (string Name, (bool Ok, string Detail) R, string Says)[]
            {
                ("after the lock, before the dispose returned", Timed(childRemoved: 145), "55.0 ms into the dispose, 5.0 ms after the PnP lock was let go"),
                ("never", Timed(childRemoved: null), "no REMOVED of its own through the held-open registration in the 9660.0 ms it stayed open after the PnP lock was let go"),
                ("only before the dispose", Timed(childRemoved: null, earlierRemoved: 50), "after the PnP lock was let go, only one 40.0 ms before the dispose began"),
                ("only through another registration", Timed(childThroughWindow: false), "no REMOVED of its own through the held-open registration"),
                ("a second child late", Timed(children: new[] { TimedChild, TimedSibling }, siblingRemoved: 400), @"HID\HIDCLASS\1&7173&0&0001: its own REMOVED 310.0 ms into the dispose, 260.0 ms after the PnP lock was let go"),
            };
            var judgedWrong = judged.Where(j => j.R.Ok || !j.R.Detail.Contains(j.Says) || j.R.Detail.Contains("not judged")).ToList();
            Check("S77's child timing: fails, judged, on a child's own REMOVED after the PnP lock was let go though before the dispose returned, on none, on one only from before the dispose, on one only through another registration, and on a second child's late one",
                  judgedWrong.Count == 0,
                  string.Join(" || ", (judgedWrong.Count > 0 ? judgedWrong : judged.Take(1).ToList()).Select(j => Show(j.Name, j.R))));

            // Failures without the window's own controls: not judged, each cause
            // named, and no word of a missing REMOVED it could not have seen.
            var unjudged = new (string Name, (bool Ok, string Detail) R, string Cause)[]
            {
                ("refused", Timed(refused: true, childThroughWindow: false), "Windows refused the held-open registration"),
                ("never made", Timed(registered: false), "the held-open wait made no registration"),
                ("made late", Timed(openedAt: 20), "the held-open registration was made 20.0 ms after the create began"),
                ("made late, the child's only ENUMERATED through its handle before it", Timed(openedAt: 20), @"no ENUMERATED for HID\HIDCLASS\1&7173&0&0000 through the held-open registration"),
                ("the parent's REMOVED through its handle only after it closed", Timed(parentThroughWindow: false, parentAfterClose: 9900), "no REMOVED for the parent through the held-open registration"),
                ("closed early", Timed(childRemoved: 130, closedAt: 120), "the held-open registration closed 20.0 ms before the PnP lock was let go"),
                ("no parent REMOVED through it", Timed(childRemoved: null, parentThroughWindow: false), "no REMOVED for the parent through the held-open registration"),
                ("no child ENUMERATED through it", Timed(childEnumerated: false), @"no ENUMERATED for HID\HIDCLASS\1&7173&0&0000 through the held-open registration"),
                ("no lock line", Timed(lockReleasedAt: double.NaN), "no diag line that the parent's RemoveDevice returned"),
                ("no child", Timed(children: Array.Empty<string>()), "the parent had no child before the dispose"),
            };
            var unjudgedWrong = unjudged.Where(u => u.R.Ok || !u.R.Detail.StartsWith("not judged: ") || !u.R.Detail.Contains(u.Cause)
                                                    || u.R.Detail.Contains("no REMOVED of its own")).ToList();
            Check("S77's child timing: fails as not judged, naming the cause, when the held-open registration was refused, never made, made after the create began or closed before the lock was let go, or carried no REMOVED for the parent or ENUMERATED for the child, or the lock's release or the child is missing",
                  unjudgedWrong.Count == 0,
                  string.Join(" || ", (unjudgedWrong.Count > 0 ? unjudgedWrong : unjudged.Take(1).ToList()).Select(u => Show(u.Name, u.R))));

            // S77's two lookups. The window's registration is the one made on
            // the held-open wait's own thread while it runs: a registration made
            // on another thread first, and a second one on the wait's thread,
            // count for nothing.
            using (var timing = new Program.ChildTiming())
            {
                using var go = new ManualResetEventSlim(false);
                var hold = timing.Hold(() =>
                {
                    go.Wait();
                    DeviceManager.AllInstancesRegistrationTap!((IntPtr)0x11);
                    DeviceManager.AllInstancesRegistrationTap!((IntPtr)0x33);
                    return false;
                });
                DeviceManager.AllInstancesRegistrationTap!((IntPtr)0x22);
                go.Set();
                hold.Wait();
                var w = timing.Window;
                Check("S77's window: the registration made on the held-open wait's own thread, first, not another thread's made before it",
                      w is { } got && got.Handle == (IntPtr)0x11 && got.OpenedAt <= got.ClosedAt,
                      w is { } shown ? $"handle 0x{shown.Handle.ToInt64():X}, made at {shown.OpenedAt:0.0} ms, closed at {shown.ClosedAt:0.0} ms" : "no registration");
            }
            // The lock's release is the line the orchestrator writes once the
            // parent's RemoveDevice returned, in its own format, not a line
            // naming the parent written under the lock, another parent's, or a
            // child's.
            using (var timing = new Program.ChildTiming())
            {
                const string LockParent = @"SWD\HIDMAESTRO_VID_045E_PID_0B13&IG_00\HM_7D367031F8E76B1E",
                             LockChild = @"HID\HIDMAESTRO_VID_045E_PID_0B13&IG_00\1&7D367031&0&0000";
                timing.DisposeStarting(LockParent, new[] { LockChild });
                DeviceOrchestrator.DiagTap!($"      SwdDeviceFactory.Remove({LockParent}) hr=0x00000000 confirmed_offline=True after 104ms");
                DeviceOrchestrator.DiagTap!($"        WaitForDeviceRemoval({LockParent}, 120000ms): Removed after 13ms");
                DeviceOrchestrator.DiagTap!($"      {LockChild} was out of the tree at its removal call, its record decided: Removed");
                DeviceOrchestrator.DiagTap!(@"    parent RemoveDevice(ROOT\HIDClass\HM_0000) returned True after 11ms");
                double underLock = timing.Now;
                Thread.Sleep(5);
                double before = timing.Now;
                DeviceOrchestrator.DiagTap!($"    parent RemoveDevice({LockParent}) returned False after 119ms");
                double after = timing.Now;
                Thread.Sleep(5);
                DeviceOrchestrator.DiagTap!($"    child RemoveDevice({LockChild}) returned True after 0ms");
                DeviceOrchestrator.DiagTap!($"    parent RemoveDevice({LockParent}) returned True after 3ms");
                double at = timing.LockReleasedAt;
                Check("S77's lock release: the line that the parent's RemoveDevice returned, the first one, not a line naming the parent written under the lock, another parent's or a child's",
                      at >= before && at <= after,
                      $"taken at {at:0.000} ms, the line written between {before:0.000} and {after:0.000} ms, the lines under the lock by {underLock:0.000} ms");
            }

            // The replayed cost of the recorded shape, ten runs, from the call
            // of RemoveDevice to its return. A replay of made-up times, no
            // device: it shows the child's 2 s bound gone from this path.
            var costs = new List<long>();
            for (int i = 0; i < 10; i++)
                costs.Add(RunRemoval(Shape(KidGone()), Id).Ms);
            costs.Sort();
            long p50 = costs[costs.Count / 2], p95 = costs[(int)Math.Ceiling(costs.Count * 0.95) - 1];
            Check("subtree, replayed cost of the recorded shape, 10 runs: p95 under 200 ms",
                  p95 < 200, $"p50 {p50} ms, p95 {p95} ms, max {costs[^1]} ms");

            // WaitRemovalComplete, the sweeps' and the exit's one test, keeps its
            // budget whole, the notification's bound inside it, and with 0 looks
            // once.
            var w8 = Complete(new Sequence { LeavesTree = 100, RecordGoneAt = 100 }, uninstalls: true, 0);
            Check("WaitRemovalComplete with a budget of 0 on a devnode still in the tree -> not finished, at once",
                  !w8.Done && w8.Ms < 50, $"{w8.Done} at {w8.Ms} ms");
            var w9 = Complete(new Sequence { LeavesTree = 50, RecordGoneAt = 100, RemovedAt = null }, uninstalls: true, 400);
            Check("WaitRemovalComplete, out at 50, record gone at 100, REMOVED never, budget 400 -> finished when the budget ends, not past it",
                  w9.Done && Within(w9.Ms, 400) && w9.Ms < 400 + 100, $"{w9.Done} at {w9.Ms} ms");
            var w10 = Complete(new Sequence { LeavesTree = 0, RecordGoneAt = null, RemovedAt = 50 }, uninstalls: true, 300);
            Check("WaitRemovalComplete on an uninstall whose record stays, budget 300 -> not finished at 300",
                  !w10.Done && Within(w10.Ms, 300), $"{w10.Done} at {w10.Ms} ms");
            var w11 = Complete(new Sequence { LeavesTree = 50, RecordGoneAt = null, RemovedAt = 150 }, uninstalls: false, 5000);
            Check("WaitRemovalComplete on a software device that keeps its record: out at 50, REMOVED at 150 -> finished at 150",
                  w11.Done && Within(w11.Ms, 150), $"{w11.Done} at {w11.Ms} ms");
            Check("an SWD id keeps its record, a ROOT or HID id does not",
                  DeviceManager.KeepsRecord(@"SWD\HIDMAESTRO\HM_0000") && !DeviceManager.KeepsRecord(@"ROOT\HIDCLASS\HM_0000")
                  && !DeviceManager.KeepsRecord(@"HID\VID_054C&PID_0CE6\1&2&0&0000"));

            // The sweeps' and the exit's waits on what they removed, AllGone and
            // the orphaned HID children's wait, are the removal test. A NORMAL
            // locate alone passes a devnode out of the tree whose removal is
            // still going.
            var ag1 = Swept(new Sequence { OutOfTreeBefore = true, RecordGoneAt = 300 }, @"HID\PNP_NOTIFY_CHECK\1&7e57&0&0000", orphans: true);
            Check("orphan wait: a HID child out of the tree whose record goes at 300 -> gone at 300, not before",
                  ag1.Gone && Within(ag1.Ms, 300), $"{ag1.Gone} at {ag1.Ms} ms");
            var ag2 = Swept(new Sequence { LeavesTree = 100, RemovedAt = 150, RecordGoneAt = 150 }, @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK", orphans: false);
            Check("AllGone: a ROOT devnode out of the tree at 100, REMOVED and its record gone at 150 -> gone at 150",
                  ag2.Gone && Within(ag2.Ms, 150), $"{ag2.Gone} at {ag2.Ms} ms");
            var ag3 = Swept(new Sequence { OutOfTreeBefore = true, RecordGoneAt = null }, @"SWD\HIDMAESTRO\PNP_NOTIFY_CHECK", orphans: false);
            Check("AllGone: a software device out of the tree with its record kept -> gone at once",
                  ag3.Gone && ag3.Ms < 50, $"{ag3.Gone} at {ag3.Ms} ms");
        }

        // The registration gate.
        if (Part(7))
        {
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
            // removes or uninstalls a devnode is under the PnP lock, and every
            // call that changes devnodes outside it is a listed exclusion the
            // lock's doc comment names.
            var outside = LockCoverage.Violations(out int sites);
            if (outside == null)
                Console.WriteLine("  info: the SDK source is not beside the probe, so the lock coverage check did not run");
            else
                Check("every call or registry write that changes a devnode is under the PnP lock, and each one outside it is a named exclusion",
                      outside.Count == 0 && sites > 0,
                      outside.Count == 0
                          ? $"{sites} call sites, {LockCoverage.ExclusionCount} exclusions, {LockCoverage.RegistryWriteCount} registry writes listed"
                          : string.Join(" | ", outside));

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

            // An install pending under the lock is no round of the three: what
            // came back twice after it still gets its third take.
            int rc = 0;
            var g14 = Gate(new Sequence { AbsentBefore = true, BusyUnderFirstLock = true },
                           rivals: remove => remove || ++rc == 1 || rc >= 4);
            Check("gate: a pending install, then a rival back twice -> allowed on the fourth take",
                  g14.Gate == DeviceManager.RegistrationGate.Allowed && g14.Held && g14.Takes == 4,
                  $"{g14.Gate}, {g14.Takes} takes");

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
        }

        // A create attempt that timed out can have made the device and closed
        // it: the retry registers the id again only once it has left.
        if (Part(8))
        {
            var n1 = Retries(new Sequence { LeavesTree = 100, RemovedAt = 150, RecordGoneAt = null }, ThreeAttempts, WaitTimeoutHr, 0);
            Check("create retry: the timed-out attempt's device out at 100, REMOVED at 150 -> the retry at 150, and it succeeds",
                  n1.Success && n1.Attempts == 2 && Within(n1.LastAt, 150) && n1.Registrations == 1,
                  $"success {n1.Success}, {n1.Attempts} attempt(s), the last at {n1.LastAt} ms, {n1.Registrations} registration(s)");
            var n2 = Retries(new Sequence { LeavesTree = null, RecordGoneAt = null }, ThreeAttempts, WaitTimeoutHr, 0, 0);
            Check("create retry: the timed-out attempt's device still in the tree when its 400 ms test ends -> no retry, the create refused as still in the tree",
                  !n2.Success && n2.Attempts == 1 && n2.HResult == SwdDeviceFactory.StillInTreeHr && Within(n2.EndAt, RetryRemovalTestMs),
                  $"success {n2.Success}, 0x{n2.HResult:X8}, {n2.Attempts} attempt(s), ended at {n2.EndAt} ms");
            var n3 = Retries(new Sequence { AbsentBefore = true }, ThreeAttempts, WaitTimeoutHr, WaitTimeoutHr, 0);
            Check("create retry: timed-out attempts that made nothing -> retried at once, with no REMOVED registration",
                  n3.Success && n3.Attempts == 3 && n3.LastAt < 500 && n3.Registrations == 0,
                  $"success {n3.Success}, {n3.Attempts} attempt(s), the last at {n3.LastAt} ms, {n3.Registrations} registration(s)");
            var n4 = Retries(new Sequence { AbsentBefore = true }, ThreeAttempts, unchecked((int)0x80004005), 0);
            Check("create retry: an attempt that failed other than by timing out -> no retry",
                  !n4.Success && n4.Attempts == 1, $"success {n4.Success}, {n4.Attempts} attempt(s)");
            // The last attempt's removal is tested too, before the hold ends.
            var n5 = Retries(new Sequence { LeavesTree = 200, RemovedAt = 200, RecordGoneAt = null }, new[] { 0 }, WaitTimeoutHr);
            Check("create retry: the last attempt timed out and its device leaves at 200 -> the create ends at 200, after its removal test",
                  !n5.Success && n5.HResult == WaitTimeoutHr && n5.Attempts == 1 && Within(n5.EndAt, 200) && n5.Registrations == 1,
                  $"success {n5.Success}, 0x{n5.HResult:X8}, ended at {n5.EndAt} ms, {n5.Registrations} registration(s)");

            // The HID orphan sweep decides from the parent before it waits for
            // the lock. A registration of the same identity queued ahead of it
            // brings the child back under the same instance id, so it looks
            // again under the lock.
            const string GoneParent = @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_PARENT";
            var o1 = Orphan(GoneParent, ParentDuringWait.StaysGone);
            Check("orphan sweep: the parent still gone under the lock -> the child removed once, under the lock, with no REMOVED registration",
                  o1.Removed && o1.Removals == 1 && o1.RemovedUnderLock && o1.Registrations == 0,
                  $"removed {o1.Removed}, {o1.Removals} removal(s), under the lock {o1.RemovedUnderLock}, {o1.Registrations} registration(s)");
            var o2 = Orphan(GoneParent, ParentDuringWait.Registered);
            Check("orphan sweep: the parent registered while the sweep waited for the lock -> the child left",
                  !o2.Removed && o2.Removals == 0, $"removed {o2.Removed}, {o2.Removals} removal(s)");
            var o3 = Orphan(GoneParent, ParentDuringWait.Created);
            Check("orphan sweep: the parent and the child back in the tree while the sweep waited -> the child left",
                  !o3.Removed && o3.Removals == 0, $"removed {o3.Removed}, {o3.Removals} removal(s)");
            var o4 = Orphan(GoneParent, ParentDuringWait.StaysGone, presentFirst: true);
            Check("orphan sweep: a parent present at the first look -> the child left without taking the lock",
                  !o4.Removed && o4.Removals == 0 && o4.LockTakes == 0, $"removed {o4.Removed}, {o4.LockTakes} take(s) of the lock");
            // The identity claims, through a stand-in for the one-instance pipes,
            // which only an elevated process can create: a name is refused while
            // any handle to it is open, the holder's own process included, as a
            // pipe made with FILE_FLAG_FIRST_PIPE_INSTANCE refuses it.
            var pipes = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
            MachineClaims.CreateClaimHook = name =>
            {
                lock (pipes)
                {
                    if (pipes.TryGetValue(name, out var open) && !open.IsClosed) return null;
                    var made = new SafeFileHandle(new IntPtr(1), ownsHandle: false);
                    pipes[name] = made;
                    return made;
                }
            };
            try
            {
                const string Token = "HM_PNPNOTIFYCHECK";
                string claimName = MachineClaims.IdentityPipeName(Token);
                // Another program's create in flight holds the claim: not through
                // this process's TryClaim.
                var foreign = new SafeFileHandle(new IntPtr(1), ownsHandle: false);
                lock (pipes) pipes[claimName] = foreign;
                var o5 = Orphan($@"ROOT\HIDCLASS\{Token}", ParentDuringWait.StaysGone);
                Check("orphan sweep: the claim of the parent's identity held by another program -> the child left",
                      !o5.Removed && o5.Removals == 0, $"removed {o5.Removed}, {o5.Removals} removal(s)");
                foreign.Dispose();

                // A one-controller dispose: the lease of the controller whose
                // teardown runs the sweep holds its identity's claim (issue #9's
                // surviving HID child).
                using var lease = MachineClaims.TryClaim(claimName);
                var o6 = Orphan($@"ROOT\HIDCLASS\{Token}", ParentDuringWait.StaysGone);
                bool kept = lease != null && MachineClaims.HeldHere(claimName);
                Check("orphan sweep: the claim held by this process, as a one-controller dispose's lease holds it -> the child removed under it, the claim kept",
                      lease != null && o6.Removed && o6.Removals == 1 && o6.RemovedUnderLock && kept,
                      $"claim taken {lease != null}, removed {o6.Removed}, {o6.Removals} removal(s), under the lock {o6.RemovedUnderLock}, claim still held {kept}");
            }
            finally { MachineClaims.CreateClaimHook = null; }
        }

        if (Part(9))
        {
            // The exit's removals take the PnP lock within the exit's bound:
            // another program's hold, here another thread's, ends them and the
            // record stays for the next sweep, instead of keeping the process in
            // its exit for as long as the hold lasts.
            var b1 = BoundedRemoval(boundMs: 300, heldMs: 3000, orphan: true);
            Check("exit: another thread holds the PnP lock for 3 s -> the orphan's removal gives up when the exit's 300 ms bound ends, removing nothing",
                  b1.Thrown is DeviceManager.PnpLockBusyException && b1.Ms >= 300 - TimerSlack && b1.Ms < 300 + Slack && b1.Removals == 0,
                  $"{b1.Thrown?.GetType().Name ?? "nothing thrown"} after {b1.Ms} ms, {b1.Removals} removal(s)");
            var b2 = BoundedRemoval(boundMs: 300, heldMs: 3000, orphan: false);
            Check("exit: another thread holds the PnP lock for 3 s -> RemoveDevice gives up when the exit's 300 ms bound ends",
                  b2.Thrown is DeviceManager.PnpLockBusyException && b2.Ms >= 300 - TimerSlack && b2.Ms < 300 + Slack,
                  $"{b2.Thrown?.GetType().Name ?? "nothing thrown"} after {b2.Ms} ms");
            var b3 = BoundedRemoval(boundMs: 300, heldMs: 0, orphan: true);
            Check("exit: the PnP lock free -> the orphan is removed within the bound",
                  b3.Thrown == null && b3.Removals == 1 && b3.Ms < 300,
                  $"{b3.Thrown?.GetType().Name ?? "nothing thrown"} after {b3.Ms} ms, {b3.Removals} removal(s)");
            // Only the waits for another program's hold count against the bound,
            // never the exit's own removals under the lock.
            var b4 = WorkThenWait(boundMs: 300, workMs: 400, otherHoldMs: 50);
            Check("exit: a 300 ms bound, 400 ms of the exit's own removal under the lock, then a 50 ms hold -> the second removal waits for it and removes",
                  b4.Thrown == null && b4.Removals == 2 && b4.SecondMs < 300,
                  $"{b4.Thrown?.GetType().Name ?? "nothing thrown"}, {b4.Removals} removal(s), the second take after {b4.SecondMs} ms");
            // A program's own dispose or cleanup inside its exit handler waits
            // for the lock within the exit's budget too, and says so.
            var b5 = InExit(exiting: true, exitBudgetMs: 300, heldMs: 3000);
            Check("exit signal: another thread holds the PnP lock for 3 s -> a removal with no bound of its own gives up when the 300 ms exit budget ends, counted as refused",
                  b5.Thrown is DeviceManager.PnpLockBusyException && b5.Ms >= 300 - TimerSlack && b5.Ms < 300 + Slack && b5.Removals == 0 && b5.Refused == 1,
                  $"{b5.Thrown?.GetType().Name ?? "nothing thrown"} after {b5.Ms} ms, {b5.Removals} removal(s), {b5.Refused} refused");
            var b6 = InExit(exiting: false, exitBudgetMs: 300, heldMs: 500);
            Check("no exit signal: another thread holds the PnP lock for 500 ms -> the removal waits for it and removes",
                  b6.Thrown == null && b6.Removals == 1 && b6.Ms >= 500 - TimerSlack && b6.Refused == 0,
                  $"{b6.Thrown?.GetType().Name ?? "nothing thrown"} after {b6.Ms} ms, {b6.Removals} removal(s)");
            // The phantom cleanup a first call runs on its own thread during the
            // exit takes the lock for each uninstall out of its own budget of
            // waits, never with the 600 s of a take with no bound, and its
            // uninstalls under the lock are not charged.
            int cleanupBudget = TimeoutScale.Apply(OwnerSweep.UninstallWaitBudgetMs);
            var b7 = PhantomCleanupInExit(workMs: 400);
            Check($"exit signal: the phantom cleanup on the calling thread takes the PnP lock for each phantom within its {cleanupBudget} ms of waiting, 400 ms of uninstall under the lock not charged",
                  b7.Removed == 2 && b7.Takes.Count == 2 && b7.Takes.All(ms => ms >= 0 && ms <= cleanupBudget)
                  && b7.Takes[1] >= cleanupBudget - 100,
                  $"{b7.Removed} removed, takes bounded at [{string.Join(", ", b7.Takes.Select(ms => ms < 0 ? "none" : ms + " ms"))}]");
            // A bound counts only the waits for another program's hold: b9, b12 and
            // the two-take check after them show a wait behind this process's own
            // hold left out, b11 and b10 another program's wait counted, and the
            // shared-budget check that threads waiting together spend the exit's
            // budget once. b8 and the stuck-hold check show the exit's bound on a
            // wait behind one hold of this process's own, and the three checks
            // after the stuck-hold check that it is the bound's full size, not what
            // is left of it, measured less the holder's wait for another program.
            var b8 = ConcurrentInExit(threads: 3, exitBudgetMs: 300, workMs: 200, otherHoldMs: 0);
            Check("exit signal: three removals of this process at once, 200 ms each under the lock, a 300 ms exit budget, no other program -> all three removed, none refused",
                  b8.Removed == 3 && b8.Refused == 0,
                  $"{b8.Removed} removed, {b8.Refused} refused, the threads took [{string.Join(", ", b8.Ms)}] ms");
            var b9 = BoundBehindOwnThread(boundMs: 300, ownWorkMs: 500, otherHoldMs: 0);
            Check("a 300 ms bound outside the exit, another thread of this process holding the lock 500 ms for its removal -> waits for it uncounted and removes",
                  b9.Thrown == null && b9.Bounded && b9.InFront && b9.Ms >= 500 - TimerSlack,
                  $"{b9.Thrown?.GetType().Name ?? "nothing thrown"} after {b9.Ms} ms, removed {b9.Bounded}, the one in front {b9.InFront}");
            var b12 = BoundBehindOwnThread(boundMs: 0, ownWorkMs: 300, otherHoldMs: 0);
            Check("a spent bound outside the exit, another thread of this process holding the lock 300 ms for its removal -> gets the lock free at its turn and removes",
                  b12.Thrown == null && b12.Bounded && b12.InFront,
                  $"{b12.Thrown?.GetType().Name ?? "nothing thrown"} after {b12.Ms} ms, removed {b12.Bounded}, the one in front {b12.InFront}");
            var b10 = ConcurrentInExit(threads: 3, exitBudgetMs: 300, workMs: 0, otherHoldMs: 3000);
            Check("exit signal: another program holds the lock for 3 s, three removals of this process at once, a 300 ms exit budget -> all three refused at about 300 ms, not one after another",
                  b10.Removed == 0 && b10.Refused == 3 && b10.Ms.All(ms => ms >= 300 - TimerSlack && ms < 300 + Slack),
                  $"{b10.Removed} removed, {b10.Refused} refused, after [{string.Join(", ", b10.Ms)}] ms");
            var b11 = BoundBehindOwnThread(boundMs: 300, ownWorkMs: 0, otherHoldMs: 3000);
            Check("a 300 ms bound, behind a thread of this process that waits for another program's 3 s hold -> refused at about 300 ms, that wait counted",
                  b11.Thrown is DeviceManager.PnpLockBusyException && !b11.Bounded && b11.InFront && b11.Ms >= 300 - TimerSlack && b11.Ms < 300 + Slack,
                  $"{b11.Thrown?.GetType().Name ?? "nothing thrown"} after {b11.Ms} ms, removed {b11.Bounded}, the one in front {b11.InFront}");
            var bW = BoundAfterOwnHold(boundMs: 300, ownWorkMs: 500, otherHoldMs: 50);
            Check("a 300 ms bound outside the exit, a take behind this process's own 500 ms removal, then a take behind another program's 50 ms hold -> both remove, the bound still whole",
                  bW.First && bW.Second && bW.SecondMs >= 50 - TimerSlack && bW.SecondMs < 50 + Slack,
                  $"first removed {bW.First}, second removed {bW.Second} after {bW.SecondMs} ms");
            var bS = SharedBudgetLater(threads: 3, exitBudgetMs: 300, firstHoldMs: 200, laterHoldMs: 50);
            Check("exit signal: three takes at once behind another program's 200 ms hold, a 300 ms exit budget, then a fourth behind a 50 ms hold -> all four remove, the budget spent once",
                  bS.First == 3 && bS.Later,
                  $"{bS.First} of 3 removed, the fourth {(bS.Later ? "removed" : "refused")} after {bS.LaterMs} ms");
            var bX = StuckOwnHoldInExit(exitBudgetMs: 300, ownHoldMs: 1500);
            Check("exit signal: a 300 ms exit budget, behind a thread of this process stuck 1.5 s under the lock -> refused at about 300 ms and counted, the record kept",
                  bX.Thrown is DeviceManager.PnpLockBusyException && !bX.Removed && bX.Refused == 1 && bX.InFront && bX.Ms >= 300 - TimerSlack && bX.Ms < 300 + Slack,
                  $"{bX.Thrown?.GetType().Name ?? "nothing thrown"} after {bX.Ms} ms, removed {bX.Removed}, {bX.Refused} refused, the one in front {bX.InFront}");
            var bD1 = SpentThenOwnInExit(exitBudgetMs: 300, otherHoldMs: 400, threads: 3, workMs: 100);
            Check("exit signal: a 300 ms exit budget spent behind another program's hold, then three removals of this process at once, 100 ms each -> all three removed",
                  !bD1.First && bD1.Removed == 3 && bD1.Refused == 0,
                  $"the spending take {(bD1.First ? "removed" : "refused")}, then {bD1.Removed} removed, {bD1.Refused} refused");
            var bD2 = SpentThenOwnInExit(exitBudgetMs: 500, otherHoldMs: 300, threads: 2, workMs: 400);
            Check("exit signal: a 500 ms exit budget with 200 ms left, then two removals of this process of 400 ms each -> both removed, a hold shorter than the full bound waited out",
                  bD2.First && bD2.Removed == 2 && bD2.Refused == 0,
                  $"the spending take {(bD2.First ? "removed" : "refused")}, then {bD2.Removed} removed, {bD2.Refused} refused");
            var bM = ConcurrentInExit(threads: 2, exitBudgetMs: 300, workMs: 100, otherHoldMs: 250);
            Check("exit signal: a 300 ms exit budget, two removals of this process at once behind another program's 250 ms hold, 100 ms each -> both removed, the holder's wait not counted twice",
                  bM.Removed == 2 && bM.Refused == 0,
                  $"{bM.Removed} removed, {bM.Refused} refused, the threads took [{string.Join(", ", bM.Ms)}] ms");
        }

        int locates, cancels;
        lock (s_flags)
        {
            locates = s_flags.Count;
            cancels = s_flags.FindAll(f => (f & CM_LOCATE_DEVNODE_CANCELREMOVE) != 0).Count;
        }
        // A part's process hands its counts to the parent, which checks the
        // sum (RunInParts).
        if (part == 0)
        {
            Check("the waits and the gate located through the hook", locates > 0, $"{locates} locates");
            Check("no CM_LOCATE_DEVNODE_CANCELREMOVE locate was issued", cancels == 0, $"{cancels} of {locates}");
        }

        // Part 10: the tool runs' read bound.
        if (Part(10)) ChildOutputBound();

        if (part != 0)
        {
            Console.WriteLine($"##PART {part} {s_total} {s_failures} {locates} {cancels}");
            return 0;
        }
        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }
}
