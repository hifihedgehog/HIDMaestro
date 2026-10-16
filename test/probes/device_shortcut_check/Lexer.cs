// The tokenizer for C#, C and C++ sources. Comments are dropped, every
// string and character literal becomes one token whose Content is the text between its
// delimiters, so no check reads code inside a string or a string as code.
//
// C#: regular, verbatim (@""), interpolated ($"", $@"", @$""), raw ("""...""")
// and interpolated raw ($$"""...{{x}}...""") strings, the u8 suffix,
// @identifiers. An interpolated string gives one String token per text
// segment, and its holes are lexed as code (a format specifier after a
// depth-0 colon is text). Directive lines (#if, #region, #pragma) are skipped.
//
// C and C++: L, u, U and u8 prefixes, R"delim(...)delim" raw strings, digit
// separators (1'000). #define, #undef, #if, #elif, #ifdef and #ifndef lines
// are lexed as code between a DirStart and a DirEnd token, honoring
// backslash continuations. Other directives (#include, #pragma, #error) are
// skipped.
using System;
using System.Collections.Generic;
using System.Globalization;

internal enum Lang { CSharp, C }

internal enum Tk { Ident, Keyword, Number, String, Char, Punct, DirStart, DirEnd }

internal sealed class Token
{
    public Tk Kind;
    public string Text = "";
    public int Line;
    /// <summary>String: the text between the delimiters, escapes as written.</summary>
    public string Content = "";
    /// <summary>Number: an integer literal, its value, and whether it is
    /// written in hex.</summary>
    public bool IsInt, IsHex;
    public ulong Value;
    /// <summary>Hex digits written, leading zeros included.</summary>
    public int HexDigits;
    /// <summary>Offsets of the token's first character and of the one after
    /// it, so a function-like macro's "(" can be told from a space.</summary>
    public int Start, End;
    /// <summary>C#: the interpolated string this token belongs to, a text
    /// segment or a token of a hole, numbered from 1, and the hole it is in,
    /// numbered from 1 (0 for a text segment). Both 0 outside one.</summary>
    public int Interp, Hole;
    public override string ToString() => Kind == Tk.String ? "\"" + Content + "\"" : Text;
}

internal sealed class Lexer
{
    static readonly HashSet<string> s_csKeywords = new(StringComparer.Ordinal)
    {
        "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const",
        "continue","decimal","default","delegate","do","double","else","enum","event","explicit","extern",
        "false","finally","fixed","float","for","foreach","goto","if","implicit","in","int","interface",
        "internal","is","lock","long","namespace","new","null","object","operator","out","override",
        "params","private","protected","public","readonly","ref","return","sbyte","sealed","short",
        "sizeof","stackalloc","static","string","struct","switch","this","throw","true","try","typeof",
        "uint","ulong","unchecked","unsafe","ushort","using","virtual","void","volatile","while",
    };
    static readonly HashSet<string> s_cKeywords = new(StringComparer.Ordinal)
    {
        "auto","break","case","char","const","continue","default","do","double","else","enum","extern",
        "float","for","goto","if","inline","int","long","register","restrict","return","short","signed",
        "sizeof","static","struct","switch","typedef","union","unsigned","void","volatile","while",
        "_Bool","bool","class","namespace","template","typename","public","private","protected",
        "virtual","override","operator","new","delete","this","true","false","nullptr","using",
        "constexpr","static_assert","static_cast","reinterpret_cast","const_cast","dynamic_cast",
        "try","catch","throw","noexcept","friend","explicit","mutable","decltype","final",
    };
    static readonly string[] s_punct3 = { "<<=", "??=", "...", "->*" };
    static readonly string[] s_punct2 =
    {
        "==","!=","<=",">=","&&","||","=>","->","::","++","--","+=","-=","*=","/=","%=","&=","|=","^=",
        "<<","??","..","##",
    };

    readonly string s;
    readonly Lang lang;
    int p, line = 1, tokenStart, interp, interpCount, hole, holeCount;
    bool atLineStart = true, inDirective;
    public readonly List<Token> Tokens = new();

    public Lexer(string source, Lang lang)
    {
        s = source;
        this.lang = lang;
    }

    public static bool IsCsKeyword(string w) => s_csKeywords.Contains(w);

    char At(int i) => i < s.Length ? s[i] : '\0';

    public Lexer Run()
    {
        while (true)
        {
            SkipTrivia();
            if (p >= s.Length) break;
            LexToken();
        }
        if (inDirective) Emit(Tk.DirEnd, "#end", line);
        return this;
    }

    void Emit(Tk kind, string text, int ln, string content = "")
    {
        Tokens.Add(new Token { Kind = kind, Text = text, Line = ln, Content = content, Start = tokenStart, End = p, Interp = interp, Hole = hole });
        atLineStart = false;
    }

