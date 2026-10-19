// rounding: WireArith's three rounding forms, Linear, Unit and
// CenteredUnit, against Math.Round and a conversion, the formula the design
// note writes. The SDK rounds by adding 1.5 x 2^52, which IEEE 754's default
// rounding turns into round half to even, and reads the integer from the
// sum's low bits. This proves the two give the same integer on every input
// the SDK can hand them: a grid of 2^20 + 1 values in [0, 1], every tie
// beside every raw value of each span with its float and double neighbors,
// and for Linear out-of-range, infinite and NaN inputs, a NaN at both rests. Bound by name, so the probe
// builds on a tree without the forms, where this section reports them
// absent and fails.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HIDMaestro;

internal static class Rounding
{
    const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly Type? s_arith = typeof(HMController).Assembly.GetType("HIDMaestro.Internal.WireArith");
    static readonly Func<double, int, int, bool, double, int>? s_linear =
        s_arith?.GetMethod("Linear", Any)?.CreateDelegate<Func<double, int, int, bool, double, int>>();
    static readonly Func<double, double, int>? s_unit =
        s_arith?.GetMethod("Unit", Any)?.CreateDelegate<Func<double, double, int>>();
    static readonly Func<double, int, double, double, int>? s_centeredUnit =
        s_arith?.GetMethod("CenteredUnit", Any)?.CreateDelegate<Func<double, int, double, double, int>>();
    static readonly Func<float, bool>? s_triggerOn =
        typeof(HMController).Assembly.GetType("HIDMaestro.Internal.VendorBlobCodec")?.GetMethod("TriggerOn", Any)
            ?.CreateDelegate<Func<float, bool>>();

    static int RefLinear(double v, int min, int max, bool invert, double rest)
    {
        if (double.IsNaN(v)) v = rest;
        double c = Math.Clamp(v, 0.0, 1.0);
        if (invert) c = 1.0 - c;
        return (int)(min + (long)Math.Round(c * ((long)max - min)));
    }

    /// <summary>The inputs for a span: the grid, then each tie (r + 0.5) /
    /// span with the floats and doubles on either side of it.</summary>
    static IEnumerable<double> Inputs(long span)
    {
        const int grid = 1 << 20;
        for (int i = 0; i <= grid; i++) yield return (double)i / grid;
        long ties = Math.Min(span, 70_000);
        for (long r = 0; r < ties; r++)
        {
            long at = span <= 70_000 ? r : r * (span / ties);
            double t = (at + 0.5) / span;
            float f = (float)t;
            yield return t;
            yield return Math.BitDecrement(t);
            yield return Math.BitIncrement(t);
            yield return f;
            yield return MathF.BitDecrement(f);
            yield return MathF.BitIncrement(f);
        }
    }

