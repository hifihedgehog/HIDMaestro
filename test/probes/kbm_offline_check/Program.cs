// Keyboards and mice without a device (issue #67).
//
//   1. HidDescriptorBuilder: recipes that worked before v1.12.0 build the
//      same bytes (each pinned below is what v1.11.0 built), and a stick
//      after buttons or a Simulation axis now gets its Generic Desktop page.
//   2. The Mouse, Keyboard, KeyboardNkro, ConsumerControl and VendorButtons
//      starters, and the guards on combining them.
//   3. The packers against bytes worked out by hand from each descriptor:
//      the starters, a Razer DeathAdder V2 and an Apple Magic Keyboard from
//      the catalog, relative movement split across reports, ErrorRollOver,
//      extra buttons in a second collection, and LED decoding.
//   4. Every keyboard and mouse in the catalog parses, packs and reads back,
//      fits the 80-byte extended lane, and is the only catalog profile that
//      delivers every report.
//
// Exit 0 PASS / 1 FAIL.

using System;
using System.Collections.Generic;
using System.Linq;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static string Hex(byte[] b) => Convert.ToHexString(b);

    static bool Throws<T>(Action a) where T : Exception
    {
        try { a(); return false; }
        catch (T) { return true; }
    }

    // What v1.11.0's HidDescriptorBuilder built for these recipes. The last
    // two were broken then (a stick on the Button and Simulation pages) and
    // are pinned at their corrected bytes.
    static readonly (string Name, Func<HidDescriptorBuilder> Build, string Hex)[] BuilderPins =
    {
        ("gamepad-canonical", () => new HidDescriptorBuilder().Gamepad().AddStick("Left", 16).AddStick("Right", 16)
            .AddTrigger("Left", 8).AddTrigger("Right", 8).AddHat().AddButtons(11),
         "05010905A101A10009300931150027FFFF0000950275108102C0A10009320935150027FFFF0000950275108102C005010933150026FF0095017508810205010934150026FF0095017508810205010939150025073500463B016614007508950181424500650005091901291015002501951075018102C0"),
        ("gamepad-8bit", () => new HidDescriptorBuilder().Gamepad().AddStick("Left", 8).AddStick("Right", 8)
            .AddTrigger("Left").AddTrigger("Right").AddHat(16).AddButtons(14),
         "05010905A101A10009300931150026FF00950275088102C0A10009320935150026FF00950275088102C005010933150026FF0095017508810205010934150026FF00950175088102050109391500250F35004651016614007508950181424500650005091901291015002501951075018102C0"),
        ("joystick-axes", () => new HidDescriptorBuilder().Joystick().AddStick("Left").AddAxis(HMAxis.Throttle, 8)
            .AddAxis(HMAxis.Rudder, 16, -32768, 32767).AddButtons(4),
         "05010904A101A10009300931150027FFFF0000950275108102C0050209BB150026FF00950175088102050209BA16008026FF7F95017510810205091901290815002501950875018102C0"),
        ("padforge-four-sticks", () =>
            {
                var b = new HidDescriptorBuilder().Joystick();
                for (int s = 0; s < 4; s++) b.AddStick(s == 0 ? "Left" : "Right", 16);
                return b.AddAxis(HMAxis.Vx, 16, 0, 65535).AddHat().AddHat().AddButtons(32);
            },
         "05010904A101A10009300931150027FFFF0000950275108102C0A10009320935150027FFFF0000950275108102C0A10009330934150027FFFF0000950275108102C0A10009360937150027FFFF0000950275108102C005010940150026FFFF95017510810205010939150025073500463B016614007508950181424500650005010939150025073500463B016614007508950181424500650005091901292015002501952075018102C0"),
        ("raw-then-stick", () => new HidDescriptorBuilder().Gamepad()
            .AddRaw(new byte[] { 0x05, 0x01, 0x09, 0x36, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08, 0x95, 0x01, 0x81, 0x02 })
            .AddStick("Left").AddButtons(8),
         "05010905A10105010936150026FF00750895018102A10009300931150027FFFF0000950275108102C005091901290815002501950875018102C0"),
        ("buttons-then-stick (fixed)", () => new HidDescriptorBuilder().Gamepad().AddButtons(8).AddStick("Left").AddStick("Right"),
         "05010905A101050919012908150025019508750181020501A10009300931150027FFFF0000950275108102C0A10009320935150027FFFF0000950275108102C0C0"),
        ("simulation-axis-then-stick (fixed)", () => new HidDescriptorBuilder().Joystick().AddAxis(HMAxis.Throttle).AddStick("Left").AddButtons(2),
         "05010904A101050209BB150026FF009501750881020501A10009300931150027FFFF0000950275108102C005091901290815002501950875018102C0"),
    };

    static void Builder()
    {
        Console.WriteLine("\n== 1. HidDescriptorBuilder output ==");
        foreach (var (name, build, hex) in BuilderPins)
        {
            string got = Hex(build().Build());
            Check($"{name} builds the pinned bytes", got == hex, got == hex ? "" : got);
        }
        var pf = new HidDescriptorBuilder().Joystick().AddStick("Left", 16).AddStick("Right", 16)
            .AddTrigger("Left", 16).AddTrigger("Right", 16).AddHat().AddButtons(11).AddPidFfbBlock().Build();
        string pfHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pf));
        Check("PadForge's custom recipe with the PID block is v1.11.0's 1,154 bytes",
              pf.Length == 1154 && pfHash == "B6EBFF1CCE866CBABAC561B6103378181A7EF5403737EC99FA06985B557D3812",
              $"{pf.Length} bytes, SHA-256 {pfHash[..16]}");
    }

    static void Starters()
    {
        Console.WriteLine("\n== 2. Keyboard and mouse starters ==");
        var mouse = HidReportMap.Parse(new HidDescriptorBuilder().Mouse().Build());
        var mc = mouse.Collections.Single();
        Check("Mouse() is one Generic Desktop / Mouse collection", mc.UsagePage == 1 && mc.Usage == 2);
        var mr = mouse.Find(HidReportKind.Input, 0)!;
        Check("its report is 7 bytes: buttons, X, Y, wheel, pan", mr.FrameBytes == 7, $"{mr.FrameBytes}");
        var x = mr.Fields.First(f => f.Usages.Any(u => u.Min == 0x00010030));
        Check("X and Y are relative 16-bit, -32767 to 32767", x.Relative && x.BitSize == 16 && x.LogicalMin == -32767 && x.LogicalMax == 32767);
        var abs = HidReportMap.Parse(new HidDescriptorBuilder().Mouse(absolute: true, wheel: false, pan: false).Build());
        var ax = abs.Find(HidReportKind.Input, 0)!.Fields.First(f => f.Usages.Any(u => u.Min == 0x00010030));
        Check("the absolute form runs 0 to 32767", !ax.Relative && ax.LogicalMin == 0 && ax.LogicalMax == 32767);

        var kb = HidReportMap.Parse(new HidDescriptorBuilder().Keyboard().Build());
        Check("Keyboard() input is the 8-byte boot report", kb.Find(HidReportKind.Input, 0)!.FrameBytes == 8);
        Check("and its LED output report is one byte", kb.Find(HidReportKind.Output, 0)?.FrameBytes == 1);
        var nkro = HidReportMap.Parse(new HidDescriptorBuilder().KeyboardNkro().Build());
        Check("KeyboardNkro() input is 29 bytes", nkro.Find(HidReportKind.Input, 0)!.FrameBytes == 29);

        var multi = new HidDescriptorBuilder().Mouse(reportId: 1).VendorButtons(3, 2).ConsumerControl(1, 3);
        var mm = HidReportMap.Parse(multi.Build());
        Check("a mouse, vendor buttons and consumer controls make three collections",
              mm.Collections.Count == 3 && mm.Collections[1].UsagePage == 0xFF00 && mm.Collections[2].UsagePage == 0x0C);
        Check("InputReportByteSize is the first collection's data", multi.InputReportByteSize == 7, $"{multi.InputReportByteSize}");

        Check("two collections without report IDs are refused",
              Throws<InvalidOperationException>(() => new HidDescriptorBuilder().Mouse().Keyboard()));
        Check("a repeated report ID is refused",
              Throws<ArgumentException>(() => new HidDescriptorBuilder().Mouse(reportId: 1).Keyboard(reportId: 1)));
        Check("a starter inside an open Gamepad collection is refused",
              Throws<InvalidOperationException>(() => new HidDescriptorBuilder().Gamepad().Mouse()));
        Check("a Gamepad collection after a starter is refused",
              Throws<InvalidOperationException>(() => new HidDescriptorBuilder().Mouse().Gamepad()));
        Check("vendor buttons need a report ID",
              Throws<ArgumentException>(() => new HidDescriptorBuilder().VendorButtons(3, 0)));

        // Usage pages as hid-core.c (hid_add_usage, then
        // hid_concatenate_last_usage_page) and hid-tools assign them.
        static uint[] UsagesOf(byte[] d) => HidReportMap.Parse(d).Find(HidReportKind.Input, 0)!.Fields[0]
            .Usages.SelectMany(r => Enumerable.Range(0, r.Count).Select(k => r.Min + (uint)k)).ToArray();
        byte[] Wrap(params byte[] items) =>
            new byte[] { 0x05, 0x01, 0x09, 0x02, 0xA1, 0x01 }
            .Concat(items)
            .Concat(new byte[] { 0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x02, 0x81, 0x02, 0xC0 }).ToArray();
        var mixed = UsagesOf(Wrap(0x09, 0x30, 0x05, 0x09, 0x09, 0x01));
        Check("a usage declared under an earlier page keeps it (01:30, then 09:01)",
              mixed.SequenceEqual(new uint[] { 0x00010030, 0x00090001 }), string.Join(",", mixed.Select(u => u.ToString("X8"))));
        var late = UsagesOf(Wrap(0x05, 0x09, 0x09, 0x30, 0x09, 0x31, 0x05, 0x01));
        Check("a page declared after its usages completes them (01:30, 01:31)",
              late.SequenceEqual(new uint[] { 0x00010030, 0x00010031 }), string.Join(",", late.Select(u => u.ToString("X8"))));
        var range = UsagesOf(Wrap(0x05, 0x09, 0x19, 0x01, 0x29, 0x02));
        Check("a Usage Minimum / Maximum pair takes the page in force (09:01, 09:02)",
              range.SequenceEqual(new uint[] { 0x00090001, 0x00090002 }), string.Join(",", range.Select(u => u.ToString("X8"))));
        var alt = UsagesOf(Wrap(0xA9, 0x01, 0x09, 0x30, 0xA9, 0x00, 0xA9, 0x01, 0x09, 0x31, 0xA9, 0x00));
        Check("of two alternative usage sets only the first counts (01:30)",
              alt.SequenceEqual(new uint[] { 0x00010030 }), string.Join(",", alt.Select(u => u.ToString("X8"))));
    }

    static KeyboardMousePacker Packer(byte[] d) => new KeyboardMousePacker(d);

    static List<byte[]> Mouse(KeyboardMousePacker p, HMMouseState s)
    {
        uint extra = 0; bool sent = false;
        return Copy(p.PackMouse(in s, ref extra, ref sent, new KeyboardMouseBuffers()));
    }

    // The packer hands back its buffers, which its next call on the same
    // set reuses, so a check that keeps frames copies them.
    static List<byte[]> Copy(List<byte[]> frames) => frames.Select(f => f.ToArray()).ToList();

    static byte[] Kb(KeyboardMousePacker p, HMKeyModifiers m, ReadOnlySpan<HMKey> keys) =>
        p.PackKeyboard(m, keys, new KeyboardMouseBuffers()).ToArray();

    static byte[] Cc(KeyboardMousePacker p, ReadOnlySpan<ushort> usages) =>
        p.PackConsumer(usages, new KeyboardMouseBuffers()).ToArray();

    static void Packers(HMContext ctx)
    {
        Console.WriteLine("\n== 3. Packing ==");
        var kb = Packer(new HidDescriptorBuilder().Keyboard().Build());
        Check("Shift+A+B is 02 00 04 05 00 00 00 00",
              Hex(Kb(kb, HMKeyModifiers.LeftShift, new[] { HMKey.A, HMKey.B })) == "0200040500000000");
        Check("a modifier passed as a key lands on its bit",
              Hex(Kb(kb, HMKeyModifiers.None, new[] { HMKey.LeftShift })) == "0200000000000000");
        Check("seven keys roll over: ErrorRollOver in all six slots",
              Hex(Kb(kb, HMKeyModifiers.None, new[] { HMKey.A, HMKey.B, HMKey.C, HMKey.D, HMKey.E, HMKey.F, HMKey.G })) == "0000010101010101");
        // An array whose usages start at A (0x04) with Logical Minimum 0: 0
        // selects A, so an empty slot must hold a value past the range.
        var fromA = Packer(new byte[]
        {
            0x05, 0x01, 0x09, 0x06, 0xA1, 0x01,
            0x05, 0x07, 0x19, 0x04, 0x29, 0x65, 0x15, 0x00, 0x25, 0x61, 0x75, 0x08, 0x95, 0x02, 0x81, 0x00,
            0xC0,
        });
        Check("an array starting at A leaves empty slots past its range (A, then 0x62)",
              Hex(Kb(fromA, HMKeyModifiers.None, new[] { HMKey.A })) == "0062"
              && Hex(Kb(fromA, HMKeyModifiers.None, ReadOnlySpan<HMKey>.Empty)) == "6262");

        // A keyboard without report IDs receives the LED byte alone, and both
        // backends carry a report's first byte in the packet's report ID
        // (HMOutputPacket.ReportId), so the LED byte arrives there.
        Check("Num Lock and Caps Lock decode from the LED byte, carried as the packet's report ID",
              kb.DecodeLeds(0x03, ReadOnlySpan<byte>.Empty) == (HMKeyboardLeds.NumLock | HMKeyboardLeds.CapsLock));
        var kb2 = Packer(new HidDescriptorBuilder().Keyboard(reportId: 2).Build());
        Check("with Report ID 2 the LED byte follows the ID",
              kb2.DecodeLeds(2, new byte[] { 0x03 }) == (HMKeyboardLeds.NumLock | HMKeyboardLeds.CapsLock));
        Check("a report ID the LED report does not use decodes to nothing", kb2.DecodeLeds(5, new byte[] { 0x03 }) == null);

        var nkro = Packer(new HidDescriptorBuilder().KeyboardNkro().Build());
        var n = Kb(nkro, HMKeyModifiers.LeftControl, new[] { HMKey.A, HMKey.Z });
        Check("NKRO: Ctrl, then A as bit 12 and Z as bit 37",
              n.Length == 29 && n[0] == 0x01 && n[1] == 0x10 && n[4] == 0x20 && n.Where((b, i) => i is not (0 or 1 or 4)).All(b => b == 0),
              Hex(n));

        var m = Packer(new HidDescriptorBuilder().Mouse().Build());
        var f = Mouse(m, new HMMouseState { Buttons = HMMouseButtons.Left | HMMouseButtons.Forward, X = 10, Y = -5, Wheel = 1, Pan = -1 });
        Check("left+forward, +10, -5, wheel up, pan left is 11 0A00 FBFF 01 FF",
              f.Count == 1 && Hex(f[0]) == "110A00FBFF01FF", string.Join(",", f.Select(Hex)));
        var big = Mouse(m, new HMMouseState { X = 70000 });
        Check("+70000 counts go as 32767, 32767 and 4466",
              big.Count == 3 && Hex(big[0]) == "00FF7F00000000" && Hex(big[1]) == "00FF7F00000000" && Hex(big[2]) == "00721100000000",
              string.Join(",", big.Select(Hex)));
        var neg = Mouse(m, new HMMouseState { Y = -70000, Buttons = HMMouseButtons.Right });
        Check("-70000 counts go as -32767, -32767 and -4466, the button held in each",
              neg.Count == 3 && neg.All(fr => fr[0] == 0x02)
              && BitConverter.ToInt16(neg[0], 3) == -32767 && BitConverter.ToInt16(neg[2], 3) == -4466);

        var nw = Packer(new HidDescriptorBuilder().Mouse(wheel: false, pan: false).Build());
        Check("a wheel or pan value on a mouse without them sends one report and stops",
              Mouse(nw, new HMMouseState { X = 1, Wheel = 5, Pan = -3 }).Count == 1);

        var am = Packer(new HidDescriptorBuilder().Mouse(absolute: true, wheel: false, pan: false).Build());
        Check("an absolute position past the range clamps to 32767",
              BitConverter.ToInt16(Mouse(am, new HMMouseState { X = 40000, Y = 1000 })[0], 1) == 32767);
        Check("and is never split", Mouse(am, new HMMouseState { X = 1_000_000 }).Count == 1);

        var mv = new HidDescriptorBuilder().Mouse(reportId: 1).VendorButtons(3, 2).Build();
        var pv = Packer(mv);
        uint extra = 0; bool sent = false;
        var pvb = new KeyboardMouseBuffers();
        var s1 = new HMMouseState { Buttons = HMMouseButtons.Left | HMMouseButtons.Button6 | HMMouseButtons.Button8 };
        var fr1 = Copy(pv.PackMouse(in s1, ref extra, ref sent, pvb));
        Check("buttons 6 and 8 go to the vendor collection as its buttons 1 and 3",
              fr1.Count == 2 && Hex(fr1[0]) == "0101000000000000" && Hex(fr1[1]) == "0205", string.Join(",", fr1.Select(Hex)));
        var fr2 = Copy(pv.PackMouse(in s1, ref extra, ref sent, pvb));
        Check("an unchanged extra-button state sends no second report", fr2.Count == 1);
        var s3 = new HMMouseState { Buttons = HMMouseButtons.Left };
        var fr3 = Copy(pv.PackMouse(in s3, ref extra, ref sent, pvb));
        Check("releasing them sends it once more, cleared", fr3.Count == 2 && Hex(fr3[1]) == "0200");
        Check("the packer counts 5 + 3 buttons", pv.ButtonCount == 8);

        // One controller packs every frame into the same buffers. Each frame
        // starts clear, and a split movement followed by a short one sends
        // only the short one's report.
        var shared = new KeyboardMouseBuffers();
        string r1 = Hex(kb.PackKeyboard(HMKeyModifiers.LeftShift, new[] { HMKey.A, HMKey.B }, shared));
        string r2 = Hex(kb.PackKeyboard(HMKeyModifiers.None, ReadOnlySpan<HMKey>.Empty, shared));
        string r3 = Hex(kb.PackKeyboard(HMKeyModifiers.None, new[] { HMKey.C }, shared));
        Check("one buffer set packs Shift+A+B, nothing, then C, each from clear",
              r1 == "0200040500000000" && r2 == "0000000000000000" && r3 == "0000060000000000", $"{r1} {r2} {r3}");
        uint e2 = 0; bool s2 = false;
        var long1 = Copy(m.PackMouse(new HMMouseState { X = 70000 }, ref e2, ref s2, shared));
        var short1 = Copy(m.PackMouse(new HMMouseState { X = 1 }, ref e2, ref s2, shared));
        Check("on the same buffers +70000 still goes as three reports, and +1 after it as one",
              long1.Count == 3 && short1.Count == 1 && Hex(short1[0]) == "00010000000000",
              $"{long1.Count}: {string.Join(",", short1.Select(Hex))}");

        var cc = Packer(new HidDescriptorBuilder().ConsumerControl().Build());
        Check("Volume Increment is E9 00", Hex(Cc(cc, new ushort[] { 0x00E9 })) == "E900");
        Check("no usage is 00 00", Hex(Cc(cc, Array.Empty<ushort>())) == "0000");

        // Two catalog devices, worked out from their own descriptors.
        var da = ctx.GetProfile("razer-deathadder-v2")!;
        var dap = da.Inner.GetOrBuildKeyboardMousePacker();
        var daf = Mouse(dap, new HMMouseState { Buttons = HMMouseButtons.Left, X = 300, Y = -2, Wheel = -1 });
        Check("DeathAdder V2: buttons, two vendor bytes, wheel, then 16-bit X and Y",
              daf.Count == 1 && Hex(daf[0]) == "010000FF2C01FEFF", string.Join(",", daf.Select(Hex)));

        // The Xiaomi receiver puts buttons, wheel and pan in report 1 and the
        // 12-bit X and Y in report 2 (Linux selftest test_mouse.py).
        var xm = ctx.GetProfile("xiaomi-mi-wireless-mouse")!.Inner.GetOrBuildKeyboardMousePacker();
        var xf = Mouse(xm, new HMMouseState { Buttons = HMMouseButtons.Left, X = 5, Y = -5, Wheel = 1 });
        Check("Xiaomi: report 1 carries the button and wheel, report 2 X = 5 and Y = -5 in 12 bits",
              xf.Count == 2 && Hex(xf[0]) == "01010100" && Hex(xf[1]) == "0205B0FF", string.Join(",", xf.Select(Hex)));

        var mk = ctx.GetProfile("apple-magic-keyboard-touch-id-numeric")!;
        var mkp = mk.Inner.GetOrBuildKeyboardMousePacker();
        string mkf = Hex(Kb(mkp, HMKeyModifiers.LeftGui, new[] { HMKey.Space }));
        Check("Magic Keyboard: Cmd+Space is report 1: modifiers, reserved, six keys, the extras byte",
              mkf == "0108002C000000000000", mkf);
        Check("its consumer report 0x52 carries Play/Pause on bit 0 and Next Track on bit 3",
              Hex(Cc(mkp, new ushort[] { 0x00CD })) == "5201" && Hex(Cc(mkp, new ushort[] { 0x00B5 })) == "5208");
        Check("its LED report is report 1", mkp.DecodeLeds(1, new byte[] { 0x02 }) == HMKeyboardLeds.CapsLock);
    }

    static void Catalog(HMContext ctx)
    {
        Console.WriteLine("\n== 4. The catalog's keyboards and mice ==");
        var all = ctx.AllProfiles.ToList();
        var kbm = all.Where(p => p.Inner.IsKeyboardOrMouse).ToList();
        Console.WriteLine($"  {all.Count} profiles, {kbm.Count(p => p.Type == "mouse")} mice and {kbm.Count(p => p.Type == "keyboard")} keyboards");
        Check("103 keyboard and mouse profiles", kbm.Count == 103, $"{kbm.Count}");

        int bad = 0;
        foreach (var p in kbm)
        {
            var packer = p.Inner.GetOrBuildKeyboardMousePacker();
            bool isMouse = p.Type == "mouse";
            string? problem = null;
            if (isMouse && !packer.HasMouse) problem = "no mouse collection";
            else if (!isMouse && !packer.HasKeyboard) problem = "no keyboard collection";
            foreach (var r in packer.Map.Reports)
                if (r.Kind == HidReportKind.Input && r.FrameBytes > SharedMemoryIO.EXTENDED_DATA_CAPACITY)
                    problem ??= $"input report {r.ReportId} is {r.FrameBytes} bytes";
            if (problem == null)
            {
                try
                {
                    if (isMouse)
                    {
                        var frames = Mouse(packer, new HMMouseState { Buttons = HMMouseButtons.Left, X = 3, Y = -3, Wheel = 1 });
                        problem = ReadBackMouse(packer, frames);
                    }
                    else
                    {
                        var frame = Kb(packer, HMKeyModifiers.LeftShift, new[] { HMKey.A });
                        problem = ReadBackKeyboard(packer, frame);
                    }
                }
                catch (Exception ex) { problem = ex.Message; }
            }
            if (problem != null) { bad++; Console.WriteLine($"    {p.Id}: {problem}"); }
        }
        Check("each one has its collection, fits the extended lane, and packs what it reads back", bad == 0, $"{bad} failed");

        var deliverAll = all.Where(p => p.Inner.IsKeyboardOrMouse
            || (string.Equals(p.Type, "other", StringComparison.OrdinalIgnoreCase) && p.IsDeployable
                && (p.Inner.GetOrBuildKeyboardMousePacker() is var k && (k.HasMouse || k.HasKeyboard)))).ToList();
        Check("no other catalog profile delivers every report",
              deliverAll.Count == kbm.Count, string.Join(", ", deliverAll.Except(kbm).Select(p => p.Id)));
        Check("no keyboard or mouse id collides with another profile",
              all.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == all.Count);
    }

    static string? ReadBackMouse(KeyboardMousePacker p, List<byte[]> frames)
    {
        bool left = false; int? xv = null;
        foreach (var frame in frames)
        {
            var r = p.Map.Reports.First(x => x.Kind == HidReportKind.Input && x.ReportId == (p.Map.UsesReportIds ? frame[0] : 0));
            var data = r.ReportId != 0 ? frame.AsSpan(1) : frame.AsSpan();
            foreach (var f in r.Fields)
            {
                if (f.Constant || !f.Variable) continue;
                for (int e = 0; e < f.Count; e++)
                {
                    uint u = f.UsageAt(e);
                    if (u == 0x00090001 && HidReportMap.ReadData(data, f, e) == 1) left = true;
                    if (u == 0x00010030) xv ??= HidReportMap.ReadData(data, f, e);
                }
            }
        }
        if (!left) return "button 1 did not read back";
        if (xv is null) return "no X field";
        // A field of 8 bits or more holds 3 exactly. A smaller one clamps.
        return xv == 3 || xv > 0 ? null : $"X read back as {xv}";
    }

    static string? ReadBackKeyboard(KeyboardMousePacker p, byte[] frame)
    {
        var r = p.Map.Reports.First(x => x.Kind == HidReportKind.Input && x.ReportId == (p.Map.UsesReportIds ? frame[0] : 0));
        var data = r.ReportId != 0 ? frame.AsSpan(1) : frame.AsSpan();
        bool shift = false, a = false;
        foreach (var f in r.Fields)
        {
            if (f.Constant) continue;
            for (int e = 0; e < f.Count; e++)
            {
                if (f.Variable)
                {
                    uint u = f.UsageAt(e);
                    int v = HidReportMap.ReadData(data, f, e);
                    if (u == 0x000700E1 && v == 1) shift = true;
                    if (u == 0x00070004 && v == 1) a = true;
                }
                else
                {
                    int v = HidReportMap.ReadData(data, f, e) - f.LogicalMin;
                    if (v >= 0 && f.UsageAt(v) == 0x00070004) a = true;
                }
            }
        }
        if (!shift) return "Left Shift did not read back";
        return a ? null : "A did not read back";
    }

    static int Main()
    {
        Console.WriteLine("=== Keyboards and mice, offline (issue #67) ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        Builder();
        Starters();
        Packers(ctx);
        Catalog(ctx);
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }
}
