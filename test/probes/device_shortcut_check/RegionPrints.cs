// The prints of the code v2.0.0 deletes from the drivers, and their search
// in a tree.
//
// A region is read as the lexer's token stream across its lines, so line
// breaks, whitespace and comments never change it. The stream is
// normalized first: directive ends dropped, an integer literal read as its
// value (0x1F, 31 and 31u are one token, NULL and FALSE are 0, TRUE is 1),
// adjacent string literals joined into one, A ## B pasted into one token,
// the parentheses around a lone identifier dropped where they are not a
// call's or a keyword's ((ctx)->X reads as ctx->X), a (void) cast dropped,
// and a comparison with 0 that ends a condition dropped (if (x != NULL)
// reads as if (x)). #define bodies are part of the stream. Each source of a
// tree is searched twice, as written and with the macros its driver
// sources define expanded (object-like and function-like, # and ##
// applied), so a region rebuilt from macros or pasted names is read as the
// code it expands to.
//
// Two kinds of print, each the first 16 hex digits of the SHA-256 of its
// window's tokens:
//   exact  every window of 6 tokens as written (names kept).
//   shape  every window of 10 tokens with each name the 9bd6279 driver
//          sources declare (functions, fields, macros, types, locals,
//          parameters) read as one placeholder. A name they only use (a
//          Windows type, API or constant) stays. Renaming the region's
//          names leaves its shape prints as they were.
// A window is printed only when it holds content of the region's own and
// no driver source at 9bd6279 holds it outside the regions (the dispatch
// lines the plan keeps count as outside, Rules.Kept). Its own content is
// one of:
//   - a name the regions alone use that the drivers declare as a function,
//     field, macro, type, enum constant or global, or a name holding a
//     device word (exact prints only, since a shape has no names),
//   - a string or an integer of 0x100 or more that only the regions hold,
//   - a row of data: integer literals separated by commas alone, three in
//     an exact window and four in a shape, of three values or more (the
//     Sony calibration's 0x40, 0x1F, 0xC0, the default descriptor's
//     bytes).
// So a local such as ULONG i, a switch on cmd, case 0x02, the row
// 0x00, 0x00 or buf[3] = 0 never prints alone: none carries such content.
//
// A print two regions share is reported for the region among them that
// also has a print of its own in the source, or for all of them when none
// has.
//
// What still evades the prints. A region whose own content is only its
// names, once every such name is renamed: its shape is generic code a
// general mechanism may also write. With every name renamed and the code
// wrapped at another width, 11 regions are still caught by their data and
// strings (sony-calibration-crc, switch-config, bluetooth-read,
// ds3-sixaxis-read, switch-responder, sony-feature-table, xusb-answers and
// the four default-device regions) and 18 are not (the DualShock 3
// helpers, input, output, feature and write branches, the Switch input
// return, worker mapping, stream start and cleanup, read and write
// branches, the context field blocks, the rumble gate's state and the
// default descriptor's copy). Code rewritten in another form (another
// statement order, another control flow, other literal values). Macros a
// tree takes from a file outside driver\.
//
// removed-regions.txt is written by "--write-regions <tree> <file>" from a
// tree whose driver\ is 9bd6279's and is pinned by its SHA-256 in
// Rules.RegionPrintsSha256. A region with no print, a file that does not
// name every region of the table, or a file whose hash differs fails the
// check, so no region can stay unseen and no edit of the file turns the
// rule off.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

// One token of a normalized stream: its key as written, its key with
// declared names as a placeholder, its line, and what it is.
internal readonly record struct NTok(string Key, string Shape, int Line, Tk Kind, ulong Num, bool IsInt);

// A macro a driver source defines: its parameters (null when object-like)
// and its body.
internal sealed record Macro(string Name, List<string>? Params, List<Token> Body);

internal sealed class RegionPrints
{
    public const string FileName = "removed-regions.txt";
    public const int ExactWindow = 6;
    public const int ShapeWindow = 10;

