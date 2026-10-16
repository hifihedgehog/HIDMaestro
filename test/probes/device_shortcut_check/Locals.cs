// Locals and parameters, in C# and C/C++, read from the token stream at any
// depth. The member and file-scope finders in Declarations.cs run first, so
// a name both find keeps the kind they gave it.
//
// A parameter is an item of a parameter list: the group after a declared
// name (a type or a modifier before the name, and a brace, =>, a
// semicolon, a colon, where, const or override after the group), the
// group before a lambda's =>, a lambda's single parameter, and the group
// after delegate. Its name is the item's last identifier before a default
// value.
//
// A local is an identifier that a type ends right before and that a
// declaration's punctuation follows (=, a semicolon, a comma, a closing
// parenthesis, [ or in), where the type starts after a statement boundary,
// an opening parenthesis, a comma or a declaring keyword (out, ref, in,
// is, case, const, using, fixed, static).
// A type is identifiers and type keywords joined by . or ::, with generic
// arguments, ?, [] and pointer stars. After is, case, out and the pattern
// words any follower counts (x is Foo f && ...). Further declarators of
// the same statement (int a = 1, b = 2) are locals too, and so are the
// names after var (, from, let, join and into.
using System;
using System.Collections.Generic;

internal sealed class Locals
{
    readonly List<Token> t;
    readonly int[] m;
    readonly bool cs;
    public readonly List<Decl> Found = new();
    /// <summary>Each declared function or method with its parameter names
    /// in order ("" where an item has no name), for the checks that read a
    /// call's argument by the parameter it binds.</summary>
    public readonly List<(string Name, string[] Params, Token? BodyFirst, Token? BodyLast)> Signatures = new();

    // Words that are never a declared name or a type.
    static readonly HashSet<string> s_notNames = new(StringComparer.Ordinal)
    {
        "var", "await", "yield", "nameof", "async", "when", "where", "get", "set", "init", "add", "remove",
        "partial", "global", "value", "and", "or", "not", "with", "from", "select", "let", "join", "into",
        "orderby", "group", "by", "on", "equals", "ascending", "descending", "record", "managed", "unmanaged",
        "dynamic", "file", "required", "scoped", "_",
    };
    static readonly HashSet<string> s_notTypes = new(StringComparer.Ordinal)
    {
        "await", "yield", "nameof", "when", "where", "and", "or", "not", "with", "from", "select", "let",
        "join", "into", "orderby", "group", "by", "on", "equals", "ascending", "descending", "async",
    };
    static readonly HashSet<string> s_typeKeywords = new(StringComparer.Ordinal)
    {
        "bool","byte","sbyte","short","ushort","int","uint","long","ulong","char","float","double","decimal",
        "string","object","void","unsigned","signed","const","volatile","struct","union","enum","_Bool",
    };
    // Keywords a local's type may follow.
    static readonly HashSet<string> s_declKeywords = new(StringComparer.Ordinal)
    {
        "out", "ref", "in", "is", "case", "const", "using", "readonly", "params", "this", "static", "fixed",
        "volatile", "unsafe", "extern", "register", "auto",
    };
    static readonly HashSet<string> s_patternContext = new(StringComparer.Ordinal) { "is", "case", "out", "and", "or", "not" };
    // Keywords a declared name's parameter list may follow.
    static readonly HashSet<string> s_memberKeywords = new(StringComparer.Ordinal)
    {
        "public","private","protected","internal","static","virtual","override","abstract","sealed","extern",
        "unsafe","async","implicit","explicit","operator","inline","readonly",
    };

    public Locals(List<Token> tokens, bool csharp)
    {
        t = tokens;
        cs = csharp;
        m = Brackets.Match(t);
        for (int k = 0; k < t.Count; k++)
        {
            if (Is(k, "(")) ParamList(k);
            if (IsIdent(k)) Local(k);
        }
    }

    bool Is(int i, string text) => i >= 0 && i < t.Count && t[i].Text == text && t[i].Kind is Tk.Punct or Tk.Keyword or Tk.Ident;
    bool IsIdent(int i) => i >= 0 && i < t.Count && t[i].Kind == Tk.Ident;
    bool IsName(int i) => IsIdent(i) && !s_notNames.Contains(t[i].Text);
    bool IsTypeWord(int i) => i >= 0 && i < t.Count
        && ((t[i].Kind == Tk.Ident && !s_notTypes.Contains(t[i].Text)) || (t[i].Kind == Tk.Keyword && s_typeKeywords.Contains(t[i].Text)));
    int Close(int open) => m[open] > open && m[open] < t.Count ? m[open] : -1;
    void Add(int i, string kind) => Found.Add(new Decl(t[i].Line, kind, t[i].Text));

    // ---- parameter lists ----

