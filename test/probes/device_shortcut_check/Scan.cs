// One run of the checks over a set of files: every file is read first, for
// the constants a comparison may name, then checked.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

internal sealed record Hit(string File, int Line, string Group, string Kind, string Name, string Text);

internal enum SourceKind { CSharp, C, Rc, Inf }

internal static class HitGroups
{
    public const string File = "file names";
    public const string Name = "declared names";
    public const string Vendor = "vendor IDs";
    public const string Product = "product IDs";
    public const string DeviceString = "device names in strings";
    public const string Inf = "INF model lines";
    public const string Profile = "profile ids";
    public const string Property = "single-profile properties";
    public const string Marker = "frozen-compat markers";
    public const string Removed = "removed regions";
    public const string Catalog = "catalog profiles not loaded";
    public static readonly string[] All = { File, Name, Vendor, Product, DeviceString, Inf, Profile, Property, Marker, Removed, Catalog };
}

// The lines of one source of the tree that carry a removed region's printed
// code lines, and how many of its prints they hold.
internal sealed record RegionFound(RemovedRegion Region, string File, int[] Lines, int Prints);

internal sealed class Scan
{
    sealed class Source
    {
        public string Rel = "", Below = "", Text = "";
        public string[] Lines = Array.Empty<string>();
        public SourceKind Kind;
        public Lexer? Lex;
        public string Line(int n) => n >= 1 && n <= Lines.Length ? Trim(Lines[n - 1]) : "";
    }

    readonly Catalog cat;
    readonly RegionPrints prints;
    readonly IReadOnlyList<RemovedRegion> regions;
    readonly AllowedName[] allowed;
    readonly List<Source> sources = new();
    readonly Dictionary<string, List<ConstantDef>> constants = new(StringComparer.Ordinal);
    readonly Dictionary<string, Macro> macros = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<string[]>> signatures = new(StringComparer.Ordinal);
    readonly VendoredFile[] vendored;

    public readonly List<Hit> Hits = new();
    public readonly List<RegionFound> Regions = new();
    public readonly List<(AllowedName Entry, string File, int Line, string Name)> AllowedNames = new();
    public readonly List<(AllowedInfLine Entry, string File, int Line)> AllowedInf = new();
    public readonly List<(AllowedString Entry, Hit Hit)> AllowedStrings = new();
    public readonly List<(string File, int Line, string Kind, string Text)> Infos = new();
    public readonly List<string> Coincidences = new();
    public readonly List<(string File, Decl D)> Names = new();
    /// <summary>Hits of rules A to C in a vendored file whose hash holds.</summary>
    public readonly List<(VendoredFile Entry, Hit Hit)> VendoredHits = new();

    readonly AllowedInfLine[] allowedInf;
    readonly AllowedString[] allowedStrings;

    public Scan(Catalog catalog, RegionPrints prints, IReadOnlyList<RemovedRegion> regions, AllowedName[] allowed, AllowedInfLine[] allowedInf,
                VendoredFile[] vendored, AllowedString[]? allowedStrings = null)
    {
        this.allowedStrings = allowedStrings ?? Rules.Strings;
        cat = catalog;
        this.prints = prints;
        this.regions = regions;
        this.allowed = allowed;
        this.allowedInf = allowedInf;
        this.vendored = vendored;
    }

    // rel: the path printed. below: the path under the scan root
    // (sdk\HIDMaestro.Core or driver), whose segments are names too.
    public void Add(string rel, string below, string text, SourceKind kind)
    {
        var s = new Source { Rel = rel, Below = below, Text = text, Lines = text.Split('\n'), Kind = kind };
        if (kind != SourceKind.Inf)
        {
            s.Lex = new Lexer(text, kind == SourceKind.CSharp ? Lang.CSharp : Lang.C).Run();
            LiteralChecks.CollectConstants(s.Lex.Tokens, rel, constants);
            if (kind == SourceKind.C) RegionPrints.CollectMacros(s.Lex.Tokens, macros);
            var sig = kind == SourceKind.CSharp ? new Locals(s.Lex.Tokens, csharp: true) : new Locals(WithoutDirectives(s.Lex.Tokens), csharp: false);
            // A parameter the body compares with an expression named for an
            // ID binds a call's argument as that ID (IsVendor(p, int x) =>
            // p.VendorId == x).
            foreach (var (name, ps, first, last) in sig.Signatures)
            {
                if (!signatures.TryGetValue(name, out var list)) signatures[name] = list = new List<string[]>();
                list.Add(first == null || last == null ? ps : LiteralChecks.ParamsComparedWithIds(s.Lex.Tokens, first, last, ps));
            }
        }
        sources.Add(s);
    }

