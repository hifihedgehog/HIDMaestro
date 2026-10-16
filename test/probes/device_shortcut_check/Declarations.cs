// Declared names, found over the token stream with brackets matched first.
//
// C#: classes, structs, records (and their positional properties),
// interfaces, enums and their members, delegates, methods (local functions
// included), properties, fields, constants and events. Constructors,
// destructors, operators, indexers, locals and parameters are not names.
//
// C and C++: functions (definitions and prototypes), struct, union and class
// tags and their fields and methods, typedefs, enum constants, macros
// (#define), file-scope variables and static variables inside functions,
// and the identifier arguments of a file-scope macro invocation
// (WDF_DECLARE_CONTEXT_TYPE_WITH_NAME names its accessor that way). Other
// locals and parameters are not names.
using System;
using System.Collections.Generic;
using System.Linq;

internal sealed record Decl(int Line, string Kind, string Name);

internal static class Brackets
{
    // For each ( [ { the index of its closer and for each closer its opener,
    // -1 elsewhere. An unmatched opener matches the end of the list, an
    // unmatched closer nothing.
    public static int[] Match(List<Token> t)
    {
        var m = new int[t.Count];
        Array.Fill(m, -1);
        var stack = new Stack<int>();
        for (int i = 0; i < t.Count; i++)
        {
            if (t[i].Kind != Tk.Punct) continue;
            string x = t[i].Text;
            if (x is "(" or "[" or "{") stack.Push(i);
            else if (x is ")" or "]" or "}")
            {
                string open = x == ")" ? "(" : x == "]" ? "[" : "{";
                // Pop to the nearest opener of the same kind.
                if (stack.Any(k => t[k].Text == open))
                {
                    while (true)
                    {
                        int k = stack.Pop();
                        if (t[k].Text == open) { m[k] = i; m[i] = k; break; }
                        m[k] = i;
                    }
                }
            }
        }
        while (stack.Count > 0) m[stack.Pop()] = t.Count;
        return m;
    }
}

internal sealed class CsDecls
{
    readonly List<Token> t;
    readonly int[] m;
    public readonly List<Decl> Found = new();

    static readonly HashSet<string> s_predefined = new(StringComparer.Ordinal)
    {
        "bool","byte","sbyte","short","ushort","int","uint","long","ulong","char","float","double",
        "decimal","string","object","void",
    };

    public CsDecls(List<Token> tokens)
    {
        t = tokens;
        m = Brackets.Match(t);
        Members(0, t.Count, null, false);
    }

    bool Is(int i, string text) => i >= 0 && i < t.Count && t[i].Text == text && t[i].Kind is Tk.Punct or Tk.Keyword or Tk.Ident;
    bool IsIdent(int i) => i >= 0 && i < t.Count && t[i].Kind == Tk.Ident;
    bool IsOpen(int i) => i < t.Count && t[i].Kind == Tk.Punct && t[i].Text is "(" or "[" or "{";
    int Skip(int i) => IsOpen(i) ? Math.Min(m[i], t.Count - 1) : i;

    void Add(int i, string kind) => Found.Add(new Decl(t[i].Line, kind, t[i].Text));

    // Index of the depth-0 semicolon that ends an expression.
    int ToSemicolon(int i, int end)
    {
        while (i < end && !Is(i, ";")) i = Skip(i) + 1;
        return i;
    }

