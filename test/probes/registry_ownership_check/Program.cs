// Machine-wide registry keys that a virtual controller shares with a real
// device of the same VID and PID must come back as they were.
//
// The live part plants the state a real pad leaves behind, snapshots it,
// creates and disposes virtuals of the same identity, runs
// RemoveAllVirtualControllers, starts a fresh context, and diffs:
//
//   - the joy.cpl OEM key of a real pad whose identity a virtual used
//     (OEMName, OEMData, an Axes subkey),
//   - OEM keys of real pads the run never created, under the vendor
//     prefixes the cleanup used to delete wholesale, in HKLM and HKCU,
//   - a vendor's own GameInput mapping for an identity a virtual used,
//   - an OEM key outside those prefixes, the control that must survive
//     under any version.
//
// It also plants one GameInput override exactly as HIDMaestro wrote them
// through v1.11.0, which the cleanup must now remove, and checks that no
// override is left for a catalog identity after the run.
//
// Everything planted is removed at the end, and anything that was there
// before is put back from a copy taken first.
//
// "--winusb-note" runs the offline part and then, in place of the live
// part, the WinUSB package note's, which belongs in a sandbox (WinUsbNote.cs).
//
// Requires elevation. Exit 0 PASS / 1 FAIL / 2 when another SDK consumer is
// running and the live part cannot run.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32;

