// PadForge discussion #130 (HIDMaestro GIP-side regression). Post-v1.3.17
// initial ship the HID descriptor synthesis for combined Z + Vx/Vy was
// fixed, but the SubmitState path that packs the 14-byte GIP buffer
// (the XInput/WGI/RawInput input source via the XUSB companion) still
// read triggers from state.Axes[layout.triggers[N].Axis], which for the
// unified xbox-360-* profiles is Vx / Vy. PadForge writes triggers to
// canonical state.Axes[HMAxis.Z] / state.Axes[HMAxis.Rz] (its
// ResolveAxisByRole default), so the GIP buffer received PadForge's
// "centered" defaults (0.5) for the layout-declared Vx/Vy positions and
// every non-DirectInput API (MultipadTester's XInput, WGI, RawInput
// tabs; Chrome gamepad tester) saw both triggers frozen at 0.50.
//
// HIDMaestroTest didn't surface the bug because StandardAxes writes
// axes[triggers[0].Axis] = leftTrigger directly — i.e. axes[Vx] = LT
// when layout.triggers[0].Axis == Vx. PadForge writes canonical Z/Rz.
//
// Probe drives HMController.ResolveTrigger across both consumer patterns
// (PadForge canonical / HIDMaestroTest StandardAxes / both / neither)
// for all three Xbox 360 gamepad profiles. Asserts the canonical
// position wins when present, the layout field wins as fallback, and
// the default (0.0) fires when neither is set.

using System;
using System.Collections.Generic;
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

    public static int Main()
    {
        Console.WriteLine("=== PadForge #130: GIP-side trigger resolver (canonical-vs-layout) ===\n");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        var profileIds = new[]
        {
            "xbox-360-wired",
            "xbox-360-wireless",
            "xbox-360-type2",
        };

        foreach (var id in profileIds)
        {
            Console.WriteLine($"--- {id} ---");
            var profile = ctx.GetProfile(id);
            if (profile == null)
            {
                Console.WriteLine($"  (profile not found, skipping)");
                continue;
            }

            var triggers = profile.Triggers;
            var axisMap = profile.Inner.AxisMap;
            Check($"{id}: layout.triggers has 2 entries (left + right)",
                  triggers.Count == 2,
                  $"got Count={triggers.Count}");
            if (triggers.Count < 2) continue;

            // PadForge pattern: canonical Z/Rz writes, possibly with Vx/Vy
            // filled to centered 0.5 from PadForge's "every axis defaults
            // to 0.5" initialiser (the original symptom — non-DirectInput
            // APIs showed 0.5 stuck because the resolver picked up Vx=0.5).
            {
                var axes = new Dictionary<HMAxis, float>
                {
                    [HMAxis.Z]  = 1.0f,   // LT pulled
                    [HMAxis.Rz] = 0.0f,   // RT idle
                    [HMAxis.Vx] = 0.5f,   // PadForge stale-default
                    [HMAxis.Vy] = 0.5f,   // PadForge stale-default
                };
                double mlt = HMController.ResolveTrigger(axes, triggers, 0, HMController.ResolveCanonicalAxis(axisMap, "lefttrigger", HMAxis.Z));
                double mrt = HMController.ResolveTrigger(axes, triggers, 1, HMController.ResolveCanonicalAxis(axisMap, "righttrigger", HMAxis.Rz));
                Check($"{id}: PadForge (Z=1, Rz=0, Vx=Vy=0.5) → mlt=1.0 (canonical Z wins over stale Vx=0.5)",
                      Math.Abs(mlt - 1.0) < 1e-6, $"got mlt={mlt}");
                Check($"{id}: PadForge (Z=1, Rz=0, Vx=Vy=0.5) → mrt=0.0 (canonical Rz wins over stale Vy=0.5)",
                      Math.Abs(mrt - 0.0) < 1e-6, $"got mrt={mrt}");
            }

            // HIDMaestroTest pattern: StandardAxes writes axes[triggers[N].Axis]
            // only — canonical Z/Rz not set. Resolver must fall back to the
            // layout field's HMAxis key (Vx/Vy for the unified profiles).
            {
                var axes = new Dictionary<HMAxis, float>
                {
                    [triggers[0].Axis] = 1.0f, // LT
                    [triggers[1].Axis] = 0.0f, // RT
                };
                double mlt = HMController.ResolveTrigger(axes, triggers, 0, HMController.ResolveCanonicalAxis(axisMap, "lefttrigger", HMAxis.Z));
                double mrt = HMController.ResolveTrigger(axes, triggers, 1, HMController.ResolveCanonicalAxis(axisMap, "righttrigger", HMAxis.Rz));
                Check($"{id}: StandardAxes (only triggers[N].Axis set) → mlt=1.0 (field-key fallback)",
                      Math.Abs(mlt - 1.0) < 1e-6, $"got mlt={mlt}");
                Check($"{id}: StandardAxes (only triggers[N].Axis set) → mrt=0.0 (field-key fallback)",
                      Math.Abs(mrt - 0.0) < 1e-6, $"got mrt={mrt}");
            }

            // Both writers active at the same value: canonical wins, result
            // identical either way.
            {
                var axes = new Dictionary<HMAxis, float>
                {
                    [HMAxis.Z]  = 0.75f,
                    [HMAxis.Rz] = 0.25f,
                    [triggers[0].Axis] = 0.75f,
                    [triggers[1].Axis] = 0.25f,
                };
                double mlt = HMController.ResolveTrigger(axes, triggers, 0, HMController.ResolveCanonicalAxis(axisMap, "lefttrigger", HMAxis.Z));
                double mrt = HMController.ResolveTrigger(axes, triggers, 1, HMController.ResolveCanonicalAxis(axisMap, "righttrigger", HMAxis.Rz));
                Check($"{id}: both writers (Z=0.75) → mlt=0.75",
                      Math.Abs(mlt - 0.75) < 1e-6, $"got mlt={mlt}");
                Check($"{id}: both writers (Rz=0.25) → mrt=0.25",
                      Math.Abs(mrt - 0.25) < 1e-6, $"got mrt={mrt}");
            }

            // Empty axes dict → default 0.0.
            {
                var axes = new Dictionary<HMAxis, float>();
                double mlt = HMController.ResolveTrigger(axes, triggers, 0, HMController.ResolveCanonicalAxis(axisMap, "lefttrigger", HMAxis.Z));
                double mrt = HMController.ResolveTrigger(axes, triggers, 1, HMController.ResolveCanonicalAxis(axisMap, "righttrigger", HMAxis.Rz));
                Check($"{id}: empty axes → mlt=0.0",
                      Math.Abs(mlt - 0.0) < 1e-6, $"got mlt={mlt}");
                Check($"{id}: empty axes → mrt=0.0",
                      Math.Abs(mrt - 0.0) < 1e-6, $"got mrt={mrt}");
            }
        }

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} {(s_failures == 0 ? "PASS" : "FAIL")} ===");
        return s_failures == 0 ? 0 : 1;
    }
}
