// v1.3.4 — fine-grained hat encoder regression probe.
//
// Verifies the four HMGamepadState hat-input shapes encode the expected
// descriptor bits across hat resolutions (8 octant, 16 HOTAS, 360 pro):
//   Hat            HMHat enum (back-compat octant)
//   HatRaw         ushort? clamped to LogicalMin..LogicalMax
//   HatHundredths  int?    integer hundredths-of-a-degree
//   HatDegrees     float?  continuous angle, 0=North CW
//
// The encoder priority chain is HatDegrees > HatHundredths > HatRaw > Hat.
// First non-null wins; remaining inputs ignored for that frame.
//
// Pure encoder unit-test: no driver install, no virtual device — builds
// profiles via HidDescriptorBuilder + HMProfileBuilder, parses descriptor
// via HidReportBuilder, calls BuildReport directly, asserts the hat byte.
//
// Exit code 0 on all PASS, 1 on any FAIL.

using System;
using HIDMaestro;
using HIDMaestro.Internal;

internal sealed class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("=== v1.3.4 hat-resolution encoder probe ===\n");

        int failures = 0;
        int total = 0;

        // ── Resolution sweep ─────────────────────────────────────────
        // Each row: positions in the descriptor's hat field. Logical
        // range is 0..positions; null state is 0 (LogicalMin).
        foreach (int positions in new[] { 8, 16, 360 })
        {
            var desc = new HidDescriptorBuilder()
                .Joystick()
                .AddStick("Left", bits: 16)
                .AddTrigger("Left", bits: 8)
                .AddButtons(8)
                .AddHat(positions: positions)
                .Build();

            var layout = HidReportBuilder.Parse(desc);
            if (layout.HatSwitch == null)
            {
                Console.WriteLine($"  [FAIL] positions={positions}: parser did not find HatSwitch field");
                failures++; total++;
                continue;
            }

            int logMin = layout.HatSwitch.LogicalMin;
            int logMax = layout.HatSwitch.LogicalMax;
            int range  = logMax - logMin + 1;
            int bitOff = layout.HatSwitch.BitOffset;
            int bitSize = layout.HatSwitch.BitSize;
            Console.WriteLine($"  Hat positions={positions}: LogicalMin={logMin}, LogicalMax={logMax}, " +
                              $"range={range}, bitOff={bitOff}, bitSize={bitSize}");

            // ── Octant via Hat enum (back-compat + high-res scaling) ────
            // Encoder maps HMHat values 1..8 to descriptor positions
            // evenly distributed across the descriptor range:
            //   idx = (octant - 1) * range / 8
            // For range=8 this is the legacy 0..7 mapping. For range=16:
            // NE → idx 2, E → idx 4, SE → idx 6, etc. — the eight
            // octants land at the matching 45° positions.
            for (int octant = 1; octant <= 8; octant++)
            {
                int expected = logMin + (octant - 1) * range / 8;
                int observed = EncodeAndReadHat(layout, hatEnum: octant);
                bool pass = observed == expected;
                Console.WriteLine($"    [{(pass ? "PASS" : "FAIL")}] " +
                                  $"Hat=HMHat({octant}) → expected={expected} observed={observed}");
                if (!pass) failures++;
                total++;
            }

            // ── HMHat.None — null state ─────────────────────────────
            int noneExpected = (logMin == 0) ? logMax + 1 : 0;
            // Special case: 16-bit hats (positions > 255) the wrap target may
            // exceed the descriptor's bit field. The encoder writes the value
            // unchanged; the field's bit width truncates. For our profiles
            // (8 → 8-bit, 16 → 8-bit, 360 → 16-bit) this stays correct.
            int noneObs = EncodeAndReadHat(layout, hatEnum: 0);
            // Mask to bitSize so we compare the ON-WIRE value (not whatever
            // higher bits the encoder may have produced before bit-truncation).
            int noneObsMasked = noneObs & ((1 << bitSize) - 1);
            int noneExpMasked = noneExpected & ((1 << bitSize) - 1);
            bool nonePass = noneObsMasked == noneExpMasked;
            Console.WriteLine($"    [{(nonePass ? "PASS" : "FAIL")}] " +
                              $"Hat=None → expected={noneExpMasked} observed={noneObsMasked} (null state)");
            if (!nonePass) failures++;
            total++;

            // ── HatRaw — bit-exact descriptor value with clamping ───
            // Pick a midrange position, the LogicalMin, the LogicalMax,
            // and one over (must clamp to LogicalMax).
            int[] rawCandidates = { logMin, logMin + range / 2, logMax, logMax + 5 };
            foreach (int raw in rawCandidates)
            {
                int expected = Math.Clamp(raw, logMin, logMax);
                int observed = EncodeAndReadHat(layout, hatRaw: (ushort)Math.Clamp(raw, 0, ushort.MaxValue));
                bool pass = observed == expected;
                Console.WriteLine($"    [{(pass ? "PASS" : "FAIL")}] " +
                                  $"HatRaw={raw} → expected={expected} observed={observed} (clamp test)");
                if (!pass) failures++;
                total++;
            }

            // ── HatDegrees — continuous angle snapping ──────────────
            // For each octant boundary: 0°=N, 90°=E, 180°=S, 270°=W.
            // Encoder snaps to nearest of `range` positions equally
            // spaced around 360°.
            (float deg, int idxExpected)[] degCases = {
                (0f,    0),
                (90f,   range / 4),
                (180f,  range / 2),
                (270f,  3 * range / 4),
                // Wrap-around: 350° on an 8-position hat is closer to 0 (idx 0)
                // than to 315 (idx 7); the trailing % range is load-bearing.
                (350f,  (int)Math.Round(350.0 / 360.0 * range) % range),
                (-10f,  (int)Math.Round(350.0 / 360.0 * range) % range),
                (720f,  0),
            };
            foreach (var (deg, idxExpected) in degCases)
            {
                int expected = logMin + idxExpected;
                int observed = EncodeAndReadHat(layout, hatDegrees: deg);
                bool pass = observed == expected;
                Console.WriteLine($"    [{(pass ? "PASS" : "FAIL")}] " +
                                  $"HatDegrees={deg,6} → idx={idxExpected,3} expected={expected,4} observed={observed,4}");
                if (!pass) failures++;
                total++;
            }

            // ── HatHundredths — integer hundredths-of-degree, truncating ─
            // Encoder uses (long)v * range / 36000 (truncation, matches vJoy).
            (int hund, int idxExpected)[] hundCases = {
                (0,        0),
                (9000,     (int)((long)9000  * range / 36000)),
                (18000,    (int)((long)18000 * range / 36000)),
                (27000,    (int)((long)27000 * range / 36000)),
                (35999,    (int)((long)35999 * range / 36000)),
                // Wrap test: -100 normalizes to 35900 → near-but-not-zero idx
                (-100,     (int)((long)35900 * range / 36000)),
            };
            foreach (var (hund, idxExpected) in hundCases)
            {
                int expected = logMin + idxExpected;
                int observed = EncodeAndReadHat(layout, hatHundredths: hund);
                bool pass = observed == expected;
                Console.WriteLine($"    [{(pass ? "PASS" : "FAIL")}] " +
                                  $"HatHundredths={hund,7} → idx={idxExpected,3} expected={expected,4} observed={observed,4}");
                if (!pass) failures++;
                total++;
            }

            // ── Priority chain ──────────────────────────────────────
            // When multiple fields are set on the same frame, the encoder
            // must pick the highest-priority and ignore the rest.
            // HatDegrees=0 (idx 0 = N) should win over HatHundredths=18000 (idx range/2 = S),
            // HatRaw=logMax (idx range-1 = NNW), Hat=South (idx 4).
            {
                int observed = EncodeAndReadHat(layout,
                    hatDegrees: 0f,
                    hatHundredths: 18000,
                    hatRaw: (ushort)logMax,
                    hatEnum: 5 /* HMHat.South */);
                int expected = logMin + 0;
                bool pass = observed == expected;
                Console.WriteLine($"    [{(pass ? "PASS" : "FAIL")}] priority: deg=0 hund=18000 raw=logMax hat=South → " +
                                  $"deg wins. expected={expected} observed={observed}");
                if (!pass) failures++;
                total++;
            }

            // HatHundredths beats HatRaw and Hat
            {
                int observed = EncodeAndReadHat(layout,
                    hatHundredths: 9000, // idx = range/4 = E
                    hatRaw: (ushort)logMax,
                    hatEnum: 5);
                int expected = logMin + (int)((long)9000 * range / 36000);
                bool pass = observed == expected;
                Console.WriteLine($"    [{(pass ? "PASS" : "FAIL")}] priority: hund=9000 raw=logMax hat=South → " +
                                  $"hund wins. expected={expected} observed={observed}");
                if (!pass) failures++;
                total++;
            }

            // HatRaw beats Hat
            {
                int observed = EncodeAndReadHat(layout,
                    hatRaw: (ushort)logMax,
                    hatEnum: 1 /* North */);
                int expected = logMax;
                bool pass = observed == expected;
                Console.WriteLine($"    [{(pass ? "PASS" : "FAIL")}] priority: raw=logMax hat=North → " +
                                  $"raw wins. expected={expected} observed={observed}");
                if (!pass) failures++;
                total++;
            }

            Console.WriteLine();
        }

        if (failures == 0)
        {
            Console.WriteLine($"=== ALL PASS ({total} cases across 3 hat resolutions) ===");
            return 0;
        }
        Console.WriteLine($"=== FAIL: {failures}/{total} encoder cases did not match ===");
        return 1;
    }

    // Encode a frame with the given hat input(s), then decode the hat
    // field from the on-wire byte buffer using the descriptor's bit
    // offset/size. Returns the hat field's logical value as an int.
    private static int EncodeAndReadHat(HidReportBuilder layout,
        float? hatDegrees = null,
        int? hatHundredths = null,
        ushort? hatRaw = null,
        int hatEnum = 0)
    {
        byte[] report = layout.BuildReport(
            hatValue: hatEnum,
            hatDegrees: hatDegrees,
            hatHundredths: hatHundredths,
            hatRaw: hatRaw);

        return ReadBits(report,
            layout.HatSwitch!.BitOffset + (layout.InputReportId != 0 ? 8 : 0),
            layout.HatSwitch.BitSize);
    }

    // Mirror of HidReportBuilder.WriteBits with a sign-blind read.
    // Always reads bits in LE order matching the encoder's WriteBits.
    private static int ReadBits(byte[] buffer, int bitOffset, int bitSize)
    {
        if ((bitOffset & 7) == 0 && (bitSize & 7) == 0)
        {
            int byteIdx = bitOffset >> 3;
            int byteCnt = bitSize >> 3;
            uint v = 0;
            for (int i = 0; i < byteCnt; i++)
            {
                if ((uint)(byteIdx + i) >= (uint)buffer.Length) break;
                v |= (uint)buffer[byteIdx + i] << (8 * i);
            }
            return (int)v;
        }
        int result = 0;
        for (int b = 0; b < bitSize; b++)
        {
            int byteIdx = (bitOffset + b) >> 3;
            int bitIdx = (bitOffset + b) & 7;
            if ((uint)byteIdx < (uint)buffer.Length)
            {
                int bit = (buffer[byteIdx] >> bitIdx) & 1;
                result |= bit << b;
            }
        }
        return result;
    }
}
