// Issue #9 reproducer: create xbox-360-wired virtual, dispose it, scan
// HID enumerator for orphan HID children whose parent ROOT\ device no
// longer exists. Exits 0 if no orphans; exits 1 if any orphan found.
//
// Only the HID children this run's controller brought are judged: those
// live after the create that were not live before it. A real pad or
// another program's virtual is live before the create and is never
// counted. Both waits are 30 s hang bounds that end as soon as PnP has
// settled, since other work on the machine slows a start and a removal.
//
// Run elevated (the manifest requests admin). Pair with a second run
// post-fix to confirm: before fix = exit 1, after fix = exit 0.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32;
using System.Runtime.InteropServices;

internal static partial class P
{
    const uint CR_SUCCESS = 0;
    const int SettleBoundMs = 30_000;

    [LibraryImport("CfgMgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    static int Main()
    {
        Console.WriteLine("--- Issue #9 repro: xbox-360-wired create + dispose + orphan check ---");
        int exitCode = 0;
        HashSet<string> ours;

        using (var ctx = new HMContext())
        {
            ctx.LoadDefaultProfiles();
            ctx.InstallDriver();

            var profile = ctx.GetProfile("xbox-360-wired");
            if (profile is null) { Console.WriteLine("FAIL: xbox-360-wired profile not found"); return 2; }

            var before = LiveChildren();
            Console.WriteLine("Creating xbox-360-wired virtual...");
            var ctrl = ctx.CreateController(profile);
            // Let PnP settle: wait for this controller's HID child to start.
            var sw = Stopwatch.StartNew();
            do
            {
                ours = LiveChildren();
                ours.ExceptWith(before);
                if (ours.Count > 0) break;
                Thread.Sleep(100);
            } while (sw.ElapsedMilliseconds < SettleBoundMs);
            foreach (var id in ours) Console.WriteLine($"  this run's HID child: {id}");

            Console.WriteLine("Disposing controller (issue #9 scenario)...");
            ctrl.Dispose();
        }

        if (ours.Count == 0)
        {
            // Without the child there is nothing to judge, and a device that
            // never started is itself a failure.
            Console.WriteLine($"\nFAIL: the controller's HID child did not start within {SettleBoundMs / 1000} s");
            HMContext.RemoveAllVirtualControllers();
            return 1;
        }

        // Let PnP settle the removal. An orphan stays, so the 30 s hang
        // bound still fails it.
        Console.WriteLine("\n--- Scanning HID enumerator for orphans ---");
        var linger = new List<string>();
        var removal = Stopwatch.StartNew();
        do
        {
            linger = ours.Where(IsLive).ToList();
            if (linger.Count == 0) break;
            Thread.Sleep(100);
        } while (removal.ElapsedMilliseconds < SettleBoundMs);
        foreach (var id in linger) Console.WriteLine($"  HID child still present: {id}");

        if (linger.Count == 0)
        {
            Console.WriteLine($"\nPASS: no HIDMaestro HID orphans after Dispose (gone after {removal.ElapsedMilliseconds} ms)");
        }
        else
        {
            Console.WriteLine($"\nFAIL: {linger.Count} HID child device(s) linger {SettleBoundMs / 1000} s after Dispose (issue #9 bug)");
            exitCode = 1;
        }

        // Final sweep so we don't leave the test system dirty
        HMContext.RemoveAllVirtualControllers();
        return exitCode;
    }

    static bool IsLive(string childId) => CM_Locate_DevNodeW(out _, childId, 0) == CR_SUCCESS;

    /// <summary>The live HID children under the enumerators an
    /// xbox-360-wired child can take: VID_045E with IG_00, or HIDCLASS.</summary>
    static HashSet<string> LiveChildren()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var hid = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\HID");
            if (hid == null) return live;
            foreach (var devName in hid.GetSubKeyNames())
            {
                string upper = devName.ToUpperInvariant();
                bool couldBeOurs = (upper.Contains("VID_045E") && upper.Contains("IG_00"))
                                 || upper.Contains("HIDCLASS");
                if (!couldBeOurs) continue;

                using var devKey = hid.OpenSubKey(devName);
                if (devKey is null) continue;
                foreach (var inst in devKey.GetSubKeyNames())
                {
                    string childId = $@"HID\{devName}\{inst}";
                    if (IsLive(childId)) live.Add(childId);
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"Scan error: {ex.Message}"); }
        return live;
    }
}
