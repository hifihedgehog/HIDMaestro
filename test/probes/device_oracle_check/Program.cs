// device_oracle_check: what the device code of 9bd6279 does, recorded so
// every later change proves its bytes identical by replay.
//
// The conversion of each device shortcut to profile data (design note
// docs/design/data-driven-devices.md) replaces C# and C written for one
// device with general mechanisms. Today's code is the oracle. This probe
// records its outputs for scripted and seeded random inputs into golden
// files, and replays the same inputs through the tree's code to compare.
//
// Cases:
//   case1-submit          SubmitState through the codec, the descriptor path
//                         and the XUSB slice, on every catalog profile whose
//                         bytes the arithmetic change touches
//   case2-packers         the Switch Pro and Switch 2 Pro bodies and rumble
//                         decodes
//   case3-switch2-device  the Switch 2 Pro composite's command responder and
//                         report clock
//   case4-usbip-features  feature answers on the USB/IP backend
//   case5-driver          the UMDF2 driver and the XUSB companion, compiled
//                         natively against stand-ins for WDF and Win32
//                         (native\build.ps1): the 9bd6279 sources in
//                         native\oracle record, the tree's sources replay.
//                         Also under each version-section parameter, the
//                         identity tag's among them, with the values a
//                         killed v2.0.0 program leaves under a v1
//                         controller's index (LeftValues.cs), and with
//                         extended-lane frames at every length
//   case6-valve-input-path  the Valve personas' input reports end to end on
//                         USB/IP, with SDL's IMU writes and idle repeats
//   case7-output-decode   OutputReceived, OutputDecoded with each field's
//                         boxed type, and the ports' OutputChanged, through
//                         the controller's own output thread
//   case8-usbip-paths     the Switch 2 Pro composite through a controller with
//                         a USB/IP handle, and raw submits on four profiles
//   case9-physical-corpus  the physical descriptors and identities of the
//                         reference clones, built as HMDeviceExtractor builds
//                         them, through the SDK and the native harness. Read
//                         from the clones (--corpus-root), skipped without
//                         them
//
// Usage:
//   record [--case <name,...>] [--seed <n>] [--golden <dir>] [--native <dir>]
//          [--corpus-root <dir>]
//   replay [--case <name,...>] [--seed <n>] [--golden <dir>] [--native <dir>]
//          [--corpus-root <dir>]   the folder holding case 9's clones
//          [--skip-corpus]          skip case 9 when its clones are not here
//          [--changes <file>]      intended changes beside changes.txt
//                                   (ChangeList.cs)
//          [--plan-change <file>]   instance configuration the tree's SDK
//                                   writes beyond the kept plan (Case5Driver.cs)
//   either mode: [--dump <dir>]   also writes every output whole to
//                                   <dir>\<case>.bin (GoldenCase.DumpDir)
//   replay [--explain <dir> --lane <lane>]
//                                 also holds every output against a base
//                                   tree's dump in <dir>, and recomputes each
//                                   by the lane's rules (Explain.cs)
//          [--write-changes <file>] writes the change list this replay
//                                   would pass with: every stream that
//                                   differs from its golden file pinned to
//                                   its count and digest, with --explain's
//                                   categories. A new entry's lane, or its
//                                   reason, is "?" until filled in
//          [--profile-patch <file>] edits catalog profiles for this run
//                                   (ProfilePatch.cs), for a mutation the
//                                   list and --explain must catch
//   selftest                      the change list's own rules (SelfTest.cs)
//   compare <dirA> <dirB>
//   compare-live <out dir> | --selftest <dir> [--golden <dir>] [--native <dir>]
//                         the live run against the native harness
//                         (CompareLive.cs), exit 2 when a step is missing
// The golden directory defaults to golden\ beside this source. Every run
// prints its seed. Creates no device and needs no elevation. Exit 0 PASS,
// 1 FAIL, 2 when replay found no golden files beside this source (a staged
// tree), or skipped case 9 under --skip-corpus, and nothing failed. A
// replay that has the golden files and not case 9's clones fails unless
// --skip-corpus is given (review N11).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

internal sealed record Options(ulong Seed, string GoldenDir, string NativeDir, bool Replay);

/// <summary>A case whose inputs are not on this machine. Replay fails on it
/// unless --skip-corpus is given, and then reports it and exits 2 when
/// nothing failed. Record fails, since its golden file would be
/// missing.</summary>
internal sealed class CaseSkippedException(string reason) : Exception(reason);

