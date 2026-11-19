// The two items a USB/IP persona leaves on the machine (2.3.0): the usbflags
// key of its identity, where Windows keeps the device's answer to string
// 0xEE, and the device records Windows keeps after the device unplugs.
//
// Offline: the usbflags claim, release and restore in scratch keys of HKCU,
// and the order of a record walk. Read only on this machine: the walk of
// the device tree by the parent Windows keeps for each record.
//
// "--usb-ledger" (live, elevated, a test machine under its bench lock, no
// other HIDMaestro program running) runs:
//   R13: one not-present USB record of a USB/IP persona, which no owner
//        record names, removed through the SDK's removal alone, with the
//        host controller, its root hub and every other record read before
//        and after, then the identity created again on USB/IP and compared
//        with the record it replaced, disposed, which leaves the records,
//        created again on the same USB instance and HID child (R20), and
//        removed by RemoveAllVirtualControllers(Everything).
//   R19: the usbflags key of a derived identity across a create and a
//        dispose, for an identity with no key and one with planted values.
//   A program killed with a USB/IP controller alive: the next program's
//        first create puts usbflags back and leaves the records, which the
//        ledger names, and RemoveAllVirtualControllers(Everything) removes
//        them.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;
using Microsoft.Win32;

internal static partial class Program
{
    const string ScratchRoot = @"SOFTWARE\HIDMaestroProbeScratch\usb_ledger";
    const string UsbFlagsRel = @"SYSTEM\CurrentControlSet\Control\usbflags";

    static void UsbLedgerOffline()
    {
        Console.WriteLine("\n== Offline: the usbflags claim, its release and its restore ==");
        var hive = Registry.CurrentUser;
        string ledger = ScratchRoot + @"\ledger", flags = ScratchRoot + @"\usbflags";
        Func<Action> noLock = () => () => { };
        void Reset() => hive.DeleteSubKeyTree(ScratchRoot, false);
        void WindowsWrites(string name)
        {
            using var k = hive.CreateSubKey($@"{flags}\{name}");
            k.SetValue("osvc", new byte[] { 0x00, 0x00 }, RegistryValueKind.Binary);
            k.SetValue("NewInterfaceUsage", 0, RegistryValueKind.DWord);
        }
        int Entries()
        {
            using var k = hive.OpenSubKey($@"{ledger}\UsbFlags");
            return k?.SubKeyCount ?? 0;
        }
        try
        {
            // An identity Windows has no key for.
            Reset();
            RegistryLedger.ClaimUsbFlagsIn(hive, ledger, flags, "054C09CC0100", "1-A-0", noLock);
            WindowsWrites("054C09CC0100");
            RegistryLedger.ReleaseUsbFlagsIn(hive, ledger, flags, "1-A-0");
            Check("a key that was absent is absent after the last holder goes", Dump(hive, $@"{flags}\054C09CC0100") == null);
            Check("and its ledger entry is gone", Entries() == 0);

            // An identity with values a real device left, of three types.
            Reset();
            using (var k = hive.CreateSubKey($@"{flags}\057E20690101"))
            {
                k.SetValue("osvc", new byte[] { 0x01, 0x01 }, RegistryValueKind.Binary);
                k.SetValue("SkipContainerIdQuery", new byte[] { 0x01, 0x00 }, RegistryValueKind.Binary);
                k.SetValue("ResetOnResume", 1, RegistryValueKind.DWord);
            }
            string? planted = Dump(hive, $@"{flags}\057E20690101");
            RegistryLedger.ClaimUsbFlagsIn(hive, ledger, flags, "057E20690101", "1-A-0", noLock);
            WindowsWrites("057E20690101");
            Check("the virtual's answer replaced the planted one while it held the key",
                  Dump(hive, $@"{flags}\057E20690101") != planted);
            RegistryLedger.ReleaseUsbFlagsIn(hive, ledger, flags, "1-A-0");
            string? after = Dump(hive, $@"{flags}\057E20690101");
            Check("each prior value comes back byte for byte with its type, and the new one goes", after == planted,
                  after?.Replace("\n", " / ").Trim() ?? "the key is gone");

            // Two holders of one identity: the key stays theirs until both go.
            Reset();
            RegistryLedger.ClaimUsbFlagsIn(hive, ledger, flags, "054C0CE60100", "1-A-0", noLock);
            WindowsWrites("054C0CE60100");
            RegistryLedger.ClaimUsbFlagsIn(hive, ledger, flags, "054C0CE60100", "2-B-0", noLock);
            RegistryLedger.ReleaseUsbFlagsIn(hive, ledger, flags, "1-A-0");
            Check("with one of two holders gone, the key stays as the device left it",
                  Dump(hive, $@"{flags}\054C0CE60100") != null && Entries() == 1);
            RegistryLedger.ReleaseUsbFlagsIn(hive, ledger, flags, "2-B-0");
            Check("the second claim kept the first one's record: absent again after both", Dump(hive, $@"{flags}\054C0CE60100") == null);

            // A key that was absent and gained a subkey: the values go, the
            // subkey and so the key stay.
            Reset();
            RegistryLedger.ClaimUsbFlagsIn(hive, ledger, flags, "28DE12050200", "1-A-0", noLock);
            WindowsWrites("28DE12050200");
            using (hive.CreateSubKey($@"{flags}\28DE12050200\Other")) { }
            RegistryLedger.ReleaseUsbFlagsIn(hive, ledger, flags, "1-A-0");
            using (var k = hive.OpenSubKey($@"{flags}\28DE12050200"))
                Check("an absent key that gained a subkey keeps the subkey and loses the values",
                      k != null && k.ValueCount == 0 && k.SubKeyCount == 1);
            Check("the usbflags name is VID, PID and bcdDevice as twelve hex digits",
                  RegistryLedger.UsbFlagsName(0x057E, 0x0337, 0x0100) == "057E03370100");
        }
        finally { Reset(); }

        Console.WriteLine("\n== Offline: which records a removal reaches ==");
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [@"USB\VID_054C&PID_0CE6\HM0000000000A1"] = new[] { @"USB\VID_054C&PID_0CE6&MI_00\3&1&0&0000", @"USB\VID_054C&PID_0CE6&MI_03\3&1&0&0003" },
            [@"USB\VID_054C&PID_0CE6&MI_03\3&1&0&0003"] = new[] { @"HID\VID_054C&PID_0CE6&MI_03\4&2&0&0000" },
            [@"USB\VID_054C&PID_0CE6&MI_00\3&1&0&0000"] = new[] { @"SWD\MMDEVAPI\{0.0.0.00000000}.{1}", @"USB\VID_054C&PID_0CE6\HM0000000000A1" },
        };
        var order = UsbRecords.SubtreeOf(@"USB\VID_054C&PID_0CE6\HM0000000000A1",
            p => map.TryGetValue(p, out var c) ? c : Array.Empty<string>());
        Check("the walk lists the instance, then each record after its parent, each once",
              order.Count == 5 && order[0].EndsWith("HM0000000000A1") && order.IndexOf(@"HID\VID_054C&PID_0CE6&MI_03\4&2&0&0000") > order.IndexOf(@"USB\VID_054C&PID_0CE6&MI_03\3&1&0&0003"),
              string.Join(" ; ", order));
        Check("a USB device instance is one the removal takes", UsbRecords.IsUsbInstance(@"USB\VID_054C&PID_09CC\HM0123456789AB"));
        Check("a port-named one too", UsbRecords.IsUsbInstance(@"USB\VID_057E&PID_0337\5&1a2b3c4d&0&2"));
        foreach (string not in new[] { @"USB\ROOT_HUB30\4&2d6b7e3&0&0", @"ROOT\USB\0000", @"HID\VID_054C&PID_09CC\3&1&0&0000", @"USB\VID_054C&PID_09CC", @"SWD\MMDEVAPI\x" })
            Check($"{not} is not one it takes", !UsbRecords.IsUsbInstance(not));