    void ParamList(int open)
    {
        int close = Close(open);
        if (close < 0) return;
        int after = close + 1;
        int before = open - 1;
        // A lambda's group, and delegate's. An expression-bodied member's
        // group (int F(int a) => a) is read the same way and also recorded
        // as its signature.
        if (Is(after, "=>") || Is(before, "delegate"))
        {
            var lambdaNames = new List<string>();
            Items(open, close, lambda: true, lambdaNames);
            if (IsIdent(before) && (TypeEnd(before - 1) || (before - 1 >= 0 && t[before - 1].Kind == Tk.Keyword && s_memberKeywords.Contains(t[before - 1].Text))))
                Signatures.Add((t[before].Text, lambdaNames.ToArray(), Body(after).First, Body(after).Last));
            return;
        }
        // var (a, b) deconstruction.
        if (IsIdent(before) && t[before].Text == "var")
        {
            Items(open, close, lambda: true);
            return;
        }
        // A declared name's group: Name( or Name<T>(.
        int name = before;
        if (Is(name, ">"))
        {
            int a = AngleOpen(name);
            if (a < 1) return;
            name = a - 1;
        }
        if (!IsIdent(name) && !(name >= 0 && t[name].Kind == Tk.Punct && Is(name - 1, "operator"))) return;
        int prev = name - 1;
        if (t[name].Kind == Tk.Punct) prev = name - 2;
        bool declared = TypeEnd(prev) || (prev >= 0 && t[prev].Kind == Tk.Keyword && s_memberKeywords.Contains(t[prev].Text))
                        || (prev >= 0 && IsIdent(prev) && t[prev].Text is "async" or "partial");
        if (!declared) return;
        bool follows = Is(after, "{") || Is(after, "=>") || Is(after, ";") || Is(after, ":") || Is(after, "const")
                       || Is(after, "override") || Is(after, "noexcept") || (IsIdent(after) && t[after].Text is "where" or "override" or "final");
        if (!follows) return;
        var names = new List<string>();
        Items(open, close, lambda: false, names);
        if (t[name].Kind == Tk.Ident) Signatures.Add((t[name].Text, names.ToArray(), Body(after).First, Body(after).Last));
    }

    // A function's body after its parameter list: the braces, or => to the
    // statement's end. None for a prototype.
    (Token? First, Token? Last) Body(int after)
    {
        if (Is(after, "{") && Close(after) > after) return (t[after], t[Close(after)]);
        if (!Is(after, "=>")) return (null, null);
        int q = after + 1;
        while (q < t.Count && !Is(q, ";")) q = IsOpenGroup(q) ? Close(q) + 1 : q + 1;
        return q < t.Count && q > after + 1 ? (t[after + 1], t[q]) : (null, null);
    }

    void Items(int open, int close, bool lambda, List<string>? names = null)
    {
        int start = open + 1, angle = 0, k = start;
        while (k <= close)
        {
            if (k == close) { Item(start, k, lambda, names); break; }
            if (Is(k, "<") && (IsIdent(k - 1) || IsTypeWord(k - 1))) angle++;
            else if (Is(k, ">") && angle > 0) angle--;
            else if (angle == 0 && Is(k, ",")) { Item(start, k, lambda, names); start = k + 1; }
            k = IsOpenGroup(k) ? Close(k) + 1 : k + 1;
        }
    }

    bool IsOpenGroup(int k) => k < t.Count && t[k].Kind == Tk.Punct && t[k].Text is "(" or "[" or "{" && Close(k) > 0;

    void Item(int start, int end, bool lambda, List<string>? names = null)
    {
        if (names != null && start == end) return;
        // ( * Name ) inside the item: a function pointer parameter.
        for (int q = start; q < end; q++)
            if (Is(q, "(") && Is(q + 1, "*") && IsName(q + 2) && Is(q + 3, ")")) { Add(q + 2, "parameter"); names?.Add(t[q + 2].Text); return; }
        int last = -1, count = 0;
        for (int q = start; q < end; q = IsOpenGroup(q) ? Close(q) + 1 : q + 1)
        {
            if (Is(q, "=")) break;
            count++;
            if (IsName(q) && !(Is(q - 1, ".") || Is(q - 1, "::"))) last = q;
        }
        if (last < 0 || (!lambda && count == 1))
        {
            // In a declaration a lone identifier is a type (void f(ULONG)), in a lambda the name.
            names?.Add("");
            return;
        }
        Add(last, "parameter");
        names?.Add(t[last].Text);
    }

    // ---- locals ----

