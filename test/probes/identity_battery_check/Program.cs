// Identity battery (issue #60). Elevation required.
//
// One controller per profile family is created, measured, destroyed and
// created again across the lives a consumer sees, and every recorded
// identity value has to come back the same:
//
//   parent instance id, ParentIdPrefix, ContainerId, HID child instance
//   ids, HID interface paths, the DirectInput instance GUID, the SDL3
//   joystick path (when the SDL3-build checkout is beside the repo), and
//   the USB serial for the USB/IP family.
//
// Lives per family: five same-process create/destroy cycles, three process
// restarts (a child process creates, records and exits), and one driver
// uninstall plus reinstall (the full sweep deletes the packages; the next
// create redeploys). A reboot cannot be run from inside a battery: the
// default run writes %TEMP%\HIDMaestro\identity_baseline.json, and
// `--compare` after a reboot creates one life per family and checks it
// against that file.
//
// After every create the empty-shell check runs: Service bound, interface
// published, input flowing (a submitted state is read back through the
// consumer-side API), output flowing (an output report or XInput rumble
// reaches OutputReceived), and for the Xbox families exactly one WGI
// Gamepad and one XInput slot more than the bench had.
//
// Two more scenarios: overlap (two pads of one VID/PID, the first removed,
// the second keeps its path and serial while its DirectInput ordinal moves
// from 1 to 0), and a profile change at the same identity (a DualShock 4
// at a DualSense's key keeps the paths and refreshes the descriptor).
//
// Families: xbox-series-xs-bt (xinputhid, SWD parent), xbox-360-wired
// (ROOT main plus SWD XUSB companion), dualsense (plain HID on ROOT), a
// generic plain-HID pad built with HMProfileBuilder at CAFE:0001 with a
// PID FFB block on a Joystick collection (the PadForge Extended shape with
// force feedback on), and dualsense-composite (USB/IP).
//
// Exit 0 PASS, 1 FAIL, 2 environment (not elevated).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;
using HIDMaestro;
using HIDMaestro.Internal;
using Windows.Gaming.Input;

internal static class Program
{
    // ── result bookkeeping ─────────────────────────────────────────────

    /// <summary>Stands in for a DirectInput instance GUID that could not be
    /// attributed to one specific pad. Compared as a value, two of these are
    /// equal, so a check that needs to tell two pads apart must reject it
    /// rather than treat it as a reading.</summary>
    const string Unattributed = "(DirectInput attributed no instance to this pad)";

    static int s_total, s_failures;

    /// <summary>Set by --overlap. Prints every device DirectInput enumerates
    /// with the path it reports, so an attribution question is answered from
    /// what DirectInput actually returned rather than from the one value that
    /// survived the search.</summary>
    static bool s_diDump;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    // ── families ───────────────────────────────────────────────────────

    sealed record Family(string Id, string Label, bool Xbox, bool Usbip, bool HidInput, byte OutputReportId, int OutputLength);

    static readonly Family[] Families =
    {
        new("xbox-series-xs-bt",  "xinputhid (SWD parent, xinputhid.sys on the child)", Xbox: true,  Usbip: false, HidInput: false, 0, 0),
        new("xbox-360-wired",     "XUSB companion (ROOT main + SWD companion)",         Xbox: true,  Usbip: false, HidInput: false, 0, 0),
        new("dualsense",          "plain HID (ROOT\\HIDClass, Sony UMDF2)",             Xbox: false, Usbip: false, HidInput: true,  0x02, 48),
        new("identity-generic",   "plain HID generic (HMProfileBuilder, CAFE:0001)",   Xbox: false, Usbip: false, HidInput: true,  0x1C, 0),
        new("dualsense-composite","USB/IP composite persona",                          Xbox: false, Usbip: true,  HidInput: true,  0x02, 48),
    };

    const string GenericId = "identity-generic";
    const ushort GenericVid = 0xCAFE, GenericPid = 0x0001;

    static HMProfile ResolveProfile(HMContext ctx, string id)
    {
        if (id != GenericId)
            return ctx.GetProfile(id) ?? throw new InvalidOperationException($"profile '{id}' missing");
        // The PadForge Extended shape: two 16-bit sticks, two 16-bit
        // triggers, a hat, eleven buttons, and the PID FFB block so the
        // output lane exists. Built identically in every process.
        var desc = new HidDescriptorBuilder()
            .Joystick()
            .AddStick("Left", 16).AddStick("Right", 16)
            .AddTrigger("Left", 16).AddTrigger("Right", 16)
            .AddHat().AddButtons(11)
            .AddPidFfbBlock()
            .Build();
        return new HMProfileBuilder()
            .Id(GenericId).Name("Identity battery generic pad").Vendor("Custom")
            .Vid(GenericVid).Pid(GenericPid)
            .ProductString("Identity Battery Game Controller").ManufacturerString("HIDMaestro")
            .Type("gamepad").Connection("usb")
            .Descriptor(desc)
            .Build();
    }

    static (ushort Vid, ushort Pid) VidPid(HMProfile p) => (p.VendorId, p.ProductId);

    // ── the record ─────────────────────────────────────────────────────

    sealed class IdentityRecord
    {
        public string Family { get; set; } = "";
        public string Life { get; set; } = "";
        public string Key { get; set; } = "";
        public string Parent { get; set; } = "";
        public string Prefix { get; set; } = "";
        public string Container { get; set; } = "";
        public string[] Children { get; set; } = Array.Empty<string>();
        public string[] Interfaces { get; set; } = Array.Empty<string>();
        public string Companion { get; set; } = "";
        public string[] CompanionInterfaces { get; set; } = Array.Empty<string>();
        public string DiGuid { get; set; } = "";
        public string SdlPath { get; set; } = "";
        public string UsbSerial { get; set; } = "";
        public int XInputSlot { get; set; } = -1;