        // Read only: the walk on this machine. Every record Windows knows a
        // parent for is in the map, and a record is listed after its parent.
        var sw = Stopwatch.StartNew();
        var all = UsbRecords.AllDeviceIds();
        var tree = UsbRecords.Tree.Read();
        long ms = sw.ElapsedMilliseconds;
        Console.WriteLine($"  this machine: {all.Count} device records, the parent walk took {ms} ms");
        // Positive control: every record SetupAPI lists as not present, an
        // independent read of the same store, is in the device id list.
        var phantoms = NonPresentBySetupApi();
        if (phantoms.Count == 0)
            Console.WriteLine("  [SKIP] SetupAPI lists no record of a device that is not present on this machine");
        else
            Check("the device id list holds every record SetupAPI lists as not present",
                  phantoms.All(id => all.Contains(id, StringComparer.OrdinalIgnoreCase)),
                  $"{phantoms.Count} not present, {phantoms.Count(id => !all.Contains(id, StringComparer.OrdinalIgnoreCase))} missing");
        string? hub = all.FirstOrDefault(id => id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase) && UsbRecords.Present(id));
        if (hub != null)
        {
            var below = tree.Subtree(hub);
            Check($"a present root hub's walk reaches what is plugged into it ({hub})",
                  below.Count >= 1 && below.Skip(1).All(id => UsbRecords.ParentOf(id) is { } p && below.Contains(p, StringComparer.OrdinalIgnoreCase)),
                  $"{below.Count - 1} below it");
        }
    }

    // ── Live ─────────────────────────────────────────────────────────────

    /// <summary>Everything about the host controller and its root hub that a
    /// removal must leave alone: their status, problem, driver and
    /// children, and the names of every USB and HID record on the
    /// machine.</summary>
    sealed record MachineView(string HostController, HashSet<string> Records);

    static MachineView ReadMachine()
    {
        var sb = new StringBuilder();
        foreach (string id in UsbRecords.AllDeviceIds().Where(i => i.StartsWith(@"ROOT\USB\", StringComparison.OrdinalIgnoreCase)
                                                                  || IsUsbipRootHub(i)).OrderBy(i => i))
        {
            sb.Append(id).Append(" present=").Append(UsbRecords.Present(id))
              .Append(" status=").Append(DevnodeStatus(id)).Append(" service=").Append(EnumValue(id, "Service"))
              .Append(" driver=").Append(EnumValue(id, "Driver")).Append('\n');
            if (UsbRecords.Present(id))
                foreach (string child in UsbRecords.Tree.Read().Subtree(id).Skip(1).Where(UsbRecords.Present).OrderBy(c => c))
                    sb.Append("  present child ").Append(child).Append(' ').Append(DevnodeStatus(child)).Append('\n');
        }
        var records = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string e in new[] { "USB", "HID", @"SWD\MMDEVAPI" })
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{e}");
            if (k == null) continue;
            foreach (string dev in k.GetSubKeyNames())
            {
                using var d = k.OpenSubKey(dev);
                if (d == null) continue;
                foreach (string inst in d.GetSubKeyNames()) records.Add($@"{e}\{dev}\{inst}");
            }
        }
        return new MachineView(sb.ToString(), records);
    }

    static bool IsUsbipRootHub(string id) =>
        id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase)
        && UsbRecords.ParentOf(id) is { } p && p.StartsWith(@"ROOT\USB\", StringComparison.OrdinalIgnoreCase);

    static string EnumValue(string id, string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{id}");
        return k?.GetValue(name) switch { string[] a => string.Join("|", a), null => "-", var v => v.ToString() ?? "" };
    }

    static string DevnodeStatus(string id)
    {
        if (CM_Locate_DevNodeW(out uint dev, id, 0) != 0) return "not-present";
        return CM_Get_DevNode_Status(out uint status, out uint problem, dev, 0) == 0 ? $"0x{status:X8}/p{problem}" : "unreadable";
    }

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll")]
    static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    /// <summary>The fields of a record that say how Windows enumerated it.</summary>
    static string Shape(string id) =>
        string.Join(" ; ", new[] { "HardwareID", "CompatibleIDs", "Service", "ClassGUID", "DeviceDesc", "Mfg" }
            .Select(n => $"{n}={EnumValue(id, n)}"));

    static string[] OwnerNamed() =>
        MachineClaims.ReadAll().SelectMany(r => r.Controllers).Select(c => c.UsbInstanceId).OfType<string>().ToArray();

    static bool LedgerNames(string id) => UsbRecords.Caused().Contains(id, StringComparer.OrdinalIgnoreCase);

    static bool args13Only;

    static bool UsbLedgerLive(bool r13Only = false)
    {
        Console.WriteLine("\n== Live: the records and the usbflags key a USB/IP persona leaves ==");
        string? consumer = FindLiveSdkConsumer();
        if (consumer != null)
        {
            Console.WriteLine($"  [SKIP] {consumer} is running and shares the SDK's devices.");
            return false;
        }
        args13Only = r13Only;
        PositiveControl("before");
        int failedBefore = s_failures;
        R13();
        // Nothing else removes a record before R13 has shown one removal is
        // safe on this machine.
        if (s_failures != failedBefore || args13Only) return true;
        R19();
        DeadProgram(everything: false);
        DeadProgram(everything: true);
        ForeignRecord();
        PortNamed();
        AfterRestart();
        PositiveControl("after");
        return true;
    }

    /// <summary>The same window's positive control: a dualsense-composite,
    /// an authored USB/IP persona, enumerates with its HID collection and
    /// leaves the device tree at its dispose.</summary>
    static void PositiveControl(string when)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var prof = ctx.GetProfile("dualsense-composite")!;
        string? id;
        bool hid;
        using (var pad = ctx.CreateController(prof, "ledger-control", HMBackend.Usbip))
        {
            Feed(pad, prof);
            id = pad.UsbipHandle!.Device.Descriptors.UsbInstanceId;
            var wait = Stopwatch.StartNew();
            hid = false;
            while (!hid && wait.ElapsedMilliseconds < 20000)
            {
                hid = id != null && UsbRecords.Tree.Read().Subtree(id).Any(r => r.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase) && UsbRecords.Present(r));
                if (!hid) Thread.Sleep(100);
            }
        }
        Check($"positive control {when}: dualsense-composite enumerated with its HID collection", id != null && hid, id ?? "no instance id");
        var gone = Stopwatch.StartNew();
        while (id != null && UsbRecords.Present(id) && gone.ElapsedMilliseconds < 10000) Thread.Sleep(50);
        Check($"positive control {when}: and left the device tree at its dispose", id != null && !UsbRecords.Present(id));
    }

    /// <summary>R13. A child program makes the record: it creates a USB/IP
    /// controller under a key of its own and is killed, so the record stays
    /// with no live program, as a crash leaves it. Then, with no HIDMaestro
    /// program running, the SDK's removal takes that one record, and the
    /// identity is created again under the same key.</summary>
    static void R13()
    {
        Console.WriteLine("\n-- R13: one owned not-present USB record removed, the host controller untouched, the identity again --");
        const string profileId = "dualshock-4-v2", key = "ledger-r13";
        string self = Environment.ProcessPath!;
        var child = Process.Start(new ProcessStartInfo(self, $"--child-hold-usbip {profileId} {key}")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        string? line, id = null;
        var wait = Stopwatch.StartNew();
        while ((line = child.StandardOutput.ReadLine()) != null && wait.ElapsedMilliseconds < 120000)
        {
            if (line.StartsWith("ID ")) id = line[3..];
            else if (line == "READY") break;
        }
        if (id == null)
        {
            Check("R13: the child created its controller", false, line ?? "no output");
            try { child.Kill(); } catch { }
            return;
        }
        child.Kill();
        child.WaitForExit(10000);
        wait.Restart();
        while (UsbRecords.Present(id) && wait.ElapsedMilliseconds < 10000) Thread.Sleep(50);
        Check("R13: the killed child's device left the device tree and its record stayed", !UsbRecords.Present(id) && UsbRecords.Exists(id));
        Check("R13: no HIDMaestro program runs", FindLiveSdkConsumer() == null);
        Console.WriteLine($"  the record: {id} ({profileId}, key {key})");
        var subtree = UsbRecords.Tree.Read().Subtree(id);
        var shapes = subtree.ToDictionary(r => r, Shape, StringComparer.OrdinalIgnoreCase);
        foreach (string r in subtree) Console.WriteLine($"    below or at it: {r}");
        var before = ReadMachine();
        Console.Write(before.HostController.Length > 0 ? "  host controller before:\n" + Indent(before.HostController) : "  no host controller listed\n");

        // The device arrived since Windows started, so the SDK's removal
        // keeps its records until a restart: the host controller's
        // since-boot test reads them.
        var kept = UsbRecords.Remove(id, 0);
        Check("R13: the SDK keeps the record of a device that arrived since Windows started", kept.Outcome == UsbRecords.Outcome.SinceBoot
              && kept.Removed.Count == 0 && UsbRecords.Exists(id), $"{kept.Outcome}, {kept.Detail}");
        // The removal itself runs on this record only while another record
        // of a device that arrived since Windows started stays below the host
        // controller, so its test reads the same after.
        var hubKids = UsbRecords.AllDeviceIds().Where(IsUsbipRootHub).SelectMany(h => UsbRecords.Tree.Read().Subtree(h).Skip(1)).ToList();
        string? witness = hubKids.FirstOrDefault(r => !subtree.Contains(r, StringComparer.OrdinalIgnoreCase)
                                                      && !UsbRecords.Present(r) && UsbRecords.ArrivedSinceBoot(r));
        if (witness == null)
        {
            Console.WriteLine("  [SKIP] R13's removal: no other record of a device that arrived since Windows started is on the host controller");
            return;
        }
        Console.WriteLine($"  another record of a device that arrived since Windows started stays: {witness}");
        var sw = Stopwatch.StartNew();
        var result = UsbRecords.Remove(id, 0, null, sinceBoot: true);
        Console.WriteLine($"  removal: {result.Outcome}, {result.Detail}, {sw.ElapsedMilliseconds} ms");
        Check("R13: the removal reports every record of the instance removed", result.Outcome == UsbRecords.Outcome.Removed
              && result.Removed.Count == subtree.Count, $"{result.Removed.Count} of {subtree.Count}");
        var after = ReadMachine();
        Check("R13: the host controller, its root hub and their present children read the same",
              after.HostController == before.HostController, after.HostController == before.HostController ? "" : Indent(after.HostController));
        var gone = before.Records.Except(after.Records, StringComparer.OrdinalIgnoreCase).ToList();
        var added = after.Records.Except(before.Records, StringComparer.OrdinalIgnoreCase).ToList();
        var expectGone = subtree.Where(r => r.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)
                                         || r.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)
                                         || r.StartsWith(@"SWD\MMDEVAPI\", StringComparison.OrdinalIgnoreCase)).ToList();
        Check("R13: exactly the instance's own records left the registry, no other record changed",
              gone.Count == expectGone.Count && expectGone.All(r => gone.Contains(r, StringComparer.OrdinalIgnoreCase)) && added.Count == 0,
              $"gone {gone.Count} (expected {expectGone.Count}), added {added.Count}");
        foreach (string r in subtree) Check($"R13: Windows has no record of {r}", !UsbRecords.Exists(r));
        Check("R13: the host controller still reads as used since Windows started",
              UsbipDriverInstaller.UsedSinceBoot(out string why), why);

        // The identity again, under the child's key, whose serial names the
        // same USB instance. A record below it is named by the prefix Windows
        // gives its new parent record, so those are matched by device id.
        static string DeviceIdOf(string r) => r[..r.LastIndexOf('\\')];
        List<string> again;
        using (var ctx = new HMContext())
        {
            ctx.LoadDefaultProfiles();
            var prof = ctx.GetProfile(profileId)!;
            sw.Restart();
            using (var pad = ctx.CreateController(prof, key, HMBackend.Usbip))
            {
                Console.WriteLine($"  created {profileId} on USB/IP under {key} in {sw.ElapsedMilliseconds} ms");
                Feed(pad, prof);
                Check("R13: the identity comes back on the same USB instance, present", UsbRecords.Present(id));
                again = UsbRecords.Tree.Read().Subtree(id);
                Check("R13: with records of the same devices below it",
                      again.Select(DeviceIdOf).OrderBy(x => x).SequenceEqual(subtree.Select(DeviceIdOf).OrderBy(x => x), StringComparer.OrdinalIgnoreCase),
                      string.Join(" ; ", again));
                foreach (string r in subtree)
                {
                    string? now = again.FirstOrDefault(a => string.Equals(DeviceIdOf(a), DeviceIdOf(r), StringComparison.OrdinalIgnoreCase));
                    Check($"R13: {DeviceIdOf(r)} enumerates as before", now != null && Shape(now) == shapes[r],
                          now == null ? "missing" : Shape(now) == shapes[r] ? (now.Equals(r, StringComparison.OrdinalIgnoreCase) ? "same instance id" : $"instance {now}") : Shape(now));
                }
                var entry = MachineClaims.ReadAll().First(x => x.Self).Controllers.First();
                Check("R13: the owner record names the instance", string.Equals(entry.UsbInstanceId, id, StringComparison.OrdinalIgnoreCase));
                Check("R13: the ledger of records HIDMaestro caused names it, since the create found none", LedgerNames(id));
            }
            foreach (string r in again) Check($"R13: the dispose leaves {r} for the identity's next life", UsbRecords.Exists(r) && !UsbRecords.Present(r));
            Check("R13: and the ledger still names it", LedgerNames(id));

            // R20 at this key: the next life has the same USB instance and
            // the same HID child, so the same HID interface path.
            using (var pad = ctx.CreateController(prof, key, HMBackend.Usbip))
            {
                Feed(pad, prof);
                var third = UsbRecords.Tree.Read().Subtree(id);
                Check("R20: the next life is on the same USB instance and the same records below it",
                      UsbRecords.Present(id) && third.OrderBy(x => x).SequenceEqual(again.OrderBy(x => x), StringComparer.OrdinalIgnoreCase),
                      string.Join(" ; ", third));
            }
        }

        // The full removal takes every record the ledger names whose device
        // arrived before Windows started. This one arrived since, so it
        // stays with its entry until a restart.
        sw.Restart();
        HMContext.RemoveAllVirtualControllers(HMCleanupScope.Everything, preserveInstall: true);
        Console.WriteLine($"  RemoveAllVirtualControllers(Everything) in {sw.ElapsedMilliseconds} ms");
        foreach (string r in again) Check($"R13: the full removal keeps {r}, whose device arrived since Windows started", UsbRecords.Exists(r) && !UsbRecords.Present(r));
        Check("R13: and the ledger still names it for a removal after a restart", LedgerNames(id));
        var end = ReadMachine();
        Check("R13: after the full removal the host controller reads as before", end.HostController == before.HostController,
              end.HostController == before.HostController ? "" : Indent(end.HostController));
        // Only HIDMaestro's own records count, by the ledger and this run's
        // identity: a real device that arrives meanwhile is no record of the
        // run's.
        var end2 = OwnAdded(end.Records.Except(after.Records, StringComparer.OrdinalIgnoreCase).Except(again, StringComparer.OrdinalIgnoreCase),
                            r => IsOwnRecord(r, id)).ToList();
        Check("R13: no other record of the run is left", end2.Count == 0, string.Join(" ; ", end2));
    }

    static string Indent(string s) => string.Join("\n", s.TrimEnd('\n').Split('\n').Select(l => "    " + l)) + "\n";

    static string OneLine(string? dump) => dump == null ? "absent" : dump.Replace("\n", " / ").Trim();

    static string? FlagsDump(string name) => Dump(Registry.LocalMachine, $@"{UsbFlagsRel}\{name}");

    /// <summary>R19 on a derived identity: dualshock-4-v2, 054C:09CC. Its
    /// key is backed up first and put back at the end.</summary>
    static void R19()
    {
        Console.WriteLine("\n-- R19: usbflags before a create, while it lives, after its dispose --");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var prof = ctx.GetProfile("dualshock-4-v2")!;
        string name = RegistryLedger.UsbFlagsName(prof.VendorId, prof.ProductId, new UsbDescriptorSet(prof.Inner).BcdDevice);
        var plant = new Plant { Hive = Registry.LocalMachine, Path = $@"{UsbFlagsRel}\{name}" };
        try
        {
            PlantKey(plant, 500);   // backs up what is there, then an empty key
            Registry.LocalMachine.DeleteSubKeyTree(plant.Path, false);

            // No key before.
            Check($"R19 none: usbflags key {name} absent before the create", FlagsDump(name) == null);
            using (var pad = ctx.CreateController(prof, "ledger-r19-a", HMBackend.Usbip))
            {
                Feed(pad, prof);
                string? live = FlagsDump(name);
                Console.WriteLine($"  while it lives: {OneLine(live)}");
                Check("R19 none: Windows wrote its answer while the device lived", live != null && live.Contains("osvc"));
                Check("R19 none: the ledger holds the key while the device lives", LedgerHolders("UsbFlags", name) == 1);
            }
            Check("R19 none: absent again after the dispose", FlagsDump(name) == null, OneLine(FlagsDump(name)));

            // Values a real device left before. While the virtual lives, the
            // key is changed as Windows changes it for a device asked anew.
            using (var k = Registry.LocalMachine.CreateSubKey(plant.Path))
            {
                k.SetValue("osvc", new byte[] { 0x01, 0x7F }, RegistryValueKind.Binary);
                k.SetValue("SkipContainerIdQuery", new byte[] { 0x01, 0x00 }, RegistryValueKind.Binary);
                k.SetValue("HMProbeMarker", 0x51, RegistryValueKind.DWord);
            }
            string? planted = FlagsDump(name);
            using (var pad = ctx.CreateController(prof, "ledger-r19-b", HMBackend.Usbip))
            {
                Feed(pad, prof);
                Check("R19 value: the ledger holds the key while the device lives", LedgerHolders("UsbFlags", name) == 1);
                using (var k = Registry.LocalMachine.OpenSubKey(plant.Path, writable: true)!)
                {
                    k.SetValue("osvc", new byte[] { 0x00, 0x00 }, RegistryValueKind.Binary);
                    k.DeleteValue("HMProbeMarker", false);
                    k.SetValue("NewInterfaceUsage", 0, RegistryValueKind.DWord);
                }
            }
            string? back = FlagsDump(name);
            Check("R19 value: every planted value is back byte for byte with its type, the new one gone", back == planted,
                  OneLine(back));
            Check("R19: the ledger has no usbflags entry left", LedgerHolders("UsbFlags", name) == -1);
        }
        finally
        {
            try { Unplant(plant); } catch (Exception e) { Console.WriteLine($"  unplant: {e.Message}"); }
        }
    }

    /// <summary>A program killed with a USB/IP controller alive. The next
    /// program's first create, or the full removal, puts the usbflags key
    /// back, and both keep the records the dead create caused, whose device
    /// arrived since Windows started, with the ledger naming them.</summary>
    static void DeadProgram(bool everything)
    {
        string how = everything ? "RemoveAllVirtualControllers(Everything)" : "the next program's first create";
        Console.WriteLine($"\n-- A program killed with a USB/IP controller alive, then {how} --");
        string key = everything ? "ledger-dead-everything" : "ledger-dead-next";
        string flagsName;
        using (var c = new HMContext())
        {
            c.LoadDefaultProfiles();
            var p = c.GetProfile("dualshock-4-v2")!;
            flagsName = RegistryLedger.UsbFlagsName(p.VendorId, p.ProductId, new UsbDescriptorSet(p.Inner).BcdDevice);
        }
        string? flagsBefore = FlagsDump(flagsName);
        string self = Environment.ProcessPath!;
        var child = Process.Start(new ProcessStartInfo(self, $"--child-hold-usbip dualshock-4-v2 {key}")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        string? line, id = null, flags = null;
        var sw = Stopwatch.StartNew();
        while ((line = child.StandardOutput.ReadLine()) != null && sw.ElapsedMilliseconds < 120000)
        {
            if (line.StartsWith("ID ")) id = line[3..];
            else if (line.StartsWith("FLAGS ")) flags = line[6..];
            else if (line == "READY") break;
        }
        if (id == null || flags == null)
        {
            Check($"{how}: the child created its controller", false, line ?? "no output");
            try { child.Kill(); } catch { }
            return;
        }
        Check($"{how}: the child's device is the identity whose usbflags key was read before it", flags == flagsName, flags);
        var subtree = UsbRecords.Tree.Read().Subtree(id);
        child.Kill();
        child.WaitForExit(10000);
        Console.WriteLine($"  the child holding {id} was killed; {subtree.Count} record(s) at or below it");
        var until = Stopwatch.StartNew();
        while (UsbRecords.Present(id) && until.ElapsedMilliseconds < 10000) Thread.Sleep(50);
        Check($"{how}: the device left with its program", !UsbRecords.Present(id));
        Check($"{how}: its records stay", UsbRecords.Exists(id));
        Check($"{how}: the dead program's owner record names the instance",
              MachineClaims.ReadAll().Any(r => !r.Live && r.Controllers.Any(c => string.Equals(c.UsbInstanceId, id, StringComparison.OrdinalIgnoreCase))));
        Check($"{how}: the ledger names it as HIDMaestro's", LedgerNames(id));
        Console.WriteLine($"  usbflags\\{flags} after the kill: {FlagsDump(flags)?.Replace("\n", " / ").Trim() ?? "absent"} (before the child: {(flagsBefore == null ? "absent" : "present")})");

        if (everything)
            HMContext.RemoveAllVirtualControllers(HMCleanupScope.Everything, preserveInstall: true);
        else
        {
            var other = Process.Start(new ProcessStartInfo(self, "--child-create-usbip logitech-f310")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            })!;
            other.StandardOutput.ReadToEnd();
            other.WaitForExit(120000);
        }
        // The device arrived since Windows started, so both keep its records
        // until a restart, and the ledger names them for the removal after
        // it.
        foreach (string r in subtree) Check($"{how}: {r} stays, its device having arrived since Windows started", UsbRecords.Exists(r));
        Check($"{how}: the ledger still names it", LedgerNames(id));
        Check($"{how}: no owner record names it any more", !OwnerNamed().Contains(id, StringComparer.OrdinalIgnoreCase));
        Check($"{how}: the ledger has no usbflags entry for {flags}", LedgerHolders("UsbFlags", flags) == -1);
        Check($"{how}: usbflags key {flags} is as it was before the child's create", FlagsDump(flags) == flagsBefore,
              $"{OneLine(FlagsDump(flags))} against {OneLine(flagsBefore)}");
    }

    static int ChildHoldUsbip(string id, string key)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var p = ctx.GetProfile(id)!;
        var pad = ctx.CreateController(p, key, HMBackend.Usbip);
        Feed(pad, p);
        var d = pad.UsbipHandle!.Device.Descriptors;
        Console.WriteLine("ID " + d.UsbInstanceId);
        Console.WriteLine("FLAGS " + RegistryLedger.UsbFlagsName(d.VendorId, d.ProductId, d.BcdDevice));
        Console.WriteLine("READY");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    static int ChildCreateUsbip(string id)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var p = ctx.GetProfile(id)!;
        using (var pad = ctx.CreateController(p, "ledger-next-program", HMBackend.Usbip)) Feed(pad, p);
        Console.WriteLine("DONE");
        return 0;
    }
}