    void Members(int i, int end, string? typeName, bool inType)
    {
        while (i < end)
        {
            if (Is(i, ";") || Is(i, "}")) { i++; continue; }
            while (i < end && Is(i, "[")) i = Skip(i) + 1;
            if (i >= end) break;
            if (Is(i, "using") || (IsIdent(i) && t[i].Text == "global" && Is(i + 1, "using")) || Is(i, "extern"))
            {
                i = ToSemicolon(i, end) + 1;
                continue;
            }

            // The header runs to a depth-0 semicolon, brace, = or =>.
            int j = i;
            bool sawParen = false, sawWhere = false;
            int typeKw = -1, delegateKw = -1, eventKw = -1, nsKw = -1, firstParen = -1;
            bool isConst = false, isFixed = false, hasOperator = false;
            while (j < end)
            {
                var x = t[j];
                if (x.Kind == Tk.Punct && x.Text is ";" or "{" or "=" or "=>" or "}") break;
                if (x.Kind == Tk.Punct && x.Text == "(")
                {
                    if (firstParen < 0 && (IsIdent(j - 1) || Is(j - 1, ">"))) firstParen = j;
                    sawParen = true;
                }
                if (x.Kind == Tk.Keyword && x.Text == "operator") hasOperator = true;
                if (!sawParen && !sawWhere)
                {
                    if (x.Kind == Tk.Keyword && x.Text is "class" or "struct" or "interface" or "enum" && typeKw < 0) typeKw = j;
                    if (x.Kind == Tk.Ident && x.Text == "record" && typeKw < 0 && (IsIdent(j + 1) || Is(j + 1, "struct") || Is(j + 1, "class"))) typeKw = j;
                    if (x.Kind == Tk.Keyword && x.Text == "delegate" && delegateKw < 0) delegateKw = j;
                    if (x.Kind == Tk.Keyword && x.Text == "namespace") nsKw = j;
                }
                if (x.Kind == Tk.Ident && x.Text == "where") sawWhere = true;
                if (x.Kind == Tk.Keyword && x.Text == "event") eventKw = j;
                if (x.Kind == Tk.Keyword && x.Text == "const") isConst = true;
                if (x.Kind == Tk.Keyword && x.Text == "fixed") isFixed = true;
                j = Skip(j) + 1;
            }
            if (j >= end) break;
            string term = t[j].Text;
            if (term == "}") { i = j + 1; continue; }

            if (nsKw >= 0)
            {
                if (term == "{") { Members(j + 1, m[j], null, false); i = m[j] + 1; }
                else i = j + 1;
                continue;
            }

            if (typeKw >= 0)
            {
                int k = typeKw;
                string kind = t[k].Text;
                if (kind == "record")
                {
                    if (Is(k + 1, "struct") || Is(k + 1, "class")) { kind = "record " + t[k + 1].Text; k++; }
                }
                int nameIdx = k + 1;
                if (IsIdent(nameIdx))
                {
                    Add(nameIdx, kind == "enum" ? "enum" : kind);
                    if (kind.StartsWith("record", StringComparison.Ordinal))
                    {
                        int q = nameIdx + 1;
                        if (Is(q, "<")) q = AngleEnd(q) + 1;
                        if (Is(q, "(")) RecordParams(q);
                    }
                }
                if (term == "{")
                {
                    if (kind == "enum") EnumBody(j + 1, m[j]);
                    else Members(j + 1, m[j], IsIdent(nameIdx) ? t[nameIdx].Text : null, true);
                    i = m[j] + 1;
                }
                else i = ToSemicolon(j, end) + 1;
                continue;
            }

            if (delegateKw >= 0)
            {
                int n = NameBeforeParen(firstParen);
                if (n >= 0) Add(n, "delegate");
                i = ToSemicolon(j, end) + 1;
                continue;
            }

            if (eventKw >= 0)
            {
                if (term == "{")
                {
                    int n = LastIdent(i, j);
                    if (n >= 0) Add(n, "event");
                    i = m[j] + 1;
                }
                else
                {
                    Declarators(i, j, end, "event", isFixed, out i);
                }
                continue;
            }

            if (hasOperator)
            {
                // Operators and conversions have no name.
                i = term == "{" ? m[j] + 1 : ToSemicolon(j, end) + 1;
                continue;
            }
            int name = firstParen >= 0 ? NameBeforeParen(firstParen) : -1;
            if (name >= 0)
            {
                bool ctor = typeName != null && t[name].Text == typeName && !IsIdent(name - 1) && !IsPredefined(name - 1);
                bool dtor = Is(name - 1, "~");
                if (!ctor && !dtor) Add(name, "method");
                if (term == "{") { LocalFunctions(j + 1, m[j]); i = m[j] + 1; }
                else if (term == "=>") { int e = ToSemicolon(j, end); LocalFunctions(j + 1, e); i = e + 1; }
                else i = ToSemicolon(j, end) + 1;
                continue;
            }
            Property(i, j, end, term, isConst, isFixed, out i);
        }
    }

