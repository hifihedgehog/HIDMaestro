// Case 9: the physical corpus. Every physical HID report descriptor and
// identity found on disk among the cloned references (ControllersInfo,
// hidapi's captures as recorded and as rebuilt from preparsed data,
// DsHidMini's mode descriptors, WinUHid, InputPlumber, DS5Dongle and
// xpadneo's dumps), each built into a profile the way
// HMDeviceExtractor.Extract builds one from a connected device
// (HMDeviceExtractor.cs:100-155), and what today's gates give it. The
// catalog's own descriptors are not in it. The identity defaults of the
// design note are tested against this record.
//
// The corpus is case9-corpus.json beside this file: one entry per
// descriptor with its repository, commit, file and line, its length and
// SHA-256, the identity and where the identity came from. The entry id
// names the stream. An entry whose source gives no vendor or product id
// uses the one its "assumedIdentity" names, with the reason, and the stream
// says so. Three entries carry the product string Windows gives the XInput
// HID child of 045E:028E, 02FD and 0B12, as Wine's HID class reproduces it
// (dlls/hidclass.sys/device.c:379-393 at 2df1ee2), on descriptors other
// entries hold, so the product string the companion gate reads is recorded
// as an extraction on Windows sees it.
//
// The descriptor bytes stay in the cloned repositories. Several sources
// carry licenses this repository does not (InputPlumber GPL-3.0, xpadneo
// GPL-2.0, ControllersInfo none stated), so no descriptor is copied here:
// the case reads each with git show at the entry's commit, parses it from
// the cited line, and fails unless its length and SHA-256 match. Every
// output passes through a redaction that writes a descriptor, as bytes or
// as hex text, as its SHA-256, and the record fails if any 16 bytes of a
// descriptor remain in the golden file. The clones are found under
// --corpus-root, else HIDMAESTRO_CORPUS_ROOT, else the folder holding this
// checkout or, for a git worktree, its main checkout. Without them the case
// is skipped: replay exits 2, and record fails.
//
// What Extract reads from Windows, and how the case stands in for it:
//   descriptor  the entry's bytes. Extract reconstructs one from preparsed
//               data, so a device with several top-level collections
//               reaches it as one interface per collection. The case
//               builds from the whole descriptor and records the number
//               of top-level collections.
//   type        the first top-level collection's usage page and usage,
//               mapped as Extract maps TopLevelUsagePage and TopLevelUsage.
//   connection  Extract reads the device path: "{0000" or "&Dev&" in it
//               means Bluetooth. A Bluetooth or Bluetooth LE entry gives
//               "bluetooth", any other "usb".
//   notes       Extract stamps the time of extraction. The case stamps a
//               fixed time, so a run repeats.
//
// Per entry, on the SDK side (stream corpus/<id>/sdk): the profile the
// builder made, the predicates case 1's predicates stream reads, whether
// the SDK runs the Switch Pro's protocol for it (at 9bd6279 the packer's
// VID and PID gate, SwitchProPacker.IsSwitchPro, and the controller's own
// flag, from v2.0.0 a UMDF2 protocol in the profile and the controller's
// protocol host), and the frames the controller writes for the
// scripted state set (States.Scripted, each state once), built offline as
// case 1 builds its controllers. Then the instance configuration
// WriteInstanceConfig writes for it, in the hive case 5 uses
// (corpus/<id>/instance-config). On the driver side, the native harness's
// --corpus mode (native\harness\hm_case_corpus.c) runs the device's start,
// the feature answers at every declared id, the Sony table's ids and
// report 0 at every size, SDL's first Switch requests, and for a profile
// the SDK gives the XUSB companion, the companion's answers.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;

internal sealed class Case9PhysicalCorpus : IOracleCase
{
    public string Name => "case9-physical-corpus";

    const string CorpusFile = "case9-corpus.json";

    /// <summary>Transport indexes far above any a program assigns and
    /// apart from case 1's (7000 on).</summary>
    const int FirstIndex = 9000;

    /// <summary>The time the case stamps in place of the time of
    /// extraction.</summary>
    const string FixedStamp = "2026-10-15 00:00:00";

    static int s_runs;

    /// <summary>The folder holding the reference clones, from
    /// --corpus-root.</summary>
    public static string? CorpusRoot;

    sealed record Entry(string Id, string Repo, string Commit, string Source, int? Line, string? IdentitySource,
                        byte[] Descriptor, ushort Vid, ushort Pid, string? Connection, string? Product, string? Manufacturer,
                        string IdentityNote, string? Assumed);

