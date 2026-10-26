// PnP notifications reach the SDK's waits. A create waits for the HID child
// with CM_Register_Notification, and a dispose waits for the device to go.
// A registration Windows refuses, or a callback that never runs, leaves those
// waits finishing on their 50 ms re-checks instead: slower, and silent. That
// happened once already, when the filter's cbSize was 432 bytes instead of
// 416.
//
// Two threads hold waits open for devices that do not exist. One waits for a
// HID child, which keeps a registration for every device instance's events
// open for the whole window. The other waits for a HID interface, which
// keeps a device-interface registration open. Meanwhile this thread creates
// and disposes a plain HID controller, whose devnodes and interfaces arrive
// and leave. Each waiting registration must receive its events, counted
// inside the SDK's callback by kind: interface arrivals and removals only
// ever come through a device-interface filter, so that count shows the
// interface filter works even when the instance filters do. A wait on the
// create path itself can return before it registers, when the child is
// already there, so the window is held open deliberately instead. The
// window's registration also times each child's own REMOVED against its
// parent's (DeviceManager.InstanceEventTap and AllInstancesRegistrationTap).
// The SDK's removal test takes a child as removed on its parent's
// notification and does not watch its own, so S77 fails when that own
// notification comes after RemoveDevice let the PnP lock go, or not at
// all. A DualShock 4 and an Xbox 360 wired are timed, each in a window of
// its own.
//
// Requires elevation. Exit 0 PASS, 1 FAIL, 2 when another program that embeds
// HIDMaestro is running.
//
// "offline" runs the removal test's and the registration gate's decisions on
// recorded sequences instead (Offline.cs): no device, no elevation, no PnP
// call. "cost" times them against an id that does not exist (Cost.cs),
// "measure [cycles]" times Dispose per profile type on live devices, and
// "measure4 [cycles]" times four controllers of each type disposed together
// (Measure.cs, elevated, through the bench only). "veto" disposes
// controllers while another holder keeps their HID children open (Veto.cs,
// elevated, through the bench only).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Program
{
    static int s_total, s_failures;

    /// <summary>GUID_DEVINTERFACE_HID.</summary>
    static readonly Guid HidInterfaceClass = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static (long On, long WithParent, long Without, long NotGone, long Unregistered) Removals() =>
        (DeviceManager.RemovalsOnNotification, DeviceManager.RemovalsWithParent, DeviceManager.RemovalsWithoutNotification,
         DeviceManager.RemovalsNotGone, DeviceManager.RemovalsUnregistered);

    /// <summary>Fails when a removal of the dispose ended with its devnode
    /// still there, or a devnode in the tree when its removal's first step
    /// started left it without a DEVICEINSTANCEREMOVED notification, its own
    /// or, for a child removed while its parent was in the tree, the
    /// parent's (DeviceManager.SubtreeChildComplete), or with the
    /// notification's registration refused. A removal in steps counts once,
    /// by its last step: a step that ended not gone and that the fallbacks
    /// then completed, as after a fast removal's CM_Uninstall_DevNode, is no
    /// failure. A child removed while its parent's watch was open has no
    /// registration of its own, and counts on the parent's notification,
    /// without one, or not gone.</summary>
    static void CheckRemovals(string profile, (long On, long WithParent, long Without, long NotGone, long Unregistered) before)
    {
        var a = Removals();
        long on = a.On - before.On, withParent = a.WithParent - before.WithParent, without = a.Without - before.Without,
             notGone = a.NotGone - before.NotGone, unregistered = a.Unregistered - before.Unregistered;
        Check($"{profile}: every removal of its dispose ended gone, each devnode in the tree on DEVICEINSTANCEREMOVED, its own or, for a child, its parent's",
              on > 0 && without == 0 && notGone == 0 && unregistered == 0,
              $"{on} on the notification, {withParent} on the parent's, {without} without one, {notGone} not gone, {unregistered} unregistered");
    }

    const uint Enumerated = 7, Removed = 9;

    /// <summary>A held-open registration as S77 saw it: its handle, zero
    /// when Windows refused it, when it was made, and when the wait that
    /// kept it returned, after it was closed.</summary>
    internal readonly record struct HeldRegistration(IntPtr Handle, double OpenedAt, double ClosedAt);

    /// <summary>Whether each child's own DEVICEINSTANCEREMOVED came before
    /// RemoveDevice let the PnP lock go, from the events the held-open
    /// all-instances registration received. The removal test takes a child
    /// as removed on its parent's notification, on the inference that the
    /// child's PDO goes inside the parent's removal: this is where a run on
    /// hardware or in the sandbox can show that inference false.
    /// <para>The children are the parent's own, listed before the dispose as
    /// TeardownController lists them, so another device enumerated in the
    /// window is no child. The bound, <paramref name="lockReleasedAt"/>, is
    /// when the orchestrator logged that the parent's RemoveDevice returned,
    /// the first line it writes once RemoveDevice let the lock go, so a
    /// REMOVED between the release and that line passes.</para>
    /// <para>Only events through <paramref name="window"/> count, between
    /// the moment it was made and the moment its wait returned, so a handle
    /// value another registration held before or after it counts for
    /// nothing. The check judges only with that registration's own
    /// controls: made before
    /// the create began, closed after the lock was let go, the parent's
    /// REMOVED and each child's ENUMERATED delivered through it. Without
    /// one it fails as not judged, naming each missing control, and says
    /// nothing of a child's REMOVED it did not see.</para></summary>
    internal static (bool Ok, string Detail) ChildRemovedInTime((double Ms, IntPtr Registration, uint Action, string Id)[] events,
                                                                HeldRegistration? window, string? parentId, IReadOnlyList<string> children,
                                                                double createAt, double disposeAt, double lockReleasedAt)
    {
        var causes = new List<string>();
        var heard = new List<(double Ms, uint Action, string Id)>();
        if (window is not { } w) causes.Add("the held-open wait made no registration");
        else if (w.Handle == IntPtr.Zero) causes.Add("Windows refused the held-open registration");
        else
        {
            heard.AddRange(events.Where(e => e.Registration == w.Handle && e.Ms >= w.OpenedAt && e.Ms <= w.ClosedAt)
                                 .Select(e => (e.Ms, e.Action, e.Id)));
            if (w.OpenedAt > createAt) causes.Add($"the held-open registration was made {w.OpenedAt - createAt:0.0} ms after the create began");
            if (w.ClosedAt < lockReleasedAt) causes.Add($"the held-open registration closed {lockReleasedAt - w.ClosedAt:0.0} ms before the PnP lock was let go");
        }
        bool listening = window is { Handle: var h } && h != IntPtr.Zero;
        double? Seen(uint action, string id, Func<double, bool> when) =>
            heard.Where(e => e.Action == action && when(e.Ms) && string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
                 .Select(e => (double?)e.Ms).Min();
        if (parentId == null) causes.Add("the controller had no instance id");
        if (double.IsNaN(lockReleasedAt)) causes.Add("no diag line that the parent's RemoveDevice returned, so no time the PnP lock was let go");
        if (children.Count == 0) causes.Add("the parent had no child before the dispose");
        double? parent = listening && parentId != null ? Seen(Removed, parentId, ms => ms >= disposeAt) : null;
        if (listening && parentId != null && parent == null) causes.Add("no REMOVED for the parent through the held-open registration");
        if (listening)
            foreach (string child in children)
                if (Seen(Enumerated, child, ms => ms >= createAt) == null) causes.Add($"no ENUMERATED for {child} through the held-open registration");

        var parts = new List<string>();
        if (causes.Count > 0) parts.Add("not judged: " + string.Join(", ", causes));
        if (parent is double p) parts.Add($"the parent's REMOVED {p - disposeAt:0.0} ms into the dispose");
        if (!double.IsNaN(lockReleasedAt)) parts.Add($"the PnP lock let go {lockReleasedAt - disposeAt:0.0} ms into it");
        bool inTime = true;
        foreach (string child in children)
        {
            double? own = listening ? Seen(Removed, child, ms => ms >= disposeAt) : null;
            if (own is double t)
            {
                bool ok = t <= lockReleasedAt;
                inTime &= ok;
                string bound = double.IsNaN(lockReleasedAt) ? ""
                    : ok ? $", {lockReleasedAt - t:0.0} ms before the PnP lock was let go"
                    : $", {t - lockReleasedAt:0.0} ms after the PnP lock was let go";
                string fromParent = parent is double q ? $", {t - q:+0.0;-0.0;0.0} ms from the parent's" : "";
                parts.Add($"{child}: its own REMOVED {t - disposeAt:0.0} ms into the dispose{bound}{fromParent}");
                continue;
            }
            inTime = false;
            double? earlier = listening
                ? heard.Where(e => e.Action == Removed && e.Ms < disposeAt && string.Equals(e.Id, child, StringComparison.OrdinalIgnoreCase))
                       .Select(e => (double?)e.Ms).Max()
                : null;
            string before = earlier is double b ? $", only one {disposeAt - b:0.0} ms before the dispose began" : "";
            parts.Add(causes.Count > 0
                ? $"{child}: nothing seen of its own REMOVED{before}"
                : $"{child}: no REMOVED of its own through the held-open registration in the {window!.Value.ClosedAt - lockReleasedAt:0.0} ms it stayed open after the PnP lock was let go{before}");
        }
        return (causes.Count == 0 && inTime, string.Join(" | ", parts));
    }

    static void CheckChildRemoved(string profile, (bool Ok, string Detail) judged) =>
        Check($"{profile}: each child's own REMOVED came before RemoveDevice let the PnP lock go, timed through the held-open registration against its parent's",
              judged.Ok, judged.Detail);

    const string NoSuchDevice = @"ROOT\HIDMAESTRO\PNP_NOTIFY_CHECK_NO_SUCH_DEVICE";

    /// <summary>A parent's children as TeardownController captures them
    /// before its removal, none without a parent.</summary>
    static IReadOnlyList<string> ChildrenOf(string? parentId) =>
        parentId == null ? Array.Empty<string>() : DeviceManager.GetAllHidChildIds(parentId);

    /// <summary>The longest each held-open wait keeps its registration: a
    /// create and a dispose of either timed profile, with room to spare. The
    /// wait ends sooner, once the dispose has returned, since nothing that
    /// arrives after that changes a verdict: a child's own REMOVED after the
    /// PnP lock was let go fails whether the window sees it or not. A
    /// window that closes before the PnP lock was let go fails its timing
    /// as not judged.</summary>
    // The SDK's wait scales it, and a create and dispose on a slow, loaded
    // machine at its timeout scale can pass 10 s, so it is a hang bound.
    const int WindowMs = 60_000;

    /// <summary>One create and dispose timed through a held-open
    /// all-instances registration, which the wait for a HID child of a
    /// device that does not exist keeps until <see cref="Close"/>, or for
    /// <see cref="WindowMs"/>. Sets the SDK's taps from its construction
    /// to its disposal and opens no registration of its own.</summary>
    internal sealed class ChildTiming : IDisposable
    {
        [ThreadStatic] static ChildTiming? t_holding;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly System.Collections.Concurrent.ConcurrentQueue<(double Ms, IntPtr Registration, uint Action, string Id)> _events = new();
        bool _registered;
        IntPtr _handle;
        readonly ManualResetEventSlim _close = new();
        double _openedAt, _closedAt = double.NaN, _createAt, _disposeAt, _lockReleasedAt = double.NaN;
        string? _parentId;
        IReadOnlyList<string> _children = Array.Empty<string>();

        /// <summary>Milliseconds on the timing's clock, the one every
        /// event and moment it records is read on.</summary>
        internal double Now => _clock.Elapsed.TotalMilliseconds;

        /// <summary>The window's registration once its wait has ended, null
        /// when the wait made none.</summary>
        internal HeldRegistration? Window => _registered ? new HeldRegistration(_handle, _openedAt, _closedAt) : null;

        /// <summary>When the orchestrator logged that the parent's
        /// RemoveDevice returned, NaN before.</summary>
        internal double LockReleasedAt => _lockReleasedAt;

        internal ChildTiming()
        {
            DeviceManager.InstanceEventTap = (registration, action, id) => _events.Enqueue((Now, registration, action, id));
            // The held-open wait runs alone on its thread while it holds the
            // window, so a registration made there is the window's.
            DeviceManager.AllInstancesRegistrationTap = handle =>
            {
                if (t_holding != this || _registered) return;
                _handle = handle;
                _openedAt = Now;
                _registered = true;
            };
            DeviceOrchestrator.DiagTap = line =>
            {
                if (_parentId is { } p && double.IsNaN(_lockReleasedAt)
                    && line.Contains($"parent RemoveDevice({p}) returned", StringComparison.OrdinalIgnoreCase))
                    _lockReleasedAt = Now;
            };
        }

        /// <summary>Starts the held-open wait, the wait for a HID child of
        /// <see cref="NoSuchDevice"/> unless <paramref name="wait"/> stands
        /// in for it (S77 offline). It ends at <see cref="Close"/>, which its
        /// failure test reads every 25 ms, or after <see cref="WindowMs"/>.
        /// Its task ends after the wait's registration is closed.</summary>
        internal Task<bool> Hold(Func<bool>? wait = null) => Task.Run(() =>
        {
            t_holding = this;
            try { return wait != null ? wait() : DeviceManager.WaitForHidChild(NoSuchDevice, WindowMs, () => _close.IsSet); }
            finally { t_holding = null; _closedAt = Now; }
        });

        /// <summary>Ends the held-open wait. Called once the dispose has
        /// returned.</summary>
        internal void Close() => _close.Set();

        /// <summary>Waits until the held-open wait has made its
        /// registration, or its task <paramref name="held"/> has ended, so
        /// the create starts inside the window. A fixed sleep let a slow
        /// thread pool start the wait after the create began. Bounded by a
        /// hang bound of a minute at the timeout scale.</summary>
        internal void WaitRegistered(Task held)
        {
            var sw = Stopwatch.StartNew();
            int bound = TimeoutScale.Apply(60_000);
            while (!Volatile.Read(ref _registered) && !held.IsCompleted && sw.ElapsedMilliseconds < bound)
                Thread.Sleep(5);
        }

        internal void CreateStarting() => _createAt = Now;

        /// <summary>The dispose of <paramref name="parentId"/> is about to
        /// start, with <paramref name="children"/> its children, listed as
        /// TeardownController lists them (<see cref="ChildrenOf"/>).</summary>
        internal void DisposeStarting(string? parentId, IReadOnlyList<string> children)
        {
            _parentId = parentId;
            _children = children;
            _disposeAt = Now;
        }

        /// <summary>The timing, once the held-open wait's task has
        /// ended.</summary>
        internal (bool Ok, string Detail) Judge() =>
            ChildRemovedInTime(_events.ToArray(), Window, _parentId, _children, _createAt, _disposeAt, _lockReleasedAt);

        public void Dispose()
        {
            _close.Set();
            DeviceManager.InstanceEventTap = null;
            DeviceManager.AllInstancesRegistrationTap = null;
            DeviceOrchestrator.DiagTap = null;
        }
    }

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "";
        // A writer process of offline part 12 (DiagLogAppend.cs), whose
        // environment its parent sets.
        if (mode == "diag-append") return DiagLogAppend.Child(args);
        // The offline modes touch no device. Their lines stay out of the
        // diag log a live run on this machine writes (HIDMAESTRO_DIAG=1),
        // which is read once, when DeviceOrchestrator first loads.
        if (mode is "offline" or "cost") Environment.SetEnvironmentVariable("HIDMAESTRO_DIAG", null);
        // The offline sequences step in unscaled milliseconds against the
        // SDK's own waits, which TimeoutScale also reads once, at type init.
        // A machine's HIDMAESTRO_TIMEOUT_SCALE would stretch those waits and
        // not the sequences, so the offline parts run at scale 1. The live
        // run keeps the machine's scale.
        if (mode == "offline") Environment.SetEnvironmentVariable("HIDMAESTRO_TIMEOUT_SCALE", null);
        // "offline" runs the parts at once, each in a process of its own,
        // "offline <part>" one part, and "offline 0" every part in turn.
        if (mode == "offline") return args.Length > 1 ? Offline.Run(int.Parse(args[1])) : Offline.RunInParts();
        if (mode == "cost") return Cost.Run(args.Length > 1 ? int.Parse(args[1]) : 200);

        Console.WriteLine(mode is "measure" or "measure4" ? "=== Dispose timing ===" : "=== PnP notifications reach the SDK's waits ===");
        // Each process's module list is read on a thread of its own, since
        // one read at a time took about 5.5 s elevated.
        var all = Process.GetProcesses();
        string? sharing = null;
        try
        {
            Parallel.ForEach(all, (p, loop) =>
            {
                try
                {
                    if (p.Id == Environment.ProcessId) return;
                    foreach (ProcessModule m in p.Modules)
                        if (string.Equals(m.ModuleName, "HIDMaestro.Core.dll", StringComparison.OrdinalIgnoreCase))
                        {
                            Interlocked.CompareExchange(ref sharing, p.ProcessName, null);
                            loop.Stop();
                            return;
                        }
                }
                catch { }
            });
        }
        finally
        {
            foreach (var p in all) p.Dispose();
        }
        if (sharing != null)
        {
            Console.WriteLine($"  [SKIP] {sharing} is running and shares the SDK's devices.");
            return 2;
        }

        if (mode == "veto") return Veto.Run();
        if (mode == "measure") return Measure.Run(args.Length > 1 ? int.Parse(args[1]) : 30);
        if (mode == "measure4") return Measure.RunBatch(args.Length > 1 ? int.Parse(args[1]) : 30);

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        var profile = ctx.GetProfile("dualshock-4-v2")!;

        var removalsBefore = Removals();
        // The held-open wait keeps an all-instances registration through the
        // create and the dispose, so a child's own REMOVED reaches it
        // whenever it comes, while the SDK's own test watches the parent's
        // alone. The timing reads that registration's events and adds none.
        var timing = new ChildTiming();
        long before = DeviceManager.NotificationsDelivered;
        long interfaceBefore = DeviceManager.InterfaceNotificationsDelivered;
        var window = timing.Hold();
        using var closeInterfaceWindow = new ManualResetEventSlim();
        var interfaceWindow = Task.Run(() => DeviceManager.WaitForDeviceInterface(NoSuchDevice, HidInterfaceClass, WindowMs, () => closeInterfaceWindow.IsSet));
        timing.WaitRegistered(window);
        Thread.Sleep(250);

        timing.CreateStarting();
        var sw = Stopwatch.StartNew();
        var c = ctx.CreateController(profile, "pnp-notify-check");
        long createMs = sw.ElapsedMilliseconds;
        long afterCreate = DeviceManager.NotificationsDelivered;
        long interfaceAfterCreate = DeviceManager.InterfaceNotificationsDelivered;
        timing.DisposeStarting(c.InstanceId, ChildrenOf(c.InstanceId));
        sw.Restart();
        c.Dispose();
        long disposeMs = sw.ElapsedMilliseconds;
        timing.Close();
        closeInterfaceWindow.Set();
        bool found = window.Result;
        bool interfaceFound = interfaceWindow.Result;
        var timed = timing.Judge();
        timing.Dispose();
        long after = DeviceManager.NotificationsDelivered;
        long interfaceAfter = DeviceManager.InterfaceNotificationsDelivered;

        Check("the held-open wait finds no child for a device that does not exist", !found);
        Check("the held-open interface wait finds nothing for a device that does not exist", !interfaceFound);
        Check("PnP notifications arrive while devices come and go", after > before,
              $"{after - before} delivered: {afterCreate - before} by the end of the create ({createMs} ms), " +
              $"{after - afterCreate} after it, through the dispose ({disposeMs} ms)");
        Check("device-interface notifications arrive through the interface filter", interfaceAfter > interfaceBefore,
              $"{interfaceAfter - interfaceBefore} delivered: {interfaceAfterCreate - interfaceBefore} by the end of the create, " +
              $"{interfaceAfter - interfaceAfterCreate} after it, through the dispose");
        CheckRemovals("dualshock-4-v2", removalsBefore);
        CheckChildRemoved("dualshock-4-v2", timed);

        // The removal test waits for DEVICEINSTANCEREMOVED for every devnode
        // in the tree when its removal starts, a child removed while its
        // parent is in the tree on the parent's. Each kind of removal the SDK
        // makes must end its test on it: a ROOT HID device and its child
        // (SetupDiRemoveDevice), an Xbox 360's ROOT device and its software
        // XUSB companion (SwDeviceClose through hmswd.exe), and an xinputhid
        // software device with its HID children.
        //
        // An Xbox 360's ROOT parent defers its child as a DualShock 4's does,
        // so its child is timed too, in a window of its own. An xinputhid
        // device's children are removed after its software close: one still
        // in the tree at its call counts gone without a notification, or not
        // gone, and CheckRemovals fails on either.
        foreach (string id in new[] { "xbox-360-wired", "xbox-series-xs-bt" })
        {
            var r0 = Removals();
            var timedHere = id == "xbox-360-wired" ? new ChildTiming() : null;
            var held = timedHere?.Hold();
            if (held != null) timedHere!.WaitRegistered(held);
            timedHere?.CreateStarting();
            var more = ctx.CreateController(ctx.GetProfile(id)!, "pnp-notify-check-" + id);
            timedHere?.DisposeStarting(more.InstanceId, ChildrenOf(more.InstanceId));
            more.Dispose();
            timedHere?.Close();
            CheckRemovals(id, r0);
            if (timedHere != null)
            {
                held!.Wait();
                CheckChildRemoved(id, timedHere.Judge());
                timedHere.Dispose();
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }
}