internal interface IOracleCase
{
    string Name { get; }
    void Record(GoldenCase golden, Options options);
}

internal static class Program
{
    /// <summary>The default seed: the date the oracle was recorded.</summary>
    public const ulong DefaultSeed = 20261015;

    /// <summary>The commit whose code the golden files beside this source
    /// record.</summary>
    const string OracleCommit = "9bd6279";

    static readonly IOracleCase[] Cases =
    {
        new Case1Submit(),
        new Case2Packers(),
        new Case3Switch2Device(),
        new Case4UsbipFeatures(),
        new Case5Driver(),
        new Case6ValveInputPath(),
        new Case7OutputDecode(),
        new Case8UsbipPaths(),
        new Case9PhysicalCorpus(),
    };

    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
        {
            Console.WriteLine(e.Message);
            return Usage();
        }
        catch (InvalidDataException e)
        {
            // A change list or another input file the run cannot read.
            Console.WriteLine($"  [FAIL] {e.Message}");
            Console.WriteLine("device_oracle_check: FAIL");
            return 1;
        }
    }

    static int Run(string[] args)
    {
        // The SDK's diagnostic log writes a line per Switch 2 Pro command
        // when this is set (millions in case 3). It changes no output.
        Environment.SetEnvironmentVariable("HIDMAESTRO_DIAG", null);
        if (args.Length == 0) return Usage();
        string mode = args[0];
        if (mode == "compare")
            return args.Length == 3 ? CompareDirs(args[1], args[2]) : Usage();
        if (mode == "selftest")
            return SelfTest.Run();
        if (mode == "compare-live")
            return CompareLive.Run(args[1..], Path.Combine(ProbeDir(), "golden"), Path.Combine(AppContext.BaseDirectory, "native"));
        if (mode != "record" && mode != "replay") return Usage();

        ulong seed = DefaultSeed;
        string golden = Path.Combine(ProbeDir(), "golden");
        string native = Path.Combine(AppContext.BaseDirectory, "native");
        bool goldenGiven = false, skipCorpus = false;
        string? lane = null, writeChanges = null, profilePatch = null;
        var changeFiles = new List<string>();
        var names = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--seed": seed = ulong.Parse(Value()); break;
                case "--golden": golden = Path.GetFullPath(Value()); goldenGiven = true; break;
                case "--native": native = Path.GetFullPath(Value()); break;
                case "--case": names.AddRange(Value().Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                case "--plan-change": Case5Driver.PlanChangeFile = Path.GetFullPath(Value()); break;
                case "--changes": changeFiles.Add(Path.GetFullPath(Value())); break;
                case "--corpus-root": Case9PhysicalCorpus.CorpusRoot = Path.GetFullPath(Value()); break;
                case "--skip-corpus": skipCorpus = true; break;
                case "--dump": GoldenCase.DumpDir = Path.GetFullPath(Value()); break;
                case "--explain": GoldenCase.ExplainDir = Path.GetFullPath(Value()); break;
                case "--lane": lane = Value(); break;
                case "--write-changes": writeChanges = Path.GetFullPath(Value()); break;
                case "--profile-patch": profilePatch = Path.GetFullPath(Value()); break;
                default: return Usage();
            }
        }
        if ((GoldenCase.ExplainDir != null) != (lane != null))
            throw new ArgumentException("--explain and --lane go together: the base dump, and the lane whose rules and entries it holds the tree to.");
        if (mode != "replay" && (GoldenCase.ExplainDir != null || writeChanges != null || profilePatch != null))
            throw new ArgumentException("--explain, --write-changes and --profile-patch are replay's.");
        var selected = names.Count == 0 ? Cases.ToList()
            : names.Select(n => Cases.FirstOrDefault(c => c.Name == n) ?? throw new ArgumentException($"No case {n}.")).ToList();
        var options = new Options(seed, golden, native, mode == "replay");

        // The intended changes a replay accepts (ChangeList.cs): changes.txt
        // beside this source, then every --changes file.
        string defaultChanges = Path.Combine(ProbeDir(), "changes.txt");
        if (mode == "replay" && File.Exists(defaultChanges)) changeFiles.Insert(0, defaultChanges);
        var changes = ChangeList.Load(changeFiles);
        Case5Driver.ConfigChanges = changes.Config.ToList();
        if (changes.Entries.Count > 0)
            Console.WriteLine($"  change list: {changes.Entries.Count} intended change(s) from {string.Join(", ", changeFiles.Select(Path.GetFileName))}");
        if (lane != null)
        {
            ExplainSession.Changes = changes;
            ExplainSession.Lane = lane;
            ExplainRules.Active = ExplainRules.From(changes, lane);
            Console.WriteLine($"  explain: lane {lane}, rules {ExplainRules.Active}");
        }
        // What the replay gave for each stream, and for each that differs
        // from its golden file, for --write-changes.
        var pins = new Dictionary<string, (long Count, string Digest)>(StringComparer.Ordinal);
        var moved = new Dictionary<string, (long Count, string Digest)>(StringComparer.Ordinal);
        var explainedStreams = new Dictionary<string, ExplainSession.StreamResult>(StringComparer.Ordinal);
        (bool Ok, string Why)? Pin(string caseName, string stream, GoldenStream? r)
        {
            long n = r?.Count ?? 0;
            string d = r?.Digest ?? "none";
            moved[$"{caseName}/{stream}"] = pins[$"{caseName}/{stream}"] = (n, d);
            var e = changes.Stream(caseName, stream);
            if (e == null) return null;
            if (e.Count == n && e.Digest == d) return (true, $"{e.Lane}: {e.Reason}");
            return (false, $"{e.Source} ({e.Lane}) pins count {e.Count} digest {Short(e.Digest)}, the replay gives count {n} digest {Short(d)}");
        }

        // The golden files beside this source are 9bd6279's outputs. Most
        // cases record from the SDK this probe is built against, and case 7
        // from the test app's build, so a record into them from a tree whose
        // device code has changed would re-baseline the oracle to that code.
        // Refused, unless the SDK, the drivers, the catalog and the test app
        // equal 9bd6279's, the one SDK change of this probe's
        // InternalsVisibleTo entry aside. A lane that regenerates the golden
        // files with its listed changes passes --golden.
        if (mode == "record" && !goldenGiven && TreeDiffersFromOracle(out string why))
        {
            Console.WriteLine($"  [REFUSED] record into {golden}: {why}. Pass --golden <dir> to record elsewhere.");
            return 1;
        }
        // A machine that runs the probes from a staged tree has the binaries
        // and not the golden files beside this source.
        if (mode == "replay" && !goldenGiven && !File.Exists(Path.Combine(golden, GoldenStore.Manifest)))
        {
            Console.WriteLine($"  [SKIP] no golden files at {golden}: the probe runs from a staged tree.");
            return 2;
        }

        Console.WriteLine($"=== device_oracle_check {mode}, seed {seed}, golden {golden} ===");
        if (profilePatch != null) ProfilePatch.Apply(profilePatch);
        // Every case reads the catalog through LoadEmbedded's process-wide
        // cache, so a case that changed a profile would change what every
        // case after it records. None may: checked after the last case.
        var catalog = CatalogText();
        int failures = 0, skipped = 0;
        // The cases that ran to their golden comparison.
        var compared = new HashSet<string>(StringComparer.Ordinal);
        var recorded = new List<GoldenCase>();
        foreach (var c in selected)
        {
            var sw = Stopwatch.StartNew();
            var g = new GoldenCase(c.Name, seed, dump: true);
            try
            {
                // --explain holds each base stream to its golden pin where no
                // entry before the lane pins it.
                if (GoldenCase.ExplainDir != null)
                    ExplainSession.Golden = GoldenCase.Parse(GoldenStore.ReadVerified(golden, c.Name)).Streams
                        .ToDictionary(s => s.Name, s => (s.Count, s.Digest), StringComparer.Ordinal);
                try { c.Record(g, options); }
                finally { g.CloseDump(); }
                Console.WriteLine($"  {c.Name}: {g.Streams.Count} streams, {g.Outputs} outputs, {sw.Elapsed.TotalSeconds:F1} s");
                if (mode == "record")
                {
                    recorded.Add(g);
                    continue;
                }
                var want = GoldenCase.Parse(GoldenStore.ReadVerified(golden, c.Name));
                if (want.Seed != seed)
                {
                    Console.WriteLine($"  [FAIL] {c.Name}: the golden file was recorded with seed {want.Seed}, this replay used {seed}");
                    failures++;
                    continue;
                }
                if (g.Explained is { } ex)
                {
                    foreach (var line in ex.Lines) Console.WriteLine(line);
                    foreach (var (name, result) in ex.Results) explainedStreams[$"{c.Name}/{name}"] = result;
                    Console.WriteLine($"  [{(ex.Failures == 0 ? "PASS" : "FAIL")}] {c.Name}: every output against the base dump, by lane {lane}'s rules, {ex.Failures} failure(s)");
                    if (ex.Failures != 0) failures++;
                }
                int bad = GoldenCase.Compare(want, g, Console.Out, (s, r) => Pin(c.Name, s, r));
                var mine = g.Streams.ToDictionary(x => x.Name, StringComparer.Ordinal);
                foreach (var s in g.Streams) pins[$"{c.Name}/{s.Name}"] = (s.Count, s.Digest);
                // A stream back at its golden bytes: its chain's last entry
                // pins those.
                foreach (var chain in changes.Entries.Where(e => e.Kind == "stream" && !e.Used && e.Case == c.Name)
                                                     .GroupBy(e => e.Target, StringComparer.Ordinal))
                {
                    var end = chain.Last();
                    foreach (var e in chain) e.Used = true;
                    var (n, d) = pins.TryGetValue(chain.Key, out var p) ? p : (0, "none");
                    if (end.Count == n && end.Digest == d) continue;
                    Console.WriteLine($"  [FAIL] {end.Source}: {chain.Key} equals its golden file, count {n} digest {Short(d)}, and its last entry pins count {end.Count} digest {Short(end.Digest)}");
                    bad++;
                }
                int same = want.Streams.Count(w => mine.TryGetValue(w.Name, out var r) && r.Count == w.Count && r.Digest == w.Digest);
                Console.WriteLine($"  [{(bad == 0 ? "PASS" : "FAIL")}] {c.Name}: {same} of {want.Streams.Count} golden streams identical in the replay{(bad == 0 ? "" : $", {bad} difference(s)")}");
                if (bad != 0) failures++;
                compared.Add(c.Name);
            }
            catch (CaseSkippedException e) when (mode == "replay" && skipCorpus)
            {
                Console.WriteLine($"  [SKIP] {c.Name}: {e.Message}");
                skipped++;
            }
            catch (CaseSkippedException e) when (mode == "replay")
            {
                Console.WriteLine($"  [FAIL] {c.Name}: the golden files are here and this case's inputs are not: {e.Message}. " +
                                  "Pass --skip-corpus to replay without it.");
                failures++;
            }
            catch (Exception e)
            {
                Console.WriteLine($"  [FAIL] {c.Name}: {e.GetType().Name}: {e.Message}");
                Console.WriteLine(e.StackTrace);
                failures++;
            }
        }
        foreach (var (id, text) in CatalogText())
            if (!catalog.TryGetValue(id, out var was) || was != text)
            {
                Console.WriteLine($"  [FAIL] the run changed catalog profile {id}: the cases must build what they change from a copy");
                failures++;
            }
        // Every change listed for a case that ran must have matched: the list
        // names the tree's changes and no others. A rule is the lane's to
        // state, and --explain's to hold the tree to. A case that stopped
        // short has failed already, and its entries are not held to it.
        var ran = compared;
        foreach (var e in changes.Entries.Where(e => !e.Used && e.Kind != "rule" && (e.Kind == "config" ? ran.Contains("case5-driver") : ran.Contains(e.Case))))
        {
            Console.WriteLine($"  [FAIL] {e.Source}: the listed change {e.Kind} {e.Target} ({e.Lane}) matched no difference");
            failures++;
        }
        if (writeChanges != null)
        {
            ChangeWriter.Write(writeChanges, changes, ran, moved, pins, explainedStreams, lane, Case5Driver.ConfigUnlisted);
            Console.WriteLine($"  wrote {writeChanges}: the change list this replay passes with, \"?\" where a lane or reason is to be filled in");
        }
        if (mode == "record" && failures == 0)
        {
            GoldenStore.Save(golden, recorded);
            foreach (var g in recorded)
            {
                var info = new FileInfo(Path.Combine(golden, GoldenStore.FileName(g.Name)));
                Console.WriteLine($"  wrote {info.Name}, {info.Length} bytes");
            }
        }
        Console.WriteLine($"device_oracle_check {mode}: " +
            (failures != 0 ? $"FAIL, {failures} case(s) differ" : skipped != 0 ? $"SKIP, {skipped} case(s) not run, the rest pass" : "PASS"));
        return failures != 0 ? 1 : skipped != 0 ? 2 : 0;
    }

    /// <summary>Two recordings of the same seed must be identical, file for
    /// file, after decompression.</summary>
    static int CompareDirs(string a, string b)
    {
        var sa = GoldenStore.ReadManifest(a);
        var sb = GoldenStore.ReadManifest(b);
        if (sa.Count == 0 || sb.Count == 0)
        {
            Console.WriteLine($"device_oracle_check compare: FAIL, no {GoldenStore.Manifest} in {(sa.Count == 0 ? a : b)}");
            return 1;
        }
        int bad = 0;
        foreach (var file in sa.Keys.Union(sb.Keys).OrderBy(f => f, StringComparer.Ordinal))
        {
            bool same = sa.TryGetValue(file, out var x) && sb.TryGetValue(file, out var y) && x == y
                && GoldenStore.Decompress(Path.Combine(a, file)).AsSpan().SequenceEqual(GoldenStore.Decompress(Path.Combine(b, file)));
            Console.WriteLine($"  [{(same ? "PASS" : "FAIL")}] {file} identical in both recordings");
            if (!same) bad++;
        }
        Console.WriteLine($"device_oracle_check compare: {(bad == 0 ? "PASS" : $"FAIL, {bad} file(s) differ")}");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>True when the SDK, the drivers, the catalog or the test app
    /// of the tree this probe sits in differ from the oracle commit, or git
    /// cannot tell. The test app's sources count because case 7 records
    /// its decode (review N3): its project file and every .cs file under
    /// test\ but the probes and sandbox folders, which hold separate
    /// projects the test app does not compile, this probe among them.</summary>
    static bool TreeDiffersFromOracle(out string why)
    {
        string root = Path.GetFullPath(Path.Combine(ProbeDir(), "..", "..", ".."));
        string[] paths =
        {
            "sdk", "driver", "profiles", ":(glob)test/**/*.cs", "test/HIDMaestroTest.csproj",
            ":(exclude)sdk/HIDMaestro.Core/Internal/AssemblyInfo.cs",
            ":(exclude)test/probes", ":(exclude)test/sandbox",
        };
        var (diffCode, diffOut) = Git(root, new[] { "diff", "--name-only", OracleCommit, "--" }.Concat(paths));
        var (statusCode, statusOut) = Git(root, new[] { "status", "--porcelain", "--untracked-files=all", "--" }.Concat(paths));
        if (diffCode != 0 || statusCode != 0)
        {
            why = $"git could not compare {root} with {OracleCommit}";
            return true;
        }
        // diff names one path a line, status puts two status letters and a
        // space before it.
        var changed = diffOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(statusOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Length > 3 ? l[3..].Trim() : l.Trim()))
            .Where(l => l.Length > 0).Distinct().ToList();
        why = changed.Count == 0 ? "" : $"{changed.Count} file(s) differ from {OracleCommit}, the first {changed[0]}";
        return changed.Count > 0;
    }

    /// <summary>git with no window. Exit code and standard output.</summary>
    static (int Code, string Output) Git(string root, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(root);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            _ = err.Result;
            return (p.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "");
        }
    }

    static string ProbeDir() =>
        typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "ProbeDir").Value!;

    static string Short(string digest) => digest.Length > 16 ? digest[..16] : digest;

    /// <summary>Each catalog profile as JSON, as the run's cases see it.</summary>
    static Dictionary<string, string> CatalogText() =>
        HIDMaestro.Internal.ProfileDatabase.LoadEmbedded().All.ToDictionary(
            p => p.Id, p => System.Text.Json.JsonSerializer.Serialize(p, HIDMaestro.Internal.HMJsonContext.Default.ControllerProfile),
            StringComparer.Ordinal);

    static int Usage()
    {
        Console.WriteLine("usage: DeviceOracleCheck record|replay [--case <name,...>] [--seed <n>] [--golden <dir>] [--native <dir>] [--corpus-root <dir>] [--plan-change <file>] [--changes <file>] [--skip-corpus] [--dump <dir>]");
        Console.WriteLine("       DeviceOracleCheck replay ... [--explain <base dump dir> --lane <lane>] [--write-changes <file>] [--profile-patch <file>]");
        Console.WriteLine("       DeviceOracleCheck selftest");
        Console.WriteLine("       DeviceOracleCheck compare <dirA> <dirB>");
        Console.WriteLine("       DeviceOracleCheck compare-live <out dir> | --selftest <dir> [--golden <dir>] [--native <dir>]");
        Console.WriteLine("cases: " + string.Join(", ", Cases.Select(c => c.Name)));
        return 1;
    }
}