internal static partial class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    const string OemRel = @"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";
    const string GameInputRel = @"SYSTEM\CurrentControlSet\Control\GameInput\Devices";

    // ── Registry tree helpers ───────────────────────────────────────────

    /// <summary>A key tree as text: every value's name, kind and data,
    /// every subkey, sorted. Null when the key does not exist.</summary>
    static string? Dump(RegistryKey hive, string path)
    {
        using var k = hive.OpenSubKey(path);
        if (k == null) return null;
        var sb = new StringBuilder();
        DumpInto(k, "", sb);
        return sb.ToString();
    }

    static void DumpInto(RegistryKey k, string indent, StringBuilder sb)
    {
        foreach (var name in k.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var kind = k.GetValueKind(name);
            object? v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            string data = v switch
            {
                byte[] b => Convert.ToHexString(b),
                string[] a => string.Join("|", a),
                null => "",
                _ => v.ToString() ?? "",
            };
            sb.Append(indent).Append(name).Append(" (").Append(kind).Append(") = ").Append(data).Append('\n');
        }
        foreach (var sub in k.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(indent).Append('[').Append(sub).Append("]\n");
            using var sk = k.OpenSubKey(sub);
            if (sk != null) DumpInto(sk, indent + "  ", sb);
        }
    }

    static void CopyTree(RegistryKey src, RegistryKey dst)
    {
        foreach (var name in src.GetValueNames())
            dst.SetValue(name, src.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, src.GetValueKind(name));
        foreach (var sub in src.GetSubKeyNames())
        {
            using var s = src.OpenSubKey(sub);
            using var d = dst.CreateSubKey(sub);
            if (s != null && d != null) CopyTree(s, d);
        }
    }

    /// <summary>One planted key: where it is, what it holds, and what was
    /// there before, so the run can put that back.</summary>
    sealed class Plant
    {
        public string Label = "";
        public RegistryKey Hive = Registry.LocalMachine;
        public string Path = "";
        public Action<RegistryKey> Fill = _ => { };
        public string? Before;            // dump of the planted state
        public bool HadOriginal;
        public string BackupPath = "";    // under the probe's own backup key
    }

    const string BackupRoot = @"SOFTWARE\HIDMaestroProbeBackup\registry_ownership_check";

    static void PlantKey(Plant p, int ordinal)
    {
        // Keep whatever is there now.
        p.BackupPath = $@"{BackupRoot}\{ordinal}";
        Registry.LocalMachine.DeleteSubKeyTree(p.BackupPath, false);
        using (var existing = p.Hive.OpenSubKey(p.Path))
        {
            p.HadOriginal = existing != null;
            if (existing != null)
            {
                using var b = Registry.LocalMachine.CreateSubKey(p.BackupPath);
                CopyTree(existing, b);
            }
        }
        p.Hive.DeleteSubKeyTree(p.Path, false);
        using (var k = p.Hive.CreateSubKey(p.Path))
            p.Fill(k);
        p.Before = Dump(p.Hive, p.Path);
    }

    static void Unplant(Plant p)
    {
        p.Hive.DeleteSubKeyTree(p.Path, false);
        if (p.HadOriginal)
        {
            using var b = Registry.LocalMachine.OpenSubKey(p.BackupPath);
            using var k = p.Hive.CreateSubKey(p.Path);
            if (b != null) CopyTree(b, k);
        }
        Registry.LocalMachine.DeleteSubKeyTree(p.BackupPath, false);
    }

    /// <summary>The GameInput override exactly as HIDMaestro wrote it
    /// through v1.11.0 for a profile with separate triggers.</summary>
    static void FillLegacyOverride(RegistryKey device)
    {
        using var gp = device.CreateSubKey("Gamepad");
        void Axis(string n, int i, bool inv = false)
        {
            using var k = gp.CreateSubKey(n);
            k.SetValue("AxisIndex", i, RegistryValueKind.DWord);
            if (inv) k.SetValue("Invert", 1, RegistryValueKind.DWord);
        }
        void Button(string n, int i)
        {
            using var k = gp.CreateSubKey(n);
            k.SetValue("ButtonIndex", i, RegistryValueKind.DWord);
        }
        void DPad(string n, string pos)
        {
            using var k = gp.CreateSubKey(n);
            k.SetValue("SwitchIndex", 0, RegistryValueKind.DWord);
            k.SetValue("SwitchPosition", pos, RegistryValueKind.String);
            k.SetValue("IncludeAdjacent", 1, RegistryValueKind.DWord);
        }
        Axis("LeftThumbstickX", 0); Axis("LeftThumbstickY", 1, true);
        Axis("RightThumbstickX", 2); Axis("RightThumbstickY", 3, true);
        Axis("LeftTrigger", 4); Axis("RightTrigger", 5);
        Button("A", 0); Button("B", 1); Button("X", 2); Button("Y", 3);
        Button("LeftShoulder", 4); Button("RightShoulder", 5);
        Button("View", 6); Button("Menu", 7);
        Button("LeftThumbstickButton", 8); Button("RightThumbstickButton", 9);
        DPad("DPadUp", "Up"); DPad("DPadDown", "Down"); DPad("DPadLeft", "Left"); DPad("DPadRight", "Right");
    }

    static string? FindLiveSdkConsumer()
    {
        if (SdkConsumers.Find() is string loaded) return loaded;
        // The named input sections are what another consumer's controllers
        // write through, so an open one means live virtuals this run's
        // cleanup would remove.
        for (int i = 0; i < 16; i++)
        {
            IntPtr h = OpenFileMappingW(0x0004, false, $@"Global\HIDMaestroInput{i}");
            if (h != IntPtr.Zero)
            {
                CloseHandle(h);
                return $"another SDK consumer (Global\\HIDMaestroInput{i} is already mapped)";
            }
        }
        return null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    // ── Live part ───────────────────────────────────────────────────────

    static bool Live()
    {
        Console.WriteLine("\n== Live: planted keys across create, dispose, cleanup and a fresh context ==");
        string? consumer = FindLiveSdkConsumer();
        if (consumer != null)
        {
            Console.WriteLine($"  [SKIP] {consumer} is running and shares the SDK's devices.");
            return false;
        }

        var plants = new List<Plant>
        {
            new()
            {
                Label = "HKLM OEM key of a real DualSense (054C:0CE6), an identity the run creates",
                Hive = Registry.LocalMachine, Path = $@"{OemRel}\VID_054C&PID_0CE6",
                Fill = k =>
                {
                    k.SetValue("OEMName", "Planted real DualSense", RegistryValueKind.String);
                    k.SetValue("OEMData", new byte[] { 0x03, 0x00, 0x08, 0x10, 0x0E, 0x00, 0x00, 0x00 }, RegistryValueKind.Binary);
                    using var axes = k.CreateSubKey(@"Axes\0");
                    axes.SetValue("Attributes", new byte[] { 0x01, 0x81, 0x00, 0x00, 0x01, 0x00, 0x30, 0x00 }, RegistryValueKind.Binary);
                    axes.SetValue("", "X Axis", RegistryValueKind.String);
                },
            },
            new()
            {
                Label = "HKLM OEM key of a real Xbox pad (045E:02FF) the run never creates",
                Hive = Registry.LocalMachine, Path = $@"{OemRel}\VID_045E&PID_02FF",
                Fill = k =>
                {
                    k.SetValue("OEMName", "Planted Xbox pad", RegistryValueKind.String);
                    k.SetValue("OEMData", new byte[] { 0x03, 0x00, 0x08, 0x10, 0x10, 0x00, 0x00, 0x00 }, RegistryValueKind.Binary);
                },
            },
            new()
            {
                Label = "HKCU OEM key of a real DualShock 4 (054C:05C4) the run never creates",
                Hive = Registry.CurrentUser, Path = $@"{OemRel}\VID_054C&PID_05C4",
                Fill = k => k.SetValue("OEMName", "Planted HKCU DualShock 4", RegistryValueKind.String),
            },
            new()
            {
                Label = "HKCU OEM key of a real Xbox Series pad (045E:0B13) the run never creates",
                Hive = Registry.CurrentUser, Path = $@"{OemRel}\VID_045E&PID_0B13",
                Fill = k => k.SetValue("OEMName", "Planted HKCU Xbox pad", RegistryValueKind.String),
            },
            new()
            {
                Label = "control: HKLM OEM key outside the swept vendor prefixes (046D:C21D)",
                Hive = Registry.LocalMachine, Path = $@"{OemRel}\VID_046D&PID_C21D",
                Fill = k => k.SetValue("OEMName", "Planted control", RegistryValueKind.String),
            },
            new()
            {
                Label = "a vendor's own GameInput mapping for 054C:09CC, an identity the run creates",
                Hive = Registry.LocalMachine, Path = $@"{GameInputRel}\054C09CC00010005",
                Fill = k =>
                {
                    k.SetValue("Description", "Planted vendor mapping", RegistryValueKind.String);
                    using var a = k.CreateSubKey(@"Gamepad\A");
                    a.SetValue("ButtonIndex", 1, RegistryValueKind.DWord);
                },
            },
        };
        var legacy = new Plant
        {
            Label = "a GameInput override as HIDMaestro wrote them through v1.11.0 (BEEF:F0A7)",
            Hive = Registry.LocalMachine, Path = $@"{GameInputRel}\BEEFF0A700010005",
            Fill = FillLegacyOverride,
        };
        string catalogOverride = $@"{GameInputRel}\054C0CE600010005";
        bool catalogOverrideBefore;

        try
        {
            for (int i = 0; i < plants.Count; i++) PlantKey(plants[i], i);
            PlantKey(legacy, 100);
            // The run's own DualSense override: whatever is there, it goes
            // first, so a key found afterwards was written by this run.
            Registry.LocalMachine.DeleteSubKeyTree(catalogOverride, false);
            catalogOverrideBefore = false;

            using (var ctx = new HMContext())
            {
                ctx.LoadDefaultProfiles();
                ctx.InstallDriver();
                // InstallDriver sweeps. Plant again so the create and the
                // explicit cleanup below are what the diff measures.
                for (int i = 0; i < plants.Count; i++) { plants[i].Hive.DeleteSubKeyTree(plants[i].Path, false); using var k = plants[i].Hive.CreateSubKey(plants[i].Path); plants[i].Fill(k); plants[i].Before = Dump(plants[i].Hive, plants[i].Path); }
                { legacy.Hive.DeleteSubKeyTree(legacy.Path, false); using var k = legacy.Hive.CreateSubKey(legacy.Path); legacy.Fill(k); legacy.Before = Dump(legacy.Hive, legacy.Path); }
                Registry.LocalMachine.DeleteSubKeyTree(catalogOverride, false);

                foreach (var id in new[] { "dualsense", "dualshock-4-v2" })
                {
                    var profile = ctx.GetProfile(id)!;
                    using var pad = ctx.CreateController(profile);
                    var state = new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(profile) };
                    for (int i = 0; i < 40; i++) { pad.SubmitState(in state); Thread.Sleep(10); }
                    Console.WriteLine($"  created and fed {id}");
                    if (id == "dualsense")
                    {
                        // While the virtual is live the shared key holds the
                        // virtual's own name, which joy.cpl shows.
                        using var k = Registry.LocalMachine.OpenSubKey(plants[0].Path);
                        string? live = k?.GetValue("OEMName") as string;
                        Check("while the DualSense lives, HKLM OEMName is its display name", live == profile.DisplayName, live ?? "(key gone)");
                        Check("while it lives, OEMData and Axes are the planted ones",
                              k?.GetValue("OEMData") is byte[] od && od.Length == 8 && od[4] == 0x0E && k.OpenSubKey(@"Axes\0") != null);
                    }
                }
                Console.WriteLine("  both disposed");
            }

            HMContext.RemoveAllVirtualControllers();
            Console.WriteLine("  RemoveAllVirtualControllers ran");
            using (var ctx2 = new HMContext())
            {
                ctx2.LoadDefaultProfiles();
                ctx2.InstallDriver();
            }
            Console.WriteLine("  a fresh context started");

            foreach (var p in plants)
            {
                string? after = Dump(p.Hive, p.Path);
                Check($"{p.Label}: as planted", after == p.Before,
                      after == null ? "the key is gone" : after == p.Before ? "" : "changed: " + after.Replace("\n", " / ").Trim());
            }
            Check($"{legacy.Label}: removed by the cleanup", Dump(legacy.Hive, legacy.Path) == null);
            Check("no GameInput override is left for the catalog identity 054C:0CE6",
                  Dump(Registry.LocalMachine, catalogOverride) == null && !catalogOverrideBefore);
            using (var ledger = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroRegistryLedger"))
            {
                int left = 0;
                if (ledger != null)
                    foreach (var kind in ledger.GetSubKeyNames())
                    {
                        using var kk = ledger.OpenSubKey(kind);
                        left += kk?.SubKeyCount ?? 0;
                    }
                Check("the ledger holds no entry once every virtual is gone", left == 0, $"{left} left");
            }

            CodeBuiltPad();
            OverrideInterplay();
            CrashRecovery();
        }
        finally
        {
            foreach (var p in plants) { try { Unplant(p); } catch (Exception ex) { Console.WriteLine($"  unplant {p.Path}: {ex.Message}"); } }
            try { Unplant(legacy); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(BackupRoot, false); } catch { }
        }
        return true;
    }

    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--child-hold") return ChildHold(args[1]);
        if (args.Length >= 2 && args[0] == "--child-create") return ChildCreate(args[1]);
        if (args.Length >= 1 && args[0] == "--child-bindgc") return ChildBindGc("");
        Console.WriteLine("=== Registry keys shared with real devices (foundation-sunshine #1138) ===");
        bool offlineOnly = args.Contains("--offline");
        Offline();
        bool ranLive = false;
        if (args.Contains("--winusb-note"))
        {
            WinUsbNoteLive();
            ranLive = true;
        }
        else if (!offlineOnly) ranLive = Live();
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        if (s_failures != 0) return 1;
        return offlineOnly || ranLive ? 0 : 2;
    }
}
