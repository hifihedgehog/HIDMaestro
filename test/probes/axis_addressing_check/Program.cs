// Arbitrary-axis addressing — HMAxis / ExtraAxes / AvailableAxes / AddAxis (v1.3.8).
//
// Pre-v1.3.8 the SDK exposed exactly 4 sticks + 2 triggers via HMGamepadState.
// Anything beyond that — flight-stick throttle slider, separate brake/throttle/
// clutch pedals on a wheel, HOTAS rudder pedal — couldn't be addressed by the
// consumer, even when the descriptor declared the field. v1.3.8 adds:
//
//   • HMAxis enum               — every recognized HID Generic Desktop / Simulation
//                                 Controls analog usage as a stable (page<<8|usage)
//                                 value
//   • HMGamepadState.ExtraAxes  — opt-in Dictionary<HMAxis,float> drive-by-usage
//   • HMProfile.AvailableAxes   — discovery: list every HMAxis the descriptor
//                                 declares
//   • HidDescriptorBuilder.AddAxis — emit any HMAxis-keyed analog input field
//
// This probe asserts:
//
//   1. Discovery: well-known catalog profiles surface their full set of axes
//      (SideWinder = X/Y/Rz/Slider; G29 = X/Z/Rz/Y; DualSense = full GD set).
//
//   2. Wire-byte fidelity: ExtraAxes writes land at the descriptor's bit
//      offset / size, scaled into the field's logical range.
//
//   3. Builder round-trip: a HOTAS-shape descriptor declared via AddAxis
//      parses back with every requested HMAxis present, and ExtraAxes-driven
//      values reach the right wire bytes.
//
//   4. Override semantics: when an axis is reachable via BOTH a semantic slot
//      (state.LeftTrigger) AND ExtraAxes, the explicit ExtraAxes write wins —
//      "explicit beats implicit" is the documented contract.
//
// No driver install, no virtual device. Exit 0 on PASS, 1 on FAIL.

