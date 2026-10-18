// HIDMaestroProfileCheck: a profile file checked as the SDK loads it, and
// its programs measured on the interpreter.
//
//   HIDMaestroProfileCheck <file or directory> ...
//   HIDMaestroProfileCheck --cost [--launches N] [--runs N] [--reports N]
//                          [--register name=value ...] <file or directory> ...
//
// The check loads the catalog first, as a program that calls
// LoadDefaultProfiles does, then each path through
// HMContext.LoadProfilesFromDirectory: a directory as given, and a file
// through the directory that holds it, since the SDK loads directories. It
// prints every issue of a named file, and of a model file in its
// directory's models folder, as "<file> at <JSON path>: <reason>", whether
// the load refused the file or loaded it with a model problem, and each
// profile a named file loaded with where each group of its device model
// came from (HMProfile.DeviceModelSource).
//
// --cost measures each program of each profile a named file loaded: the
// layouts of inputReports, liveFeatureReports, a report a protocol rule
// emits, the protocol's decoders and the state block's program, all on the
// interpreter, as a profile from outside the catalog runs them. It is the
// method the design note's interpreter table was measured by (InterpreterCost.cs):
// N launches (30), each the median of 10 runs of 100,000 reports, the
// programs interleaved run by run, at high priority. It prints p50 and p95
// across launches per report, each program's compile and the whole model's,
// with the runtime, CPU, instruction set and tiering they were measured
// under. Registers start at the protocol's initial values, so a block a
// host's command turns on runs only with --register setting it, as
// featureEnabled=0x04 turns on the Switch 2 Pro's motion in report 0x05.
//
// Exit 0 when every named file loaded with no issue, 1 when a load refused a
// file or listed a problem, or a measurement failed, 2 for an argument it
// cannot use.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HIDMaestro;

namespace HIDMaestro.ProfileCheck;

internal static class Program
{
    static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length > 0 && args[0] == InterpreterCost.LaunchArg) return InterpreterCost.Launch(args[1..]);

        bool cost = false;
        int launches = 30, runs = 10, reports = 100_000;
        var paths = new List<string>();
        var registers = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--cost": cost = true; break;
                case "--launches" when i + 1 < args.Length && int.TryParse(args[i + 1], out int l) && l > 0: launches = l; i++; break;
                case "--runs" when i + 1 < args.Length && int.TryParse(args[i + 1], out int r) && r > 0: runs = r; i++; break;
                case "--reports" when i + 1 < args.Length && int.TryParse(args[i + 1], out int n) && n > 0: reports = n; i++; break;
                case "--register" when i + 1 < args.Length:
                    InterpreterCost.ParseRegisters(args[i + 1], out string? bad);
                    if (bad != null) return Usage($"--register {bad}");
                    registers.Add(args[++i]);
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) return Usage($"{args[i]} is not an option, or its value is missing or not a positive number");
                    paths.Add(Path.GetFullPath(args[i]));
                    break;
            }
        }
        if (paths.Count == 0) return Usage("name a profile file or a directory of them");
        foreach (string p in paths)
        {
            if (Directory.Exists(p)) continue;
            if (!File.Exists(p)) return Usage($"{p} does not exist");
            if (!p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return Usage($"{p} is not a .json file, and the SDK loads only those");
        }

        Console.WriteLine($"HIDMaestroProfileCheck, HIDMaestro SDK {typeof(HMContext).Assembly.GetName().Version}");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        // Each directory loads once, however many of its files are named.
        var dirs = paths.Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p)!)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string dir in dirs) ctx.LoadProfilesFromDirectory(dir);

        var issues = ctx.ProfileLoadIssues;
        var profiles = ctx.AllProfiles;
        int problems = 0;
        var measured = new List<(string Dir, string Id)>();
        foreach (string path in paths)
        {
            bool isDir = Directory.Exists(path);
            string dir = isDir ? path : Path.GetDirectoryName(path)!;
            string models = Path.Combine(dir, "models") + Path.DirectorySeparatorChar;
            bool Named(string? file) => file != null && (isDir
                ? file.StartsWith(path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                : string.Equals(file, path, StringComparison.OrdinalIgnoreCase) || file.StartsWith(models, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine(path);
            var mine = issues.Where(i => Named(i.File)).ToList();
            foreach (var i in mine)
            {
                problems++;
                string what = i.Kind switch
                {
                    HMProfileLoadIssueKind.Refused => "refused",
                    HMProfileLoadIssueKind.NotAProfile => "not a profile",
                    _ => "loaded with a model problem",
                };
                Console.WriteLine($"  {what}: {i}");
            }
            var loaded = profiles.Where(p => Named(p.Inner.SourceFile)).OrderBy(p => p.Inner.SourceFile, StringComparer.Ordinal).ToList();
            foreach (var p in loaded)
            {
                string groups = string.Join(", ", p.DeviceModelSource.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}"));
                Console.WriteLine($"  loaded {p.Id}{(isDir ? $" from {Path.GetRelativePath(path, p.Inner.SourceFile!)}" : "")}, device model: {(groups.Length == 0 ? "none" : groups)}");
                measured.Add((dir, p.Id));
            }
            if (mine.Count == 0 && loaded.Count == 0) Console.WriteLine("  no profile file here");
        }
        Console.WriteLine(problems == 0 ? "load: no issue" : $"load: {problems} issue{(problems == 1 ? "" : "s")}");

        int costFailed = 0;
        if (cost) costFailed = InterpreterCost.Measure(measured, launches, runs, reports, string.Join(",", registers));
        return problems > 0 || costFailed > 0 ? 1 : 0;
    }

    static int Usage(string why)
    {
        Console.WriteLine($"HIDMaestroProfileCheck: {why}.");
        Console.WriteLine("usage: HIDMaestroProfileCheck [--cost [--launches N] [--runs N] [--reports N] [--register name=value ...]] <profile file or directory> ...");
        return 2;
    }
}