    /// <summary>Each region's prints, by region name ("x" exact or "s"
    /// shape, a space, the print).</summary>
    public readonly Dictionary<string, HashSet<string>> ByRegion = new(StringComparer.Ordinal);

    /// <summary>The regions each print belongs to.</summary>
    readonly Dictionary<string, List<string>> owners = new(StringComparer.Ordinal);

    /// <summary>The names 9bd6279's driver sources use and do not declare,
    /// which a shape keeps as written.</summary>
    public readonly HashSet<string> External = new(StringComparer.Ordinal);

    public int Count(string region) => ByRegion.TryGetValue(region, out var set) ? set.Count : 0;
    public int Count(string region, char kind) =>
        ByRegion.TryGetValue(region, out var set) ? set.Count(p => p[0] == kind) : 0;

    public IReadOnlyList<string> Owners(string print) =>
        owners.TryGetValue(print, out var list) ? list : (IReadOnlyList<string>)Array.Empty<string>();

    void Add(string region, string print)
    {
        if (!ByRegion.TryGetValue(region, out var set)) ByRegion[region] = set = new HashSet<string>(StringComparer.Ordinal);
        if (!set.Add(print)) return;
        if (!owners.TryGetValue(print, out var list)) owners[print] = list = new List<string>();
        list.Add(region);
    }

