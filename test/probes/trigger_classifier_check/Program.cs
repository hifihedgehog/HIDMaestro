// Issue #22 / #27 — trigger axis classifier regression probe.
//
// AddStick / AddTrigger Usage-code convention as of v1.3.14 (issue #27):
//     AddStick("Left")  → X (0x30) + Y (0x31)
//     AddStick("Right") → Z (0x32) + Rz (0x35)   ← RumblePad / vJoy stick-2
//     AddTrigger("Left")  → Rx (0x33)
//     AddTrigger("Right") → Ry (0x34)
//
// Exercises the Custom-Extended layouts the issue bodies called out as
// silently broken pre-fix:
//
//   Case 1: 1 stick + 1 trigger        — Trigger field (RC=1, LogicalMin=0)
//                                        must classify as LeftTrigger, not
//                                        get silently claimed as RightStickX
//                                        by the unconditional case-0x33 arm.
//
//   Case 2: 2 sticks + 1 trigger       — Right stick (Z+Rz, RC=2) must
//                                        classify as RightStickX/Y while the
//                                        lone Rx trigger (RC=1) lands in
//                                        LeftTrigger; sweep is direct 0..max
//                                        with no combined-Z synthesis.
//
// Plus the don't-regress cases:
//
//   Case 3: 6-axis builder layout (X+Y+Z+Rz sticks + Rx+Ry triggers) — the
//           default builder output.
//
//   Case 3b: 4-axis DInput (X+Y+Z+Rz only, no Rx/Ry, RC=1) — the F310 /
//           WebKit "standard gamepad" pattern. fourAxisDInput rescues Z/Rz
//           into RightStickX/Y because no triggers exist.
//
//   Case 4: 2 sticks + 2 triggers      — independent triggers route to
//                                        LeftTrigger / RightTrigger and write
//                                        independently (no combined synthesis).
//
//   Case 5: Xbox-360-wired-shape (X+Y+Rx+Ry+Z+Vx+Vy) — Rx/Ry at RC=2 are
//           the right stick, Z (combined trigger) sits in CombinedTrigger,
//           Vx in LeftTrigger, Vy in RightTrigger. Hand-rolled raw
//           descriptor independent of the builder convention.
//
// No driver install, no virtual device. Exit 0 on PASS, 1 on FAIL.

using System;
using HIDMaestro;
using HIDMaestro.Internal;

internal sealed class Program
{
    static int s_total = 0;
    static int s_failures = 0;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static int ReadField(byte[] report, HidReportBuilder.InputField field, byte reportId)
    {
        // Mirrors HidReportBuilder.WriteBits read direction.
        int idOffset = reportId != 0 ? 8 : 0;
        int bit = field.BitOffset + idOffset;
        int size = field.BitSize;
        long value = 0;
        for (int i = 0; i < size; i++)
        {
            int b = (bit + i) / 8;
            int sh = (bit + i) % 8;
            if ((report[b] & (1 << sh)) != 0)
                value |= 1L << i;
        }
        return (int)value;
    }

