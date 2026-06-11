// PadForge discussion #130 (HIDMaestro regression). The Xbox 360 wired
// profile's split-trigger workaround broke at the v1.3.9 refactor:
// BuildReportInto's combined-Z synthesis reads from
// state.Axes[LeftTrigger.Usage] and state.Axes[RightTrigger.Usage] — i.e.
// state.Axes[Vx] and state.Axes[Vy] — but consumers (PadForge) write
// state.Axes[Z] = leftTrigger and state.Axes[Rz] = rightTrigger because
// ResolveAxisByRole defaults to HMAxis.Z / HMAxis.Rz when the profile has
// no axisMap. Vx / Vy stay at their seed values (0 or 0.5) so the
// synthesis always lands on combined = 0.5 and joy.cpl shows the Z axis
// pinned at midpoint regardless of LT/RT input — except the per-axis loop
// at line 695 wrote Z = state.Axes[Z] = leftTrigger right before
// synthesis overwrote it, so the symptom that surfaces is "Z reflects LT
// only; RT does nothing." The XInput / WGI Vx / Vy fields also carry
// stale 0.5 values instead of the actual trigger values.
//
// All Xbox 360 profiles (xbox-360-wired, xbox-360-wireless, plus the
// type2 / wheel / arcade-stick / flight-stick / etc. cousins that share
// the X / Y / Rx / Ry / Z / Vx / Vy shape) are affected.
//
// Assertions:
//   - With state.Axes[Z]=1.0, state.Axes[Rz]=0.0 (LT pulled, RT idle):
//       Z wire byte == LogicalMin (combined formula → 0)
//       Vx wire byte == LogicalMax (full LT)
//       Vy wire byte == LogicalMin (RT idle)
//   - With state.Axes[Z]=0.0, state.Axes[Rz]=1.0 (RT pulled, LT idle):
//       Z wire byte == LogicalMax (combined formula → 1)
//       Vx wire byte == LogicalMin (LT idle)
//       Vy wire byte == LogicalMax (full RT)
//   - With state.Axes[Z]=0.0, state.Axes[Rz]=0.0 (both idle):
//       Z wire byte == LogicalMidpoint (combined → 0.5)
//       Vx, Vy == LogicalMin
//
// Exit 0 on PASS, 1 on FAIL.

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

    static int ReadField(byte[] report, HidReportBuilder.InputField field, byte reportId)
    {
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
        Console.WriteLine("=== PadForge #130: Xbox 360 split-trigger regression ===\n");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        // Every Xbox 360 gamepad-shape profile MUST present the same
        // split-trigger workaround: Z + Vx + Vy in the descriptor, with
        // joy.cpl seeing combined Z and WGI / XInput seeing raw Vx / Vy.
        // Asymmetry between profiles (wired works, wireless doesn't) is
        // exactly the user-visible regression class the issue called out.
        // Specialty controllers (wheel, dance pad, flight stick, guitar)
        // intentionally don't carry this combo — they're not gamepad-
        // shaped and don't go through the combined-Z path.
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

            var descBytes = profile.GetDescriptorBytes();
            if (descBytes == null)
            {
                Console.WriteLine($"  (no descriptor, skipping)");
                continue;
            }
            var b = HidReportBuilder.Parse(descBytes, profile.AxisMap);

            // Classifier outcomes: the workaround needs CombinedTrigger
            // pointing at Z, LeftTrigger at Vx, RightTrigger at Vy.
            Check($"{id}: CombinedTrigger from Z",
                  b.CombinedTrigger != null && b.CombinedTrigger.Usage == 0x32,
                  b.CombinedTrigger == null ? "(null)" : $"got 0x{b.CombinedTrigger.Usage:X2}");
            Check($"{id}: LeftTrigger from Vx",
                  b.LeftTrigger != null && b.LeftTrigger.Usage == 0x40,
                  b.LeftTrigger == null ? "(null)" : $"got 0x{b.LeftTrigger.Usage:X2}");
            Check($"{id}: RightTrigger from Vy",
                  b.RightTrigger != null && b.RightTrigger.Usage == 0x41,
                  b.RightTrigger == null ? "(null)" : $"got 0x{b.RightTrigger.Usage:X2}");

            if (b.CombinedTrigger == null || b.LeftTrigger == null || b.RightTrigger == null)
            {
                Console.WriteLine("  (skipping wire checks; classifier mis-routed)");
                continue;
            }

            int zMin = b.CombinedTrigger.LogicalMin;
            int zMax = b.CombinedTrigger.LogicalMax;
            int zMid = (zMin + zMax) / 2;
            int vxMin = b.LeftTrigger.LogicalMin;
            int vxMax = b.LeftTrigger.LogicalMax;
            int vyMin = b.RightTrigger.LogicalMin;
            int vyMax = b.RightTrigger.LogicalMax;

            int reportSize = (b.InputReportBitSize + 7) / 8;
            byte[] report = new byte[reportSize];

            // Consumer-written state.Axes carries trigger values at the
            // canonical user-facing positions (HMAxis.Z for LT, HMAxis.Rz
            // for RT — what PadForge's ResolveAxisByRole defaults to when
            // the profile carries no axisMap).
            // Case A: LT=1.0, RT=0.0 → expect combined Z at LogicalMin,
            // Vx full-scale, Vy zero.
            {
                var axes = new Dictionary<HMAxis, float>
                {
                    [HMAxis.X] = 0.5f, [HMAxis.Y] = 0.5f,
                    [HMAxis.Rx] = 0.5f, [HMAxis.Ry] = 0.5f,
                    [HMAxis.Z] = 1.0f, [HMAxis.Rz] = 0.0f,
                    [HMAxis.Vx] = 0.5f, [HMAxis.Vy] = 0.5f,
                };
                b.BuildReportInto(report, axes);
                int z  = ReadField(report, b.CombinedTrigger, b.InputReportId);
                int vx = ReadField(report, b.LeftTrigger,     b.InputReportId);
                int vy = ReadField(report, b.RightTrigger,    b.InputReportId);
                Check($"{id}: LT=1.0 RT=0.0 → Z = LogicalMin (combined 0)",
                      z == zMin, $"got Z={z}, expected {zMin}");
                Check($"{id}: LT=1.0 RT=0.0 → Vx = LogicalMax (full LT)",
                      vx == vxMax, $"got Vx={vx}, expected {vxMax}");
                Check($"{id}: LT=1.0 RT=0.0 → Vy = LogicalMin (idle RT)",
                      vy == vyMin, $"got Vy={vy}, expected {vyMin}");
            }

            // Case B: LT=0.0, RT=1.0 → expect combined Z at LogicalMax,
            // Vx zero, Vy full-scale.
            {
                var axes = new Dictionary<HMAxis, float>
                {
                    [HMAxis.X] = 0.5f, [HMAxis.Y] = 0.5f,
                    [HMAxis.Rx] = 0.5f, [HMAxis.Ry] = 0.5f,
                    [HMAxis.Z] = 0.0f, [HMAxis.Rz] = 1.0f,
                    [HMAxis.Vx] = 0.5f, [HMAxis.Vy] = 0.5f,
                };
                b.BuildReportInto(report, axes);
                int z  = ReadField(report, b.CombinedTrigger, b.InputReportId);
                int vx = ReadField(report, b.LeftTrigger,     b.InputReportId);
                int vy = ReadField(report, b.RightTrigger,    b.InputReportId);
                Check($"{id}: LT=0.0 RT=1.0 → Z = LogicalMax (combined full)",
                      z == zMax, $"got Z={z}, expected {zMax}");
                Check($"{id}: LT=0.0 RT=1.0 → Vx = LogicalMin (idle LT)",
                      vx == vxMin, $"got Vx={vx}, expected {vxMin}");
                Check($"{id}: LT=0.0 RT=1.0 → Vy = LogicalMax (full RT)",
                      vy == vyMax, $"got Vy={vy}, expected {vyMax}");
            }

            // Case C: LT=0.0 RT=0.0 → Z at midpoint, Vx Vy zero.
            {
                var axes = new Dictionary<HMAxis, float>
                {
                    [HMAxis.X] = 0.5f, [HMAxis.Y] = 0.5f,
                    [HMAxis.Rx] = 0.5f, [HMAxis.Ry] = 0.5f,
                    [HMAxis.Z] = 0.0f, [HMAxis.Rz] = 0.0f,
                    [HMAxis.Vx] = 0.5f, [HMAxis.Vy] = 0.5f,
                };
                b.BuildReportInto(report, axes);
                int z  = ReadField(report, b.CombinedTrigger, b.InputReportId);
                int vx = ReadField(report, b.LeftTrigger,     b.InputReportId);
                int vy = ReadField(report, b.RightTrigger,    b.InputReportId);
                Check($"{id}: LT=0 RT=0 → Z = LogicalMidpoint",
                      Math.Abs(z - zMid) <= 1, $"got Z={z}, expected ~{zMid}");
                Check($"{id}: LT=0 RT=0 → Vx = LogicalMin",
                      vx == vxMin, $"got Vx={vx}, expected {vxMin}");
                Check($"{id}: LT=0 RT=0 → Vy = LogicalMin",
                      vy == vyMin, $"got Vy={vy}, expected {vyMin}");
            }
        }

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} {(s_failures == 0 ? "PASS" : "FAIL")} ===");
        return s_failures == 0 ? 0 : 1;
    }
}