internal static partial class Program
{
    /// <summary>"--remove-listed &lt;file&gt;": the not-present USB records a
    /// test run left, by the instance ids its report lists, removed through
    /// the SDK's own removal (UsbRecords.Remove). A listed USB instance goes
    /// only when it is not present, no owner record of a running program
    /// names it, and every record below it is listed too. Any other record
    /// stays and is listed as kept.</summary>
    static int RemoveListed(string file)
    {
        Console.WriteLine($"=== Removing the listed USB records: {file} ===");
        if (FindLiveSdkConsumer() is { } consumer)
        {
            Console.WriteLine($"  [SKIP] {consumer} is running");
            return 2;
        }
        var listed = new HashSet<string>(System.IO.File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0),
                                         StringComparer.OrdinalIgnoreCase);
        var live = new HashSet<string>(MachineClaims.ReadAll().Where(r => r.Live).SelectMany(r => r.Controllers)
            .Select(c => c.UsbInstanceId ?? ""), StringComparer.OrdinalIgnoreCase);
        var tree = UsbRecords.Tree.Read();
        int removed = 0, kept = 0, absent = 0;
        var sw = Stopwatch.StartNew();
        foreach (string id in listed.Where(UsbRecords.IsUsbInstance).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!UsbRecords.Exists(id)) { absent++; Console.WriteLine($"  absent  {id}"); continue; }
            string? why = live.Contains(id) ? "a running program's owner record names it"
                : UsbRecords.Present(id) ? "it is present"
                : null;
            var subtree = tree.Subtree(id);
            var unlisted = subtree.Where(r => !listed.Contains(r)).ToList();
            why ??= unlisted.Count > 0 ? "records below it are not listed: " + string.Join(", ", unlisted) : null;
            if (why != null) { kept++; Console.WriteLine($"  kept    {id}: {why}"); continue; }
            var r = UsbRecords.Remove(id, 0, tree);
            if (r.Outcome == UsbRecords.Outcome.Removed)
            {
                removed++;
                foreach (string x in r.Removed) Console.WriteLine($"  removed {x}");
            }
            else { kept++; Console.WriteLine($"  kept    {id}: {r.Outcome}, {r.Detail}"); }
        }
        var left = listed.Where(UsbRecords.Exists).ToList();
        Console.WriteLine($"\n  {removed} USB instance(s) removed with every record below them, {kept} kept, {absent} already absent, in {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"  listed records still on the machine: {left.Count}");
        foreach (string x in left) Console.WriteLine($"    {x}");
        return 0;
    }

    /// <summary>"--uninstall": HIDMaestro's removal with usbip-win2 kept,
    /// after its preview, and the start type GameInputSvc had before
    /// HIDMaestro set it, read from its record first.</summary>
    static int UninstallKeepingUsbip()
    {
        Console.WriteLine("=== HIDMaestro's removal, usbip-win2 kept ===");
        if (FindLiveSdkConsumer() is { } consumer)
        {
            Console.WriteLine($"  [SKIP] {consumer} is running");
            return 2;
        }
        // A record of a device that arrived since Windows started, which the
        // ledger names: the uninstall takes it, the since-boot rule
        // notwithstanding, and decides the host controller from its one
        // reading taken before it removed anything.
        string made;
        using (var ctx = new HMContext())
        {
            ctx.LoadDefaultProfiles();
            var prof = ctx.GetProfile("dualshock-4-v2")!;
            using (var pad = ctx.CreateController(prof, "ledger-uninstall", HMBackend.Usbip))
            {
                Feed(pad, prof);
                made = pad.UsbipHandle!.Device.Descriptors.UsbInstanceId!;
            }
        }
        Check("a record the ledger names and whose device arrived since Windows started is on the machine",
              LedgerNames(made) && UsbRecords.Exists(made) && UsbRecords.ArrivedSinceBoot(made), made);
        bool usedBefore = UsbipDriverInstaller.UsedSinceBoot(out string usedWhy);
        Console.WriteLine($"  the host controller before the removal: {(usedBefore ? "used since Windows started, " + usedWhy : "not used since Windows started")}");
        object? recorded;
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroGameInputSvc")) recorded = k?.GetValue("Start");
        object? before;
        using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\GameInputSvc")) before = k?.GetValue("Start");
        Console.WriteLine($"  GameInputSvc Start {before}, recorded before HIDMaestro: {recorded ?? "(no record)"}");
        var preview = HMUninstall.Preview();
        Console.WriteLine($"  preview: {preview.Entries.Count} entries");
        foreach (var e in preview.Entries) Console.WriteLine($"    {e.Outcome} {e.Kind}: {e.Location}{(e.Reason is { Length: > 0 } r ? "  (" + r + ")" : "")}");
        var report = HMUninstall.Run();
        Console.WriteLine($"  removal: {report.Entries.Count} entries");
        foreach (var e in report.Entries) Console.WriteLine($"    {e.Outcome} {e.Kind}: {e.Location}{(e.Reason is { Length: > 0 } r ? "  (" + r + ")" : "")}");
        object? after;
        using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\GameInputSvc")) after = k?.GetValue("Start");
        Console.WriteLine($"  GameInputSvc Start after: {after}");
        if (recorded != null)
            Check("the removal put GameInputSvc's recorded start type back", Equals(after, recorded), $"{after}");
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroGameInputSvc"))
            Check("and its record is gone", k == null);
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroRegistryLedger"))
            Check("the registry ledger is gone", k == null);
        Check("the uninstall removed the record whose device arrived since Windows started", !UsbRecords.Exists(made), made);
        using (var k = Registry.LocalMachine.OpenSubKey(UsbRecords.LedgerRoot))
            Check("and the ledger of USB records is gone", k == null, k == null ? "" : $"{k.SubKeyCount} entr(ies) left");
        var keptForRestart = report.Entries.Where(e => e.Outcome == HMUninstallOutcome.Kept
            && (e.Reason ?? "").Contains("restart", StringComparison.OrdinalIgnoreCase)
            && (e.Location.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) || e.Location.Contains("HIDMaestroUsbRecords"))).ToList();
        Check("no USB record and no ledger key is kept for a restart", keptForRestart.Count == 0,
              string.Join(" ; ", keptForRestart.Select(e => e.Location)));
        var host = report.Entries.FirstOrDefault(e => e.Kind == "devnode" && e.Location.StartsWith(@"ROOT\USB\", StringComparison.OrdinalIgnoreCase));
        if (usedBefore && host != null)
            Check("the host controller, used since Windows started by the reading taken before the removal, stays",
                  host.Outcome == HMUninstallOutcome.Kept, $"{host.Outcome}: {host.Reason}");
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }
}

