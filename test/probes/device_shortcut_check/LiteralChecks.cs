// Checks 2 to 4 over one file's tokens: vendor and product IDs as numbers,
// constant expressions, named constants and locals, strings and split bytes,
// device names in strings and in string constants, and catalog profile ids
// as strings.
//
// Strings are folded before they are matched: literals and string
// constants joined by + (C#), adjacent literals and #define'd strings (C),
// an interpolated string whose holes are literals or constants, and
// string.Concat of literals. A regular expression's pattern (Regex.IsMatch,
// Regex.Match, Regex.Matches, new Regex, [GeneratedRegex]) hits when its
// letters and digits, with escapes such as \s and the inline options
// dropped, spell a device word. A local or field assigned from an
// expression named for an ID (ushort maker = profile.VendorId) is an ID
// operand whatever its name, and one assigned from an expression that
// reads an ID (byte hi = (byte)(pid >> 8)) is an ID's byte for the split
// comparisons. A literal passed where the called function's parameter is
// named for an ID is bound to that name. A cast to a 16-bit or 8-bit type
// truncates, a hex string parsed as a number (int.Parse with
// NumberStyles.HexNumber, Convert.ToUInt16(s, 16), strtoul(s, .., 16)) is
// read as its value.
//
// What these miss: a name assembled at run time from values that are not
// constants (a StringBuilder, a char array, a string read from a resource),
// a local whose ID is assigned through a ref, an out argument or a
// function the check cannot see, and a byte comparison whose other side is
// read from a buffer at run time rather than from an expression named for
// an ID.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

// A name declared with one constant value: a const, a static readonly, a
// #define, or a local or field initialized from one integer expression or
// one string. Num and Content hold the value. Constant marks const, static
// readonly and #define, which count from any file. Other names count in
// their own file only.
internal sealed record ConstantDef(string File, int Line, string Name, string Value, ulong Num, bool IdForm, bool Composite,
                                   bool Constant, string? Content);

// What one file's checks report: hits, device-word strings that are not
// hits, literals equal to a product ID that do not count, and constants
// that a comparison made into IDs.
internal interface ILiteralSink
{
    void Hit(int line, string group, string kind, string name);
    void Info(int line, string kind, string text);
    void Coincidence(int line, string why);
    void ConstantUsed(ConstantDef def, string group, string where);
}

