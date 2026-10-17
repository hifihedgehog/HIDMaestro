using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Programs;

/// <summary>Each validation rule of the model groups, the programs, the
/// protocol and the stream, broken by one edit of a valid profile: the load
/// must refuse it with the JSON path of the value at fault. Under the JIT the
/// check also maps each refusal to the line that threw it and lists every
/// throw in the validating sources no case reaches.</summary>
internal static partial class RuleChecks
{
    static void Fail(string text) => Checks.Fail(text);

    /// <summary>One broken profile: the rule, the profile it edits, the
    /// edit, and the path the refusal must name.</summary>
    internal sealed record Case(string Rule, string Base, string Expect, Action<JsonNode>? Edit = null, Func<string, string>? Text = null)
    {
        /// <summary>Read through the shared parse alone, as the build tool
        /// reads a file, for a file the profile's own parse refuses first.</summary>
        public bool Shared { get; init; }
        /// <summary>Also read through the shared parse alone, which must refuse
        /// it at the same path, as the build tool would.</summary>
        public bool Tool { get; init; }
    }

    /// <summary>The run-time loader on one file's text: the profile parse,
    /// the model groups' shared parse, and the device model's validation as a
    /// create runs it. Null when the file loads, else the refusal's path,
    /// reason and exception.</summary>
    public static (string Path, string Reason, Exception Error)? Load(string text)
    {
        try
        {
            var p = ControllerProfile.Parse(Encoding.UTF8.GetBytes(text));
            if (p == null) return ("$", "the file reads as nothing", new InvalidDataException());
            p.ValidateDeviceModel();
            return null;
        }
        catch (FormException e) { return (e.Path, e.Reason, e); }
        catch (InvalidOperationException e) { return Split(e.Message, e); }
        catch (JsonException e) { return ("$" + (e.Path is { Length: > 1 } jp ? jp[1..] : ""), e.Message, e); }
        catch (Exception e) { return ("(threw " + e.GetType().Name + ")", e.Message, e); }
    }

    [GeneratedRegex(@"^Profile '[^']*': (\$?[A-Za-z$][^\s:]*)[:\s]\s*(.*)$", RegexOptions.Singleline)]
    private static partial Regex ProfileMessage();

    /// <summary>The path and reason of a refusal the SDK words as "Profile
    /// 'id': path: reason". Older checks name a path from the root without
    /// "$.", which this adds.</summary>
    static (string, string, Exception) Split(string message, Exception e)
    {
        var m = ProfileMessage().Match(message);
        if (!m.Success) return ("(no path)", message, e);
        string path = m.Groups[1].Value;
        // "fields[3].repeat's last frame": the possessive is prose, not a
        // step, as DD-2's Locate reads it.
        if (path.EndsWith("'s", StringComparison.Ordinal)) path = path[..^2];
        return (path.StartsWith('$') ? path : "$." + path, m.Groups[2].Value, e);
    }

    /// <summary>The shared parse and compile alone, as the build tool runs
    /// them.</summary>
    static (string Path, string Reason, Exception Error)? LoadShared(string text)
    {
        try { ModelCompiler.Compile(ProfilePrograms.Read(Encoding.UTF8.GetBytes(text))); return null; }
        catch (FormException e) { return (e.Path, e.Reason, e); }
        catch (Exception e) { return ("(threw " + e.GetType().Name + ")", e.Message, e); }
    }

    /// <summary>One file through the run-time loader, printed as the build
    /// tool's check-file prints it, so the two can be compared.</summary>
    public static string ParseFile(string file)
    {
        string name = Path.GetFileName(file);
        try
        {
            var model = ModelCompiler.Compile(ProfilePrograms.Read(File.ReadAllBytes(file)));
            return $"{name}: {model.AllPrograms.Count()} programs valid";
        }
        catch (FormException e) { return $"{name}: refused at {e.Path}: {e.Reason}"; }
    }

    static readonly Dictionary<string, string> s_bases = new(StringComparer.Ordinal);

