using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Programs;

/// <summary>The naive evaluator on a program as its author wrote it: the
/// profile's JSON, read here key by key from the design note (mechanism 2:
/// the op table, "One semantics" and its write order, the condition grammar,
/// the hat8 sector rule, and mechanism 1's packed-uint for the decoders).
/// Nothing here goes through the SDK's parse, its lowering to the validated
/// form, its condition parser or the decoders' tables, so a defect in any of
/// those shows as a difference from the generated and interpreted paths.
/// A default the note does not state (a field's bytes, clamp and ratio, a
/// counter's width and scale, a register's width) is the schema's, written
/// out at its use. Programs built in code have no authored form, and
/// <see cref="Naive"/> reads those.</summary>
internal sealed class NaiveJson
{
    readonly JsonObject _root;
    readonly string[] _regNames;
    readonly int[] _regBits;
    readonly string[] _counterNames;

    public NaiveJson(JsonObject root)
    {
        _root = root;
        var state = root["protocol"]?["state"] as JsonArray;
        _regNames = state?.Select(r => (string)r!["name"]!).ToArray() ?? Array.Empty<string>();
        // A register is 1 bit unless it says otherwise.
        _regBits = state?.Select(r => r!["bits"] is JsonNode b ? (int)b : 1).ToArray() ?? Array.Empty<int>();
        var counters = root["protocol"]?["counters"] as JsonArray;
        _counterNames = counters?.Select(c => (string)c!["name"]!).ToArray() ?? Array.Empty<string>();
    }

    public delegate void Evaluate(byte[] s, byte[] d, uint[] regs, uint[] ctr);

    /// <summary>Each program the compiled model holds, with this evaluator
    /// for the JSON it came from: layouts by position, decoders by name,
    /// emitted reports by their rule and live feature reports by position.</summary>
    public IEnumerable<(ValidProgram Program, Evaluate Naive, string What)> Pair(CompiledModel m)
    {
        var layouts = (JsonArray)_root["inputReports"]!;
        for (int i = 0; i < m.Layouts.Length; i++)
        {
            var program = (JsonArray)layouts[i]!["program"]!;
            int size = (int)layouts[i]!["size"]!;
            yield return (m.Layouts[i].Program, (s, d, r, c) => Program(program, size, s, d, r, c), $"layout {m.Layouts[i].Name}");
        }
        foreach (var dec in m.Decoders)
        {
            var spec = (JsonObject)_root["protocol"]!["decoders"]![dec.Name]!;
            yield return (dec.Program, (s, d, r, c) => Decode(spec, s, d), $"decoder {dec.Name}");
        }
        var rules = _root["protocol"]?["commands"]?["rules"] as JsonArray;
        for (int i = 0; i < m.RuleEmits.Length; i++)
        {
            if (m.RuleEmits[i] is not { } e) continue;
            var emit = (JsonObject)rules![i]!["emitOutput"]!;
            var program = (JsonArray)emit["program"]!;
            int size = (int)emit["size"]!;
            yield return (e.Program, (s, d, r, c) => Program(program, size, s, d, r, c), $"emitted report of rule {i}");
        }
        var live = _root["liveFeatureReports"] as JsonArray;
        for (int i = 0; i < m.LiveFeatures.Length; i++)
        {
            var program = (JsonArray)live![i]!["program"]!;
            int size = (int)live[i]!["size"]!;
            yield return (m.LiveFeatures[i], (s, d, r, c) => Program(program, size, s, d, r, c), $"live feature report {i}");
        }
    }

    // ── Values ───────────────────────────────────────────────────────────

    static int StateBit(byte[] s, long k) => (s[k / 8] >> (int)(k % 8)) & 1;

    static void SetReportBit(byte[] d, long k, int value)
    {
        int mask = 1 << (int)(k % 8);
        d[k / 8] = (byte)(value != 0 ? d[k / 8] | mask : d[k / 8] & ~mask);
    }

    /// <summary>"A value is read as N bits at a bit offset or as a 16-bit
    /// word in either byte order": { "bit", "bits" }, a byte { "byte" }, or
    /// { "word", "order" } at a byte offset, little-endian unless "be".</summary>
    static long Read(byte[] s, JsonNode r)
    {
        if (r["word"] is JsonNode w)
        {
            int at = (int)w;
            return (string?)r["order"] == "be" ? 256L * s[at] + s[at + 1] : s[at] + 256L * s[at + 1];
        }
        if (r["byte"] is JsonNode b) return s[(int)b];
        return Bits(s, (long)r["bit"]!, (int)r["bits"]!);
    }

