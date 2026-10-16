// The v1 capture: what the 9bd6279 driver answers a program built on
// v1.12.0, recorded for the v1 data path and stale-value tests of decision
// (c) in docs/design/data-driven-devices.md, which compare the v2.0.0
// driver with it.
//
//   CoexistCheck.exe capture <out dir>
//
// The probe removes every controller and installs the driver of the SDK it
// is built against, through a peer on that SDK as the sections do, so the
// probe never embeds HIDMaestro itself. A peer built on the released
// v1.12.0 DLL (legacy\v1.12.0\CoexistPeer.exe) then creates switch-pro,
// dualsense-bt, dualshock-4-v2-bt, dualshock-3-full, xbox-360-wired,
// xbox-360-wheel-v1, amazon-luna-usb, xbox-one-s and dualshock-4-v2, one at
// a time. After each create the probe checks the driver version of every
// devnode it reads against the DriverVer of the INFs that SDK embeds, and
// stops before recording when one differs. It reads each device through
// the interfaces a host reaches and writes one golden case,
// coexist-v1-capture, in device_oracle_check's format (Golden.cs), with
// GoldenStore.Save writing the manifest.
//
// Then the stale-value cases the design note names (decision (c)): for
// each of dualsense-bt, xbox-360-wired, dualshock-3-full and
// amazon-luna-usb, every controller is removed, the values a killed v2.0.0
// program leaves (LeftValues.cs) are written into
// SOFTWARE\HIDMaestro\Controller0, and a new v1.12.0 peer creates the
// profile, which takes index 0. Its streams carry the prefix "stale/", and
// stale/<profile>/left records which of those values the create left in
// place. dualshock-3-full, whose report 0 a left LiveFeatureReport would
// serve from a live store, records its sequence stream: per state the
// frames and report 0's GET_FEATURE, then the output commands.
//
//   <profile>/create      the transport index the create took. The Sony
//                         pairing reports and the XUSB capabilities carry it
//   <profile>/feature     HidD_GetFeature of every feature report id the
//                         descriptor declares, at buffer lengths 0, 1, the
//                         report's length less one, its length, one more and
//                         65: the status (GetLastError), the bytes, and
//                         whether a feature-read packet reached the
//                         program's OutputReceived. featureN when the
//                         controller has more than one HID interface
//   dualshock-3-full/sequence
//                         per scripted state the program submits, its input
//                         reports (ReadFile) and report 0 (HidD_GetFeature).
//                         Then sixaxis.sys commands 1, 2, 3, 9 and 10
//                         (driver.c:1273-1324) through WriteFile and through
//                         HidD_SetOutputReport, each with what reached
//                         OutputReceived
//   switch-pro/simple     the 0x3F frames before any protocol traffic
//   switch-pro/init       SDL's Bluetooth open of a Pro Controller, write by
//                         write, each with its 0x21 reply and its raw
//                         publish to OutputReceived, then SDL's rumble stop
//   switch-pro/stream     3 s of the stream after the open
//   <xbox profile>/xusb   the XUSB companion's IOCTL answers and the rumble
//                         gate: whether each IOCTL_XUSB_SET_STATE reached
//                         OutputReceived
//   <profile>/input       per scripted state the program submits, its input
//                         reports (ReadFile), for every profile but
//                         dualshock-3-full and switch-pro, whose own streams
//                         hold them
//
// Bytes that depend on time are kept out of those streams: the Switch Pro's
// timer byte (report byte 1 of 0x21 and 0x30), how many frames a window
// holds, and the XUSB packet number (GET_STATE bytes 5 to 8). A stream's
// frames are recorded as the distinct frames in the order they came, with
// the timer byte zeroed, and those values go to a sibling stream named
// "<stream>/time". Two captures of the same peer on the same driver repeat
// every other stream byte for byte, so a comparison of two captures skips
// the streams whose names end in "/time" and compares all the rest.
//
// The case seed is 0: nothing here is chosen at random. Requires elevation.
// Exit 0 written, 1 refused or failed with nothing written, 2 when another
// program that embeds HIDMaestro is running.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal static partial class Program
{
    const string CaptureCase = "coexist-v1-capture";
    const string CapturePeerVersion = "v1.12.0";

    /// <summary>How long the probe waits for a packet to reach the peer's
    /// OutputReceived. The driver publishes before it completes the request
    /// (driver.c PublishOutput, then WdfRequestComplete), so a packet that
    /// comes arrives within the SDK's doorbell wake, far inside this.</summary>
    const int OutputWaitMs = 150;

    /// <summary>The profiles in creation order. The order fixes the
    /// transport index each one takes on a machine with no controllers.
    /// The last three complete one profile of each UMDF2 family the
    /// design note's table names.</summary>
    static readonly string[] CaptureProfiles =
    {
        "switch-pro", "dualsense-bt", "dualshock-4-v2-bt", "dualshock-3-full", "xbox-360-wired", "xbox-360-wheel-v1",
        "amazon-luna-usb", "xbox-one-s", "dualshock-4-v2",
    };

    /// <summary>The profiles the stale-value cases create, as case 5's
    /// stale-value plan holds them: the design note's dualsense-bt,
    /// xbox-360-wired, dualshock-3-full and a generic profile
    /// (docs/design/data-driven-devices.md, decision (c), the stale-state
    /// cases).</summary>
    static readonly string[] LeftProfiles = { "dualsense-bt", "xbox-360-wired", "dualshock-3-full", "amazon-luna-usb" };

    static readonly HashSet<string> XusbProfiles = new(StringComparer.Ordinal) { "xbox-360-wired", "xbox-360-wheel-v1" };

    /// <summary>One controller the peer created and the interfaces the
    /// probe reads it through. Stream is the profile with any prefix its
    /// streams carry.</summary>
    sealed record CapturedDevice(int Slot, string Profile, string Stream, string Instance, int Transport, List<string> Hid, string? Xusb);

    /// <summary>A stream and its "/time" sibling, written to the case
    /// together once the stream ends.</summary>
    sealed class CaptureStream
    {
        public readonly string Name;
        readonly List<(string Tag, byte[] Data)> _main = new(), _time = new();
        public CaptureStream(string name) => Name = name;
        public void Out(string tag, byte[] data) => _main.Add((tag, data));
        public void Time(string tag, byte[] data) => _time.Add((tag, data));

        public void WriteTo(GoldenCase g)
        {
            g.Stream(Name);
            foreach (var (tag, data) in _main) g.Out(tag, data);
            if (_time.Count == 0) return;
            g.Stream(Name + "/time");
            foreach (var (tag, data) in _time) g.Out(tag, data);
        }

        /// <summary>Frames as the distinct frames in arrival order, each run
        /// of equal frames once, with the run's length in "/time".</summary>
        public void Runs(string tag, IEnumerable<byte[]> frames)
        {
            byte[]? last = null;
            int run = 0;
            foreach (var f in frames)
            {
                if (last != null && f.AsSpan().SequenceEqual(last)) { run++; continue; }
                if (last != null) Time(tag + ".count", Le32(run));
                Out(tag, f);
                last = f;
                run = 1;
            }
            if (last != null) Time(tag + ".count", Le32(run));
            else Out(tag + ".none", Array.Empty<byte>());
        }
    }

    static byte[] Le32(int v) => BitConverter.GetBytes(v);

    static int Capture(string[] args)
    {
        Console.WriteLine("=== The driver as a v1.12.0 program sees it, and the values a v2.0.0 program leaves ===");
        if (args.Length != 2 || args[1].StartsWith('-'))
        {
            Console.WriteLine("usage: CoexistCheck capture <out dir>");
            return 1;
        }
        string outDir = Path.GetFullPath(args[1]);
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            if (!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("  [FAIL] capture requires elevation: it installs the driver and creates controllers. Nothing was run.");
                return 1;
            }
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "legacy", CapturePeerVersion, "CoexistPeer.exe")))
        {
            Console.WriteLine($"  [FAIL] the {CapturePeerVersion} peer is not built. Build CoexistCheck.csproj. Nothing was run.");
            return 1;
        }
        string? consumer = FindLiveSdkConsumer();
        if (consumer != null)
        {
            Console.WriteLine($"  [SKIP] {consumer} is running and shares the SDK's devices.");
            return 2;
        }
        string? mainVer = EmbeddedDriverVer("hidmaestro.inf"), xusbVer = EmbeddedDriverVer("hidmaestro_xusb.inf");
        if (mainVer == null || xusbVer == null)
        {
            Console.WriteLine($"  [FAIL] the SDK embeds no DriverVer: hidmaestro.inf {mainVer ?? "none"}, hidmaestro_xusb.inf {xusbVer ?? "none"}. Nothing was run.");
            return 1;
        }
        Console.WriteLine($"  the SDK's driver is {mainVer}, its XUSB companion {xusbVer}");

        var g = new GoldenCase(CaptureCase, 0);
        var sw = Stopwatch.StartNew();
        PeerProcess? peer = null;
        try
        {
            Clean();
            // The peer never runs InstallDriver. A v1.12.0 create deploys
            // only when no driver is installed (b4d29d9
            // DeviceOrchestrator.cs:1395-1415), so the driver Clean installed
            // stays, and the version checks below confirm it did.
            peer = PeerProcess.Start($"capture-{CapturePeerVersion}", CapturePeerVersion);
            RecordAll(peer, g, mainVer, xusbVer);
            peer.Dispose();
            peer = null;
            RecordLeft(g, mainVer, xusbVer);
        }
        catch (Exception ex)
        {
            Check("the capture ran to the end", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            peer?.Dispose();
            try { Clean(); }
            catch (Exception ex) { Check("the controllers were removed afterward", false, $"{ex.GetType().Name}: {ex.Message}"); }
        }

        if (s_failures > 0)
        {
            Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed. Nothing was written. ===");
            return 1;
        }
        GoldenStore.Save(outDir, new[] { g });
        Console.WriteLine($"  {g.Streams.Count} streams, {g.Outputs} outputs, {sw.Elapsed.TotalSeconds:F0} s, written to {outDir}");
        foreach (var s in g.Streams) Console.WriteLine($"    {s.Name}  {s.Count}  {s.Digest[..16]}");
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return 0;
    }

    /// <summary>The version of an INF the SDK embeds for this machine's
    /// architecture, from its DriverVer line: what Windows reports as the
    /// DriverVersion of a devnode that runs it.</summary>
    static string? EmbeddedDriverVer(string inf)
    {
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        byte[]? bytes = ReadEmbedded($"HIDMaestro.Native.{arch}.{inf}");
        if (bytes == null) return null;
        var m = Regex.Match(System.Text.Encoding.UTF8.GetString(bytes), @"(?m)^\s*DriverVer\s*=\s*[^,\r\n]+,\s*([0-9.]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Create the controllers through the peer and record each.
    /// The peer and the expected driver versions come in as arguments, so
    /// the compatibility section can run it with another peer or driver.</summary>
    static void RecordAll(PeerProcess peer, GoldenCase g, string mainVer, string xusbVer)
    {
        var devices = new List<CapturedDevice>();
        for (int slot = 0; slot < CaptureProfiles.Length; slot++)
            devices.Add(CreateOne(peer, g, slot, CaptureProfiles[slot], "", mainVer, xusbVer));
        foreach (var d in devices) RecordFeatureSet(peer, g, d);
        foreach (var d in devices)
        {
            if (d.Profile == "dualshock-3-full") RecordDs3(peer, g, d);
            else if (d.Profile == "switch-pro") RecordSwitch(peer, g, d);
            else
            {
                RecordInput(peer, g, d);
                if (d.Xusb != null) RecordXusb(peer, g, d);
            }
        }
    }

    /// <summary>The stale-value cases: per profile, no controller left, the
    /// values a killed v2.0.0 program leaves written under index 0, then a
    /// new v1.12.0 peer's create, which takes index 0, and its streams.</summary>
    static void RecordLeft(GoldenCase g, string mainVer, string xusbVer)
    {
        foreach (string id in LeftProfiles)
        {
            Clean();
            using (var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\HIDMaestro\Controller0", writable: true))
                foreach (var (name, kind, data) in LeftValues.All)
                {
                    if (kind == LeftValues.RegDword) key.SetValue(name, BitConverter.ToInt32(data), RegistryValueKind.DWord);
                    else key.SetValue(name, data, RegistryValueKind.Binary);
                }
            using var peer = PeerProcess.Start($"capture-left-{CapturePeerVersion}", CapturePeerVersion);
            var d = CreateOne(peer, g, 0, id, "stale/", mainVer, xusbVer);
            Check($"stale {id}: the create took index 0, where the values were left", d.Transport == 0, $"transport {d.Transport}");
            if (s_failures > 0) throw new InvalidOperationException($"stale {id}: stopped before recording");
            var left = new CaptureStream($"{d.Stream}/left");
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestro\Controller0"))
                foreach (var (name, kind, data) in LeftValues.All)
                {
                    object? v = key?.GetValue(name);
                    byte[]? now = v switch { byte[] b => b, int i => BitConverter.GetBytes(i), _ => null };
                    left.Out(name, new[] { (byte)(now == null ? 0 : now.AsSpan().SequenceEqual(data) ? 1 : 2) });
                }
            left.WriteTo(g);
            RecordFeatureSet(peer, g, d);
            // The DualShock 3's frames and report 0 go in its sequence
            // stream, as RecordAll records them.
            if (d.Profile == "dualshock-3-full") RecordDs3(peer, g, d);
            else RecordInput(peer, g, d);
            var xusb = new CaptureStream($"{d.Stream}/xusb-interface");
            xusb.Out("present", new[] { (byte)(d.Xusb != null ? 1 : 0) });
            xusb.WriteTo(g);
            if (d.Xusb != null) RecordXusb(peer, g, d);
        }
    }

    /// <summary>One create through the peer, waited on until its HID
    /// interfaces, and its XUSB interface for a companion profile, are
    /// present and run the SDK's drivers. Writes its create stream.</summary>
    static CapturedDevice CreateOne(PeerProcess peer, GoldenCase g, int slot, string id, string prefix, string mainVer, string xusbVer)
    {
        var hidBefore = new HashSet<string>(Native.InterfaceList(Native.HidGuid, null), StringComparer.OrdinalIgnoreCase);
        var xusbBefore = new HashSet<string>(Native.InterfaceList(XusbGuid, null), StringComparer.OrdinalIgnoreCase);
        string r = peer.Send($"create profile {id} -");
        if (!Ok(r)) throw new InvalidOperationException($"{id}: {r}");
        string inst = PeerProcess.Field(r, "instance") ?? "none";
        int transport = int.Parse(PeerProcess.Field(r, "transport") ?? "-1", CultureInfo.InvariantCulture);
        if (!Ok(peer.Send($"outlog {slot} on"))) throw new InvalidOperationException($"{id}: outlog did not start");

        List<string> hid = new();
        WaitFor(() => (hid = OwnedHid(hidBefore, inst, transport)).Count > 0, 15_000);
        // A descriptor with more than one top-level collection gets one
        // interface per collection, and they arrive together.
        if (hid.Count > 0) { Thread.Sleep(500); hid = OwnedHid(hidBefore, inst, transport); }
        string? xusb = null;
        if (XusbProfiles.Contains(id))
            WaitFor(() => (xusb = OwnedXusb(xusbBefore, transport)) != null, 15_000);

        Check($"{prefix}{id}: the {CapturePeerVersion} program created it, and its HID interface is present",
              hid.Count > 0, $"{inst}, transport {transport}, {hid.Count} interface(s)");
        foreach (var path in hid)
        {
            string? parent = Native.InstanceOfInterface(path) is { } child ? Native.ParentOf(child) : null;
            string? v = parent == null ? null : Native.DriverVersionOf(parent);
            Check($"{prefix}{id}: the devnode behind its HID interface runs the SDK's driver", v == mainVer, $"{parent} {v ?? "none"}, expected {mainVer}");
        }
        if (XusbProfiles.Contains(id))
        {
            string? ci = xusb == null ? null : Native.InstanceOfInterface(xusb);
            string? v = ci == null ? null : Native.DriverVersionOf(ci);
            Check($"{prefix}{id}: its XUSB companion runs the SDK's companion", v == xusbVer, $"{ci ?? "no XUSB interface"} {v ?? "none"}, expected {xusbVer}");
        }
        if (s_failures > 0) throw new InvalidOperationException($"{prefix}{id}: stopped before recording");

        var create = new CaptureStream($"{prefix}{id}/create");
        create.Out("transport", Le32(transport));
        create.WriteTo(g);
        return new CapturedDevice(slot, id, prefix + id, inst, transport, hid, xusb);
    }

    /// <summary>Every declared feature report of every HID interface.</summary>
    static void RecordFeatureSet(PeerProcess peer, GoldenCase g, CapturedDevice d)
    {
        byte[]? descriptor = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\HIDMaestro\Controller{d.Transport}")
            ?.GetValue("ReportDescriptor") as byte[];
        if (descriptor == null) throw new InvalidOperationException($"{d.Profile}: no ReportDescriptor under Controller{d.Transport}");
        var declared = HidFeatureLayout.Lengths(descriptor);
        for (int k = 0; k < d.Hid.Count; k++)
            RecordFeatures(peer, g, d, d.Hid[k], d.Hid.Count == 1 ? "feature" : $"feature{k}", declared);
    }

    /// <summary>Per scripted state, the input reports the first HID
    /// interface delivers: the frames the driver sent for what the program
    /// wrote.</summary>
    static void RecordInput(PeerProcess peer, GoldenCase g, CapturedDevice d)
    {
        var s = new CaptureStream($"{d.Stream}/input");
        using var cap = HidCapture.Open(d.Hid[0]) ?? throw new InvalidOperationException($"{d.Profile}: input did not open");
        foreach (string state in Ds3States)
        {
            long mark = cap.Mark();
            string r = peer.Send($"submit {d.Slot} {state} 6");
            if (!Ok(r)) throw new InvalidOperationException($"{d.Profile}: {r}");
            Thread.Sleep(150);
            s.Runs(state + ".input", cap.Since(mark).Select(x => x.Report));
        }
        s.WriteTo(g);
    }

    /// <summary>New HID interfaces that belong to the controller: their
    /// devnode descends from the controller's devnode, or carries its
    /// transport index in Device Parameters\ControllerIndex, where the SDK
    /// writes it for the driver (DeviceOrchestrator).</summary>
    static List<string> OwnedHid(HashSet<string> before, string instance, int transport) =>
        Native.InterfaceList(Native.HidGuid, null)
            .Where(p => !before.Contains(p))
            .Where(p => Native.InstanceOfInterface(p) is { } node
                        && (DescendsFrom(node, instance) || ControllerIndexOf(node) == transport))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    static string? OwnedXusb(HashSet<string> before, int transport) =>
        Native.InterfaceList(XusbGuid, null)
            .Where(p => !before.Contains(p))
            .FirstOrDefault(p => Native.InstanceOfInterface(p) is { } node && ControllerIndexOf(node) == transport);

    static bool DescendsFrom(string node, string ancestor)
    {
        string? n = node;
        for (int i = 0; i < 4 && n != null; i++)
        {
            n = Native.ParentOf(n);
            if (string.Equals(n, ancestor, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>The ControllerIndex a devnode's driver reads from its
    /// hardware key (driver.c InitInstancePaths, companion.c
    /// CompanionDeviceAdd).</summary>
    static int ControllerIndexOf(string instance) =>
        Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instance}\Device Parameters")
            ?.GetValue("ControllerIndex") is int v ? v : -1;

    // ── Feature reports ────────────────────────────────────────────────

    /// <summary>The buffer lengths at which a feature read's result can
    /// change: none, the report id alone, one short, exact, one over, and
    /// 65, the length hidapi and SDL read Sony reports into.</summary>
    static IEnumerable<int> FeatureBoundaries(int length) =>
        new[] { 0, 1, length - 1, length, length + 1, 65 }.Where(n => n >= 0).Distinct().OrderBy(n => n);

    static void RecordFeatures(PeerProcess peer, GoldenCase g, CapturedDevice d, string path, string what,
                               SortedDictionary<byte, int> declared)
    {
        var s = new CaptureStream($"{d.Stream}/{what}");
        s.Out("declared", declared.SelectMany(kv => new[] { kv.Key, (byte)kv.Value, (byte)(kv.Value >> 8) }).ToArray());
        using var h = HidIo.OpenSync(path);
        if (h.IsInvalid) throw new InvalidOperationException($"{d.Profile}: {path} did not open ({Marshal.GetLastWin32Error()})");
        foreach (var (rid, length) in declared)
            foreach (int n in FeatureBoundaries(length))
            {
                var buf = new byte[Math.Max(n, 1)];
                buf[0] = rid;
                ClearOutputs(peer, d.Slot);
                bool ok = HidIo.HidD_GetFeature(h, buf, n);
                int err = ok ? 0 : Marshal.GetLastWin32Error();
                string tag = $"id{rid:X2}.len{n}";
                s.Out(tag + ".status", Status(ok, err));
                s.Out(tag + ".bytes", buf[..n]);
                // HMOutputSource.HidFeatureRead is 3 (HMOutputPacket.cs).
                string o = peer.Send($"outlog {d.Slot} 3 {rid:X2} {OutputWaitMs}");
                s.Out(tag + ".notify", Notified(o));
            }
        s.WriteTo(g);
    }

    static byte[] Status(bool ok, int err)
    {
        var b = new byte[5];
        b[0] = (byte)(ok ? 1 : 0);
        BitConverter.TryWriteBytes(b.AsSpan(1), err);
        return b;
    }

    /// <summary>Forget the packets the peer kept for a controller, so the
    /// next ask sees only what the next request produced. A packet another
    /// program's request produced in between can still show in the ask's
    /// count, which is why the count is not recorded.</summary>
    static void ClearOutputs(PeerProcess peer, int slot)
    {
        string r = peer.Send($"outlog {slot} clear");
        if (!Ok(r)) throw new InvalidOperationException($"outlog: {r}");
    }

    /// <summary>An outlog reply as bytes: 1 and the packet's bytes when the
    /// packet came, 0 when it did not.</summary>
    static byte[] Notified(string reply)
    {
        if (!Ok(reply)) throw new InvalidOperationException($"outlog: {reply}");
        string data = PeerProcess.Field(reply, "data") ?? "-";
        var b = new List<byte> { (byte)(PeerProcess.Field(reply, "hit") == "1" ? 1 : 0) };
        if (data != "-") b.AddRange(Convert.FromHexString(data));
        return b.ToArray();
    }

    // ── DualShock 3 in sixaxis.sys form ────────────────────────────────

    /// <summary>The scripted states, in order. Peer.cs Scripted holds them.</summary>
    static readonly string[] Ds3States = { "neutral", "ds3-a", "ds3-b", "ds3-c" };

    /// <summary>Output report 0 as SDL's sixaxis driver writes it: 49 bytes,
    /// the report id 0, then a sixaxis.sys command (SDL_hidapi_ps3.c:50-74,
    /// HIDAPI_DriverPS3SonySixaxis_SendJoystickEffect :1252-1269).</summary>
    static byte[] Ds3Command(params byte[] head)
    {
        var r = new byte[49];
        head.CopyTo(r, 0);
        return r;
    }

    static readonly (string Name, byte[] Report)[] Ds3Commands =
    {
        // Set motors: the small motor on for 0xFF, the large one at 200 for
        // 0xFF (UpdateRumbleSonySixaxis, SDL_hidapi_ps3.c:1441-1459).
        ("cmd2", Ds3Command(0x00, 0x02, 0x00, 0x00, 0x00, 0xFF, 0x01, 0xFF, 0xC8)),
        // Set LEDs: LED 1 on, player 0 (UpdateLEDsSonySixaxis :1461-1478).
        ("cmd1", Ds3Command(0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01)),
        // Block LEDs: byte 5 nonzero blocks (:63-65).
        ("cmd3", Ds3Command(0x00, 0x03, 0x00, 0x00, 0x00, 0x01)),
        // Refresh driver settings and clear pairing, no parameters (:67-73).
        ("cmd9", Ds3Command(0x00, 0x09)),
        ("cmd10", Ds3Command(0x00, 0x0A)),
    };

    static void RecordDs3(PeerProcess peer, GoldenCase g, CapturedDevice d)
    {
        var s = new CaptureStream($"{d.Stream}/sequence");
        string path = d.Hid[0];
        using var h = HidIo.OpenSync(path);
        if (h.IsInvalid) throw new InvalidOperationException($"{d.Profile}: {path} did not open ({Marshal.GetLastWin32Error()})");
        using var cap = HidCapture.Open(path) ?? throw new InvalidOperationException($"{d.Profile}: input did not open");
        foreach (string state in Ds3States)
        {
            long mark = cap.Mark();
            string r = peer.Send($"submit {d.Slot} {state} 6");
            if (!Ok(r)) throw new InvalidOperationException($"{d.Profile}: {r}");
            Thread.Sleep(150);
            s.Runs(state + ".input", cap.Since(mark).Select(x => x.Report));
            // Report 0, the report SDL's sixaxis driver polls (UpdateDevice,
            // SDL_hidapi_ps3.c:1397-1398), at its declared length: 49 bytes
            // and the id.
            var f = new byte[50];
            bool ok = HidIo.HidD_GetFeature(h, f, f.Length);
            s.Out(state + ".feature0.status", Status(ok, ok ? 0 : Marshal.GetLastWin32Error()));
            s.Out(state + ".feature0", f);
        }
        foreach (var (entry, write) in new (string, Func<byte[], bool>)[]
                 {
                     ("write", rep => HidIo.WriteFile(h, rep, rep.Length, out int n, IntPtr.Zero) && n == rep.Length),
                     ("setoutput", rep => HidIo.HidD_SetOutputReport(h, rep, rep.Length)),
                 })
            foreach (var (name, report) in Ds3Commands)
            {
                ClearOutputs(peer, d.Slot);
                bool ok = write(report);
                s.Out($"{entry}.{name}.status", Status(ok, ok ? 0 : Marshal.GetLastWin32Error()));
                // The driver publishes the native report 0x01 body as
                // HMOutputSource.HidOutput (0), driver.c:1323.
                s.Out($"{entry}.{name}.published", Notified(peer.Send($"outlog {d.Slot} 0 01 {OutputWaitMs}")));
            }
        s.WriteTo(g);
    }

    // ── Switch Pro ─────────────────────────────────────────────────────

    /// <summary>SDL's writes when it opens a Pro Controller over Bluetooth,
    /// in order, with the default hints. HIDAPI_DriverSwitch_InitDevice
    /// reads the device info (SDL_hidapi_switch.c:1594-1600, BReadDeviceInfo
    /// :676-687). HIDAPI_DriverSwitch_OpenJoystick skips BTrySetupUSB on
    /// Bluetooth (:1654-1659), reads the user then the factory stick
    /// calibration (LoadStickCalibration :990-1021, offsets and lengths
    /// :134-140), the IMU calibration (LoadIMUCalibration :1110-1116,
    /// :142-144), enables vibration (:1679-1680, :731-734), sets input mode 0x30
    /// because SDL_JOYSTICK_ENHANCED_REPORTS defaults to on (:1682-1684,
    /// SetEnhancedReportHint :926-944, GetDefaultInputMode :829-873,
    /// SetInputMode :735-751), leaves the Home LED alone with its hint unset
    /// (SDL_HomeLEDHintChanged :776-793) and lights player 1's LED
    /// (UpdateSlotLED :795-806, :1712-1715). A program that turns the
    /// sensors on then sends EnableIMU 1 (SetJoystickSensorsEnabled
    /// :1934-1949, SetIMUEnabled :973-977). SDL skips the 0x03 write when
    /// its first read already saw mode 0x30 (:744-746). This list sends it,
    /// as SDL does after a first read of 0x3F or of nothing.</summary>
    static readonly (byte Subcommand, byte[] Args)[] SdlSwitchOpen =
    {
        (0x02, Array.Empty<byte>()),
        (0x10, new byte[] { 0x10, 0x80, 0x00, 0x00, 0x16 }),
        (0x10, new byte[] { 0x3D, 0x60, 0x00, 0x00, 0x12 }),
        (0x10, new byte[] { 0x20, 0x60, 0x00, 0x00, 0x18 }),
        (0x48, new byte[] { 0x01 }),
        (0x03, new byte[] { 0x30 }),
        (0x30, new byte[] { 0x01 }),
        (0x40, new byte[] { 0x01 }),
    };

    /// <summary>SDL's neutral rumble for one side (SetNeutralRumble,
    /// SDL_hidapi_switch.c:604-608).</summary>
    static readonly byte[] SwitchNeutralRumble = { 0x00, 0x01, 0x40, 0x40 };

    /// <summary>A 49-byte Bluetooth output packet (WritePacket pads to
    /// k_unSwitchBluetoothPacketLength, :486-506, :129-131): the packet
    /// type, SDL's command counter, both sides' rumble, then the rest.</summary>
    static byte[] SwitchPacket(byte type, int counter, byte[] tail)
    {
        var p = new byte[49];
        p[0] = type;
        p[1] = (byte)(counter & 0xF);
        SwitchNeutralRumble.CopyTo(p, 2);
        SwitchNeutralRumble.CopyTo(p, 6);
        tail.CopyTo(p, 10);
        return p;
    }

    static byte[] ZeroTimer(byte[] report)
    {
        var r = (byte[])report.Clone();
        if (r.Length > 1 && r[0] is 0x21 or 0x30 or 0x31) r[1] = 0;
        return r;
    }

    static void RecordSwitch(PeerProcess peer, GoldenCase g, CapturedDevice d)
    {
        using var port = HidIo.OverlappedPort.Open(d.Hid[0]) ?? throw new InvalidOperationException($"{d.Profile}: did not open for read and write");
        string r = peer.Send($"submit {d.Slot} switch-a 6");
        if (!Ok(r)) throw new InvalidOperationException($"{d.Profile}: {r}");

        // Before any protocol traffic the driver streams simple-mode 0x3F
        // frames (driver.c:1843-1849).
        var simple = new CaptureStream($"{d.Stream}/simple");
        simple.Runs("frame", port.ReadFor(300));
        simple.WriteTo(g);

        // SDL's counter starts at 0 in a zeroed context (:1576) and steps by
        // one per packet (ConstructSubcommand :469-484, WriteRumble :644-645).
        var init = new CaptureStream($"{d.Stream}/init");
        int counter = 0;
        foreach (var (sub, args) in SdlSwitchOpen)
        {
            var tail = new byte[1 + args.Length];
            tail[0] = sub;
            args.CopyTo(tail, 1);
            byte[] packet = SwitchPacket(0x01, counter++, tail);
            string tag = $"sub{sub:X2}";
            if (sub == 0x10) tag += $".{args[1]:X2}{args[0]:X2}";
            init.Out(tag + ".write", packet);
            ClearOutputs(peer, d.Slot);
            bool ok = port.Write(packet, out int err);
            init.Out(tag + ".status", Status(ok, err));
            // ReadSubcommandReply's rule (:414-437): a 0x21 with this
            // subcommand id and the ack's top bit, and for an SPI read the
            // echoed address and length. SDL waits 100 ms. The probe waits
            // a second so a late reply reads as late, not as missing.
            byte[]? reply = ok ? port.ReadUntil(1000, x => x.Length > 20 && x[0] == 0x21 && x[14] == sub && (x[13] & 0x80) != 0
                                                       && (sub != 0x10 || x.AsSpan(15, 5).SequenceEqual(args))) : null;
            init.Out(tag + ".reply", reply == null ? Array.Empty<byte>() : ZeroTimer(reply));
            if (reply != null) init.Time(tag + ".timer", new[] { reply[1] });
            // The driver also publishes the raw 0x01 report (driver.c:2910-2915).
            init.Out(tag + ".published", Notified(peer.Send($"outlog {d.Slot} 0 01 {OutputWaitMs} {Convert.ToHexString(packet, 1, 48)}")));
        }
        // Rumble stop: both sides neutral (ActuallyRumbleJoystick :1742-1768,
        // WriteRumble :638-651). No reply, published raw.
        byte[] rumble = SwitchPacket(0x10, counter++, Array.Empty<byte>());
        init.Out("rumble.write", rumble);
        ClearOutputs(peer, d.Slot);
        bool rok = port.Write(rumble, out int rerr);
        init.Out("rumble.status", Status(rok, rerr));
        init.Out("rumble.published", Notified(peer.Send($"outlog {d.Slot} 0 10 {OutputWaitMs} {Convert.ToHexString(rumble, 1, 48)}")));
        init.WriteTo(g);

        var stream = new CaptureStream($"{d.Stream}/stream");
        var frames = port.ReadFor(3000);
        foreach (var f in frames)
            if (f.Length > 1 && f[0] is 0x21 or 0x30 or 0x31) stream.Time("timer", new[] { f[1] });
        stream.Runs("frame", frames.Select(ZeroTimer));
        stream.WriteTo(g);
    }

    // ── XUSB companion ─────────────────────────────────────────────────

    // companion.c:29-38, and OpenXInput's CTL_CODE values (OpenXinput.cpp:427-438).
    const uint IOCTL_XUSB_GET_INFORMATION = 0x80006000, IOCTL_XUSB_GET_CAPABILITIES = 0x8000E004,
               IOCTL_XUSB_GET_LED_STATE = 0x8000E008, IOCTL_XUSB_GET_STATE = 0x8000E00C,
               IOCTL_XUSB_SET_STATE = 0x8000A010, IOCTL_XUSB_GET_BATTERY_INFO = 0x8000E018,
               IOCTL_XUSB_GET_INFORMATION_EX = 0x8000E3FC;

    /// <summary>InBaseBusInformation as OpenXInput sends it (OpenXinput.cpp
    /// :198-219, :2488-2494): 36 bytes, version 0x0104, unk0 2.</summary>
    static byte[] BaseBusRequest()
    {
        var b = new byte[36];
        b[0] = 0x04; b[1] = 0x01; b[2] = 0x02;
        return b;
    }

    /// <summary>The read-only IOCTLs, each with the input and output sizes
    /// xinput1_4 uses as OpenXInput reconstructs them.</summary>
    static readonly (string Name, uint Code, byte[] In, int OutLen)[] XusbReads =
    {
        // ReceiveIoctl, no input, OutDeviceInfos_t (:2359, :95-105).
        ("information", IOCTL_XUSB_GET_INFORMATION, Array.Empty<byte>(), 12),
        // InBaseRequest_t 0x0101 into GamepadCapabilities0101 (:2446-2455,
        // :63-67, :152-172), and 0x0102 into GamepadCapabilities0102
        // (:2461-2466, :174-196).
        ("capabilities24", IOCTL_XUSB_GET_CAPABILITIES, new byte[] { 0x01, 0x01, 0x00 }, 24),
        ("capabilities36", IOCTL_XUSB_GET_CAPABILITIES, new byte[] { 0x02, 0x01, 0x00 }, 36),
        // OutGetLEDBuffer_t (GetLEDState :2588-2600, :89-93).
        ("led", IOCTL_XUSB_GET_LED_STATE, new byte[] { 0x01, 0x01, 0x00 }, 3),
        // InGamepadBatteryInformation0102 for the gamepad (:2528-2536, :241-252).
        ("battery", IOCTL_XUSB_GET_BATTERY_INFO, new byte[] { 0x02, 0x01, 0x00, 0x00 }, 4),
        // OutBaseBusInformation, 949 bytes packed (:221-239).
        ("informationex", IOCTL_XUSB_GET_INFORMATION_EX, BaseBusRequest(), 949),
    };

    /// <summary>InSetState_t (OpenXinput.cpp:72-79): vibration as
    /// SendDeviceVibration sends it (:2342-2353) and an LED state as
    /// SendLEDState sends it (:2329-2340).</summary>
    static readonly (string Name, byte[] In)[] XusbWrites =
    {
        ("vibration", new byte[] { 0x00, 0x00, 0x80, 0x40, 0x02 }),
        ("led", new byte[] { 0x00, 0x02, 0x00, 0x00, 0x01 }),
    };

    static void RecordXusb(PeerProcess peer, GoldenCase g, CapturedDevice d)
    {
        var s = new CaptureStream($"{d.Stream}/xusb");
        using var h = HidIo.OpenSync(d.Xusb!);
        if (h.IsInvalid) throw new InvalidOperationException($"{d.Profile}: {d.Xusb} did not open ({Marshal.GetLastWin32Error()})");
        foreach (var (name, code, input, outLen) in XusbReads)
        {
            var (ok, err, bytes) = HidIo.Ioctl(h, code, input, outLen);
            s.Out(name + ".status", Status(ok, err));
            s.Out(name, bytes);
        }
        // GET_STATE with InBaseRequest_t 0x0101 into GamepadState0101, 29
        // bytes (GetLatestDeviceInfo :2366-2400, :128-150). Bytes 5 to 8 are
        // the packet number, which counts every caller's reads.
        foreach (string state in new[] { "neutral", "xbox-a" })
        {
            string r = peer.Send($"submit {d.Slot} {state} 6");
            if (!Ok(r)) throw new InvalidOperationException($"{d.Profile}: {r}");
            Thread.Sleep(100);
            var (ok, err, bytes) = HidIo.Ioctl(h, IOCTL_XUSB_GET_STATE, new byte[] { 0x01, 0x01, 0x00 }, 29);
            s.Out($"state.{state}.status", Status(ok, err));
            if (bytes.Length >= 9)
            {
                s.Time($"state.{state}.packet", bytes[5..9]);
                Array.Clear(bytes, 5, 4);
            }
            s.Out($"state.{state}", bytes);
        }
        // The rumble gate (companion.c:928-983): the companion publishes the
        // input as HMOutputSource.XInput (2) with report id 0.
        foreach (var (name, input) in XusbWrites)
        {
            ClearOutputs(peer, d.Slot);
            var (ok, err, _) = HidIo.Ioctl(h, IOCTL_XUSB_SET_STATE, input, 0);
            s.Out($"setstate.{name}.status", Status(ok, err));
            s.Out($"setstate.{name}.published", Notified(peer.Send($"outlog {d.Slot} 2 00 {OutputWaitMs} {Convert.ToHexString(input)}")));
        }
        s.WriteTo(g);
    }

    // ── Raw I/O ────────────────────────────────────────────────────────

    static class HidIo
    {
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3,
                   FILE_FLAG_OVERLAPPED = 0x40000000;
        const int ERROR_IO_PENDING = 997;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string p, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, int len);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(SafeFileHandle h, byte[] buf, int len, out int written, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? inBuf, int inLen, byte[]? outBuf, int outLen, out int returned, IntPtr overlapped);

        /// <summary>A handle for synchronous requests: feature reads, output
        /// writes and IOCTLs. The caller checks IsInvalid.</summary>
        public static SafeFileHandle OpenSync(string path) =>
            CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

        public static (bool Ok, int Err, byte[] Bytes) Ioctl(SafeFileHandle h, uint code, byte[] input, int outLen)
        {
            byte[]? output = outLen > 0 ? new byte[outLen] : null;
            bool ok = DeviceIoControl(h, code, input.Length > 0 ? input : null, input.Length, output, outLen, out int returned, IntPtr.Zero);
            int err = ok ? 0 : Marshal.GetLastWin32Error();
            return (ok, err, output == null ? Array.Empty<byte>() : output[..Math.Clamp(returned, 0, outLen)]);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern unsafe bool ReadFile(SafeFileHandle h, byte* buf, int len, IntPtr read, NativeOverlapped* ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern unsafe bool WriteFile(SafeFileHandle h, byte* buf, int len, IntPtr written, NativeOverlapped* ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern unsafe bool GetOverlappedResultEx(SafeFileHandle h, NativeOverlapped* ov, out int transferred, uint ms, bool alertable);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern unsafe bool GetOverlappedResult(SafeFileHandle h, NativeOverlapped* ov, out int transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern unsafe bool CancelIoEx(SafeFileHandle h, NativeOverlapped* ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateEventW(IntPtr sa, bool manualReset, bool initialState, IntPtr name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ResetEvent(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);

        /// <summary>One HID interface opened for overlapped reads and writes,
        /// as hidapi opens it, so a read can time out. The buffers and the
        /// OVERLAPPED live in native memory, where a pending request can
        /// write to them however long it stays pending. A request that times
        /// out is canceled, and its cancellation is waited for before the
        /// buffer is used again.</summary>
        public sealed unsafe class OverlappedPort : IDisposable
        {
            readonly SafeFileHandle _h;
            readonly int _inLen;
            readonly byte* _in, _out;
            readonly NativeOverlapped* _readOv, _writeOv;
            readonly IntPtr _readEvent, _writeEvent;

            OverlappedPort(SafeFileHandle h, int inLen)
            {
                _h = h;
                _inLen = inLen;
                _in = (byte*)NativeMemory.AllocZeroed((nuint)inLen);
                _out = (byte*)NativeMemory.AllocZeroed(512);
                _readOv = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
                _writeOv = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
                _readEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
                _writeEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            }

            public static OverlappedPort? Open(string path)
            {
                var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (h.IsInvalid) { h.Dispose(); return null; }
                // InputReportByteLength from the preparsed data (HIDP_CAPS
                // offset 4), the length HidClass completes every read at.
                IntPtr fh = h.DangerousGetHandle();
                int len = 0;
                if (Native.HidD_GetPreparsedData(fh, out IntPtr pp))
                {
                    var caps = new byte[64];
                    if (Native.HidP_GetCaps(pp, caps) == Native.HIDP_STATUS_SUCCESS) len = BitConverter.ToUInt16(caps, 4);
                    Native.HidD_FreePreparsedData(pp);
                }
                if (len == 0) { h.Dispose(); return null; }
                return new OverlappedPort(h, len);
            }

            /// <summary>One input report, or null when none came in time.</summary>
            public byte[]? Read(int timeoutMs)
            {
                *_readOv = default;
                ResetEvent(_readEvent);
                _readOv->EventHandle = _readEvent;
                if (!ReadFile(_h, _in, _inLen, IntPtr.Zero, _readOv) && Marshal.GetLastWin32Error() != ERROR_IO_PENDING)
                    return null;
                if (!GetOverlappedResultEx(_h, _readOv, out int n, (uint)Math.Max(timeoutMs, 0), false))
                {
                    CancelIoEx(_h, _readOv);
                    // A read that completed before the cancel landed still
                    // carries a report. Keep it rather than lose it.
                    if (!GetOverlappedResult(_h, _readOv, out n, true)) return null;
                }
                return n > 0 ? new ReadOnlySpan<byte>(_in, n).ToArray() : null;
            }

            /// <summary>Every report that arrives within the window.</summary>
            public List<byte[]> ReadFor(int ms)
            {
                var list = new List<byte[]>();
                var sw = Stopwatch.StartNew();
                long left;
                while ((left = ms - sw.ElapsedMilliseconds) > 0)
                    if (Read((int)left) is { } r) list.Add(r);
                return list;
            }

            /// <summary>The first report <paramref name="accept"/> takes
            /// within the window, skipping the others as SDL's reply readers
            /// do.</summary>
            public byte[]? ReadUntil(int ms, Func<byte[], bool> accept)
            {
                var sw = Stopwatch.StartNew();
                long left;
                while ((left = ms - sw.ElapsedMilliseconds) > 0)
                    if (Read((int)left) is { } r && accept(r)) return r;
                return null;
            }

            public bool Write(byte[] report, out int err)
            {
                err = 0;
                if (report.Length > 512) { err = 87; return false; }
                new ReadOnlySpan<byte>(report).CopyTo(new Span<byte>(_out, report.Length));
                *_writeOv = default;
                ResetEvent(_writeEvent);
                _writeOv->EventHandle = _writeEvent;
                if (!WriteFile(_h, _out, report.Length, IntPtr.Zero, _writeOv) && (err = Marshal.GetLastWin32Error()) != ERROR_IO_PENDING)
                    return false;
                err = 0;
                if (!GetOverlappedResultEx(_h, _writeOv, out int n, 1000, false))
                {
                    err = Marshal.GetLastWin32Error();
                    CancelIoEx(_h, _writeOv);
                    GetOverlappedResult(_h, _writeOv, out n, true);
                    return false;
                }
                if (n != report.Length) { err = -n; return false; }
                return true;
            }

            public void Dispose()
            {
                CancelIoEx(_h, null);
                GetOverlappedResult(_h, _readOv, out _, true);
                GetOverlappedResult(_h, _writeOv, out _, true);
                _h.Dispose();
                CloseHandle(_readEvent);
                CloseHandle(_writeEvent);
                NativeMemory.Free(_in);
                NativeMemory.Free(_out);
                NativeMemory.Free(_readOv);
                NativeMemory.Free(_writeOv);
            }
        }
    }
}