    /// <summary>A base profile's text: "every-op" is user/every-op.json, any
    /// other name a catalog profile id.</summary>
    static string BaseText(string name)
    {
        if (s_bases.TryGetValue(name, out var text)) return text;
        if (name == "every-op") text = File.ReadAllText(Path.Combine(UserChecks.DefaultDir(), "every-op.json"));
        else
        {
            var asm = typeof(HMContext).Assembly;
            text = null;
            foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal)))
            {
                using var st = asm.GetManifestResourceStream(res)!;
                using var sr = new StreamReader(st, Encoding.UTF8);
                string t = sr.ReadToEnd();
                if (t.Contains($"\"id\":\"{name}\"", StringComparison.Ordinal) || t.Contains($"\"id\": \"{name}\"", StringComparison.Ordinal)) { text = t; break; }
            }
            if (text == null) throw new InvalidOperationException($"no catalog profile {name}");
        }
        return s_bases[name] = text;
    }

    // ── Editing a profile by path ────────────────────────────────────────

    /// <summary>The steps of a path such as $.a.b[2]["c d"]: a string is a
    /// property, an int an array index.</summary>
    static List<object> Steps(string path)
    {
        var steps = new List<object>();
        int i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                int j = i + 1;
                while (j < path.Length && path[j] != '.' && path[j] != '[') j++;
                steps.Add(path[(i + 1)..j]);
                i = j;
            }
            else if (path[i] == '[' && i + 1 < path.Length && path[i + 1] == '"')
            {
                int j = path.IndexOf("\"]", i + 2, StringComparison.Ordinal);
                steps.Add(path[(i + 2)..j]);
                i = j + 2;
            }
            else if (path[i] == '[')
            {
                int j = path.IndexOf(']', i);
                steps.Add(int.Parse(path[(i + 1)..j]));
                i = j + 1;
            }
            else throw new FormatException(path);
        }
        return steps;
    }

    static JsonNode Parent(JsonNode root, List<object> steps)
    {
        JsonNode node = root;
        for (int k = 0; k < steps.Count - 1; k++)
            node = steps[k] is int n ? node[n]! : node[(string)steps[k]] ?? throw new InvalidOperationException($"no {steps[k]} on the path");
        return node;
    }

    /// <summary>Sets the value at <paramref name="path"/> to the JSON text
    /// <paramref name="json"/>, adding a property that is not there.</summary>
    public static Action<JsonNode> Set(string path, string json) => root =>
    {
        var steps = Steps(path);
        var parent = Parent(root, steps);
        var value = JsonNode.Parse(json);
        if (steps[^1] is int n) parent[n] = value; else parent[(string)steps[^1]] = value;
    };

    /// <summary>Removes the value at <paramref name="path"/>.</summary>
    public static Action<JsonNode> Remove(string path) => root =>
    {
        var steps = Steps(path);
        var parent = Parent(root, steps);
        if (steps[^1] is int n) parent.AsArray().RemoveAt(n); else parent.AsObject().Remove((string)steps[^1]);
    };

    /// <summary>Appends the JSON text <paramref name="json"/> to the array at
    /// <paramref name="path"/>.</summary>
    public static Action<JsonNode> Append(string path, string json) => root =>
    {
        var steps = Steps(path);
        var node = Parent(root, steps);
        node = steps[^1] is int n ? node[n]! : node[(string)steps[^1]]!;
        node.AsArray().Add(JsonNode.Parse(json));
    };

    public static Action<JsonNode> All(params Action<JsonNode>[] edits) => root => { foreach (var e in edits) e(root); };

    // ── Coverage ─────────────────────────────────────────────────────────


    /// <summary>Records the message of each exception of the chain: the
    /// rule's own refusal and any rethrow that adds the profile's path.</summary>
    public static void Record(Exception e)
    {
        for (Exception? x = e; x != null; x = x.InnerException) s_messages.Add(x.Message);
    }

    static readonly List<string> s_messages = new();

    /// <summary>The text a throw writes, as a pattern: the literal text of
    /// its string arguments in order, with each interpolation hole and the
    /// code between two literals matching anything. The throw ends where its
    /// parentheses close. Null for a throw whose text is all holes, such as a
    /// rethrow that adds a path, which every refusal through it would match.</summary>
    static Regex? Template(string[] lines, int at)
    {
        var text = new StringBuilder();
        for (int k = at; k < Math.Min(lines.Length, at + 8); k++)
        {
            int start = k == at ? Math.Max(0, lines[k].IndexOf("throw ", StringComparison.Ordinal)) : 0;
            text.Append(lines[k], start, lines[k].Length - start).Append('\n');
        }
        string s = text.ToString();
        var parts = new List<string>();
        var piece = new StringBuilder();
        void Flush()
        {
            string t = piece.ToString();
            if (t.Trim().Length > 2) parts.Add(Regex.Escape(t));
            piece.Clear();
        }
        // Skips a string literal starting at i (on its quote), returning the
        // index after it. Used inside holes, where the text is code.
        int SkipString(int i)
        {
            for (i++; i < s.Length && s[i] != '"'; i++) if (s[i] == '\\') i++;
            return i + 1;
        }
        int depth = 0;
        bool opened = false, choice = false;
        var literals = new List<List<string>>();
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '(') { depth++; opened = true; continue; }
            if (ch == ')') { if (--depth == 0 && opened) break; continue; }
            if (ch == '?' && i + 1 < s.Length && s[i + 1] == ' ') choice = true;
            if (ch != '"') continue;
            int before = parts.Count;
            bool interpolated = i > 0 && s[i - 1] == '$';
            for (i++; i < s.Length && s[i] != '"'; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length) { piece.Append(s[i + 1] == 'n' ? '\n' : s[i + 1]); i++; continue; }
                if (interpolated && s[i] == '{' && i + 1 < s.Length && s[i + 1] == '{') { piece.Append('{'); i++; continue; }
                if (interpolated && s[i] == '}' && i + 1 < s.Length && s[i + 1] == '}') { piece.Append('}'); i++; continue; }
                if (interpolated && s[i] == '{')
                {
                    Flush();
                    int braces = 1;
                    for (i++; i < s.Length && braces > 0; i++)
                    {
                        if (s[i] == '"') { i = SkipString(i) - 1; continue; }
                        if (s[i] == '{') braces++;
                        else if (s[i] == '}') braces--;
                    }
                    i--;
                    continue;
                }
                piece.Append(s[i]);
            }
            Flush();
            if (parts.Count > before) literals.Add(parts.GetRange(before, parts.Count - before));
        }
        if (parts.Count == 0) return null;
        // A throw that chooses its text (cond ? "a" : "b") matches either
        // literal.
        string pattern = choice
            ? string.Join("|", literals.Select(l => "(?:" + string.Join(".*?", l) + ")"))
            : string.Join(".*?", parts);
        return new Regex(pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    /// <summary>The validating sources and the throws in each.</summary>
    static readonly (string file, Regex site)[] Sources =
    {
        (@"sdk\HIDMaestro.Core\ProgramForm\ProgramForm.cs", new(@"throw new FormException\(")),
        (@"sdk\HIDMaestro.Core\ProgramForm\ModelCompiler.cs", new(@"throw new FormException\(")),
        (@"sdk\HIDMaestro.Core\ProgramForm\ModelJson.cs", new(@"throw new FormException\(")),
        (@"sdk\HIDMaestro.Core\Internal\Usbip\DeviceProtocol.cs", new(@"throw Fail\(")),
        (@"sdk\HIDMaestro.Core\Internal\StreamClock.cs", new(@"throw new InvalidOperationException\(")),
    };

    static string? SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, Sources[0].file))) return dir.FullName;
        return null;
    }

    static void Coverage()
    {
        string? root = SourceRoot();
        if (root == null) { Console.WriteLine("rule coverage: the sources are not beside the probe"); return; }
        int sites = 0, missed = 0, rethrows = 0;
        foreach (var (file, site) in Sources)
        {
            var lines = File.ReadAllLines(Path.Combine(root, file));
            int here = 0, hereMissed = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!site.IsMatch(lines[i]) || lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                var pattern = Template(lines, i);
                if (pattern == null) { rethrows++; continue; }
                here++;
                if (s_messages.Any(m => pattern.IsMatch(m))) continue;
                hereMissed++;
                Fail($"no case reaches {Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                if (Environment.GetEnvironmentVariable("HM_PEC_TRACE") == "1") Console.WriteLine("  pattern " + pattern);
            }
            Console.WriteLine($"{Path.GetFileName(file)}: {here - hereMissed} of {here} refusals reached");
            sites += here; missed += hereMissed;
        }
        Console.WriteLine($"rule coverage: {sites - missed} of {sites} refusals reached by a case, each matched by its text; {rethrows} rethrows that add a path to a refusal");
    }

    // ── The run ──────────────────────────────────────────────────────────

    public static int Run()
    {
        int before = Checks.Failures;
        Console.WriteLine("== Validation: each rule broken by one edit, refused with the path of the value at fault");
        // The program form's own rules: the corpus's negatives (checked with
        // their paths in Checks), and the rules only a program built in code
        // reaches, since the profile's parse refuses the same value first.
        foreach (var (rule, p, path) in Corpus.Negatives().Concat(Round5Cases.RefusedCenters.Select(x => (x.program.Name, x.program, x.path)))
                     .Concat(Round5Cases.RefusedOverTemplate.Select(x => (x.program.Name, x.program, x.path))))
            try { ProgramForm.Validate(p); }
            catch (FormException e) { Record(e); }
        foreach (var (rule, p, path) in FormNegatives().Concat(BoundsNegatives()).Concat(StateChecks.Negatives()))
            try { ProgramForm.Validate(p); Fail($"accepted: {rule}"); }
            catch (FormException e)
            {
                Record(e);
                if (e.Path == path) Console.WriteLine($"refused: {rule}: {e.Message}");
                else Fail($"{rule}: refused at {e.Path}, not {path}: {e.Reason}");
            }
            catch (Exception e) { Fail($"{rule}: threw {e.GetType().Name}, not a refusal: {e.Message}"); }
        foreach (var c in Cases())
        {
            string text = BaseText(c.Base);
            if (c.Edit != null)
            {
                var root = JsonNode.Parse(text)!;
                try { c.Edit(root); }
                catch (Exception e) when (e is InvalidOperationException or NullReferenceException or ArgumentException or FormatException)
                { Fail($"{c.Rule}: the edit does not apply to {c.Base}: {e.Message}"); continue; }
                text = root.ToJsonString();
            }
            if (c.Text != null) text = c.Text(text);
            var r = c.Shared ? LoadShared(text) : Load(text);
            if (r == null) { Fail($"{c.Rule}: loads, where {c.Expect} must be refused"); continue; }
            Record(r.Value.Error);
            if (r.Value.Path == c.Expect) Console.WriteLine($"refused: {c.Rule}: {r.Value.Path}: {r.Value.Reason}");
            else Fail($"{c.Rule}: refused at {r.Value.Path}, not {c.Expect}: {r.Value.Reason}");
            // What a load filter written as lane DD-2's reads from the same
            // exception: a defect, at this path.
            if (!c.Shared)
            {
                var err = r.Value.Error;
                if (!Dd2View.IsDefect(err)) Fail($"{c.Rule}: {err.GetType().Name} is not a profile defect to DD-2's load");
                else if (Dd2View.Locate(err) is var at && at != Dd2View.Reachable(c.Expect))
                    Fail($"{c.Rule}: DD-2's load reads the path {at}, not {Dd2View.Reachable(c.Expect)}: {err.Message}");
            }
            if (c.Tool && LoadShared(text) is var t && t?.Path != c.Expect)
                Fail($"{c.Rule}: the build tool's parse gives {(t == null ? "no refusal" : $"{t.Value.Path}: {t.Value.Reason}")}, not a refusal at {c.Expect}");
        }
        // Every base loads unedited, so each case's refusal is its edit's.
        foreach (var name in Cases().Select(c => c.Base).Distinct())
            if (Load(BaseText(name)) is { } r) Fail($"the base {name} itself is refused at {r.Path}: {r.Reason}");
        Coverage();
        return Checks.Failures - before;
    }
}