    sealed record Listed(string Id, string Repo, string Commit, string Source, int? Line, int Length, string Sha256,
                         IReadOnlyList<string> Headers);

    public void Record(GoldenCase golden, Options options)
    {
        string corpusPath = Path.Combine(ProbeDir(), CorpusFile);
        var listed = ListAll(corpusPath);
        var repos = listed.Select(x => x.Repo).Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).ToList();
        string root = FindRoot(repos, out string tried)
            ?? throw new CaseSkippedException($"the reference clones ({string.Join(", ", repos)}) are not under {tried}. Pass --corpus-root <dir> or set HIDMAESTRO_CORPUS_ROOT");
        var bytes = listed.ToDictionary(x => x.Id, x => Fetch(root, x), StringComparer.Ordinal);
        var entries = Load(corpusPath, bytes);
        var redaction = new Redaction(bytes.Values);
        golden.Redact = redaction.Apply;
        golden.Stream("corpus");
        // Line ends as git checks the file out on this machine left out.
        golden.Out("sha256", System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(File.ReadAllText(corpusPath).Replace("\r\n", "\n"))));
        golden.Out("entries", BitConverter.GetBytes(entries.Count));

        string work = Path.Combine(AppContext.BaseDirectory, "case9",
            $"run-{Environment.ProcessId}-{Interlocked.Increment(ref s_runs)}");
        Directory.CreateDirectory(work);
        try { Run(golden, options, entries, work); }
        finally { Case5Driver.DeleteDirectory(work); }
        redaction.CheckNoneIn(golden.Render());
    }

    void Run(GoldenCase golden, Options options, List<Entry> entries, string work)
    {
        var plan = new StringBuilder("hm-driver-plan 1\n");
        IntPtr view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
        try
        {
            var section = new byte[SharedMemoryIO.SHARED_INPUT_SIZE];
            var frame = new byte[2 + SharedMemoryIO.DATA_CAPACITY + 2 + SharedMemoryIO.EXTENDED_DATA_CAPACITY + SharedMemoryIO.GIP_DATA_LENGTH];
            int n = 0;
            foreach (var e in entries)
            {
                golden.Stream($"corpus/{e.Id}/sdk");
                golden.Out("source", Encoding.UTF8.GetBytes(
                    $"{e.Repo}@{e.Commit} {e.Source}{(e.Line is int l ? $":{l}" : "")} identity {e.IdentitySource ?? "none"}" +
                    (e.Assumed != null ? $" assumed {e.Assumed}" : "")));
                var (topPage, topUsage, collections) = TopLevel(e.Descriptor);
                golden.Out("collections", BitConverter.GetBytes(collections));

                ControllerProfile p;
                try { p = Build(e, topPage, topUsage).Inner; }
                catch (Exception ex)
                {
                    golden.Out("build.throw", Encoding.ASCII.GetBytes(ex.GetType().FullName ?? "?"));
                    continue;
                }
                golden.Out("built", Encoding.UTF8.GetBytes(
                    $"id={p.Id} name={p.Name} vendor={p.Vendor} vid=0x{p.VendorId:X4} pid=0x{p.ProductId:X4} " +
                    $"product={p.ProductString} manufacturer={p.ManufacturerString} type={p.Type} connection={p.Connection}"));
                golden.Out("predicates", Encoding.UTF8.GetBytes(Predicates(p)));
                golden.Out("switch.packer", new[] { (byte)(p.Protocol != null && !p.RequiresUsbipBackend ? 1 : 0) });
                Submit(golden, p, FirstIndex + n++, view, section, frame);

                // The instance configuration, one profile at a time: two
                // extracted profiles can share an id.
                golden.Stream($"corpus/{e.Id}/instance-config");
                List<Case5Driver.RegValue> values;
                try { values = Case5Driver.InstanceConfig(new List<ControllerProfile> { p }, work)[p.Id]; }
                catch (Exception ex)
                {
                    golden.Out("throw", Encoding.ASCII.GetBytes((ex is TargetInvocationException t ? t.InnerException ?? ex : ex).GetType().FullName ?? "?"));
                    continue;
                }
                foreach (var v in values)
                    golden.Out("value", Encoding.UTF8.GetBytes($"{v.Name} {v.Show()}"));
                try { Case5Driver.AddProfile(plan, p.RequiresXusbCompanion ? "corpus-xusb" : "corpus", p, values, e.Id); }
                catch (Exception ex)
                {
                    golden.Out("plan.throw", Encoding.ASCII.GetBytes(ex.GetType().FullName ?? "?"));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(view);
        }

        // The plan holds each descriptor, so the golden file keeps its digest
        // only, and every run builds it again from the clones.
        string text = plan.ToString();
        golden.Stream("plan");
        golden.Out("sha256", System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        string planPath = Path.Combine(work, "plan.txt");
        string output = Path.Combine(work, options.Replay ? "tree.bin" : "oracle.bin");
        // Replay runs the corpus as the tree's SDK, under the version
        // contract, so each extracted profile's answers are the ones its
        // identity default compiles (Case5Driver.AsTreeSdk).
        File.WriteAllText(planPath, options.Replay ? Case5Driver.AsTreeSdk(text) : text, new UTF8Encoding(false));
        string exe = Path.Combine(options.NativeDir, options.Replay ? "driver_harness_tree.exe" : "driver_harness_oracle.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException($"{exe} is not built: run native\\build.ps1 -Out {options.NativeDir}");
        Case5Driver.RunHarness(exe, planPath, output, options.Seed, "--corpus");
        NativeStreamReader.ReadInto(output, golden);
    }

    // ── the corpus ───────────────────────────────────────────────────────

    /// <summary>Every entry of the corpus with where its bytes are, the two
    /// entries with no identity included.</summary>
    static List<Listed> ListAll(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var list = new List<Listed>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var x in doc.RootElement.EnumerateArray())
        {
            string? S(string name) => x.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            string id = S("id") ?? throw new InvalidDataException("a corpus entry has no id");
            if (!seen.Add(id)) throw new InvalidDataException($"two corpus entries are named {id}");
            list.Add(new Listed(id, S("repo") ?? throw new InvalidDataException($"{id} names no repository"),
                S("commit") ?? throw new InvalidDataException($"{id} names no commit"),
                S("source") ?? throw new InvalidDataException($"{id} names no file"),
                x.TryGetProperty("line", out var ln) && ln.ValueKind == JsonValueKind.Number ? ln.GetInt32() : null,
                x.GetProperty("length").GetInt32(),
                S("sha256") ?? throw new InvalidDataException($"{id} has no SHA-256"),
                x.TryGetProperty("headers", out var hd) && hd.ValueKind == JsonValueKind.Array
                    ? hd.EnumerateArray().Select(h => h.GetString()!).ToList()
                    : new List<string>()));
        }
        return list;
    }

    static List<Entry> Load(string path, Dictionary<string, byte[]> bytes)
    {
        var where = ListAll(path).ToDictionary(x => x.Id, StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var list = new List<Entry>();
        foreach (var x in doc.RootElement.EnumerateArray())
        {
            string? S(string name) => x.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            string id = S("id")!;
            string? vid = S("vid"), pid = S("pid");
            string? assumed = null;
            if ((vid == null || pid == null) && x.TryGetProperty("assumedIdentity", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                vid = a.GetProperty("vid").GetString();
                pid = a.GetProperty("pid").GetString();
                assumed = $"{vid}:{pid} ({a.GetProperty("reason").GetString()})";
            }
            if (vid == null || pid == null) continue;   // no identity, none assumed: listed in the corpus, not built
            var w = where[id];
            list.Add(new Entry(id, w.Repo, w.Commit, w.Source, w.Line, S("identitySource"), bytes[id],
                Convert.ToUInt16(vid, 16), Convert.ToUInt16(pid, 16),
                S("connection"), S("product"), S("manufacturer"), S("identityNote") ?? "", assumed));
        }
        return list;
    }

    // ── the descriptor bytes, from the clones ────────────────────────────

    /// <summary>The first folder that holds every repository the corpus
    /// names.</summary>
    static string? FindRoot(IReadOnlyList<string> repos, out string tried)
    {
        var candidates = new List<string>();
        string? env = Environment.GetEnvironmentVariable("HIDMAESTRO_CORPUS_ROOT");
        if (CorpusRoot != null) candidates.Add(Path.GetFullPath(CorpusRoot));
        else if (!string.IsNullOrEmpty(env)) candidates.Add(Path.GetFullPath(env));
        else
        {
            string checkout = Path.GetFullPath(Path.Combine(ProbeDir(), "..", "..", ".."));
            if (Path.GetDirectoryName(checkout) is string up) candidates.Add(up);
            // A worktree's main checkout is the folder that holds its common
            // git directory.
            var (code, output, _) = Git(checkout, "rev-parse", "--path-format=absolute", "--git-common-dir");
            string common = Encoding.UTF8.GetString(output).Trim();
            if (code == 0 && common.Length > 0
                && Path.GetDirectoryName(Path.GetFullPath(common).TrimEnd('\\', '/')) is string main
                && Path.GetDirectoryName(main) is string mainUp
                && !candidates.Contains(mainUp, StringComparer.OrdinalIgnoreCase))
                candidates.Add(mainUp);
        }
        tried = candidates.Count == 0 ? "no folder" : string.Join(" or ", candidates);
        return candidates.FirstOrDefault(c => repos.All(r => Directory.Exists(Path.Combine(c, r))));
    }

    /// <summary>The entry's descriptor at its commit: a binary file whole,
    /// or the bytes written from the cited line on. Fails unless the length
    /// and the SHA-256 are the corpus's.</summary>
    static byte[] Fetch(string root, Listed e)
    {
        string repo = Path.Combine(root, e.Repo);
        var macros = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var header in e.Headers)
            foreach (var l in Encoding.UTF8.GetString(Show(repo, e, header)).Replace("\r", "").Split('\n'))
                Define(l, macros);
        byte[] d = e.Line is int line
            ? FromText(Lines(repo, e, e.Source, line, 0, macros), e.Length, e.Source.EndsWith(".rpt_desc", StringComparison.OrdinalIgnoreCase))
            : Show(repo, e, e.Source);
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(d));
        if (d.Length != e.Length || !string.Equals(sha, e.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{e.Id}: {e.Repo}@{e.Commit} {e.Source}{(e.Line is int l ? $":{l}" : "")} gives {d.Length} bytes with SHA-256 {sha}, the corpus lists {e.Length} and {e.Sha256}");
        return d;
    }

    static byte[] Show(string repo, Listed e, string file)
    {
        var (code, blob, error) = Git(repo, "show", $"{e.Commit}:{file}");
        if (code != 0)
            throw new InvalidDataException($"{e.Id}: git show {e.Commit}:{file} in {repo} failed: {error.Trim()}");
        return blob;
    }

    static readonly Dictionary<string, string[]> s_trees = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An included path as the Windows build finds it, by name
    /// regardless of case (DsHid.c includes "02_GPJ_Col1_Gamepad.h", and
    /// the commit holds "02_GPJ_Col1_GamePad.h").</summary>
    static string Resolve(string repo, Listed e, string path)
    {
        string key = $"{repo}|{e.Commit}";
        if (!s_trees.TryGetValue(key, out var names))
        {
            var (code, output, error) = Git(repo, "ls-tree", "-r", "--name-only", e.Commit);
            if (code != 0) throw new InvalidDataException($"{e.Id}: git ls-tree {e.Commit} in {repo} failed: {error.Trim()}");
            s_trees[key] = names = Encoding.UTF8.GetString(output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        var found = names.Where(n => string.Equals(n, path, StringComparison.OrdinalIgnoreCase)).ToList();
        return found.Count == 1 ? found[0]
            : throw new InvalidDataException($"{e.Id}: {path} matches {found.Count} files at {e.Commit}");
    }

    static readonly Regex s_define = new(@"^\s*#\s*define\s+(\w+)(?:\s+(\S+))?", RegexOptions.CultureInvariant);
    static readonly Regex s_identifier = new(@"\b[A-Za-z_]\w*\b", RegexOptions.CultureInvariant);

    /// <summary>Records a #define line's name, and its value when the value
    /// is one 0x token.</summary>
    static void Define(string line, Dictionary<string, string?> macros)
    {
        var m = s_define.Match(StripComments(line));
        if (!m.Success) return;
        string? value = m.Groups[2].Success ? m.Groups[2].Value : null;
        macros[m.Groups[1].Value] = value != null && s_hexToken.IsMatch(value) && s_hexToken.Match(value).Length == value.Length ? value : null;
    }

    static string StripComments(string line)
    {
        bool inBlock = false;
        return StripComments(line, ref inBlock);
    }

    static readonly Regex s_include = new(@"^\s*#\s*include\s+""([^""]+)""", RegexOptions.CultureInvariant);
    static readonly Regex s_conditional = new(@"^\s*#\s*(ifdef|ifndef|if|elif|else|endif)\b\s*(\w*)", RegexOptions.CultureInvariant);

    /// <summary>A file's lines from its 1-based line on, comments left out,
    /// as the C preprocessor gives them to the compiler: a quoted #include
    /// read at the same commit beside the including file, #ifdef, #ifndef
    /// and #else on the names defined so far, #define recorded, and a
    /// macro whose value is one 0x token replaced by it. The entry's
    /// "headers" are the files its source includes before the cited line
    /// (DsHidMini's driver/Driver.h, which defines DSHM_FEATURE_FFB, and
    /// driver/PID/PIDTypes.h, which defines the PID report ids). Only a C or
    /// C++ file is preprocessed: a # line elsewhere is text (hidapi's
    /// hid-decode dumps write "# 0x05, 0x01, ...").</summary>
    static IEnumerable<string> Lines(string repo, Listed e, string file, int from, int depth, Dictionary<string, string?> macros)
    {
        if (depth > 8) throw new InvalidDataException($"{e.Id}: includes nest deeper than 8 at {file}");
        var lines = Encoding.UTF8.GetString(Show(repo, e, file)).Replace("\r", "").Split('\n');
        var active = new Stack<(bool On, bool Parent)>();
        bool on = true, inBlock = false;
        bool preprocessed = Path.GetExtension(file).ToLowerInvariant() is ".c" or ".cpp" or ".h" or ".hpp";
        for (int i = from - 1; i < lines.Length; i++)
        {
            string code = StripComments(lines[i], ref inBlock);
            if (!preprocessed || !code.TrimStart().StartsWith('#'))
            {
                if (on) yield return preprocessed
                    ? s_identifier.Replace(code, m => macros.TryGetValue(m.Value, out var v) && v != null ? v : m.Value)
                    : code;
                continue;
            }
            var c = s_conditional.Match(code);
            if (c.Success)
            {
                switch (c.Groups[1].Value)
                {
                    case "ifdef": active.Push((on, on)); on = on && macros.ContainsKey(c.Groups[2].Value); break;
                    case "ifndef": active.Push((on, on)); on = on && !macros.ContainsKey(c.Groups[2].Value); break;
                    case "else":
                        if (active.Count == 0) throw new InvalidDataException($"{e.Id}: #else with no #if at {file}:{i + 1}");
                        var top = active.Peek();
                        on = top.Parent && !on;
                        break;
                    case "endif":
                        if (active.Count == 0) yield break;   // the cited line sits inside a conditional that ends here
                        on = active.Pop().On;
                        break;
                    default: throw new InvalidDataException($"{e.Id}: #{c.Groups[1].Value} at {file}:{i + 1} is not read by this case");
                }
                continue;
            }
            if (on) Define(code, macros);
            var inc = s_include.Match(code);
            if (inc.Success && on)
            {
                string dir = Path.GetDirectoryName(file.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
                string target = string.IsNullOrEmpty(dir) ? inc.Groups[1].Value : $"{dir}/{inc.Groups[1].Value}";
                foreach (var l in Lines(repo, e, Resolve(repo, e, target), 1, depth + 1, macros)) yield return l;
            }
        }
    }

    static readonly Regex s_hexToken = new(@"(?<![0-9A-Za-z_])0[xX]([0-9A-Fa-f]{1,2})(?![0-9A-Za-z_])", RegexOptions.CultureInvariant);
    static readonly Regex s_hexPairs = new(@"^((?:[0-9A-Fa-f]{2}\s+)*[0-9A-Fa-f]{2})$", RegexOptions.CultureInvariant);
    static readonly Regex s_offsetPairs = new(@"\b[0-9A-Fa-f]{8}:((?: [0-9A-Fa-f]{2}(?=\s|$))+)", RegexOptions.CultureInvariant);
    static readonly Regex s_trailingPairs = new(@"(?:^|\s)((?:[0-9A-Fa-f]{2}\s+)*[0-9A-Fa-f]{2})$", RegexOptions.CultureInvariant);

    /// <summary>The bytes the lines write: 0x tokens (C, C++, Rust, hidapi's
    /// expected .rpt_desc and a C block in Markdown), or on a line with none,
    /// a line of bare hex pairs, or the pairs after an offset such as
    /// "00000010:" up to the dump's text column (xxd and kernel log dumps in
    /// Markdown), or in a .rpt_desc,
    /// the hex pairs that end an item's line (hidapi's real captures,
    /// "Report ID (1) 85 01").</summary>
    static byte[] FromText(IEnumerable<string> lines, int length, bool itemLines)
    {
        var bytes = new List<byte>(length);
        foreach (var code in lines)
        {
            if (bytes.Count >= length) break;
            var tokens = s_hexToken.Matches(code);
            if (tokens.Count > 0)
            {
                foreach (Match t in tokens)
                    if (bytes.Count < length) bytes.Add(Convert.ToByte(t.Groups[1].Value, 16));
            }
            else if ((itemLines ? s_trailingPairs.Match(code.Trim())
                      : s_offsetPairs.Match(code) is { Success: true } dump ? dump : s_hexPairs.Match(code.Trim())) is { Success: true } m)
            {
                string run = m.Groups[1].Value;
                foreach (var t in run.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    if (bytes.Count < length) bytes.Add(Convert.ToByte(t, 16));
            }
        }
        return bytes.ToArray();
    }

    static string StripComments(string line, ref bool inBlock)
    {
        var sb = new StringBuilder(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            if (inBlock)
            {
                if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/') { inBlock = false; i++; }
                continue;
            }
            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*') { inBlock = true; i++; continue; }
            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            sb.Append(line[i]);
        }
        return sb.ToString();
    }

    /// <summary>git with no window: exit code, standard output as bytes,
    /// standard error.</summary>
    static (int Code, byte[] Output, string Error) Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(dir);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var error = p.StandardError.ReadToEndAsync();
            using var output = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(output);
            p.WaitForExit();
            return (p.ExitCode, output.ToArray(), error.Result);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, Array.Empty<byte>(), ex.Message);
        }
    }

    /// <summary>Writes each descriptor of the corpus, found in an output as
    /// bytes or as hex text in either case, as its SHA-256 (the bytes, or
    /// "sha256:" and the hex), and checks a rendered golden file for any 16
    /// bytes in a row of one, padding aside.</summary>
    sealed class Redaction
    {
        const int Window = 16;
        readonly List<(byte[] Find, byte[] With)> _pairs = new();
        readonly HashSet<string> _windows = new(StringComparer.Ordinal);
        readonly int _shortest;

        public Redaction(IEnumerable<byte[]> descriptors)
        {
            var distinct = descriptors.GroupBy(d => Convert.ToHexString(d), StringComparer.Ordinal)
                .Select(g => g.First()).OrderByDescending(d => d.Length).ToList();
            foreach (var d in distinct)
            {
                byte[] hash = System.Security.Cryptography.SHA256.HashData(d);
                byte[] hashText = Encoding.ASCII.GetBytes("sha256:" + Convert.ToHexString(hash));
                string hex = Convert.ToHexString(d);
                _pairs.Add((d, hash));
                _pairs.Add((Encoding.ASCII.GetBytes(hex), hashText));
                _pairs.Add((Encoding.ASCII.GetBytes(hex.ToLowerInvariant()), hashText));
                // A run of padding (ControllersInfo's zeroplus dump ends in
                // 3,872 zero bytes) is no one's expression and matches any
                // idle report, so a window counts only with four or more
                // distinct byte values.
                for (int i = 0; i + Window <= d.Length; i++)
                    if (d.AsSpan(i, Window).ToArray().Distinct().Count() >= 4)
                        _windows.Add(hex.Substring(2 * i, 2 * Window));
            }
            _shortest = distinct.Count == 0 ? int.MaxValue : distinct.Min(d => d.Length);
        }

        public byte[] Apply(byte[] data)
        {
            if (data.Length < _shortest) return data;
            foreach (var (find, with) in _pairs)
                if (data.Length >= find.Length) data = Replace(data, find, with);
            return data;
        }

        static byte[] Replace(byte[] data, byte[] find, byte[] with)
        {
            int at = data.AsSpan().IndexOf(find);
            if (at < 0) return data;
            var o = new List<byte>(data.Length);
            int from = 0;
            while (at >= 0)
            {
                o.AddRange(data.AsSpan(from, at - from).ToArray());
                o.AddRange(with);
                from = at + find.Length;
                int next = data.AsSpan(from).IndexOf(find);
                at = next < 0 ? -1 : from + next;
            }
            o.AddRange(data.AsSpan(from).ToArray());
            return o.ToArray();
        }

        /// <summary>Fails when a rendered golden file holds 16 bytes of a
        /// descriptor in its kept outputs, as the uppercase hex it writes.</summary>
        public void CheckNoneIn(string rendered)
        {
            var lookup = _windows.GetAlternateLookup<ReadOnlySpan<char>>();
            foreach (var line in rendered.Split('\n'))
            {
                int space = line.IndexOf(' ');
                if (space < 0) continue;
                var hex = line.AsSpan(space + 1);
                for (int i = 0; i + 2 * Window <= hex.Length; i += 2)
                    if (lookup.Contains(hex.Slice(i, 2 * Window)))
                        throw new InvalidDataException($"the golden file would hold descriptor bytes: {line[..Math.Min(line.Length, 60)]}");
            }
        }
    }

    /// <summary>The first top-level collection's usage page and usage, and
    /// the number of top-level collections: what HidP_GetCaps gives the
    /// interface of the first collection.</summary>
    static (ushort Page, ushort Usage, int Collections) TopLevel(byte[] d)
    {
        ushort page = 0, firstPage = 0, firstUsage = 0;
        uint usage = 0;
        bool haveUsage = false;
        int depth = 0, collections = 0;
        for (int i = 0; i < d.Length;)
        {
            byte b = d[i];
            if (b == 0xFE)
            {
                if (i + 2 >= d.Length) break;
                i += 3 + d[i + 1];
                continue;
            }
            int size = (b & 3) == 3 ? 4 : b & 3;
            int type = (b >> 2) & 3, tag = b >> 4;
            uint data = 0;
            for (int k = 0; k < size && i + 1 + k < d.Length; k++) data |= (uint)d[i + 1 + k] << (8 * k);
            i += 1 + size;
            if (type == 1 && tag == 0) page = (ushort)data;
            else if (type == 2 && tag == 0 && !haveUsage) { usage = size == 4 ? data : ((uint)page << 16) | (data & 0xFFFF); haveUsage = true; }
            else if (type == 0)
            {
                if (tag == 0xA)
                {
                    if (depth == 0 && data == 1)
                    {
                        if (collections == 0) { firstPage = (ushort)(usage >> 16); firstUsage = (ushort)usage; }
                        collections++;
                    }
                    depth++;
                }
                else if (tag == 0xC && depth > 0) depth--;
                haveUsage = false;
            }
        }
        return (firstPage, firstUsage, collections);
    }

    static readonly MethodInfo s_slugify =
        typeof(HMDeviceExtractor).GetMethod("Slugify", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("HMDeviceExtractor.Slugify");

    /// <summary>The builder calls of HMDeviceExtractor.Extract
    /// (HMDeviceExtractor.cs:100-155), in its order, from the entry's
    /// identity.</summary>
    static HMProfile Build(Entry e, ushort topPage, ushort topUsage)
    {
        string type = topPage switch
        {
            0x01 => topUsage switch { 0x02 => "other", 0x04 => "joystick", 0x05 => "gamepad", 0x06 => "other", 0x08 => "other", _ => "other" },
            _ => "other",
        };
        string connection = e.Connection is "bluetooth" or "bluetoothle" ? "bluetooth" : "usb";
        string idSeed = e.Product ?? $"vid{e.Vid:x4}pid{e.Pid:x4}";
        string id = (string)s_slugify.Invoke(null, new object[] { idSeed })!;
        string name = e.Product ?? $"Device {e.Vid:X4}:{e.Pid:X4}";
        string vendor = e.Manufacturer ?? "Unknown";
        string notes =
            $"Extracted by HMDeviceExtractor on {FixedStamp} UTC. " +
            "Descriptor reconstructed from Windows preparsed data (HIDAPI algorithm); " +
            "logically equivalent to the physical device's HID report descriptor but " +
            "not guaranteed byte-identical.";
        string resolvedProductString = e.Product ?? name;
        return new HMProfileBuilder()
            .Id(id)
            .Name(name)
            .Vendor(vendor)
            .Vid(e.Vid)
            .Pid(e.Pid)
            .ProductString(resolvedProductString)
            .ManufacturerString(e.Manufacturer ?? "")
            .DeviceDescription(resolvedProductString)
            .Type(type)
            .Connection(connection)
            .Descriptor(e.Descriptor)
            .Notes(notes)
            .Build();
    }

    /// <summary>The predicates case 1's predicates stream reads, by name,
    /// "name=value" pairs (Case1Submit.Predicates).</summary>
    static string Predicates(ControllerProfile p)
    {
        string[] names =
        {
            "VendorId", "ProductId", "DriverPid", "Connection", "DriverMode", "Backend", "UsesXinputhid", "UsesXusb22",
            "UsesUpperFilter", "UpperFilterName", "IsXboxBranded", "RequiresXusbCompanion", "CompanionOnly",
            "RequiresUsbipBackend", "SixaxisCompatible", "IsKeyboardOrMouse", "HasDescriptor", "Type", "Name", "ProductString",
        };
        var sb = new StringBuilder(p.Id);
        foreach (var name in names)
        {
            // v2.0.0 keeps the sixaxisCompatible key on an internal member
                // with a name of no device's, read here under the old name so
                // the line stays the same (CompatModelKey).
                var prop = name == "SixaxisCompatible"
                    ? typeof(ControllerProfile).GetProperty("CompatModelKey", BindingFlags.Instance | BindingFlags.NonPublic)
                    : typeof(ControllerProfile).GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            object? v = prop?.GetValue(p);
            string text = prop == null ? "absent" : v switch
            {
                null => "null",
                ushort u => $"0x{u:X4}",
                bool b => b ? "1" : "0",
                _ => v.ToString() ?? "",
            };
            sb.Append(' ').Append(name).Append('=').Append(text);
        }
        return sb.ToString();
    }

    /// <summary>The controller's submit path for the scripted state set,
    /// each state once, on the case's own section (Case1Submit.Create). A
    /// state with no sensor timestamp carries one, as in case 1.</summary>
    static void Submit(GoldenCase golden, ControllerProfile p, int index, IntPtr view, byte[] section, byte[] frame)
    {
        Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, view, SharedMemoryIO.SHARED_INPUT_SIZE);
        HMController c;
        try { c = Case1Submit.Create(p, index, view); }
        catch (Exception ex)
        {
            golden.Out("create.throw", Encoding.ASCII.GetBytes((ex is TargetInvocationException t ? t.InnerException ?? ex : ex).GetType().FullName ?? "?"));
            Case1Submit.Forget(index);
            return;
        }
        try
        {
            golden.Explainer = new FrameExplainer(c);
            object? flag = typeof(HMController).GetField("_switchProtocol", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(c)
                ?? typeof(HMController).GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(c) is { };
            golden.Out("switch.protocol", new[] { flag is bool on ? (byte)(on ? 1 : 0) : (byte)0xFF });
            IReadOnlyList<HMAxis> axes;
            try { axes = AxesOf(p); }
            catch (Exception ex)
            {
                golden.Out("axes.throw", Encoding.ASCII.GetBytes(ex.GetType().FullName ?? "?"));
                return;
            }
            var scratch = new Dictionary<HMAxis, float>();
            uint stamp = 0;
            foreach (var state in States.Scripted(axes, scratch))
            {
                var s = state;
                stamp += 12000;
                if (s.SensorTimestamp == 0) s.SensorTimestamp = stamp;
                golden.SetInput(in s);
                try { c.SubmitState(in s); }
                catch (Exception ex)
                {
                    golden.Out("throw", Encoding.ASCII.GetBytes(ex.GetType().FullName ?? "?"));
                    continue;
                }
                Marshal.Copy(view, section, 0, section.Length);
                golden.Out("frame", Case1Submit.Frame(section, frame));
            }
        }
        finally { Case1Submit.Forget(index); }
    }

    /// <summary>The analog inputs a profile reads, as case 1 lists them.</summary>
    static IReadOnlyList<HMAxis> AxesOf(ControllerProfile p)
    {
        var set = new SortedSet<HMAxis> { HMAxis.X, HMAxis.Y, HMAxis.Z, HMAxis.Rx, HMAxis.Ry, HMAxis.Rz };
        if (p.HasDescriptor)
            foreach (var a in p.GetOrBuildReportBuilder().AxisFields.Keys) set.Add(a);
        var hp = new HMProfile(p);
        foreach (var s in hp.Sticks) { set.Add(s.XAxis); if (s.YAxis != HMAxis.None) set.Add(s.YAxis); }
        foreach (var t in hp.Triggers) set.Add(t.Axis);
        set.Remove(HMAxis.None);
        return set.ToList();
    }

    static string ProbeDir() =>
        typeof(Case9PhysicalCorpus).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "ProbeDir").Value!;
}