    public void Run()
    {
        foreach (var s in sources)
        {
            int before = Hits.Count, infos = Infos.Count, coincidences = Coincidences.Count;
            FileNames(s);
            if (s.Kind == SourceKind.Inf) Inf(s);
            else
            {
                DeclaredNames(s);
                new LiteralChecks(s.Lex!.Tokens, cat, constants, signatures, s.Rel, new Sink(this, s)).Run();
            }
            // A vendored file's hits of rules A to C are listed as allowed
            // while its hash holds. The marker and region rules still apply.
            string hash = Rules.TextSha256(System.Text.Encoding.UTF8.GetBytes(s.Text));
            var entry = vendored.FirstOrDefault(v => v.Path.Equals(s.Rel, StringComparison.OrdinalIgnoreCase)
                                                     && v.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
            if (entry != null)
            {
                var own = Hits.Skip(before).Where(h => h.File == s.Rel).ToList();
                foreach (var h in own) VendoredHits.Add((entry, h));
                var kept = Hits.Skip(before).Where(h => h.File != s.Rel).ToList();
                Hits.RemoveRange(before, Hits.Count - before);
                Hits.AddRange(kept);
                Infos.RemoveRange(infos, Infos.Count - infos);
                Coincidences.RemoveRange(coincidences, Coincidences.Count - coincidences);
            }
            Markers(s);
            if (s.Kind == SourceKind.C) RemovedCode(s);
        }
        // A string a constant carries is a hit once the constant is used, not information.
        Infos.RemoveAll(i => Hits.Any(h => h.Group == HitGroups.DeviceString && h.File == i.File && h.Line == i.Line));
        // A string the table allows, in the file it names, is listed with
        // its reason instead.
        foreach (var h in Hits.Distinct())
            if (allowedStrings.FirstOrDefault(a => a.Matches(h)) is { } entry) AllowedStrings.Add((entry, h));
        Hits.RemoveAll(h => allowedStrings.Any(a => a.Matches(h)));
    }

    sealed class Sink : ILiteralSink
    {
        readonly Scan scan;
        readonly Source src;
        public Sink(Scan scan, Source src) { this.scan = scan; this.src = src; }
        public void Hit(int line, string group, string kind, string name) => scan.Hits.Add(new Hit(src.Rel, line, group, kind, name, src.Line(line)));
        public void Info(int line, string kind, string text) => scan.Infos.Add((src.Rel, line, kind, text));
        public void Coincidence(int line, string why) => scan.Coincidences.Add($"{src.Rel}:{line}  {why}  | {src.Line(line)}");
        public void ConstantUsed(ConstantDef def, string group, string where)
        {
            var at = scan.sources.FirstOrDefault(x => x.Rel == def.File);
            string kind = def.Content != null ? "device name kept in a constant, used at " : def.Constant ? "ID constant, bound at " : "ID value, bound at ";
            var hit = new Hit(def.File, def.Line, group, kind + where, def.Name + " = " + def.Value, at?.Line(def.Line) ?? "");
            if (!scan.Hits.Contains(hit)) scan.Hits.Add(hit);
        }
    }

    // ---- results ----

    static IEnumerable<Hit> Sorted(IEnumerable<Hit> h) =>
        h.OrderBy(x => x.File, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Line).ThenBy(x => x.Kind, StringComparer.Ordinal)
         .ThenBy(x => x.Name, StringComparer.Ordinal);

    // No region exempts a hit: every hit fails.
    public List<Hit> Failing => Sorted(Hits).Distinct().ToList();

    // ---- 1. names ----

    void FileNames(Source s)
    {
        string[] segs = s.Below.Split(Path.DirectorySeparatorChar);
        for (int i = 0; i < segs.Length; i++)
        {
            bool last = i == segs.Length - 1;
            var mt = cat.Words.Match(last ? Path.GetFileNameWithoutExtension(segs[i]) : segs[i]);
            if (mt.Hit) Hits.Add(new Hit(s.Rel, 1, HitGroups.File, last ? "file" : "directory", segs[i], string.Join(", ", mt.Tokens)));
        }
    }

    void DeclaredNames(Source s)
    {
        var tokens = s.Lex!.Tokens;
        IEnumerable<Decl> decls;
        if (s.Kind == SourceKind.CSharp)
            decls = new CsDecls(tokens).Found.Concat(new Locals(tokens, csharp: true).Found);
        else if (s.Kind == SourceKind.C)
            decls = new CDecls(tokens).Found.Concat(new Locals(WithoutDirectives(tokens), csharp: false).Found);
        else
            decls = Macros(tokens);
        var seen = new HashSet<(int, string)>();
        foreach (var d in decls)
        {
            if (!seen.Add((d.Line, d.Name))) continue;
            Names.Add((s.Rel, d));
            var mt = cat.Words.Match(d.Name);
            if (!mt.Hit) continue;
            var entry = allowed.FirstOrDefault(a => a.Matches(d.Name, s.Rel));
            if (entry != null) AllowedNames.Add((entry, s.Rel, d.Line, d.Name));
            else Hits.Add(new Hit(s.Rel, d.Line, HitGroups.Name, $"{d.Kind} ({string.Join(", ", mt.Tokens)})", d.Name, s.Line(d.Line)));
        }
    }

    internal static List<Token> WithoutDirectives(List<Token> all)
    {
        var t = new List<Token>();
        bool inDir = false;
        foreach (var x in all)
        {
            if (x.Kind == Tk.DirStart) { inDir = true; continue; }
            if (x.Kind == Tk.DirEnd) { inDir = false; continue; }
            if (!inDir) t.Add(x);
        }
        return t;
    }

    static IEnumerable<Decl> Macros(List<Token> t)
    {
        for (int i = 0; i + 1 < t.Count; i++)
            if (t[i].Kind == Tk.DirStart && t[i].Text == "define" && t[i + 1].Kind is Tk.Ident or Tk.Keyword)
                yield return new Decl(t[i + 1].Line, "macro", t[i + 1].Text);
    }

    // ---- INF model lines ----

    static readonly Regex s_infId = new(@"(?<![A-Za-z])(vid|pid)(?:&0002|&02|[_&])?([0-9A-Fa-f]{4})(?![0-9A-Fa-f])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex s_chunk = new(@"[A-Za-z0-9]+", RegexOptions.CultureInvariant);
    static readonly Regex s_infToken = new(@"%([^%\s]+)%", RegexOptions.CultureInvariant);

    // The sections [Manufacturer] names (Base, and Base.Decoration for each
    // decoration) hold the model lines. A comment starts at a semicolon
    // outside quotes. A %token% on a model line is read as its [Strings]
    // value before the line is checked.
    void Inf(Source s)
    {
        var lines = s.Lines.Select(StripInfComment).ToArray();
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        foreach (string l in lines)
        {
            string x = l.Trim();
            if (x.StartsWith('[') && x.EndsWith(']')) { section = x[1..^1].Trim(); continue; }
            if (section.StartsWith("Strings", StringComparison.OrdinalIgnoreCase) && x.Contains('='))
            {
                string key = x[..x.IndexOf('=')].Trim(), value = x[(x.IndexOf('=') + 1)..].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
                strings.TryAdd(key, value);
                continue;
            }
            if (!section.Equals("Manufacturer", StringComparison.OrdinalIgnoreCase) || !x.Contains('=')) continue;
            var parts = x[(x.IndexOf('=') + 1)..].Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Count == 0) continue;
            models.Add(parts[0]);
            foreach (string dec in parts.Skip(1)) models.Add(parts[0] + "." + dec);
        }
        section = "";
        for (int i = 0; i < lines.Length; i++)
        {
            string x = lines[i].Trim();
            if (x.StartsWith('[') && x.EndsWith(']')) { section = x[1..^1].Trim(); continue; }
            if (!models.Contains(section) || x.Length == 0) continue;
            var allow = allowedInf.FirstOrDefault(a => a.File.Equals(s.Rel, StringComparison.OrdinalIgnoreCase)
                                                       && Regex.Replace(a.Text, @"\s+", " ") == Regex.Replace(x, @"\s+", " "));
            if (allow != null) { AllowedInf.Add((allow, s.Rel, i + 1)); continue; }
            x = s_infToken.Replace(x, m => strings.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
            foreach (Match mt in s_infId.Matches(x))
            {
                bool vendor = mt.Groups[1].Value.Equals("vid", StringComparison.OrdinalIgnoreCase);
                Hits.Add(new Hit(s.Rel, i + 1, vendor ? HitGroups.Vendor : HitGroups.Product, (vendor ? "vendor" : "product") + " ID in an INF model line", mt.Value, s.Line(i + 1)));
            }
            foreach (Match c in s_chunk.Matches(x))
            {
                var mt = cat.Words.Match(c.Value);
                if (mt.Hit) Hits.Add(new Hit(s.Rel, i + 1, HitGroups.Inf, "device name in an INF model line", c.Value, s.Line(i + 1)));
            }
        }
    }

    static string StripInfComment(string l)
    {
        bool q = false;
        for (int i = 0; i < l.Length; i++)
        {
            if (l[i] == '"') q = !q;
            else if (l[i] == ';' && !q) return l[..i];
        }
        return l;
    }

    // ---- rule D: no exempt region ----

    // A frozen-compat marker of any form, in a comment, in code or in an
    // INF, anywhere in the scanned sources: the two words with nothing or
    // only separators between them (frozen-compat, frozen_compat,
    // FrozenCompat, a Unicode hyphen, "frozen-" + "compat" across a line
    // break), read after NFKC folding with invisible format characters
    // removed, and in every string the literal checks fold.
    static readonly Regex s_marker = new(@"frozen[^A-Za-z0-9]{0,40}compat", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // HTML entities (&#45;) and \uXXXX and \xXX escapes decoded, NFKC
    // applied, format characters removed.
    static string Folded(string text)
    {
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\\u([0-9A-Fa-f]{4})|\\x([0-9A-Fa-f]{2})",
            m => ((char)Convert.ToInt32(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, 16)).ToString());
        return new(text.Normalize(System.Text.NormalizationForm.FormKC)
            .Where(c => char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
    }

    public static bool IsMarker(string text) => s_marker.IsMatch(Folded(text));

    void Markers(Source s)
    {
        var lines = new SortedSet<int>();
        for (int n = 1; n <= s.Lines.Length; n++)
        {
            // The line and the next two, so a marker split across a line
            // break is read whole, reported at the line it starts on.
            string window = string.Join("\n", s.Lines.Skip(n - 1).Take(3));
            var mt = s_marker.Match(Folded(window));
            if (!mt.Success) continue;
            string head = Folded(s.Lines[n - 1]);
            if (mt.Index < head.Length) lines.Add(n);
        }
        // A line the folded strings already reported is reported once.
        foreach (int n in lines)
            if (!Hits.Any(h => h.Group == HitGroups.Marker && h.File == s.Rel && h.Line == n))
                Hits.Add(new Hit(s.Rel, n, HitGroups.Marker, "frozen-compat marker", "frozen-compat", s.Line(n)));
    }

    // The prints of a removed region, in any C or C++ source under driver\,
    // as written or with the drivers' macros expanded: one hit per region
    // and source, at its first line found.
    void RemovedCode(Source s)
    {
        if (!s.Rel.StartsWith("driver" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        var found = prints.Find(s.Lex!.Tokens, macros);
        foreach (var r in regions)
        {
            if (!found.TryGetValue(r.Name, out var f)) continue;
            int first = f.Lines.Min;
            Regions.Add(new RegionFound(r, s.Rel, f.Lines.ToArray(), f.Prints.Count));
            Hits.Add(new Hit(s.Rel, first, HitGroups.Removed,
                $"{f.Prints.Count} of {prints.Count(r.Name)} prints of {r.File}:{r.Begin}-{r.End} at 9bd6279 still here, from {f.Lines.Count} lines, {r.What}, deleted by {r.Lane}",
                r.Name, s.Line(first)));
        }
    }

    static string Trim(string s)
    {
        s = s.Trim();
        return s.Length > 110 ? s.Substring(0, 107) + "..." : s;
    }
}
