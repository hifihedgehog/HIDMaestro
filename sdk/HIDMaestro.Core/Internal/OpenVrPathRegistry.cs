using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace HIDMaestro.Internal;

/// <summary>
/// Reads and writes the user's OpenVR path registry,
/// <c>%LOCALAPPDATA%\openvr\openvrpaths.vrpath</c> (or the file
/// <c>VR_PATHREG_OVERRIDE</c> names), the way SteamVR's own
/// <c>vrpathreg.exe adddriver</c> and <c>removedriver</c> do.
///
/// <para>Through v1.12.0 the SDK ran <c>vrpathreg.exe</c> from the SteamVR
/// folder, elevated. A Steam library grants Users full control, and the
/// steamcmd install at <c>C:\SteamVR</c> grants Authenticated Users modify,
/// so any unelevated program could replace <c>vrpathreg.exe</c>, or the
/// <c>openvr_api.dll</c> it loads from beside itself, and have HIDMaestro run
/// it elevated. With SteamVR running the tool also shell-executes
/// <c>vrmonitor://driverinstalled</c>, whose handler is
/// <c>vrmonitor.exe</c> in the same folder. Nothing from that folder runs
/// now. The SDK writes the file itself.</para>
///
/// <para>The format follows OpenVR's own code, byte for byte:
/// <c>CVRPathRegistry_Public::BLoadFromFile</c> and <c>BSaveToFile</c>
/// (openvr src/vrcore/vrpathregistry_public.cpp), <c>Path_ReadTextFile</c>
/// (src/vrcore/pathtools_public.cpp, which drops a UTF-8 BOM and folds CRLF
/// to LF), and the jsoncpp 1.6.5 it builds with (src/jsoncpp.cpp:
/// <c>OurReader</c> with <c>CharReaderBuilder</c>'s defaults, and
/// <c>BuiltStyledStreamWriter</c> with <c>StreamWriterBuilder</c>'s, whose
/// <c>commentStyle "All"</c> puts every array on its own lines). The file is
/// written in text mode, so every line ends CRLF, with no newline after the
/// closing brace. vr_driver_conflict_check holds the bytes the shipped
/// <c>vrpathreg.exe</c> wrote for each case and compares against them.</para>
///
/// <para>The one deliberate difference is the write itself. vrpathreg
/// truncates the file in place, through any link. The SDK writes a new file
/// beside it and moves it over the old one, and when the process is the
/// elevated half of a split token it does both while impersonating the same
/// user without administrator rights (<see cref="RunAsUser{T}"/>), so it can
/// change nothing in that folder the user could not change.</para>
/// </summary>
internal static partial class OpenVrPathRegistry
{
    /// <summary>The four lists BLoadFromFile keeps. Each entry holds the
    /// string's bytes as jsoncpp holds them, which need not be valid
    /// UTF-8: vrpathreg writes its ANSI command line as it came.</summary>
    internal sealed class Lists
    {
        internal readonly List<byte[]> Runtime = new();
        internal readonly List<byte[]> Config = new();
        internal readonly List<byte[]> Log = new();
        internal readonly List<byte[]> ExternalDrivers = new();
    }

    internal const string AddDriverVerb = "adddriver";
    internal const string RemoveDriverVerb = "removedriver";