internal sealed class LiteralChecks
{
    // VID_xxxx, PID_xxxx, &PID_xxxx, VID&02xxxx and the like, any value.
    static readonly Regex s_idForm = new(@"(?<![A-Za-z])(vid|pid)(?:&0002|&02|[_&=: ])?([0-9A-Fa-f]{4})(?![0-9A-Fa-f])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex s_wholeId = new(@"^(?:0[xX])?([0-9A-Fa-f]{4})$", RegexOptions.CultureInvariant);
    static readonly Regex s_chunk = new(@"[A-Za-z0-9]+", RegexOptions.CultureInvariant);

    // Calls that compare or search a string.
    static readonly HashSet<string> s_search = new(StringComparer.Ordinal)
    {
        "Contains", "IndexOf", "LastIndexOf", "StartsWith", "EndsWith", "Equals", "Compare", "CompareOrdinal",
        "strstr", "wcsstr", "StrStrI", "StrStrIW", "strcmp", "wcscmp", "_stricmp", "_wcsicmp", "stricmp", "wcsicmp",
        "strncmp", "wcsncmp", "_strnicmp", "_wcsnicmp", "lstrcmpA", "lstrcmpW", "lstrcmpiA", "lstrcmpiW",
        "CompareStringW", "RtlEqualUnicodeString",
    };
    // Regex's static calls whose second argument is the pattern.
    static readonly HashSet<string> s_regexStatic = new(StringComparer.Ordinal) { "IsMatch", "Match", "Matches", "Replace", "Split" };
    // Integer types a cast truncates to, by width.
    static readonly Dictionary<string, ulong> s_narrow = new(StringComparer.Ordinal)
    {
        ["ushort"] = 0xFFFF, ["short"] = 0xFFFF, ["UInt16"] = 0xFFFF, ["Int16"] = 0xFFFF, ["USHORT"] = 0xFFFF, ["SHORT"] = 0xFFFF,
        ["WORD"] = 0xFFFF, ["UINT16"] = 0xFFFF, ["uint16_t"] = 0xFFFF, ["int16_t"] = 0xFFFF, ["char"] = 0xFFFF,
        ["byte"] = 0xFF, ["sbyte"] = 0xFF, ["Byte"] = 0xFF, ["SByte"] = 0xFF, ["UCHAR"] = 0xFF, ["BYTE"] = 0xFF, ["CHAR"] = 0xFF,
        ["uint8_t"] = 0xFF, ["int8_t"] = 0xFF, ["UINT8"] = 0xFF,
    };
    // Number types whose Parse and TryParse read a hex string with NumberStyles.
    static readonly HashSet<string> s_parseTypes = new(StringComparer.Ordinal)
    {
        "int", "uint", "short", "ushort", "long", "ulong", "byte", "Int32", "UInt32", "Int16", "UInt16", "Int64", "UInt64", "Byte",
    };
    // Words of an identifier that holds a product string or a name.
    static readonly HashSet<string> s_nameWords = new(StringComparer.Ordinal)
        { "name", "string", "str", "product", "description", "desc", "manufacturer", "title", "label" };
    // Operators a constant expression may use. Right shift is two '>'.
    static readonly HashSet<string> s_ops = new(StringComparer.Ordinal) { "<<", "|", "+", "&", "^", "-", "*" };

    readonly List<Token> t;
    readonly int[] m;
    readonly Catalog cat;
    readonly ILiteralSink sink;
    readonly Dictionary<string, List<ConstantDef>> constants;
    readonly Dictionary<string, List<string[]>> signatures;
    readonly string file;
    // Names assigned from an ID: Whole when the value is the ID itself.
    readonly Dictionary<string, (bool Vendor, bool Product, bool Strong, bool Whole)> aliases = new(StringComparer.Ordinal);

    public LiteralChecks(List<Token> tokens, Catalog catalog, Dictionary<string, List<ConstantDef>> constants,
                         Dictionary<string, List<string[]>> signatures, string file, ILiteralSink sink)
    {
        t = tokens;
        m = Brackets.Match(t);
        cat = catalog;
        this.constants = constants;
        this.signatures = signatures;
        this.file = file;
        this.sink = sink;
    }

    bool Is(int i, string text) => i >= 0 && i < t.Count && t[i].Text == text && t[i].Kind is Tk.Punct or Tk.Keyword or Tk.Ident;
    bool IsIdent(int i) => i >= 0 && i < t.Count && t[i].Kind == Tk.Ident;
    bool IsNumber(int i) => i >= 0 && i < t.Count && t[i].Kind == Tk.Number;

    // The definitions a name at i can mean: constants from any file, other
    // names from this file.
    List<ConstantDef> Defs(int i, bool strings)
    {
        if (!IsIdent(i) || !constants.TryGetValue(t[i].Text, out var all)) return new List<ConstantDef>();
        return all.Where(d => (d.Content != null) == strings && (d.Constant || d.File == file)).ToList();
    }
    bool IsConstant(int i) => Defs(i, strings: false).Count > 0;

    public void Run()
    {
        Aliases();
        Folded();
        var done = new HashSet<int>();
        for (int i = 0; i < t.Count; i++)
        {
            var x = t[i];
            if (x.Kind == Tk.Number && x.IsInt && !done.Contains(i)) Number(i, done);
            else if (x.Kind == Tk.String) Str(i);
            else if (IsIdent(i))
            {
                if (IsConstant(i)) Constant(i);
                if (Defs(i, strings: true).Count > 0) StringConstant(i);
            }
        }
        SplitBytes();
        SplitCompares();
        TwoByteArrays();
    }

    // ---- values ----

    // The arithmetic expression around the literal at i: literals, casts,
    // parentheses and operators, grown while the next piece is one too.
    (int Start, int End) Span(int i)
    {
        int s = i, e = i;
        bool grew = true;
        while (grew)
        {
            grew = false;
            int r = OpAt(e + 1);
            if (r > 0 && OperandEnd(e + 1 + r) is int oe && oe > 0) { e = oe; grew = true; continue; }
            int l = OpBefore(s - 1);
            if (l > 0 && OperandStart(s - 1 - l) is int os && os >= 0) { s = os; grew = true; continue; }
            if (Is(s - 1, "(") && Is(e + 1, ")") && m[s - 1] == e + 1) { s--; e++; grew = true; continue; }
            if (Is(s - 1, ")") && Is(s - 3, "(") && t[s - 2].Kind is Tk.Ident or Tk.Keyword && !Is(s - 4, ")")
                && (s - 4 < 0 || !IsIdent(s - 4))) { s -= 3; grew = true; continue; }
            // unchecked(...) and checked(...) around the group.
            if ((Is(s - 1, "unchecked") || Is(s - 1, "checked")) && Is(s, "(") && m[s] == e) { s--; grew = true; }
        }
        return (s, e);
    }

    // Length of the operator starting at k (2 for '>' '>'), or 0.
    int OpAt(int k)
    {
        if (k >= t.Count || t[k].Kind != Tk.Punct) return 0;
        if (s_ops.Contains(t[k].Text)) return 1;
        return Is(k, ">") && Is(k + 1, ">") ? 2 : 0;
    }

    int OpBefore(int k)
    {
        if (k < 0 || t[k].Kind != Tk.Punct) return 0;
        if (s_ops.Contains(t[k].Text)) return 1;
        return Is(k, ">") && Is(k - 1, ">") ? 2 : 0;
    }

    // The end of an operand that starts at k: a literal or an arithmetic
    // group.
    int? OperandEnd(int k)
    {
        if (IsNumber(k) && t[k].IsInt) return k;
        if (Is(k, "(") && m[k] > k && Arithmetic(k + 1, m[k] - 1)) return m[k];
        return null;
    }

    int? OperandStart(int k)
    {
        if (IsNumber(k) && t[k].IsInt) return k;
        if (Is(k, ")") && m[k] >= 0 && m[k] < k && Arithmetic(m[k] + 1, k - 1)) return m[k];
        return null;
    }

    bool Arithmetic(int s, int e)
    {
        if (s > e) return false;
        for (int k = s; k <= e; k++)
        {
            var x = t[k];
            if (x.Kind == Tk.Number && x.IsInt) continue;
            if (x.Kind == Tk.Punct && (x.Text is "(" or ")" or ">" or "~" || s_ops.Contains(x.Text))) continue;
            if (x.Kind is Tk.Ident or Tk.Keyword && Is(k - 1, "(") && Is(k + 1, ")")) continue;
            if (x.Text is "unchecked" or "checked" && Is(k + 1, "(")) continue;
            return false;
        }
        return true;
    }

    // The value of an arithmetic span, or null.
    ulong? Eval(int s, int e)
    {
        int k = s;
        ulong? v = Or(ref k, e);
        return k == e + 1 ? v : null;
    }

    ulong? Or(ref int k, int e)
    {
        ulong? a = Shift(ref k, e);
        while (a != null && k <= e && t[k].Kind == Tk.Punct && t[k].Text is "|" or "^" or "&")
        {
            string op = t[k++].Text;
            ulong? b = Shift(ref k, e);
            if (b == null) return null;
            a = op == "|" ? a | b : op == "^" ? a ^ b : a & b;
        }
        return a;
    }

    ulong? Shift(ref int k, int e)
    {
        ulong? a = Sum(ref k, e);
        while (a != null && k <= e && (Is(k, "<<") || (Is(k, ">") && Is(k + 1, ">"))))
        {
            bool left = Is(k, "<<");
            k += left ? 1 : 2;
            ulong? b = Sum(ref k, e);
            if (b == null || b > 63) return null;
            a = left ? a << (int)b : a >> (int)b;
        }
        return a;
    }

    ulong? Sum(ref int k, int e)
    {
        ulong? a = Unit(ref k, e);
        while (a != null && k <= e && t[k].Kind == Tk.Punct && t[k].Text is "+" or "-" or "*")
        {
            string op = t[k++].Text;
            ulong? b = Unit(ref k, e);
            if (b == null) return null;
            a = op == "+" ? a + b : op == "-" ? a - b : a * b;
        }
        return a;
    }

    ulong? Unit(ref int k, int e)
    {
        if (k > e) return null;
        if (IsNumber(k) && t[k].IsInt) return t[k++].Value;
        if ((Is(k, "unchecked") || Is(k, "checked")) && Is(k + 1, "(")) k++;
        if (Is(k, "(") && m[k] > k && m[k] <= e)
        {
            int close = m[k];
            // A cast: (type) operand, truncated to a narrow type's width.
            if (close == k + 2 && t[k + 1].Kind is Tk.Ident or Tk.Keyword)
            {
                string type = t[k + 1].Text;
                k = close + 1;
                ulong? cast = Unit(ref k, e);
                return cast != null && s_narrow.TryGetValue(type, out ulong mask) ? cast & mask : cast;
            }
            int inner = k + 1;
            ulong? v = Or(ref inner, close - 1);
            if (inner != close) return null;
            k = close + 1;
            return v;
        }
        return null;
    }

    // Constants: const and static readonly declarations, #define, and
    // locals or fields declared with one integer expression or one string.
    public static void CollectConstants(List<Token> t, string file, Dictionary<string, List<ConstantDef>> into)
    {
        var probe = new LiteralChecks(t, new Catalog(), into, new Dictionary<string, List<string[]>>(), file, NullSink.Instance);
        for (int i = 1; i + 1 < t.Count; i++)
        {
            if (t[i].Kind != Tk.Ident) continue;
            int v = -1;
            bool constant = false;
            if (t[i - 1].Kind == Tk.DirStart && t[i - 1].Text == "define")
            {
                v = i + 1;
                constant = true;
            }
            else if (t[i + 1].Kind == Tk.Punct && t[i + 1].Text == "=")
            {
                // const, or static and readonly, in the same declaration, or a
                // type right before the name.
                bool isConst = false, isStatic = false, isReadonly = false;
                for (int k = i - 1; k >= 0 && k > i - 12; k--)
                {
                    var x = t[k];
                    if (x.Kind == Tk.Punct && x.Text is ";" or "{" or "}" or "(" or ")" or "=" or ",") break;
                    if (x.Text == "const") isConst = true;
                    if (x.Text == "static") isStatic = true;
                    if (x.Text == "readonly") isReadonly = true;
                }
                var p = t[i - 1];
                bool typed = (p.Kind == Tk.Ident && p.Text is not ("return" or "await" or "yield"))
                             || (p.Kind == Tk.Keyword && p.Text is "int" or "uint" or "short" or "ushort" or "long" or "ulong" or "byte"
                                 or "char" or "string" or "unsigned" or "signed" or "bool")
                             || (p.Kind == Tk.Punct && p.Text is ">" or "?" or "]" or "*");
                constant = isConst || (isStatic && isReadonly);
                if (constant || typed) v = i + 2;
            }
            if (v < 0 || v >= t.Count) continue;
            // One string.
            if (t[v].Kind == Tk.String && (v + 1 >= t.Count || t[v + 1].Kind == Tk.DirEnd || t[v + 1].Text is ";" or ","))
            {
                Add(into, new ConstantDef(file, t[i].Line, t[i].Text, t[v].Text, 0, false, false, constant, t[v].Content));
                continue;
            }
            // One string folded from parts ("Nin" + "tendo", A + B).
            if (probe.Expr(v, out int fe, out var fp) && fp.Count >= 2
                && (fe + 1 >= t.Count || t[fe + 1].Kind == Tk.DirEnd || t[fe + 1].Text is ";" or ","))
            {
                string content = string.Concat(fp);
                Add(into, new ConstantDef(file, t[i].Line, t[i].Text, "\"" + content + "\"", 0, false, false, constant, content));
                continue;
            }
            // One integer expression up to the end of the declarator.
            int e = v;
            while (e < t.Count && t[e].Kind != Tk.DirEnd && !(t[e].Kind == Tk.Punct && t[e].Text is ";" or ",")) e = probe.Skip(e) + 1;
            int end = e - 1;
            if (end < v || !probe.Arithmetic(v, end) || probe.Eval(v, end) is not ulong num) continue;
            int literals = 0;
            for (int k = v; k <= end; k++) if (t[k].Kind == Tk.Number) literals++;
            var lit = t.Skip(v).Take(end - v + 1).First(x => x.Kind == Tk.Number);
            Add(into, new ConstantDef(file, t[i].Line, t[i].Text, string.Join("", t.Skip(v).Take(end - v + 1).Select(x => x.Text)),
                num, literals == 1 && lit.IsHex && lit.HexDigits >= 4, literals > 1, constant, null));
        }
    }

    static void Add(Dictionary<string, List<ConstantDef>> into, ConstantDef d)
    {
        if (!into.TryGetValue(d.Name, out var list)) into[d.Name] = list = new List<ConstantDef>();
        list.Add(d);
    }

    int Skip(int i) => i < t.Count && t[i].Kind == Tk.Punct && t[i].Text is "(" or "[" or "{" && m[i] > i ? Math.Min(m[i], t.Count - 1) : i;

    sealed class NullSink : ILiteralSink
    {
        public static readonly NullSink Instance = new();
        public void Hit(int line, string group, string kind, string name) { }
        public void Info(int line, string kind, string text) { }
        public void Coincidence(int line, string why) { }
        public void ConstantUsed(ConstantDef def, string group, string where) { }
    }

    // The words of an identifier: named for a vendor or product ID, and
    // whether that is unmistakable (the word product or vendor beside id)
    // or a bare pid or vid, which also names process IDs and property keys.
    public static (bool Vendor, bool Product, bool Strong) IdNaming(string name)
    {
        var w = Words.Split(name).Select(x => x.ToLowerInvariant()).ToList();
        if (w.Count == 0) return (false, false, false);
        string last = w[^1];
        bool Pair(string what) => last == what + "id"
            || (w.Count >= 2 && ((w[^2] == what && last == "id") || (w[^2] == "id" && last == what)));
        bool pStrong = Pair("product"), vStrong = Pair("vendor");
        bool p = pStrong || last == "pid", v = vStrong || last == "vid";
        return (v, p, pStrong || vStrong);
    }

    // Any word of the name says vendor or product ID: pidHi, vendorBytes.
    static bool MentionsId(string name) =>
        Words.Split(name).Any(w => w.ToLowerInvariant() is "pid" or "vid" or "product" or "vendor" or "productid" or "vendorid");

    // What an operand names: its own words, or the ID it was assigned from.
    (bool Vendor, bool Product, bool Strong) NamingOf(string? operand)
    {
        if (operand == null) return default;
        var n = IdNaming(operand);
        if ((n.Vendor || n.Product) || !aliases.TryGetValue(operand, out var a) || !a.Whole) return n;
        return (a.Vendor, a.Product, a.Strong);
    }

    // The operand mentions an ID or was assigned from an expression that reads one.
    bool FromId(string? operand) => operand != null && (MentionsId(operand) || aliases.ContainsKey(operand));

    // Names assigned from an ID: name = expression, where the expression is
    // a member, a call or a cast of one named for an ID (Whole), or reads
    // such a name or another alias anywhere (pidHi = (byte)(pid >> 8)).
    void Aliases()
    {
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i + 2 < t.Count; i++)
            {
                if (!IsIdent(i) || !Is(i + 1, "=") || Is(i - 1, ".") || Is(i - 1, "->") || Is(i - 1, "::") || Is(i - 1, "?.")) continue;
                int e = i + 2;
                while (e < t.Count && !(t[e].Kind == Tk.Punct && t[e].Text is ";" or "," or ")" or "}") && t[e].Kind != Tk.DirEnd) e = Skip(e) + 1;
                if (e <= i + 2) continue;
                var whole = NamingOfInitializer(i + 2, e - 1);
                if (whole.Vendor || whole.Product) { aliases[t[i].Text] = (whole.Vendor, whole.Product, whole.Strong, true); continue; }
                bool vendor = false, product = false;
                for (int k = i + 2; k < e; k++)
                {
                    if (!IsIdent(k)) continue;
                    var n = IdNaming(t[k].Text);
                    if (aliases.TryGetValue(t[k].Text, out var a)) { vendor |= a.Vendor; product |= a.Product; }
                    else if (n.Vendor || n.Product) { vendor |= n.Vendor; product |= n.Product; }
                }
                if ((vendor || product) && !aliases.ContainsKey(t[i].Text)) aliases[t[i].Text] = (vendor, product, false, false);
            }
    }