using System;
using System.Collections.Generic;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Program
{
    static int s_total = 0;
    static int s_failures = 0;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static int Main()
    {
        Console.WriteLine("=== Arbitrary-axis addressing (HMAxis / ExtraAxes / AvailableAxes / AddAxis) ===");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        // ── Phase 1: Discovery on canonical catalog profiles ──────────────
        Console.WriteLine("\n-- Discovery --");

        AssertProfileAxes(ctx, "sidewinder-force-feedback-2",
            HMAxis.X, HMAxis.Y, HMAxis.Rz, HMAxis.Slider);

        AssertProfileAxes(ctx, "logitech-g29",
            HMAxis.X, HMAxis.Z, HMAxis.Rz, HMAxis.Y);

        AssertProfileAxes(ctx, "dualsense",
            HMAxis.X, HMAxis.Y, HMAxis.Z, HMAxis.Rz, HMAxis.Rx, HMAxis.Ry);

        AssertProfileAxes(ctx, "xbox-360-wired",
            HMAxis.X, HMAxis.Y, HMAxis.Rx, HMAxis.Ry, HMAxis.Z, HMAxis.Vx, HMAxis.Vy);

        // ── Phase 2: Wire-byte fidelity via ExtraAxes ─────────────────────
        Console.WriteLine("\n-- Wire-byte fidelity --");

        // SideWinder Slider: bit 40 (byte 6 with the RID prefix at byte 0),
        // 7 bits, range [0..127].
        WireCheck(ctx, "sidewinder-force-feedback-2", HMAxis.Slider, 1.0f,  6, 0x7F);
        WireCheck(ctx, "sidewinder-force-feedback-2", HMAxis.Slider, 0.5f,  6, 0x3F);
        WireCheck(ctx, "sidewinder-force-feedback-2", HMAxis.Slider, 0.0f,  6, 0x00);

        // G29 has report ID = 0x00 (no RID prefix). 8-bit pedals at bits 48/56/64.
        WireCheck(ctx, "logitech-g29", HMAxis.Z,  1.0f, 6, 0xFF);
        WireCheck(ctx, "logitech-g29", HMAxis.Rz, 1.0f, 7, 0xFF);
        WireCheck(ctx, "logitech-g29", HMAxis.Y,  1.0f, 8, 0xFF);

        // ── Phase 3: Builder round-trip (HOTAS shape via AddAxis) ─────────
        Console.WriteLine("\n-- Builder round-trip --");

        var desc = new HidDescriptorBuilder()
            .Joystick()
            .AddStick("Left", bits: 16)
            .AddAxis(HMAxis.Slider,   bits: 8)
            .AddAxis(HMAxis.Rudder,   bits: 8)
            .AddAxis(HMAxis.Throttle, bits: 8)
            .AddAxis(HMAxis.Brake,    bits: 8)
            .AddAxis(HMAxis.Clutch,   bits: 8)
            .AddButtons(8)
            .AddHat()
            .Build();

        var rb = HidReportBuilder.Parse(desc);

        Check("Builder emits parseable descriptor", rb.InputFields.Count > 0,
              $"{desc.Length} bytes, {rb.InputFields.Count} fields");

        Check("AxisFields contains X (from AddStick)",   rb.AxisFields.ContainsKey(HMAxis.X));
        Check("AxisFields contains Y (from AddStick)",   rb.AxisFields.ContainsKey(HMAxis.Y));
        Check("AxisFields contains Slider",              rb.AxisFields.ContainsKey(HMAxis.Slider));
        Check("AxisFields contains Rudder",              rb.AxisFields.ContainsKey(HMAxis.Rudder));
        Check("AxisFields contains Throttle",            rb.AxisFields.ContainsKey(HMAxis.Throttle));
        Check("AxisFields contains Brake",               rb.AxisFields.ContainsKey(HMAxis.Brake));
        Check("AxisFields contains Clutch",              rb.AxisFields.ContainsKey(HMAxis.Clutch));

        // Drive every axis via the unified Axes dict; verify each field's wire byte.
        var built = rb.BuildReport(new Dictionary<HMAxis, float>
        {
            [HMAxis.Slider]   = 1.0f,
            [HMAxis.Rudder]   = 0.5f,
            [HMAxis.Throttle] = 0.75f,
            [HMAxis.Brake]    = 0.25f,
            [HMAxis.Clutch]   = 0.0f,
        });

        Check("Builder Slider wire byte = 0xFF",   built[rb.AxisFields[HMAxis.Slider].BitOffset / 8]   == 0xFF);
        Check("Builder Rudder wire byte = 0x7F",   built[rb.AxisFields[HMAxis.Rudder].BitOffset / 8]   == 0x7F);
        Check("Builder Throttle wire byte = 0xBF", built[rb.AxisFields[HMAxis.Throttle].BitOffset / 8] == 0xBF);
        Check("Builder Brake wire byte = 0x3F",    built[rb.AxisFields[HMAxis.Brake].BitOffset / 8]    == 0x3F);
        Check("Builder Clutch wire byte = 0x00",   built[rb.AxisFields[HMAxis.Clutch].BitOffset / 8]   == 0x00);

        // ── Phase 4: Last-write-wins on the unified Axes dict ─────────────
        Console.WriteLine("\n-- Last-write-wins on the unified Axes dict --");

        // SideWinder's Slider field is the descriptor axis HMAxis.Slider; in
        // v1.3.9 there is exactly ONE write surface (state.Axes[axis]) so the
        // last write to a key wins. Verify the dict-driven write lands at
        // the correct wire byte.
        var sw = ctx.GetProfile("sidewinder-force-feedback-2")!;
        var swRb = HidReportBuilder.Parse(sw.GetDescriptorBytes()!);
        var swReport = swRb.BuildReport(
            new Dictionary<HMAxis, float> { [HMAxis.Slider] = 1.0f });

        Check("Slider=1.0 lands at byte 6 = 0x7F on SideWinder descriptor",
              swReport[6] == 0x7F,
              $"got 0x{swReport[6]:X2}");

        // ── Summary ───────────────────────────────────────────────────────
        Console.WriteLine($"\n=== {(s_failures == 0 ? "PASS" : "FAIL")}: {s_total - s_failures}/{s_total} ===");
        return s_failures == 0 ? 0 : 1;
    }

    static void AssertProfileAxes(HMContext ctx, string id, params HMAxis[] expected)
    {
        var p = ctx.GetProfile(id);
        if (p == null || !p.IsDeployable)
        {
            Check($"{id} loaded + deployable", false);
            return;
        }
        var rb = HidReportBuilder.Parse(p.GetDescriptorBytes()!);
        Check($"{id} AvailableAxes count = {expected.Length}",
              p.AvailableAxes.Count == expected.Length,
              $"got [{string.Join(",", p.AvailableAxes)}]");
        foreach (var a in expected)
            Check($"{id} declares {a}", rb.AxisFields.ContainsKey(a));
    }

    static void WireCheck(HMContext ctx, string id, HMAxis axis, float val,
                          int byteOffset, int expected)
    {
        var p = ctx.GetProfile(id);
        if (p == null || !p.IsDeployable)
        {
            Check($"{id} {axis}={val:F2}", false, "(profile not deployable)");
            return;
        }
        var rb = HidReportBuilder.Parse(p.GetDescriptorBytes()!);
        if (!rb.AxisFields.ContainsKey(axis))
        {
            Check($"{id} {axis}={val:F2}", false, "(axis not declared in descriptor)");
            return;
        }
        var report = rb.BuildReport(new Dictionary<HMAxis, float> { [axis] = val });
        int got = report[byteOffset];
        Check($"{id} {axis}={val:F2} -> byte[{byteOffset}] = 0x{expected:X2}",
              got == expected,
              got == expected ? "" : $"got 0x{got:X2}");
    }
}
