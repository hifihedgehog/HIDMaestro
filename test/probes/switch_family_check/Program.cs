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
}