internal static partial class Program
{
    static void OsBindingOffline()
    {
        Console.WriteLine("\n== Offline: which restore flags a devnode bound by Microsoft OS descriptors ==");
        Check("winusb.inf on a devnode claimed on input.inf is a binding by OS descriptors",
              WinUsbBinding.IsOsDescriptorBinding(@"C:\Windows\INF\winusb.inf", @"C:\Windows\INF\input.inf"));
        Check("winusb.inf on a devnode claimed with no driver is not", !WinUsbBinding.IsOsDescriptorBinding("winusb.inf", ""));
        Check("winusb.inf on a devnode claimed on winusb.inf is not", !WinUsbBinding.IsOsDescriptorBinding("winusb.inf", "winusb.inf"));
        Check("HIDMaestro's package on a devnode is not", !WinUsbBinding.IsOsDescriptorBinding("oem12.inf", "input.inf"));
        Check("an unreadable claim is not", !WinUsbBinding.IsOsDescriptorBinding("winusb.inf", null));
    }
}

internal static partial class Program
{
    /// <summary>The usbip-win2 removal's rule for a not-present record below
    /// the host controller: the ledger of records HIDMaestro's creates caused
    /// decides, for every record alike.</summary>
    static void PersonaRuleOffline()
    {
        Console.WriteLine("\n== Offline: the usbip-win2 removal takes a record by the ledger's proof alone ==");
        const string adapter = @"USB\VID_057E&PID_0337\15/07/2014";
        const string synthetic = @"USB\VID_054C&PID_09CC\HM0123456789AB";
        var a = UsbipRemoval.PersonaVerdict(adapter, caused: true);
        Check("a ledgered record with a serial real units share (the GameCube adapter's) goes", a.Take, a.Reason ?? "");
        var b = UsbipRemoval.PersonaVerdict(synthetic, caused: false);
        Check("a record with HIDMaestro's serial form that the ledger does not name stays", !b.Take, b.Reason ?? "taken");
        var c = UsbipRemoval.PersonaVerdict(adapter, caused: false);
        Check("the GameCube adapter's record the ledger does not name stays", !c.Take, c.Reason ?? "taken");
        var e = UsbipRemoval.PersonaVerdict(synthetic, caused: true);
        Check("a ledgered record with HIDMaestro's serial form goes", e.Take, e.Reason ?? "");
    }
}

