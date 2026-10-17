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
        readonly Sequence _none = new() { AbsentBefore = true };

        public Sequence Of(string id) => Ids.TryGetValue(id, out var s) ? s : _none;

        public bool Call(string kind, string id)
        {
            lock (Calls) Calls.Add($"{kind} {id}");
            var s = Of(id);
            if (s.Now < 0) s.Begin();
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

    /// <summary>DeviceManager.RemoveDevice on <paramref name="parentId"/>
    /// through the hooks, so the removal runs in RemoveDeviceLocked's own
    /// order. Returns its answer, when it returned from the call, and what
    /// it added to the counts S77 reads.</summary>
    static (bool Removed, long Ms, long On, long Without, long NotGone) RunRemoval(Tree tree, string parentId,
        bool fast = false, int timeoutMs = 5000)
    {
        long on0 = DeviceManager.RemovalsOnNotification, without0 = DeviceManager.RemovalsWithoutNotification,
             notGone0 = DeviceManager.RemovalsNotGone;
        DeviceManager.LocateHook = (id, flags) => tree.Of(id).Locate(id, flags);
        DeviceManager.RemovedHook = tree.Register;
        DeviceManager.PnpLockHook = () => new StandInLock();
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
                    DeviceManager.RemovalsOnNotification - on0, DeviceManager.RemovalsWithoutNotification - without0,
                    DeviceManager.RemovalsNotGone - notGone0);
        }
        finally
        {
            foreach (var s in tree.Ids.Values) s.End();
            DeviceManager.RemovalCallsHook = null;
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
        var holder = new Thread(() =>
        {
            standIn.Wait();
            held.Set();
            Thread.Sleep(heldMs);
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
        var holder = new Thread(() =>
        {
            standIn.Wait();
            held.Set();
            Thread.Sleep(heldMs);
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
        bool removed = false, inFront = false;
        Exception? thrown = null;
        long ms = 0, refused0 = DeviceManager.LockTakesRefusedHere;
        var front = new Thread(() => DeviceManager.RemoveOrphanHidChild(F, @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_PARENT", () =>
        {
            working.Set();
            Thread.Sleep(ownHoldMs);
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
        Check("RemoveDevice through the hooks: one REMOVED registration open at a time, one for each devnode",
              pairTree.MostOpen == 1 && pairTree.Ids[Id].Registrations == 1 && pairTree.Ids[ChildId].Registrations == 1,
              $"at most {pairTree.MostOpen} open, parent {pairTree.Ids[Id].Registrations}, child {pairTree.Ids[ChildId].Registrations}");
        var fastTree = Pair();
        var fastPair = RunRemoval(fastTree, Id, fast: true);
        Check("fast RemoveDevice through the hooks: the child uninstalled, then the parent, one REMOVED registration open at a time",
              fastPair.Removed && fastTree.Calls.SequenceEqual(new[] { $"uninstall {ChildId}", $"uninstall {Id}" }) && fastTree.MostOpen == 1,
              $"{string.Join(", ", fastTree.Calls)}, at most {fastTree.MostOpen} open, {fastPair.Ms} ms");

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

        // A create attempt that timed out can have made the device and closed
        // it: the retry registers the id again only once it has left.
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