    /// <summary><c>GetVRPathRegistryFilename</c>: the override when set,
    /// else <c>%LOCALAPPDATA%\openvr\openvrpaths.vrpath</c>.</summary>
    internal static string FilePath
    {
        get
        {
            string? file = Environment.GetEnvironmentVariable("VR_PATHREG_OVERRIDE");
            if (!string.IsNullOrEmpty(file)) return file;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "openvr", "openvrpaths.vrpath");
        }
    }

    /// <summary>The external_drivers entries SteamVR reads, decoded as
    /// UTF-8. Empty when the file is missing or unreadable. Reads only.</summary>
    internal static List<string> ExternalDrivers()
    {
        var list = new List<string>();
        byte[]? raw = ReadFile(FilePath);
        foreach (var e in Load(raw).ExternalDrivers)
            list.Add(Encoding.UTF8.GetString(e));
        return list;
    }

    /// <summary>Add <paramref name="driverPath"/> to external_drivers unless
    /// the exact string is there. Writes only when the list changes.</summary>
    internal static bool AddDriver(string driverPath) =>
        Mutate(l => Edit(l, AddDriverVerb, Encoding.UTF8.GetBytes(driverPath)));

    /// <summary>Remove every external_drivers entry equal to
    /// <paramref name="driverPath"/>: the file vrpathreg leaves when
    /// removedriver is run until nothing changes, since one run removes only
    /// the first. Writes only when the list changes.</summary>
    internal static bool RemoveDriver(string driverPath)
    {
        byte[] path = Encoding.UTF8.GetBytes(driverPath);
        return Mutate(l =>
        {
            bool any = false;
            while (Edit(l, RemoveDriverVerb, path)) any = true;
            return any;
        });
    }

    /// <summary>The bytes vrpathreg writes for <c>vrpathreg verb path</c>
    /// over a file holding <paramref name="contents"/> (null: no file). It
    /// writes even when nothing changed, so this always returns bytes.</summary>
    internal static byte[] Apply(byte[]? contents, string verb, string driverPath)
    {
        var l = Load(contents);
        Edit(l, verb, Encoding.UTF8.GetBytes(driverPath));
        return Save(l);
    }

    /// <summary>adddriver appends when no entry matches exactly, case and
    /// all. removedriver drops the first exact match only. Both measured
    /// against the shipped tool.</summary>
    private static bool Edit(Lists l, string verb, byte[] path)
    {
        var list = l.ExternalDrivers;
        if (verb == AddDriverVerb)
        {
            foreach (var e in list)
                if (e.AsSpan().SequenceEqual(path)) return false;
            list.Add(path);
            return true;
        }
        if (verb == RemoveDriverVerb)
        {
            int i = list.FindIndex(e => e.AsSpan().SequenceEqual(path));
            if (i < 0) return false;
            list.RemoveAt(i);
            return true;
        }
        throw new ArgumentOutOfRangeException(nameof(verb), verb, null);
    }

    private static bool Mutate(Func<Lists, bool> edit) => RunAsUser(() =>
    {
        string file = Path.GetFullPath(FilePath);
        var l = Load(ReadFile(file, missingOnly: true));
        if (!edit(l)) return false;
        byte[] bytes = Save(l);

        // BSaveToFile creates the directory first (BCreateDirectoryRecursive).
        string dir = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, Path.GetFileName(file) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, file, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        return true;
    });

    /// <summary>Path_ReadBinaryFile: null when the file is missing, cannot
    /// be opened, or is empty. Before a write, only a missing file reads as
    /// none: vrpathreg would also write over one it could not open, and lose
    /// the runtime, config and log paths in it, so the SDK stops there.</summary>
    private static byte[]? ReadFile(string file, bool missingOnly = false)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.Length > 0 ? ms.ToArray() : null;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException ||
                                  (!missingOnly && e is IOException or UnauthorizedAccessException))
        {
            return null;
        }
    }

    // ── Load: BLoadFromFile ──────────────────────────────────────────────

    /// <summary>BLoadFromFile's result. A file that does not parse leaves
    /// every list empty, as vrpathreg does before it writes over it. A
    /// jsoncpp logic error part way (a list given as a non-empty object, or
    /// a root that is not an object) keeps the lists already read, because
    /// BLoadFromFile fills the members in order and the exception only ends
    /// the load.</summary>
    internal static Lists Load(byte[]? raw)
    {
        var l = new Lists();
        if (raw == null || raw.Length == 0) return l;
        byte[] text = ReadTextFile(raw);
        JValue? root;
        try { root = Reader.Parse(text); }
        catch (ReaderException) { root = null; }
        if (root == null) return l;

        // isMember throws on any root but null or object, before any list.
        if (root.Kind != Kind.Object) return l;
        if (!FillList(l.Runtime, root, "runtime")) return l;
        if (!FillList(l.Config, root, "config")) return l;
        if (!FillList(l.Log, root, "log")) return l;
        if (root.O!.TryGetValue("external_drivers", out var ext) && ext.Kind == Kind.Array)
            FillList(l.ExternalDrivers, root, "external_drivers");
        return l;
    }

    /// <summary>ParseStringListFromJson. False where jsoncpp throws.</summary>
    private static bool FillList(List<byte[]> list, JValue root, string name)
    {
        if (!root.O!.TryGetValue(name, out var node)) return true;
        if (node.Kind == Kind.Null) return true;   // "not an array", list kept
        list.Clear();
        switch (node.Kind)
        {
            case Kind.Array:
                foreach (var e in node.A!) list.Add(AsString(e));
                return true;
            case Kind.Object:
                // size() counts members, and operator[](index) on an object
                // is a logic error.
                return node.O!.Count == 0;
            default:
                return true;   // size() is 0
        }
    }

    /// <summary>Value::asString in OpenVR's jsoncpp: arrays and objects
    /// give an empty string rather than throwing.</summary>
    private static byte[] AsString(JValue v) => v.Kind switch
    {
        Kind.String => v.S!,
        Kind.Bool => Encoding.ASCII.GetBytes(v.B ? "true" : "false"),
        Kind.Int => Encoding.ASCII.GetBytes(v.I.ToString(CultureInfo.InvariantCulture)),
        Kind.UInt => Encoding.ASCII.GetBytes(v.U.ToString(CultureInfo.InvariantCulture)),
        Kind.Real => Encoding.ASCII.GetBytes(FormatDouble(v.D)),
        _ => Array.Empty<byte>(),
    };

    /// <summary>valueToString(double): <c>%.17g</c>, and the two
    /// infinities as <c>1e+9999</c> and <c>-1e+9999</c>. .NET's G17 picks
    /// fixed or exponent form by the same rule and writes the exponent with
    /// at least two digits, as C does.</summary>
    private static string FormatDouble(double d)
    {
        if (double.IsNaN(d)) return "null";
        if (double.IsPositiveInfinity(d)) return "1e+9999";
        if (double.IsNegativeInfinity(d)) return "-1e+9999";
        return d.ToString("G17", CultureInfo.InvariantCulture).Replace('E', 'e');
    }

    /// <summary>Path_ReadTextFile: drop a UTF-8 BOM and fold CRLF to LF, in
    /// place, with its loop as written.</summary>
    private static byte[] ReadTextFile(byte[] raw)
    {
        byte[] buf = (byte[])raw.Clone();
        int size = buf.Length;
        int i = 1;
        int outsize = 1;
        if (size >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF)
        {
            i = 3;
            outsize = 0;
        }
        for (; i < size; i++)
        {
            if (buf[i] == (byte)'\n' && buf[i - 1] == (byte)'\r')
                buf[outsize - 1] = (byte)'\n';
            else
                buf[outsize++] = buf[i];
        }
        return buf.AsSpan(0, outsize).ToArray();
    }

    // ── Save: BSaveToFile ────────────────────────────────────────────────

    /// <summary>The bytes BSaveToFile writes. Members in jsoncpp's sorted
    /// order, an empty list as <c>null</c>, tab indentation, CRLF line ends
    /// from the text-mode write, no newline at the end.</summary>
    internal static byte[] Save(Lists l)
    {
        var o = new List<byte>(256);
        void Ascii(string s) { foreach (char c in s) o.Add((byte)c); }
        void Value(List<byte[]> list)
        {
            if (list.Count == 0) { Ascii("null"); return; }
            Ascii("\r\n\t[");
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) Ascii(",");
                Ascii("\r\n\t\t");
                Quote(o, list[i]);
            }
            Ascii("\r\n\t]");
        }

        Ascii("{\r\n\t\"config\" : "); Value(l.Config);
        Ascii(",\r\n\t\"external_drivers\" : "); Value(l.ExternalDrivers);
        Ascii(",\r\n\t\"jsonid\" : \"vrpathreg\"");
        Ascii(",\r\n\t\"log\" : "); Value(l.Log);
        Ascii(",\r\n\t\"runtime\" : "); Value(l.Runtime);
        Ascii(",\r\n\t\"version\" : 1\r\n}");
        return o.ToArray();
    }

    /// <summary>valueToQuotedStringN. A byte of 0x80 or above is a negative
    /// char there, so only NUL and 0x01-0x1F are escaped as \uXXXX, in
    /// upper-case hex.</summary>
    private static void Quote(List<byte> o, byte[] s)
    {
        o.Add((byte)'"');
        foreach (byte b in s)
        {
            switch (b)
            {
                case (byte)'"': o.Add((byte)'\\'); o.Add((byte)'"'); break;
                case (byte)'\\': o.Add((byte)'\\'); o.Add((byte)'\\'); break;
                case 0x08: o.Add((byte)'\\'); o.Add((byte)'b'); break;
                case 0x0C: o.Add((byte)'\\'); o.Add((byte)'f'); break;
                case 0x0A: o.Add((byte)'\\'); o.Add((byte)'n'); break;
                case 0x0D: o.Add((byte)'\\'); o.Add((byte)'r'); break;
                case 0x09: o.Add((byte)'\\'); o.Add((byte)'t'); break;
                default:
                    if (b <= 0x1F)
                        foreach (char c in "\\u" + b.ToString("X4", CultureInfo.InvariantCulture)) o.Add((byte)c);
                    else
                        o.Add(b);
                    break;
            }
        }
        o.Add((byte)'"');
    }

    // ── jsoncpp OurReader, CharReaderBuilder defaults ────────────────────
    // allowComments true, strictRoot false, allowDroppedNullPlaceholders
    // false, allowNumericKeys false, allowSingleQuotes false, stackLimit
    // 1000, failIfExtra false, rejectDupKeys false, allowSpecialFloats
    // false (src/jsoncpp.cpp CharReaderBuilder::setDefaults).

    private enum Kind : byte { Null, Int, UInt, Real, String, Bool, Array, Object }

    private sealed class JValue
    {
        public Kind Kind;
        public long I;
        public ulong U;
        public double D;
        public bool B;
        public byte[]? S;
        public List<JValue>? A;
        // Keys are the name's bytes, one char per byte, so any byte string
        // is a distinct key and a later duplicate replaces the earlier one.
        public Dictionary<string, JValue>? O;
    }

    /// <summary>Where jsoncpp throws inside the parse: the stack limit, and
    /// Value::setComment's "Comments must start with /".</summary>
    private sealed class ReaderException : Exception { }

    private enum Tok : byte
    {
        ObjectBegin, ObjectEnd, ArrayBegin, ArrayEnd, String, Number, True, False,
        Null, ArraySeparator, MemberSeparator, Comment, EndOfStream, Error,
    }

    private readonly struct Token
    {
        public readonly Tok Type; public readonly int Start; public readonly int End;
        public Token(Tok type, int start, int end) { Type = type; Start = start; End = end; }
    }

    private sealed class Reader
    {
        private const int StackLimit = 1000;
        private readonly byte[] _d;
        private readonly int _end;
        private int _cur;
        private int _depth;
        // Comment collection (collectComments true). Only the first byte of
        // a stored comment matters: setComment throws unless it is '/', and
        // a comment opened by a single quote starts with one.
        private byte _commentsBeforeFirst;   // 0 while commentsBefore_ is empty
        private int _lastValueEnd = -1;      // lastValueEnd_, -1 while null

        private Reader(byte[] d) { _d = d; _end = d.Length; }

        /// <summary>OurReader::parse. Null when it returns false. Throws
        /// <see cref="ReaderException"/> where jsoncpp throws.</summary>
        public static JValue? Parse(byte[] doc)
        {
            var r = new Reader(doc);
            var root = new JValue();
            bool ok = r.ReadValue(root);
            r.SkipCommentTokens();
            r.FlushCommentsBefore();
            return ok ? root : null;
        }

        private void FlushCommentsBefore()
        {
            if (_commentsBeforeFirst == 0) return;
            if (_commentsBeforeFirst != (byte)'/') throw new ReaderException();
            _commentsBeforeFirst = 0;
        }

        private byte Next() => _cur == _end ? (byte)0 : _d[_cur++];
        private byte Peek() => _cur == _end ? (byte)0 : _d[_cur];

        private bool ReadValue(JValue into)
        {
            if (_depth >= StackLimit) throw new ReaderException();
            ++_depth;
            Token t = SkipCommentTokens();
            FlushCommentsBefore();
            bool ok = true;
            switch (t.Type)
            {
                case Tok.ObjectBegin: ok = ReadObject(into); break;
                case Tok.ArrayBegin: ok = ReadArray(into); break;
                case Tok.Number: ok = DecodeNumber(t, into); break;
                case Tok.String:
                    ok = DecodeString(t, out byte[] s);
                    if (ok) { into.Kind = Kind.String; into.S = s; }
                    break;
                case Tok.True: into.Kind = Kind.Bool; into.B = true; break;
                case Tok.False: into.Kind = Kind.Bool; into.B = false; break;
                case Tok.Null: into.Kind = Kind.Null; break;
                default: return false;
            }
            _lastValueEnd = _cur;
            --_depth;
            return ok;
        }

        private Token SkipCommentTokens()
        {
            Token t;
            do { t = ReadToken(); } while (t.Type == Tok.Comment);
            return t;
        }

        private Token ReadToken()
        {
            SkipSpaces();
            int start = _cur;
            byte c = Next();
            bool ok = true;
            Tok type;
            switch (c)
            {
                case (byte)'{': type = Tok.ObjectBegin; break;
                case (byte)'}': type = Tok.ObjectEnd; break;
                case (byte)'[': type = Tok.ArrayBegin; break;
                case (byte)']': type = Tok.ArrayEnd; break;
                case (byte)'"': type = Tok.String; ok = ReadString(); break;
                // With single quotes off, jsoncpp's switch falls through
                // from '\'' into the comment case.
                case (byte)'\'':
                case (byte)'/': type = Tok.Comment; ok = ReadComment(); break;
                case >= (byte)'0' and <= (byte)'9': type = Tok.Number; ReadNumber(); break;
                case (byte)'-':
                    if (Peek() == (byte)'I') { _cur++; type = Tok.Error; ok = false; }
                    else { ReadNumber(); type = Tok.Number; }
                    break;
                case (byte)'t': type = Tok.True; ok = Match("rue"); break;
                case (byte)'f': type = Tok.False; ok = Match("alse"); break;
                case (byte)'n': type = Tok.Null; ok = Match("ull"); break;
                case (byte)',': type = Tok.ArraySeparator; break;
                case (byte)':': type = Tok.MemberSeparator; break;
                case 0: type = Tok.EndOfStream; break;
                default: type = Tok.Error; ok = false; break;
            }
            if (!ok) type = Tok.Error;
            return new Token(type, start, _cur);
        }

        private void SkipSpaces()
        {
            while (_cur != _end)
            {
                byte c = _d[_cur];
                if (c == (byte)' ' || c == (byte)'\t' || c == (byte)'\r' || c == (byte)'\n') ++_cur;
                else break;
            }
        }

        private bool Match(string pattern)
        {
            if (_end - _cur < pattern.Length) return false;
            for (int i = 0; i < pattern.Length; i++)
                if (_d[_cur + i] != (byte)pattern[i]) return false;
            _cur += pattern.Length;
            return true;
        }

        private bool ReadComment()
        {
            int begin = _cur - 1;
            byte c = Next();
            bool ok;
            if (c == (byte)'*') ok = ReadCStyleComment();
            else if (c == (byte)'/') ok = ReadCppStyleComment();
            else return false;
            if (!ok) return false;

            // OurReader::readComment and addComment: a comment on the same
            // line as the value before it is stored on that value at once,
            // any other is held for the next value.
            bool afterOnSameLine = _lastValueEnd >= 0 && !ContainsNewLine(_lastValueEnd, begin) &&
                                   (c != (byte)'*' || !ContainsNewLine(begin, _cur));
            if (afterOnSameLine)
            {
                if (_d[begin] != (byte)'/') throw new ReaderException();
            }
            else if (_commentsBeforeFirst == 0)
            {
                _commentsBeforeFirst = _d[begin];
            }
            return true;
        }

        private bool ContainsNewLine(int begin, int end)
        {
            for (; begin < end; ++begin)
                if (_d[begin] == 0x0A || _d[begin] == 0x0D) return true;
            return false;
        }

        private bool ReadCStyleComment()
        {
            while (_cur != _end)
            {
                byte c = Next();
                if (c == (byte)'*' && Peek() == (byte)'/') break;
            }
            return Next() == (byte)'/';
        }

        private bool ReadCppStyleComment()
        {
            while (_cur != _end)
            {
                byte c = Next();
                if (c == (byte)'\n') break;
                if (c == (byte)'\r')
                {
                    if (_cur != _end && _d[_cur] == (byte)'\n') Next();
                    break;
                }
            }
            return true;
        }

        /// <summary>OurReader::readNumber after its first character:
        /// digits, an optional fraction, an optional exponent, each part
        /// possibly empty. The token is checked when it is decoded.</summary>
        private void ReadNumber()
        {
            int p = _cur;
            byte c = (byte)'0';
            while (c >= (byte)'0' && c <= (byte)'9') { _cur = p; c = p < _end ? _d[p++] : (byte)0; }
            if (c == (byte)'.')
            {
                _cur = p; c = p < _end ? _d[p++] : (byte)0;
                while (c >= (byte)'0' && c <= (byte)'9') { _cur = p; c = p < _end ? _d[p++] : (byte)0; }
            }
            if (c == (byte)'e' || c == (byte)'E')
            {
                _cur = p; c = p < _end ? _d[p++] : (byte)0;
                if (c == (byte)'+' || c == (byte)'-') { _cur = p; c = p < _end ? _d[p++] : (byte)0; }
                while (c >= (byte)'0' && c <= (byte)'9') { _cur = p; c = p < _end ? _d[p++] : (byte)0; }
            }
        }

        private bool ReadString()
        {
            byte c = 0;
            while (_cur != _end)
            {
                c = Next();
                if (c == (byte)'\\') Next();
                else if (c == (byte)'"') break;
            }
            return c == (byte)'"';
        }

        private bool ReadObject(JValue into)
        {
            into.Kind = Kind.Object;
            into.O = new Dictionary<string, JValue>(StringComparer.Ordinal);
            string name = "";
            while (true)
            {
                Token tokenName = ReadToken();
                while (tokenName.Type == Tok.Comment) tokenName = ReadToken();
                // jsoncpp tests the previous member's name here, so a comma
                // after a member named "" may close the object.
                if (tokenName.Type == Tok.ObjectEnd && name.Length == 0) return true;
                name = "";
                if (tokenName.Type != Tok.String) break;
                if (!DecodeString(tokenName, out byte[] nameBytes)) return false;
                name = Encoding.Latin1.GetString(nameBytes);

                if (ReadToken().Type != Tok.MemberSeparator) return false;
                var value = new JValue();
                into.O[name] = value;
                if (!ReadValue(value)) return false;

                Token comma = ReadToken();
                if (comma.Type != Tok.ObjectEnd && comma.Type != Tok.ArraySeparator && comma.Type != Tok.Comment)
                    return false;
                while (comma.Type == Tok.Comment) comma = ReadToken();
                if (comma.Type == Tok.ObjectEnd) return true;
            }
            return false;
        }

        private bool ReadArray(JValue into)
        {
            into.Kind = Kind.Array;
            into.A = new List<JValue>();
            SkipSpaces();
            if (Peek() == (byte)']')
            {
                ReadToken();
                return true;
            }
            while (true)
            {
                var value = new JValue();
                into.A.Add(value);
                if (!ReadValue(value)) return false;
                Token t = ReadToken();
                while (t.Type == Tok.Comment) t = ReadToken();
                if (t.Type != Tok.ArraySeparator && t.Type != Tok.ArrayEnd) return false;
                if (t.Type == Tok.ArrayEnd) return true;
            }
        }

        /// <summary>OurReader::decodeNumber: an integer when every
        /// character is a digit and it fits, else a double.</summary>
        private bool DecodeNumber(Token t, JValue into)
        {
            int cur = t.Start;
            bool negative = _d[cur] == (byte)'-';
            if (negative) ++cur;
            ulong max = negative ? (ulong)long.MaxValue + 1 : ulong.MaxValue;
            ulong threshold = max / 10;
            ulong value = 0;
            while (cur < t.End)
            {
                byte c = _d[cur++];
                if (c < (byte)'0' || c > (byte)'9') return DecodeDouble(t, into);
                uint digit = (uint)(c - '0');
                if (value >= threshold &&
                    (value > threshold || cur != t.End || digit > max % 10))
                    return DecodeDouble(t, into);
                value = value * 10 + digit;
            }
            if (negative) { into.Kind = Kind.Int; into.I = unchecked(-(long)value); }
            else if (value <= int.MaxValue) { into.Kind = Kind.Int; into.I = (long)value; }
            else { into.Kind = Kind.UInt; into.U = value; }
            return true;
        }

        /// <summary>OurReader::decodeDouble, which reads the token with the
        /// MSVC CRT's <c>sscanf("%lf")</c>. Measured against the shipped
        /// tool: a token with a dangling exponent (<c>1e</c>, <c>1e+</c>)
        /// fails, out-of-range values become an infinity or zero.</summary>
        private bool DecodeDouble(Token t, JValue into)
        {
            string text = Encoding.Latin1.GetString(_d, t.Start, t.End - t.Start);
            if (!double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                                 CultureInfo.InvariantCulture, out double d))
                return false;
            into.Kind = Kind.Real;
            into.D = d;
            return true;
        }

        private bool DecodeString(Token t, out byte[] decoded)
        {
            var o = new List<byte>(Math.Max(0, t.End - t.Start - 2));
            decoded = Array.Empty<byte>();
            int cur = t.Start + 1;
            int end = t.End - 1;
            while (cur != end)
            {
                byte c = _d[cur++];
                if (c == (byte)'"') break;
                if (c != (byte)'\\') { o.Add(c); continue; }
                if (cur == end) return false;
                byte escape = _d[cur++];
                switch (escape)
                {
                    case (byte)'"': o.Add((byte)'"'); break;
                    case (byte)'/': o.Add((byte)'/'); break;
                    case (byte)'\\': o.Add((byte)'\\'); break;
                    case (byte)'b': o.Add(0x08); break;
                    case (byte)'f': o.Add(0x0C); break;
                    case (byte)'n': o.Add(0x0A); break;
                    case (byte)'r': o.Add(0x0D); break;
                    case (byte)'t': o.Add(0x09); break;
                    case (byte)'u':
                        if (!DecodeUnicodeCodePoint(ref cur, end, out uint cp)) return false;
                        AppendUtf8(o, cp);
                        break;
                    default: return false;
                }
            }
            decoded = o.ToArray();
            return true;
        }

        /// <summary>A high surrogate takes the next \u escape as its pair,
        /// unchecked. A lone low surrogate is encoded as is.</summary>
        private bool DecodeUnicodeCodePoint(ref int cur, int end, out uint unicode)
        {
            if (!DecodeUnicodeEscapeSequence(ref cur, end, out unicode)) return false;
            if (unicode >= 0xD800 && unicode <= 0xDBFF)
            {
                if (end - cur < 6) return false;
                if (_d[cur++] != (byte)'\\' || _d[cur++] != (byte)'u') return false;
                if (!DecodeUnicodeEscapeSequence(ref cur, end, out uint pair)) return false;
                unicode = 0x10000 + ((unicode & 0x3FF) << 10) + (pair & 0x3FF);
            }
            return true;
        }

        private bool DecodeUnicodeEscapeSequence(ref int cur, int end, out uint unicode)
        {
            unicode = 0;
            if (end - cur < 4) return false;
            for (int i = 0; i < 4; i++)
            {
                byte c = _d[cur++];
                unicode *= 16;
                if (c >= (byte)'0' && c <= (byte)'9') unicode += (uint)(c - '0');
                else if (c >= (byte)'a' && c <= (byte)'f') unicode += (uint)(c - 'a' + 10);
                else if (c >= (byte)'A' && c <= (byte)'F') unicode += (uint)(c - 'A' + 10);
                else return false;
            }
            return true;
        }

        /// <summary>codePointToUTF8.</summary>
        private static void AppendUtf8(List<byte> o, uint cp)
        {
            if (cp <= 0x7F) o.Add((byte)cp);
            else if (cp <= 0x7FF)
            {
                o.Add((byte)(0xC0 | (0x1F & (cp >> 6))));
                o.Add((byte)(0x80 | (0x3F & cp)));
            }
            else if (cp <= 0xFFFF)
            {
                o.Add((byte)(0xE0 | (0xF & (cp >> 12))));
                o.Add((byte)(0x80 | (0x3F & (cp >> 6))));
                o.Add((byte)(0x80 | (0x3F & cp)));
            }
            else if (cp <= 0x10FFFF)
            {
                o.Add((byte)(0xF0 | (0x7 & (cp >> 18))));
                o.Add((byte)(0x80 | (0x3F & (cp >> 12))));
                o.Add((byte)(0x80 | (0x3F & (cp >> 6))));
                o.Add((byte)(0x80 | (0x3F & cp)));
            }
        }
    }

    // ── The write runs with the user's own rights ────────────────────────

    /// <summary>Run <paramref name="work"/> on this thread as the same user
    /// without administrator rights when the process is the elevated half of
    /// a split token. The file is the user's, in a folder the user's
    /// unelevated programs can change, so the write gets exactly the user's
    /// access: a link planted there cannot point it at a file only
    /// administrators may write. A process that is not elevated, or runs as
    /// SYSTEM or as an administrator without a split token, runs it as is.
    ///
    /// <para>The token is SaferComputeTokenFromLevel at
    /// SAFER_LEVELID_NORMALUSER ("execute as a user that does not have
    /// Administrator or Power User user rights", Microsoft Learn,
    /// SaferCreateLevel), lowered to medium integrity, with the user as the
    /// default owner so new files are the user's.</para></summary>
    internal static T RunAsUser<T>(Func<T> work)
    {
        IntPtr process = IntPtr.Zero, limited = IntPtr.Zero, level = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out process))
                throw new System.ComponentModel.Win32Exception();
            if (!GetTokenInformation(process, TokenElevationType, out int elevationType, sizeof(int), out _))
                throw new System.ComponentModel.Win32Exception();
            if (elevationType != TokenElevationTypeFull)
                return work();

            if (!SaferCreateLevel(SAFER_SCOPEID_USER, SAFER_LEVELID_NORMALUSER, SAFER_LEVEL_OPEN, out level, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception();
            if (!SaferComputeTokenFromLevel(level, IntPtr.Zero, out limited, 0, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception();
            SetMediumIntegrity(limited);
            SetOwnerToUser(limited);

            if (!ImpersonateLoggedOnUser(limited))
                throw new System.ComponentModel.Win32Exception();
            try
            {
                return work();
            }
            finally
            {
                // A thread left impersonating would run the caller's next
                // code with the wrong token. There is no safe way on.
                if (!RevertToSelf())
                    Environment.FailFast("HIDMaestro: RevertToSelf failed after writing the OpenVR path registry.");
            }
        }
        finally
        {
            if (limited != IntPtr.Zero) CloseHandle(limited);
            if (level != IntPtr.Zero) SaferCloseLevel(level);
            if (process != IntPtr.Zero) CloseHandle(process);
        }
    }

    private static void SetMediumIntegrity(IntPtr token)
    {
        byte[] sid = new byte[SecurityIdentifier.MaxBinaryLength];
        var medium = new SecurityIdentifier("S-1-16-8192");
        medium.GetBinaryForm(sid, 0);
        SetSidInformation(token, TokenIntegrityLevel, sid, medium.BinaryLength, SE_GROUP_INTEGRITY);
    }

    private static void SetOwnerToUser(IntPtr token)
    {
        using var me = WindowsIdentity.GetCurrent();
        var user = me.User ?? throw new InvalidOperationException("The process token has no user SID.");
        byte[] sid = new byte[user.BinaryLength];
        user.GetBinaryForm(sid, 0);
        SetSidInformation(token, TokenOwner, sid, sid.Length, attributes: null);
    }

    /// <summary>TOKEN_MANDATORY_LABEL is a SID_AND_ATTRIBUTES, a pointer
    /// then a DWORD. TOKEN_OWNER is the pointer alone. The SID follows the
    /// structure in one buffer.</summary>
    private static void SetSidInformation(IntPtr token, int infoClass, byte[] sid, int sidLength, uint? attributes)
    {
        int head = attributes.HasValue ? IntPtr.Size * 2 : IntPtr.Size;
        int size = head + sidLength;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteIntPtr(buf, buf + head);
            if (attributes.HasValue) Marshal.WriteInt32(buf, IntPtr.Size, (int)attributes.Value);
            Marshal.Copy(sid, 0, buf + head, sidLength);
            if (!SetTokenInformation(token, infoClass, buf, size))
                throw new System.ComponentModel.Win32Exception();
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenOwner = 4;
    private const int TokenElevationType = 18;
    private const int TokenIntegrityLevel = 25;
    private const int TokenElevationTypeFull = 2;
    private const uint SE_GROUP_INTEGRITY = 0x20;
    private const uint SAFER_SCOPEID_USER = 2;
    private const uint SAFER_LEVELID_NORMALUSER = 0x20000;
    private const uint SAFER_LEVEL_OPEN = 1;

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SaferCreateLevel(uint scopeId, uint levelId, uint openFlags, out IntPtr level, IntPtr reserved);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SaferComputeTokenFromLevel(IntPtr level, IntPtr inToken, out IntPtr outToken, uint flags, IntPtr reserved);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SaferCloseLevel(IntPtr level);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ImpersonateLoggedOnUser(IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RevertToSelf();
}
