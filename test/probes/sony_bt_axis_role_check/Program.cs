// v1.3.13 — Sony Bluetooth axis-role regression probe (#23).
//
// Guards the bug where DualSense / DualShock 4 Bluetooth profiles emitted
// right-stick and trigger axes swapped. Root cause: HMProfile's simple-slot
// resolver (GetSimpleSticks / GetSimpleTriggers) classified axes purely
// through HidReportBuilder's raw HID-usage-code heuristic and ignored the
// profile JSON's axisMap / layout. Sony controllers use Z/Rz for the right
// stick and Rx/Ry for the triggers (opposite of the XInput convention the
// heuristic assumes), so the right stick and triggers came out swapped.
//
// Two-part check, pure in-process (no driver, no virtual device):
//
//   Part A — contract assertion. For every Sony BT profile, assert
//     Profile.Sticks / Profile.Triggers resolve to the Sony-convention
//     axis keys: right stick = Z/Rz, triggers = Rx/Ry. This is the exact
//     contract that regressed; pre-fix it returned Rx/Ry for the right
//     stick and Z/Rz for the triggers.
//
//   Part B — encode round-trip. Fill state.Axes by the INDEPENDENT Sony
//     convention (Axes[Z] = right-stick-X, Axes[Rx] = left-trigger, ...)
//     the way a consumer that read the profile's axisMap from JSON does.
//     Resolve the six simple-slot floats exactly as HMController.SubmitState
//     does (GetAxis(Profile.Sticks[i]...)), run VendorBlobCodec.EncodeInput,
//     and assert the right-stick and trigger bytes of the emitted Report
//     0x31 carry the right-stick and trigger values — not each other's.
//     Distinct per-axis test values make any swap visible.
//
//     IMPORTANT: Part B must NOT fill state.Axes via
//     HMGamepadStateHelpers.StandardAxes — that resolves through the same
//     Profile.Sticks the probe is testing, so a swap would cancel out and
//     hide pre-fix. The Sony-convention fill is hard-coded here.
//
// Exit 0 on all PASS, 1 on any FAIL.

using System;
using System.Collections.Generic;
using HIDMaestro;
using HIDMaestro.Internal;

internal sealed class Program
{
    static int s_total;
    static int s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    // The four Sony Bluetooth profiles that ride the #23 bug. All carry the
    // Sony-convention axisMap (0x32->rightStickX, 0x35->rightStickY,
    // 0x33->leftTrigger, 0x34->rightTrigger).
    static readonly string[] SonyBtProfiles =
    {
        "dualsense-bt", "dualsense-bt-full", "dualsense-edge-bt", "dualshock-4-v2-bt",
    };

    public static int Main()
    {
        Console.WriteLine("=== v1.3.13 Sony BT axis-role regression probe (#23) ===\n");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        foreach (var id in SonyBtProfiles)
        {
            var p = ctx.GetProfile(id);
            if (p == null) { Check($"{id}: profile present in catalog", false); continue; }
            Console.WriteLine($"--- {id} ---");
            CheckContract(p);
            CheckEncodeRoundTrip(p);
            Console.WriteLine();
        }

        Console.WriteLine($"=== {(s_failures == 0 ? "PASS" : "FAIL")}: {s_total - s_failures}/{s_total} ===");
        return s_failures == 0 ? 0 : 1;
    }

    // Part A — Profile.Sticks / Profile.Triggers must report the Sony
    // convention: left stick X/Y, right stick Z/Rz, triggers Rx/Ry.
    static void CheckContract(HMProfile p)
    {
        var sticks = p.Sticks;
        var triggers = p.Triggers;

        Check($"{p.Id}: 2 sticks", sticks.Count == 2, $"got {sticks.Count}");
        Check($"{p.Id}: 2 triggers", triggers.Count == 2, $"got {triggers.Count}");
        if (sticks.Count != 2 || triggers.Count != 2) return;

        Check($"{p.Id}: left stick = X/Y",
              sticks[0].XAxis == HMAxis.X && sticks[0].YAxis == HMAxis.Y,
              $"got {sticks[0].XAxis}/{sticks[0].YAxis}");
        Check($"{p.Id}: right stick = Z/Rz (Sony convention)",
              sticks[1].XAxis == HMAxis.Z && sticks[1].YAxis == HMAxis.Rz,
              $"got {sticks[1].XAxis}/{sticks[1].YAxis}");
        Check($"{p.Id}: left trigger = Rx (Sony convention)",
              triggers[0].Axis == HMAxis.Rx, $"got {triggers[0].Axis}");
        Check($"{p.Id}: right trigger = Ry (Sony convention)",
              triggers[1].Axis == HMAxis.Ry, $"got {triggers[1].Axis}");
    }

