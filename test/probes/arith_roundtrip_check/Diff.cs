// diff: every float in [0, 1], 1,065,353,217 of them, through each catalog
// conversion's old and new arithmetic: how many inputs change the value a
// consumer reads, by how much, and the value at 0, 0.5 and 1. Then every
// float whose scaled value lies in the int16 range through each motion
// scale. Then the per-profile list of the conversions each catalog profile
// carries. Fails when a conversion changes in a way the design note's table
// (docs/design/data-driven-devices.md, decision (a)) does not list.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static class Diff
{
    const uint OneBits = 0x3F800000;

    /// <summary>The design note's table, per conversion: inputs that change
    /// of the 1,065,353,217 floats in [0, 1], and the value at 0.5 before
    /// and after. Every change is one count, and every value at 0 and 1
    /// stays.</summary>
    static readonly Dictionary<string, (long Changed, int OldHalf, int NewHalf)> Table = new(StringComparer.Ordinal)
    {
        ["codec LT_DIGITAL / RT_DIGITAL"] = (1, 1, 1),
        ["codec int16-axis"] = (154483, 0, 0),
        ["codec int16-axis Y (negated)"] = (154483, 0, 0),
        ["codec stick12-pair"] = (2048, 2048, 2048),
        ["codec uint16-trigger"] = (16128, 16384, 16384),
        ["codec uint8-axis center 128"] = (128, 128, 128),
        ["codec uint8-axis center 128 invert"] = (799, 128, 128),
        ["codec uint8-trigger"] = (128, 128, 128),
        ["hid-descriptor[-1..1]"] = (448790528, 0, 0),
        ["hid-descriptor[-127..127]"] = (419595522, 0, 0),
        ["hid-descriptor[-128..127]"] = (419529024, 0, 0),
        ["hid-descriptor[-1800..1800]"] = (403324268, 0, 0),
        ["hid-descriptor[-2047..2047]"] = (402671615, 0, 0),
        ["hid-descriptor[-32..31]"] = (428085296, 0, 0),
        ["hid-descriptor[-32767..32767]"] = (385876990, 0, 0),
        ["hid-descriptor[-32768..32767]"] = (385876288, 0, 0),
        ["hid-descriptor[-512..511]"] = (411074564, 0, 0),
        ["hid-descriptor[0..1023]"] = (50290668, 511, 512),
        ["hid-descriptor[0..11]"] = (21352820, 5, 6),
        ["hid-descriptor[0..127]"] = (37517560, 63, 64),
        ["hid-descriptor[0..16383]"] = (67106048, 8191, 8192),
        ["hid-descriptor[0..1]"] = (8388607, 0, 0),
        ["hid-descriptor[0..2047]"] = (54503421, 1023, 1024),
        ["hid-descriptor[0..32767]"] = (71301888, 16383, 16384),
        ["hid-descriptor[0..255]"] = (41811392, 127, 128),
        ["hid-descriptor[0..2]"] = (12582911, 1, 1),
        ["hid-descriptor[0..3]"] = (13981013, 1, 2),
        ["hid-descriptor[0..4095]"] = (58706944, 2047, 2048),
        ["hid-descriptor[0..65535]"] = (75496896, 32767, 32768),
        ["hid-descriptor[0..768]"] = (47535317, 384, 384),
        ["hid-descriptor[1..255]"] = (41711863, 128, 128),
        ["hid-descriptor[1..32767]"] = (71300351, 16384, 16384),
        ["switch-pro stick X"] = (405448875, 2048, 2048),
        ["switch-pro stick Y"] = (405448875, 2048, 2048),
        ["switch2 composite ZL/ZR"] = (0, 1, 1),
        ["switch2 composite stick X"] = (0, 2048, 2048),
        ["switch2 composite stick Y"] = (0, 2048, 2048),
        ["xusb slice stick X"] = (75496896, -1, 0),
        ["xusb slice stick Y"] = (75496895, 0, 0),
        ["xusb slice trigger"] = (54128256, 127, 128),
    };

    /// <summary>The table's motion rows: changed inputs of the floats whose
    /// scaled value lies in the int16 range, both signs.</summary>
    static readonly Dictionary<string, long> ImuTable = new(StringComparer.Ordinal)
    {
        ["switch-pro gyro 13371/936"] = 32792,
        ["switch-pro accel 4096"] = 0,
        ["switch2 accel 32767/8"] = 32257,
        ["switch2 gyro (pi/180)*32767/34.8"] = 32743,
    };

    public static int Run(string? only)
    {
        if (!NewArith.Present)
        {
            Console.WriteLine("diff: this SDK has no WireArith, so there is no new arithmetic to compare. Run it on a tree with the change.");
            return 1;
        }
        Inventory.Build();
        int fail = 0;
        var changedOf = new Dictionary<string, (long Changed, int Old0, int New0, int OldHalf, int NewHalf, int Old1, int New1)>(StringComparer.Ordinal);
        Console.WriteLine($"diff: every float in [0, 1], {OneBits + 1} inputs per conversion");
        Console.WriteLine("  conversion | changed inputs | largest change | at 0 old/new | at 0.5 old/new | at 1 old/new | first changed inputs");
        foreach (var s in Inventory.SiteByKey.Values)
        {
            if (only != null && !s.Key.Contains(only, StringComparison.Ordinal)) continue;
            var sw = Stopwatch.StartNew();
            var (changed, maxDelta, firsts) = Count(s);
            var row = (changed, s.Old(0f), s.New(0f), s.Old(0.5f), s.New(0.5f), s.Old(1f), s.New(1f));
            changedOf[s.Key] = row;
            bool ok = Table.TryGetValue(s.Key, out var want)
                && want.Changed == changed && want.OldHalf == row.Item4 && want.NewHalf == row.Item5
                && row.Item2 == row.Item3 && row.Item6 == row.Item7 && maxDelta <= 1;
            if (!ok) fail++;
            string first = string.Join(" ", firsts.Select(x => $"0x{x:X8}"));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {s.Key} | {changed} | {maxDelta} | {row.Item2}/{row.Item3} | {row.Item4}/{row.Item5} | {row.Item6}/{row.Item7} | {first} ({sw.Elapsed.TotalSeconds:F1} s)");
        }
        if (only == null || only == "imu")
        {
            Console.WriteLine("diff: motion, every float whose scaled value is in the int16 range, both signs");
            foreach (var imu in ImuScale.All)
            {
                var (changed, inputs) = CountImu(imu);
                bool ok = ImuTable.TryGetValue(imu.Key, out long want) && want == changed;
                if (!ok) fail++;
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] imu {imu.Key} | {changed} of {inputs}");
            }
        }
        if (only == null)
        {
            Console.WriteLine("diff: per profile, each conversion it carries, inputs that change and the value at 0.5 old/new");
            int profiles = 0, rows = 0;
            foreach (var g in Inventory.Uses.GroupBy(u => u.Profile).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                bool any = false;
                foreach (var u in g)
                {
                    var r = changedOf[u.SiteKey];
                    if (r.Changed == 0) continue;
                    any = true;
                    rows++;
                    Console.WriteLine($"  {u.Profile} | {u.Path} | {u.SiteKey} | {u.Where} | {r.Changed} | {r.OldHalf}/{r.NewHalf}");
                }
                if (any) profiles++;
            }
            Console.WriteLine($"diff: {profiles} catalog profiles have a field that changes on some input, {rows} rows");
        }
        Console.WriteLine(fail == 0 ? "diff: PASS, every change is in the design note's table" : $"diff: FAIL, {fail} conversion(s) outside the table");
        return fail;
    }

    static (long Changed, int MaxDelta, List<uint> Firsts) Count(Site s)
    {
        long changed = 0;
        int maxDelta = 0;
        var firsts = new ConcurrentBag<uint>();
        const int chunks = 1024;
        uint per = (OneBits + 1 + chunks - 1) / chunks;
        Parallel.For(0, chunks, () => (0L, 0), (c, _, acc) =>
        {
            uint lo = (uint)c * per;
            if (lo > OneBits) return acc;
            uint hi = Math.Min(OneBits, lo + per - 1);
            for (uint b = lo; ; b++)
            {
                float v = BitConverter.UInt32BitsToSingle(b);
                int o = s.ReadBack(s.Old(v)), n = s.ReadBack(s.New(v));
                if (o != n)
                {
                    acc.Item1++;
                    int d = Math.Abs(o - n);
                    if (d > acc.Item2) acc.Item2 = d;
                    if (firsts.Count < 64) firsts.Add(b);
                }
                if (b == hi) break;
            }
            return acc;
        }, acc =>
        {
            Interlocked.Add(ref changed, acc.Item1);
            int cur;
            while ((cur = Volatile.Read(ref maxDelta)) < acc.Item2
                   && Interlocked.CompareExchange(ref maxDelta, acc.Item2, cur) != cur) { }
        });
        return (changed, maxDelta, firsts.OrderBy(x => x).Take(3).ToList());
    }

    static (long Changed, long Inputs) CountImu(ImuScale s)
    {
        float limit = (float)(32768.5 / s.Scale);
        uint top = BitConverter.SingleToUInt32Bits(limit);
        long changed = 0, inputs = 0;
        const int chunks = 1024;
        uint per = (top + 1 + chunks - 1) / chunks;
        Parallel.For(0, chunks, () => (0L, 0L), (c, _, acc) =>
        {
            uint lo = (uint)c * per;
            if (lo > top) return acc;
            uint hi = Math.Min(top, lo + per - 1);
            for (uint b = lo; ; b++)
            {
                float v = BitConverter.UInt32BitsToSingle(b);
                acc.Item2 += b == 0 ? 1 : 2;
                if (Old.Imu(v, s.OldScale) != NewArith.Scaled(v, s.Numerator, s.Denominator)) acc.Item1++;
                if (b != 0 && Old.Imu(-v, s.OldScale) != NewArith.Scaled(-v, s.Numerator, s.Denominator)) acc.Item1++;
                if (b == hi) break;
            }
            return acc;
        }, acc => { Interlocked.Add(ref changed, acc.Item1); Interlocked.Add(ref inputs, acc.Item2); });
        return (changed, inputs);
    }
}