internal static partial class Program
{
    /// <summary>The records a run added that are HIDMaestro's own. A record
    /// any other device added meanwhile is not the run's.</summary>
    static IEnumerable<string> OwnAdded(IEnumerable<string> added, Func<string, bool> isOwn) => added.Where(isOwn);

    /// <summary>A record of the run's identity or of an instance the ledger
    /// names, or below one, by the parent chain Windows keeps.</summary>
    static bool IsOwnRecord(string record, string identity)
    {
        var owned = new HashSet<string>(UsbRecords.Caused(), StringComparer.OrdinalIgnoreCase) { identity };
        for (string? r = record; r != null; r = UsbRecords.ParentOf(r))
            if (owned.Contains(r)) return true;
        return false;
    }

    static List<string> NonPresentBySetupApi()
    {
        var list = new List<string>();
        Guid none = Guid.Empty;
        IntPtr set = SetupDiGetClassDevsW(ref none, null, IntPtr.Zero, 0x4 /* DIGCF_ALLCLASSES */);
        if (set == new IntPtr(-1)) return list;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var buf = new char[512];
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                if (SetupDiGetDeviceInstanceIdW(set, ref data, buf, (uint)buf.Length, out uint n))
                {
                    string id = new string(buf, 0, (int)n - 1);
                    if (!UsbRecords.Present(id)) list.Add(id);
                }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [System.Runtime.InteropServices.DllImport("setupapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwnd, uint flags);
    [System.Runtime.InteropServices.DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
    [System.Runtime.InteropServices.DllImport("setupapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, char[] id, uint size, out uint required);
    [System.Runtime.InteropServices.DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    /// <summary>The review's findings, each against the rule it asks for.</summary>
    static void ReviewFixesOffline()
    {
        Console.WriteLine("\n== Offline: the review's fixes ==");
        // B1: a record whose device last arrived on a physical hub is not
        // HIDMaestro's to remove, whatever the ledger says.
        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"USB\VID_057E&PID_0337\15/07/2014"] = @"USB\ROOT_HUB30\4&4223c81&0&0",
            [@"USB\ROOT_HUB30\4&4223c81&0&0"] = @"PCI\VEN_8086&DEV_31A8&SUBSYS_72708086&REV_06\3&11583659&0&A0",
            [@"USB\VID_054C&PID_09CC\HM0123456789AB"] = @"USB\ROOT_HUB30\1&2b53a856&0&0",
            [@"USB\ROOT_HUB30\1&2b53a856&0&0"] = @"ROOT\USB\0000",
        };
        var hwids = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [@"PCI\VEN_8086&DEV_31A8&SUBSYS_72708086&REV_06\3&11583659&0&A0"] = new[] { @"PCI\VEN_8086&DEV_31A8&SUBSYS_72708086&REV_06" },
            [@"ROOT\USB\0000"] = new[] { @"ROOT\USBIP_WIN2\UDE", @"ROOT\HIDMAESTRO_UDE" },
        };
        string? Parent(string id) => parents.TryGetValue(id, out var p) ? p : null;
        string[] Hw(string id) => hwids.TryGetValue(id, out var h) ? h : Array.Empty<string>();
        Check("B1: a record whose device last arrived on a physical hub may not be removed",
              !UsbRecords.MayRemove(@"USB\VID_057E&PID_0337\15/07/2014", Parent, Hw));
        Check("B1: a record whose device last arrived on a usbip-win2 root hub may",
              UsbRecords.MayRemove(@"USB\VID_054C&PID_09CC\HM0123456789AB", Parent, Hw));
        Check("B1: a record with no parent Windows names may not", !UsbRecords.MayRemove(@"USB\VID_1234&PID_5678\X", Parent, Hw));

        // &MI_: an interface record is no device a create names.
        Check("an interface record (&MI_03) is not one the removal takes",
              !UsbRecords.IsUsbInstance(@"USB\VID_054C&PID_0CE6&MI_03\3&1&0&0003"));

        // usbflags: the clear keeps subkeys, which the restore never writes
        // back, and changes nothing for a holder that holds no entry.
        var hive = Registry.CurrentUser;
        string ledger = ScratchRoot + @"\ledger", flags = ScratchRoot + @"\usbflags", name = "057E03370100";
        hive.DeleteSubKeyTree(ScratchRoot, false);
        try
        {
            RegistryLedger.ClaimUsbFlagsIn(hive, ledger, flags, name, "1-A-0", () => () => { });
            using (var k = hive.CreateSubKey($@"{flags}\{name}"))
            {
                k.SetValue("osvc", new byte[] { 0x00, 0x00 }, RegistryValueKind.Binary);
                using (k.CreateSubKey("Other")) { }
            }
            Check("a holder that holds no entry clears nothing", !RegistryLedger.ClearHeldUsbFlagsIn(hive, ledger, flags, name, "9-Z-0")
                  && Dump(hive, $@"{flags}\{name}")!.Contains("osvc"));
            bool cleared = RegistryLedger.ClearHeldUsbFlagsIn(hive, ledger, flags, name, "1-A-0");
            using (var k = hive.OpenSubKey($@"{flags}\{name}"))
                Check("the clear takes the values and keeps the subkey", cleared && k != null && k.ValueCount == 0 && k.SubKeyCount == 1,
                      k == null ? "the key is gone" : $"{k.ValueCount} value(s), {k.SubKeyCount} subkey(s)");

            // A claim cut short before its marker: the restore leaves the key.
            hive.DeleteSubKeyTree(ScratchRoot, false);
            using (var k = hive.CreateSubKey($@"{flags}\{name}")) k.SetValue("osvc", new byte[] { 0x01, 0x01 }, RegistryValueKind.Binary);
            string? live = Dump(hive, $@"{flags}\{name}");
            using (var e = hive.CreateSubKey($@"{ledger}\UsbFlags\{name}"))
            using (var prior = e.CreateSubKey("Prior")) prior.SetValue("osvc", new byte[] { 0x01, 0x01 }, RegistryValueKind.Binary);
            using (var e = hive.OpenSubKey($@"{ledger}\UsbFlags\{name}"))
                RegistryLedger.RestoreUsbFlagsIn(hive, flags, name, e!);
            Check("an entry a crash left before its KeyExisted marker changes nothing at its restore",
                  Dump(hive, $@"{flags}\{name}") == live, OneLine(Dump(hive, $@"{flags}\{name}")));
        }
        finally { hive.DeleteSubKeyTree(ScratchRoot, false); }

        // R13 and uninstall_check count only HIDMaestro's own records.
        var added = new[] { @"HID\VID_054C&PID_09CC\3&1fbe0dcc&7&0000", @"USB\VID_046D&PID_C52B\5&3a&0&2" };
        var own = OwnAdded(added, r => r.Contains("VID_054C", StringComparison.OrdinalIgnoreCase)).ToList();
        Check("a real device's record added during a run is not the run's", own.Count == 1, string.Join(" ; ", own));
    }
}

internal static partial class Program
{
    /// <summary>B1 live: a record whose device last arrived on a physical
    /// hub, put in the ledger as a persona's record would be once a real
    /// device has taken its instance. The full removal's path drops the
    /// entry and leaves the record. Run only once the gate itself refuses
    /// the record, so a gate that is wrong removes nothing here.</summary>
    static void ForeignRecord()
    {
        Console.WriteLine("\n-- B1: a ledgered record a real device has taken stays --");
        string? foreign = UsbRecords.AllDeviceIds().FirstOrDefault(id => UsbRecords.IsUsbInstance(id) && UsbRecords.Exists(id)
            && !UsbRecords.Present(id) && UsbRecords.ParentOf(id) is { } hub
            && hub.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase) && !IsUsbipRootHub(hub));
        if (foreign == null)
        {
            Console.WriteLine("  [SKIP] no record of a device that is not present and last arrived on a physical hub");
            return;
        }
        Console.WriteLine($"  the record: {foreign}, last on {UsbRecords.ParentOf(foreign)}");
        bool gate = UsbRecords.MayRemove(foreign, UsbRecords.ParentOf, UsbipDriverInstaller.HardwareIdsOf);
        Check("B1: the gate refuses a record whose device last arrived on a physical hub", !gate);
        if (gate) return;
        var subtree = UsbRecords.Tree.Read().Subtree(foreign);
        UsbRecords.NoteCaused(foreign);
        var r = UsbRecords.Remove(foreign, 0, null);
        Check("B1: the removal reports it a real device's and removes nothing", r.Outcome == UsbRecords.Outcome.Foreign && r.Removed.Count == 0,
              $"{r.Outcome}, {r.Detail}");
        UsbRecords.RemoveCaused(0, sinceBoot: false);
        foreach (string x in subtree) Check($"B1: {x} is still on the machine", UsbRecords.Exists(x));
        Check("B1: the ledger names it no more", !LedgerNames(foreign));
    }

    /// <summary>The removal a restart allows, through the probe's boot-time
    /// seam: every arrival counts as before Windows started. Run only while
    /// another record of a device that arrived since Windows started, which
    /// the ledger does not name, stays below the host controller, so the
    /// host controller's own since-boot test reads the same after.</summary>
    static void AfterRestart()
    {
        Console.WriteLine("\n-- The removal a restart allows (boot-time seam) --");
        var caused = UsbRecords.Caused().Where(id => UsbRecords.Exists(id) && !UsbRecords.Present(id)).ToList();
        if (caused.Count == 0)
        {
            Console.WriteLine("  [SKIP] the ledger names no record that is not present");
            return;
        }
        var ledgered = new HashSet<string>(UsbRecords.Caused(), StringComparer.OrdinalIgnoreCase);
        var hubKids = UsbRecords.AllDeviceIds().Where(IsUsbipRootHub).SelectMany(h => UsbRecords.Tree.Read().Subtree(h).Skip(1)).ToList();
        string? witness = hubKids.FirstOrDefault(r => UsbRecords.IsUsbInstance(r) && !ledgered.Contains(r)
                                                      && !UsbRecords.Present(r) && UsbRecords.ArrivedSinceBoot(r));
        if (witness == null)
        {
            Console.WriteLine("  [SKIP] no record the ledger does not name, of a device that arrived since Windows started, stays below the host controller");
            return;
        }
        Console.WriteLine($"  {caused.Count} ledgered record(s), witness {witness}");
        var subtrees = caused.SelectMany(id => UsbRecords.Tree.Read().Subtree(id)).ToList();
        UsbRecords.BootTimeForProbe = () => DateTime.UtcNow.AddMinutes(1);
        try { HMContext.RemoveAllVirtualControllers(HMCleanupScope.Everything, preserveInstall: true); }
        finally { UsbRecords.BootTimeForProbe = null; }
        var left = subtrees.Where(UsbRecords.Exists).ToList();
        Check("after a restart, the full removal removes every record the ledger names", left.Count == 0, string.Join(" ; ", left));
        if (s_portNamed != null)
            Check("the port-named record the ledger took is among them", !UsbRecords.Exists(s_portNamed), s_portNamed);
        Check("drops every entry", UsbRecords.Caused().Count == 0, string.Join(" ; ", UsbRecords.Caused()));
        using (var k = Registry.LocalMachine.OpenSubKey(UsbRecords.LedgerRoot))
            Check("and deletes the ledger's key", k == null);
        Check("the host controller still reads as used since Windows started", UsbipDriverInstaller.UsedSinceBoot(out string why), why);
    }
}

internal static partial class Program
{
    static string? s_portNamed;

    /// <summary>Item 8: a derived device Windows names by its root hub port
    /// (usbflags IgnoreHWSerNum for its VID and PID). The create's start
    /// wait finds it on the port, and the ledger names the record it caused
    /// under that name. The serial's name has no record, and the removal
    /// drops its entry as absent.</summary>
    static void PortNamed()
    {
        Console.WriteLine("\n-- A derived device Windows names by its port --");
        const string flags = @"SYSTEM\CurrentControlSet\Control\usbflags";
        const string value = "IgnoreHWSerNum054C09CC";
        object? prior;
        using (var k = Registry.LocalMachine.OpenSubKey(flags)) prior = k?.GetValue(value);
        var known = UsbRecords.Known(0x054C, 0x09CC);
        using (var k = Registry.LocalMachine.CreateSubKey(flags))
            k.SetValue(value, new byte[] { 1 }, RegistryValueKind.Binary);
        try
        {
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            string serialId, onPort;
            using (var pad = ctx.CreateController(ctx.GetProfile("dualshock-4-v2")!, "ledger-portnamed", HMBackend.Usbip))
            {
                serialId = pad.UsbipHandle!.Device.Descriptors.UsbInstanceId!;
                onPort = VhciClient.DeviceOnRootHubPort(pad.UsbipHandle.Device.VhciPort) ?? "";
                Check("Windows named the device by its port, not its serial (positive control for the flag)",
                      onPort.Length > 0 && !string.Equals(onPort, serialId, StringComparison.OrdinalIgnoreCase), $"{serialId} on the port: {onPort}");
                if (onPort.Length == 0 || string.Equals(onPort, serialId, StringComparison.OrdinalIgnoreCase)) return;
                bool created = UsbRecords.CreatedBy(onPort, known);
                Console.WriteLine($"  {onPort}: {(created ? "new, the create caused it" : "there before the create")}");
                if (created)
                {
                    s_portNamed = onPort;
                    Check("the ledger names the port-named record the create caused", LedgerNames(onPort), onPort);
                }
                else
                    Check("the ledger does not name a port-named record that was there before the create", !LedgerNames(onPort), onPort);
                Check("the serial's name has no record", !UsbRecords.Exists(serialId), serialId);
            }
        }
        finally
        {
            using var k = Registry.LocalMachine.OpenSubKey(flags, writable: true);
            if (prior != null) k?.SetValue(value, prior, RegistryValueKind.Binary);
            else k?.DeleteValue(value, throwOnMissingValue: false);
        }
    }
}