    void Newline()
    {
        line++;
        atLineStart = true;
        if (inDirective)
        {
            Emit(Tk.DirEnd, "#end", line - 1);
            inDirective = false;
            atLineStart = true;
        }
    }

    void SkipTrivia()
    {
        while (p < s.Length)
        {
            char c = s[p];
            if (c == '\n') { p++; Newline(); continue; }
            if (c == ' ' || c == '\t' || c == '\r' || c == '\f' || c == '\v' || c == '\uFEFF') { p++; continue; }
            if (c == '\\' && lang == Lang.C && (At(p + 1) == '\n' || (At(p + 1) == '\r' && At(p + 2) == '\n')))
            {
                // A continuation joins the next line to this one: no directive end.
                p += At(p + 1) == '\n' ? 2 : 3;
                line++;
                continue;
            }
            if (c == '/' && At(p + 1) == '/')
            {
                int e = s.IndexOf('\n', p);
                if (e < 0) e = s.Length;
                p = e;
                continue;
            }
            if (c == '/' && At(p + 1) == '*')
            {
                int e = s.IndexOf("*/", p + 2, StringComparison.Ordinal);
                e = e < 0 ? s.Length : e + 2;
                for (int i = p; i < e; i++) if (s[i] == '\n') line++;
                p = e;
                continue;
            }
            if (c == '#' && atLineStart && !inDirective)
            {
                Directive();
                continue;
            }
            break;
        }
    }

    void Directive()
    {
        int q = p + 1;
        while (At(q) == ' ' || At(q) == '\t') q++;
        int n0 = q;
        while (char.IsLetter(At(q))) q++;
        string name = s.Substring(n0, q - n0);
        bool lexed = lang == Lang.C && name is "define" or "undef" or "if" or "elif" or "ifdef" or "ifndef";
        if (lexed)
        {
            tokenStart = p;
            Emit(Tk.DirStart, name, line);
            inDirective = true;
            p = q;
            return;
        }
        // Skip to the end of the line, following C continuations.
        while (p < s.Length && s[p] != '\n')
        {
            if (lang == Lang.C && s[p] == '\\' && (At(p + 1) == '\n' || (At(p + 1) == '\r' && At(p + 2) == '\n')))
            {
                p += At(p + 1) == '\n' ? 2 : 3;
                line++;
                continue;
            }
            p++;
        }
    }

    static bool IdStart(char c) => c == '_' || char.IsLetter(c);
    static bool IdPart(char c) => c == '_' || char.IsLetterOrDigit(c);

    void LexToken()
    {
        char c = s[p];
        int ln = line;
        tokenStart = p;

        if (lang == Lang.CSharp)
        {
            if (c == '$' || (c == '@' && At(p + 1) == '$'))
            {
                CsInterpolated();
                return;
            }
            if (c == '@' && At(p + 1) == '"')
            {
                p++;
                CsVerbatim(ln);
                return;
            }
            if (c == '@' && IdStart(At(p + 1)))
            {
                int b = ++p;
                while (IdPart(At(p))) p++;
                Emit(Tk.Ident, s.Substring(b, p - b), ln);
                return;
            }
            if (c == '"' && At(p + 1) == '"' && At(p + 2) == '"')
            {
                CsRaw(ln, 0);
                return;
            }
        }

        if (IdStart(c))
        {
            int b = p;
            while (IdPart(At(p))) p++;
            string w = s.Substring(b, p - b);
            if (lang == Lang.C && At(p) == '"' && w is "L" or "u" or "U" or "u8")
            {
                RegularString(ln);
                return;
            }
            if (lang == Lang.C && At(p) == '"' && w is "R" or "LR" or "uR" or "UR" or "u8R")
            {
                CppRaw(ln);
                return;
            }
            if (lang == Lang.C && At(p) == '\'' && w is "L" or "u" or "U" or "u8")
            {
                CharLiteral(ln);
                return;
            }
            bool kw = lang == Lang.CSharp ? s_csKeywords.Contains(w) : s_cKeywords.Contains(w);
            Emit(kw ? Tk.Keyword : Tk.Ident, w, ln);
            return;
        }
        if (char.IsDigit(c) || (c == '.' && char.IsDigit(At(p + 1))))
        {
            Number(ln);
            return;
        }
        if (c == '"')
        {
            RegularString(ln);
            return;
        }
        if (c == '\'')
        {
            CharLiteral(ln);
            return;
        }
        foreach (string op in s_punct3)
            if (string.CompareOrdinal(s, p, op, 0, 3) == 0) { p += 3; Emit(Tk.Punct, op, ln); return; }
        foreach (string op in s_punct2)
            if (string.CompareOrdinal(s, p, op, 0, 2) == 0)
            {
                p += 2;
                Emit(Tk.Punct, op, ln);
                return;
            }
        if (c == '?' && At(p + 1) == '.' && !char.IsDigit(At(p + 2)))
        {
            p += 2;
            Emit(Tk.Punct, "?.", ln);
            return;
        }
        p++;
        Emit(Tk.Punct, c.ToString(), ln);
    }