    // An initializer that is one expression named for an ID, after casts
    // and parentheses: profile.VendorId, ctx->VendorID, (USHORT)val's
    // source, GetProductId(p).
    (bool Vendor, bool Product, bool Strong) NamingOfInitializer(int s, int e)
    {
        while (s <= e)
        {
            // A cast to a byte type takes one byte of the ID, not the ID.
            if (Is(s, "(") && m[s] == s + 2 && t[s + 1].Kind is Tk.Ident or Tk.Keyword && s + 3 <= e)
            {
                if (s_narrow.TryGetValue(t[s + 1].Text, out ulong mask) && mask == 0xFF) return default;
                s += 3;
                continue;
            }
            if (Is(s, "(") && m[s] == e) { s++; e--; continue; }
            break;
        }
        if (s > e) return default;
        int j = s;
        string? name = null;
        while (j <= e && IsIdent(j))
        {
            name = t[j].Text;
            if (j + 1 <= e && (Is(j + 1, ".") || Is(j + 1, "->") || Is(j + 1, "?.") || Is(j + 1, "::")) && IsIdent(j + 2)) { j += 2; continue; }
            j++;
            break;
        }
        if (name == null) return default;
        // A call: name ( ... ) to the end.
        if (j <= e && Is(j, "(") && m[j] == e) j = e + 1;
        if (j != e + 1) return default;
        if (aliases.TryGetValue(name, out var a) && a.Whole) return (a.Vendor, a.Product, a.Strong);
        return IdNaming(name);
    }

