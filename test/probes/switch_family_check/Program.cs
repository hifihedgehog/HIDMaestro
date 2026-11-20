// switch_family_check (S97): the Switch family's protocol mechanisms carry a
// second device. joycon-l-proof.json, beside this source, is a Joy-Con (L)
// over Bluetooth built from the mechanisms switch-pro uses, a test profile
// and no catalog persona. Offline, no device, no elevation:
//
//   - the profile loads and its protocol compiles for UMDF2
//   - SDL's Joy-Con open, as SDL 3.4.18 sends it over Bluetooth
//     (SDL_hidapi_switch.c), gets a 0x21 reply to every subcommand, and the
//     device info carries nxbt's bytes for a Joy-Con (L) (protocol.py
//     set_device_info at ec4b800: ACK 82, subcommand 02, firmware 03 8B,
//     controller ID 01, 02, the address, 01 01)
//   - before the handshake, the 0x3F report's byte 3 follows dekuNukem's
//     grid (bluetooth_hid_notes.md "Stick hat data" at ac8093c) for the
//     stick pushed in each of the eight directions, with the pad held
//     sideways: upright up reads 6, up-right 7, right 0, down-right 1, down
//     2, down-left 3, left 4, up-left 5, and the center and a push inside
//     the profile's dead zone 8
//
// Then the catalog's Joy-Con (L), Joy-Con (R), SNES, N64 and Genesis
// controllers for Nintendo Switch Online, each on the same mechanisms:
//
//   - the captured identity (the Genesis reports 057E:2017 over Bluetooth)
//     and the captured 170-byte Bluetooth descriptor
//   - the captured 0x3F report at rest, and each button on the 0x3F button
//     and the 0x30 bit the real pad sends it on (dekuNukem, BlueRetro
//     sw.c, hid-nintendo.c button mappings)
//   - SDL 3.4.18's Bluetooth open gets an acknowledged 0x21 reply to every
//     subcommand, the device info carries the pad's device type, and the
//     stick calibration read echoes its address, without which SDL's open
//     fails (SDL_hidapi_switch.c LoadStickCalibration)
//   - the catalog's profile of each pad on USB (the id with -usb) answers
//     SDL's USB setup with the device type and sends the 64-byte 0x30 report
//     after it
//
// Exit 0 PASS, 1 FAIL.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Programs;
using HIDMaestro.Internal.Usbip;

internal static class Program
{
    static int s_failures;

