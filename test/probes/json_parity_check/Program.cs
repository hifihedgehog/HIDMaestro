// Source-generated JSON reads the catalog into the same objects reflection
// did. v1.13.0 moved every profile read from reflection-based
// System.Text.Json to HMJsonContext, and both loaders drop a profile that
// fails to parse without a word, so a type or enum the context missed would
// shrink the catalog silently. This probe rebuilds the v1.12.0 reflection
// options, reads every embedded profile both ways, and requires the same
// profiles with the same contents:
//
//   A. Every embedded profile parses both ways, with the same id.
//   B. Written back out with the reflection options, each pair is identical,
//      so every property of every profile and layout matches.
//   C. A layout kind still matches in any letter case and with or without
//      underscores, as it did, and an unknown kind is still refused.
//   D. HMLayoutJsonOptions.Default is the source-generated options.
//   E. A key the JSON leaves out keeps the record's initializer, in nested
//      records too: a trigger's Stages stays 1 and a rotary encoder's Role
//      stays Dial.
//
// --time <runs> starts this program <runs> times per mode and reports the
// full-catalog parse time, p50 and p95, for the first parse in a fresh
// process, which is what a consumer's startup pays, and for a second parse
// in the same process, once the code has been compiled. No device is touched.
// Exit 0 PASS, 1 FAIL.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
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

    /// <summary>The options HMLayoutJsonOptions.Default carried through
    /// v1.12.0: reflection resolver, the same flags and the same converters,
    /// with the layout converter as v1.12.0 shipped it.</summary>
    static JsonSerializerOptions ReflectionOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        opts.Converters.Add(new HMAxisJsonConverter());
        opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        opts.Converters.Add(new V112LayoutConverter());
        return opts;
    }

    static (string Name, string Json)[] Resources()
    {
        var asm = typeof(HMContext).Assembly;
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => { using var r = new StreamReader(asm.GetManifestResourceStream(n)!); return (n, r.ReadToEnd()); })
            .ToArray();
    }

    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--once") return Once(args[1]);
        if (args.Length == 2 && args[0] == "--time") return Time(int.Parse(args[1]));

        Console.WriteLine("=== Profiles read through source-generated JSON ===");
        var reflection = ReflectionOptions();
        var resources = Resources();

        // A and B.
        int both = 0;
        var mismatches = new List<string>();
        foreach (var (name, json) in resources)
        {
            ControllerProfile? a = null, b = null;
            string? errA = null, errB = null;
            try { a = JsonSerializer.Deserialize<ControllerProfile>(json, reflection); } catch (Exception ex) { errA = ex.Message; }
            try { b = JsonSerializer.Deserialize(json, HMJsonContext.Default.ControllerProfile); } catch (Exception ex) { errB = ex.Message; }
            if (a == null || b == null || string.IsNullOrEmpty(a.Id) || a.Id != b.Id)
            {
                mismatches.Add($"{name}: reflection {(a == null ? "failed " + errA : a.Id)}, generated {(b == null ? "failed " + errB : b.Id)}");
                continue;
            }
            both++;
            string ja = JsonSerializer.Serialize(a, reflection), jb = JsonSerializer.Serialize(b, reflection);
            if (ja != jb)
            {
                int at = FirstDiff(ja, jb);
                string Around(string t) => t.Substring(Math.Max(0, at - 60), Math.Min(140, t.Length - Math.Max(0, at - 60)));
                mismatches.Add($"{name}: contents differ at char {at}");
                if (Environment.GetEnvironmentVariable("PARITY_DIFF") == "1")
                {
                    Console.WriteLine($"    {name}");
                    Console.WriteLine($"      reflection: {Around(ja)}");
                    Console.WriteLine($"      generated:  {Around(jb)}");
                }
            }
        }
        Check("A: every embedded profile parses both ways, with the same id", both == resources.Length && resources.Length > 0,
              $"{both} of {resources.Length}");
        Check("B: every profile's contents match", mismatches.Count == 0, string.Join("; ", mismatches.Take(5)));

        // C.
        foreach (string kind in new[] { "flight_stick", "FlightStick", "flightstick", "FLIGHT_STICK" })
        {
            string json = $"{{\"kind\":\"{kind}\"}}";
            HMLayout? viaReflection = null, viaContext = null;
            try { viaReflection = JsonSerializer.Deserialize<HMLayout>(json, reflection); } catch { }
            try { viaContext = JsonSerializer.Deserialize(json, HMJsonContext.Default.HMLayout); } catch { }
            Check($"C: kind \"{kind}\" reads as a flight stick both ways",
                  viaReflection is HMFlightStickLayout && viaContext is HMFlightStickLayout);
        }
        bool refused;
        try { JsonSerializer.Deserialize("{\"kind\":\"spaceship\"}", HMJsonContext.Default.HMLayout); refused = false; }
        catch (JsonException) { refused = true; }
        Check("C: an unknown kind is refused", refused);

        // E. A key the JSON leaves out keeps the record's initializer, nested
        // records included, as reflection kept it. The catalog may never
        // omit these two, so they are checked here directly.
        const string joystick = "{\"kind\":\"joystick\",\"trigger\":{\"buttonIndex\":0}}";
        const string wheel = "{\"kind\":\"wheel\",\"rotaryEncoders\":[{\"axis\":\"Dial\"}]}";
        var j = JsonSerializer.Deserialize(joystick, HMJsonContext.Default.HMLayout) as HMJoystickLayout;
        var jOld = JsonSerializer.Deserialize<HMLayout>(joystick, reflection) as HMJoystickLayout;
        Check("E: a trigger without \"stages\" has Stages 1, as before",
              j?.Trigger?.Stages == 1 && jOld?.Trigger?.Stages == 1, $"{j?.Trigger?.Stages} and {jOld?.Trigger?.Stages}");
        Check("E: and its lists are empty, not null",
              j != null && j.StickButtons != null && j.StickButtons.Count == 0 && j.Hats != null);
        var w = JsonSerializer.Deserialize(wheel, HMJsonContext.Default.HMLayout) as HMWheelLayout;
        var wOld = JsonSerializer.Deserialize<HMLayout>(wheel, reflection) as HMWheelLayout;
        Check("E: a rotary encoder without \"role\" has Role Dial, as before",
              w?.RotaryEncoders.Count == 1 && w.RotaryEncoders[0].Role == HMAxisRole.Dial
              && wOld?.RotaryEncoders[0].Role == HMAxisRole.Dial,
              $"{(w?.RotaryEncoders.Count > 0 ? w.RotaryEncoders[0].Role : "none")}");
        Check("E: a wheel without \"wheel\" gets a new HMWheelSpec, as before", w?.Wheel != null && wOld?.Wheel != null);

        // D.
        Check("D: HMLayoutJsonOptions.Default is the source-generated options",
              ReferenceEquals(HMLayoutJsonOptions.Default, HMJsonContext.Default.Options)
              && HMLayoutJsonOptions.Default.TypeInfoResolver is HMJsonContext);

        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static int FirstDiff(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return n;
    }

    /// <summary>One cold parse of the whole catalog, the way the SDK's own
    /// loader does it: in parallel, one profile per task. Prints the
    /// milliseconds and the count.</summary>
    static int Once(string mode)
    {
        var resources = Resources();
        var reflection = mode == "reflection" ? ReflectionOptions() : null;
        double Parse()
        {
            var sw = Stopwatch.StartNew();
            var parsed = new ConcurrentBag<ControllerProfile>();
            Parallel.ForEach(resources, r =>
            {
                var p = reflection != null
                    ? JsonSerializer.Deserialize<ControllerProfile>(r.Json, reflection)
                    : JsonSerializer.Deserialize(r.Json, HMJsonContext.Default.ControllerProfile);
                if (p != null) parsed.Add(p);
            });
            if (parsed.Count != resources.Length) throw new InvalidOperationException($"{parsed.Count} of {resources.Length} parsed");
            return sw.Elapsed.TotalMilliseconds;
        }
        double cold = Parse();
        double warm = Parse();
        Console.WriteLine(FormattableString.Invariant($"{cold:F2} {warm:F2}"));
        return 0;
    }

    static int Time(int runs)
    {
        string self = Environment.ProcessPath!;
        foreach (string mode in new[] { "reflection", "generated" })
        {
            var cold = new List<double>();
            var warm = new List<double>();
            for (int i = 0; i < runs; i++)
            {
                var psi = new ProcessStartInfo(self) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                psi.ArgumentList.Add("--once");
                psi.ArgumentList.Add(mode);
                using var p = Process.Start(psi)!;
                string[] f = p.StandardOutput.ReadToEnd().Trim().Split(' ');
                p.WaitForExit();
                cold.Add(double.Parse(f[0], System.Globalization.CultureInfo.InvariantCulture));
                warm.Add(double.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture));
            }
            Console.WriteLine($"  {mode}: full-catalog parse over {runs} processes. First in the process p50 {Pct(cold, 0.50):F1} ms, p95 {Pct(cold, 0.95):F1} ms. Second p50 {Pct(warm, 0.50):F1} ms, p95 {Pct(warm, 0.95):F1} ms");
        }
        return 0;
    }

    static double Pct(List<double> values, double q)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[(int)Math.Ceiling(q * sorted.Count) - 1];
    }
}