    void Property(int i, int j, int end, string term, bool isConst, bool isFixed, out int next)
    {
        if (term == "{")
        {
            int n = Is(j - 1, "]") ? -1 : LastIdent(i, j);
            if (n >= 0) Add(n, "property");
            LocalFunctions(j + 1, m[j]);
            int after = m[j] + 1;
            if (Is(after, "=")) after = ToSemicolon(after, end) + 1;
            next = after;
            return;
        }
        if (term == "=>")
        {
            int n = Is(j - 1, "]") ? -1 : LastIdent(i, j);
            if (n >= 0) Add(n, "property");
            int e = ToSemicolon(j, end);
            LocalFunctions(j + 1, e);
            next = e + 1;
            return;
        }
        Declarators(i, j, end, isConst ? "constant" : "field", isFixed, out next);
    }

    // A field or event header that ends at a semicolon or '='.
    void Declarators(int i, int j, int end, string kind, bool isFixed, out int next)
    {
        if (t[j].Text == ";")
        {
            int angle = 0;
            for (int k = i; k < j; k = Skip(k) + 1)
            {
                if (Is(k, "<") && (IsIdent(k - 1) || IsPredefined(k - 1))) angle++;
                else if (Is(k, ">") && angle > 0) angle--;
                else if (angle == 0 && IsIdent(k) && (Is(k + 1, ",") || k + 1 == j || (isFixed && Is(k + 1, "["))))
                    Add(k, kind);
            }
            next = j + 1;
            return;
        }
        // '=': the name before it, then any further declarators in the initializer.
        int n = j - 1;
        if (Is(n, "]")) n = m[n] - 1;
        if (IsIdent(n)) Add(n, kind);
        int e = j + 1;
        while (e < end && !Is(e, ";"))
        {
            if (Is(e, ",") && IsIdent(e + 1) && (Is(e + 2, "=") || Is(e + 2, ",") || Is(e + 2, ";")))
                Add(e + 1, kind);
            e = Skip(e) + 1;
        }
        LocalFunctions(j + 1, e);
        next = e + 1;
    }

    bool IsPredefined(int i) => i >= 0 && i < t.Count && t[i].Kind == Tk.Keyword && s_predefined.Contains(t[i].Text);

    int AngleEnd(int i)
    {
        int d = 0;
        for (int k = i; k < t.Count; k++)
        {
            if (Is(k, "<")) d++;
            else if (Is(k, ">")) { d--; if (d == 0) return k; }
            else if (Is(k, ";") || Is(k, "{")) return k;
        }
        return t.Count - 1;
    }

    int AngleStart(int close)
    {
        int d = 0;
        for (int k = close; k >= 0; k--)
        {
            if (Is(k, ">")) d++;
            else if (Is(k, "<")) { d--; if (d == 0) return k; }
            else if (Is(k, ";") || Is(k, "{") || Is(k, "}")) return -1;
        }
        return -1;
    }

    // The identifier a parameter list belongs to: Name( or Name<T>(.
    int NameBeforeParen(int paren)
    {
        if (paren < 1) return -1;
        int k = paren - 1;
        if (Is(k, ">"))
        {
            int a = AngleStart(k);
            if (a < 1) return -1;
            k = a - 1;
        }
        if (!IsIdent(k)) return -1;
        if (Is(k - 1, "operator") || Is(k - 1, ".") && Is(k - 2, "operator")) return -1;
        return k;
    }