    static long Bits(byte[] s, long at, int count)
    {
        long v = 0;
        for (int i = 0; i < count; i++) v += (long)StateBit(s, at + i) << i;
        return v;
    }

    static uint Hex(JsonNode n)
    {
        if (n.GetValueKind() == System.Text.Json.JsonValueKind.Number) return (uint)n;
        if (n.GetValueKind() == System.Text.Json.JsonValueKind.True) return 1;
        if (n.GetValueKind() == System.Text.Json.JsonValueKind.False) return 0;
        string t = ((string)n!).Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return uint.Parse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    static byte[] HexBytes(string text)
    {
        string t = text.Replace(" ", "", StringComparison.Ordinal);
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return Convert.FromHexString(t);
    }

    /// <summary>"a-b" or "a", inclusive.</summary>
    static (int lo, int hi) ByteRange(string text)
    {
        int dash = text.IndexOf('-');
        return dash < 0 ? (int.Parse(text, CultureInfo.InvariantCulture), int.Parse(text, CultureInfo.InvariantCulture))
            : (int.Parse(text[..dash], CultureInfo.InvariantCulture), int.Parse(text[(dash + 1)..], CultureInfo.InvariantCulture));
    }

    /// <summary>A ratio: a number, or [numerator, denominator].</summary>
    static (double num, double den) Ratio(JsonNode? n) =>
        n == null ? (1, 1) : n is JsonArray a ? ((double)a[0]!, (double)a[1]!) : ((double)n, 1);

    // ── Conditions ───────────────────────────────────────────────────────

    /// <summary>"Terms join with &amp;&amp; into a group, and groups join
    /// with ||". A term tests a register as wide as it is declared: name
    /// (nonzero), !name (zero), name==V, name!=V, name&amp;M (any of the bits
    /// set) or name in a,b,c, every value hex. The list form is one group.</summary>
    bool Holds(JsonNode cond, uint[] regs)
    {
        string text = cond is JsonArray list ? string.Join(" && ", list.Select(t => (string)t!)) : (string)cond!;
        foreach (string group in text.Split("||"))
        {
            bool all = true;
            foreach (string raw in group.Split("&&"))
                all &= Term(raw.Trim(), regs);
            if (all) return true;
        }
        return false;
    }

    bool Term(string t, uint[] regs)
    {
        uint Reg(string name)
        {
            int i = Array.IndexOf(_regNames, name.Trim());
            if (i < 0) throw new InvalidOperationException($"no register {name}");
            int bits = _regBits[i];
            return bits == 32 ? regs[i] : regs[i] & ((1u << bits) - 1);
        }
        static uint V(string v) => Hex(JsonValue.Create(v.Trim())!);
        if (t.StartsWith('!') && !t.Contains('=')) return Reg(t[1..]) == 0;
        int at;
        if ((at = t.IndexOf("!=", StringComparison.Ordinal)) > 0) return Reg(t[..at]) != V(t[(at + 2)..]);
        if ((at = t.IndexOf("==", StringComparison.Ordinal)) > 0) return Reg(t[..at]) == V(t[(at + 2)..]);
        if ((at = t.IndexOf('&')) > 0) return (Reg(t[..at]) & V(t[(at + 1)..])) != 0;
        if ((at = t.IndexOf(" in ", StringComparison.Ordinal)) > 0)
        {
            uint r = Reg(t[..at]);
            return t[(at + 4)..].Split(',').Any(x => V(x) == r);
        }
        return Reg(t) != 0;
    }

    // ── Programs ─────────────────────────────────────────────────────────

    /// <summary>The note's write order: the template of every top-level
    /// const, then every other top-level op in list order, then each block
    /// whose condition holds, its ops in list order, a later write replacing
    /// an earlier one. An op that ORs into a byte writes its bits over it,
    /// which gives the same byte under validation's template rule.</summary>
    void Program(JsonArray program, int size, byte[] s, byte[] d, uint[] regs, uint[] ctr)
    {
        for (int i = 0; i < size; i++) d[i] = 0;
        foreach (var op in program)
            if (op!["const"] is JsonObject c) Const(c, d);
        foreach (var op in program)
            if (op!["const"] == null && op["when"] == null) Op((JsonObject)op, s, d, ctr);
        foreach (var op in program)
        {
            if (op!["when"] is not JsonNode cond || !Holds(cond, regs)) continue;
            foreach (var sub in (JsonArray)op["ops"]!)
            {
                if (sub!["const"] is JsonObject c) Const(c, d);
                else Op((JsonObject)sub, s, d, ctr);
            }
        }
    }

    static void Const(JsonObject c, byte[] d)
    {
        foreach (var (key, hex) in c)
        {
            int at = int.Parse(key, CultureInfo.InvariantCulture);
            var bytes = HexBytes((string)hex!);
            for (int i = 0; i < bytes.Length; i++) d[at + i] = bytes[i];
        }
    }

    void Op(JsonObject o, byte[] s, byte[] d, uint[] ctr)
    {
        if (o["copy"] is JsonNode copy)
        {
            var (lo, hi) = ByteRange((string)copy!);
            int to = (int)o["to"]!;
            if (o["mask"] is JsonNode mask)
            {
                // Bits under the mask, each from its state bit to the same
                // bit of the report byte.
                int m = (int)Hex(mask);
                for (int k = 0; k <= hi - lo; k++)
                    for (int b = 0; b < 8; b++)
                        if (((m >> b) & 1) != 0) SetReportBit(d, (to + k) * 8L + b, StateBit(s, (lo + k) * 8L + b));
            }
            else for (int k = 0; k <= hi - lo; k++) d[to + k] = s[lo + k];
        }
        else if (o["bits"] is JsonArray moves)
            foreach (var pair in moves) SetReportBit(d, (long)pair![1]!, StateBit(s, (long)pair[0]!));
        else if (o["xor"] is JsonNode xor)
        {
            var (lo, hi) = ByteRange((string)xor!);
            int to = (int)o["to"]!, m = (int)Hex(o["mask"]!);
            for (int k = 0; k <= hi - lo; k++) d[to + k] = (byte)(s[lo + k] ^ m);
        }
        else if (o["swap16"] is JsonNode swap)
        {
            var (lo, hi) = ByteRange((string)swap!);
            int to = (int)o["to"]!;
            for (int k = 0; k + 1 <= hi - lo; k += 2) { d[to + k] = s[lo + k + 1]; d[to + k + 1] = s[lo + k]; }
        }
        else if (o["field"] is JsonNode field) Field(o, field, s, d);
        else if (o["lookup"] is JsonNode lookup)
        {
            var table = HexBytes((string)o["table"]!);
            int entry = table[Bits(s, (long)lookup["bit"]!, (int)lookup["bits"]!)];
            int written = table.Aggregate(0, (acc, e) => acc | e);
            int to = (int)o["to"]!;
            for (int b = 0; b < 8; b++)
                if (((written >> b) & 1) != 0) SetReportBit(d, to * 8L + b, (entry >> b) & 1);
        }
        else if (o["hat8"] is JsonArray hat)
        {
            long center = (long)o["center"]!;
            long x = Read(s, hat[0]!) - center, y = Read(s, hat[1]!) - center;
            if (!(o["yUp"] is JsonNode up && (bool)up)) y = -y;
            d[(int)o["to"]!] = HexBytes((string)o["table"]!)[Naive.HatSector(x, y, (long)o["deadzone"]!)];
        }
        else if (o["pack12"] is JsonArray pack)
        {
            int xs = (int)pack[0]!, ys = (int)pack[1]!, to = (int)o["to"]!;
            long x = s[xs] + 256L * s[xs + 1], y = s[ys] + 256L * s[ys + 1];
            long v24 = (x % 4096) + 4096 * (y % 4096);
            for (int i = 0; i < 3; i++) d[to + i] = (byte)(v24 >> (8 * i));
        }
        else if (o["counter"] is JsonNode counter)
        {
            // A counter's width is 8 bits and its scale 1 unless given.
            int slot = Array.IndexOf(_counterNames, (string)counter!);
            int bytes = (o["width"] is JsonNode w ? (int)w : 8) / 8;
            ulong scale = o["scale"] is JsonNode sc ? (ulong)Ratio(sc).num : 1;
            ulong v = ((ulong)ctr[slot] * scale + (o["add"] is JsonNode a ? Hex(a) : 0)) % 4294967296UL;
            bool be = (string?)o["order"] == "be";
            int at = (int)o["at"]!;
            for (int i = 0; i < bytes; i++) d[be ? at + bytes - 1 - i : at + i] = (byte)(v >> (8 * i));
        }
        else throw new InvalidOperationException("the note defines no op " + o.ToJsonString());
    }

    /// <summary>"minus a center, negated if asked, times a ratio with C
    /// truncation, plus an offset, then clamped or, with overflow wrap16,
    /// wrapped as an unsigned 16-bit value, written as uint8 or uint16 in
    /// either order". One byte, a ratio of 1, an offset of 0 and a clamp to
    /// the bytes' unsigned range unless given.</summary>
    static void Field(JsonObject o, JsonNode read, byte[] s, byte[] d)
    {
        int bytes = o["bytes"] is JsonNode b ? (int)b : 1;
        long v = Read(s, read) - (o["center"] is JsonNode c ? (long)c : 0);
        if (o["negate"] is JsonNode neg && (bool)neg) v = -v;
        var (num, den) = Ratio(o["scale"]);
        long product = v * (long)num;
        long quotient = Math.Abs(product) / Math.Abs((long)den);
        if ((product < 0) != (den < 0) && product != 0) quotient = -quotient;
        v = quotient + (o["offset"] is JsonNode off ? (long)off : 0);
        long w;
        if ((string?)o["overflow"] == "wrap16") w = ((v % 65536) + 65536) % 65536;
        else
        {
            long min = 0, max = bytes == 1 ? 255 : 65535;
            if (o["clamp"] is JsonArray clamp) { min = (long)clamp[0]!; max = (long)clamp[1]!; }
            w = Math.Min(Math.Max(v, min), max);
        }
        long u16 = ((w % 65536) + 65536) % 65536;
        int to = (int)o["to"]!;
        if (bytes == 1) d[to] = (byte)u16;
        else if ((string?)o["order"] == "be") { d[to] = (byte)(u16 >> 8); d[to + 1] = (byte)u16; }
        else { d[to] = (byte)u16; d[to + 1] = (byte)(u16 >> 8); }
    }

    // ── Decoders ─────────────────────────────────────────────────────────

    /// <summary>packed-uint (mechanism 1): each part's pieces assembled, the
    /// first lowest, over its frames of a stride, plus the offset, 0 outside
    /// the range, scaled by the ratio and rounded down or to nearest with ties
    /// to even, clamped to 0..255, the largest frame and then the largest part
    /// kept. Field j is output byte j.</summary>
    static void Decode(JsonObject spec, byte[] s, byte[] d)
    {
        var fields = (JsonArray)spec["fields"]!;
        for (int j = 0; j < fields.Count; j++)
        {
            int best = 0;
            foreach (var part in (JsonArray)fields[j]!["parts"]!)
            {
                int frames = 1, stride = 0;
                if (part!["frames"] is JsonArray fr) { frames = (int)fr[0]!; stride = (int)fr[1]!; }
                var (num, den) = Ratio(part["scale"]);
                long offset = part["offset"] is JsonNode o ? (long)o : 0;
                long lo = long.MinValue, hi = long.MaxValue;
                if (part["range"] is JsonArray range) { lo = (long)range[0]!; hi = (long)range[1]!; }
                bool down = (string?)part["round"] == "down";
                for (int f = 0; f < frames; f++)
                {
                    long value = 0;
                    int shift = 0;
                    foreach (var piece in (JsonArray)part["pieces"]!)
                    {
                        int count = (int)piece!["count"]!;
                        value += Bits(s, (long)piece["at"]! + 8L * stride * f, count) << shift;
                        shift += count;
                    }
                    long v = value + offset;
                    int level = 0;
                    if (v >= lo && v <= hi)
                    {
                        double scaled = v * num / den;
                        double r = down ? Math.Floor(scaled) : Math.Round(scaled, MidpointRounding.ToEven);
                        level = (int)Math.Clamp(r, 0, 255);
                    }
                    best = Math.Max(best, level);
                }
            }
            d[j] = (byte)best;
        }
    }
}