    bool CatalogId(ulong v) => v >= 0x100 && (cat.Vids.Contains(v) || cat.Pids.Contains(v));

    // Whether a value bound to an expression named for an ID hits: a hex
    // literal of four or more digits, an expression built from literals, a
    // const, static readonly or #define, any nonzero value when the name is
    // unmistakable, and a catalog ID of three or more hex digits whatever
    // it is written as. A bare pid or vid with zero or a small value is a
    // process ID, a property key or a byte.
    bool Binds(ulong v, bool idForm, bool composite, bool constant, (bool Vendor, bool Product, bool Strong) n) =>
        (n.Vendor || n.Product) && (idForm || ((composite || constant || n.Strong) && v != 0) || CatalogId(v));

    // A literal or a constant expression compared with, switched on or
    // assigned to an expression named for an ID (Binds says when). By value
    // alone, a catalog vendor ID as a four-digit hex literal or a constant
    // expression hits anywhere, and a catalog product ID beside the hex
    // literal of a vendor ID the catalog pairs with it.
    void Number(int i, HashSet<int> done)
    {
        var (s, e) = Span(i);
        bool composite = false;
        for (int k = s; k <= e; k++) if (k != i && IsNumber(k)) composite = true;
        if (Eval(s, e) is not ulong v) { s = i; e = i; v = t[i].Value; composite = false; }
        // The span's other literals are this expression, not values of their own.
        if (composite) for (int k = s; k <= e; k++) if (IsNumber(k)) done.Add(k);
        var x = t[i];
        string text = composite ? string.Join("", t.Skip(s).Take(e - s + 1).Select(q => q.Text)) : x.Text;
        var (ctx, operand) = Context(s, e);
        bool idForm = !composite && x.IsHex && x.HexDigits >= 4;
        if (ctx != null && operand != null)
        {
            var n = NamingOf(operand);
            if (Binds(v, idForm, composite, false, n))
            {
                string what = n.Product ? "product" : "vendor";
                sink.Hit(x.Line, n.Product ? HitGroups.Product : HitGroups.Vendor, $"{what} ID {ctx}", operand + " " + text);
                return;
            }
        }
        bool isPid = cat.Pids.Contains(v), isVid = cat.Vids.Contains(v);
        if (isVid && (idForm || (composite && v >= 0x100)))
        {
            sink.Hit(x.Line, HitGroups.Vendor, ctx == null ? "vendor ID literal" : "vendor ID " + ctx, (operand != null ? operand + " " : "") + text);
            return;
        }
        if ((idForm || composite) && isPid && ctx is "comparison" or "case label" && VendorInStatement(i, v, out string vid))
        {
            sink.Hit(x.Line, HitGroups.Product, "product ID in a vendor ID condition", vid + " " + text);
            return;
        }
        if (isPid && ctx != null)
        {
            var n = NamingOf(operand);
            sink.Coincidence(x.Line, n.Product || n.Vendor
                ? $"{ctx} of {operand} with {text}, a bare pid or vid with zero or a small value"
                : $"{ctx} of {operand ?? "an expression"} with {text}, not named for an ID");
        }
    }

    // A named constant, or a local or field with one value, compared with,
    // switched on or assigned to an expression named for an ID: a hit here
    // and at its definition.
    void Constant(int i)
    {
        var defs = Defs(i, strings: false);
        if (defs.Any(d => d.File == file && d.Line == t[i].Line && Is(i + 1, "="))) return;
        if (Is(i - 1, ".") && !defs.Any(d => d.Constant)) return;
        var (ctx, operand) = Context(i, i);
        if (ctx == null || operand == null || operand == t[i].Text) return;
        var n = NamingOf(operand);
        var bound = defs.Where(d => Binds(d.Num, d.IdForm, d.Composite, d.Constant, n)).ToList();
        if (bound.Count == 0) return;
        string group = n.Product ? HitGroups.Product : HitGroups.Vendor;
        string what = n.Product ? "product" : "vendor";
        sink.Hit(t[i].Line, group, $"{what} ID {(bound[0].Constant ? "constant" : "value")} in a {ctx}", $"{operand} {t[i].Text} = {bound[0].Value}");
        foreach (var d in bound) sink.ConstantUsed(d, group, $"{file}:{t[i].Line}");
    }

    // A string constant or local with a device name, compared, searched or
    // given to a name: a hit here and at its definition.
    void StringConstant(int i)
    {
        var defs = Defs(i, strings: true).Where(d => DeviceWord(d.Content!)).ToList();
        if (defs.Count == 0) return;
        if (defs.Any(d => d.File == file && d.Line == t[i].Line && Is(i + 1, "="))) return;
        if (Is(i - 1, ".") && !defs.Any(d => d.Constant)) return;
        var (used, how) = StringUse(i);
        if (!used) return;
        sink.Hit(t[i].Line, HitGroups.DeviceString, $"device name constant {how}", $"{t[i].Text} = {Shorten(defs[0].Content!)}");
        foreach (var d in defs) sink.ConstantUsed(d, HitGroups.DeviceString, $"{file}:{t[i].Line}");
    }

    bool DeviceWord(string content) => DeviceWords(content).Count > 0;

    List<string> DeviceWords(string content)
    {
        var words = new List<string>();
        foreach (Match c in s_chunk.Matches(content))
            foreach (string w in cat.Words.Match(c.Value).Tokens)
                if (!words.Contains(w)) words.Add(w);
        return words;
    }