    int LastIdent(int i, int j)
    {
        int last = -1;
        for (int k = i; k < j; k = Skip(k) + 1)
            if (IsIdent(k) && !(t[k].Text is "get" or "set" or "init")) last = k;
        return last;
    }

    void RecordParams(int open)
    {
        int close = m[open];
        int start = open + 1;
        for (int k = start; k <= close; k = Skip(k) + 1)
        {
            if (k == close || Is(k, ","))
            {
                int last = -1;
                for (int q = start; q < k; q = Skip(q) + 1)
                {
                    if (Is(q, "=")) break;
                    if (IsIdent(q)) last = q;
                }
                if (last >= 0) Add(last, "property");
                start = k + 1;
            }
        }
    }

    void EnumBody(int i, int end)
    {
        bool itemStart = true;
        for (int k = i; k < end; k = Skip(k) + 1)
        {
            if (Is(k, ",")) { itemStart = true; continue; }
            if (Is(k, "[")) continue;
            if (itemStart && IsIdent(k)) Add(k, "enum member");
            itemStart = false;
        }
    }

    // Local functions inside a code range: Type Name(...) { or =>.
    void LocalFunctions(int i, int end)
    {
        for (int k = i; k < end && k < t.Count; k++)
        {
            if (!IsIdent(k)) continue;
            int paren = k + 1;
            if (Is(paren, "<"))
            {
                int a = AngleEnd(paren);
                if (!Is(a, ">")) continue;
                paren = a + 1;
            }
            if (!Is(paren, "(")) continue;
            int prev = k - 1;
            bool typeEnd = IsIdent(prev) || IsPredefined(prev) || Is(prev, ">") || Is(prev, "]") || Is(prev, "?") || Is(prev, "*");
            if (!typeEnd) continue;
            if (IsIdent(prev) && t[prev].Text is "var" or "await" or "yield" or "when" or "is" or "or" or "and" or "not" or "nameof") continue;
            if (Is(prev - 1, ".") || Is(prev - 1, "?.") || Is(prev - 1, "new")) continue;
            int after = Math.Min(m[paren], t.Count - 1) + 1;
            if (Is(after, "{") || Is(after, "=>") || (IsIdent(after) && t[after].Text == "where"))
                Add(k, "local function");
        }
    }
}

internal sealed class CDecls
{
    readonly List<Token> t;
    readonly int[] m;
    public readonly List<Decl> Found = new();

    static readonly HashSet<string> s_typeKw = new(StringComparer.Ordinal) { "struct", "union", "enum", "class" };

    public CDecls(List<Token> all)
    {
        // Macros come from the directive tokens, the rest from the stream
        // without them.
        for (int i = 0; i < all.Count; i++)
            if (all[i].Kind == Tk.DirStart && all[i].Text == "define" && i + 1 < all.Count && all[i + 1].Kind is Tk.Ident or Tk.Keyword)
                Found.Add(new Decl(all[i + 1].Line, "macro", all[i + 1].Text));
        t = new List<Token>();
        bool inDir = false;
        foreach (var x in all)
        {
            if (x.Kind == Tk.DirStart) { inDir = true; continue; }
            if (x.Kind == Tk.DirEnd) { inDir = false; continue; }
            if (!inDir) t.Add(x);
        }
        m = Brackets.Match(t);
        Scope(0, t.Count, file: true);
    }

    bool Is(int i, string text) => i >= 0 && i < t.Count && t[i].Text == text && t[i].Kind is Tk.Punct or Tk.Keyword or Tk.Ident;
    bool IsIdent(int i) => i >= 0 && i < t.Count && t[i].Kind == Tk.Ident;
    bool IsOpen(int i) => i < t.Count && t[i].Kind == Tk.Punct && t[i].Text is "(" or "[" or "{";
    int Skip(int i) => IsOpen(i) ? Math.Min(m[i], t.Count - 1) : i;
    void Add(int i, string kind) => Found.Add(new Decl(t[i].Line, kind, t[i].Text));