    public static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];

    // ---- normalization ----

    /// <summary>The normalized stream of raw lexer tokens. external names
    /// stay in a shape, every other name is "$".</summary>
    public static List<NTok> Normalize(IReadOnlyList<Token> raw, ISet<string> external)
    {
        // Pasting and joined strings first.
        var items = new List<Token>();
        for (int i = 0; i < raw.Count; i++)
        {
            var x = raw[i];
            if (x.Kind == Tk.DirEnd) continue;
            if (x.Kind == Tk.Punct && x.Text == "##" && items.Count > 0 && i + 1 < raw.Count
                && Pastable(items[^1]) && Pastable(raw[i + 1]))
            {
                items[^1] = Paste(items[^1], raw[i + 1]);
                i++;
                continue;
            }
            if (x.Kind == Tk.String && items.Count > 0 && items[^1].Kind == Tk.String && items[^1].Interp == x.Interp)
            {
                var prev = items[^1];
                items[^1] = new Token { Kind = Tk.String, Text = "\"" + prev.Content + x.Content + "\"", Content = prev.Content + x.Content, Line = prev.Line, Start = prev.Start, End = x.End, Interp = prev.Interp };
                continue;
            }
            items.Add(x);
        }
        // NULL, nullptr and FALSE read as 0, TRUE as 1.
        for (int i = 0; i < items.Count; i++)
            if (items[i].Kind == Tk.Ident && items[i].Text is "NULL" or "nullptr" or "FALSE" or "TRUE")
                items[i] = new Token { Kind = Tk.Number, Text = items[i].Text == "TRUE" ? "1" : "0", IsInt = true, Value = items[i].Text == "TRUE" ? 1UL : 0UL, Line = items[i].Line, Start = items[i].Start, End = items[i].End };
        // ( name ) that is not a call's, a keyword's or an index's group, a
        // (void) cast, and a comparison with 0 that ends a condition
        // (x != 0) reads as x).
        var outList = new List<Token>();
        static bool P(Token x, string text) => x.Kind == Tk.Punct && x.Text == text;
        for (int i = 0; i < items.Count; i++)
        {
            if (P(items[i], "(") && i + 2 < items.Count && items[i + 1].Kind == Tk.Keyword && items[i + 1].Text == "void" && P(items[i + 2], ")")
                && i + 3 < items.Count && items[i + 3].Kind == Tk.Ident)
            {
                i += 2;
                continue;
            }
            if (P(items[i], "!=") && i + 2 < items.Count && items[i + 1].Kind == Tk.Number && items[i + 1].IsInt && items[i + 1].Value == 0
                && items[i + 2].Kind == Tk.Punct && items[i + 2].Text is ")" or "&&" or "||" or "?")
            {
                i += 1;
                continue;
            }
            if (items[i].Kind == Tk.Punct && items[i].Text == "(" && i + 2 < items.Count && items[i + 1].Kind == Tk.Ident
                && items[i + 2].Kind == Tk.Punct && items[i + 2].Text == ")")
            {
                var before = outList.Count > 0 ? outList[^1] : null;
                bool group = before != null && (before.Kind is Tk.Ident or Tk.Keyword || (before.Kind == Tk.Punct && before.Text is ")" or "]"));
                if (!group)
                {
                    outList.Add(items[i + 1]);
                    i += 2;
                    continue;
                }
            }
            outList.Add(items[i]);
        }
        var result = new List<NTok>(outList.Count);
        foreach (var x in outList)
        {
            string key = x.Kind switch
            {
                Tk.Ident => "i" + x.Text,
                Tk.Keyword => "k" + x.Text,
                Tk.Number => x.IsInt ? "n" + x.Value : "f" + x.Text.ToLowerInvariant(),
                Tk.String => "s" + x.Content,
                Tk.Char => "c" + x.Content,
                Tk.DirStart => "d" + x.Text,
                _ => "p" + x.Text,
            };
            string shape = x.Kind == Tk.Ident && !external.Contains(x.Text) ? "$" : key;
            result.Add(new NTok(key, shape, x.Line, x.Kind, x.Value, x.Kind == Tk.Number && x.IsInt));
        }
        return result;
    }

    static bool Pastable(Token x) => x.Kind is Tk.Ident or Tk.Keyword or Tk.Number;

    static Token Paste(Token a, Token b)
    {
        string text = a.Text + b.Text;
        var lexed = new Lexer(text, Lang.C).Run().Tokens;
        if (lexed.Count == 1)
        {
            var t = lexed[0];
            t.Line = a.Line;
            t.Start = a.Start;
            t.End = b.End;
            return t;
        }
        return new Token { Kind = Tk.Ident, Text = text, Line = a.Line, Start = a.Start, End = b.End };
    }

    // ---- macros ----

    /// <summary>The macros #define'd in a source's tokens.</summary>
    public static void CollectMacros(IReadOnlyList<Token> t, Dictionary<string, Macro> into)
    {
        for (int i = 0; i + 1 < t.Count; i++)
        {
            if (t[i].Kind != Tk.DirStart || t[i].Text != "define" || t[i + 1].Kind is not (Tk.Ident or Tk.Keyword)) continue;
            var name = t[i + 1];
            int k = i + 2;
            List<string>? ps = null;
            if (k < t.Count && t[k].Kind == Tk.Punct && t[k].Text == "(" && t[k].Start == name.End)
            {
                ps = new List<string>();
                for (k++; k < t.Count && t[k].Kind != Tk.DirEnd; k++)
                {
                    if (t[k].Kind == Tk.Punct && t[k].Text == ")") { k++; break; }
                    if (t[k].Kind is Tk.Ident or Tk.Keyword) ps.Add(t[k].Text);
                    else if (t[k].Kind == Tk.Punct && t[k].Text == "...") ps.Add("__VA_ARGS__");
                }
            }
            var body = new List<Token>();
            for (; k < t.Count && t[k].Kind != Tk.DirEnd; k++) body.Add(t[k]);
            into[name.Text] = new Macro(name.Text, ps, body);
        }
    }

    /// <summary>The tokens outside directives with every macro of the table
    /// expanded, each expansion carrying the line of its invocation.</summary>
    public static List<Token> Expand(IReadOnlyList<Token> all, IReadOnlyDictionary<string, Macro> macros)
    {
        var code = new List<Token>();
        bool inDir = false;
        foreach (var x in all)
        {
            if (x.Kind == Tk.DirStart) { inDir = true; continue; }
            if (x.Kind == Tk.DirEnd) { inDir = false; continue; }
            if (!inDir) code.Add(x);
        }
        return Expand(code, macros, new HashSet<string>(StringComparer.Ordinal), 0, -1);
    }

    static List<Token> Expand(IReadOnlyList<Token> input, IReadOnlyDictionary<string, Macro> macros, HashSet<string> active, int depth, int line)
    {
        var output = new List<Token>();
        for (int i = 0; i < input.Count; i++)
        {
            var x = input[i];
            if (x.Kind == Tk.Ident && depth < 16 && macros.TryGetValue(x.Text, out var mac) && !active.Contains(x.Text))
            {
                int at = line >= 0 ? line : x.Line;
                List<List<Token>>? args = null;
                int end = i;
                if (mac.Params != null)
                {
                    if (i + 1 >= input.Count || input[i + 1].Kind != Tk.Punct || input[i + 1].Text != "(") { output.Add(Relined(x, line)); continue; }
                    args = new List<List<Token>>();
                    var cur = new List<Token>();
                    int d = 0, k;
                    for (k = i + 1; k < input.Count; k++)
                    {
                        var y = input[k];
                        if (y.Kind == Tk.Punct && y.Text == "(") { if (d++ > 0) cur.Add(y); continue; }
                        if (y.Kind == Tk.Punct && y.Text == ")") { if (--d == 0) break; cur.Add(y); continue; }
                        if (y.Kind == Tk.Punct && y.Text == "," && d == 1) { args.Add(cur); cur = new List<Token>(); continue; }
                        cur.Add(y);
                    }
                    if (k >= input.Count) { output.Add(Relined(x, line)); continue; }
                    args.Add(cur);
                    if (mac.Params.Count == 0 && args.Count == 1 && args[0].Count == 0) args.Clear();
                    end = k;
                }
                var body = Substitute(mac, args, at, macros, active, depth);
                active.Add(mac.Name);
                output.AddRange(Expand(body, macros, active, depth + 1, at));
                active.Remove(mac.Name);
                i = end;
                continue;
            }
            output.Add(Relined(x, line));
        }
        return output;
    }

    static Token Relined(Token x, int line) => line < 0 || x.Line == line ? x : Clone(x, line);

    static Token Clone(Token x, int line) => new()
    {
        Kind = x.Kind, Text = x.Text, Line = line, Content = x.Content, IsInt = x.IsInt, IsHex = x.IsHex,
        Value = x.Value, HexDigits = x.HexDigits, Start = x.Start, End = x.End, Interp = x.Interp,
    };

    static List<Token> Substitute(Macro mac, List<List<Token>>? args, int line, IReadOnlyDictionary<string, Macro> macros,
                                  HashSet<string> active, int depth)
    {
        List<Token>? Arg(string name)
        {
            if (mac.Params == null || args == null) return null;
            int ix = mac.Params.IndexOf(name);
            if (ix < 0) return null;
            if (name == "__VA_ARGS__")
            {
                var rest = new List<Token>();
                for (int a = ix; a < args.Count; a++)
                {
                    if (a > ix) rest.Add(new Token { Kind = Tk.Punct, Text = ",", Line = line });
                    rest.AddRange(args[a]);
                }
                return rest;
            }
            return ix < args.Count ? args[ix] : new List<Token>();
        }
        var b = mac.Body;
        var outList = new List<Token>();
        for (int k = 0; k < b.Count; k++)
        {
            var x = b[k];
            if (x.Kind == Tk.Punct && x.Text == "#" && k + 1 < b.Count && b[k + 1].Kind == Tk.Ident && Arg(b[k + 1].Text) is List<Token> s)
            {
                string text = string.Join(" ", s.Select(q => q.Text));
                outList.Add(new Token { Kind = Tk.String, Text = "\"" + text + "\"", Content = text, Line = line });
                k++;
                continue;
            }
            if (x.Kind == Tk.Ident && Arg(x.Text) is List<Token> arg)
            {
                bool pasted = (k > 0 && b[k - 1].Text == "##") || (k + 1 < b.Count && b[k + 1].Text == "##");
                var with = pasted ? arg : Expand(arg, macros, active, depth + 1, line);
                foreach (var y in with) outList.Add(Clone(y, line));
                continue;
            }
            outList.Add(Clone(x, line));
        }
        // ## joins its neighbors.
        var pasted2 = new List<Token>();
        for (int k = 0; k < outList.Count; k++)
        {
            if (outList[k].Kind == Tk.Punct && outList[k].Text == "##" && pasted2.Count > 0 && k + 1 < outList.Count)
            {
                pasted2[^1] = Paste(pasted2[^1], outList[k + 1]);
                k++;
                continue;
            }
            pasted2.Add(outList[k]);
        }
        return pasted2;
    }

    // ---- the windows ----

    static string Key(List<NTok> s, int at, int n, bool shape)
    {
        var sb = new StringBuilder();
        for (int k = at; k < at + n; k++)
        {
            if (k > at) sb.Append('\u0001');
            sb.Append(shape ? s[k].Shape : s[k].Key);
        }
        return sb.ToString();
    }

    /// <summary>Every window of a stream: kind, key and the line of its
    /// first token.</summary>
    public static IEnumerable<(char Kind, string Key, int Line, int At)> Windows(List<NTok> s)
    {
        for (int at = 0; at + ExactWindow <= s.Count; at++) yield return ('x', Key(s, at, ExactWindow, false), s[at].Line, at);
        for (int at = 0; at + ShapeWindow <= s.Count; at++) yield return ('s', Key(s, at, ShapeWindow, true), s[at].Line, at);
    }

    // ---- writing ----

    /// <summary>The prints of every region from the driver sources of a tree
    /// at 9bd6279: (path relative to the tree, text). deviceName tells a
    /// name holding a device word.</summary>
    public static RegionPrints Build(IEnumerable<(string Rel, string Text)> sources, IReadOnlyList<RemovedRegion> regions,
                                     IReadOnlyList<KeptRange> kept, Func<string, bool> deviceName, TextWriter? log = null)
    {
        var files = sources.Select(s => (s.Rel, Tokens: new Lexer(s.Text, Lang.C).Run().Tokens)).ToList();
        bool InRegion(string rel, int line) =>
            regions.Any(r => r.File.Equals(rel, StringComparison.OrdinalIgnoreCase) && line >= r.Begin && line <= r.End)
            && !kept.Any(k => k.File.Equals(rel, StringComparison.OrdinalIgnoreCase) && line >= k.Begin && line <= k.End);

        // Declared names, and the names used and never declared.
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var fileScope = new HashSet<string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var allMacros = new Dictionary<string, Macro>(StringComparer.Ordinal);
        var outsideMacros = new Dictionary<string, Macro>(StringComparer.Ordinal);
        foreach (var (rel, toks) in files)
        {
            foreach (var d in new CDecls(toks).Found) { declared.Add(d.Name); fileScope.Add(d.Name); }
            foreach (var d in new Locals(Scan.WithoutDirectives(toks), csharp: false).Found) declared.Add(d.Name);
            CollectMacros(toks, allMacros);
            CollectMacros(toks.Where(x => !InRegion(rel, x.Line)).ToList(), outsideMacros);
            foreach (var x in toks) if (x.Kind == Tk.Ident) used.Add(x.Text);
        }
        foreach (var m in allMacros.Values) if (m.Params != null) foreach (string p in m.Params) declared.Add(p);
        var prints = new RegionPrints();
        foreach (string u in used) if (!declared.Contains(u)) prints.External.Add(u);

        // What code outside the regions holds: windows, names, strings, integers.
        var outKeys = new HashSet<string>(StringComparer.Ordinal);
        var outNames = new HashSet<string>(StringComparer.Ordinal);
        var outStrings = new HashSet<string>(StringComparer.Ordinal);
        var outInts = new HashSet<ulong>();
        foreach (var (rel, toks) in files)
        {
            var outside = toks.Where(x => !InRegion(rel, x.Line)).ToList();
            foreach (var x in outside)
            {
                if (x.Kind == Tk.Ident) outNames.Add(x.Text);
                if (x.Kind == Tk.String) outStrings.Add(x.Content);
                if (x.Kind == Tk.Number && x.IsInt) outInts.Add(x.Value);
            }
            foreach (var stream in new[] { Normalize(outside, prints.External), Normalize(Expand(outside, outsideMacros), prints.External) })
            {
                foreach (var t in stream)
                {
                    if (t.Kind == Tk.String) outStrings.Add(t.Key[1..]);
                    if (t.IsInt) outInts.Add(t.Num);
                }
                foreach (var w in Windows(stream)) outKeys.Add(w.Kind + w.Key);
            }
        }

        foreach (var r in regions)
        {
            var file = files.FirstOrDefault(a => a.Rel.Equals(r.File, StringComparison.OrdinalIgnoreCase));
            if (file.Tokens == null) throw new InvalidDataException($"{r.File} is not among the sources the regions were printed from");
            // The file is normalized whole, then the region's tokens taken,
            // so a region's edge reads as the file reads it.
            var s = Normalize(file.Tokens, prints.External).Where(x => x.Line >= r.Begin && x.Line <= r.End && InRegion(file.Rel, x.Line)).ToList();
            int exact = 0, shape = 0;
            foreach (var w in Windows(s))
            {
                if (outKeys.Contains(w.Kind + w.Key)) continue;
                int n = w.Kind == 'x' ? ExactWindow : ShapeWindow;
                if (!OwnContent(s, w.At, n, names: w.Kind == 'x', outNames, outStrings, outInts, fileScope, deviceName)) continue;
                prints.Add(r.Name, w.Kind + " " + Hash(w.Key));
                if (w.Kind == 'x') exact++; else shape++;
            }
            log?.WriteLine($"  {r.Name}: {s.Count} tokens, {prints.Count(r.Name, 'x')} exact and {prints.Count(r.Name, 's')} shape prints");
        }
        return prints;
    }

    // The window holds content of the region's own (see the header).
    static bool OwnContent(List<NTok> s, int at, int n, bool names, HashSet<string> outNames, HashSet<string> outStrings,
                           HashSet<ulong> outInts, HashSet<string> fileScope, Func<string, bool> deviceName)
    {
        int run = 0, need = names ? 3 : 4;
        var values = new HashSet<ulong>();
        for (int k = at; k < at + n; k++)
        {
            var x = s[k];
            if (names && x.Kind == Tk.Ident)
            {
                string name = x.Key[1..];
                if ((!outNames.Contains(name) && fileScope.Contains(name)) || deviceName(name)) return true;
            }
            if (x.Kind == Tk.String && !outStrings.Contains(x.Key[1..])) return true;
            if (x.IsInt && x.Num >= 0x100 && !outInts.Contains(x.Num)) return true;
            // A row of data: int , int , int ...
            if (x.IsInt)
            {
                if (run > 0 && s[k - 1].Key == "p,") run++;
                else
                {
                    run = 1;
                    values.Clear();
                }
                values.Add(x.Num);
                if (run >= need && values.Count >= 3) return true;
            }
            else if (!(x.Key == "p," && run > 0))
            {
                run = 0;
                values.Clear();
            }
        }
        return false;
    }

    public string Write(string path, IReadOnlyList<RemovedRegion> regions, string from)
    {
        var sb = new StringBuilder();
        sb.Append("# The prints of the code v2.0.0 deletes from the drivers (RegionPrints.cs),\n");
        sb.Append("# written by device_shortcut_check --write-regions from ").Append(from).Append(".\n");
        sb.Append("# Pinned by its SHA-256 in Rules.RegionPrintsSha256.\n");
        sb.Append("external ").Append(External.Count).Append('\n');
        foreach (string e in External.OrderBy(e => e, StringComparer.Ordinal)) sb.Append(e).Append('\n');
        foreach (var r in regions)
        {
            var set = ByRegion.TryGetValue(r.Name, out var x) ? x : new HashSet<string>();
            sb.Append("region ").Append(r.Name).Append(' ').Append(set.Count).Append('\n');
            foreach (string p in set.OrderBy(p => p, StringComparer.Ordinal)) sb.Append(p).Append('\n');
        }
        byte[] bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
        File.WriteAllBytes(path, bytes);
        return Rules.TextSha256(bytes);
    }

    /// <summary>Reads the prints, and fails unless the file's SHA-256 is the
    /// pinned one and it names every region of the table, each with at
    /// least one print, and nothing else.</summary>
    public static RegionPrints Load(string path, IReadOnlyList<RemovedRegion> regions, string sha256)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string actual = Rules.TextSha256(bytes);
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{path} has SHA-256 {actual}, and Rules.RegionPrintsSha256 pins {sha256}");
        var prints = new RegionPrints();
        string? region = null;
        int externals = -1, expected = 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string raw in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("external ", StringComparison.Ordinal))
            {
                if (externals >= 0 || region != null || !int.TryParse(line[9..], out externals))
                    throw new InvalidDataException($"{path}: a malformed external line: {line}");
                continue;
            }
            if (line.StartsWith("region ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || !int.TryParse(parts[2], out expected))
                    throw new InvalidDataException($"{path}: a malformed region line: {line}");
                region = parts[1];
                if (regions.All(r => r.Name != region)) throw new InvalidDataException($"{path}: region {region} is not in the table");
                if (!counts.TryAdd(region, expected)) throw new InvalidDataException($"{path}: region {region} twice");
                continue;
            }
            if (region == null)
            {
                if (externals < 0) throw new InvalidDataException($"{path}: a name before the external line: {line}");
                prints.External.Add(line);
                continue;
            }
            if (line.Length != 18 || line[0] is not ('x' or 's') || line[1] != ' ' || !line[2..].All(Uri.IsHexDigit))
                throw new InvalidDataException($"{path}: a malformed print: {line}");
            prints.Add(region, line);
        }
        if (externals < 0 || prints.External.Count != externals)
            throw new InvalidDataException($"{path}: the external line lists {externals} names and the file holds {prints.External.Count}");
        foreach (var r in regions)
        {
            if (!counts.TryGetValue(r.Name, out int n)) throw new InvalidDataException($"{path} has no prints for region {r.Name}");
            if (n == 0 || prints.Count(r.Name) != n)
                throw new InvalidDataException($"{path}: region {r.Name} lists {n} prints and holds {prints.Count(r.Name)}, and needs at least one");
        }
        return prints;
    }

    // ---- search ----

    /// <summary>The prints a source holds, as written and expanded: for
    /// each region, the prints found and the lines they start on. A print
    /// several regions share counts for those of them with a print of their
    /// own in the source, or for all of them when none has one.</summary>
    public Dictionary<string, (HashSet<string> Prints, SortedSet<int> Lines)> Find(IReadOnlyList<Token> tokens, IReadOnlyDictionary<string, Macro> macros)
    {
        var hits = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        foreach (var stream in new[] { Normalize(tokens, External), Normalize(Expand(tokens, macros), External) })
            foreach (var w in Windows(stream))
            {
                string print = w.Kind + " " + Hash(w.Key);
                if (Owners(print).Count == 0) continue;
                if (!hits.TryGetValue(print, out var lines)) hits[print] = lines = new SortedSet<int>();
                lines.Add(w.Line);
            }
        var own = new HashSet<string>(hits.Keys.Where(p => Owners(p).Count == 1).Select(p => Owners(p)[0]), StringComparer.Ordinal);
        var found = new Dictionary<string, (HashSet<string>, SortedSet<int>)>(StringComparer.Ordinal);
        foreach (var (print, lines) in hits)
        {
            var owners = Owners(print);
            var to = owners.Count == 1 || !owners.Any(own.Contains) ? owners : owners.Where(own.Contains).ToList();
            foreach (string name in to)
            {
                if (!found.TryGetValue(name, out var f)) found[name] = f = (new HashSet<string>(StringComparer.Ordinal), new SortedSet<int>());
                f.Item1.Add(print);
                f.Item2.UnionWith(lines);
            }
        }
        return found;
    }
}