        public string Summary() =>
            $"parent={Parent} prefix={Prefix} container={Container}\n" +
            $"          children=[{string.Join(", ", Children)}]\n" +
            $"          interfaces=[{string.Join(", ", Interfaces)}]\n" +
            (Companion.Length > 0 ? $"          companion={Companion} xusb=[{string.Join(", ", CompanionInterfaces)}]\n" : "") +
            $"          dinput={DiGuid} sdl={SdlPath} serial={UsbSerial}";

        public static string[] Differences(IdentityRecord a, IdentityRecord b)
        {
            var d = new List<string>();
            if (a.Parent != b.Parent) d.Add($"parent {a.Parent} -> {b.Parent}");
            if (!string.Equals(a.Prefix, b.Prefix, StringComparison.OrdinalIgnoreCase)) d.Add($"prefix {a.Prefix} -> {b.Prefix}");
            if (!string.Equals(a.Container, b.Container, StringComparison.OrdinalIgnoreCase)) d.Add($"container {a.Container} -> {b.Container}");
            if (!a.Children.SequenceEqual(b.Children, StringComparer.OrdinalIgnoreCase)) d.Add($"children [{string.Join(",", a.Children)}] -> [{string.Join(",", b.Children)}]");
            if (!a.Interfaces.SequenceEqual(b.Interfaces, StringComparer.OrdinalIgnoreCase)) d.Add($"interfaces [{string.Join(",", a.Interfaces)}] -> [{string.Join(",", b.Interfaces)}]");
            if (!string.Equals(a.Companion, b.Companion, StringComparison.OrdinalIgnoreCase)) d.Add($"companion {a.Companion} -> {b.Companion}");
            if (!a.CompanionInterfaces.SequenceEqual(b.CompanionInterfaces, StringComparer.OrdinalIgnoreCase)) d.Add("companion interfaces differ");
            if (!string.Equals(a.DiGuid, b.DiGuid, StringComparison.OrdinalIgnoreCase)) d.Add($"dinput {a.DiGuid} -> {b.DiGuid}");
            if (!string.Equals(a.SdlPath, b.SdlPath, StringComparison.OrdinalIgnoreCase)) d.Add($"sdl {a.SdlPath} -> {b.SdlPath}");
            if (!string.Equals(a.UsbSerial, b.UsbSerial, StringComparison.OrdinalIgnoreCase)) d.Add($"serial {a.UsbSerial} -> {b.UsbSerial}");
            return d.ToArray();
        }
    }

    // ── entry ──────────────────────────────────────────────────────────

    static readonly string BaselinePath = Path.Combine(Path.GetTempPath(), "HIDMaestro", "identity_baseline.json");