    // Reserved names (SAL annotations, __declspec, __attribute__) that take
    // arguments in a declaration without being its name.
    static bool Reserved(string w) => w.StartsWith("__", StringComparison.Ordinal) || (w.Length > 1 && w[0] == '_' && char.IsUpper(w[1]));

    void Scope(int i, int end, bool file)
    {
        while (i < end)
        {
            if (Is(i, ";") || Is(i, "}")) { i++; continue; }
            if (Is(i, "namespace"))
            {
                int b = i + 1;
                while (b < end && !Is(b, "{") && !Is(b, ";")) b++;
                if (Is(b, "{")) { Scope(b + 1, m[b], true); i = m[b] + 1; } else i = b + 1;
                continue;
            }
            if (Is(i, "extern") && i + 1 < end && t[i + 1].Kind == Tk.String && Is(i + 2, "{"))
            {
                Scope(i + 3, m[i + 2], file);
                i = m[i + 2] + 1;
                continue;
            }
            if (Is(i, "template") && Is(i + 1, "<"))
            {
                int d = 0, k = i + 1;
                for (; k < end; k++) { if (Is(k, "<")) d++; else if (Is(k, ">") && --d == 0) break; }
                i = k + 1;
                continue;
            }
            if ((Is(i, "public") || Is(i, "private") || Is(i, "protected")) && Is(i + 1, ":")) { i += 2; continue; }
            if (Is(i, "using") || Is(i, "friend") || Is(i, "static_assert"))
            {
                while (i < end && !Is(i, ";")) i = Skip(i) + 1;
                i++;
                continue;
            }
            // A statement that opens with Name( is a macro invocation.
            if (IsIdent(i) && Is(i + 1, "("))
            {
                int close = Math.Min(m[i + 1], end);
                if (file && !Reserved(t[i].Text))
                    for (int k = i + 2; k < close; k = Skip(k) + 1)
                        if (IsIdent(k) && (Is(k + 1, ",") || k + 1 == close) && (Is(k - 1, ",") || k - 1 == i + 1))
                            Add(k, "macro argument");
                i = close + 1;
                if (Is(i, ";")) i++;
                continue;
            }

            int j = i, typeKw = -1, firstParen = -1;
            bool typedef = Is(i, "typedef");
            while (j < end)
            {
                if (Is(j, ";") || Is(j, "{") || Is(j, "=") || Is(j, "}")) break;
                if (Is(j, "(") && firstParen < 0)
                {
                    // __declspec(x) and SAL's _In_reads_(n) are not the parameter list.
                    if (!(IsIdent(j - 1) && Reserved(t[j - 1].Text))) firstParen = j;
                }
                if (firstParen < 0 && typeKw < 0 && t[j].Kind == Tk.Keyword && s_typeKw.Contains(t[j].Text)) typeKw = j;
                j = Skip(j) + 1;
            }
            if (j >= end) break;
            string term = t[j].Text;

            if (typeKw >= 0 && (term == "{" || term == ";") && firstParen < 0)
            {
                string kw = t[typeKw].Text;
                int tag = typeKw + 1;
                if (kw == "enum" && (Is(tag, "class") || Is(tag, "struct"))) tag++;
                bool hasTag = IsIdent(tag);
                if (term == "{")
                {
                    if (hasTag) Add(tag, kw + " tag");
                    if (kw == "enum") EnumBody(j + 1, m[j]);
                    else Scope(j + 1, m[j], false);
                    int after = m[j] + 1;
                    // Declarators after the body: typedef names, variables or fields.
                    int e = after;
                    while (e < end && !Is(e, ";")) e = Skip(e) + 1;
                    string kind = typedef ? "typedef" : file ? "variable" : "field";
                    DeclaratorList(after, e, kind);
                    i = e + 1;
                    continue;
                }
                // A forward declaration (struct X) or a variable (struct X name).
                if (hasTag && tag + 1 == j) Add(tag, kw + " tag");
                else DeclaratorList(i, j, typedef ? "typedef" : file ? "variable" : "field");
                i = j + 1;
                continue;
            }

            if (term == "{")
            {
                if (firstParen >= 0)
                {
                    int n = FunctionName(firstParen);
                    if (n >= 0) Add(n, file ? "function" : "method");
                    StaticLocals(j + 1, m[j]);
                }
                else if (!file)
                {
                    int n = LastIdent(i, j);
                    if (n >= 0) Add(n, "field");
                }
                i = m[j] + 1;
                if (!file && Is(i, ";")) i++;
                continue;
            }

            // The header ends at a semicolon or '='.
            int stop = j;
            if (term == "=")
            {
                stop = j;
                while (stop < end && !Is(stop, ";")) stop = Skip(stop) + 1;
            }
            if (typedef)
            {
                int fp = FunctionPointerName(i, j);
                if (fp >= 0) Add(fp, "typedef");
                else if (firstParen >= 0) { int n = FunctionName(firstParen); if (n >= 0) Add(n, "typedef"); }
                else DeclaratorList(i + 1, stop, "typedef");
            }
            else if (firstParen >= 0)
            {
                int fp = FunctionPointerName(i, j);
                if (fp >= 0) Add(fp, file ? "variable" : "field");
                else
                {
                    int n = FunctionName(firstParen);
                    if (n >= 0) Add(n, file ? "function" : "method");
                }
            }
            else DeclaratorList(i, stop, file ? "variable" : "field");
            i = stop + 1;
        }
    }

