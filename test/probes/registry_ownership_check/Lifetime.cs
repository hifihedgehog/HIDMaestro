// Live parts that follow the shared keys through a controller's lifetime:
// the GameInput table a code-built pad holds, HMOemNameOverride claimed
// before or after the virtual and released in either order, and a consumer
// that dies with its virtual alive.

using System;
using System.Diagnostics;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using Microsoft.Win32;

internal static partial class Program
{
    const string LedgerRoot = @"SOFTWARE\HIDMaestroRegistryLedger";

    static HMProfile CodeBuilt(ushort pid, string name) =>
        new HMProfileBuilder().Id($"regown-{pid:x4}").Name(name).Vendor("Custom")
            .Vid(0xBEEF).Pid(pid).ProductString(name).ManufacturerString("HIDMaestro")
            .Type("gamepad").Connection("usb").FromDescriptorBuilder(Canonical(16, 8, 11)).Build();

    static int LedgerHolders(string kind, string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"{LedgerRoot}\{kind}\{name}\Holders");
        return k?.GetValueNames().Length ?? -1;
    }

    static void Feed(HMController pad, HMProfile profile)
    {
        var s = new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(profile) };
        for (int i = 0; i < 10; i++) { pad.SubmitState(in s); Thread.Sleep(10); }
    }

    static void CodeBuiltPad()
    {
        Console.WriteLine("\n== Live: the GameInput table a code-built game pad holds ==");
        string key = $@"{GameInputRel}\BEEFF0A600010005";
        string foreign = $@"{GameInputRel}\BEEFF0A900010005";
        var foreignPlant = new Plant
        {
            Hive = Registry.LocalMachine, Path = foreign,
            Fill = k =>
            {
                k.SetValue("Description", "Planted vendor mapping", RegistryValueKind.String);
                using var a = k.CreateSubKey(@"Gamepad\A");
                a.SetValue("ButtonIndex", 3, RegistryValueKind.DWord);
            },
        };
        Registry.LocalMachine.DeleteSubKeyTree(key, false);
        try
        {
            PlantKey(foreignPlant, 200);
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            var a = CodeBuilt(0xF0A6, "RegOwn Pad");
            using (var pad1 = ctx.CreateController(a))
            {
                Feed(pad1, a);
                Check("while it lives, the table is under BEEF:F0A6",
                      Dump(Registry.LocalMachine, key) == GameInputMapping.TableDump(false));
                Check("the ledger records one holder", LedgerHolders("GameInput", "BEEFF0A600010005") == 1);
                using (var pad2 = ctx.CreateController(a))
                {
                    Feed(pad2, a);
                    Check("a second pad of the identity adds a holder", LedgerHolders("GameInput", "BEEFF0A600010005") == 2);
                }
                Check("the table stays while the first pad lives",
                      Dump(Registry.LocalMachine, key) == GameInputMapping.TableDump(false));
            }
            Check("the table is gone once the last pad is gone", Dump(Registry.LocalMachine, key) == null);
            Check("and so is its ledger entry", LedgerHolders("GameInput", "BEEFF0A600010005") == -1);
            using (var root = Registry.LocalMachine.OpenSubKey(LedgerRoot))
                Check("with nothing held, the ledger leaves no key behind", root == null);

            var b = CodeBuilt(0xF0A9, "RegOwn Foreign");
            using (var pad = ctx.CreateController(b))
            {
                Feed(pad, b);
                Check("a vendor's mapping under the identity is left alone", Dump(Registry.LocalMachine, foreign) == foreignPlant.Before);
                Check("and the ledger takes no hold on it", LedgerHolders("GameInput", "BEEFF0A900010005") == -1);
            }
            Check("the vendor's mapping is as planted after dispose", Dump(Registry.LocalMachine, foreign) == foreignPlant.Before);

            // Windows ships a mapping for 054C:05C4. A code-built pad under that
            // identity must not shadow it from SYSTEM.
            string ds4Key = $@"{GameInputRel}\054C05C400010005";
            Registry.LocalMachine.DeleteSubKeyTree(ds4Key, false);
            var c = new HMProfileBuilder().Id("regown-ds4id").Vid(0x054C).Pid(0x05C4).ProductString("RegOwn DS4 id")
                .FromDescriptorBuilder(Canonical(16, 8, 11)).Build();
            if (GameInputMapping.WindowsHasMapping(0x054C, 0x05C4))
            {
                using var pad = ctx.CreateController(c);
                Feed(pad, c);
                Check("a code-built pad does not shadow Windows' own mapping (054C:05C4)", Dump(Registry.LocalMachine, ds4Key) == null);
            }
            else
                Console.WriteLine("  [SKIP] this Windows build ships no mapping for 054C:05C4");
        }
        finally
        {
            try { Unplant(foreignPlant); } catch { }
            Registry.LocalMachine.DeleteSubKeyTree(key, false);
        }
    }

    static void OverrideInterplay()
    {
        Console.WriteLine("\n== Live: HMOemNameOverride and a live virtual of the same identity, every order ==");
        const ushort pid = 0xF0AA;
        string vidPid = $"VID_BEEF&PID_{pid:X4}";
        var plant = new Plant
        {
            Hive = Registry.LocalMachine, Path = $@"{OemRel}\{vidPid}",
            Fill = k =>
            {
                k.SetValue("OEMName", "Planted F0AA", RegistryValueKind.String);
                k.SetValue("Flags2", new byte[] { 0x00, 0x00, 0x08, 0x00 }, RegistryValueKind.Binary);
            },
        };
        string hkcu = $@"{OemRel}\{vidPid}";
        try
        {
            PlantKey(plant, 300);
            Registry.CurrentUser.DeleteSubKeyTree(hkcu, false);
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            var p = CodeBuilt(pid, "RegOwn Label");

            var orders = new (string Name, string[] Steps)[]
            {
                ("create, set, dispose, clear", new[] { "create", "set", "dispose", "clear" }),
                ("create, set, clear, dispose", new[] { "create", "set", "clear", "dispose" }),
                ("set, create, dispose, clear", new[] { "set", "create", "dispose", "clear" }),
                ("set, create, clear, dispose", new[] { "set", "create", "clear", "dispose" }),
            };
            foreach (var (name, steps) in orders)
            {
                HMController? pad = null;
                foreach (var step in steps)
                {
                    switch (step)
                    {
                        case "create": pad = ctx.CreateController(p); Feed(pad, p); break;
                        case "dispose": pad!.Dispose(); pad = null; break;
                        case "set": HMOemNameOverride.Set(0xBEEF, pid, "Override F0AA"); break;
                        case "clear": HMOemNameOverride.Clear(0xBEEF, pid); break;
                    }
                }
                string? after = Dump(plant.Hive, plant.Path);
                Check($"{name}: the HKLM OEM key ends as planted", after == plant.Before,
                      after == null ? "the key is gone" : after == plant.Before ? "" : after.Replace("\n", " / ").Trim());
                Check($"{name}: no HKCU key is left", Dump(Registry.CurrentUser, hkcu) == null);
                Check($"{name}: the ledger has no entry for it", LedgerHolders("JoystickOem", vidPid) == -1);
            }
        }
        finally
        {
            try { Unplant(plant); } catch { }
            try { HMOemNameOverride.Clear(0xBEEF, pid); } catch { }
            Registry.CurrentUser.DeleteSubKeyTree(hkcu, false);
        }
    }

    /// <summary>A per-user joy.cpl name an earlier release wrote. Windows
    /// copies HKLM's OEMName into the user's key the first time Game
    /// Controllers shows a device the key names nothing for, and reads that
    /// copy ahead of HKLM, so the create of a profile whose
    /// replacesJoystickNames holds the copy's name deletes it, the OEMName
    /// value alone. In the same window a user's name the list does not hold
    /// stays, and so does a listed name an HMOemNameOverride holds, which
    /// the override's record restores. Every key is a probe identity's
    /// (BEEF), planted after a backup and put back in the finally, and the
    /// pads' descriptor is one the GameInput table does not take, so the run
    /// writes no GameInput key.</summary>
    static void ReplacedJoystickNames()
    {
        Console.WriteLine("\n== Live: a per-user joy.cpl name an earlier release wrote goes at the create ==");
        const ushort pid = 0xF0AB, controlPid = 0xF0AC, overridePid = 0xF0AD;
        const string earlier = "RegOwn Earlier Name", current = "RegOwn Current Name";
        static string Key(ushort p) => $@"{OemRel}\VID_BEEF&PID_{p:X4}";
        static void Data(RegistryKey k) =>
            k.SetValue("OEMData", new byte[] { 0x03, 0x00, 0x08, 0x10, 0x10, 0x00, 0x00, 0x00 }, RegistryValueKind.Binary);
        var replaced = new Plant
        {
            Hive = Registry.CurrentUser, Path = Key(pid),
            Fill = k => { k.SetValue("OEMName", earlier, RegistryValueKind.String); Data(k); },
        };
        var users = new Plant
        {
            Hive = Registry.CurrentUser, Path = Key(controlPid),
            Fill = k => k.SetValue("OEMName", "RegOwn A Name Of The User's", RegistryValueKind.String),
        };
        static HMProfile Pad(ushort p) =>
            new HMProfileBuilder().Id($"regown-{p:x4}").Name("RegOwn Renamed").Vendor("Custom")
                .Vid(0xBEEF).Pid(p).ProductString("RegOwn Renamed").ManufacturerString("HIDMaestro")
                .JoystickName(current).ReplacesJoystickNames(new[] { "RegOwn Older Name", earlier })
                .Type("gamepad").Connection("usb").FromDescriptorBuilder(Canonical(16, 8, 8)).Build();
        static string? Name(RegistryKey hive, string path)
        {
            using var k = hive.OpenSubKey(path);
            return k?.GetValue("OEMName") as string;
        }
        try
        {
            // The create changes this plant and puts nothing back, so a run
            // stopped after it left the key without its OEMName, or with the
            // current name Windows copied in. Either is this run's own
            // leftover and goes, rather than being kept as an original.
            DropLeftover(Registry.CurrentUser, replaced.Path, Data, k => { k.SetValue("OEMName", current, RegistryValueKind.String); Data(k); });
            PlantKey(replaced, 500);
            PlantKey(users, 501);
            Registry.CurrentUser.DeleteSubKeyTree(Key(overridePid), false);
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            var a = Pad(pid);
            var b = Pad(controlPid);
            using (var pad = ctx.CreateController(a))
            using (var control = ctx.CreateController(b))
            {
                Feed(pad, a);
                Feed(control, b);
                // The create deleted the value before it returned. Windows
                // may copy the current HKLM name in again at any time after.
                string? left = Name(Registry.CurrentUser, replaced.Path);
                using (var k = Registry.CurrentUser.OpenSubKey(replaced.Path))
                {
                    Check("the user's OEMName that is a replaced name is gone", k != null && (left == null || left == current), left ?? "(no value)");
                    Check("and the key's other values stay", k?.GetValue("OEMData") is byte[] d && d.Length == 8 && d[4] == 0x10);
                }
                Check("HKLM OEMName is the profile's joystickName", Name(Registry.LocalMachine, replaced.Path) == current,
                      Name(Registry.LocalMachine, replaced.Path) ?? "(no value)");
                Check("a user's OEMName the list does not hold stays", Dump(users.Hive, users.Path) == users.Before,
                      Dump(users.Hive, users.Path)?.Replace("\n", " / ").Trim() ?? "the key is gone");
            }
            Check("after the dispose the replaced name is not put back", Name(Registry.CurrentUser, replaced.Path) != earlier,
                  Name(Registry.CurrentUser, replaced.Path) ?? "(no value)");
            Check("the ledger has no entry for the identity", LedgerHolders("JoystickOem", $"VID_BEEF&PID_{pid:X4}") == -1);

            HMOemNameOverride.Set(0xBEEF, overridePid, earlier);
            var c = Pad(overridePid);
            using (var pad = ctx.CreateController(c))
            {
                Feed(pad, c);
                Check("a listed name an HMOemNameOverride holds stays", Name(Registry.CurrentUser, Key(overridePid)) == earlier,
                      Name(Registry.CurrentUser, Key(overridePid)) ?? "(no value)");
            }
            HMOemNameOverride.Clear(0xBEEF, overridePid);
            Check("the override's clear leaves no user key, as there was none", Dump(Registry.CurrentUser, Key(overridePid)) == null);
        }
        finally
        {
            try { HMOemNameOverride.Clear(0xBEEF, overridePid); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(Key(overridePid), false); } catch { }
            try { Unplant(replaced); } catch { }
            try { Unplant(users); } catch { }
        }
    }

    /// <summary>Deletes the key when it reads exactly as one of
    /// <paramref name="states"/>, the states a run's plant is left in once
    /// the run changed it, each built in a scratch key and compared as
    /// <see cref="PlantKey"/> compares a plant.</summary>
    static void DropLeftover(RegistryKey hive, string path, params Action<RegistryKey>[] states)
    {
        string? now = Dump(hive, path);
        if (now == null) return;
        string scratch = $@"{BackupRoot}\scratch";
        try
        {
            foreach (var fill in states)
            {
                Registry.LocalMachine.DeleteSubKeyTree(scratch, false);
                using (var s = Registry.LocalMachine.CreateSubKey(scratch)) fill(s);
                if (Dump(Registry.LocalMachine, scratch) != now) continue;
                hive.DeleteSubKeyTree(path, false);
                return;
            }
        }
        finally
        {
            Registry.LocalMachine.DeleteSubKeyTree(scratch, false);
            DropEmptyBackupArea();
        }
    }

    static void CrashRecovery()
    {
        Console.WriteLine("\n== Live: a consumer that dies with its virtual alive ==");
        var plant = new Plant
        {
            Hive = Registry.LocalMachine, Path = $@"{OemRel}\VID_054C&PID_0CE6",
            Fill = k => k.SetValue("OEMName", "Planted before the crash", RegistryValueKind.String),
        };
        try
        {
            PlantKey(plant, 400);
            string self = Environment.ProcessPath!;
            var holder = Process.Start(new ProcessStartInfo(self, "--child-hold dualsense")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            })!;
            string? line;
            var sw = Stopwatch.StartNew();
            while ((line = holder.StandardOutput.ReadLine()) != null && line != "READY" && sw.ElapsedMilliseconds < 60000) { }
            using (var k = Registry.LocalMachine.OpenSubKey(plant.Path))
                Check("the child's virtual put its name in the shared key", (k?.GetValue("OEMName") as string) == "DualSense Wireless Controller");
            holder.Kill();
            holder.WaitForExit(10000);
            Console.WriteLine("  the child holding the DualSense was killed");

            // A second consumer's first create prunes holders whose process is
            // gone, without the full cleanup.
            var other = Process.Start(new ProcessStartInfo(self, "--child-create logitech-f310")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            })!;
            other.StandardOutput.ReadToEnd();
            other.WaitForExit(60000);
            Check("the next consumer's first create put the key back", Dump(plant.Hive, plant.Path) == plant.Before,
                  Dump(plant.Hive, plant.Path)?.Replace("\n", " / ").Trim() ?? "the key is gone");
            Check("and dropped the dead holder's ledger entry", LedgerHolders("JoystickOem", "VID_054C&PID_0CE6") == -1);
        }
        finally
        {
            // The killed child's DualSense goes. The driver stays installed,
            // so the next program's InstallDriver takes its fast path.
            HMContext.RemoveAllVirtualControllers(preserveInstall: true);
            try { Unplant(plant); } catch { }
        }
    }

    static int ChildHold(string id)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var p = ctx.GetProfile(id)!;
        var pad = ctx.CreateController(p);
        Feed(pad, p);
        Console.WriteLine("READY");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    static int ChildCreate(string id)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var p = ctx.GetProfile(id)!;
        using (var pad = ctx.CreateController(p)) Feed(pad, p);
        Console.WriteLine("DONE");
        return 0;
    }
}