    // The device words a regular expression's pattern spells: its letters
    // and digits, lowercase, with escapes (\s, \d, \p{L}) and inline
    // options ((?i)) removed first.
    List<string> PatternWords(string pattern)
    {
        string p = Regex.Replace(pattern, @"\(\?[a-zA-Z-]*[:)]|\\[pP]\{[^}]*\}|\\[a-zA-Z]", "");
        string letters = new string(p.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return cat.Words.Compound.Where(w => letters.Contains(w, StringComparison.Ordinal))
            .Concat(cat.Words.Single.Concat(cat.Words.Spanning).Where(w => w.Length >= 4 && letters.Contains(w, StringComparison.Ordinal)))
            .Distinct().ToList();
    }

    // Four byte literals in a row: a catalog vendor ID little-endian, then a
    // product ID the catalog pairs with it.
    void SplitBytes()
    {
        for (int i = 0; i + 6 < t.Count; i++)
        {
            if (!Byte(i) || !Is(i + 1, ",") || !Byte(i + 2) || !Is(i + 3, ",") || !Byte(i + 4) || !Is(i + 5, ",") || !Byte(i + 6)) continue;
            ulong v = t[i].Value | (t[i + 2].Value << 8), p = t[i + 4].Value | (t[i + 6].Value << 8);
            if (cat.Pairs.Contains((v, p)))
                sink.Hit(t[i].Line, HitGroups.Vendor, "vendor and product ID as bytes",
                    $"{t[i].Text}, {t[i + 2].Text}, {t[i + 4].Text}, {t[i + 6].Text} ({v:X4}:{p:X4})");
        }
    }

    bool Byte(int i) => IsNumber(i) && t[i].IsInt && t[i].Value <= 0xFF;

    // Two byte comparisons joined by && (hi == 0x20 && lo == 0x09): an ID
    // split into bytes, either order, when an operand names an ID, or when
    // the bytes make a catalog ID and the statement compares an expression
    // named for an ID or holds a catalog vendor ID.
    void SplitCompares()
    {
        for (int k = 0; k + 6 < t.Count; k++)
        {
            if (!Is(k, "==") || !Byte(k + 1) || !Is(k + 2, "&&")) continue;
            int k2 = -1;
            for (int q = k + 3; q < t.Count && q < k + 12; q++)
            {
                if (Is(q, "==")) { k2 = q; break; }
                if (!(IsIdent(q) || Is(q, ".") || Is(q, "->") || Is(q, "[") || Is(q, "]") || IsNumber(q))) break;
            }
            if (k2 < 0 || !Byte(k2 + 1)) continue;
            string a = OperandBefore(k) ?? "", b = OperandBefore(k2) ?? "";
            ulong hi = t[k + 1].Value, lo = t[k2 + 1].Value;
            ulong v1 = (hi << 8) | lo, v2 = (lo << 8) | hi;
            bool named = FromId(a) || FromId(b);
            bool beside = (CatalogId(v1) || CatalogId(v2)) && StatementNamesId(k);
            if (!named && !beside) continue;
            ulong v = CatalogId(v1) || !CatalogId(v2) ? v1 : v2;
            sink.Hit(t[k + 1].Line, cat.Vids.Contains(v) ? HitGroups.Vendor : HitGroups.Product, "ID split into two compared bytes",
                $"{a} {t[k + 1].Text}, {b} {t[k2 + 1].Text} ({v:X4})");
        }
    }

    // Two bytes in an initializer list ({ 0x09, 0x20 }) given to a name
    // for an ID, or beside one, that make a catalog ID either way round.
    void TwoByteArrays()
    {
        for (int k = 0; k + 4 < t.Count; k++)
        {
            if (!(Is(k, "{") || Is(k, "[")) || !Byte(k + 1) || !Is(k + 2, ",") || !Byte(k + 3) || !(Is(k + 4, "}") || Is(k + 4, "]"))) continue;
            if (m[k] != k + 4) continue;
            ulong le = t[k + 1].Value | (t[k + 3].Value << 8), be = (t[k + 1].Value << 8) | t[k + 3].Value;
            if (!CatalogId(le) && !CatalogId(be)) continue;
            int q = k - 1;
            while (q >= 0 && (Is(q, "]") || Is(q, "[") || IsNumber(q) || Is(q, "new") || t[q].Kind == Tk.Keyword && t[q].Text is "byte" or "char" or "unsigned")) q--;
            string? target = Is(q, "=") ? AssignTarget(q) : null;
            bool named = target != null && (MentionsId(target) || target.Equals("id", StringComparison.OrdinalIgnoreCase)
                                            || IdNaming(target).Vendor || IdNaming(target).Product);
            // Or both its bytes compared one by one with anything, when
            // neither byte is zero (Sig = { 0x09, 0x20 }, report[1] == Sig[0]
            // && report[2] == Sig[1]).
            bool byBytes = target != null && t[k + 1].Value != 0 && t[k + 3].Value != 0 && ComparedByteByByte(target);
            if (!named && !StatementNamesId(k) && !(target != null && ComparedWithId(target)) && !byBytes) continue;
            ulong v = CatalogId(le) ? le : be;
            sink.Hit(t[k + 1].Line, cat.Vids.Contains(v) ? HitGroups.Vendor : HitGroups.Product, "ID as two bytes",
                $"{target ?? "an initializer"} {t[k + 1].Text}, {t[k + 3].Text} ({v:X4})");
        }
    }

    // The parameters a function's body compares with an expression named
    // for an ID: each entry is the ID's name for such a parameter, the
    // parameter's own name otherwise.
    public static string[] ParamsComparedWithIds(List<Token> tokens, Token first, Token last, string[] ps)
    {
        var lc = new LiteralChecks(tokens, new Catalog(), new Dictionary<string, List<ConstantDef>>(), new Dictionary<string, List<string[]>>(), "", NullSink.Instance);
        int a = tokens.IndexOf(first), b = tokens.IndexOf(last);
        var result = (string[])ps.Clone();
        if (a < 0 || b < a) return result;
        for (int q = a; q <= b; q++)
        {
            if (!(lc.Is(q, "==") || lc.Is(q, "!="))) continue;
            string? l = lc.IsIdent(q - 1) ? tokens[q - 1].Text : null, r = lc.IsIdent(q + 1) && !lc.Is(q + 2, ".") && !lc.Is(q + 2, "->") ? tokens[q + 1].Text : null;
            string? lo = lc.OperandBefore(q), ro = lc.OperandAfter(q);
            for (int k = 0; k < ps.Length; k++)
            {
                if (ps[k].Length == 0 || IdNaming(ps[k]).Vendor || IdNaming(ps[k]).Product) continue;
                if (r == ps[k] && lo != null && (IdNaming(lo).Vendor || IdNaming(lo).Product)) result[k] = lo;
                if (l == ps[k] && !lc.Is(q - 2, ".") && !lc.Is(q - 2, "->") && ro != null && (IdNaming(ro).Vendor || IdNaming(ro).Product)) result[k] = ro;
            }
        }
        return result;
    }

    // An array whose elements 0 and 1 are both compared with == or !=.
    bool ComparedByteByByte(string array)
    {
        bool zero = false, one = false;
        for (int k = 0; k + 3 < t.Count; k++)
        {
            if (!IsIdent(k) || t[k].Text != array || !Is(k + 1, "[") || !IsNumber(k + 2) || !Is(k + 3, "]")) continue;
            if (!(Is(k + 4, "==") || Is(k + 4, "!=") || Is(k - 1, "==") || Is(k - 1, "!="))) continue;
            if (t[k + 2].Value == 0) zero = true;
            if (t[k + 2].Value == 1) one = true;
        }
        return zero && one;
    }

    // An array compared byte by byte (Sig[0] == lo, hi != Sig[1]) with an
    // expression that mentions an ID or was assigned from one.
    bool ComparedWithId(string array)
    {
        for (int k = 0; k + 3 < t.Count; k++)
        {
            if (!IsIdent(k) || t[k].Text != array || !Is(k + 1, "[") || m[k + 1] <= k + 1) continue;
            int close = m[k + 1];
            if ((Is(close + 1, "==") || Is(close + 1, "!=")) && FromId(OperandAfter(close + 1))) return true;
            if ((Is(k - 1, "==") || Is(k - 1, "!=")) && FromId(OperandBefore(k - 1))) return true;
        }
        return false;
    }

    // The statement around k compares an expression named for an ID or
    // holds a catalog vendor ID as a four-digit hex literal.
    bool StatementNamesId(int k)
    {
        int a = k, b = k;
        while (a > 0 && !Boundary(a - 1)) a--;
        while (b < t.Count - 1 && !Boundary(b + 1)) b++;
        for (int q = a; q <= b; q++)
        {
            if ((Is(q, "==") || Is(q, "!=")) && OperandBefore(q) is string o && (NamingOf(o).Vendor || NamingOf(o).Product)) return true;
            if (IsNumber(q) && t[q].IsHex && t[q].HexDigits >= 4 && cat.Vids.Contains(t[q].Value)) return true;
        }
        return false;
    }

    void Str(int i)
    {
        var x = t[i];
        // A segment of an interpolated string, or a string inside a fold,
        // is also read whole by Folded.
        StrCheck(x.Content, x, i, i, null);
    }

    // The checks of one string, a literal or a fold: IDs in it, a profile
    // id, a device name compared, searched or given to a name, a regular
    // expression's pattern and a hex string parsed as an ID. pieces, for a
    // fold, are its parts' contents: a finding a part makes alone is
    // reported for that part and not again for the fold.
    void StrCheck(string content, Token at, int start, int end, List<string>? pieces)
    {
        string what = pieces == null ? "" : " folded from " + pieces.Count + " parts";
        bool InPiece(Func<string, bool> found) => pieces != null && pieces.Any(found);
        foreach (Match mt in s_idForm.Matches(content))
        {
            if (InPiece(pc => pc.Contains(mt.Value, StringComparison.OrdinalIgnoreCase))) continue;
            bool vendor = mt.Groups[1].Value.Equals("vid", StringComparison.OrdinalIgnoreCase);
            sink.Hit(pieces == null ? LineAt(at, mt.Index) : at.Line, vendor ? HitGroups.Vendor : HitGroups.Product,
                (vendor ? "vendor" : "product") + " ID in a string" + what, mt.Value);
        }
        string trimmed = content.Trim();
        var whole = s_wholeId.Match(trimmed);
        if (whole.Success && pieces != null && pieces.Any(pc => pc.Trim() == trimmed)) whole = Match.Empty;
        if (whole.Success)
        {
            ulong v = Convert.ToUInt64(whole.Groups[1].Value, 16);
            if (v != 0 && (cat.Vids.Contains(v) || cat.Pids.Contains(v)))
                sink.Hit(at.Line, cat.Vids.Contains(v) ? HitGroups.Vendor : HitGroups.Product, "ID as a whole string" + what, trimmed);
        }
        ParsedId(content, start, end, at.Line);

        if (cat.Ids.Contains(trimmed) && !InPiece(pc => pc.Trim() == trimmed)) sink.Hit(at.Line, HitGroups.Profile, "profile id string" + what, trimmed);
        else if (!cat.Ids.Contains(trimmed))
            foreach (Match mt in cat.HyphenatedIds.Matches(content))
            {
                if (InPiece(pc => cat.HyphenatedIds.Matches(pc).Any(q => q.Value == mt.Value))) continue;
                sink.Hit(pieces == null ? LineAt(at, mt.Index) : at.Line, HitGroups.Profile, "profile id in a string" + what, mt.Value);
            }
        if (pieces != null && Scan.IsMarker(content) && !pieces.Any(Scan.IsMarker))
            sink.Hit(at.Line, HitGroups.Marker, "frozen-compat marker in a string" + what, Shorten(content));

        string shown = Shorten(content);
        if (RegexPattern(start, end) is string callee)
        {
            var words = PatternWords(content);
            if (words.Count > 0 && !InPiece(pc => PatternWords(pc).Count >= words.Count))
                sink.Hit(at.Line, HitGroups.DeviceString, "device name in the pattern of " + callee + what, shown + " (" + string.Join(", ", words) + ")");
            return;
        }
        var device = DeviceWords(content);
        if (device.Count == 0 || InPiece(pc => DeviceWords(pc).Count >= device.Count)) return;
        var (used, how) = StringUse(start, end);
        if (used) sink.Hit(at.Line, HitGroups.DeviceString, "device name " + how + what, shown);
        else sink.Info(at.Line, how + what, shown);
    }

    // The string at start..end is a regular expression's pattern: the
    // second argument of Regex.IsMatch, Match, Matches, Replace or Split,
    // the first of new Regex(...), or a [GeneratedRegex(...)] attribute's.
    string? RegexPattern(int start, int end)
    {
        int call = EnclosingParen(start);
        if (call < 1 || !IsIdent(call - 1)) return null;
        if (!(Is(end + 1, ",") || Is(end + 1, ")"))) return null;
        int index = ArgIndex(call, start);
        string callee = t[call - 1].Text;
        if (s_regexStatic.Contains(callee) && Is(call - 2, ".") && IsIdent(call - 3) && t[call - 3].Text == "Regex" && index == 1)
            return "Regex." + callee;
        if (callee == "Regex" && Is(call - 2, "new") && index == 0) return "new Regex";
        if (callee is "GeneratedRegex" or "GeneratedRegexAttribute" && InAttribute(call) && index == 0) return "[GeneratedRegex]";
        return null;
    }

    // The 0-based argument index of the token at k in the call whose '('
    // is at open.
    int ArgIndex(int open, int k)
    {
        int index = 0;
        for (int q = open + 1; q < k; q = Skip(q) + 1)
            if (Is(q, ",")) index++;
        return index;
    }

    // A hex string parsed as a number that is a catalog ID: int.Parse(s,
    // NumberStyles.HexNumber), ushort.TryParse(s, NumberStyles.AllowHexSpecifier, ..),
    // Convert.ToUInt16(s, 16), strtoul(s, NULL, 16).
    void ParsedId(string content, int start, int end, int line)
    {
        int call = EnclosingParen(start);
        if (call < 1 || !IsIdent(call - 1) || ArgIndex(call, start) != 0 || !(Is(end + 1, ",") || Is(end + 1, ")"))) return;
        string callee = t[call - 1].Text;
        bool hex = false;
        if (callee is "Parse" or "TryParse" && Is(call - 2, ".") && call - 3 >= 0 && s_parseTypes.Contains(t[call - 3].Text))
            for (int q = call + 1; q < m[call] && q < t.Count; q++)
                if (IsIdent(q) && t[q].Text is "HexNumber" or "AllowHexSpecifier") hex = true;
        if ((callee is "ToUInt16" or "ToInt16" or "ToUInt32" or "ToInt32" or "ToUInt64" or "ToInt64" && Is(call - 2, ".") && IsIdent(call - 3) && t[call - 3].Text == "Convert")
            || callee is "strtoul" or "strtol" or "wcstoul" or "wcstol" or "_strtoui64" or "_wcstoui64" or "strtoull" or "wcstoull")
        {
            int close = m[call];
            if (close > call && IsNumber(close - 1) && t[close - 1].Value == 16 && Is(close - 2, ",")) hex = true;
        }
        string text = content.Trim();
        if (!hex || text.Length < 3 || text.Length > 8) return;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (!ulong.TryParse(text, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out ulong v)) return;
        if (!CatalogId(v)) return;
        sink.Hit(line, cat.Vids.Contains(v) ? HitGroups.Vendor : HitGroups.Product, "ID parsed from a hex string by " + callee, $"\"{content}\" ({v:X4})");
    }

    // ---- folded strings ----

    // A string's parts as written: a literal, a string constant, an
    // interpolated string whose holes are such parts, string.Concat of
    // parts, a part in parentheses, and parts joined by + (C#) or written
    // side by side (C, where a macro can name a string).
    void Folded()
    {
        for (int i = 0; i < t.Count; i++)
        {
            if (!(t[i].Kind == Tk.String || (IsIdent(i) && StringDef(i) != null) || IsConcat(i) || Is(i, "("))) continue;
            if (Expr(i, out int end, out var pieces) && pieces.Count >= 2)
            {
                StrCheck(string.Concat(pieces), t[i], i, end, pieces);
                i = end;
            }
        }
    }

    string? StringDef(int i)
    {
        var d = Defs(i, strings: true);
        return d.Count > 0 && d.All(x => x.Content == d[0].Content) ? d[0].Content : null;
    }

    bool IsConcat(int i) =>
        (Is(i, "string") || (IsIdent(i) && t[i].Text == "String")) && Is(i + 1, ".") && IsIdent(i + 2) && t[i + 2].Text == "Concat" && Is(i + 3, "(");

    // Parts joined into one string from i: + between parts, or parts side
    // by side when the file has C directives or the parts share an
    // interpolated string.
    bool Expr(int i, out int end, out List<string> pieces)
    {
        pieces = new List<string>();
        end = i - 1;
        int k = i;
        while (true)
        {
            if (!Part(k, out int pe, out var pp)) break;
            pieces.AddRange(pp);
            end = pe;
            if (Is(pe + 1, "+") && !Is(pe + 2, "+") && !Is(pe + 2, "=")) { k = pe + 2; continue; }
            if (pe + 1 < t.Count && (t[pe + 1].Kind == Tk.String || (IsIdent(pe + 1) && StringDef(pe + 1) != null))
                && (t[pe].Interp != 0 && t[pe + 1].Interp == t[pe].Interp || (t[pe].Interp == 0 && t[pe + 1].Interp == 0 && !IsCsFile())))
            { k = pe + 1; continue; }
            break;
        }
        return pieces.Count > 0;
    }

    bool? csFile;
    bool IsCsFile() => csFile ??= file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    bool Part(int k, out int end, out List<string> pieces)
    {
        pieces = new List<string>();
        end = k;
        if (k >= t.Count) return false;
        if (t[k].Kind == Tk.String)
        {
            if (t[k].Interp == 0 || t[k].Hole != 0) { pieces.Add(t[k].Content); return true; }
            // An interpolated string: its segments and holes in order.
            int id = t[k].Interp, q = k;
            while (q < t.Count && t[q].Interp == id)
            {
                if (t[q].Kind == Tk.String && t[q].Hole == 0) { pieces.Add(t[q].Content); end = q; q++; continue; }
                int hole = t[q].Hole, h = q;
                while (h + 1 < t.Count && t[h + 1].Interp == id && t[h + 1].Hole == hole) h++;
                if (!Expr(q, out int he, out var hp) || he != h) return pieces.Count > 0;
                pieces.AddRange(hp);
                end = h;
                q = h + 1;
            }
            return true;
        }
        // A macro's own name on its #define line is not a use of it.
        if (IsIdent(k) && !(k > 0 && t[k - 1].Kind == Tk.DirStart) && StringDef(k) is string c) { pieces.Add(c); return true; }
        if (IsConcat(k))
        {
            int open = k + 3, close = m[open];
            if (close <= open) return false;
            int q = open + 1;
            while (q < close)
            {
                if (!Expr(q, out int ae, out var ap) || !(Is(ae + 1, ",") || ae + 1 == close)) return false;
                pieces.AddRange(ap);
                q = ae + 2;
            }
            end = close;
            return pieces.Count > 0;
        }
        // A group in parentheses, not a call's or a keyword's argument list.
        bool group = !(k > 0 && (t[k - 1].Kind is Tk.Ident or Tk.Keyword || Is(k - 1, ")") || Is(k - 1, "]")));
        if (group && Is(k, "(") && m[k] > k + 1 && m[k] < t.Count && Expr(k + 1, out int ie, out var ip) && ie == m[k] - 1)
        {
            pieces.AddRange(ip);
            end = m[k];
            return true;
        }
        return false;
    }

    static string Shorten(string s)
    {
        s = s.Replace('\n', ' ').Replace("\r", "");
        return "\"" + (s.Length > 60 ? s.Substring(0, 57) + "..." : s) + "\"";
    }

    // How a string is used. Compared, searched or given to a name hits.
    (bool Used, string How) StringUse(int i) => StringUse(i, i);

    (bool Used, string How) StringUse(int i, int end)
    {
        if (Is(i - 1, "==") || Is(i - 1, "!=") || Is(end + 1, "==") || Is(end + 1, "!=")) return (true, "compared");
        int k = i - 1;
        while (k >= 0 && i - k < 24 && (t[k].Kind == Tk.String || Is(k, "(") || Is(k, ")") || (IsIdent(k) && t[k].Text is "or" or "and" or "not")))
            k--;
        if (Is(k, "is") || Is(k, "case")) return (true, "compared");
        if (Is(k, "{") || Is(k, ","))
        {
            int open = Enclosing(k + 1);
            if (open >= 0 && Is(open - 1, "switch")) return (true, "compared");
        }
        int call = EnclosingParen(i);
        if (call > 0 && IsIdent(call - 1))
        {
            string callee = t[call - 1].Text;
            if (s_search.Contains(callee)) return (true, "searched by " + callee);
            if (InAttribute(call)) return (false, "attribute " + callee);
            if (Is(call - 2, "new")) return (false, "message of new " + callee);
            return (false, "argument of " + callee);
        }
        if (Is(i - 1, "=") || (Is(i - 1, ":") && IsIdent(i - 2)))
        {
            string? target = Is(i - 1, "=") ? AssignTarget(i - 1) : t[i - 2].Text;
            if (target != null && Words.Split(target).Any(w => s_nameWords.Contains(w.ToLowerInvariant())))
                return (true, "given to " + target);
            return (false, "initializer of " + (target ?? "an expression"));
        }
        if (Is(i - 1, "return") || Is(i - 1, "=>")) return (false, "return value");
        if (Is(i - 1, "+") || Is(end + 1, "+")) return (false, "concatenation");
        return (false, "other");
    }

    // The '(' is an attribute's argument list: a '[' opens before any
    // statement boundary.
    bool InAttribute(int open)
    {
        for (int k = open - 2; k >= 0; k--)
        {
            if (Is(k, "]") || Is(k, ";") || Is(k, "{") || Is(k, "}") || Is(k, "(") || Is(k, "=")) return false;
            if (Is(k, "[")) return true;
        }
        return false;
    }

    // The '(' whose group holds index i directly, or -1.
    int EnclosingParen(int i)
    {
        for (int j = i - 1; j >= 0; j--)
        {
            if (Is(j, ")") || Is(j, "]") || Is(j, "}"))
            {
                if (m[j] >= 0 && m[j] < j) j = m[j];
                continue;
            }
            if (Is(j, "(")) return j;
            if (Is(j, "{") || Is(j, "[") || Is(j, ";")) return -1;
        }
        return -1;
    }

    static int LineAt(Token x, int index)
    {
        int ln = x.Line;
        for (int k = 0; k < index && k < x.Content.Length; k++) if (x.Content[k] == '\n') ln++;
        return ln;
    }

    // What the literal at st..i is compared with, switched on or assigned to.
    (string? Ctx, string? Operand) Context(int st, int i)
    {
        if (Is(st - 1, "==") || Is(st - 1, "!=")) return ("comparison", OperandBefore(st - 1));
        if (Is(i + 1, "==") || Is(i + 1, "!=")) return ("comparison", OperandAfter(i + 1));

        // Back over a pattern (0x2009 or 0x2069, not 0x2009) to is, case or a switch arm.
        int k = st - 1;
        while (k >= 0 && st - k < 24 && (IsNumber(k) || IsConstant(k) || Is(k, "(") || Is(k, ")") || (IsIdent(k) && t[k].Text is "or" or "and" or "not")))
            k--;
        if (Is(k, "is")) return ("comparison", OperandBefore(k));
        if (Is(k, "case")) return ("case label", SwitchOperand(k));
        if (Is(k, "{") || Is(k, ","))
        {
            int open = Enclosing(k + 1);
            if (open >= 0 && Is(open - 1, "switch") && ArmFollows(i)) return ("case label", OperandBefore(open - 1));
        }

        if ((Is(st - 1, "(") || Is(st - 1, ",")) && (Is(i + 1, ")") || Is(i + 1, ",")))
        {
            int call = EnclosingParen(st);
            if (call > 0 && IsIdent(call - 1) && signatures.TryGetValue(t[call - 1].Text, out var sigs) && !Is(call - 2, "new"))
            {
                int index = ArgIndex(call, st);
                foreach (var sig in sigs)
                    if (index < sig.Length && sig[index].Length > 0 && (IdNaming(sig[index]).Vendor || IdNaming(sig[index]).Product))
                        return ("argument of " + t[call - 1].Text + ", bound to", sig[index]);
            }
        }
        if (Is(st - 1, "=")) return ("initializer", AssignTarget(st - 1));
        if (Is(st - 1, ":") && IsIdent(st - 2) && (Is(st - 3, "(") || Is(st - 3, ","))) return ("initializer", t[st - 2].Text);
        if (IsIdent(st - 1) && st - 2 >= 0 && t[st - 2].Kind == Tk.DirStart && t[st - 2].Text == "define")
            return ("initializer", t[st - 1].Text);
        if (Is(st - 1, "(") && IsIdent(st - 2) && st - 3 >= 0 && t[st - 3].Kind == Tk.DirStart && t[st - 3].Text == "define")
            return ("initializer", t[st - 2].Text);
        return (null, null);
    }

    bool ArmFollows(int i)
    {
        int k = i + 1;
        while (k < t.Count && (IsNumber(k) || IsConstant(k) || Is(k, "(") || Is(k, ")") || (IsIdent(k) && t[k].Text is "or" or "and" or "not"))) k++;
        return Is(k, "=>") || (IsIdent(k) && t[k].Text == "when");
    }

    // The unmatched '{' that encloses index k, or -1.
    int Enclosing(int k)
    {
        for (int j = k - 1; j >= 0; j--)
        {
            if (Is(j, "}") || Is(j, ")") || Is(j, "]"))
            {
                if (m[j] >= 0 && m[j] < j) j = m[j];
                continue;
            }
            if (Is(j, "{")) return j;
        }
        return -1;
    }

    string? SwitchOperand(int caseIdx)
    {
        int open = Enclosing(caseIdx);
        if (open < 1 || !Is(open - 1, ")")) return null;
        int po = m[open - 1];
        if (po < 1 || !Is(po - 1, "switch")) return null;
        return LastName(po + 1, open - 1);
    }

    string? AssignTarget(int eq)
    {
        if (Is(eq - 1, "}") && m[eq - 1] >= 1 && IsIdent(m[eq - 1] - 1)) return t[m[eq - 1] - 1].Text;
        return OperandBefore(eq);
    }

    // The member name an expression ends in, read back from an operator.
    string? OperandBefore(int op)
    {
        int j = op - 1;
        if (Is(j, "!")) j--;
        if ((Is(j, ")") || Is(j, "]")) && m[j] >= 0 && m[j] < j)
        {
            int open = m[j];
            if (IsIdent(open - 1)) return t[open - 1].Text;
            return LastName(open + 1, j);
        }
        return IsIdent(j) ? t[j].Text : null;
    }

    string? OperandAfter(int op)
    {
        int j = op + 1;
        if (Is(j, "(") && m[j] > j)
        {
            int close = m[j];
            bool cast = close == j + 2 && t[j + 1].Kind is Tk.Ident or Tk.Keyword && (IsIdent(close + 1) || Is(close + 1, "("));
            if (!cast) return LastName(j + 1, close);
            j = close + 1;
            if (Is(j, "(") && m[j] > j) return LastName(j + 1, m[j]);
        }
        string? name = null;
        while (IsIdent(j))
        {
            name = t[j].Text;
            if ((Is(j + 1, ".") || Is(j + 1, "->") || Is(j + 1, "?.") || Is(j + 1, "::")) && IsIdent(j + 2)) { j += 2; continue; }
            break;
        }
        return name;
    }

    string? LastName(int from, int to)
    {
        string? last = null;
        for (int k = from; k < to; k++) if (IsIdent(k)) last = t[k].Text;
        return last;
    }

    // A catalog vendor ID hex literal in the same statement that the catalog
    // pairs with this product ID.
    bool VendorInStatement(int i, ulong pid, out string vid)
    {
        vid = "";
        int a = i, b = i;
        while (a > 0 && !Boundary(a - 1)) a--;
        while (b < t.Count - 1 && !Boundary(b + 1)) b++;
        for (int k = a; k <= b; k++)
        {
            var x = t[k];
            if (k != i && x.Kind == Tk.Number && x.IsInt && x.IsHex && x.HexDigits >= 4 && cat.Pairs.Contains((x.Value, pid)))
            {
                vid = x.Text;
                return true;
            }
        }
        return false;
    }

    bool Boundary(int k) => Is(k, ";") || Is(k, "{") || Is(k, "}") || t[k].Kind is Tk.DirStart or Tk.DirEnd;
}