    void Number(int ln)
    {
        int b = p;
        bool hex = false, bin = false, real = false;
        if (s[p] == '0' && (At(p + 1) == 'x' || At(p + 1) == 'X')) { hex = true; p += 2; }
        else if (s[p] == '0' && (At(p + 1) == 'b' || At(p + 1) == 'B') && (At(p + 2) == '0' || At(p + 2) == '1')) { bin = true; p += 2; }
        int digitsStart = p;
        while (true)
        {
            char c = At(p);
            if (hex ? Uri.IsHexDigit(c) : char.IsDigit(c)) { p++; continue; }
            if (c == '_' ) { p++; continue; }
            if (c == '\'' && lang == Lang.C && Uri.IsHexDigit(At(p + 1))) { p++; continue; }
            if (!hex && !bin && c == '.' && char.IsDigit(At(p + 1))) { real = true; p++; continue; }
            if (!hex && !bin && (c == 'e' || c == 'E') && (char.IsDigit(At(p + 1)) || ((At(p + 1) == '+' || At(p + 1) == '-') && char.IsDigit(At(p + 2)))))
            {
                real = true;
                p += char.IsDigit(At(p + 1)) ? 1 : 2;
                continue;
            }
            break;
        }
        string digits = s.Substring(digitsStart, p - digitsStart).Replace("_", "").Replace("'", "");
        int suffixStart = p;
        while (IdPart(At(p))) p++;
        string suffix = s.Substring(suffixStart, p - suffixStart);
        var t = new Token { Kind = Tk.Number, Text = s.Substring(b, p - b), Line = ln, Start = b, End = p, Interp = interp, Hole = hole };
        bool intSuffix = suffix.Length == 0 || suffix.ToLowerInvariant() is "u" or "l" or "ul" or "lu" or "ll" or "ull" or "llu" or "i64" or "ui64";
        if (!real && intSuffix && digits.Length > 0)
        {
            if (hex && ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hv))
            {
                t.IsInt = true; t.IsHex = true; t.Value = hv; t.HexDigits = digits.Length;
            }
            else if (bin)
            {
                try { t.Value = Convert.ToUInt64(digits, 2); t.IsInt = true; } catch (FormatException) { } catch (OverflowException) { }
            }
            else if (!hex && ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out ulong dv))
            {
                t.IsInt = true; t.Value = dv;
            }
        }
        Tokens.Add(t);
        atLineStart = false;
    }

    void RegularString(int ln)
    {
        int b = ++p;
        while (p < s.Length && s[p] != '"' && s[p] != '\n')
        {
            if (s[p] == '\\')
            {
                if (At(p + 1) == '\n') line++;
                p += 2;
                continue;
            }
            p++;
        }
        string content = s.Substring(b, Math.Min(p, s.Length) - b);
        if (At(p) == '"') p++;
        if (lang == Lang.CSharp && At(p) == 'u' && At(p + 1) == '8' && !IdPart(At(p + 2))) p += 2;
        Emit(Tk.String, "\"" + content + "\"", ln, content);
    }

    void CharLiteral(int ln)
    {
        int b = ++p;
        while (p < s.Length && s[p] != '\'' && s[p] != '\n')
        {
            p += s[p] == '\\' ? 2 : 1;
        }
        string content = s.Substring(b, Math.Min(p, s.Length) - b);
        if (At(p) == '\'') p++;
        Emit(Tk.Char, "'" + content + "'", ln, content);
    }

    void CppRaw(int ln)
    {
        // R"delim( ... )delim"
        int q = p + 1;
        int open = s.IndexOf('(', q);
        string delim = open < 0 ? "" : s.Substring(q, open - q);
        string close = ")" + delim + "\"";
        int e = open < 0 ? -1 : s.IndexOf(close, open + 1, StringComparison.Ordinal);
        if (e < 0) e = s.Length;
        string content = open < 0 ? "" : s.Substring(open + 1, Math.Max(0, e - open - 1));
        foreach (char ch in content) if (ch == '\n') line++;
        p = Math.Min(s.Length, e + close.Length);
        Emit(Tk.String, "R\"" + content + "\"", ln, content);
    }

    void CsVerbatim(int ln)
    {
        // At the opening quote of @"...".
        int b = ++p;
        var sb = new System.Text.StringBuilder();
        while (p < s.Length)
        {
            if (s[p] == '"')
            {
                if (At(p + 1) == '"') { sb.Append('"'); p += 2; continue; }
                break;
            }
            if (s[p] == '\n') line++;
            sb.Append(s[p]);
            p++;
        }
        if (At(p) == '"') p++;
        if (At(p) == 'u' && At(p + 1) == '8' && !IdPart(At(p + 2))) p += 2;
        Emit(Tk.String, "@\"" + sb + "\"", ln, sb.ToString());
    }

    void CsRaw(int ln, int dollars)
    {
        int q = 0;
        while (At(p + q) == '"') q++;
        p += q;
        if (dollars == 0)
        {
            int b = p;
            int e = FindQuoteRun(q);
            string content = s.Substring(b, e - b);
            foreach (char ch in content) if (ch == '\n') line++;
            p = Math.Min(s.Length, e + q);
            if (At(p) == 'u' && At(p + 1) == '8' && !IdPart(At(p + 2))) p += 2;
            Emit(Tk.String, "\"\"\"" + content + "\"\"\"", ln, content);
            return;
        }
        Interpolated(ln, raw: true, verbatim: false, quotes: q, braces: dollars);
    }

    int FindQuoteRun(int q)
    {
        int i = p;
        while (i < s.Length)
        {
            if (s[i] == '"')
            {
                int r = 0;
                while (At(i + r) == '"') r++;
                if (r >= q) return i;
                i += r;
                continue;
            }
            i++;
        }
        return s.Length;
    }

    void CsInterpolated()
    {
        int ln = line;
        bool verbatim = false;
        if (s[p] == '@') { verbatim = true; p++; }
        int dollars = 0;
        while (At(p) == '$') { dollars++; p++; }
        if (At(p) == '@') { verbatim = true; p++; }
        if (At(p) != '"')
        {
            Emit(Tk.Punct, "$", ln);
            return;
        }
        if (!verbatim && At(p + 1) == '"' && At(p + 2) == '"')
        {
            CsRaw(ln, dollars);
            return;
        }
        p++;
        Interpolated(ln, raw: false, verbatim: verbatim, quotes: 1, braces: 1);
    }

    // At the first character of the content. Emits one String token per text
    // segment and lexes each hole as code.
    void Interpolated(int ln, bool raw, bool verbatim, int quotes, int braces)
    {
        int outer = interp, outerHole = hole;
        interp = ++interpCount;
        hole = 0;
        try { InterpolatedBody(ln, raw, verbatim, quotes, braces); }
        finally { interp = outer; hole = outerHole; }
    }

    void InterpolatedBody(int ln, bool raw, bool verbatim, int quotes, int braces)
    {
        var seg = new System.Text.StringBuilder();
        int segLine = ln;
        void Flush()
        {
            if (seg.Length > 0) Emit(Tk.String, "$\"" + seg + "\"", segLine, seg.ToString());
            seg.Clear();
        }
        while (p < s.Length)
        {
            char c = s[p];
            if (c == '"')
            {
                if (raw)
                {
                    int r = 0;
                    while (At(p + r) == '"') r++;
                    if (r >= quotes) { p += quotes; break; }
                    seg.Append('"', r);
                    p += r;
                    continue;
                }
                if (verbatim && At(p + 1) == '"') { seg.Append('"'); p += 2; continue; }
                p++;
                break;
            }
            if (!raw && !verbatim && c == '\\')
            {
                seg.Append(c).Append(At(p + 1));
                p += 2;
                continue;
            }
            if (!raw && !verbatim && c == '\n') break;
            if (c == '{')
            {
                int r = 0;
                while (At(p + r) == '{') r++;
                if (!raw)
                {
                    if (r >= 2) { seg.Append('{'); p += 2; continue; }
                }
                else if (r < braces)
                {
                    seg.Append('{', r);
                    p += r;
                    continue;
                }
                else
                {
                    seg.Append('{', r - braces);
                    p += r - braces;
                }
                Flush();
                p += braces;
                hole = ++holeCount;
                Hole(braces);
                hole = 0;
                segLine = line;
                continue;
            }
            if (c == '}' && !raw && At(p + 1) == '}') { seg.Append('}'); p += 2; continue; }
            if (c == '\n') line++;
            seg.Append(c);
            p++;
        }
        Flush();
    }

    void Hole(int braces)
    {
        int depth = 0;
        while (p < s.Length)
        {
            SkipTrivia();
            if (p >= s.Length) return;
            char c = s[p];
            if (depth == 0 && c == '}')
            {
                int r = 0;
                while (r < braces && At(p + r) == '}') r++;
                p += r;
                return;
            }
            if (depth == 0 && c == ':' && At(p + 1) != ':')
            {
                // A format specifier: text up to the closing brace.
                while (p < s.Length && s[p] != '}') { if (s[p] == '\n') line++; p++; }
                continue;
            }
            LexToken();
            var t = Tokens[^1];
            if (t.Kind == Tk.Punct)
            {
                if (t.Text is "(" or "[" or "{") depth++;
                else if (t.Text is ")" or "]" or "}") depth--;
            }
        }
    }
}