    void Local(int k)
    {
        if (!IsName(k)) return;
        int f = k + 1;
        int p = k - 1;
        if (IsIdent(p) && t[p].Text is "from" or "let" or "join" or "into") { Add(k, "local"); return; }
        // A lambda's single parameter, or a declaration pattern before an arm.
        if (Is(f, "=>"))
        {
            if (Is(p, ".") || Is(p, "?.") || Is(p, "->") || Is(p, "::")) return;
            Add(k, TypeEnd(p) ? "local" : "parameter");
            return;
        }
        if (!TypeEnd(p)) return;
        int s = TypeStart(p, out bool pointer);
        if (s < 0) return;
        int c = s - 1;
        bool pattern = c >= 0 && ((t[c].Kind == Tk.Keyword && s_patternContext.Contains(t[c].Text))
                                  || (IsIdent(c) && s_patternContext.Contains(t[c].Text)));
        bool statement = c < 0 || Is(c, ";") || Is(c, "{") || Is(c, "}") || (Is(c, "(") && (Is(c - 1, "for") || Is(c - 1, "fixed") || Is(c - 1, "using") || Is(c - 1, "foreach")))
                         || (c >= 0 && t[c].Kind == Tk.Keyword && s_declKeywords.Contains(t[c].Text));
        bool context = statement || pattern || Is(c, "(") || Is(c, ",") || Is(c, ":") || Is(c, "]")
                       || (IsIdent(c) && t[c].Text is "scoped" or "await");
        if (!context) return;
        if (pointer && !statement) return;
        bool follower = Is(f, "=") || Is(f, ";") || Is(f, ",") || Is(f, ")") || Is(f, "[") || Is(f, "in");
        if (!follower && !(pattern && !(Is(f, ".") || Is(f, "(") || Is(f, "<") || Is(f, "::") || Is(f, "->") || Is(f, "?.")))) return;
        if (Is(f, ")") && !(Is(c, "(") || Is(c, ",") || pattern)) return;
        Add(k, "local");
        if (statement && (Is(f, "=") || Is(f, ",") || Is(f, "[")))
            MoreDeclarators(k + 1, Is(c, "(") ? c : -1);
    }

    // int a = 1, *b, c[4]: the names after depth-0 commas up to the semicolon.
    void MoreDeclarators(int from, int paren)
    {
        int end = paren >= 0 && Close(paren) > 0 ? Close(paren) : t.Count;
        for (int q = from; q < end; q = IsOpenGroup(q) ? Close(q) + 1 : q + 1)
        {
            if (Is(q, ";")) return;
            if (!Is(q, ",")) continue;
            int n = q + 1;
            while (Is(n, "*") || Is(n, "&")) n++;
            if (IsName(n) && (Is(n + 1, "=") || Is(n + 1, ",") || Is(n + 1, ";") || Is(n + 1, "[") || Is(n + 1, ")")))
                Add(n, "local");
        }
    }

    // The token at p can end a type: an identifier, a type keyword, the >
    // of generic arguments, ?, [] or a pointer star.
    bool TypeEnd(int p)
    {
        if (p < 0) return false;
        if (IsTypeWord(p)) return !(t[p].Kind == Tk.Keyword && t[p].Text is "struct" or "union" or "enum" or "const" or "volatile");
        if (Is(p, ">")) return AngleOpen(p) > 0 && IsTypeWord(AngleOpen(p) - 1);
        if (Is(p, "?")) return TypeEnd(p - 1);
        if (Is(p, "]")) { int o = m[p]; return o >= 0 && o < p && OnlyCommas(o, p) && TypeEnd(o - 1); }
        if (Is(p, "*") || Is(p, "&")) return TypeEnd(p - 1) || Is(p - 1, "*");
        return false;
    }

    bool OnlyCommas(int o, int c)
    {
        for (int q = o + 1; q < c; q++) if (!Is(q, ",")) return false;
        return true;
    }

    // Back from the end of a type to its first token.
    int TypeStart(int p, out bool pointer)
    {
        pointer = false;
        int q = p;
        bool needWord = true;
        while (q >= 0)
        {
            if (Is(q, "*") || Is(q, "&")) { pointer = true; q--; continue; }
            if (Is(q, "?")) { q--; continue; }
            if (Is(q, "]")) { int o = m[q]; if (o < 0 || o >= q || !OnlyCommas(o, q)) return -1; q = o - 1; continue; }
            if (Is(q, ">")) { int a = AngleOpen(q); if (a < 1) return -1; q = a - 1; continue; }
            if (IsTypeWord(q))
            {
                needWord = false;
                if (Is(q - 1, ".") || Is(q - 1, "::")) { q -= 2; continue; }
                // Type keywords and qualifiers before it: unsigned long, const T, struct tag.
                if (q - 1 >= 0 && t[q - 1].Kind == Tk.Keyword && s_typeKeywords.Contains(t[q - 1].Text)) { q--; continue; }
                return q;
            }
            break;
        }
        return needWord ? -1 : q + 1;
    }

    // The < that matches the > at close, when everything between can be
    // generic arguments.
    int AngleOpen(int close)
    {
        int d = 0;
        for (int q = close; q >= 0; q--)
        {
            if (Is(q, ">")) { d++; continue; }
            if (Is(q, "<")) { if (--d == 0) return q; continue; }
            if (IsIdent(q) || t[q].Kind == Tk.Keyword || Is(q, ",") || Is(q, ".") || Is(q, "?") || Is(q, "[") || Is(q, "]")
                || Is(q, "(") || Is(q, ")") || Is(q, "*") || Is(q, "::"))
                continue;
            return -1;
        }
        return -1;
    }
}
