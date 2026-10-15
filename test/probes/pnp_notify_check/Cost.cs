// What the removal test and the registration gate cost when nothing is being
// removed or installed, with no device and no elevation: locates, a REMOVED
// registration and the install-queue test against an instance id that does
// not exist. It also prints whether Windows accepts a REMOVED registration
// for that id and for the root devnode, which is always present. Every call
// here reads PnP state and changes none. The gate's
// machine-wide mutex lives in an administrators-only namespace, so a mutex
// in this session's namespace stands in for its kernel calls.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro.Internal;

internal static partial class Cost
{
    const string Id = @"ROOT\HIDCLASS\PNP_NOTIFY_CHECK_COST_NO_SUCH_DEVICE";

    [LibraryImport("CfgMgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [LibraryImport("CfgMgr32.dll", EntryPoint = "CMP_WaitNoPendingInstallEvents")]
    private static partial uint CMP_WaitNoPendingInstallEvents(uint dwTimeout);

    static void Report(string what, int runs, Action body)
    {
        body();
        var ticks = new long[runs];
        for (int i = 0; i < runs; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            body();
            ticks[i] = Stopwatch.GetTimestamp() - t0;
        }
        Array.Sort(ticks);
        double Us(long t) => t * 1_000_000.0 / Stopwatch.Frequency;
        Console.WriteLine($"  {what,-64} n={runs} p50={Us(ticks[runs / 2]),9:F1} us  p95={Us(ticks[(int)(runs * 0.95)]),9:F1} us");
    }

    internal static int Run(int runs)
    {
        Console.WriteLine($"=== Removal test and registration gate: cost with nothing pending ({runs} runs each) ===");
        Console.WriteLine("  before (v1.12.0 and master 238bd2b):");
        Report("gate: NORMAL and PHANTOM locate of an absent id", runs, () =>
        {
            CM_Locate_DevNodeW(out _, Id, 0);
            CM_Locate_DevNodeW(out _, Id, 1);
        });
        Report("wait: entry NORMAL locate of an absent id", runs, () => CM_Locate_DevNodeW(out _, Id, 0));

        Console.WriteLine("  after:");
        foreach (string id in new[] { Id, @"HTREE\ROOT\0" })
        {
            using var probe = DeviceManager.RemovalWatch.Start(id);
            Console.WriteLine($"  REMOVED registration for {id} ({(probe.PresentAtStart ? "present" : "absent")}): " +
                              $"{(probe.Registered ? "accepted" : "refused, CONFIGRET 0x" + Measure.RegisterResult(id).ToString("X"))}");
        }
        Report("removal test: REMOVED registration, locates, unregister", runs, () =>
        {
            using var w = DeviceManager.RemovalWatch.Start(Id);
            w.IsComplete(uninstalls: true);
        });
        Report("install queue: CMP_WaitNoPendingInstallEvents(30000), idle", runs, () => CMP_WaitNoPendingInstallEvents(30_000));
        Report("gate: removal test and install queue, absent id", runs, () =>
            DeviceManager.AwaitRegistrationGate(Id, () => true, timeoutMs: 5000, installWaitMs: DeviceManager.InstallQueueWaitMs));
        string name = @"Local\HIDMaestro.PnpNotifyCheck.Cost." + Environment.ProcessId;
        Report("mutex stand-in: open, wait, release, close", runs, () =>
        {
            using var m = new Mutex(false, name);
            m.WaitOne();
            m.ReleaseMutex();
        });
        return 0;
    }
}
