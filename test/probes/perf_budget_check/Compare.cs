// compare <before> <after>: the budgets' non-inferiority rule
// (docs/design/data-driven-devices.md, "Budgets"): a conversion's path is
// at or under its baseline when the median of the conversion's per-run
// values is at or under the 90th percentile of the baseline's.
//
// Each side is a file or a directory of files, read for two kinds of line:
//   kind,name...,ns[,bytes] one variant's median in one launch, as the
//                           offline modes (report, program, encode) print
//                           it. A side with 30 launches holds 30 per
//                           variant, and those are its per-run values
//   label: unit: p50 a, p95 b, max c (n samples, r runs)
//                           a live mode's summary (Report), whose per-run
//                           values are gone. Its p50 is the median, but its
//                           p90 is not recorded
// A variant is the line's text before its value. With per-run values on
// both sides the rule is applied as written. With a summary for the
// baseline it is bounded: under the baseline's p50 passes, over its p95
// fails, and between them the run is inconclusive, since p90 lies there
// and is not known. A conversion that allocates where its baseline did not
// fails, as the budget table asks 0 bytes of the hot paths. Percentiles
// are nearest-rank, as Report prints them.
//
// Exit 0 when nothing fails, 1 when a variant fails or a side holds no
// variant.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

internal static class BudgetCompare
{
    internal sealed class Side
    {
        public readonly List<double> Runs = new();
        public double MaxBytes;
        public (double P50, double P95, double Max, int Samples, int RunCount)? Summary;
    }

    internal enum Verdict { Pass, Fail, Inconclusive, NoBaseline, NotMeasured }

    // The key is the shortest run before the value, so a name may hold
    // digits. The bytes are absent where a mode does not count them.
    static readonly Regex s_csv = new(@"^(?<key>[A-Za-z][\w-]*,.+?),(?<ns>-?\d+(?:\.\d+)?)(?:,(?<bytes>\d+(?:\.\d+)?))?\s*$", RegexOptions.CultureInvariant);
    static readonly Regex s_summary = new(@"^\s*(?<key>.+?): p50 (?<p50>[\d.]+), p95 (?<p95>[\d.]+), max (?<max>[\d.]+) \((?<n>\d+) samples, (?<r>\d+) runs\)", RegexOptions.CultureInvariant);