    int FunctionName(int paren)
    {
        int k = paren - 1;
        if (!IsIdent(k)) return -1;
        if (Is(k - 1, "operator")) return -1;
        return k;
    }

    // ( * Name ) in a header.
    int FunctionPointerName(int i, int j)
    {
        for (int k = i; k < j; k++)
            if (Is(k, "(") && Is(k + 1, "*"))
            {
                int q = k + 1;
                while (Is(q, "*") || Is(q, "const")) q++;
                if (IsIdent(q) && Is(q + 1, ")")) return q;
            }
        return -1;
    }

    int LastIdent(int i, int j)
    {
        int last = -1;
        for (int k = i; k < j; k = Skip(k) + 1) if (IsIdent(k)) last = k;
        return last;
    }

    // Comma-separated declarators from i to the semicolon at stop: each one's name
    // is its last identifier before [ : = or a nested (.
    void DeclaratorList(int i, int stop, string kind)
    {
        int start = i;
        for (int k = i; k <= stop; k = Skip(k) + 1)
        {
            if (k == stop || Is(k, ","))
            {
                int fp = FunctionPointerName(start, k);
                if (fp >= 0) Add(fp, kind);
                else
                {
                    int last = -1;
                    for (int q = start; q < k; q = Skip(q) + 1)
                    {
                        if (Is(q, "=") || Is(q, ":") || Is(q, "[")) break;
                        if (IsIdent(q) && !(Reserved(t[q].Text) && Is(q + 1, "("))) last = q;
                    }
                    if (last >= 0) Add(last, kind);
                }
                start = k + 1;
                if (k == stop) break;
            }
        }
    }

    // A static inside a function body has static storage like a file-scope
    // variable (the driver keeps its feature tables that way).
    void StaticLocals(int i, int end)
    {
        for (int k = i; k < end && k < t.Count; k++)
        {
            if (!Is(k, "static") || !(Is(k - 1, ";") || Is(k - 1, "{") || Is(k - 1, "}"))) continue;
            int stop = k;
            while (stop < end && !Is(stop, ";")) stop = Skip(stop) + 1;
            DeclaratorList(k, stop, "static variable");
            k = stop;
        }
    }

    void EnumBody(int i, int end)
    {
        bool itemStart = true;
        for (int k = i; k < end; k = Skip(k) + 1)
        {
            if (Is(k, ",")) { itemStart = true; continue; }
            if (itemStart && IsIdent(k)) Add(k, "enum constant");
            itemStart = false;
        }
    }
}
