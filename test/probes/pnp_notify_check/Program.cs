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
// already there, so the window is held open deliberately instead.
//
// Requires elevation. Exit 0 PASS, 1 FAIL, 2 when another program that embeds
// HIDMaestro is running.
//
// "offline" runs the removal test's and the registration gate's decisions on
// recorded sequences instead (Offline.cs): no device, no elevation, no PnP
// call. "cost" times them against an id that does not exist (Cost.cs),
// "measure [cycles]" times Dispose per profile type on live devices, and
// "measure4 [cycles]" times four controllers of each type disposed together
// (Measure.cs, elevated, through the bench only).
using System;
using System.Diagnostics;
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

    static (long On, long Without, long NotGone, long Unregistered) Removals() =>
        (DeviceManager.RemovalsOnNotification, DeviceManager.RemovalsWithoutNotification,
         DeviceManager.RemovalsNotGone, DeviceManager.RemovalsUnregistered);

    /// <summary>Fails when a removal of the dispose ended with its devnode
    /// still there, or a devnode in the tree when its removal's first step
    /// started left it without DEVICEINSTANCEREMOVED or with the
    /// notification's registration refused. A removal in steps counts once,
    /// by its last step: a step that ended not gone and that the fallbacks
    /// then completed, as after a fast removal's CM_Uninstall_DevNode, is no
    /// failure.</summary>
    static void CheckRemovals(string profile, (long On, long Without, long NotGone, long Unregistered) before)
    {
        var a = Removals();
        long on = a.On - before.On, without = a.Without - before.Without, notGone = a.NotGone - before.NotGone,
             unregistered = a.Unregistered - before.Unregistered;
        Check($"{profile}: every removal of its dispose ended gone, each devnode in the tree on DEVICEINSTANCEREMOVED",
              on > 0 && without == 0 && notGone == 0 && unregistered == 0,
              $"{on} on the notification, {without} without it, {notGone} not gone, {unregistered} unregistered");
    }

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "";
        // The offline modes touch no device. Their lines stay out of the
        // diag log a live run on this machine writes (HIDMAESTRO_DIAG=1),
        // which is read once, when DeviceOrchestrator first loads.
        if (mode is "offline" or "cost") Environment.SetEnvironmentVariable("HIDMAESTRO_DIAG", null);
        if (mode == "offline") return Offline.Run();
        if (mode == "cost") return Cost.Run(args.Length > 1 ? int.Parse(args[1]) : 200);

        Console.WriteLine(mode is "measure" or "measure4" ? "=== Dispose timing ===" : "=== PnP notifications reach the SDK's waits ===");
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                foreach (ProcessModule m in p.Modules)
                    if (string.Equals(m.ModuleName, "HIDMaestro.Core.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"  [SKIP] {p.ProcessName} is running and shares the SDK's devices.");
                        return 2;
                    }
            }
            catch { }
            finally { p.Dispose(); }
        }

        if (mode == "measure") return Measure.Run(args.Length > 1 ? int.Parse(args[1]) : 30);
        if (mode == "measure4") return Measure.RunBatch(args.Length > 1 ? int.Parse(args[1]) : 30);

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        var profile = ctx.GetProfile("dualshock-4-v2")!;

        var removalsBefore = Removals();
        long before = DeviceManager.NotificationsDelivered;
        long interfaceBefore = DeviceManager.InterfaceNotificationsDelivered;
        var window = Task.Run(() => DeviceManager.WaitForHidChild(@"ROOT\HIDMAESTRO\PNP_NOTIFY_CHECK_NO_SUCH_DEVICE", 6000));
        var interfaceWindow = Task.Run(() => DeviceManager.WaitForDeviceInterface(@"ROOT\HIDMAESTRO\PNP_NOTIFY_CHECK_NO_SUCH_DEVICE", HidInterfaceClass, 6000));
        Thread.Sleep(250);

        var sw = Stopwatch.StartNew();
        var c = ctx.CreateController(profile, "pnp-notify-check");
        long createMs = sw.ElapsedMilliseconds;
        long afterCreate = DeviceManager.NotificationsDelivered;
        long interfaceAfterCreate = DeviceManager.InterfaceNotificationsDelivered;
        sw.Restart();
        c.Dispose();
        long disposeMs = sw.ElapsedMilliseconds;
        bool found = window.Result;
        bool interfaceFound = interfaceWindow.Result;
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

        // The removal test waits for DEVICEINSTANCEREMOVED for every devnode
        // in the tree when its removal starts. Each kind of removal the SDK
        // makes must end its test on it: a ROOT HID device and its child
        // (SetupDiRemoveDevice), an Xbox 360's ROOT device and its software
        // XUSB companion (SwDeviceClose through hmswd.exe), and an xinputhid
        // software device with its HID children.
        foreach (string id in new[] { "xbox-360-wired", "xbox-series-xs-bt" })
        {
            var r0 = Removals();
            var more = ctx.CreateController(ctx.GetProfile(id)!, "pnp-notify-check-" + id);
            more.Dispose();
            CheckRemovals(id, r0);
        }

        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }
}