    static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>Nearest rank, as Report prints p50 and p95.</summary>
    internal static double Rank(IReadOnlyList<double> sorted, double q) =>
        sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];

    internal static Dictionary<string, Side> Read(IEnumerable<string> lines)
    {
        var sides = new Dictionary<string, Side>(StringComparer.Ordinal);
        Side Get(string key) => sides.TryGetValue(key, out var s) ? s : sides[key] = new Side();
        foreach (string line in lines)
        {
            var m = s_csv.Match(line);
            if (m.Success)
            {
                var s = Get(m.Groups["key"].Value);
                s.Runs.Add(D(m.Groups["ns"].Value));
                if (m.Groups["bytes"].Success) s.MaxBytes = Math.Max(s.MaxBytes, D(m.Groups["bytes"].Value));
                continue;
            }
            m = s_summary.Match(line);
            if (m.Success)
                Get(m.Groups["key"].Value).Summary = (D(m.Groups["p50"].Value), D(m.Groups["p95"].Value), D(m.Groups["max"].Value),
                                                      int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture),
                                                      int.Parse(m.Groups["r"].Value, CultureInfo.InvariantCulture));
        }
        return sides;
    }

    static IEnumerable<string> Lines(string path) =>
        Directory.Exists(path)
            ? Directory.EnumerateFiles(path).OrderBy(f => f, StringComparer.Ordinal).SelectMany(File.ReadLines)
            : File.ReadLines(path);

    /// <summary>The rule for one variant, and what it compared.</summary>
    internal static (Verdict Verdict, string Detail) Judge(Side? before, Side? after)
    {
        if (before == null || before.Runs.Count == 0 && before.Summary == null) return (Verdict.NoBaseline, "no baseline");
        if (after == null || after.Runs.Count == 0 && after.Summary == null) return (Verdict.NotMeasured, "not measured after");
        // The conversion's median: of its per-run values, or a summary's p50.
        double median = after.Runs.Count > 0 ? Rank(after.Runs.OrderBy(x => x).ToList(), 0.50) : after.Summary!.Value.P50;
        string afterText = after.Runs.Count > 0
            ? F($"after p50 {median:F3}, p95 {Rank(after.Runs.OrderBy(x => x).ToList(), 0.95):F3} over {after.Runs.Count} runs")
            : F($"after p50 {median:F3}, p95 {after.Summary!.Value.P95:F3} ({after.Summary.Value.Samples} samples)");
        if (after.MaxBytes > 0 && before.MaxBytes == 0)
            return (Verdict.Fail, F($"{afterText}: allocates up to {after.MaxBytes:F0} bytes per call, the baseline 0"));
        if (before.Runs.Count > 0)
        {
            var b = before.Runs.OrderBy(x => x).ToList();
            double p90 = Rank(b, 0.90);
            string text = F($"before p50 {Rank(b, 0.50):F3}, p90 {p90:F3}, p95 {Rank(b, 0.95):F3} over {b.Count} runs; {afterText}");
            return (median <= p90 ? Verdict.Pass : Verdict.Fail, text);
        }
        var s = before.Summary!.Value;
        string bounds = F($"before p50 {s.P50:F3}, p95 {s.P95:F3} ({s.Samples} samples, p90 not recorded); {afterText}");
        if (median <= s.P50) return (Verdict.Pass, bounds);
        if (median > s.P95) return (Verdict.Fail, bounds);
        return (Verdict.Inconclusive, bounds + ", between the baseline's p50 and p95");
    }

    static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public static int Run(string beforePath, string afterPath)
    {
        Console.WriteLine($"=== budgets: {afterPath} against {beforePath}, the median after at or under the baseline's 90th percentile ===");
        var before = Read(Lines(beforePath));
        var after = Read(Lines(afterPath));
        if (before.Count == 0 || after.Count == 0)
        {
            Console.WriteLine($"  [FAIL] {(before.Count == 0 ? beforePath : afterPath)} holds no measurement");
            return 1;
        }
        var counts = new Dictionary<Verdict, int>();
        foreach (string key in before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var (verdict, detail) = Judge(before.GetValueOrDefault(key), after.GetValueOrDefault(key));
            counts[verdict] = counts.GetValueOrDefault(verdict) + 1;
            string tag = verdict switch
            {
                Verdict.Pass => "PASS", Verdict.Fail => "FAIL", Verdict.Inconclusive => "OPEN",
                Verdict.NoBaseline => "NEW ", _ => "GONE",
            };
            Console.WriteLine($"  [{tag}] {key}: {detail}");
        }
        Console.WriteLine($"compare: {counts.GetValueOrDefault(Verdict.Pass)} pass, {counts.GetValueOrDefault(Verdict.Fail)} fail, "
                          + $"{counts.GetValueOrDefault(Verdict.Inconclusive)} open, {counts.GetValueOrDefault(Verdict.NoBaseline)} with no baseline, "
                          + $"{counts.GetValueOrDefault(Verdict.NotMeasured)} not measured after");
        return counts.GetValueOrDefault(Verdict.Fail) > 0 ? 1 : 0;
    }

    /// <summary>The rule on values whose verdict is known.</summary>
    internal static IEnumerable<(string Name, bool Ok, string Detail)> SelfTest()
    {
        // Per-run values 1 to 30: p50 15, p90 27 (the 27th), p95 29.
        var lines = Enumerable.Range(1, 30).Select(i => $"report,SubmitState,fx,{i}.000,0").ToList();
        var before = Read(lines)["report,SubmitState,fx"];
        Side Runs(double median, double bytes = 0)
        {
            var s = new Side();
            for (int i = 0; i < 14; i++) s.Runs.Add(median - 1);
            s.Runs.Add(median);
            for (int i = 0; i < 15; i++) s.Runs.Add(median + 1);
            s.MaxBytes = bytes;
            return s;
        }
        yield return ("the baseline's per-run values give p50 15, p90 27, p95 29",
                      Rank(before.Runs.OrderBy(x => x).ToList(), 0.5) == 15 && Rank(before.Runs.OrderBy(x => x).ToList(), 0.9) == 27
                      && Rank(before.Runs.OrderBy(x => x).ToList(), 0.95) == 29, "");
        var atP90 = Judge(before, Runs(27));
        yield return ("a median at the baseline's p90 passes", atP90.Verdict == Verdict.Pass, atP90.Detail);
        var over = Judge(before, Runs(27.001));
        yield return ("a median over the baseline's p90 fails", over.Verdict == Verdict.Fail, over.Detail);
        var allocates = Judge(before, Runs(10, bytes: 24));
        var noBytes = Read(new[] { "encode,fx,codec,12.5", "program,s2-report-09,29.450,0" });
        yield return ("a line without bytes, and a name that holds digits, are read",
                      noBytes.TryGetValue("encode,fx,codec", out var e) && e.Runs.SequenceEqual(new[] { 12.5 })
                      && noBytes.TryGetValue("program,s2-report-09", out var r) && r.Runs.SequenceEqual(new[] { 29.45 }), string.Join(" | ", noBytes.Keys));
        yield return ("a conversion that allocates where the baseline did not fails", allocates.Verdict == Verdict.Fail, allocates.Detail);
        var summary = Read(new[] { "  SubmitState, fx: ns per call: p50 15.000, p95 29.000, max 31.000 (30 samples, 30 runs), most bytes allocated in one run of 20000 calls 0" });
        var b2 = summary["SubmitState, fx: ns per call"];
        yield return ("a live summary is read", b2.Summary is { P50: 15, P95: 29, Samples: 30 }, "");
        var under = Judge(b2, Runs(15));
        yield return ("against a summary, a median at its p50 passes", under.Verdict == Verdict.Pass, under.Detail);
        var between = Judge(b2, Runs(20));
        yield return ("against a summary, a median between its p50 and p95 is open", between.Verdict == Verdict.Inconclusive, between.Detail);
        var above = Judge(b2, Runs(29.5));
        yield return ("against a summary, a median over its p95 fails", above.Verdict == Verdict.Fail, above.Detail);
        var none = Judge(null, Runs(1));
        yield return ("a variant with no baseline is listed apart", none.Verdict == Verdict.NoBaseline, none.Detail);
    }
}