    public static int Run()
    {
        Console.WriteLine("rounding: WireArith's biased rounding against Math.Round, the design note's formula");
        if (s_linear == null || s_unit == null || s_centeredUnit == null)
        {
            Console.WriteLine("  [FAIL] WireArith.Linear, Unit or CenteredUnit is absent on this tree");
            return 1;
        }
        // Each case is independent of the others, so the cases run at once
        // and report in the order they are listed.
        var cases = new List<(string What, Func<(long N, long Bad, string First)> Run)>();
        foreach (var (min, max) in new[] { (0, 255), (0, 1023), (0, 4095), (0x200, 0xE00), (-32768, 32767), (-32767, 32767),
                                           (0, 65535), (0, 32767), (-127, 127), (int.MinValue, int.MaxValue) })
            foreach (bool invert in new[] { false, true })
                cases.Add(($"Linear {min}..{max}{(invert ? " invert" : "")}", () =>
                {
                    long n = 0, bad = 0;
                    string first = "";
                    var extra = new[] { -1.0, -0.0, 2.0, double.NegativeInfinity, double.PositiveInfinity, double.NaN, double.Epsilon, 1 - 1e-17 };
                    foreach (var v in Concat(Inputs((long)max - min), extra))
                    {
                        n++;
                        // A NaN takes the rest it is given: a stick's and a trigger's.
                        foreach (double rest in double.IsNaN(v) ? new[] { 0.5, 0.0 } : new[] { 0.5 })
                        {
                            int got = s_linear(v, min, max, invert, rest), want = RefLinear(v, min, max, invert, rest);
                            if (got != want && bad++ == 0) first = $"v {v:R} rest {rest}: {got}, Math.Round {want}";
                        }
                    }
                    return (n, bad, first);
                }));

        foreach (long span in new long[] { 255, 1023, 65535 })
            cases.Add(($"Unit span {span}", () =>
            {
                long n = 0, bad = 0;
                string first = "";
                // Unit's callers hand it no NaN: each replaced it by its rest.
                foreach (var v in Inputs(span))
                {
                    n++;
                    int got = s_unit(v, span), want = (int)Math.Round(v * span);
                    if (got != want && bad++ == 0) first = $"v {v:R}: {got}, Math.Round {want}";
                }
                return (n, bad, first);
            }));

        // Luna's two centered fields and two lopsided ones, where t x sL is
        // negative below the center.
        foreach (var (min, center, max) in new[] { (0, 127, 255), (0, 32767, 65535), (-100, 0, 27), (0, 3, 4095) })
            cases.Add(($"CenteredUnit {min}..{max} center {center}", () =>
            {
                double low = 2.0 * ((long)center - min), step = 2.0 * ((long)max - center) - low;
                long n = 0, bad = 0;
                string first = "";
                foreach (var v in Inputs((long)max - min))
                {
                    n++;
                    double t = v - 0.5;
                    int got = s_centeredUnit(v, center, low, step);
                    int want = center + (int)Math.Round(t * low + Math.Max(t, 0.0) * step);
                    if (got != want && bad++ == 0) first = $"v {v:R}: {got}, Math.Round {want}";
                }
                return (n, bad, first);
            }));

        // The codec's LT and RT digital bits: one compare, c x 255 > 0.5,
        // against Linear's byte above zero, on every float from 0 to 2 / 255
        // and the specials. The floats run in chunks across the processors,
        // and the first input that differs is the smallest of the chunks'.
        if (s_triggerOn != null)
            cases.Add(("TriggerOn against Linear(v, 0, 255) > 0", () =>
            {
                uint top = BitConverter.SingleToUInt32Bits(2f / 255);
                long n = 0, bad = 0;
                uint firstBits = uint.MaxValue;
                const int chunks = 1024;
                uint per = (top + chunks) / chunks;
                object gate = new();
                Parallel.For(0, chunks, c =>
                {
                    uint lo = (uint)c * per;
                    if (lo > top) return;
                    uint hi = Math.Min(top, lo + per - 1);
                    long cn = 0, cbad = 0;
                    uint cfirst = uint.MaxValue;
                    for (uint bits = lo; ; bits++)
                    {
                        float v = BitConverter.UInt32BitsToSingle(bits);
                        cn++;
                        if (s_triggerOn(v) != (s_linear(v, 0, 255, false, 0.0) > 0) && cbad++ == 0) cfirst = bits;
                        if (bits == hi) break;
                    }
                    lock (gate) { n += cn; bad += cbad; firstBits = Math.Min(firstBits, cfirst); }
                });
                string first = bad == 0 ? "" : $"v {BitConverter.UInt32BitsToSingle(firstBits):R}";
                var specials = new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f, -0f, 0f, 1f, 2f, float.Epsilon };
                foreach (float v in specials)
                {
                    n++;
                    if (s_triggerOn(v) != (s_linear(v, 0, 255, false, 0.0) > 0) && bad++ == 0) first = $"v {v:R}";
                }
                return (n, bad, first);
            }));

        var results = new (long N, long Bad, string First)[cases.Count];
        Parallel.For(0, cases.Count, i => results[i] = cases[i].Run());

        int fail = 0;
        for (int i = 0; i < cases.Count; i++)
        {
            var (n, bad, first) = results[i];
            if (bad != 0) fail++;
            Console.WriteLine($"  [{(bad == 0 ? "PASS" : "FAIL")}] {cases[i].What}: {n} inputs, {bad} differ{(bad == 0 ? "" : $", first {first}")}");
        }
        if (s_triggerOn == null)
        {
            Console.WriteLine("  [FAIL] VendorBlobCodec.TriggerOn is absent on this tree");
            fail++;
        }
        return fail;
    }

    static IEnumerable<double> Concat(IEnumerable<double> a, IEnumerable<double> b)
    {
        foreach (var x in a) yield return x;
        foreach (var x in b) yield return x;
    }
}