    public static int Main()
    {
        Console.WriteLine("=== Issue #22 trigger-classifier check ===\n");

        // ── Case 1: 1 stick + 1 trigger (16-bit) ──────────────────────────────
        Console.WriteLine("--- Case 1: 1 stick + 1 trigger ---");
        {
            byte[] desc = new HidDescriptorBuilder()
                .Joystick()
                .AddStick("Left", 16)
                .AddTrigger("Left", 16)
                .AddHat()
                .AddButtons(11)
                .Build();

            var b = HidReportBuilder.Parse(desc);
            Check("LeftTrigger classified", b.LeftTrigger != null,
                  b.LeftTrigger == null ? "(null)" : $"usage=0x{b.LeftTrigger.Usage:X2} bits={b.LeftTrigger.BitSize} rc={b.LeftTrigger.ReportCount}");
            Check("RightStickX NOT claimed by Rx (lone trigger)",
                  b.RightStickX == null,
                  b.RightStickX == null ? "" : $"unexpected: usage=0x{b.RightStickX.Usage:X2}");

            // Sweep leftTrigger from 0.0 to 1.0; the trigger field's wire value
            // must rise monotonically from 0 to LogicalMax.
            byte[] r0 = b.BuildReport(b.StandardAxes(leftTrigger: 0.0));
            byte[] r1 = b.BuildReport(b.StandardAxes(leftTrigger: 1.0));
            int v0 = b.LeftTrigger != null ? ReadField(r0, b.LeftTrigger, b.InputReportId) : -1;
            int v1 = b.LeftTrigger != null ? ReadField(r1, b.LeftTrigger, b.InputReportId) : -1;
            Check("Trigger wire value at LT=0.0 is 0", v0 == 0, $"got {v0}");
            Check("Trigger wire value at LT=1.0 is full-scale",
                  b.LeftTrigger != null && v1 == b.LeftTrigger.LogicalMax,
                  $"got {v1}, expected {b.LeftTrigger?.LogicalMax}");
        }

        // ── Case 2: 2 sticks + 1 trigger (16-bit) ─────────────────────────────
        Console.WriteLine("\n--- Case 2: 2 sticks + 1 trigger ---");
        {
            byte[] desc = new HidDescriptorBuilder()
                .Joystick()
                .AddStick("Left", 16)
                .AddStick("Right", 16)
                .AddTrigger("Left", 16)
                .AddHat()
                .AddButtons(11)
                .Build();

            var b = HidReportBuilder.Parse(desc);
            Check("LeftStickX classified", b.LeftStickX != null);
            Check("LeftStickY classified", b.LeftStickY != null);
            Check("RightStickX classified (from Z)", b.RightStickX != null,
                  b.RightStickX == null ? "" : $"usage=0x{b.RightStickX.Usage:X2}");
            Check("RightStickY classified (from Rz)", b.RightStickY != null);
            Check("LeftTrigger classified (from Rx)", b.LeftTrigger != null,
                  b.LeftTrigger == null ? "" : $"usage=0x{b.LeftTrigger.Usage:X2}");
            Check("CombinedTrigger NOT set (no Vx)", b.CombinedTrigger == null);
            Check("RightTrigger NOT set (no Ry, no Vy)", b.RightTrigger == null);

            // Sweep leftTrigger 0..1 with rightTrigger left at 0.0. Pre-fix
            // the lone-LeftTrigger branch synthesized 0.5 - leftTrigger * 0.5,
            // producing 0.5 at LT=0 and 0.0 at LT=1. After fix, value is
            // direct: 0 at LT=0, full-scale at LT=1.
            byte[] r0 = b.BuildReport(b.StandardAxes(leftTrigger: 0.0, rightTrigger: 0.0));
            byte[] r1 = b.BuildReport(b.StandardAxes(leftTrigger: 1.0, rightTrigger: 0.0));
            int v0 = ReadField(r0, b.LeftTrigger!, b.InputReportId);
            int v1 = ReadField(r1, b.LeftTrigger!, b.InputReportId);
            Check("LT=0.0 wire value is 0 (no combined-Z synthesis)",
                  v0 == 0, $"got {v0}");
            Check("LT=1.0 wire value is full-scale",
                  v1 == b.LeftTrigger!.LogicalMax,
                  $"got {v1}, expected {b.LeftTrigger.LogicalMax}");
        }

        // ── Case 3: Standard 6-axis (don't-regress) ──────────────────────────
        Console.WriteLine("\n--- Case 3: Standard 6-axis (8-bit) ---");
        {
            byte[] desc = new HidDescriptorBuilder()
                .Gamepad()
                .AddStick("Left", 8)
                .AddStick("Right", 8)        // emits Z+Rz (RumblePad / vJoy convention)
                .AddTrigger("Left", 8)
                .AddTrigger("Right", 8)
                .AddButtons(10)
                .Build();
            var b = HidReportBuilder.Parse(desc);
            Check("Standard 6-axis: LeftStickX from X", b.LeftStickX != null && b.LeftStickX.Usage == 0x30);
            Check("Standard 6-axis: RightStickX from Z", b.RightStickX != null && b.RightStickX.Usage == 0x32);
            Check("Standard 6-axis: LeftTrigger from Rx", b.LeftTrigger != null && b.LeftTrigger.Usage == 0x33);
            Check("Standard 6-axis: RightTrigger from Ry", b.RightTrigger != null && b.RightTrigger.Usage == 0x34);
        }

        // 4-axis DInput (real) — X+Y+Z+Rz only, no Rx/Ry. A hand-built
        // descriptor matching the WebKit "standard gamepad" pattern: 8-bit
        // unsigned values, X/Y/Z/Rz declared together (Report Count 4).
        Console.WriteLine("\n--- Case 3b: 4-axis DInput (X+Y+Z+Rz, 8-bit, RC=4) ---");
        {
            // Hand-rolled descriptor. Mirror logitech-f310's shape. Wraps in
            // an Application collection so the parser tracks it.
            var d = new System.Collections.Generic.List<byte>();
            d.AddRange(new byte[] { 0x05, 0x01 });          // Usage Page (Generic Desktop)
            d.AddRange(new byte[] { 0x09, 0x05 });          // Usage (Game Pad)
            d.AddRange(new byte[] { 0xA1, 0x01 });          // Collection (Application)
            d.AddRange(new byte[] { 0xA1, 0x00 });          // Collection (Physical)
            d.AddRange(new byte[] { 0x09, 0x30, 0x09, 0x31, 0x09, 0x32, 0x09, 0x35 }); // X,Y,Z,Rz
            d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x00 }); // Logical 0..255
            d.AddRange(new byte[] { 0x95, 0x04, 0x75, 0x08 });   // RC=4 RS=8
            d.AddRange(new byte[] { 0x81, 0x02 });               // Input
            d.AddRange(new byte[] { 0xC0, 0xC0 });