    // Part B — full encode round trip. Distinct per-axis test values.
    const float kLeftStickX  = 0.60f;   // -> byte 153
    const float kLeftStickY  = 0.35f;   // -> byte 89
    const float kRightStickX = 0.75f;   // -> byte 191
    const float kRightStickY = 0.25f;   // -> byte 64
    const float kLeftTrigger = 0.40f;   // -> byte 102
    const float kRightTrigger = 0.90f;  // -> byte 230

    static void CheckEncodeRoundTrip(HMProfile p)
    {
        if (!p.HasExtendedInput || p.ExtendedReport == null)
        {
            Console.WriteLine($"  [skip] {p.Id}: no extendedReport — encode round-trip not applicable");
            return;
        }

        // Fill state.Axes by the Sony convention, INDEPENDENT of Profile.Sticks.
        // This is what a consumer that read the profile JSON's axisMap does:
        // Z = right-stick-X, Rz = right-stick-Y, Rx = L2, Ry = R2.
        var axes = new Dictionary<HMAxis, float>
        {
            [HMAxis.X]  = kLeftStickX,
            [HMAxis.Y]  = kLeftStickY,
            [HMAxis.Z]  = kRightStickX,
            [HMAxis.Rz] = kRightStickY,
            [HMAxis.Rx] = kLeftTrigger,
            [HMAxis.Ry] = kRightTrigger,
        };
        var state = new HMGamepadState { Axes = axes };

        // Resolve the six simple-slot floats exactly as HMController.SubmitState
        // does: GetAxis(Profile.Sticks[i].Axis). After the #23 fix Sticks[1].XAxis
        // is Z, so mrx reads Axes[Z] = right-stick-X. Pre-fix it was Rx, so mrx
        // read Axes[Rx] = left-trigger -> swap.
        float GetAxis(HMAxis ax, float def) =>
            axes.TryGetValue(ax, out var v) ? Math.Clamp(v, 0f, 1f) : def;

        var sticks = p.Sticks;
        var triggers = p.Triggers;
        float mlx = sticks.Count > 0 ? GetAxis(sticks[0].XAxis, 0.5f) : 0.5f;
        float mly = sticks.Count > 0 ? GetAxis(sticks[0].YAxis, 0.5f) : 0.5f;
        float mrx = sticks.Count > 1 ? GetAxis(sticks[1].XAxis, 0.5f) : 0.5f;
        float mry = sticks.Count > 1 ? GetAxis(sticks[1].YAxis, 0.5f) : 0.5f;
        float mlt = triggers.Count > 0 ? GetAxis(triggers[0].Axis, 0.0f) : 0.0f;
        float mrt = triggers.Count > 1 ? GetAxis(triggers[1].Axis, 0.0f) : 0.0f;

        var spec = p.ExtendedReport!;
        var buffer = new byte[spec.Size];
        var encState = new VendorBlobCodec.EncoderState();
        VendorBlobCodec.EncodeInput(spec, in state,
            mlx, mly, mrx, mry, mlt, mrt, buffer, encState);

        // Resolve each axis field's byte offset from the profile's
        // extendedReport spec — DualSense Report 0x31 and DS4 Report 0x11
        // put the six axes at different offsets, so don't hardcode.
        int OffsetOf(string semantic)
        {
            foreach (var f in spec.Fields)
                if (string.Equals(f.Semantic, semantic, StringComparison.OrdinalIgnoreCase)
                    && f.Byte.HasValue)
                    return f.Byte.Value;
            return -1;
        }

        static int B(float v) => (int)Math.Round(Math.Clamp(v, 0f, 1f) * 255f);
        var asserts = new (string semantic, int expected, string name)[]
        {
            ("leftStickX",   B(kLeftStickX),   "left-stick-X byte"),
            ("leftStickY",   B(kLeftStickY),   "left-stick-Y byte"),
            ("rightStickX",  B(kRightStickX),  "right-stick-X byte (NOT a trigger value)"),
            ("rightStickY",  B(kRightStickY),  "right-stick-Y byte (NOT a trigger value)"),
            ("leftTrigger",  B(kLeftTrigger),  "left-trigger byte (NOT a stick value)"),
            ("rightTrigger", B(kRightTrigger), "right-trigger byte (NOT a stick value)"),
        };
        foreach (var (semantic, expected, name) in asserts)
        {
            int idx = OffsetOf(semantic);
            if (idx < 0)
            {
                Check($"{p.Id}: {name}", false, $"semantic '{semantic}' not found in extendedReport spec");
                continue;
            }
            // Allow ±1 for rounding.
            bool pass = idx < buffer.Length && Math.Abs(buffer[idx] - expected) <= 1;
            Check($"{p.Id}: {name}", pass,
                  $"buffer[{idx}]={(idx < buffer.Length ? buffer[idx] : -1)} expected ~{expected}");
        }
    }
}
