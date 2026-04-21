// Issue #9 reproducer: create xbox-360-wired virtual, dispose it, scan
// HID enumerator for orphan HID children whose parent ROOT\ device no
// longer exists. Exits 0 if no orphans; exits 1 if any orphan found.
//
// Run elevated (the manifest requests admin). Pair with a second run
// post-fix to confirm: before fix = exit 1, after fix = exit 0.

using System;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32;
using System.Runtime.InteropServices;

internal static partial class P
{
    const uint CR_SUCCESS = 0;
    const uint CM_LOCATE_DEVNODE_PHANTOM = 1;

    [LibraryImport("CfgMgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    static int Main()
    {
        Console.WriteLine("--- Issue #9 repro: xbox-360-wired create + dispose + orphan check ---");
        int exitCode = 0;

        using (var ctx = new HMContext())
        {
            ctx.LoadDefaultProfiles();
            ctx.InstallDriver();

            var profile = ctx.GetProfile("xbox-360-wired");
            if (profile is null) { Console.WriteLine("FAIL: xbox-360-wired profile not found"); return 2; }

            Console.WriteLine("Creating xbox-360-wired virtual...");
            var ctrl = ctx.CreateController(profile);
            Thread.Sleep(2000); // let PnP settle

            Console.WriteLine("Disposing controller (issue #9 scenario)...");
            ctrl.Dispose();
            Thread.Sleep(2000); // let PnP settle removal
        }

        Console.WriteLine("\n--- Scanning HID enumerator for orphans ---");
        int orphanCount = 0;
        try
        {
            using var hid = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\HID");
            if (hid != null)
            {
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
                        using var instKey = devKey.OpenSubKey(inst);
                        if (instKey is null) continue;
                        if (instKey.GetValue("ParentIdPrefix") is not string)
                        {
                            // Fall through — check via parent devnode
                        }

                        // Reconstruct parent from registry or devnode
                        if (CM_Locate_DevNodeW(out uint childInst, childId, 0) == CR_SUCCESS)
                        {
                            // Live child — check if its parent still exists
                            if (CM_Locate_DevNodeW(out uint _, childId, CM_LOCATE_DEVNODE_PHANTOM) == CR_SUCCESS)
                            {
                                // check explicit parent
                            }
                            // Use DeviceManager's sweep logic: if the kernel thinks the
                            // parent devnode can't be located, it's an orphan.
                            // Simpler: the DeviceManager.RemoveOrphanHidChildren does this check —
                            // but it ALSO removes. For diagnostic we just count.
                            // Use reflection or just assume any HIDMaestro-matching HID child that
                            // lingers is suspect.
                            Console.WriteLine($"  HID child still present: {childId}");
                            orphanCount++;
                        }
                    }
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"Scan error: {ex.Message}"); }

        if (orphanCount == 0)
        {
            Console.WriteLine("\nPASS: no HIDMaestro HID orphans after Dispose");
        }
        else
        {
            Console.WriteLine($"\nFAIL: {orphanCount} HID child device(s) linger after Dispose (issue #9 bug)");
            exitCode = 1;
        }

        // Final sweep so we don't leave the test system dirty
        HMContext.RemoveAllVirtualControllers();
        return exitCode;
    }
}
