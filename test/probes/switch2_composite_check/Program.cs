// Switch 2 Pro Controller composite persona check (issue #66).
//
// The persona is the two-interface USB device a Pro Controller 2 on
// firmware 1.1.5 presents: HID on interface 0 and a vendor interface with a
// bulk pair on interface 1, where a host sends the command protocol that
// starts the pad. Every expectation below is a literal from a source other
// than the code under test:
//
//   * a link-layer capture of a console driving a real pad
//     (switch2_controller_research captures/usb/rumble-procon-gccon.pcapng,
//     "frame N" below), for both descriptors and every reply it shows
//   * that repository's commands.md, hid_reports.md and memory_layout.md
//   * SDL's SDL_hidapi_switch2.c, the client the persona has to satisfy
//   * the Microsoft OS 1.0 tables two device-side emulators serve
//
// Part A plays usbip-win2's role over loopback TCP against the in-process
// server and the emulated device. No PnP, no kernel driver.
// Part B attaches the persona for real and drives it through Windows: PnP
// binding, WinUSB on interface 1, HidUsb on interface 0.
// Part C is the acceptance client: SDL3 with libusb opens the persona,
// reads its motion back and rumbles it.
// Part D attaches two personas and checks each answers its own handle.
// Part E hands the persona to the Steam client and reads Steam's logs.
//
// Requires elevation. Exit 0 PASS / 1 FAIL / 2 when another SDK consumer or
// the transport kept every live part from running. `--offline` runs part A alone,
// and `--steam-explore` prints a Steam session without asserting anything.
// C and the SDL half of D skip without the sibling
// SDL3-build/build-unfiltered build, and E skips without a Steam install.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static partial class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));
    static string X(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

    const string ProfileId = "switch2-pro-controller-composite";

    // Frame 44.
    static readonly byte[] DeviceDescriptor = H("12010002ef0201407e056920010101020301");

    // Frames 71 and 76: 2 interfaces, self powered, 500 mA. Interface 0 HID
    // with interrupt IN 0x81 and OUT 0x01, 64 bytes, interval 4. Interface 1
    // class FF with bulk OUT 0x02 and bulk IN 0x82, 64 bytes.
    static readonly byte[] ConfigurationDescriptor = H(
        "09025000020104c0fa" + "080b000103000000" + "090400000203000005" + "092111010001226100" +
        "07058103400004" + "07050103400004" + "080b0101ff000000" + "0904010002ff000006" +
        "07050202400000" + "07058202400000");

    // MSFT100, vendor code 0x01.
    static readonly byte[] MsOsString = H("12034d005300460054003100300030000100");

    // Extended compatible ID: WINUSB for interface 1.
    static readonly byte[] MsOsCompatId = H(
        "28000000" + "0001" + "0400" + "01" + "00000000000000" +
        "01" + "01" + "57494e5553420000" + "0000000000000000" + "000000000000");

    const string InterfaceGuid = "{6F13725E-EF0E-4FD3-AE5F-B2DE989EC825}";

    // Extended properties: DeviceInterfaceGUID, REG_SZ.
    static byte[] MsOsProperties()
    {
        var name = Encoding.Unicode.GetBytes("DeviceInterfaceGUID\0");
        var data = Encoding.Unicode.GetBytes(InterfaceGuid + "\0");
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int section = 4 + 4 + 2 + name.Length + 4 + data.Length;
        w.Write((uint)(10 + section)); w.Write((ushort)0x0100); w.Write((ushort)5); w.Write((ushort)1);
        w.Write((uint)section); w.Write((uint)1); w.Write((ushort)name.Length); w.Write(name);
        w.Write((uint)data.Length); w.Write(data);
        return ms.ToArray();
    }

    // memory_layout.md 0x13080 and 0x130C0, and frames 8837 and 8902.
    static readonly byte[] StickBlockConstant = H(
        "01add99a555665a0000aa0000ae2200ee2200e9aadd99aadd90aa5500aa5502ff6622ff6620affff");

    static int Main(string[] args)
    {
        // --diag: the SDK's diagnostic log, which records every bulk
        // transfer, to %TEMP%\HIDMaestro	eardown_diag.log. Set before the
        // SDK reads it.
        if (args.Contains("--diag")) Environment.SetEnvironmentVariable("HIDMAESTRO_DIAG", "1");
        Console.WriteLine("=== Switch 2 Pro Controller composite persona (issue #66) ===");

        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("  [FAIL] requires elevation (Global\\ sections and CreateController)");
                return 1;
            }
        }

        bool offline = args.Contains("--offline");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        if (args.Contains("--steam-explore")) return SteamExplore(ctx, 45);

        PartA(ctx);
        if (!offline)
        {
            PartB(ctx);
            PartC(ctx);
            PartD(ctx);
            QuitSdl();   // before Steam, which takes the vendor interface for itself
            PartE(ctx);
        }

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        if (s_failures != 0) return 1;
        // Nothing live ran: say so, the way the other device probes do.
        return s_liveSkipped ? 2 : 0;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Part A: the wire, offline
    // ══════════════════════════════════════════════════════════════════════

    const int IndexA = 61;       // far from live controller indices
    const int IndexSony = 62;
    const int IndexValve = 63;

    static void PartA(HMContext ctx)
    {
        Console.WriteLine("\n== Part A: the wire, with this probe in the driver's place ==");

        var hm = ctx.GetProfile(ProfileId);
        Check($"{ProfileId} is in the catalog", hm != null);
        if (hm == null) return;
        var profile = hm.Inner;

        Console.WriteLine("\n-- A1 profile --");
        var plain = ctx.GetProfile("switch2-pro-controller")!;
        Check("takes the USB/IP backend", hm.RequiresUsbipBackend);
        Check("the UMDF2 profile is untouched and still not a USB/IP persona", !plain.RequiresUsbipBackend);
        Check("057E:2069", hm.VendorId == 0x057E && hm.ProductId == 0x2069, $"{hm.VendorId:X4}:{hm.ProductId:X4}");
        Check("strings 1 and 2 are Nintendo and Switch 2 Pro Controller (descriptors.md)",
              profile.ManufacturerString == "Nintendo" && profile.ProductString == "Switch 2 Pro Controller",
              $"{profile.ManufacturerString} / {profile.ProductString}");
        Check("no captured serial, so string 3 is the identity's", string.IsNullOrEmpty(profile.SerialString));
        Check("the report descriptor is the 97 bytes switch2-pro-controller carries",
              profile.GetDescriptorBytes()!.SequenceEqual(plain.Inner.GetDescriptorBytes()!)
              && profile.GetDescriptorBytes()!.Length == 97);
        Check("report 0x09's declared layout equals switch2-pro-controller's field for field",
              System.Text.Json.JsonSerializer.Serialize(profile.ExtendedReport)
              == System.Text.Json.JsonSerializer.Serialize(plain.Inner.ExtendedReport));
        Check("stick and trigger discovery match switch2-pro-controller",
              hm.Sticks.Count == 2 && hm.Sticks.Count == plain.Sticks.Count
              && hm.Sticks[0].XAxis == plain.Sticks[0].XAxis && hm.Sticks[0].YAxis == plain.Sticks[0].YAxis
              && hm.Sticks[1].XAxis == plain.Sticks[1].XAxis && hm.Sticks[1].YAxis == plain.Sticks[1].YAxis
              && HMController.ResolveCanonicalAxis(hm.AxisMap, "lefttrigger", HMAxis.Z)
                 == HMController.ResolveCanonicalAxis(plain.AxisMap, "lefttrigger", HMAxis.Z)
              && HMController.ResolveCanonicalAxis(hm.AxisMap, "righttrigger", HMAxis.Rz)
                 == HMController.ResolveCanonicalAxis(plain.AxisMap, "righttrigger", HMAxis.Rz));

        // The right stick's Y is usage Rz, which is also the default right
        // trigger axis. The axisMap moves the trigger to Ry, and both the
        // helper and the resolver have to honor that, on the UMDF2 profile
        // as on this one.
        foreach (var p in new[] { plain, hm })
        {
            var axes = HMGamepadStateHelpers.StandardAxes(p, 0.25f, 0.25f, 0.75f, 1f, 0.5f, 0.75f);
            var lt = HMController.ResolveCanonicalAxis(p.AxisMap, "lefttrigger", HMAxis.Z);
            var rt = HMController.ResolveCanonicalAxis(p.AxisMap, "righttrigger", HMAxis.Rz);
            Check($"{p.Id}: StandardAxes keeps the right stick's Y on Rz and puts the triggers on the axes SubmitState reads",
                  axes[HMAxis.X] == 0.25f && axes[HMAxis.Y] == 0.25f && axes[HMAxis.Rx] == 0.75f && axes[HMAxis.Rz] == 1f
                  && rt != HMAxis.Rz && axes[lt] == 0.5f && axes[rt] == 0.75f,
                  string.Join(" ", axes.Select(kv => $"{kv.Key}={kv.Value}")));
            var stickOnly = new Dictionary<HMAxis, float> { [HMAxis.Rz] = 1f };
            Check($"{p.Id}: the right stick pushed down, with no trigger axis written, pulls no trigger",
                  HMController.ResolveTrigger(stickOnly, p.Triggers, 1, rt) == 0.0
                  && HMController.ResolveTrigger(stickOnly, p.Triggers, 0, lt) == 0.0);
            Check($"{p.Id}: a value on its trigger axes is read",
                  HMController.ResolveTrigger(axes, p.Triggers, 0, lt) == 0.5 && HMController.ResolveTrigger(axes, p.Triggers, 1, rt) == 0.75);
        }

        var server = UsbipServer.GetOrStart();
        var identity = DeviceIdentity.ForIndex(IndexA);
        var device = new UsbipEmulatedDevice(profile, IndexA, identity);
        server.Register(device);
        try
        {
            Check("the device carries the Switch 2 responder", device.Switch2 != null);
            if (device.Switch2 != null) RunWire(server, device, identity);
        }
        finally
        {
            server.Unregister(device);
            device.Dispose();
            SharedMemoryIO.DestroyController(IndexA);
        }

        OtherPersonasStillStall(ctx, server);
        RumbleDecode();
    }

    // SDL_hidapi_switch2.c EncodeHDRumble, transcribed.
    static byte[] SdlEncodeHdRumble(ushort highFreq, ushort highAmp, ushort lowFreq, ushort lowAmp) => new[]
    {
        (byte)(highFreq & 0xFF),
        (byte)(((highAmp >> 4) & 0xfc) | ((highFreq >> 8) & 0x03)),
        (byte)((highAmp >> 12) | (lowFreq << 4)),
        (byte)((lowAmp & 0xc0) | ((lowFreq >> 4) & 0x3f)),
        (byte)(lowAmp >> 8),
    };

    /// <summary>The 64-byte output report SDL's UpdateRumble writes for a Pro
    /// Controller 2: report 0x02, the left block at 1 and a copy at 0x11.</summary>
    static byte[] SdlRumbleReport(ushort lowFrequencyRumble, ushort highFrequencyRumble, int seq = 0)
    {
        const int RumbleMax = 29000;
        ushort lowAmp = (ushort)(lowFrequencyRumble * RumbleMax / ushort.MaxValue);
        ushort highAmp = (ushort)(highFrequencyRumble * RumbleMax / ushort.MaxValue);
        var r = new byte[64];
        r[0] = 0x02;
        r[1] = (byte)(0x50 | (seq & 0xf));
        SdlEncodeHdRumble(0x187, highAmp, 0x112, lowAmp).CopyTo(r, 2);
        Array.Copy(r, 0x01, r, 0x11, 6);
        return r;
    }

    static void RumbleDecode()
    {
        Console.WriteLine("\n-- A12 rumble decode --");
        var half = SdlRumbleReport(0x8000, 0x4000);
        Check("SDL_RumbleGamepad(0x8000, 0x4000) writes 87 c5 21 91 38",
              half.AsSpan(2, 5).SequenceEqual(H("87c5219138")), X(half.AsSpan(2, 5)));
        Switch2ProPacker.DecodeRumble(half.AsSpan(1), out byte l, out byte r);
        Check("it decodes to leftMotor 127, rightMotor 64", l == 127 && r == 64, $"{l}/{r}");

        Switch2ProPacker.DecodeRumble(SdlRumbleReport(0xFFFF, 0xFFFF).AsSpan(1), out l, out r);
        Check("full scale on both decodes to 255 and 255", l == 255 && r == 255, $"{l}/{r}");
        Switch2ProPacker.DecodeRumble(SdlRumbleReport(0, 0).AsSpan(1), out l, out r);
        Check("a stop decodes to 0 and 0", l == 0 && r == 0, $"{l}/{r}");
        Switch2ProPacker.DecodeRumble(SdlRumbleReport(0xFFFF, 0).AsSpan(1), out l, out r);
        Check("the low-frequency motor alone is the left motor", l == 255 && r == 0, $"{l}/{r}");
        Switch2ProPacker.DecodeRumble(SdlRumbleReport(0, 0xFFFF).AsSpan(1), out l, out r);
        Check("the high-frequency motor alone is the right motor", l == 0 && r == 255, $"{l}/{r}");

        // The largest amplitude over all six frames, not the first frame's.
        var later = new byte[64];
        later[0] = 0x02; later[1] = 0x50; later[0x11] = 0x50;
        SdlEncodeHdRumble(0x187, 29000, 0x112, 0).CopyTo(later, 0x11 + 1 + 10);   // right block, third frame
        Switch2ProPacker.DecodeRumble(later.AsSpan(1), out l, out r);
        Check("an amplitude in the right block's third frame is read", l == 0 && r == 255, $"{l}/{r}");

        // Frame 257220 of the capture, a console's own packet: 96 51 30 1f 06
        // holds first amplitude 20 and second amplitude 24.
        var console = new byte[64];
        console[0] = 0x02; console[1] = 0x53; console[0x11] = 0x53;
        H("9651301f06").CopyTo(console, 2); H("9651301f06").CopyTo(console, 0x12);
        Switch2ProPacker.DecodeRumble(console.AsSpan(1), out l, out r);
        Check("a console's frame 96 51 30 1f 06 decodes to left 14, right 11", l == 14 && r == 11, $"{l}/{r}");

        Switch2ProPacker.DecodeRumble(SdlRumbleReport(0xFFFF, 0xFFFF).AsSpan(1, 31), out l, out r);
        Check("a report shorter than 33 bytes decodes to nothing", l == 0 && r == 0, $"{l}/{r}");
    }

    static void OtherPersonasStillStall(HMContext ctx, UsbipServer server)
    {
        Console.WriteLine("\n-- A11 the Sony and Valve personas go on stalling --");
        foreach (var (id, index) in new[] { ("dualsense-composite", IndexSony), ("steam-deck-composite", IndexValve) })
        {
            var p = ctx.GetProfile(id)!.Inner;
            var dev = new UsbipEmulatedDevice(p, index);
            server.Register(dev);
            try
            {
                using var w = new Wire(server.Port, index);
                w.Import(dev.BusId);
                var ee = w.ControlIn(0x80, 0x06, 0x03EE, 0, 255);
                Check($"{id}: string 0xEE stalls", ee.Status == -32, $"status {ee.Status}");
                var c4 = w.ControlIn(0xC0, 0x01, 0, 4, 40);
                var c5 = w.ControlIn(0xC1, 0x01, 0, 5, 142);
                Check($"{id}: vendor requests for wIndex 4 and 5 stall", c4.Status == -32 && c5.Status == -32,
                      $"{c4.Status}/{c5.Status}");
                Check($"{id}: carries no Switch 2 responder", dev.Switch2 == null);
                var bulkIn = dev.Descriptors.Endpoints.Values.FirstOrDefault(e => e.TransferType == 2 && e.IsIn);
                if (bulkIn.Address != 0)
                {
                    var r = w.Wait(w.SubmitIn((uint)bulkIn.Number, 64), 2000);
                    Check($"{id}: its bulk IN endpoint 0x{bulkIn.Address:X2} still stalls",
                          r.HasValue && r.Value.Status == -32, r.HasValue ? $"status {r.Value.Status}" : "no answer");
                }
            }
            finally
            {
                server.Unregister(dev);
                dev.Dispose();
                SharedMemoryIO.DestroyController(index);
            }
        }
    }

    /// <summary>Interval statistics, in milliseconds, from the second
    /// report on. A read that arrives after a pause is answered at once, so
    /// the first interval of a run can be short by up to a whole step.</summary>
    static (double Mean, double Median, double P5, double P95, double Min, double Max) Cadence(List<Wire.Ret> r)
    {
        var gaps = new List<double>();
        for (int i = 2; i < r.Count; i++) gaps.Add((r[i].Ticks - r[i - 1].Ticks) * 1000.0 / Stopwatch.Frequency);
        gaps.Sort();
        double mean = (r[^1].Ticks - r[1].Ticks) * 1000.0 / Stopwatch.Frequency / (r.Count - 2);
        return (mean, gaps[gaps.Count / 2], gaps[gaps.Count / 20], gaps[gaps.Count * 19 / 20], gaps[0], gaps[^1]);
    }

    /// <summary>One report every 4 ms. The mean may not run short, which
    /// is the claim that no submit rate adds a report. It may run up to 5
    /// percent long: a tick the clock's thread was held past is skipped, not
    /// made up, and this process hosts device and client both, so a garbage
    /// collection here holds the clock too.</summary>
    static bool OnTheClock((double Mean, double Median, double P5, double P95, double Min, double Max) c)
        => c.Median > 3.85 && c.Median < 4.15 && c.Mean > 3.96 && c.Mean < 4.20;

    static string Describe((double Mean, double Median, double P5, double P95, double Min, double Max) c)
        => $"mean {c.Mean:F4} ms, median {c.Median:F3}, 5th to 95th percentile {c.P5:F3} to {c.P95:F3}, min {c.Min:F3}, max {c.Max:F3}";

    // Requests, as a console or SDL sends them.
    static byte[] Cmd(byte command, byte sub, params byte[] data)
    {
        var c = new byte[8 + data.Length];
        c[0] = command; c[1] = 0x91; c[2] = 0x00; c[3] = sub; c[5] = (byte)data.Length;
        data.CopyTo(c, 8);
        return c;
    }
    static byte[] FlashBlockCmd(uint address)
    {
        var d = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), address);
        return Cmd(0x02, 0x01, d);
    }
    static byte[] MemoryReadCmd(byte length, uint address)
    {
        var d = new byte[8];
        d[0] = length; d[1] = 0x7E;
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), address);
        return Cmd(0x02, 0x04, d);
    }
    static byte[] Reply(byte command, byte sub, string dataHex = "", byte status = 0x01)
        => new byte[] { command, status, 0x00, sub, 0x00, 0xF8, 0x00, 0x00 }.Concat(H(dataHex)).ToArray();

    static void RunWire(UsbipServer server, UsbipEmulatedDevice device, DeviceIdentity identity)
    {
        var s2 = device.Switch2!;
        var sent = new List<byte[]>();   // every bulk OUT payload, for the log check

        Console.WriteLine("\n-- A2 enumeration --");
        using (var list = new Wire(server.Port, IndexA))
        {
            var rows = list.Devlist();
            var mine = rows.FirstOrDefault(r => r.BusId == device.BusId);
            Check("devlist row: 057E:2069 with interface classes 03 and FF",
                  mine.BusId != null && mine.Vid == 0x057E && mine.Pid == 0x2069
                  && mine.InterfaceClasses.SequenceEqual(new byte[] { 0x03, 0xFF }),
                  mine.BusId != null ? X(mine.InterfaceClasses) : "missing");
        }

        using var w = new Wire(server.Port, IndexA);
        uint speed = w.Import(device.BusId);
        Check("import reply: full speed", speed == 2, $"speed {speed}");

        var dev = w.ControlIn(0x80, 0x06, 0x0100, 0, 18);
        Check("device descriptor is frame 44, byte for byte", dev.Status == 0 && dev.Data.SequenceEqual(DeviceDescriptor), X(dev.Data));
        var cfg9 = w.ControlIn(0x80, 0x06, 0x0200, 0, 9);
        Check("configuration header read of 9 bytes", cfg9.Status == 0 && cfg9.Data.SequenceEqual(ConfigurationDescriptor.Take(9)));
        var cfg = w.ControlIn(0x80, 0x06, 0x0200, 0, 0x50);
        Check("configuration descriptor is frames 71 and 76, 80 bytes, byte for byte",
              cfg.Status == 0 && cfg.Data.SequenceEqual(ConfigurationDescriptor), $"{cfg.Data.Length} bytes");
        Check("device qualifier and other-speed stall, as a full-speed pad's do",
              w.ControlIn(0x80, 0x06, 0x0600, 0, 10).Status == -32 && w.ControlIn(0x80, 0x06, 0x0700, 0, 255).Status == -32);

        string StringAt(byte index)
        {
            var r = w.ControlIn(0x80, 0x06, (ushort)(0x0300 | index), 0x0409, 255);
            return r.Status == 0 && r.Data.Length >= 2 ? Encoding.Unicode.GetString(r.Data, 2, r.Data.Length - 2) : $"<status {r.Status}>";
        }
        Check("string 1 is Nintendo", StringAt(1) == "Nintendo", StringAt(1));
        Check("string 2 is Switch 2 Pro Controller", StringAt(2) == "Switch 2 Pro Controller", StringAt(2));
        string serial = StringAt(3);
        Check("string 3 is the identity's serial, 14 characters like a real pad's",
              serial == identity.SyntheticSerial && serial.Length == 14, serial);
        Check("strings 4, 5 and 6 stall",
              new byte[] { 4, 5, 6 }.All(i => w.ControlIn(0x80, 0x06, (ushort)(0x0300 | i), 0x0409, 255).Status == -32));

        var ee = w.ControlIn(0x80, 0x06, 0x03EE, 0, 255);
        Check("string 0xEE returns 18 bytes ending 01 00",
              ee.Status == 0 && ee.Data.SequenceEqual(MsOsString) && ee.Data[16] == 0x01 && ee.Data[17] == 0x00, X(ee.Data));
        Check("string 0xEE is cut to wLength", w.ControlIn(0x80, 0x06, 0x03EE, 0, 2).Data.SequenceEqual(MsOsString.Take(2)));

        var properties = MsOsProperties();
        Check("the expected properties table is 142 bytes", properties.Length == 142, $"{properties.Length}");
        bool compatOk = true, propOk = true;
        var detail = new StringBuilder();
        foreach (byte code in new byte[] { 0x01, 0x20, 0xCD })
        {
            foreach (byte bm in new byte[] { 0xC0, 0xC1 })
            {
                foreach (ushort wValue in new ushort[] { 0x0000, 0x0100 })
                {
                    var c = w.ControlIn(bm, code, wValue, 4, 255);
                    var p = w.ControlIn(bm, code, wValue, 5, 255);
                    if (!(c.Status == 0 && c.Data.SequenceEqual(MsOsCompatId))) { compatOk = false; detail.Append($" compat[{code:X2},{bm:X2},{wValue:X4}]"); }
                    if (!(p.Status == 0 && p.Data.SequenceEqual(properties))) { propOk = false; detail.Append($" prop[{code:X2},{bm:X2},{wValue:X4}]"); }
                }
            }
        }
        Check("a vendor IN request with wIndex 4 returns the 40-byte compatible ID, for bRequest 0x01, 0x20 and 0xCD alike",
              compatOk, detail.ToString());
        Check("one with wIndex 5 returns the 142-byte properties, for bRequest 0x01, 0x20 and 0xCD alike",
              propOk, detail.ToString());
        Check("Windows' header reads come back cut to wLength: 16 bytes of the compatible ID, 10 of the properties",
              w.ControlIn(0xC0, 0x01, 0, 4, 16).Data.SequenceEqual(MsOsCompatId.Take(16))
              && w.ControlIn(0xC1, 0x01, 0, 5, 10).Data.SequenceEqual(properties.Take(10)));
        Check("a vendor IN request with any other wIndex stalls, the console's own three among them (frames 90 to 109)",
              new ushort[] { 0, 1, 3, 6, 7 }.All(i => w.ControlIn(0xC0, 0x03, 0, i, 64).Status == -32)
              && w.ControlIn(0xC0, 0x02, 0, 0, 16).Status == -32);
        Check("a vendor OUT request stalls", w.ControlOut(0x40, 0x04, 0x0276, 0, Array.Empty<byte>()).Status == -32);

        var rd = w.ControlIn(0x81, 0x06, 0x2200, 0, 255);
        Check("the HID report descriptor is served on interface 0, 97 bytes", rd.Status == 0 && rd.Data.Length == 97);
        Check("SET_CONFIGURATION and SET_IDLE ack",
              w.ControlOut(0x00, 0x09, 0x0001, 0, Array.Empty<byte>()).Status == 0
              && w.ControlOut(0x21, 0x0A, 0, 0, Array.Empty<byte>()).Status == 0);

        // ── Silence before start ────────────────────────────────────────
        Console.WriteLine("\n-- A3 silence before start --");
        Check("at attach: reports off, report 0x09 selected, no features",
              !s2.ReportsOn && s2.SelectedReport == 0x09 && s2.FeatureMask == 0 && s2.FeatureEnabled == 0);
        uint parked = w.SubmitIn(1, 64);
        var silenceClock = Stopwatch.StartNew();
        byte[] Command(byte[] c, int read = 64)
        {
            sent.Add(c);
            return w.Command(c, read);
        }
        var r0701 = Command(Cmd(0x07, 0x01));
        Check("07/01 on bulk gets 07 01 00 01 00 f8 00 00 00 (frame 237), the control that the device is live",
              r0701.SequenceEqual(Reply(0x07, 0x01, "00")), X(r0701));
        bool early = w.Wait(parked, 200 - (int)Math.Min(199, silenceClock.ElapsedMilliseconds)).HasValue;
        Check("no input report arrives in the 200 ms after attach", !early);
        var peek = w.ControlIn(0xA1, 0x01, 0x0100, 0, 64);
        Check("GET_REPORT(Input) reads the selected report at rest and counts nothing: 0x09, sticks at 0x800",
              peek.Status == 0 && peek.Data.Length == 64 && peek.Data[0] == 0x09 && peek.Data[1] == 0x00
              && peek.Data.AsSpan(6, 6).SequenceEqual(H("000880000880")), X(peek.Data.AsSpan(0, 13)));

        // ── The command table ───────────────────────────────────────────
        Console.WriteLine("\n-- A4 command table, before start --");
        void Expect(string label, byte[] request, byte[] expected, int read = 64)
        {
            var got = Command(request, read);
            Check(label, got.SequenceEqual(expected), got.SequenceEqual(expected) ? "" : $"got {X(got)}");
        }
        Expect("09/07 player LEDs: no data (frame 749)", Cmd(0x09, 0x07, 0x05, 0, 0, 0, 0, 0, 0, 0), Reply(0x09, 0x07));
        Check("the LED mask is kept", s2.PlayerLeds == 0x05, $"0x{s2.PlayerLeds:X2}");
        Expect("0A/08 vibration data: no data (frame 9271)",
               Cmd(0x0A, 0x08, H("01ffffffffffffffff3500460000000000000000")), Reply(0x0A, 0x08));
        Expect("10/01 firmware: 1.1.5, Pro Controller (frame 10045)", Cmd(0x10, 0x01), Reply(0x10, 0x01, "010105020c000000ffffffff"));
        Expect("11/01: 01 00 00 00 (commands.md)", Cmd(0x11, 0x01), Reply(0x11, 0x01, "01000000"));
        Expect("11/03 sensor scales: the 29 bytes of commands.md, which the capture's second pad also sends (frame 376492)",
               Cmd(0x11, 0x03), Reply(0x11, 0x03, "01200300000ae81c3b797d8b3a0ae89c4258a00b420ae89c4158a00b41"));
        Expect("01/0C: 61 12 50 10 (frame 10107)", Cmd(0x01, 0x0C), Reply(0x01, 0x0C, "61125010"));
        Expect("16/01: 24 zero bytes (frame 297)", Cmd(0x16, 0x01), Reply(0x16, 0x01, new string('0', 48)));
        Expect("18/01: status 0 and no data, as firmware 1.1.5 answers it (frame 10166)", Cmd(0x18, 0x01), Reply(0x18, 0x01, "", status: 0x00));
        Expect("03/0C: no data (frame 8764)", Cmd(0x03, 0x0C, 1, 0, 0, 0), Reply(0x03, 0x0C));
        Expect("0A/02: no data (frame 8770)", Cmd(0x0A, 0x02, 3, 0, 0, 0), Reply(0x0A, 0x02));
        Expect("01/01 and 08/02 from SDL's start: no data", Cmd(0x01, 0x01), Reply(0x01, 0x01));
        Expect("08/02", Cmd(0x08, 0x02, 1, 0, 0, 0), Reply(0x08, 0x02));
        Expect("a command no source names is answered too, with no data", Cmd(0x55, 0xAA, 1, 2, 3), Reply(0x55, 0xAA));
        Expect("0C/01 feature info for 0x2F: 07 07 01 00 00 03 00 00 (commands.md)",
               Cmd(0x0C, 0x01, 0x2F, 0, 0, 0), Reply(0x0C, 0x01, "00000000" + "0707010000030000"));
        Expect("0C/01 feature info for 0xFF", Cmd(0x0C, 0x01, 0xFF, 0, 0, 0), Reply(0x0C, 0x01, "00000000" + "0707010101030000"));
        Check("none of that started the stream", !s2.ReportsOn && !w.Arrived(parked));

        Console.WriteLine("\n-- A5 feature mask --");
        Expect("0C/04 enable before any mask is set", Cmd(0x0C, 0x04, 0x27, 0, 0, 0), Reply(0x0C, 0x04, "00000000"));
        Check("enables nothing: features outside the mask are ignored", s2.FeatureEnabled == 0, $"0x{s2.FeatureEnabled:X2}");
        Expect("0C/02 set mask 0x27 (frame 8757)", Cmd(0x0C, 0x02, 0x27, 0, 0, 0), Reply(0x0C, 0x02, "00000000"));
        Expect("0C/04 enable 0xFF (frame 9806 for the reply)", Cmd(0x0C, 0x04, 0xFF, 0, 0, 0), Reply(0x0C, 0x04, "00000000"));
        Check("enabled is data AND mask: 0x27", s2.FeatureMask == 0x27 && s2.FeatureEnabled == 0x27, $"0x{s2.FeatureEnabled:X2}");
        Expect("0C/05 disable 0x04", Cmd(0x0C, 0x05, 0x04, 0, 0, 0), Reply(0x0C, 0x05, "00000000"));
        Check("disable clears that bit alone: 0x23", s2.FeatureEnabled == 0x23, $"0x{s2.FeatureEnabled:X2}");
        Expect("0C/04 enable 0x04", Cmd(0x0C, 0x04, 0x04, 0, 0, 0), Reply(0x0C, 0x04, "00000000"));
        Check("enable adds to the set and leaves the rest: 0x27", s2.FeatureEnabled == 0x27, $"0x{s2.FeatureEnabled:X2}");
        Expect("0C/03 clear", Cmd(0x0C, 0x03, 0, 0, 0, 0), Reply(0x0C, 0x03, "00000000"));
        Check("clears the mask and every enabled feature", s2.FeatureMask == 0 && s2.FeatureEnabled == 0);

        // ── Flash ───────────────────────────────────────────────────────
        Console.WriteLine("\n-- A6 flash --");
        sent.Add(FlashBlockCmd(0x13000));
        w.BulkOut(FlashBlockCmd(0x13000));
        var first = w.BulkIn(64, 2000);
        var second = w.BulkIn(64, 2000);
        Check("02/01 at 0x13000 returns 80 bytes as 64 then 16 (frames 8837 and 8840 for the split)",
              first != null && second != null && first.Length == 64 && second.Length == 16,
              $"{first?.Length}/{second?.Length}");
        var block = (first ?? Array.Empty<byte>()).Concat(second ?? Array.Empty<byte>()).ToArray();
        if (block.Length == 80)
        {
            Check("reply header, then 40 00 00 00 and the address",
                  block.AsSpan(0, 16).SequenceEqual(Reply(0x02, 0x01, "40000000" + "00300100")), X(block.AsSpan(0, 16)));
            string flashSerial = Encoding.ASCII.GetString(block, 0x12, 16).TrimEnd('\0');
            Check("the serial at reply offset 0x12 equals USB string 3, zero padded to 16",
                  flashSerial == serial && block.AsSpan(0x12 + serial.Length, 16 - serial.Length).ToArray().All(b => b == 0),
                  flashSerial);
            Check("0x13000: 01 00, then after the serial 7e 05 69 20, 01 06 01 and the four colors (memory_layout.md, frame 93)",
                  block.AsSpan(0x10, 2).SequenceEqual(H("0100"))
                  && block.AsSpan(0x10 + 0x12, 19).SequenceEqual(H("7e056920" + "010601" + "232323a0a0a0e6e6e6323232")),
                  X(block.AsSpan(0x10 + 0x12, 19)));
            Check("the rest of the block reads 0xFF, as erased flash does", block.AsSpan(0x10 + 0x25).ToArray().All(b => b == 0xFF));
        }

        byte[] Flash(uint address)
        {
            var r = Command(FlashBlockCmd(address), 0x50);
            return r.Length == 80 ? r.AsSpan(16).ToArray() : Array.Empty<byte>();
        }
        var f40 = Flash(0x13040);
        Check("0x13040: floats 25.0, 0, 0, 0, a temperature then the gyro bias SDL subtracts",
              f40.Length == 64 && BitConverter.ToSingle(f40, 0) == 25.0f && f40.AsSpan(4, 12).ToArray().All(b => b == 0)
              && f40.AsSpan(16).ToArray().All(b => b == 0xFF));
        foreach (uint a in new uint[] { 0x13080, 0x130C0 })
        {
            var f = Flash(a);
            Check($"0x{a:X}: the 40-byte constant, then the stick calibration 00 08 80 ff f7 7f 00 08 80",
                  f.Length == 64 && f.AsSpan(0, 40).SequenceEqual(StickBlockConstant)
                  && f.AsSpan(40, 9).SequenceEqual(H("000880fff77f000880")) && f.AsSpan(49).ToArray().All(b => b == 0xFF),
                  f.Length == 64 ? X(f.AsSpan(40, 9)) : "short");
            if (f.Length == 64)
            {
                // SDL ParseStickCalibration.
                var d = f.AsSpan(0x28);
                int nx = d[0] | ((d[1] & 0x0F) << 8), ny = (d[1] >> 4) | (d[2] << 4);
                int xx = d[3] | ((d[4] & 0x0F) << 8), xy = (d[4] >> 4) | (d[5] << 4);
                int mx = d[6] | ((d[7] & 0x0F) << 8), my = (d[7] >> 4) | (d[8] << 4);
                Check("SDL reads it as center 0x800, 2047 above and 2048 below, on both axes",
                      nx == 0x800 && ny == 0x800 && xx == 2047 && xy == 2047 && mx == 2048 && my == 2048,
                      $"{nx}/{ny} {xx}/{xy} {mx}/{my}");
            }
        }
        var f100 = Flash(0x13100);
        Check("0x13100: 12 zero bytes, then floats 0, 0, 9.80665",
              f100.Length == 64 && f100.AsSpan(0, 20).ToArray().All(b => b == 0)
              && BitConverter.ToSingle(f100, 20) == 9.80665f && f100.AsSpan(24).ToArray().All(b => b == 0xFF));
        Check("0x1FC040 and 0x1FC080 read 0xFF: no user calibration (frame 8966)",
              Flash(0x1FC040).All(b => b == 0xFF) && Flash(0x1FC080).All(b => b == 0xFF) && Flash(0x1FC040).Length == 64);

        Expect("02/04 reads N bytes: 0x10 at 0x13040 comes back as N, 00 00 00, the address, the bytes (frame 9031)",
               MemoryReadCmd(0x10, 0x13040), Reply(0x02, 0x04, "10000000" + "40300100" + "0000c841" + new string('0', 24)));
        Expect("02/04 of 0x20 at 0x13060 is all 0xFF (frame 9211)",
               MemoryReadCmd(0x20, 0x13060), Reply(0x02, 0x04, "20000000" + "60300100" + new string('f', 64)));
        var big = Command(MemoryReadCmd(0x50, 0x13080), 0x60);
        Check("02/04 of 0x50, the most USB allows, returns 96 bytes",
              big.Length == 96 && big[8] == 0x50 && big.AsSpan(16, 40).SequenceEqual(StickBlockConstant), $"{big.Length}");
        var over = Command(MemoryReadCmd(0x7F, 0x13080), 0x100);
        Check("a longer read is cut to 0x50", over.Length == 96 && over[8] == 0x50, $"{over.Length}");
        Expect("addresses wrap at 0x200000 (commands.md)",
               MemoryReadCmd(0x02, 0x213000), Reply(0x02, 0x04, "02000000" + "00302100" + "0100"));

        // ── Bulk IN: parking, spanning, unlink ──────────────────────────
        Console.WriteLine("\n-- A7 bulk IN --");
        uint read1 = w.SubmitIn(2, 64);
        Check("a bulk IN read with no reply waiting parks", !w.Wait(read1, 60).HasValue);
        sent.Add(Cmd(0x07, 0x01));
        w.BulkOut(Cmd(0x07, 0x01));
        var late = w.Wait(read1, 2000);
        Check("the next command's reply completes it", late.HasValue && late.Value.Data.SequenceEqual(Reply(0x07, 0x01, "00")));

        uint victim = w.SubmitIn(2, 64);
        int unlink = w.Unlink(victim);
        Check("unlinking a parked bulk IN read answers -ECONNRESET and no RET_SUBMIT", unlink == -104 && !w.Arrived(victim), $"{unlink}");
        sent.Add(Cmd(0x07, 0x01));
        w.BulkOut(Cmd(0x07, 0x01));
        var afterUnlink = w.BulkIn(64, 2000);
        Check("the unlinked read took nothing: the next read gets the reply whole",
              afterUnlink != null && afterUnlink.SequenceEqual(Reply(0x07, 0x01, "00")));

        sent.Add(FlashBlockCmd(0x13080));
        w.BulkOut(FlashBlockCmd(0x13080));
        var whole = w.BulkIn(512, 2000);
        Check("a read of 512 bytes takes the whole 80-byte reply", whole != null && whole.Length == 80, $"{whole?.Length}");
        sent.Add(Cmd(0x10, 0x01));
        w.BulkOut(Cmd(0x10, 0x01));
        var bits = new List<byte>();
        int reads = 0;
        while (bits.Count < 20 && reads < 10) { var p = w.BulkIn(8, 2000); if (p == null) break; bits.AddRange(p); reads++; }
        Check("reads of 8 bytes take a 20-byte reply as 8, 8 and 4",
              reads == 3 && bits.ToArray().SequenceEqual(Reply(0x10, 0x01, "010105020c000000ffffffff")), $"{reads} reads");
        sent.Add(Cmd(0x07, 0x01)); sent.Add(Cmd(0x01, 0x0C));
        w.BulkOut(Cmd(0x07, 0x01));
        w.BulkOut(Cmd(0x01, 0x0C));
        var one = w.BulkIn(512, 2000);
        var two = w.BulkIn(512, 2000);
        Check("two waiting replies never share a read",
              one != null && two != null && one.SequenceEqual(Reply(0x07, 0x01, "00")) && two.SequenceEqual(Reply(0x01, 0x0C, "61125010")),
              $"{one?.Length}/{two?.Length}");
        var shortCmd = new byte[] { 0x07, 0x91, 0x00 };
        sent.Add(shortCmd);
        w.BulkOut(shortCmd);
        Check("a payload shorter than a header gets no reply", w.BulkIn(64, 80) == null);

        // ── Start ───────────────────────────────────────────────────────
        Console.WriteLine("\n-- A8 start --");
        // SubmitState's work, done here on the section: one body.
        IntPtr view = SharedMemoryIO.EnsureInputMapping(IndexA);
        IntPtr evt = SharedMemoryIO.GetInputEvent(IndexA);
        uint seqNo = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(view, 0);
        var bodyBuf = new byte[Switch2ProPacker.BodySize];
        void Submit(HMGamepadState st, double lx = 0.5, double ly = 0.5, double rx = 0.5, double ry = 0.5, double lt = 0, double rt = 0)
        {
            Switch2ProPacker.BuildBody(in st, lx, ly, rx, ry, lt, rt, bodyBuf);
            SharedMemoryIO.WriteInputFrame(view, evt, ref seqNo, Array.Empty<byte>(), 0, null,
                dataOffset: 0, extendedData: bodyBuf, extendedLen: Switch2ProPacker.BodySize);
        }

        Check("the read parked at attach is still parked", !w.Arrived(parked));
        var startRequest = Cmd(0x03, 0x0D, H("0100ffffffffffff"));   // frame 167, with the host address SDL sends
        sent.Add(startRequest);
        long tStart = Stopwatch.GetTimestamp();
        w.BulkOut(startRequest);
        var startReply = w.BulkIn(64, 2000);
        Check("03/0D answers 03 01 00 0d 00 f8 00 00 01 00 00 00 (frame 180)",
              startReply != null && startReply.SequenceEqual(Reply(0x03, 0x0D, "01000000")), X(startReply ?? Array.Empty<byte>()));
        var firstReport = w.Wait(parked, 2000);
        double firstMs = firstReport.HasValue ? (firstReport.Value.Ticks - tStart) * 1000.0 / Stopwatch.Frequency : -1;
        Check("the first report follows the start by one interval (3.7 ms on the pad)",
              firstReport.HasValue && firstMs > 2.5 && firstMs < 12, $"{firstMs:F2} ms");
        Check("it is report 0x09 with counter 0, power 0x25, sticks at 0x800 and byte 12 at 0x30",
              firstReport.HasValue && firstReport.Value.Data.Length == 64
              && firstReport.Value.Data.AsSpan(0, 13).SequenceEqual(H("09" + "00" + "25" + "000000" + "000880" + "000880" + "30"))
              && firstReport.Value.Data.AsSpan(13).ToArray().All(b => b == 0),
              firstReport.HasValue ? X(firstReport.Value.Data.AsSpan(0, 16)) : "none");

        var run = w.ReadReports(250, 4000);
        Check("250 more reports arrive", run.Count == 250, $"{run.Count}");
        if (run.Count == 250)
        {
            bool allPro = run.All(r => r.Data[0] == 0x09);
            bool counted = true;
            for (int i = 0; i < run.Count; i++) if (run[i].Data[1] != (byte)(1 + i)) counted = false;
            Check("every one is 0x09 and the counter rises by 1", allPro && counted);
            var c = Cadence(run);
            Check("one report every 4 ms", OnTheClock(c), Describe(c));
        }

        // The clock owns the cadence. Submits at 1 kHz add no report.
        Console.WriteLine("\n-- A9 the clock, not the consumer, sends reports --");
        bool stopFeed = false;
        var feeder = new Thread(() =>
        {
            var st = new HMGamepadState();
            var sw = Stopwatch.StartNew();
            long next = 0;
            while (!Volatile.Read(ref stopFeed))
            {
                Submit(st, lx: (sw.ElapsedMilliseconds % 100) / 100.0);
                next += Stopwatch.Frequency / 1000;
                while (sw.ElapsedTicks < next) Thread.SpinWait(50);
            }
        }) { IsBackground = true };
        feeder.Start();
        var fed = w.ReadReports(250, 4000);
        Volatile.Write(ref stopFeed, true);
        feeder.Join(500);
        if (fed.Count == 250)
        {
            var c = Cadence(fed);
            Check("with a consumer submitting every 1 ms the device still sends one report every 4 ms",
                  OnTheClock(c), Describe(c));
            Check("and those reports carry the consumer's newest state",
                  fed.Select(r => r.Data[6] | ((r.Data[7] & 0x0F) << 8)).Distinct().Count() > 20);
        }
        else Check("250 reports under a 1 kHz consumer", false, $"{fed.Count}");
        Submit(new HMGamepadState());
        Thread.Sleep(30);
        var quiet = w.ReadReports(100, 2000);
        if (quiet.Count == 100)
        {
            var c = Cadence(quiet);
            Check("with the consumer silent it sends the same step", OnTheClock(c), Describe(c));
        }
        else Check("100 reports from a silent consumer", false, $"{quiet.Count}");

        Thread.Sleep(30);   // no read parked across several ticks
        long tLate = Stopwatch.GetTimestamp();
        var owed = w.Wait(w.SubmitIn(1, 64), 500);
        double owedMs = owed.HasValue ? (owed.Value.Ticks - tLate) * 1000.0 / Stopwatch.Frequency : -1;
        var afterOwed = w.ReadReports(3, 500);
        // Seven ticks passed with no read. One report was owed, not seven:
        // the three reads after it wait on the clock, two full intervals
        // and part of a third.
        double burstMs = afterOwed.Count == 3 && owed.HasValue
            ? (afterOwed[2].Ticks - owed.Value.Ticks) * 1000.0 / Stopwatch.Frequency : -1;
        Check("a read that arrives late is answered at once, with one report and no burst",
              owed.HasValue && owedMs < 2.0 && burstMs > 7.0,
              $"answered in {owedMs:F2} ms, the next three took {burstMs:F2} ms");

        // ── Report 0x09 content ─────────────────────────────────────────
        Console.WriteLine("\n-- A10 report content --");
        byte[] Next(HMGamepadState st, double lx = 0.5, double ly = 0.5, double rx = 0.5, double ry = 0.5, double lt = 0, double rt = 0)
        {
            Submit(st, lx, ly, rx, ry, lt, rt);
            Thread.Sleep(12);                       // the pump takes the body, then two ticks pass
            var rs = w.ReadReports(3, 1000);
            return rs.Count == 3 ? rs[2].Data : new byte[64];
        }
        HMGamepadState Btn(HMButton b) => new() { Buttons = b };
        HMGamepadState Hat(HMHat h) => new() { Hat = h };

        // (state, 0x09 byte and mask, 0x05 byte and mask), both from hid_reports.md.
        var buttons = new (string Name, Func<byte[]> Make, int ProByte, byte ProMask, int ComByte, byte ComMask)[]
        {
            ("B", () => Next(Btn(HMButton.B)), 3, 0x01, 5, 0x04),
            ("A", () => Next(Btn(HMButton.A)), 3, 0x02, 5, 0x08),
            ("Y", () => Next(Btn(HMButton.Y)), 3, 0x04, 5, 0x01),
            ("X", () => Next(Btn(HMButton.X)), 3, 0x08, 5, 0x02),
            ("R", () => Next(Btn(HMButton.RightBumper)), 3, 0x10, 5, 0x40),
            ("ZR", () => Next(new HMGamepadState(), rt: 1.0), 3, 0x20, 5, 0x80),
            ("Plus", () => Next(Btn(HMButton.Start)), 3, 0x40, 6, 0x02),
            ("Right Stick", () => Next(Btn(HMButton.RightStick)), 3, 0x80, 6, 0x04),
            ("Down", () => Next(Hat(HMHat.South)), 4, 0x01, 7, 0x01),
            ("Right", () => Next(Hat(HMHat.East)), 4, 0x02, 7, 0x04),
            ("Left", () => Next(Hat(HMHat.West)), 4, 0x04, 7, 0x08),
            ("Up", () => Next(Hat(HMHat.North)), 4, 0x08, 7, 0x02),
            ("L", () => Next(Btn(HMButton.LeftBumper)), 4, 0x10, 7, 0x40),
            ("ZL", () => Next(new HMGamepadState(), lt: 1.0), 4, 0x20, 7, 0x80),
            ("Minus", () => Next(Btn(HMButton.Back)), 4, 0x40, 6, 0x01),
            ("Left Stick", () => Next(Btn(HMButton.LeftStick)), 4, 0x80, 6, 0x08),
            ("Home", () => Next(Btn(HMButton.Guide)), 5, 0x01, 6, 0x10),
            ("Capture", () => Next(Btn(HMButton.Share)), 5, 0x02, 6, 0x20),
            ("GR", () => Next(Btn(HMButton.RightPaddle)), 5, 0x04, 8, 0x01),
            ("GL", () => Next(Btn(HMButton.LeftPaddle)), 5, 0x08, 8, 0x02),
            ("C", () => Next(Btn(HMButton.Misc1)), 5, 0x10, 6, 0x40),
        };
        bool proOk = true;
        var proDetail = new StringBuilder();
        foreach (var b in buttons)
        {
            var r = b.Make();
            var want = new byte[3];
            want[b.ProByte - 3] = b.ProMask;
            if (r[0] != 0x09 || !r.AsSpan(3, 3).SequenceEqual(want)) { proOk = false; proDetail.Append($" {b.Name}={X(r.AsSpan(3, 3))}"); }
        }
        Check("report 0x09: each of the 21 controls sets its one bit of bytes 3 to 5 (hid_reports.md)", proOk, proDetail.ToString());

        var corner = Next(new HMGamepadState(), lx: 0.0, ly: 0.0, rx: 1.0, ry: 1.0);
        Check("report 0x09 sticks: left at top left is X 0, Y 4095 and right at bottom right is X 4095, Y 0",
              corner.AsSpan(6, 6).SequenceEqual(H("00f0ff" + "ff0f00")), X(corner.AsSpan(6, 6)));
        Check("a trigger below one count of 255 presses nothing, and one count presses it",
              (Next(new HMGamepadState(), lt: 0.0019, rt: 0.0019)[3] & 0x20) == 0
              && (Next(new HMGamepadState(), rt: 0.002)[3] & 0x20) != 0);
        var diag = Next(Hat(HMHat.NorthEast));
        Check("a diagonal sets both of its directions", (diag[4] & 0x0F) == 0x0A, $"0x{diag[4]:X2}");

        // ── Report 0x05 ─────────────────────────────────────────────────
        Console.WriteLine("\n-- A11 report 0x05 and the motion gate --");
        Submit(new HMGamepadState());
        Thread.Sleep(12);
        Expect("03/0A with 0x07, a Joy-Con's report, is ignored", Cmd(0x03, 0x0A, 0x07, 0, 0, 0), Reply(0x03, 0x0A));
        Check("the selection stays 0x09", s2.SelectedReport == 0x09 && w.ReadReports(2, 500).All(r => r.Data[0] == 0x09));
        Expect("03/0A with 0x05 (frame 9919 for the reply)", Cmd(0x03, 0x0A, 0x05, 0, 0, 0), Reply(0x03, 0x0A));
        var common = w.ReadReports(60, 2000);
        Check("after it, every report is 0x05", common.Count == 60 && common.All(r => r.Data[0] == 0x05));
        if (common.Count == 60)
        {
            bool ms = true, still = true;
            for (int i = 1; i < common.Count; i++)
                if (BinaryPrimitives.ReadUInt32LittleEndian(common[i].Data.AsSpan(1)) - BinaryPrimitives.ReadUInt32LittleEndian(common[i - 1].Data.AsSpan(1)) != 4) ms = false;
            foreach (var r in common) if (!r.Data.AsSpan(0x2B, 0x12).ToArray().All(b => b == 0)) still = false;
            Check("bytes 1 to 4 count milliseconds, 4 more each report", ms);
            Check("with feature 0x04 off, bytes 0x2B to 0x3C are 0", still);
            var d = common[^1].Data;
            Check("battery 3800 mV at 0x20, 0x20 at 0x22 and 0x01 at 0x2A, sticks at 0x800, every other byte 0",
                  d.AsSpan(0x20, 3).SequenceEqual(H("d80e20")) && d[0x2A] == 0x01
                  && d.AsSpan(0x0B, 6).SequenceEqual(H("000880000880"))
                  && d.AsSpan(5, 6).ToArray().All(b => b == 0) && d.AsSpan(0x11, 0x0F).ToArray().All(b => b == 0)
                  && d.AsSpan(0x23, 7).ToArray().All(b => b == 0) && d.AsSpan(0x3D).ToArray().All(b => b == 0),
                  X(d));
        }

        bool comOk = true;
        var comDetail = new StringBuilder();
        foreach (var b in buttons)
        {
            var r = b.Make();
            var want = new byte[4];
            want[b.ComByte - 5] = b.ComMask;
            if (r[0] != 0x05 || !r.AsSpan(5, 4).SequenceEqual(want)) { comOk = false; comDetail.Append($" {b.Name}={X(r.AsSpan(5, 4))}"); }
        }
        Check("report 0x05: each of the 21 controls sets its one bit of bytes 5 to 8 (hid_reports.md, SDL HandleSwitchProState)",
              comOk, comDetail.ToString());
        corner = Next(new HMGamepadState(), lx: 0.0, ly: 0.0, rx: 1.0, ry: 1.0);
        Check("report 0x05 sticks at 0x0B and 0x0E carry the same raw pairs", corner.AsSpan(0x0B, 6).SequenceEqual(H("00f0ff" + "ff0f00")), X(corner.AsSpan(0x0B, 6)));

        Command(Cmd(0x0C, 0x02, 0x27, 0, 0, 0));
        Command(Cmd(0x0C, 0x04, 0x27, 0, 0, 0));
        var moving = w.ReadReports(120, 3000);
        Check("with 0x27 enabled, 120 reports", moving.Count == 120);
        if (moving.Count == 120)
        {
            bool step = true, nonzero = true;
            for (int i = 1; i < moving.Count; i++)
                if (BinaryPrimitives.ReadUInt32LittleEndian(moving[i].Data.AsSpan(0x2B)) - BinaryPrimitives.ReadUInt32LittleEndian(moving[i - 1].Data.AsSpan(0x2B)) != 4000) step = false;
            foreach (var r in moving) if (BinaryPrimitives.ReadUInt32LittleEndian(r.Data.AsSpan(0x2B)) == 0) nonzero = false;
            Check("the timestamp at 0x2B rises by 4000 a report and is never 0", step && nonzero);
            uint ts = BinaryPrimitives.ReadUInt32LittleEndian(moving[0].Data.AsSpan(0x2B));
            uint msField = BinaryPrimitives.ReadUInt32LittleEndian(moving[0].Data.AsSpan(1));
            Check("it is 1 plus 1000 times the millisecond count, both from one count of reports", ts == 1 + msField * 1000, $"{ts} vs {msField} ms");
            // SDL's window: 100 reports must advance the timestamp 360000 to 439999.
            long advance = (long)BinaryPrimitives.ReadUInt32LittleEndian(moving[104].Data.AsSpan(0x2B)) - BinaryPrimitives.ReadUInt32LittleEndian(moving[4].Data.AsSpan(0x2B));
            long coeff = 1000 * advance / (100 * 4);
            Check("SDL's 100-report measure lands on its first set of constants", (coeff + 100000) / 200000 == 5, $"coeff {coeff}");
        }

        // The motion conversion, read back with SDL's own lines
        // (HandleStatePacket: accel 0x31, 0x35, -0x33 at 8 g full scale and
        // gyro 0x37, 0x3B, -0x39 at 34.8 rad/s full scale).
        static (float x, float y, float z, float gx, float gy, float gz) SdlMotion(byte[] d)
        {
            const float g = 9.80665f;
            const float accelScale = g * 8f / short.MaxValue;
            const float gyroCoeff = 34.8f;
            short S(int o) => (short)(d[o] | (d[o + 1] << 8));
            return (S(0x31) * accelScale, S(0x35) * accelScale, S(0x33) * -accelScale,
                    S(0x37) * gyroCoeff / short.MaxValue, S(0x3B) * gyroCoeff / short.MaxValue, S(0x39) * -gyroCoeff / short.MaxValue);
        }
        bool Near(float got, float want) => Math.Abs(got - want) <= Math.Max(0.01f * Math.Abs(want), 0.005f);

        var rest = Next(new HMGamepadState { AccelGY = 1.0f });
        var m = SdlMotion(rest);
        Check("AccelGY 1.0 goes out as 4096 at 0x35 and SDL reads (0, 9.807, 0) m/s/s",
              (short)(rest[0x35] | (rest[0x36] << 8)) == 4096 && Near(m.x, 0) && Near(m.y, 9.80665f) && Near(m.z, 0),
              $"({m.x:F3}, {m.y:F3}, {m.z:F3})");
        m = SdlMotion(Next(new HMGamepadState { AccelGX = 1.0f }));
        Check("AccelGX 1.0 reads back on X", Near(m.x, 9.80665f) && Near(m.y, 0) && Near(m.z, 0), $"({m.x:F3}, {m.y:F3}, {m.z:F3})");
        m = SdlMotion(Next(new HMGamepadState { AccelGZ = 1.0f }));
        Check("AccelGZ 1.0 reads back on Z", Near(m.x, 0) && Near(m.y, 0) && Near(m.z, 9.80665f), $"({m.x:F3}, {m.y:F3}, {m.z:F3})");
        var turn = Next(new HMGamepadState { GyroDpsX = 100f });
        m = SdlMotion(turn);
        Check("100 deg/s on X goes out as 1643 at 0x37 and SDL reads 1.745 rad/s on X",
              (short)(turn[0x37] | (turn[0x38] << 8)) == 1643 && Near(m.gx, 1.74533f) && Near(m.gy, 0) && Near(m.gz, 0),
              $"({m.gx:F4}, {m.gy:F4}, {m.gz:F4})");
        m = SdlMotion(Next(new HMGamepadState { GyroDpsY = -100f }));
        Check("minus 100 deg/s on Y reads back on Y with its sign", Near(m.gx, 0) && Near(m.gy, -1.74533f) && Near(m.gz, 0), $"({m.gx:F4}, {m.gy:F4}, {m.gz:F4})");
        m = SdlMotion(Next(new HMGamepadState { GyroDpsZ = 100f }));
        Check("100 deg/s on Z reads back on Z", Near(m.gx, 0) && Near(m.gy, 0) && Near(m.gz, 1.74533f), $"({m.gx:F4}, {m.gy:F4}, {m.gz:F4})");
        var clamp = Next(new HMGamepadState { AccelGX = 20f, AccelGY = -20f, GyroDpsX = 5000f });
        Check("values past full scale clamp to a signed 16-bit count",
              (short)(clamp[0x31] | (clamp[0x32] << 8)) == short.MaxValue && (short)(clamp[0x35] | (clamp[0x36] << 8)) == short.MinValue
              && (short)(clamp[0x37] | (clamp[0x38] << 8)) == short.MaxValue);
        Check("temperature at 0x2F stays 0", clamp[0x2F] == 0 && clamp[0x30] == 0);

        Submit(new HMGamepadState { AccelGY = 1.0f, GyroDpsX = 50f });
        Thread.Sleep(12);
        Command(Cmd(0x0C, 0x05, 0x04, 0, 0, 0));
        Check("0C/05 disabling 0x04 zeroes bytes 0x2B to 0x3C",
              w.ReadReports(20, 1000) is { Count: 20 } imuOff && imuOff.All(r => r.Data.AsSpan(0x2B, 0x12).ToArray().All(b => b == 0)));
        Command(Cmd(0x0C, 0x04, 0x04, 0, 0, 0));
        Check("0C/04 enabling it again brings the motion back",
              w.ReadReports(5, 1000) is { Count: 5 } imuOn && (short)(imuOn[4].Data[0x35] | (imuOn[4].Data[0x36] << 8)) == 4096);
        Command(Cmd(0x0C, 0x03, 0, 0, 0, 0));
        Check("with the mask cleared by 0C/03, bytes 0x2B to 0x3C are 0",
              w.ReadReports(20, 1000) is { Count: 20 } cleared && cleared.All(r => r.Data.AsSpan(0x2B, 0x12).ToArray().All(b => b == 0)));

        Command(Cmd(0x03, 0x0A, 0x09, 0, 0, 0));
        Check("back on 0x09 with no feature, byte 12 is 0x30 (frame 9811)",
              w.ReadReports(3, 500) is { Count: 3 } p30 && p30.All(r => r.Data[0] == 0x09 && r.Data[12] == 0x30));
        Command(Cmd(0x0C, 0x02, 0x27, 0, 0, 0));
        Command(Cmd(0x0C, 0x04, 0x27, 0, 0, 0));
        Check("with feature 0x20 enabled, byte 12 is 0x38 (frame 9818) and byte 15 stays 0",
              w.ReadReports(3, 500) is { Count: 3 } p38 && p38.All(r => r.Data[0] == 0x09 && r.Data[12] == 0x38 && r.Data[15] == 0));

        // ── Stop and start again ────────────────────────────────────────
        Console.WriteLine("\n-- A13 stop, start, reset --");
        var last = w.ReadReports(1, 500);
        Expect("03/03 with 0: reports off", Cmd(0x03, 0x03, 0, 0, 0, 0), Reply(0x03, 0x03, "01000000"));
        uint idle = w.SubmitIn(1, 64);
        Check("then every read parks again", !w.Wait(idle, 120).HasValue && !s2.ReportsOn);
        Expect("03/03 with 1: reports on", Cmd(0x03, 0x03, 1, 0, 0, 0), Reply(0x03, 0x03, "01000000"));
        var resumed = w.Wait(idle, 500);
        Check("the parked read completes and the counter carries on",
              resumed.HasValue && last.Count == 1 && resumed.Value.Data[1] == (byte)(last[0].Data[1] + 1),
              resumed.HasValue ? $"{last[0].Data[1]} then {resumed.Value.Data[1]}" : "none");

        // A stop and a start closer together than one report interval, with
        // a report owed from before the stop. The start owes nothing from
        // the run before it: its first report is the clock's, one interval
        // after the command.
        Thread.Sleep(30);   // no read parked across several ticks: one report is owed
        var off = Cmd(0x03, 0x03, 0, 0, 0, 0);
        var on = Cmd(0x03, 0x03, 1, 0, 0, 0);
        sent.Add(off);
        sent.Add(on);
        w.BulkOut(off);
        long tOn = Stopwatch.GetTimestamp();
        w.BulkOut(on);
        var quick = w.Wait(w.SubmitIn(1, 64), 500);
        double quickMs = quick.HasValue ? (quick.Value.Ticks - tOn) * 1000.0 / Stopwatch.Frequency : -1;
        Check("a start right behind a stop sends its first report one interval later, not the one owed from before",
              quick.HasValue && quickMs > 2.5 && quickMs < 12, $"{quickMs:F2} ms");
        var offReply = w.BulkIn(64, 500);
        var onReply = w.BulkIn(64, 500);
        Check("and both commands are answered in order",
              offReply != null && offReply.SequenceEqual(Reply(0x03, 0x03, "01000000"))
              && onReply != null && onReply.SequenceEqual(Reply(0x03, 0x03, "01000000")));

        // The output report reaches the ring whole.
        IntPtr outView = SharedMemoryIO.EnsureOutputMapping(IndexA);
        uint ringSeq = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(outView, 0);
        var ringBuf = new byte[256];
        var rumble = SdlRumbleReport(0x8000, 0x4000, 7);
        var ack = w.Wait(w.SubmitOut(1, rumble), 2000);
        bool ring = false;
        for (int i = 0; i < 100 && !ring; i++)
        {
            ring = SharedMemoryIO.TryReadOutputFrame(outView, ref ringSeq, out byte src, out byte rid, out int size, ringBuf)
                   && src == 0 && rid == 0x02 && size == 63 && ringBuf.AsSpan(0, 63).SequenceEqual(rumble.AsSpan(1));
            if (!ring) Thread.Sleep(2);
        }
        Check("output report 0x02 on interrupt OUT is acked and reaches the output ring as it stands",
              ack.HasValue && ack.Value.Status == 0 && ack.Value.ActualLength == 64 && ring);

        // A command left unread, then a port reset.
        sent.Add(Cmd(0x10, 0x01));
        w.BulkOut(Cmd(0x10, 0x01));
        var reset = w.ControlOut(0x23, 0x03, 4, 1, Array.Empty<byte>());
        Check("port reset acks", reset.Status == 0);
        Check("after it: reports off, report 0x09 selected, mask and features zero",
              !s2.ReportsOn && s2.SelectedReport == 0x09 && s2.FeatureMask == 0 && s2.FeatureEnabled == 0 && s2.PlayerLeds == 0);
        Check("and the reply queue is empty: a bulk IN read parks", w.BulkIn(64, 80) == null);
        uint afterReset = w.SubmitIn(1, 64);
        Check("no report follows a reset until a host starts the pad again", !w.Wait(afterReset, 120).HasValue);
        Command(Cmd(0x03, 0x0D, H("0100ffffffffffff")));
        var again = w.Wait(afterReset, 500);
        Check("03/0D starts it again from counter 0", again.HasValue && again.Value.Data[0] == 0x09 && again.Value.Data[1] == 0);

        // ── The bulk log ────────────────────────────────────────────────
        Console.WriteLine("\n-- A14 the bulk log --");
        var log = s2.SnapshotBulkLog();
        bool same = log.Count == sent.Count;
        for (int i = 0; same && i < log.Count; i++) same = log[i].SequenceEqual(sent[i]);
        Check("every bulk OUT payload is on record raw, in order: the unknown commands and the 3-byte one among them",
              same && log.Any(p => p.Length == 3) && log.Any(p => p.Length >= 4 && p[0] == 0x55 && p[3] == 0xAA),
              $"{log.Count} logged, {sent.Count} sent");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  The wire client: usbip-win2's side of the connection
    // ══════════════════════════════════════════════════════════════════════

    sealed class Wire : IDisposable
    {
        readonly TcpClient _tcp;
        readonly NetworkStream _s;
        readonly int _index;
        Thread? _reader;
        uint _seq = 100;
        readonly object _lock = new();
        readonly Dictionary<uint, Ret> _rets = new();
        readonly Dictionary<uint, int> _unlinks = new();
        readonly HashSet<uint> _in = new();
        volatile bool _closed;

        public struct Ret
        {
            public int Status, ActualLength;
            public byte[] Data;
            public long Ticks;   // Stopwatch time the reply was read
        }

        public Wire(int port, int index)
        {
            _index = index;
            _tcp = new TcpClient("127.0.0.1", port) { NoDelay = true };
            _s = _tcp.GetStream();
        }

        public struct DevRow { public string BusId; public ushort Vid, Pid; public byte[] InterfaceClasses; }

        void SendOp(ushort code)
        {
            var b = new byte[8];
            BinaryPrimitives.WriteUInt16BigEndian(b, 0x0111);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), code);
            _s.Write(b);
        }

        public List<DevRow> Devlist()
        {
            SendOp(0x8005);
            Exactly(8);
            int n = (int)BinaryPrimitives.ReadUInt32BigEndian(Exactly(4));
            var rows = new List<DevRow>();
            for (int i = 0; i < n; i++)
            {
                var d = Exactly(312);
                int ifaces = d[311];
                var classes = new byte[ifaces];
                for (int k = 0; k < ifaces; k++) classes[k] = Exactly(4)[0];
                int end = Array.IndexOf(d, (byte)0, 256, 32);
                rows.Add(new DevRow
                {
                    BusId = Encoding.UTF8.GetString(d, 256, (end < 0 ? 288 : end) - 256),
                    Vid = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(300)),
                    Pid = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(302)),
                    InterfaceClasses = classes,
                });
            }
            return rows;
        }

        /// <summary>Import the device and start reading replies. Returns
        /// the speed the import reply states.</summary>
        public uint Import(string busid)
        {
            SendOp(0x8003);
            var b = new byte[32];
            Encoding.UTF8.GetBytes(busid).CopyTo(b, 0);
            _s.Write(b);
            var op = Exactly(8);
            if (BinaryPrimitives.ReadUInt32BigEndian(op.AsSpan(4)) != 0) throw new IOException("import refused");
            var dev = Exactly(312);
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "WireReader" };
            _reader.Start();
            return BinaryPrimitives.ReadUInt32BigEndian(dev.AsSpan(296));
        }

        void ReadLoop()
        {
            try
            {
                while (!_closed)
                {
                    var h = Exactly(48);
                    long ticks = Stopwatch.GetTimestamp();
                    uint command = BinaryPrimitives.ReadUInt32BigEndian(h);
                    uint seq = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(4));
                    int status = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(20));
                    if (command == 4)
                    {
                        lock (_lock) { _unlinks[seq] = status; Monitor.PulseAll(_lock); }
                        continue;
                    }
                    int actual = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(24));
                    bool wasIn;
                    lock (_lock) wasIn = _in.Remove(seq);
                    var data = wasIn && actual > 0 ? Exactly(actual) : Array.Empty<byte>();
                    lock (_lock)
                    {
                        _rets[seq] = new Ret { Status = status, ActualLength = actual, Data = data, Ticks = ticks };
                        Monitor.PulseAll(_lock);
                    }
                }
            }
            catch { /* closed */ }
        }

        byte[] Exactly(int n)
        {
            var b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = _s.Read(b, got, n - got);
                if (r <= 0) throw new IOException("EOF");
                got += r;
            }
            return b;
        }

        void Header(uint command, uint seq, uint direction, uint ep, int length, ulong setup, uint unlink = 0)
        {
            var h = new byte[48];
            BinaryPrimitives.WriteUInt32BigEndian(h, command);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), seq);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8), (1u << 16) | (uint)(_index + 1));
            if (command == 1)
            {
                BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(12), direction);
                BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(16), ep);
                BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(24), length);
                BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(32), -1);
                BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(40), setup);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(20), unlink);
            }
            lock (_s) _s.Write(h);
        }

        static ulong Setup(byte bm, byte request, ushort wValue, ushort wIndex, ushort wLength)
        {
            Span<byte> s = stackalloc byte[8];
            s[0] = bm; s[1] = request;
            BinaryPrimitives.WriteUInt16LittleEndian(s[2..], wValue);
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], wIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(s[6..], wLength);
            return BinaryPrimitives.ReadUInt64LittleEndian(s);
        }

        public Ret ControlIn(byte bm, byte request, ushort wValue, ushort wIndex, ushort wLength)
        {
            uint seq = _seq++;
            lock (_lock) _in.Add(seq);
            Header(1, seq, 1, 0, wLength, Setup(bm, request, wValue, wIndex, wLength));
            return Wait(seq, 3000) ?? throw new IOException("control IN timed out");
        }

        public Ret ControlOut(byte bm, byte request, ushort wValue, ushort wIndex, byte[] data)
        {
            uint seq = _seq++;
            lock (_s)
            {
                Header(1, seq, 0, 0, data.Length, Setup(bm, request, wValue, wIndex, (ushort)data.Length));
                if (data.Length > 0) _s.Write(data);
            }
            return Wait(seq, 3000) ?? throw new IOException("control OUT timed out");
        }

        public uint SubmitIn(uint ep, int length)
        {
            uint seq = _seq++;
            lock (_lock) _in.Add(seq);
            Header(1, seq, 1, ep, length, 0);
            return seq;
        }

        public uint SubmitOut(uint ep, byte[] data)
        {
            uint seq = _seq++;
            lock (_s)
            {
                Header(1, seq, 0, ep, data.Length, 0);
                if (data.Length > 0) _s.Write(data);
            }
            return seq;
        }

        public Ret? Wait(uint seq, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
            lock (_lock)
            {
                while (true)
                {
                    if (_rets.TryGetValue(seq, out var r)) return r;
                    long left = deadline - Environment.TickCount64;
                    if (left <= 0) return null;
                    Monitor.Wait(_lock, (int)left);
                }
            }
        }

        public bool Arrived(uint seq) { lock (_lock) return _rets.ContainsKey(seq); }

        /// <summary>Unlink a URB. Returns the RET_UNLINK status.</summary>
        public int Unlink(uint victim)
        {
            uint seq = _seq++;
            Header(2, seq, 0, 0, 0, 0, victim);
            long deadline = Environment.TickCount64 + 3000;
            lock (_lock)
            {
                while (!_unlinks.ContainsKey(seq))
                {
                    long left = deadline - Environment.TickCount64;
                    if (left <= 0) throw new IOException("unlink timed out");
                    Monitor.Wait(_lock, (int)left);
                }
                return _unlinks[seq];
            }
        }

        /// <summary>Send one command on bulk OUT 0x02 and wait for its
        /// URB to be acked.</summary>
        public void BulkOut(byte[] command)
        {
            var r = Wait(SubmitOut(2, command), 3000) ?? throw new IOException("bulk OUT timed out");
            if (r.Status != 0 || r.ActualLength != command.Length)
                throw new IOException($"bulk OUT status {r.Status}, length {r.ActualLength}");
        }

        /// <summary>One read on bulk IN 0x82. Null on timeout, and the read
        /// is then unlinked so it cannot take a later reply.</summary>
        public byte[]? BulkIn(int length, int timeoutMs)
        {
            uint seq = SubmitIn(2, length);
            var r = Wait(seq, timeoutMs);
            if (r.HasValue) return r.Value.Status == 0 ? r.Value.Data : null;
            Unlink(seq);
            var raced = Wait(seq, 0);
            return raced.HasValue && raced.Value.Status == 0 ? raced.Value.Data : null;
        }

        /// <summary>A command and its reply, read the way SDL's RecvBulkData
        /// reads: 64 bytes at a time until a short read or
        /// <paramref name="size"/> bytes.</summary>
        public byte[] Command(byte[] command, int size = 64)
        {
            BulkOut(command);
            var all = new List<byte>();
            while (size > 0)
            {
                int ask = Math.Min(size, 64);
                var part = BulkIn(ask, 2000);
                if (part == null) break;
                all.AddRange(part);
                size -= part.Length;
                if (part.Length < ask) break;
            }
            return all.ToArray();
        }

        /// <summary>Read input reports the way hidusb does: one interrupt
        /// IN read outstanding, the next sent as each completes.</summary>
        public List<Ret> ReadReports(int count, int timeoutMs)
        {
            var got = new List<Ret>(count);
            long deadline = Environment.TickCount64 + timeoutMs;
            while (got.Count < count)
            {
                uint seq = SubmitIn(1, 64);
                var r = Wait(seq, (int)Math.Max(0, deadline - Environment.TickCount64));
                if (!r.HasValue)
                {
                    Unlink(seq);
                    break;
                }
                got.Add(r.Value);
            }
            return got;
        }

        public void Dispose()
        {
            _closed = true;
            try { _tcp.Close(); } catch { }
        }
    }
}