    static void Check(string what, bool ok, string detail = "")
    {
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    sealed class Build : ILayoutVisitor<int>
    {
        public ProtocolEngine Engine = null!;
        public readonly byte[] Buffer = new byte[64];
        public int Visit<TSet>(ref TSet set) where TSet : struct, IReportSet => Engine.BuildNext(ref set, Buffer);
    }

    static int Main()
    {
        Console.WriteLine("=== switch_family_check: a Joy-Con (L) on switch-pro's mechanisms ===");
        string file = Path.Combine(AppContext.BaseDirectory, "joycon-l-proof.json");
        ControllerProfile? p = null;
        CompiledProtocol? proto = null;
        try
        {
            var db = ProfileDatabase.LoadTexts(new[] { (file, File.ReadAllText(file)) }, ModelRegistry.Catalog);
            p = db.All.SingleOrDefault();
            if (p != null)
            {
                p.ValidateDeviceModel();
                proto = CompiledProtocol.Compile(p, null);
            }
            Check("joycon-l-proof loads and its protocol compiles for UMDF2", proto != null,
                  string.Join("; ", db.Issues.Select(i => $"{i.JsonPath} {i.Reason}")));
        }
        catch (Exception e) { Check("joycon-l-proof loads and its protocol compiles for UMDF2", false, e.Message); }
        if (p == null || proto == null) return Done();

        Hat(p, proto);
        Open(p, proto);
        Catalog();
        return Done();
    }

    static int Done()
    {
        Console.WriteLine(s_failures == 0 ? "switch_family_check: PASS" : $"switch_family_check: FAIL, {s_failures}");
        return s_failures == 0 ? 0 : 1;
    }

    static (ProtocolEngine Engine, LayoutRunner Runner) Engine(ControllerProfile p, CompiledProtocol proto, int index)
    {
        var runner = ProgramBinder.BindLayouts(proto.Model)!;
        var e = new ProtocolEngine(proto, p.SerialString, index, runner);
        e.SetStateBlock(p.StateBlockAtRest());
        return (e, runner);
    }

    /// <summary>The 0x3F report's byte 3 for the left stick at
    /// (<paramref name="x"/>, <paramref name="y"/>), each 0 to 1 with y 0
    /// up, as SubmitState encodes it.</summary>
    static void Hat(ControllerProfile p, CompiledProtocol proto)
    {
        var spec = p.StateBlockSpecForCodec();
        var (e, runner) = Engine(p, proto, 0);
        var build = new Build { Engine = e };
        byte? HatAt(float x, float y)
        {
            var block = new byte[spec.Size];
            VendorBlobCodec.EncodeInput(spec, new HMGamepadState(), x, y, 0.5f, 0.5f, 0f, 0f, block, new VendorBlobCodec.EncoderState());
            e.SetStateBlock(block);
            int n = runner.Visit(build);
            return n == 12 && build.Buffer[0] == 0x3F ? build.Buffer[3] : null;
        }
        var cases = new (string Name, float X, float Y, byte Want)[]
        {
            ("up", 0.5f, 0f, 6), ("up-right", 1f, 0f, 7), ("right", 1f, 0.5f, 0), ("down-right", 1f, 1f, 1),
            ("down", 0.5f, 1f, 2), ("down-left", 0f, 1f, 3), ("left", 0f, 0.5f, 4), ("up-left", 0f, 0f, 5),
        };
        var got = new List<string>();
        bool all = true;
        foreach (var c in cases)
        {
            // A center report between pushes, so each push is a change.
            HatAt(0.5f, 0.5f);
            var h = HatAt(c.X, c.Y);
            got.Add($"{c.Name} {h?.ToString() ?? "none"}");
            all &= h == c.Want;
        }
        Check("the stick pushed each way gives dekuNukem's grid, the pad held sideways", all, string.Join(", ", got));
        HatAt(1f, 0.5f);
        var center = HatAt(0.5f, 0.5f);
        HatAt(1f, 0.5f);
        // 768 counts of 0x600 is a push to 0.5 + 0.25 * 0.5.
        var inside = HatAt(0.5f + 0.1f * 0.5f, 0.5f);
        Check("the center gives 8, and a push inside the dead zone 8", center == 8 && inside == 8, $"{center} {inside}");
    }

    /// <summary>SDL's Joy-Con open over Bluetooth through the engine:
    /// device info, the stick and IMU calibration reads, vibration, the
    /// player lights, the IMU and the input mode.</summary>
    static void Open(ControllerProfile p, CompiledProtocol proto)
    {
        const int index = 3;
        var (e, _) = Engine(p, proto, index);
        byte number = 0;
        var replies = new List<byte[]>();
        void Sub(byte id, params byte[] data)
        {
            var pkt = new byte[49];
            pkt[0] = 0x01;
            pkt[1] = number;
            number = (byte)((number + 1) & 0xF);
            new byte[] { 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40 }.CopyTo(pkt, 2);
            pkt[10] = id;
            data.CopyTo(pkt, 11);
            e.HandleCommand(pkt, Stopwatch.GetTimestamp());
            var r = new byte[64];
            int n = e.TakeReply(r.Length, r);
            replies.Add(r[..Math.Max(0, n)]);
        }
        static byte[] Spi(uint a, byte len) => new[] { (byte)a, (byte)(a >> 8), (byte)(a >> 16), (byte)(a >> 24), len };
        Sub(0x02);
        Sub(0x10, Spi(0x8010, 0x16));
        Sub(0x10, Spi(0x603D, 0x12));
        Sub(0x10, Spi(0x6020, 0x18));
        Sub(0x10, Spi(0x8026, 0x14));
        Sub(0x48, 0x01);
        Sub(0x30, 0x01);
        Sub(0x40, 0x01);
        Sub(0x03, 0x30);
        Check("every subcommand of SDL's open gets a 49-byte 0x21 reply with an ACK",
              replies.All(r => r.Length == 49 && r[0] == 0x21 && (r[13] & 0x80) != 0),
              string.Join(" ", replies.Select(r => r.Length > 14 ? Convert.ToHexString(r.AsSpan(13, 2)) : "none")));
        var info = replies[0];
        byte[] want = { 0x82, 0x02, 0x03, 0x8B, 0x01, 0x02, 0x98, 0xB6, 0xE9, 0x48, 0x4D, (byte)(0x30 + index), 0x01, 0x01 };
        Check("the device info is nxbt's for a Joy-Con (L): 82 02 03 8B 01 02, the address, 01 01",
              info.Length == 49 && info.AsSpan(13, want.Length).SequenceEqual(want), Convert.ToHexString(info.AsSpan(13, Math.Min(14, Math.Max(0, info.Length - 13)))));
        Check("the reply's battery and connection byte is nxbt's 9E for a Joy-Con", info.Length > 2 && info[2] == 0x9E, info.Length > 2 ? $"{info[2]:X2}" : "");
    }

    // ── The catalog's five Bluetooth-only Nintendo pads and each on USB ─────

    /// <summary>The 170-byte Bluetooth descriptor every Switch Bluetooth
    /// pad serves, as captured from Joy-Con (L), Joy-Con (R), SNES, N64 and
    /// Genesis controllers (darthcloud bt_traces, Nefarius.Utilities.Bluetooth
    /// Dumps, switchbrew Joy-Con revisions 153 and 154).</summary>
    const string BtDescriptor =
        "05010905a1010601ff8521092175089530810285300930750895308102853109317508966901810285320932750896690181028533093375089669018102853f05091901291015002501750195108102050109391500250775049501814205097504950181010501093009310933093416000027ffff00007510950481020601ff85010901750895309102851009107508953091028511091175089530910285120912750895309102c0";

    /// <summary>One pad: its Bluetooth identity, the device type it reports
    /// (SDL_hidapi_nintendo.h, hid-nintendo.c), the PID it has on USB,
    /// whether SDL reads its six-axis calibration (SDL_hidapi_switch.c
    /// HIDAPI_DriverSwitch_OpenJoystick skips it for the NSO pads), byte 2
    /// and byte 12 of its 0x30 report, its 0x3F report at rest, and each
    /// button as HMButton bit, 0x30 byte and mask, and 0x3F button bit
    /// (dekuNukem bluetooth_hid_notes.md, BlueRetro sw.c, hid-nintendo.c
    /// button mappings). Info is the device info a real pad of the kind
    /// sends, its address left out, where a capture holds one (darthcloud
    /// bt_traces, dekuNukem's Joy-Con sample). Arrows says how SubmitState's
    /// hat reaches the 0x3F report: "buttons" for the Joy-Con (L), whose
    /// arrows are its buttons 1 to 4 as left, down, up and right
    /// (SDL_hidapi_switch.c HandleCombinedSimpleControllerStateL, BlueRetro
    /// sw.c sw_jc_btns_mask), "hat" for the NSO pads, and "none" for the
    /// Joy-Con (R). UsbType is what 81 01 says on USB, and UsbProduct the
    /// form's product string: the charging grip's for a Joy-Con.</summary>
    sealed record Pad(string Id, ushort Pid, string Product, byte Type, ushort UsbPid, bool Imu, byte Battery, byte Motor,
                      string Rest3F, (int Bit, int Byte, byte Mask, int Simple)[] Buttons,
                      string? Info = null, string Arrows = "hat", byte? UsbType = null, string? UsbProduct = null);

    static readonly Pad[] Pads =
    {
        new("joycon-l", 0x2006, "Joy-Con (L)", 0x01, 0x200E, true, 0x8E, 0xB0, "3F0000080080008000800080", new[]
        {
            (4, 5, (byte)0x20, 4), (5, 5, (byte)0x10, 5), (8, 4, (byte)0x01, 8), (10, 4, (byte)0x08, 10),
            (13, 4, (byte)0x20, 13), (14, 5, (byte)0x40, 14), (15, 5, (byte)0x80, 15),
        }, Info: "03480102|0101", Arrows: "buttons", UsbProduct: "Joy-Con Charging Grip"),
        new("joycon-r", 0x2007, "Joy-Con (R)", 0x02, 0x200E, true, 0x8E, 0x0A, "3F0000080080008000800080", new[]
        {
            (0, 3, (byte)0x08, 0), (1, 3, (byte)0x02, 1), (2, 3, (byte)0x04, 2), (3, 3, (byte)0x01, 3),
            (4, 3, (byte)0x20, 4), (5, 3, (byte)0x10, 5), (9, 4, (byte)0x02, 9), (11, 4, (byte)0x04, 11),
            (12, 4, (byte)0x10, 12), (14, 3, (byte)0x40, 14), (15, 3, (byte)0x80, 15),
        }, Arrows: "none", UsbProduct: "Joy-Con Charging Grip"),
        new("snes-nso", 0x2017, "SNES Controller", 0x0B, 0x2017, false, 0x80, 0x00, "3F0000080080008000800080", new[]
        {
            (0, 3, (byte)0x04, 0), (1, 3, (byte)0x08, 1), (2, 3, (byte)0x01, 2), (3, 3, (byte)0x02, 3),
            (4, 5, (byte)0x40, 4), (5, 3, (byte)0x40, 5), (6, 5, (byte)0x80, 6), (15, 3, (byte)0x80, 15),
            (8, 4, (byte)0x01, 8), (9, 4, (byte)0x02, 9),
        }, Info: "03B80B02|0002"),
        new("n64-nso", 0x2019, "N64 Controller", 0x0C, 0x2019, false, 0x80, 0x08, "3F00000800800080" + "00806F5C", new[]
        {
            (0, 3, (byte)0x04, 0), (1, 3, (byte)0x08, 1), (2, 3, (byte)0x01, 2), (3, 3, (byte)0x02, 3),
            (4, 5, (byte)0x40, 4), (5, 3, (byte)0x40, 5), (6, 5, (byte)0x80, 6), (7, 3, (byte)0x80, 7),
            (8, 4, (byte)0x01, 8), (9, 4, (byte)0x02, 9), (10, 4, (byte)0x08, 10), (12, 4, (byte)0x10, 12),
            (13, 4, (byte)0x20, 13),
        }, Info: "04110C02|0002", UsbType: 0x03),
        new("genesis-nso", 0x2017, "MD/Gen Control Pad", 0x0D, 0x201E, false, 0x80, 0x00, "3F0000080080008000800080", new[]
        {
            (0, 3, (byte)0x04, 0), (1, 3, (byte)0x08, 1), (5, 3, (byte)0x40, 5), (7, 3, (byte)0x80, 7),
            (9, 4, (byte)0x02, 9), (12, 4, (byte)0x10, 12), (13, 4, (byte)0x20, 13),
        }),
    };

    static void Catalog()
    {
        Console.WriteLine();
        Console.WriteLine("--- The catalog's Bluetooth-only Nintendo pads and each on USB ---");
        var db = ProfileDatabase.LoadEmbedded();
        var byId = db.All.ToDictionary(q => q.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var pad in Pads)
        {
            Console.WriteLine($"  {pad.Id}");
            if (!byId.TryGetValue(pad.Id, out var p)) { Check($"{pad.Id}: in the catalog", false); continue; }
            Check($"{pad.Id}: 057E:{pad.Pid:X4} \"{pad.Product}\" over Bluetooth",
                  p.VendorId == 0x057E && p.ProductId == pad.Pid && p.ProductString == pad.Product
                  && string.Equals(p.Connection, "bluetooth", StringComparison.OrdinalIgnoreCase),
                  $"{p.VendorId:X4}:{p.ProductId:X4} \"{p.ProductString}\" {p.Connection}");
            Check($"{pad.Id}: the captured 170-byte Bluetooth descriptor",
                  string.Equals(p.Descriptor, BtDescriptor, StringComparison.OrdinalIgnoreCase), $"{(p.Descriptor?.Length ?? 0) / 2} bytes");
            CompiledProtocol? proto = null;
            try { p.ValidateDeviceModel(); proto = CompiledProtocol.Compile(p, null); }
            catch (Exception e) { Check($"{pad.Id}: its protocol compiles", false, e.Message); }
            if (proto == null) { Check($"{pad.Id}: carries a protocol", false); continue; }
            CheckPad(pad, p, proto);
            CheckOnUsb(pad, byId);
        }
    }

    static byte[] Subcommand(ref byte number, byte id, int length, params byte[] data)
    {
        var pkt = new byte[length];
        pkt[0] = 0x01;
        pkt[1] = number;
        number = (byte)((number + 1) & 0xF);
        new byte[] { 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40 }.CopyTo(pkt, 2);
        pkt[10] = id;
        data.CopyTo(pkt, 11);
        return pkt;
    }

    static byte[] Spi(uint a, byte len) => new[] { (byte)a, (byte)(a >> 8), (byte)(a >> 16), (byte)(a >> 24), len };

    static void CheckPad(Pad pad, ControllerProfile p, CompiledProtocol proto)
    {
        const int index = 2;
        var (e, runner) = Engine(p, proto, index);
        var build = new Build { Engine = e };
        var spec = p.StateBlockSpecForCodec();

        // At rest, before a host speaks the protocol: the captured 0x3F report.
        int n = runner.Visit(build);
        string rest = n > 0 ? Convert.ToHexString(build.Buffer.AsSpan(0, n)) : "none";
        Check($"{pad.Id}: before the handshake, the captured 0x3F report at rest", rest == pad.Rest3F, rest);

        // Each button in the 0x3F report, before the handshake.
        var miss = new List<string>();
        foreach (var b in pad.Buttons)
        {
            var block = new byte[spec.Size];
            VendorBlobCodec.EncodeInput(spec, new HMGamepadState { Buttons = (HMButton)(1u << b.Bit) },
                0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f, block, new VendorBlobCodec.EncoderState());
            e.SetStateBlock(block);
            int m = runner.Visit(build);
            int word = m == 12 ? build.Buffer[1] | (build.Buffer[2] << 8) : -1;
            if (word != 1 << b.Simple) miss.Add($"#{b.Bit}->{(word < 0 ? "none" : "0x" + word.ToString("X4"))}");
        }
        e.SetStateBlock(p.StateBlockAtRest());
        Check($"{pad.Id}: each button on its own 0x3F button", miss.Count == 0, string.Join(" ", miss));
        Arrows3F(pad, runner, build, spec);
        e.SetStateBlock(p.StateBlockAtRest());

        // SDL 3.4.18's Bluetooth open (SDL_hidapi_switch.c BReadDeviceInfo,
        // LoadStickCalibration, LoadIMUCalibration for the Joy-Cons only,
        // SetVibrationEnabled, UpdateSlotLED, the Joy-Cons' home LED,
        // SetInputMode), then hid-nintendo's extra calibration reads.
        byte number = 0;
        var sent = new List<(string Name, byte[] Reply)>();
        void Sub(string name, byte id, params byte[] data)
        {
            e.HandleCommand(Subcommand(ref number, id, 49, data), Stopwatch.GetTimestamp());
            var r = new byte[64];
            int k = e.TakeReply(r.Length, r);
            sent.Add((name, r[..Math.Max(0, k)]));
        }
        Sub("02", 0x02);
        Sub("10 8010", 0x10, Spi(0x8010, 0x16));
        Sub("10 603D", 0x10, Spi(0x603D, 0x12));
        if (pad.Imu) { Sub("10 6020", 0x10, Spi(0x6020, 0x18)); Sub("10 8026", 0x10, Spi(0x8026, 0x14)); }
        Sub("48", 0x48, 0x01);
        Sub("30", 0x30, 0x01);
        if (pad.Imu) { Sub("38", 0x38, 0x0F, 0xF0, 0x00); Sub("40", 0x40, 0x01); }
        Sub("10 6086", 0x10, Spi(0x6086, 0x12));
        Sub("03", 0x03, 0x30);
        var bad = sent.Where(s => s.Reply.Length != 49 || s.Reply[0] != 0x21 || (s.Reply[13] & 0x80) == 0 || s.Reply[14] != byte.Parse(s.Name[..2], System.Globalization.NumberStyles.HexNumber))
                      .Select(s => $"{s.Name}:{(s.Reply.Length > 14 ? Convert.ToHexString(s.Reply.AsSpan(13, 2)) : "none")}").ToList();
        Check($"{pad.Id}: every subcommand of SDL's open gets a 49-byte 0x21 reply that acknowledges it", bad.Count == 0, string.Join(" ", bad));
        var info = sent[0].Reply;
        Check($"{pad.Id}: the device info carries device type {pad.Type:X2} and the address",
              info.Length == 49 && info[15 + 2] == pad.Type && info.AsSpan(19, 5).SequenceEqual(new byte[] { 0x98, 0xB6, 0xE9, 0x48, 0x4D }) && info[24] == 0x30 + index,
              info.Length == 49 ? Convert.ToHexString(info.AsSpan(13, 14)) : "");
        if (pad.Info != null)
        {
            var parts = pad.Info.Split('|');
            string got = info.Length == 49 ? Convert.ToHexString(info.AsSpan(15, 4)) + "|" + Convert.ToHexString(info.AsSpan(25, 2)) : "none";
            Check($"{pad.Id}: the device info is the captured pad's: {parts[0]}, the address, {parts[1]}", got == pad.Info, got);
        }
        var cal = sent[2].Reply;
        Check($"{pad.Id}: the stick calibration read echoes 0x603D and 18 bytes, as SDL requires",
              cal.Length == 49 && cal[13] == 0x90 && cal.AsSpan(15, 5).SequenceEqual(Spi(0x603D, 0x12)),
              cal.Length == 49 ? Convert.ToHexString(cal.AsSpan(13, 7)) : "");
        Check($"{pad.Id}: the reply's byte 2 is {pad.Battery:X2} and byte 12 {pad.Motor:X2}",
              info.Length == 49 && info[2] == pad.Battery && info[12] == pad.Motor, info.Length == 49 ? $"{info[2]:X2} {info[12]:X2}" : "");
        var typeAt = new byte[1];
        e.ReadMemory(0, 0x6012, typeAt);
        Check($"{pad.Id}: SPI 0x6012 holds the device type", typeAt[0] == pad.Type, $"{typeAt[0]:X2}");

        // After the handshake: the 0x30 report and each button on its bit.
        n = runner.Visit(build);
        Check($"{pad.Id}: after the handshake the 49-byte 0x30 report, byte 2 {pad.Battery:X2} and byte 12 {pad.Motor:X2}",
              n == 49 && build.Buffer[0] == 0x30 && build.Buffer[2] == pad.Battery && build.Buffer[12] == pad.Motor,
              n > 12 ? Convert.ToHexString(build.Buffer.AsSpan(0, 13)) : $"{n} bytes");
        miss.Clear();
        foreach (var b in pad.Buttons)
        {
            var block = new byte[spec.Size];
            VendorBlobCodec.EncodeInput(spec, new HMGamepadState { Buttons = (HMButton)(1u << b.Bit) },
                0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f, block, new VendorBlobCodec.EncoderState());
            e.SetStateBlock(block);
            int m = runner.Visit(build);
            bool only = m == 49 && build.Buffer[b.Byte] == b.Mask
                        && Enumerable.Range(3, 3).All(i => i == b.Byte || build.Buffer[i] == 0);
            if (!only) miss.Add($"#{b.Bit}->{(m == 49 ? Convert.ToHexString(build.Buffer.AsSpan(3, 3)) : "none")}");
        }
        Check($"{pad.Id}: each button on the 0x30 bit the real pad sends it on", miss.Count == 0, string.Join(" ", miss));

        // The arrows in the 0x30 report: byte 5, Down 01, Up 02, Right 04,
        // Left 08 (dekuNukem bluetooth_hid_notes.md, SDL
        // HandleFullControllerState), on every pad with arrows.
        var wrong = new List<string>();
        foreach (var (hat, mask) in new[] { (HMHat.West, 0x08), (HMHat.South, 0x01), (HMHat.North, 0x02), (HMHat.East, 0x04) })
        {
            int m = VisitWith(runner, build, ArrowBlock(spec, hat));
            int want = pad.Arrows == "none" ? 0 : mask;
            if (m != 49 || build.Buffer[5] != want || build.Buffer[3] != 0 || build.Buffer[4] != 0)
                wrong.Add($"{hat}->{(m == 49 ? Convert.ToHexString(build.Buffer.AsSpan(3, 3)) : "none")}");
        }
        Check(pad.Arrows == "none" ? $"{pad.Id}: SubmitState's hat sets no bit of the 0x30 report, which has no arrows"
                                   : $"{pad.Id}: each arrow on its 0x30 bit", wrong.Count == 0, string.Join(" ", wrong));
    }

    static byte[] ArrowBlock(ExtendedReportSpec spec, HMHat hat)
    {
        var block = new byte[spec.Size];
        VendorBlobCodec.EncodeInput(spec, new HMGamepadState { Hat = hat }, 0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f, block,
                                    new VendorBlobCodec.EncoderState());
        return block;
    }

    static int VisitWith(LayoutRunner runner, Build build, byte[] block)
    {
        build.Engine.SetStateBlock(block);
        return runner.Visit(build);
    }

    /// <summary>Each arrow in the 0x3F report: the Joy-Con (L) sends left,
    /// down, up and right on buttons 1 to 4, the NSO pads send the D-pad as
    /// the hat (0 up, 2 right, 4 down, 6 left), and the Joy-Con (R) has
    /// none, so its report keeps the rest state.</summary>
    static void Arrows3F(Pad pad, LayoutRunner runner, Build build, ExtendedReportSpec spec)
    {
        var wrong = new List<string>();
        foreach (var (hat, bit, hatValue) in new[] { (HMHat.West, 0, 6), (HMHat.South, 1, 4), (HMHat.North, 2, 0), (HMHat.East, 3, 2) })
        {
            VisitWith(runner, build, ArrowBlock(spec, HMHat.None));
            int m = VisitWith(runner, build, ArrowBlock(spec, hat));
            string got = m == 12 ? Convert.ToHexString(build.Buffer.AsSpan(0, 4)) : $"{m} bytes";
            bool ok = pad.Arrows switch
            {
                "buttons" => m == 12 && build.Buffer[1] == 1 << bit && build.Buffer[2] == 0 && build.Buffer[3] == 8,
                "hat" => m == 12 && build.Buffer[1] == 0 && build.Buffer[2] == 0 && build.Buffer[3] == hatValue,
                _ => m == 0 || (m == 12 && build.Buffer[1] == 0 && build.Buffer[2] == 0 && build.Buffer[3] == 8),
            };
            if (!ok) wrong.Add($"{hat}->{got}");
        }
        Check(pad.Arrows switch
        {
            "buttons" => $"{pad.Id}: the arrows on 0x3F buttons 1 to 4 as left, down, up and right",
            "hat" => $"{pad.Id}: the D-pad as the 0x3F hat, 0 up, 2 right, 4 down, 6 left",
            _ => $"{pad.Id}: SubmitState's hat changes nothing in the 0x3F report, which has no arrows",
        }, wrong.Count == 0, string.Join(" ", wrong));
    }

    /// <summary>The catalog's profile of <paramref name="pad"/> on USB, the
    /// pad's id with -usb: a profile of its own, which a create of the
    /// Bluetooth pad never presents.</summary>
    static void CheckOnUsb(Pad pad, Dictionary<string, ControllerProfile> byId)
    {
        string formId = pad.Id + "-usb";
        if (!byId.TryGetValue(formId, out var f)) { Check($"{formId}: in the catalog", false); return; }
        Check($"{formId}: 057E:{pad.UsbPid:X4} on USB", f.VendorId == 0x057E && f.ProductId == pad.UsbPid
              && string.Equals(f.Connection, "usb", StringComparison.OrdinalIgnoreCase), $"{f.VendorId:X4}:{f.ProductId:X4} {f.Connection}");
        string wantProduct = pad.UsbProduct ?? pad.Product;
        Check($"{formId}: the product string \"{wantProduct}\"", f.ProductString == wantProduct, f.ProductString ?? "none");
        if (pad.UsbProduct != null)
        {
            // The charging grip: two HID interfaces, interface 1 the left
            // slot and 0 the right (SDL_hidapi_switch.c
            // CalculateControllerType), the docked pad's interface first.
            var ifaces = f.UsbConfiguration?.Interfaces;
            int slot = pad.Type == 0x01 ? 1 : 0;
            Check($"{formId}: the grip's two HID interfaces, the pad on interface {slot}",
                  ifaces != null && ifaces.Count == 2 && ifaces[0].InterfaceNumber == slot && ifaces[0].Function == "hid"
                  && ifaces[1].InterfaceNumber == 1 - slot && f.RequiresUsbipBackend,
                  ifaces == null ? "none" : string.Join(" ", ifaces.Select(i => $"{i.InterfaceNumber}:{i.Function}")));
        }
        CompiledProtocol? proto = null;
        // The charging grip runs its protocol on its own endpoints, the
        // USB/IP backend's form of a protocol.
        try { f.ValidateDeviceModel(); proto = CompiledProtocol.Compile(f, f.RequiresUsbipBackend ? new UsbDescriptorSet(f) : null); }
        catch (Exception ex) { Check($"{formId}: its protocol compiles", false, ex.Message); }
        if (proto == null) return;
        const int index = 1;
        var (e, runner) = Engine(f, proto, index);
        // SDL's USB setup (SDL_hidapi_switch.c BTrySetupUSB, BReadDeviceInfo
        // on a USB bus): 80 01 answers 81 01 00 with the device type and the
        // address least significant byte first.
        var status = new byte[64];
        status[0] = 0x80; status[1] = 0x01;
        e.HandleCommand(status, Stopwatch.GetTimestamp());
        var r = new byte[64];
        int k = e.TakeReply(r.Length, r);
        byte usbType = pad.UsbType ?? pad.Type;
        Check($"{formId}: 80 01 answers 81 01 00 {usbType:X2} and the address",
              k == 64 && r[0] == 0x81 && r[1] == 0x01 && r[2] == 0x00 && r[3] == usbType && r[4] == 0x30 + index && r[5] == 0x4D,
              k > 0 ? Convert.ToHexString(r.AsSpan(0, 10)) : "none");
        byte number = 0;
        e.HandleCommand(Subcommand(ref number, 0x02, 64), Stopwatch.GetTimestamp());
        k = e.TakeReply(r.Length, r);
        Check($"{formId}: the device info subcommand answers in a 64-byte 0x21 reply with type {pad.Type:X2}",
              k == 64 && r[0] == 0x21 && r[13] == 0x82 && r[14] == 0x02 && r[17] == pad.Type, k > 14 ? Convert.ToHexString(r.AsSpan(13, 6)) : "none");
        var build = new Build { Engine = e };
        int n = runner.Visit(build);
        Check($"{formId}: after the handshake the 64-byte 0x30 report", n == 64 && build.Buffer[0] == 0x30, $"{n} bytes");
    }
}