    static int Main(string[] args)
    {
        if (args.Length >= 4 && args[0] == "--child")
            return ChildLife(args[1], args[2], args[3]);

        Console.WriteLine("=== Stable device identity across every life (issue #60) ===");
        if (!IsElevated())
        {
            Console.WriteLine("  not elevated; this probe creates devices");
            return 2;
        }

        // WGI's broker only tracks controllers for processes that listen.
        Gamepad.GamepadAdded += (_, _) => { };
        RawGameController.RawGameControllerAdded += (_, _) => { };
        Thread.Sleep(500);
        Sdl.TryLoad();

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        if (args.Length >= 1 && args[0] == "--compare")
            return CompareAfterReboot(ctx);

        // --overlap runs just the overlap scenarios. The full run takes two
        // minutes, and diagnosing a DirectInput attribution question does not
        // need the nine lives of five families.
        if (args.Length >= 1 && args[0] == "--overlap")
        {
            s_diDump = true;
            Overlap(ctx, Families[2], "dualsense");
            Overlap(ctx, Families[4], "dualsense-composite");
            Console.WriteLine();
            Console.WriteLine($"=== {s_total - s_failures}/{s_total} {(s_failures == 0 ? "PASS" : "FAIL")} ===");
            return s_failures == 0 ? 0 : 1;
        }

        var baseline = new List<IdentityRecord>();
        foreach (var fam in Families)
        {
            Console.WriteLine();
            Console.WriteLine($"--- {fam.Id}: {fam.Label} ---");
            string key = $"identity-battery:{fam.Id}";
            var profile = ResolveProfile(ctx, fam.Id);
            var lives = new List<IdentityRecord>();

            // L1..L5: same process.
            for (int n = 1; n <= 5; n++)
            {
                var r = RunLife(ctx, fam, profile, key, $"same-process #{n}");
                if (r != null) lives.Add(r);
            }
            // L6..L8: process restarts.
            for (int n = 1; n <= 3; n++)
            {
                var r = SpawnLife(fam, key, $"process-restart #{n}");
                if (r != null) lives.Add(r);
            }
            // L9: driver uninstall + reinstall. The full sweep evicts every
            // device and deletes the driver packages; the next create sees
            // no package and redeploys.
            HMContext.RemoveAllVirtualControllers();
            Check($"{fam.Id}: driver packages gone after the full sweep", !DriverBuilder.IsDriverInstalled());
            var r9 = RunLife(ctx, fam, profile, key, "driver reinstall");
            if (r9 != null) lives.Add(r9);

            Check($"{fam.Id}: 9 lives recorded", lives.Count == 9, $"{lives.Count}");
            if (lives.Count > 0)
            {
                Console.WriteLine($"  life 1: {lives[0].Summary()}");
                bool allSame = true;
                for (int i = 1; i < lives.Count; i++)
                {
                    var diff = IdentityRecord.Differences(lives[0], lives[i]);
                    if (diff.Length > 0)
                    {
                        allSame = false;
                        Console.WriteLine($"  life {i + 1} ({lives[i].Life}) differs: {string.Join("; ", diff)}");
                    }
                }
                Check($"{fam.Id}: every identity value identical across all {lives.Count} lives", allSame);
                baseline.Add(lives[0]);
            }
        }

        Console.WriteLine();
        Console.WriteLine("--- default key: the index reproduces the same identity across lives ---");
        {
            var profile = ResolveProfile(ctx, "dualsense");
            var a = RunLife(ctx, Families[2], profile, null, "default key #1");
            var b = RunLife(ctx, Families[2], profile, null, "default key #2");
            Check("default key: index identity is stable across two lives",
                  a != null && b != null && IdentityRecord.Differences(a, b).Length == 0
                  && a.Parent.EndsWith(@"\HM_0000", StringComparison.OrdinalIgnoreCase),
                  a?.Parent ?? "(none)");
        }

        Console.WriteLine();
        Console.WriteLine("--- overlap: two pads of one VID/PID, first removed ---");
        Overlap(ctx, Families[2], "dualsense");
        Overlap(ctx, Families[4], "dualsense-composite");

        Console.WriteLine();
        Console.WriteLine("--- profile change at the same identity ---");
        ProfileChange(ctx);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BaselinePath)!);
            File.WriteAllText(BaselinePath, JsonSerializer.Serialize(baseline, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"\n  baseline written: {BaselinePath} (run with --compare after a reboot)");
        }
        catch (Exception ex) { Console.WriteLine($"  baseline not written: {ex.Message}"); }

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} {(s_failures == 0 ? "PASS" : "FAIL")} ===");
        return s_failures == 0 ? 0 : 1;
    }

    static int CompareAfterReboot(HMContext ctx)
    {
        if (!File.Exists(BaselinePath)) { Console.WriteLine($"  no baseline at {BaselinePath}"); return 2; }
        var baseline = JsonSerializer.Deserialize<List<IdentityRecord>>(File.ReadAllText(BaselinePath)) ?? new();
        foreach (var fam in Families)
        {
            var before = baseline.FirstOrDefault(b => b.Family == fam.Id);
            if (before == null) { Check($"{fam.Id}: baseline has a record", false); continue; }
            var now = RunLife(ctx, fam, ResolveProfile(ctx, fam.Id), before.Key, "after reboot");
            var diff = now == null ? new[] { "no record" } : IdentityRecord.Differences(before, now);
            Check($"{fam.Id}: identical to the pre-reboot baseline", diff.Length == 0, string.Join("; ", diff));
        }
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} {(s_failures == 0 ? "PASS" : "FAIL")} ===");
        return s_failures == 0 ? 0 : 1;
    }

    // ── one life ───────────────────────────────────────────────────────

    static IdentityRecord? RunLife(HMContext ctx, Family fam, HMProfile profile, string? key, string life)
    {
        var (vid, pid) = VidPid(profile);
        var wgiBefore = fam.Xbox ? WgiCounts(vid, pid) : (0, 0);
        int slotsBefore = fam.Xbox ? XInputSlots().Count : 0;
        HMController? c = null;
        try
        {
            var sw = Stopwatch.StartNew();
            c = ctx.CreateController(profile, key);
            long createMs = sw.ElapsedMilliseconds;
            var rec = Measure(c, fam, profile, life);
            Console.WriteLine($"  {life}: created in {createMs} ms, {rec.Children.Length} HID child(ren), {rec.Interfaces.Length} interface(s)");
            EmptyShellChecks(c, fam, profile, rec, wgiBefore, slotsBefore);
            return rec;
        }
        catch (Exception ex)
        {
            Check($"{fam.Id}: {life} ran without throwing", false, ex.Message);
            return null;
        }
        finally
        {
            try { c?.Dispose(); } catch { }
            if (fam.Xbox)
            {
                SettleWgi(vid, pid, wgiBefore);
                SettleXInput(slotsBefore);
            }
        }
    }

    static int ChildLife(string famId, string key, string outFile)
    {
        try
        {
            var fam = Families.First(f => f.Id == famId);
            Sdl.TryLoad();
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            ctx.InstallDriver();
            var profile = ResolveProfile(ctx, famId);
            using var c = ctx.CreateController(profile, key);
            var rec = Measure(c, fam, profile, "child");
            File.WriteAllText(outFile, JsonSerializer.Serialize(rec));
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(outFile + ".err", ex.ToString()); } catch { }
            return 1;
        }
    }

    static IdentityRecord? SpawnLife(Family fam, string key, string life)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "HIDMaestro", $"identity_{fam.Id}_{Guid.NewGuid():N}.json");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--child"); psi.ArgumentList.Add(fam.Id); psi.ArgumentList.Add(key); psi.ArgumentList.Add(outFile);
            var sw = Stopwatch.StartNew();
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(180_000)) { try { p.Kill(); } catch { } }
            if (p.ExitCode != 0 || !File.Exists(outFile))
            {
                string err = File.Exists(outFile + ".err") ? File.ReadAllText(outFile + ".err").Split('\n')[0] : $"exit {p.ExitCode}";
                Check($"{fam.Id}: {life} child process succeeded", false, err);
                return null;
            }
            var rec = JsonSerializer.Deserialize<IdentityRecord>(File.ReadAllText(outFile))!;
            rec.Life = life;
            Console.WriteLine($"  {life}: child process round trip {sw.ElapsedMilliseconds} ms");
            return rec;
        }
        finally
        {
            try { File.Delete(outFile); File.Delete(outFile + ".err"); } catch { }
            // The child disposed its controller before exiting; its XUSB or
            // xinputhid cascade may still be finishing.
            if (fam.Xbox) Thread.Sleep(1500);
        }
    }

    // ── measurement ────────────────────────────────────────────────────

    static IdentityRecord Measure(HMController c, Family fam, HMProfile profile, string life)
    {
        var (vid, pid) = VidPid(profile);
        var identity = DeviceIdentity.Resolve(c.IdentityKey, c.Index);
        var rec = new IdentityRecord { Family = fam.Id, Life = life, Key = c.IdentityKey };

        if (fam.Usbip)
        {
            // The composite parent is keyed on the serial the persona serves.
            rec.UsbSerial = identity.SyntheticSerial;
            rec.Parent = $@"USB\VID_{vid:X4}&PID_{pid:X4}\{identity.SyntheticSerial}";
            for (int i = 0; i < 100 && CM_Locate_DevNodeW(out _, rec.Parent, 0) != 0; i++) Thread.Sleep(100);
        }
        else
        {
            rec.Parent = c.InstanceId ?? "";
            rec.UsbSerial = "n/a";
        }

        rec.Prefix = ReadRegString(rec.Parent, "ParentIdPrefix") ?? "";
        rec.Container = ContainerIdOf(rec.Parent);
        rec.Children = Descendants(rec.Parent).Where(d => d.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)).ToArray();

        // The HID interface takes a moment after the devnode.
        for (int i = 0; i < 50; i++)
        {
            rec.Interfaces = rec.Children.SelectMany(ch => InterfaceList(HidGuid, ch)).ToArray();
            if (rec.Interfaces.Length > 0) break;
            Thread.Sleep(100);
            rec.Children = Descendants(rec.Parent).Where(d => d.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        if (fam.Id == "xbox-360-wired")
        {
            rec.Companion = $@"SWD\HIDMAESTRO\{identity.Token}";
            rec.CompanionInterfaces = InterfaceList(XusbGuid, rec.Companion).ToArray();
        }

        if (s_diDump)
        {
            Console.WriteLine($"   [{life}] looking for:");
            foreach (var i in rec.Interfaces) Console.WriteLine($"     ours:    {i}");
        }
        rec.DiGuid = DirectInputGuid(rec.Interfaces, vid, pid) ?? "(not enumerated)";
        // SDL reports XInput-claimed pads as XInput#<slot>, so the slot our
        // input lands in is part of the record for the Xbox families. The
        // bench may carry a real pad of the same VID/PID (a Bluetooth
        // DualSense does here), so SDL's list is filtered to our own
        // interface paths, or to our XInput slot.
        if (fam.Xbox) rec.XInputSlot = FindXInputSlot(c);
        rec.SdlPath = Sdl.PathFor(vid, pid, rec.Interfaces, rec.XInputSlot);
        return rec;
    }

    // ── empty-shell checks ─────────────────────────────────────────────

    static void EmptyShellChecks(HMController c, Family fam, HMProfile profile, IdentityRecord rec,
                                 (int Gamepads, int Raw) wgiBefore, int slotsBefore)
    {
        var (vid, pid) = VidPid(profile);
        string tag = $"{fam.Id} {rec.Life}";

        string? service = ReadRegString(rec.Parent, "Service");
        Check($"{tag}: Service bound on the parent", !string.IsNullOrEmpty(service), service ?? "(none)");
        Check($"{tag}: parent started", IsStarted(rec.Parent));
        Check($"{tag}: HID interface published", rec.Interfaces.Length > 0);
        if (fam.Id == "xbox-360-wired")
            Check($"{tag}: XUSB interface published on the companion", rec.CompanionInterfaces.Length == 1,
                  $"{rec.CompanionInterfaces.Length}");

        int outputs = 0;
        c.OutputReceived += (_, _) => Interlocked.Increment(ref outputs);

        if (fam.Xbox)
        {
            int slot = rec.XInputSlot >= 0 ? rec.XInputSlot : FindXInputSlot(c);
            Check($"{tag}: XInput input flows (a submitted stick lands in a slot)", slot >= 0, slot >= 0 ? $"slot {slot}" : "no slot moved");
            if (slot >= 0)
            {
                var vib = new XINPUT_VIBRATION { wLeftMotorSpeed = 65535, wRightMotorSpeed = 65535 };
                XInputSetState((uint)slot, ref vib);
                Check($"{tag}: output flows (XInputSetState reaches OutputReceived)", WaitFor(() => outputs > 0, 3000));
                vib = default; XInputSetState((uint)slot, ref vib);
            }
            var counts = (0, 0);
            for (int i = 0; i < 40; i++) { Thread.Sleep(250); counts = WgiCounts(vid, pid); if (counts.Item1 > wgiBefore.Gamepads) break; }
            Thread.Sleep(1500);
            counts = WgiCounts(vid, pid);
            Check($"{tag}: exactly one WGI Gamepad more than the bench had",
                  counts.Item1 == wgiBefore.Gamepads + 1 && counts.Item2 == wgiBefore.Raw + 1,
                  $"gamepads {wgiBefore.Gamepads}->{counts.Item1} raw {wgiBefore.Raw}->{counts.Item2}");
            Check($"{tag}: exactly one XInput slot more than the bench had", XInputSlots().Count == slotsBefore + 1,
                  $"{slotsBefore}->{XInputSlots().Count}");
        }
        else
        {
            string? path = rec.Interfaces.FirstOrDefault();
            Check($"{tag}: HID input flows (submitted state read back)", path != null && HidInputFlows(c, profile, path));
            int outLen = fam.OutputLength > 0 ? fam.OutputLength : OutputReportLength(path);
            if (path != null && outLen > 0)
            {
                var report = new byte[outLen];
                report[0] = fam.OutputReportId;
                if (fam.OutputReportId == 0x1C) report[1] = 0x01; // PID device control: enable actuators
                bool sent = SetOutputReport(path, report);
                Check($"{tag}: output flows (report 0x{fam.OutputReportId:X2} reaches OutputReceived)",
                      sent && WaitFor(() => outputs > 0, 3000), sent ? "" : $"HidD_SetOutputReport failed ({Marshal.GetLastWin32Error()})");
            }
            else Check($"{tag}: output report lane exists", false, "no output report length");
        }
    }

    static bool HidInputFlows(HMController c, HMProfile profile, string path)
    {
        var builder = profile.Inner.GetOrBuildReportBuilder();
        var stateA = new HMGamepadState { Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 1.0f, [HMAxis.Y] = 0.5f }, Buttons = HMButton.A };
        var stateB = new HMGamepadState { Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 0.0f, [HMAxis.Y] = 0.5f }, Buttons = 0 };
        byte[]? a = null, b = null;
        for (int i = 0; i < 20 && (a == null || b == null || a.SequenceEqual(b)); i++)
        {
            c.SubmitState(stateA); Thread.Sleep(30);
            a = ReadInput(c, path, builder, stateA);
            c.SubmitState(stateB); Thread.Sleep(30);
            b = ReadInput(c, path, builder, stateB);
        }
        return a != null && b != null && !a.SequenceEqual(b);
    }

    /// <summary>HidD_GetInputReport when the stack answers it (UMDF2 and
    /// hidusb both do), else one blocking ReadFile while the state is
    /// resubmitted so a report is in flight.</summary>
    static byte[]? ReadInput(HMController c, string path, HidReportBuilder builder, HMGamepadState state)
    {
        IntPtr h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID) return null;
        try
        {
            int len = Math.Max(builder.InputReportByteSize, InputReportLength(h));
            var buf = new byte[len];
            buf[0] = builder.InputReportId;
            if (HidD_GetInputReport(h, buf, buf.Length)) return buf;
            using var stop = new ManualResetEventSlim(false);
            var pump = new Thread(() => { while (!stop.IsSet) { c.SubmitState(state); Thread.Sleep(8); } }) { IsBackground = true };
            pump.Start();
            try
            {
                var rb = new byte[len];
                return ReadFile(h, rb, rb.Length, out int read, IntPtr.Zero) && read > 0 ? rb : null;
            }
            finally { stop.Set(); pump.Join(500); }
        }
        finally { CloseHandle(h); }
    }

    static int FindXInputSlot(HMController c)
    {
        var state = new HMGamepadState { Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 1.0f, [HMAxis.Y] = 0.5f } };
        for (int i = 0; i < 100; i++)
        {
            c.SubmitState(state);
            Thread.Sleep(20);
            for (uint s = 0; s < 4; s++)
                if (XInputGetState(s, out var st) == 0 && st.Gamepad.sThumbLX > 30000) return (int)s;
        }
        return -1;
    }

    static bool WaitFor(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(20); }
        return cond();
    }

    static void SettleWgi(ushort vid, ushort pid, (int Gamepads, int Raw) before)
    {
        for (int i = 0; i < 30; i++)
        {
            var now = WgiCounts(vid, pid);
            if (now.Item1 <= before.Gamepads && now.Item2 <= before.Raw) break;
            Thread.Sleep(500);
        }
    }

    /// <summary>XInput drops a removed pad's slot after WGI drops the pad.
    /// The next life samples its bench from XInput, so wait for the slot to
    /// go too, or that bench counts the previous life's pad.</summary>
    static void SettleXInput(int before)
    {
        for (int i = 0; i < 30; i++)
        {
            if (XInputSlots().Count <= before) break;
            Thread.Sleep(500);
        }
    }

    // ── overlap and profile change ─────────────────────────────────────

    /// <summary>True when DirectInput named one specific pad for this record.</summary>
    static bool Attributed(IdentityRecord r) =>
        r.DiGuid != Unattributed && r.DiGuid != "(not enumerated)";

    static void Overlap(HMContext ctx, Family fam, string profileId)
    {
        var profile = ResolveProfile(ctx, profileId);
        var (vid, pid) = VidPid(profile);
        HMController? a = null, b = null;
        try
        {
            a = ctx.CreateController(profile, $"overlap:{profileId}:A");
            var recA = Measure(a, fam, profile, "overlap A");
            b = ctx.CreateController(profile, $"overlap:{profileId}:B");
            var recB1 = Measure(b, fam, profile, "overlap B with A present");
            Check($"{profileId} overlap: the two pads have distinct paths", recA.Parent != recB1.Parent
                  && !recA.Interfaces.SequenceEqual(recB1.Interfaces, StringComparer.OrdinalIgnoreCase));
            // Positive control for the ordinal check below. DirectInput can
            // only place A and B at different ordinals if A is still on the
            // system when B is measured, so re-read A here. Without it, a pad
            // that quietly went away and a DirectInput that skipped it look
            // the same.
            var recA2 = Measure(a, fam, profile, "overlap A while B present");
            Check($"{profileId} overlap: A is still published while B is present",
                  recA2.Parent == recA.Parent
                  && recA2.Interfaces.SequenceEqual(recA.Interfaces, StringComparer.OrdinalIgnoreCase),
                  string.Join("; ", IdentityRecord.Differences(recA, recA2)));
            // A ROOT-enumerated devnode has no container of its own, so the
            // sentinel is the right answer there. The composite parent is a
            // USB device and gets a real one, which must differ per pad.
            if (fam.Usbip)
                Check($"{profileId} overlap: the two pads have distinct container ids",
                      recA.Container != recB1.Container, $"A={recA.Container} B={recB1.Container}");
            // DirectInput instance GUIDs are positional. Windows keeps one
            // GUID per slot under the VID/PID key and hands slot 0 to
            // whichever pad it enumerates first, which follows PnP order.
            // For the composite that is hub port order, so B can enumerate
            // ahead of A even though A was created first. The property worth
            // holding is that DirectInput separates the two pads, not which
            // one lands at slot 0, so both readings are taken in the same
            // window with both pads present. An unattributed reading is not a
            // measurement of either pad, and two of those markers compare
            // equal, which would answer the question for the wrong reason.
            bool attributed = Attributed(recA2) && Attributed(recB1);
            Check($"{profileId} overlap: DirectInput attributes an instance to each pad", attributed,
                  $"A={recA2.DiGuid} B={recB1.DiGuid}");
            if (attributed)
                Check($"{profileId} overlap: DirectInput holds two distinct instances while both pads are present",
                      recA2.DiGuid != recB1.DiGuid, $"A={recA2.DiGuid} B={recB1.DiGuid}");
            a.Dispose(); a = null;
            Thread.Sleep(500);
            var recB2 = Measure(b, fam, profile, "overlap B after A removed");
            Check($"{profileId} overlap: B keeps its parent, children, interfaces and serial",
                  recB2.Parent == recB1.Parent && recB2.Children.SequenceEqual(recB1.Children, StringComparer.OrdinalIgnoreCase)
                  && recB2.Interfaces.SequenceEqual(recB1.Interfaces, StringComparer.OrdinalIgnoreCase)
                  && recB2.UsbSerial == recB1.UsbSerial,
                  string.Join("; ", IdentityRecord.Differences(recB1, recB2)));
            // recA is A measured alone, so its GUID is DirectInput's first
            // slot for this VID/PID. B is now the only pad left and must
            // hold that same slot.
            if (attributed && Attributed(recB2))
                Check($"{profileId} overlap: B takes DirectInput's first instance once A is gone",
                      recB2.DiGuid == recA.DiGuid, $"B now {recB2.DiGuid}, the first slot is {recA.DiGuid}");
        }
        catch (Exception ex) { Check($"{profileId} overlap ran without throwing", false, ex.Message); }
        finally { try { a?.Dispose(); } catch { } try { b?.Dispose(); } catch { } }
    }

    static void ProfileChange(HMContext ctx)
    {
        var fam = Families[2];
        string key = "identity-battery:profile-change";
        var ds5 = ResolveProfile(ctx, "dualsense");
        var ds4 = ResolveProfile(ctx, "dualshock-4-v2");
        IdentityRecord? r1 = null;
        try
        {
            using (var c1 = ctx.CreateController(ds5, key))
                r1 = Measure(c1, fam, ds5, "dualsense");
            using var c2 = ctx.CreateController(ds4, key);
            var r2 = Measure(c2, fam, ds4, "dualshock-4-v2 at the same key");
            var diff = IdentityRecord.Differences(r1, r2).Where(d => !d.StartsWith("dinput") && !d.StartsWith("sdl")).ToArray();
            Check("profile change: parent, prefix, children and interfaces unchanged", diff.Length == 0, string.Join("; ", diff));
            string? path = r2.Interfaces.FirstOrDefault();
            var attr = path != null ? HidAttributes(path) : null;
            Check("profile change: the HID attributes are the new profile's",
                  attr != null && attr.Value.Vid == ds4.VendorId && attr.Value.Pid == ds4.ProductId,
                  attr != null ? $"{attr.Value.Vid:X4}:{attr.Value.Pid:X4}" : "(no path)");
            Check("profile change: input flows on the refreshed descriptor", path != null && HidInputFlows(c2, ds4, path));
            Check("profile change: Service bound", !string.IsNullOrEmpty(ReadRegString(r2.Parent, "Service")));
        }
        catch (Exception ex) { Check("profile change ran without throwing", false, ex.Message); }
    }

    // ── PnP helpers ────────────────────────────────────────────────────

    static readonly Guid HidGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    static readonly Guid XusbGuid = new("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");
    const uint DN_STARTED = 0x00000008;

    static string? ReadRegString(string instanceId, string value)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
            return k?.GetValue(value) as string;
        }
        catch { return null; }
    }

    static bool IsStarted(string instanceId)
        => CM_Locate_DevNodeW(out uint di, instanceId, 0) == 0
        && CM_Get_DevNode_Status(out uint st, out _, di, 0) == 0 && (st & DN_STARTED) != 0;

    static string ContainerIdOf(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint di, instanceId, 0) != 0) return "";
        var key = new DEVPROPKEY { fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };
        var buf = new byte[16];
        uint size = 16;
        if (CM_Get_DevNode_PropertyW(di, ref key, out uint type, buf, ref size, 0) != 0 || size != 16) return "";
        return new Guid(buf).ToString("B");
    }

    static List<string> Descendants(string instanceId)
    {
        var list = new List<string>();
        if (CM_Locate_DevNodeW(out uint di, instanceId, 0) != 0) return list;
        void Walk(uint node)
        {
            if (CM_Get_Child(out uint child, node, 0) != 0) return;
            uint cur = child;
            do
            {
                list.Add(DeviceIdOf(cur));
                Walk(cur);
            } while (CM_Get_Sibling(out cur, cur, 0) == 0);
        }
        Walk(di);
        return list;
    }

    static string DeviceIdOf(uint devInst)
    {
        var buf = new char[512];
        return CM_Get_Device_IDW(devInst, buf, (uint)buf.Length, 0) == 0
            ? new string(buf, 0, Array.IndexOf(buf, '\0') is int z && z >= 0 ? z : buf.Length) : "";
    }

    static IEnumerable<string> InterfaceList(Guid guid, string deviceId)
    {
        if (CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, deviceId, 0) != 0 || len <= 1) yield break;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref guid, deviceId, buf, len, 0) != 0) yield break;
        int i = 0;
        while (i < buf.Length)
        {
            int e = Array.IndexOf(buf, '\0', i);
            if (e < 0 || e == i) break;
            yield return new string(buf, i, e - i);
            i = e + 1;
        }
    }

    static (int, int) WgiCounts(ushort vid, ushort pid)
    {
        int gp = 0;
        foreach (var g in Gamepad.Gamepads)
        {
            var r = RawGameController.FromGameController(g);
            if (r != null && r.HardwareVendorId == vid && r.HardwareProductId == pid) gp++;
        }
        int raw = RawGameController.RawGameControllers.Count(r => r.HardwareVendorId == vid && r.HardwareProductId == pid);
        return (gp, raw);
    }

    static List<uint> XInputSlots()
    {
        var l = new List<uint>();
        for (uint s = 0; s < 4; s++) if (XInputGetState(s, out _) == 0) l.Add(s);
        return l;
    }

    static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    // ── HID helpers ────────────────────────────────────────────────────

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3;
    static readonly IntPtr INVALID = new(-1);

    static (ushort Vid, ushort Pid)? HidAttributes(string path)
    {
        IntPtr h = CreateFileW(path, 0, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID) return null;
        try
        {
            var a = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            return HidD_GetAttributes(h, ref a) ? (a.VendorID, a.ProductID) : null;
        }
        finally { CloseHandle(h); }
    }

    static int InputReportLength(IntPtr h)
    {
        if (!HidD_GetPreparsedData(h, out IntPtr pp)) return 0;
        try
        {
            var caps = new byte[64];
            return HidP_GetCaps(pp, caps) == 0x00110000 ? BitConverter.ToUInt16(caps, 4) : 0;
        }
        finally { HidD_FreePreparsedData(pp); }
    }

    static int OutputReportLength(string? path)
    {
        if (path == null) return 0;
        IntPtr h = CreateFileW(path, 0, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID) return 0;
        try
        {
            if (!HidD_GetPreparsedData(h, out IntPtr pp)) return 0;
            try
            {
                var caps = new byte[64];
                return HidP_GetCaps(pp, caps) == 0x00110000 ? BitConverter.ToUInt16(caps, 6) : 0;
            }
            finally { HidD_FreePreparsedData(pp); }
        }
        finally { CloseHandle(h); }
    }

    static bool SetOutputReport(string path, byte[] report)
    {
        IntPtr h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID) return false;
        try { return HidD_SetOutputReport(h, report, report.Length); }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] static extern bool HidD_GetInputReport(IntPtr h, byte[] buf, int len);
    [DllImport("hid.dll")] static extern bool HidD_SetOutputReport(IntPtr h, byte[] buf, int len);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, byte[] caps);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string p, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr h, byte[] buf, int n, out int read, IntPtr overlapped);

    // ── XInput ─────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_VIBRATION { public ushort wLeftMotorSpeed, wRightMotorSpeed; }
    [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint idx, out XINPUT_STATE st);
    [DllImport("xinput1_4.dll")] static extern uint XInputSetState(uint idx, ref XINPUT_VIBRATION v);

    // ── CfgMgr32 ───────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public uint pid; }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern uint CM_Locate_DevNodeW(out uint di, string id, uint f);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Get_DevNode_Status(out uint st, out uint pr, uint di, uint f);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Get_Child(out uint c, uint di, uint f);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Get_Sibling(out uint s, uint di, uint f);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern uint CM_Get_Device_IDW(uint di, char[] buf, uint len, uint f);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_DevNode_PropertyW(uint di, ref DEVPROPKEY key, out uint type, byte[] buf, ref uint size, uint f);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid g, string? id, uint f);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid g, string? id, char[] buf, uint len, uint f);

    // ── DirectInput ────────────────────────────────────────────────────

    /// <summary>The DirectInput instance GUID of the device whose
    /// DIPROP_GUIDANDPATH path is one of <paramref name="interfaces"/>,
    /// falling back to the VID/PID packed in guidProduct. DirectInput
    /// keys the GUID on the ordinal among attached same-VID/PID devices,
    /// which is what the overlap scenario measures.</summary>
    static string? DirectInputGuid(string[] interfaces, ushort vid, ushort pid)
    {
        Guid iid = new("BF798031-483A-4DA2-AA99-5D64ED369700");
        for (int attempt = 0; attempt < 25; attempt++)
        {
            string? found = null;
            try
            {
                if (DirectInput8Create(GetModuleHandleW(null), 0x0800, ref iid, out IntPtr di8, IntPtr.Zero) == 0 && di8 != IntPtr.Zero)
                {
                    try
                    {
                        var entries = new List<(Guid Inst, uint Prod)>();
                        EnumCb cb = (ref DIDEVICEINSTANCEW d, IntPtr _) => { entries.Add((d.guidInstance, d.guidProduct.ToByteArray() is var b ? BitConverter.ToUInt32(b, 0) : 0u)); return 1; };
                        var enumDevices = Marshal.GetDelegateForFunctionPointer<EnumDevicesFn>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(di8), 4 * IntPtr.Size));
                        enumDevices(di8, 4, Marshal.GetFunctionPointerForDelegate(cb), IntPtr.Zero, 1);
                        GC.KeepAlive(cb);
                        var createDevice = Marshal.GetDelegateForFunctionPointer<CreateDeviceFn>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(di8), 3 * IntPtr.Size));
                        string? byVidPid = null;
                        int vidPidCount = 0;
                        var seenPaths = new List<string>();
                        foreach (var (inst, prod) in entries)
                        {
                            Guid g = inst;
                            if (createDevice(di8, ref g, out IntPtr dev, IntPtr.Zero) != 0 || dev == IntPtr.Zero) continue;
                            try
                            {
                                var getProperty = Marshal.GetDelegateForFunctionPointer<GetPropertyFn>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(dev), 5 * IntPtr.Size));
                                int size = Marshal.SizeOf<DIPROPGUIDANDPATH>();
                                IntPtr mem = Marshal.AllocHGlobal(size);
                                try
                                {
                                    var hdr = new DIPROPGUIDANDPATH { dwSize = (uint)size, dwHeaderSize = 16, dwObj = 0, dwHow = 0, wszPath = "" };
                                    Marshal.StructureToPtr(hdr, mem, false);
                                    if (getProperty(dev, new IntPtr(12), mem) == 0)
                                    {
                                        var got = Marshal.PtrToStructure<DIPROPGUIDANDPATH>(mem);
                                        if (s_diDump) Console.WriteLine($"     DI enum: {inst:B} {got.wszPath.TrimEnd('\0')}");
                                        if ((prod & 0xFFFF) == vid && (prod >> 16) == pid) seenPaths.Add(got.wszPath.TrimEnd('\0'));
                                        if (interfaces.Any(i => string.Equals(NormalizePath(i), NormalizePath(got.wszPath), StringComparison.OrdinalIgnoreCase)))
                                        { found = inst.ToString("B"); break; }
                                    }
                                    else if (s_diDump) Console.WriteLine($"     DI enum: {inst:B} (no path property)");
                                }
                                finally { Marshal.FreeHGlobal(mem); }
                                if ((prod & 0xFFFF) == vid && (prod >> 16) == pid)
                                {
                                    vidPidCount++;
                                    byVidPid ??= inst.ToString("B");
                                }
                            }
                            finally { Marshal.GetDelegateForFunctionPointer<ReleaseFn>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(dev), 2 * IntPtr.Size))(dev); }
                        }
                        // The VID/PID fallback names a device only when one
                        // device carries that VID/PID. With more than one it
                        // would return whichever DirectInput enumerated
                        // first, which silently equates two pads the overlap
                        // scenario exists to tell apart. Say so instead of
                        // guessing, and print the paths DirectInput did
                        // report so the reason is on the record.
                        if (found == null && vidPidCount > 1)
                        {
                            Console.WriteLine($"     DirectInput did not attribute a device to any of our interfaces ({vidPidCount} share {vid:X4}:{pid:X4}).");
                            foreach (var p in seenPaths) Console.WriteLine($"       DI path: {p}");
                            foreach (var i in interfaces) Console.WriteLine($"       ours:    {i}");
                        }
                        found ??= vidPidCount == 1 ? byVidPid
                                : vidPidCount > 1 ? Unattributed
                                : null;
                    }
                    finally { Marshal.GetDelegateForFunctionPointer<ReleaseFn>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(di8), 2 * IntPtr.Size))(di8); }
                }
            }
            catch { }
            if (found != null) return found;
            Thread.Sleep(200);
        }
        return null;
    }

    static string NormalizePath(string p) => p.Replace("\\\\?\\", "\\\\?\\").TrimEnd('\0').Replace('/', '\\');

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DIDEVICEINSTANCEW
    {
        public int dwSize; public Guid guidInstance; public Guid guidProduct; public uint dwDevType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string tszInstanceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string tszProductName;
        public Guid guidFFDriver; public ushort wUsagePage; public ushort wUsage;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DIPROPGUIDANDPATH
    {
        public uint dwSize, dwHeaderSize, dwObj, dwHow; public Guid guidClass;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszPath;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumCb(ref DIDEVICEINSTANCEW d, IntPtr pv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumDevicesFn(IntPtr self, uint t, IntPtr cb, IntPtr pv, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateDeviceFn(IntPtr self, ref Guid g, out IntPtr dev, IntPtr punk);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetPropertyFn(IntPtr self, IntPtr prop, IntPtr header);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ReleaseFn(IntPtr self);
    [DllImport("dinput8.dll", CharSet = CharSet.Unicode)]
    static extern int DirectInput8Create(IntPtr hInst, uint ver, ref Guid riid, out IntPtr ppv, IntPtr punk);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? n);

    // ── SDL3 ───────────────────────────────────────────────────────────

    /// <summary>SDL3's joystick path for our VID/PID, through the real
    /// SDL3.dll from the sibling SDL3-build checkout when present. "n/a"
    /// consistently when it is not, so the comparison still holds.</summary>
    static class Sdl
    {
        static bool s_available;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetDllDirectoryW(string p);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string p);
        [DllImport("SDL3")] static extern bool SDL_Init(uint flags);
        [DllImport("SDL3")] static extern void SDL_Quit();
        [DllImport("SDL3")] static extern void SDL_PumpEvents();
        [DllImport("SDL3")] static extern IntPtr SDL_GetJoysticks(out int count);
        [DllImport("SDL3")] static extern ushort SDL_GetJoystickVendorForID(uint id);
        [DllImport("SDL3")] static extern ushort SDL_GetJoystickProductForID(uint id);
        [DllImport("SDL3")] static extern IntPtr SDL_GetJoystickPathForID(uint id);
        [DllImport("SDL3")] static extern void SDL_free(IntPtr p);

        public static void TryLoad()
        {
            string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
            foreach (var c in new[] { Path.Combine(repoRoot, "..", "SDL3-build"), Path.Combine(repoRoot, "..", "SDL3-build", "build", "Release") })
            {
                if (!File.Exists(Path.Combine(c, "SDL3.dll"))) continue;
                string dir = Path.GetFullPath(c);
                string libusb = Path.GetFullPath(Path.Combine(dir, "..", "..", "libusb-1.0.dll"));
                if (!File.Exists(libusb)) libusb = Path.Combine(dir, "libusb-1.0.dll");
                if (File.Exists(libusb)) LoadLibraryW(libusb);
                SetDllDirectoryW(dir);
                s_available = LoadLibraryW(Path.Combine(dir, "SDL3.dll")) != IntPtr.Zero;
                Console.WriteLine($"  SDL3: {(s_available ? dir : "failed to load")}");
                return;
            }
            Console.WriteLine("  SDL3: not found beside the repo; SDL paths recorded as n/a");
        }

        public static string PathFor(ushort vid, ushort pid, string[] interfaces, int xinputSlot)
        {
            if (!s_available) return "n/a";
            try
            {
                if (!SDL_Init(0x200)) return "(SDL_Init failed)";
                try
                {
                    var paths = new List<string>();
                    for (int attempt = 0; attempt < 15 && paths.Count == 0; attempt++)
                    {
                        SDL_PumpEvents();
                        IntPtr ids = SDL_GetJoysticks(out int count);
                        if (ids != IntPtr.Zero)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                uint id = (uint)Marshal.ReadInt32(ids, i * 4);
                                if (SDL_GetJoystickVendorForID(id) != vid || SDL_GetJoystickProductForID(id) != pid) continue;
                                string path = Marshal.PtrToStringUTF8(SDL_GetJoystickPathForID(id)) ?? "";
                                bool ours = interfaces.Any(i => string.Equals(NormalizePath(i), NormalizePath(path), StringComparison.OrdinalIgnoreCase))
                                         || (xinputSlot >= 0 && string.Equals(path, $"XInput#{xinputSlot}", StringComparison.OrdinalIgnoreCase));
                                if (ours) paths.Add(path);
                            }
                            SDL_free(ids);
                        }
                        if (paths.Count == 0) Thread.Sleep(200);
                    }
                    paths.Sort(StringComparer.OrdinalIgnoreCase);
                    return paths.Count == 0 ? "(not enumerated)" : string.Join(" | ", paths);
                }
                finally { SDL_Quit(); }
            }
            catch (Exception ex) { return "(SDL error " + ex.GetType().Name + ")"; }
        }
    }
}