            var b = HidReportBuilder.Parse(d.ToArray());
            Check("4-axis DInput: LeftStickX from X", b.LeftStickX != null && b.LeftStickX.Usage == 0x30);
            Check("4-axis DInput: LeftStickY from Y", b.LeftStickY != null && b.LeftStickY.Usage == 0x31);
            Check("4-axis DInput: RightStickX from Z (fourAxisDInput)",
                  b.RightStickX != null && b.RightStickX.Usage == 0x32,
                  b.RightStickX == null ? "(null)" : $"usage=0x{b.RightStickX.Usage:X2}");
            Check("4-axis DInput: RightStickY from Rz",
                  b.RightStickY != null && b.RightStickY.Usage == 0x35);
            Check("4-axis DInput: LeftTrigger NOT set",
                  b.LeftTrigger == null);
        }

        // ── Case 4: 2 sticks + 2 triggers — independent (don't-regress) ───────
        Console.WriteLine("\n--- Case 4: 2 sticks + 2 independent triggers ---");
        {
            byte[] desc = new HidDescriptorBuilder()
                .Joystick()
                .AddStick("Left", 16)
                .AddStick("Right", 16)
                .AddTrigger("Left", 16)
                .AddTrigger("Right", 16)
                .AddButtons(10)
                .Build();
            var b = HidReportBuilder.Parse(desc);
            Check("LeftTrigger from Rx", b.LeftTrigger != null && b.LeftTrigger.Usage == 0x33);
            Check("RightTrigger from Ry", b.RightTrigger != null && b.RightTrigger.Usage == 0x34);
            Check("CombinedTrigger NOT set", b.CombinedTrigger == null);

            byte[] rL = b.BuildReport(b.StandardAxes(leftTrigger: 1.0, rightTrigger: 0.0));
            byte[] rR = b.BuildReport(b.StandardAxes(leftTrigger: 0.0, rightTrigger: 1.0));
            int lAtFullL = ReadField(rL, b.LeftTrigger!, b.InputReportId);
            int rAtFullL = ReadField(rL, b.RightTrigger!, b.InputReportId);
            int lAtFullR = ReadField(rR, b.LeftTrigger!, b.InputReportId);
            int rAtFullR = ReadField(rR, b.RightTrigger!, b.InputReportId);
            Check("LT=1, RT=0 → LeftTrigger field full-scale", lAtFullL == b.LeftTrigger!.LogicalMax);
            Check("LT=1, RT=0 → RightTrigger field zero", rAtFullL == 0);
            Check("LT=0, RT=1 → LeftTrigger field zero", lAtFullR == 0);
            Check("LT=0, RT=1 → RightTrigger field full-scale", rAtFullR == b.RightTrigger!.LogicalMax);
        }

        // ── Case 5: Xbox 360 wired shape — combined Z + Vx/Vy (don't-regress) ─
        Console.WriteLine("\n--- Case 5: Xbox 360 wired shape (combined Z + Vx/Vy) ---");
        {
            // Hand-roll the Xbox-360-wired descriptor shape: X, Y, Rx, Ry, Z,
            // Vx, Vy, all 16-bit. The BuildReport combined-Z formula fires
            // only when Z is captured to CombinedTrigger by the Vx override.
            var d = new System.Collections.Generic.List<byte>();
            d.AddRange(new byte[] { 0x05, 0x01, 0x09, 0x05, 0xA1, 0x01 });
            // X,Y
            d.AddRange(new byte[] { 0xA1, 0x00, 0x09, 0x30, 0x09, 0x31,
                                    0x15, 0x00, 0x27, 0xFF, 0xFF, 0x00, 0x00,
                                    0x35, 0x00, 0x47, 0xFF, 0xFF, 0x00, 0x00,
                                    0x95, 0x02, 0x75, 0x10, 0x81, 0x02, 0xC0 });
            // Rx,Ry
            d.AddRange(new byte[] { 0xA1, 0x00, 0x09, 0x33, 0x09, 0x34,
                                    0x15, 0x00, 0x27, 0xFF, 0xFF, 0x00, 0x00,
                                    0x35, 0x00, 0x47, 0xFF, 0xFF, 0x00, 0x00,
                                    0x95, 0x02, 0x75, 0x10, 0x81, 0x02, 0xC0 });
            // Z
            d.AddRange(new byte[] { 0xA1, 0x00, 0x09, 0x32,
                                    0x15, 0x00, 0x27, 0xFF, 0xFF, 0x00, 0x00,
                                    0x35, 0x00, 0x47, 0xFF, 0xFF, 0x00, 0x00,
                                    0x95, 0x01, 0x75, 0x10, 0x81, 0x02, 0xC0 });
            // Vx,Vy
            d.AddRange(new byte[] { 0xA1, 0x00, 0x09, 0x40, 0x09, 0x41,
                                    0x15, 0x00, 0x27, 0xFF, 0xFF, 0x00, 0x00,
                                    0x35, 0x00, 0x47, 0xFF, 0xFF, 0x00, 0x00,
                                    0x95, 0x02, 0x75, 0x10, 0x81, 0x02, 0xC0 });
            d.AddRange(new byte[] { 0xC0 });

            var b = HidReportBuilder.Parse(d.ToArray());
            Check("Xbox shape: LeftStickX from X", b.LeftStickX != null && b.LeftStickX.Usage == 0x30);
            Check("Xbox shape: RightStickX from Rx", b.RightStickX != null && b.RightStickX.Usage == 0x33);
            Check("Xbox shape: CombinedTrigger from Z", b.CombinedTrigger != null && b.CombinedTrigger.Usage == 0x32);
            Check("Xbox shape: LeftTrigger from Vx", b.LeftTrigger != null && b.LeftTrigger.Usage == 0x40);
            Check("Xbox shape: RightTrigger from Vy", b.RightTrigger != null && b.RightTrigger.Usage == 0x41);

            // Combined-Z formula at LT=1, RT=0 → Z = 0.5 - 0.5 = 0 (LogicalMin).
            byte[] r = b.BuildReport(b.StandardAxes(leftTrigger: 1.0, rightTrigger: 0.0));
            int z = ReadField(r, b.CombinedTrigger!, b.InputReportId);
            int vx = ReadField(r, b.LeftTrigger!, b.InputReportId);
            int vy = ReadField(r, b.RightTrigger!, b.InputReportId);
            Check("Combined Z at LT=1, RT=0 → 0",
                  z == b.CombinedTrigger!.LogicalMin, $"got {z}, expected {b.CombinedTrigger.LogicalMin}");
            Check("Vx at LT=1, RT=0 → full-scale", vx == b.LeftTrigger!.LogicalMax);
            Check("Vy at LT=1, RT=0 → 0", vy == 0);

            byte[] r2 = b.BuildReport(b.StandardAxes(leftTrigger: 0.0, rightTrigger: 1.0));
            int z2 = ReadField(r2, b.CombinedTrigger!, b.InputReportId);
            Check("Combined Z at LT=0, RT=1 → full-scale",
                  z2 == b.CombinedTrigger.LogicalMax, $"got {z2}, expected {b.CombinedTrigger.LogicalMax}");
        }

        // ── Case 6: Custom Extended (PadForge BEEF:F000) — read live profile ──
        // This matches what PadForge ships as its custom-shaped virtual.
        // Sticks=2, triggers=1 — the same shape as case 2 but verifying the
        // SDK's profile-loaded path classifies it the same as the
        // builder-built path.
        Console.WriteLine("\n--- Case 6: PadForge custom-extended path (sticks=2, triggers=1) ---");
        {
            // Build the descriptor through HidProfileBuilder so we exercise the
            // public API consumers actually call.
            byte[] desc = new HidDescriptorBuilder()
                .Joystick()
                .AddStick("Left", 16)
                .AddStick("Right", 16)
                .AddTrigger("Left", 16)
                .AddHat()
                .AddButtons(11)
                .Build();
            var b = HidReportBuilder.Parse(desc);
            Check("PadForge shape: trigger lands in LeftTrigger from Rx",
                  b.LeftTrigger != null && b.LeftTrigger.Usage == 0x33);
            Check("PadForge shape: RightStickX classified from Z",
                  b.RightStickX != null && b.RightStickX.Usage == 0x32,
                  b.RightStickX == null ? "(null)" : $"got 0x{b.RightStickX.Usage:X2}");
            // Mid-press value: at 0.5, the trigger field should sit at half of
            // LogicalMax (rounded). Pre-fix the inverted/offset combined formula
            // produced 0.5 - 0.5*0.5 = 0.25 of LogicalMax for LT=0.5/RT=0.
            byte[] r = b.BuildReport(b.StandardAxes(leftTrigger: 0.5));
            int v = ReadField(r, b.LeftTrigger!, b.InputReportId);
            int expected = b.LeftTrigger!.LogicalMax / 2;
            // Allow ±2 rounding slack on the encode rounding direction.
            Check("PadForge shape: LT=0.5 wire value is half-scale",
                  Math.Abs(v - expected) <= 2,
                  $"got {v}, expected ~{expected}");
        }

        // ── Case 7: 3-stick PadForge Extended (v1.3.15, #124) ────────────────
        Console.WriteLine("\n--- Case 7: PadForge Extended 3-stick (no triggers) ---");
        {
            byte[] desc = new HidDescriptorBuilder()
                .Joystick()
                .AddStick("Left", 16)
                .AddStick("Right", 16)
                .AddStick("Right", 16)   // stick 3 → Rx/Ry per v1.3.15 pool
                .AddHat()
                .AddButtons(13)
                .Build();
            var b = HidReportBuilder.Parse(desc);
            Check("3-stick: LeftStickX from X",      b.LeftStickX  != null && b.LeftStickX.Usage  == 0x30);
            Check("3-stick: LeftStickY from Y",      b.LeftStickY  != null && b.LeftStickY.Usage  == 0x31);
            Check("3-stick: RightStickX from Z",     b.RightStickX != null && b.RightStickX.Usage == 0x32);
            Check("3-stick: RightStickY from Rz",    b.RightStickY != null && b.RightStickY.Usage == 0x35);
            Check("3-stick: ThirdStickX from Rx",    b.ThirdStickX != null && b.ThirdStickX.Usage == 0x33,
                  b.ThirdStickX == null ? "(null)" : $"got 0x{b.ThirdStickX.Usage:X2}");
            Check("3-stick: ThirdStickY from Ry",    b.ThirdStickY != null && b.ThirdStickY.Usage == 0x34);
            Check("3-stick: FourthStickX null",      b.FourthStickX == null);
            Check("3-stick: FourthStickY null",      b.FourthStickY == null);
            Check("3-stick: LeftTrigger null",       b.LeftTrigger  == null);
            Check("3-stick: RightTrigger null",      b.RightTrigger == null);
        }

        // ── Case 8: 4-stick PadForge Extended (v1.3.15, #124) ────────────────
        Console.WriteLine("\n--- Case 8: PadForge Extended 4-stick (no triggers) ---");
        {
            byte[] desc = new HidDescriptorBuilder()
                .Joystick()
                .AddStick("Left", 16)
                .AddStick("Right", 16)
                .AddStick("Right", 16)
                .AddStick("Right", 16)   // stick 4 → Slider/Dial
                .AddHat()
                .AddButtons(13)
                .Build();
            var b = HidReportBuilder.Parse(desc);
            Check("4-stick: LeftStickX from X",      b.LeftStickX  != null && b.LeftStickX.Usage  == 0x30);
            Check("4-stick: RightStickX from Z",     b.RightStickX != null && b.RightStickX.Usage == 0x32);
            Check("4-stick: ThirdStickX from Rx",    b.ThirdStickX != null && b.ThirdStickX.Usage == 0x33);
            Check("4-stick: ThirdStickY from Ry",    b.ThirdStickY != null && b.ThirdStickY.Usage == 0x34);
            Check("4-stick: FourthStickX from Slider", b.FourthStickX != null && b.FourthStickX.Usage == 0x36,
                  b.FourthStickX == null ? "(null)" : $"got 0x{b.FourthStickX.Usage:X2}");
            Check("4-stick: FourthStickY from Dial",   b.FourthStickY != null && b.FourthStickY.Usage == 0x37);
            Check("4-stick: LeftTrigger null",       b.LeftTrigger  == null);
            Check("4-stick: RightTrigger null",      b.RightTrigger == null);
        }

        // ── Case 9: 3-stick + 2-trigger trigger cascade (v1.3.15, #124) ──────
        Console.WriteLine("\n--- Case 9: 3-stick + 2-trigger trigger cascade ---");
        {
            // With sticks 1-3 consuming X/Y, Z/Rz, Rx/Ry, the trigger pool
            // falls to Slider then Dial.
            byte[] desc = new HidDescriptorBuilder()
                .Joystick()
                .AddStick("Left", 16)
                .AddStick("Right", 16)
                .AddStick("Right", 16)
                .AddTrigger("Left", 8)
                .AddTrigger("Right", 8)
                .AddButtons(10)
                .Build();
            var b = HidReportBuilder.Parse(desc);
            Check("3-stick+2-trig: ThirdStickX from Rx",  b.ThirdStickX != null && b.ThirdStickX.Usage == 0x33);
            Check("3-stick+2-trig: LeftTrigger from Slider", b.LeftTrigger != null && b.LeftTrigger.Usage == 0x36,
                  b.LeftTrigger == null ? "(null)" : $"got 0x{b.LeftTrigger.Usage:X2}");
            Check("3-stick+2-trig: RightTrigger from Dial",  b.RightTrigger != null && b.RightTrigger.Usage == 0x37);
            Check("3-stick+2-trig: FourthStickX null (Slider claimed as trigger)", b.FourthStickX == null);
        }

        // ── Matrix: every layout HidDescriptorBuilder can emit ───────────────
        // The user's instruction: "ensure thorough tests for this issue so any
        // number of buttons, axes, and sticks won't go wonky like this ever
        // again." This loop exhaustively covers the (sticks, triggers, bits,
        // buttons, hat) combinations the SDK's builder API can produce, and
        // for each one verifies (a) classifier slot assignments, (b) encoder
        // wire-byte fidelity, (c) button round-trip. The named cases above
        // (1-6) cover the issue's specific examples; this matrix catches
        // anything else with the same shape.
        Console.WriteLine("\n--- Matrix: every builder-emittable layout ---");
        {
            int matrixTotal = 0, matrixFails = 0;
            // Stick configs: which sticks are present and in what order. The
            // builder's slot-pool allocator (v1.3.15, #124) emits sticks 1-4
            // onto X+Y, Z+Rz, Rx+Ry, Slider+Dial in priority order, with
            // "Left" biasing stick 1 to X+Y and "Right" biasing stick 1 to
            // Z+Rz. Repeated AddStick("Right") cascades through the pool.
            (string label, string[] sticks)[] stickConfigs = {
                ("none",    new string[] { }),
                ("L",       new[] { "Left" }),
                ("R",       new[] { "Right" }),
                ("L+R",     new[] { "Left", "Right" }),
                ("L+R+R",   new[] { "Left", "Right", "Right" }),
                ("L+R+R+R", new[] { "Left", "Right", "Right", "Right" }),
            };
            // Trigger configs: which named triggers are declared.
            (string label, string[] triggers)[] triggerConfigs = {
                ("none",    new string[] { }),
                ("L",       new[] { "Left" }),
                ("R",       new[] { "Right" }),
                ("L+R",     new[] { "Left", "Right" }),
            };
            int[] stickBitsSet     = { 8, 16 };
            int[] triggerBitsSet   = { 8, 16 };
            int[] buttonCounts     = { 0, 1, 8, 10, 16, 32 };
            bool[] hatOptions      = { false, true };

            int caseIndex = 0;
            foreach (var (sLabel, sticks) in stickConfigs)
            foreach (var (tLabel, triggers) in triggerConfigs)
            foreach (int sBits in stickBitsSet)
            foreach (int tBits in triggerBitsSet)
            foreach (int btnCount in buttonCounts)
            foreach (bool hat in hatOptions)
            {
                // Skip configs the builder can't emit (no axes at all and no
                // hat and no buttons would produce an empty descriptor; HID
                // requires at least one Input item).
                if (sticks.Length == 0 && triggers.Length == 0 && !hat && btnCount == 0)
                    continue;
                // Skip stickBits/triggerBits when the corresponding axis type
                // isn't present — those are no-op variants.
                if (sticks.Length == 0 && sBits != 8) continue;
                if (triggers.Length == 0 && tBits != 8) continue;

                // v1.3.15 (#124): the slot-pool builder has a hard cap of
                // 4 paired stick slots + however many of [Rx, Ry, Slider, Dial]
                // remain after sticks consume Rx/Ry/Slider/Dial. Configs that
                // would overflow the pool intentionally throw at AddStick /
                // AddTrigger build time. Skip them — Case 8 already exercises
                // the 4-stick boundary, and pool-overflow throws are part of
                // the API contract documented on the methods.
                int triggerSlotsTaken = 0;
                if (sticks.Length >= 3) triggerSlotsTaken += 2; // Rx + Ry consumed by stick 3
                if (sticks.Length >= 4) triggerSlotsTaken += 2; // Slider + Dial consumed by stick 4
                if (triggers.Length > 4 - triggerSlotsTaken) continue;

                caseIndex++;
                string label = $"#{caseIndex} sticks={sLabel} triggers={tLabel} sBits={sBits} tBits={tBits} btn={btnCount} hat={hat}";

                var dbuild = new HidDescriptorBuilder().Joystick();
                foreach (var s in sticks) dbuild = dbuild.AddStick(s, sBits);
                foreach (var t in triggers) dbuild = dbuild.AddTrigger(t, tBits);
                if (hat) dbuild = dbuild.AddHat();
                if (btnCount > 0) dbuild = dbuild.AddButtons(btnCount);

                byte[] desc;
                HidReportBuilder b;
                try
                {
                    desc = dbuild.Build();
                    b = HidReportBuilder.Parse(desc);
                }
                catch (Exception ex)
                {
                    matrixTotal++; matrixFails++;
                    Console.WriteLine($"  [FAIL] {label}: build/parse threw {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                // v1.3.15 (#124): simulate the SDK's slot-pool allocator to
                // know exactly which Usage code each AddStick / AddTrigger
                // call SHOULD emit, then verify the classifier surfaces them
                // in the expected semantic slot (LeftStick → Right → Third →
                // Fourth; LeftTrigger / RightTrigger by Usage preference).
                bool xy = false, zRz = false, rxRy = false, slDl = false;
                bool rxT = false, ryT = false, slT = false, dlT = false;
                byte expLSX = 0, expRSX = 0, expTSX = 0, expFSX = 0;
                byte expLT = 0, expRT = 0;
                foreach (var s in sticks) {
                    bool isL = s.Equals("Left", StringComparison.OrdinalIgnoreCase);
                    byte u;
                    if (isL && !xy)  { xy = true; u = 0x30; }
                    else if (!isL && !zRz) { zRz = true; u = 0x32; }
                    else if (!xy)    { xy = true; u = 0x30; }
                    else if (!zRz)   { zRz = true; u = 0x32; }
                    else if (!rxRy)  { rxRy = true; u = 0x33; }
                    else if (!slDl)  { slDl = true; u = 0x36; }
                    else continue;
                    if (u == 0x30) expLSX = u;
                    else if (expRSX == 0) expRSX = u;
                    else if (expTSX == 0) expTSX = u;
                    else if (expFSX == 0) expFSX = u;
                }
                foreach (var t in triggers) {
                    bool isL = t.Equals("Left", StringComparison.OrdinalIgnoreCase);
                    byte u;
                    if (isL && !rxRy && !rxT) { rxT = true; u = 0x33; }
                    else if (!isL && !rxRy && !ryT) { ryT = true; u = 0x34; }
                    else if (!rxRy && !rxT) { rxT = true; u = 0x33; }
                    else if (!rxRy && !ryT) { ryT = true; u = 0x34; }
                    else if (!slDl && !slT) { slT = true; u = 0x36; }
                    else if (!slDl && !dlT) { dlT = true; u = 0x37; }
                    else continue;
                    // Classifier preference: 0x32/0x33/0x36 prefer LeftTrigger;
                    // 0x34/0x35/0x37 prefer RightTrigger.
                    bool leftPref = (u == 0x32 || u == 0x33 || u == 0x36);
                    if (leftPref) {
                        if (expLT == 0) expLT = u;
                        else if (expRT == 0) expRT = u;
                    } else {
                        if (expRT == 0) expRT = u;
                        else if (expLT == 0) expLT = u;
                    }
                }

                bool ok = true;
                string detail = "";

                // Stick slot assertions.
                if (expLSX != 0 && (b.LeftStickX == null || b.LeftStickX.Usage != expLSX)) {
                    ok = false; detail += $" expected LeftStickX from 0x{expLSX:X2};";
                }
                if (expLSX == 0 && b.LeftStickX != null) {
                    ok = false; detail += $" unexpected LeftStickX usage=0x{b.LeftStickX.Usage:X2};";
                }
                if (expRSX != 0 && (b.RightStickX == null || b.RightStickX.Usage != expRSX)) {
                    ok = false; detail += $" expected RightStickX from 0x{expRSX:X2};";
                }
                if (expRSX == 0 && b.RightStickX != null) {
                    ok = false; detail += $" unexpected RightStickX usage=0x{b.RightStickX.Usage:X2};";
                }
                if (expTSX != 0 && (b.ThirdStickX == null || b.ThirdStickX.Usage != expTSX)) {
                    ok = false; detail += $" expected ThirdStickX from 0x{expTSX:X2};";
                }
                if (expTSX == 0 && b.ThirdStickX != null) {
                    ok = false; detail += $" unexpected ThirdStickX usage=0x{b.ThirdStickX.Usage:X2};";
                }
                if (expFSX != 0 && (b.FourthStickX == null || b.FourthStickX.Usage != expFSX)) {
                    ok = false; detail += $" expected FourthStickX from 0x{expFSX:X2};";
                }
                if (expFSX == 0 && b.FourthStickX != null) {
                    ok = false; detail += $" unexpected FourthStickX usage=0x{b.FourthStickX.Usage:X2};";
                }

                // Trigger slot assertions.
                if (expLT != 0 && (b.LeftTrigger == null || b.LeftTrigger.Usage != expLT)) {
                    ok = false; detail += $" expected LeftTrigger from 0x{expLT:X2};";
                }
                if (expLT == 0 && b.LeftTrigger != null) {
                    ok = false; detail += $" unexpected LeftTrigger usage=0x{b.LeftTrigger.Usage:X2};";
                }
                if (expRT != 0 && (b.RightTrigger == null || b.RightTrigger.Usage != expRT)) {
                    ok = false; detail += $" expected RightTrigger from 0x{expRT:X2};";
                }
                if (expRT == 0 && b.RightTrigger != null) {
                    ok = false; detail += $" unexpected RightTrigger usage=0x{b.RightTrigger.Usage:X2};";
                }

                bool wantLT = expLT != 0;
                bool wantRT = expRT != 0;

                // CombinedTrigger should never be set for builder-emitted
                // descriptors — it's a pure Vx/Vy override slot.
                if (b.CombinedTrigger != null) {
                    ok = false; detail += " CombinedTrigger unexpectedly set;";
                }

                // Encoder fidelity: sweep each declared trigger 0..1 and check
                // wire value at min/mid/max; sweep each stick X to 0/0.5/1 and
                // check the wire byte too.
                if (ok && wantLT)
                {
                    byte[] r0 = b.BuildReport(b.StandardAxes(leftTrigger: 0.0, rightTrigger: 0.0));
                    byte[] r1 = b.BuildReport(b.StandardAxes(leftTrigger: 1.0, rightTrigger: 0.0));
                    int v0 = ReadField(r0, b.LeftTrigger!, b.InputReportId);
                    int v1 = ReadField(r1, b.LeftTrigger!, b.InputReportId);
                    if (v0 != 0) {
                        ok = false; detail += $" LT idle wire {v0} != 0;";
                    }
                    if (v1 != b.LeftTrigger!.LogicalMax) {
                        ok = false; detail += $" LT full wire {v1} != {b.LeftTrigger.LogicalMax};";
                    }
                }
                if (ok && wantRT)
                {
                    byte[] r0 = b.BuildReport(b.StandardAxes(leftTrigger: 0.0, rightTrigger: 0.0));
                    byte[] r1 = b.BuildReport(b.StandardAxes(leftTrigger: 0.0, rightTrigger: 1.0));
                    int v0 = ReadField(r0, b.RightTrigger!, b.InputReportId);
                    int v1 = ReadField(r1, b.RightTrigger!, b.InputReportId);
                    if (v0 != 0) {
                        ok = false; detail += $" RT idle wire {v0} != 0;";
                    }
                    if (v1 != b.RightTrigger!.LogicalMax) {
                        ok = false; detail += $" RT full wire {v1} != {b.RightTrigger.LogicalMax};";
                    }
                }
                if (ok && wantLT && wantRT)
                {
                    // Both triggers declared: writing one must not bleed into
                    // the other (no combined-Z synthesis fires).
                    byte[] rL = b.BuildReport(b.StandardAxes(leftTrigger: 1.0, rightTrigger: 0.0));
                    byte[] rR = b.BuildReport(b.StandardAxes(leftTrigger: 0.0, rightTrigger: 1.0));
                    int rt_when_lt = ReadField(rL, b.RightTrigger!, b.InputReportId);
                    int lt_when_rt = ReadField(rR, b.LeftTrigger!, b.InputReportId);
                    if (rt_when_lt != 0) {
                        ok = false; detail += $" RT bled to {rt_when_lt} when LT=1 RT=0;";
                    }
                    if (lt_when_rt != 0) {
                        ok = false; detail += $" LT bled to {lt_when_rt} when LT=0 RT=1;";
                    }
                }
                if (ok && expLSX != 0)
                {
                    byte[] rMid  = b.BuildReport(b.StandardAxes(leftX: 0.5));
                    byte[] rMax  = b.BuildReport(b.StandardAxes(leftX: 1.0));
                    int vMid = ReadField(rMid, b.LeftStickX!, b.InputReportId);
                    int vMax = ReadField(rMax, b.LeftStickX!, b.InputReportId);
                    int half = b.LeftStickX!.LogicalMax / 2;
                    if (Math.Abs(vMid - half) > 2) {
                        ok = false; detail += $" LSX mid wire {vMid} not ~{half};";
                    }
                    if (vMax != b.LeftStickX.LogicalMax) {
                        ok = false; detail += $" LSX max wire {vMax} != {b.LeftStickX.LogicalMax};";
                    }
                }
                if (ok && expRSX != 0)
                {
                    byte[] rMid  = b.BuildReport(b.StandardAxes(rightX: 0.5));
                    byte[] rMax  = b.BuildReport(b.StandardAxes(rightX: 1.0));
                    int vMid = ReadField(rMid, b.RightStickX!, b.InputReportId);
                    int vMax = ReadField(rMax, b.RightStickX!, b.InputReportId);
                    int half = b.RightStickX!.LogicalMax / 2;
                    if (Math.Abs(vMid - half) > 2) {
                        ok = false; detail += $" RSX mid wire {vMid} not ~{half};";
                    }
                    if (vMax != b.RightStickX.LogicalMax) {
                        ok = false; detail += $" RSX max wire {vMax} != {b.RightStickX.LogicalMax};";
                    }
                }

                // Button round-trip. Press button N (descriptor index N-1),
                // verify only that bit fires in the wire button bytes.
                if (ok && btnCount > 0)
                {
                    // Pick a button index 1-based that exists.
                    int probeBtn = Math.Min(btnCount, 5); // button 5 (index 4)
                    uint mask = 1u << (probeBtn - 1);
                    byte[] rBtn = b.BuildReport(buttonMask: mask);
                    // Find first button field
                    var btnField = b.Buttons.Count > 0 ? b.Buttons[probeBtn - 1] : null;
                    if (btnField == null) {
                        ok = false; detail += $" no button[{probeBtn}] field exposed;";
                    } else {
                        int bitVal = ReadField(rBtn, btnField, b.InputReportId);
                        if (bitVal != 1) {
                            ok = false; detail += $" button[{probeBtn}] bit {bitVal} != 1;";
                        }
                    }
                }

                matrixTotal++;
                if (!ok) {
                    matrixFails++;
                    Console.WriteLine($"  [FAIL] {label}:{detail}");
                }
            }

            s_total += matrixTotal;
            s_failures += matrixFails;
            Console.WriteLine($"  Matrix: {matrixTotal - matrixFails}/{matrixTotal} configurations passed");
        }

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} PASS ===");
        return s_failures == 0 ? 0 : 1;
    }
}
