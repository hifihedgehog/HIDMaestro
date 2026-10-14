// PnP notifications reach the SDK's waits. A create waits for the HID child
// with CM_Register_Notification, and a dispose waits for the device to go.
// A registration Windows refuses, or a callback that never runs, leaves those
// waits finishing on their 50 ms re-checks instead: slower, and silent. That
// happened once already, when the filter's cbSize was 432 bytes instead of
// 416.
//
// One thread waits for the HID child of a device that does not exist, which
// keeps a registration for every device instance's events open for the whole
// window. Meanwhile this thread creates and disposes a plain HID controller,
// whose devnodes arrive and leave. The waiting registration must receive
// those events, counted inside the SDK's callback. A wait on the create path
// itself can return before it registers, when the child is already there, so
// the window is held open deliberately instead.
//
// Requires elevation. Exit 0 PASS, 1 FAIL, 2 when another program that embeds
// HIDMaestro is running.
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static int Main()
    {
        Console.WriteLine("=== PnP notifications reach the SDK's waits ===");
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

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        var profile = ctx.GetProfile("dualshock-4-v2")!;

        long before = DeviceManager.NotificationsDelivered;
        var window = Task.Run(() => DeviceManager.WaitForHidChild(@"ROOT\HIDMAESTRO\PNP_NOTIFY_CHECK_NO_SUCH_DEVICE", 6000));
        Thread.Sleep(250);

        var sw = Stopwatch.StartNew();
        var c = ctx.CreateController(profile, "pnp-notify-check");
        long createMs = sw.ElapsedMilliseconds;
        long afterCreate = DeviceManager.NotificationsDelivered;
        sw.Restart();
        c.Dispose();
        long disposeMs = sw.ElapsedMilliseconds;
        bool found = window.Result;
        long after = DeviceManager.NotificationsDelivered;

        Check("the held-open wait finds no child for a device that does not exist", !found);
        Check("PnP notifications arrive while devices come and go", after > before,
              $"{after - before} delivered: {afterCreate - before} by the end of the create ({createMs} ms), " +
              $"{after - afterCreate} after it, through the dispose ({disposeMs} ms)");

        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }
}
