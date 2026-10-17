// replay --explain <base dump dir> --lane <lane>: every output of the tree
// held against the same output of a base tree, recorded with --dump, and
// recomputed from it by the rules the lane states.
//
// A case names how a stream's outputs are laid out (GoldenCase.Explainer)
// and the input of each (GoldenCase.SetInput): the state it submits, or the
// slot values it hands a packer itself. For one output the explainer starts
// from the base's bytes. For each normalized field it reads the field's
// input from that state, checks that the base holds what the base's own
// arithmetic gives for it, and writes what the lane's rules give. The
// tree's output must equal the result byte for byte. A field no rule covers
// keeps the base's bytes. While the lane states a rule, every output of a
// stream with an explainer is recomputed, the unchanged ones too, so a site
// the rule covers and the tree left alone fails like a site the tree moved
// wrongly.
//
// The rules, each a "rule" line of the lane in the change list
// (ChangeList.cs), and the categories they count:
//   rule round      decision (a) of docs/design/data-driven-devices.md, on
//                   each site's range at the base: min + RoundHalfEven(v' x
//                   (max - min)) in double, v' the clamped input or 1 minus
//                   it for a field that grows the other way. Counts round.
//                   Also counts xusb, the XUSB slice's trigger, which
//                   carries the smallest 10-bit g whose g * 255 / 1023 is the
//                   rule's XInput byte, and digital, an LT_DIGITAL,
//                   RT_DIGITAL, ZL or ZR bit, set when the rule's 8-bit
//                   trigger is nonzero
//   rule imu        a calibrated motion value: RoundHalfEven(v x n / d) in
//                   double, clamped to int16, n / d the site's own ratio.
//                   Counts imu
//   rule rest       a descriptor stick the state leaves out rests at 0.5,
//                   where the base rested an unsigned one at 0. The sticks
//                   are the tree's (its report builder's _stickAxes).
//                   Counts rest
//   rule nan to=rest|nearest-zero
//                   a NaN input writes the field's rest value (0.5 on a
//                   stick or a combined trigger, 0 on a trigger, 0 counts of
//                   motion), or the wire value nearest zero in the field's
//                   range. Counts nan, also for the combined axis of a split
//                   trigger pair that a NaN trigger feeds
//   rule centered profile=<id> <axis>=<wire center> ...
//                   decision (a)'s centered form on the profile's
//                   descriptor axes: c + RoundHalfEven(t x sL + max(t, 0) x
//                   (sH - sL)), t = v' - 0.5, sL = 2 (c - min), sH = 2 (max -
//                   c). Counts centered
//   rule stamp profile=<id> byte=<n> width=16|32 divisor=<n>
//                   the sensor timestamp at that byte of the profile's codec
//                   report: (acc / divisor) mod 2^width, acc the design
//                   note's accumulator (mechanism 1) over the states' stamps
//                   and the readings of the clock each controller's encoder
//                   reads (ScriptedClock). A stamp of 0 after the switch to
//                   the consumer's stamps adds the clock's time and moves
//                   the last stamp on by as much. Counts stamp
// A CRC field is recomputed over the bytes it covers, and counts crc. An
// output the rules do not give counts other: a lane claims those outputs
// by number, and only its entry's digest states their bytes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

/// <summary>Lays out one stream's outputs for --explain.</summary>
internal interface IExplainer
{
    /// <summary>Writes into <paramref name="expected"/>, a copy of
    /// <paramref name="before"/>, what the tree must give for this output by
    /// the lane's rules, and counts the categories of the fields it moves.
    /// Fails through <paramref name="t"/> when the base's bytes do not
    /// follow the recorded input.</summary>
    void Expect(string tag, byte[] before, byte[] expected, ExplainInput input, Tally t);
}

/// <summary>An explainer that also reads the stream's notes
/// (GoldenCase.Note), the base's and the tree's.</summary>
internal interface INoteReader
{
    void Note(string tag, byte[] before, byte[] after);
}

/// <summary>The rules of the lane --lane names, from its "rule" lines.</summary>
internal sealed class ExplainRules
{
    public bool Round, Imu, Rest;
    public string? Nan;
    public readonly Dictionary<string, Dictionary<HMAxis, int>> Centers = new(StringComparer.Ordinal);
    public readonly Dictionary<string, StampRule> Stamps = new(StringComparer.Ordinal);

    public bool Any => Round || Imu || Rest || Nan != null || Centers.Count > 0 || Stamps.Count > 0;

    public static ExplainRules Active { get; set; } = new();

    public override string ToString()
    {
        var names = new List<string>();
        if (Round) names.Add("round");
        if (Imu) names.Add("imu");
        if (Rest) names.Add("rest");
        if (Nan != null) names.Add($"nan to={Nan}");
        foreach (var (p, c) in Centers) names.Add($"centered {p} {string.Join(' ', c.Select(kv => $"{kv.Key}={kv.Value}"))}");
        foreach (var (p, s) in Stamps) names.Add($"stamp {p} byte={s.Byte} width={s.Width} divisor={s.Divisor}");
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    public static ExplainRules From(ChangeList list, string lane)
    {
        var r = new ExplainRules();
        foreach (var e in list.Rules(lane))
        {
            var f = e.Fields;
            string Need(string key) => f.TryGetValue(key, out var v) ? v
                : throw new InvalidDataException($"{e.Source}: rule {e.Target} needs {key}=");
            void Only(params string[] keys)
            {
                var extra = f.Keys.Where(k => !keys.Contains(k) && !(e.Target == "centered" && Enum.TryParse<HMAxis>(k, out _))).ToList();
                if (extra.Count > 0) throw new InvalidDataException($"{e.Source}: rule {e.Target} takes no {string.Join(", ", extra)}");
            }
            switch (e.Target)
            {
                case "round": Only(); r.Round = true; break;
                case "imu": Only(); r.Imu = true; break;
                case "rest": Only(); r.Rest = true; break;
                case "nan":
                    Only("to");
                    r.Nan = Need("to") is "rest" or "nearest-zero" ? f["to"]
                        : throw new InvalidDataException($"{e.Source}: rule nan to= takes rest or nearest-zero");
                    break;
                case "centered":
                {
                    Only("profile");
                    var axes = new Dictionary<HMAxis, int>();
                    foreach (var (k, v) in f)
                        if (k != "profile") axes[Enum.Parse<HMAxis>(k)] = ParseInt(v);
                    if (axes.Count == 0) throw new InvalidDataException($"{e.Source}: rule centered names no axis");
                    r.Centers[Need("profile")] = axes;
                    break;
                }
                case "stamp":
                {
                    Only("profile", "byte", "width", "divisor");
                    int width = ParseInt(Need("width"));
                    if (width is not (16 or 32)) throw new InvalidDataException($"{e.Source}: rule stamp width= takes 16 or 32");
                    uint divisor = (uint)ParseInt(Need("divisor"));
                    if (divisor == 0) throw new InvalidDataException($"{e.Source}: rule stamp divisor= is at least 1");
                    r.Stamps[Need("profile")] = new StampRule(ParseInt(Need("byte")), width, divisor);
                    break;
                }
                default: throw new InvalidDataException($"{e.Source}: no rule {e.Target} (Explain.cs names the rules)");
            }
        }
        return r;
    }

    static int ParseInt(string v) => v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToInt32(v[2..], 16) : int.Parse(v);
}

internal sealed record StampRule(int Byte, int Width, uint Divisor);

/// <summary>The base dump of one case read in step with the tree's outputs,
/// each output recomputed by the lane's rules, and each stream's counts held
/// against its entry in the change list.</summary>
internal sealed class ExplainSession
{
    /// <summary>The change list and the lane whose entries --explain holds
    /// each moved stream to.</summary>
    public static ChangeList? Changes { get; set; }
    public static string Lane { get; set; } = "";

    /// <summary>What one stream did against the base: its outputs by
    /// category, "other" counting those the rules do not account
    /// for.</summary>
    public sealed record StreamResult(long Same, long Changed, SortedDictionary<string, long> Categories);

    readonly BinaryReader _base;
    readonly string _case;
    string _stream = "";
    long _same, _changed, _unexplainedCount, _raw;
    readonly SortedDictionary<string, long> _categories = new(StringComparer.Ordinal);
    readonly List<string> _unexplained = new();
    readonly Tally _tally = new();
    public readonly List<string> Lines = new();
    public readonly Dictionary<string, StreamResult> Results = new(StringComparer.Ordinal);
    public int Failures { get; private set; }

    public ExplainSession(string dir, string caseName)
    {
        _case = caseName;
        string path = Path.Combine(dir, caseName + ".bin");
        if (!File.Exists(path)) throw new FileNotFoundException($"No base dump {path}.");
        _base = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 20), Encoding.ASCII);
    }

    public void Stream(string name)
    {
        Flush();
        _stream = name;
        int kind = _base.ReadByte();
        if (kind != 'S') { Fail($"stream {name}: the base has record {(char)kind} here"); throw new InvalidDataException("The replay and the base dump fell out of step."); }
        string baseName = Encoding.ASCII.GetString(_base.ReadBytes(_base.ReadUInt16()));
        if (baseName != name) { Fail($"stream {name}: the base has stream {baseName} here"); throw new InvalidDataException("The replay and the base dump fell out of step."); }
    }

    public void Out(string tag, ReadOnlySpan<byte> data, IExplainer? explainer, ExplainInput input)
    {
        int kind = _base.ReadByte();
        if (kind != 'O') { Fail($"{_stream}: output {tag}, the base has record {(char)kind} here"); throw new InvalidDataException("The replay and the base dump fell out of step."); }
        string baseTag = Encoding.ASCII.GetString(_base.ReadBytes(_base.ReadByte()));
        byte[] before = _base.ReadBytes(checked((int)_base.ReadUInt32()));
        bool same = baseTag == tag && data.SequenceEqual(before);
        bool rules = ExplainRules.Active.Any;
        if (same && (!rules || explainer == null)) { _same++; return; }
        string why;
        if (baseTag != tag) why = $"tag {baseTag} became {tag}";
        else if (explainer == null) why = "no explainer lays out this stream";
        else if (data.Length != before.Length) why = $"the length went from {before.Length} to {data.Length}";
        else
        {
            var expected = (byte[])before.Clone();
            _tally.Reset();
            explainer.Expect(tag, before, expected, input, _tally);
            if (_tally.Raw) _raw++;
            if (_tally.Why == null && data.SequenceEqual(expected))
            {
                if (same) { _same++; return; }
                _changed++;
                foreach (var c in _tally.Cats) _categories[c] = _categories.GetValueOrDefault(c) + 1;
                return;
            }
            if (_tally.Why != null) why = _tally.Why;
            else
            {
                int at = 0;
                while (data[at] == expected[at]) at++;
                why = $"byte {at}: the rules give {expected[at]:X2} (base {before[at]:X2}), the tree {data[at]:X2}";
            }
        }
        if (!same) _changed++;
        if (_unexplained.Count < 4)
            _unexplained.Add($"{tag}{(same ? ", unchanged," : "")} {why}: base {Convert.ToHexString(before)}, tree {Convert.ToHexString(data)}");
        _unexplainedCount++;
    }

    /// <summary>The base dump's next record must be the same note. It
    /// goes to the stream's explainer, and is never an output.</summary>
    public void Note(string tag, ReadOnlySpan<byte> data, IExplainer? explainer)
    {
        int kind = _base.ReadByte();
        if (kind != 'N') { Fail($"{_stream}: note {tag}, the base has record {(char)kind} here"); throw new InvalidDataException("The replay and the base dump fell out of step."); }
        string baseTag = Encoding.ASCII.GetString(_base.ReadBytes(_base.ReadByte()));
        byte[] before = _base.ReadBytes(checked((int)_base.ReadUInt32()));
        if (baseTag != tag) { Fail($"{_stream}: note {tag}, the base has note {baseTag} here"); return; }
        (explainer as INoteReader)?.Note(tag, before, data.ToArray());
    }

    /// <summary>Holds the stream's counts against the last entry naming it:
    /// a stream that moved against the base, or that the rules move and the
    /// tree does not, needs one of this lane whose categories equal the
    /// counts. "other" counts the outputs the rules do not account for,
    /// whose bytes only the entry's digest states.</summary>
    void Flush()
    {
        if (_stream.Length == 0) return;
        var counts = new SortedDictionary<string, long>(_categories, StringComparer.Ordinal);
        if (_unexplainedCount > 0) counts["other"] = _unexplainedCount;
        var result = new StreamResult(_same, _changed, counts);
        string where = $"{_case}/{_stream}";
        string tally = $"{_same} identical, {_changed} changed ({string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}"))})"
                     + (_raw == 0 ? "" : $", {_raw} carrying raw bytes");
        if (_changed > 0 || _unexplainedCount > 0)
        {
            Results[_stream] = result;
            var e = Changes?.Peek(_case, _stream);
            string verdict;
            string? problem = null;
            if (e == null || e.Lane != Lane)
                problem = e == null ? $"no entry names it" : $"its last entry, {e.Source}, is lane {e.Lane}'s";
            else if (!counts.SequenceEqual(e.Categories))
                problem = $"{e.Source} claims {(e.Fields.Count == 0 ? "no categories" : string.Join(" ", e.Fields.Select(kv => $"{kv.Key}={kv.Value}")))}";
            if (problem != null) { verdict = "FAIL"; Failures++; }
            else verdict = _unexplainedCount == 0 ? "EXPLAINED" : "CLAIMED";
            Lines.Add($"  [{verdict}] {where}: {tally}{(problem == null ? "" : $". {problem}")}");
            foreach (var u in _unexplained) Lines.Add("      " + u);
        }
        _same = _changed = _unexplainedCount = _raw = 0;
        _categories.Clear();
        _unexplained.Clear();
    }

    void Fail(string what)
    {
        Lines.Add($"  [FAIL] {_case}: {what}");
        Failures++;
    }

    public void End()
    {
        Flush();
        _stream = "";
        int kind = _base.ReadByte();
        if (kind != 'E') Fail($"the base dump has more after the replay's last output (record {(char)kind})");
        _base.Dispose();
    }
}

/// <summary>The categories of one output, and the first field whose base
/// bytes do not follow the recorded input.</summary>
internal sealed class Tally
{
    public readonly SortedSet<string> Cats = new(StringComparer.Ordinal);
    public string? Why;
    /// <summary>The output carries a raw submit's bytes, not the recorded
    /// input: no rule moves it.</summary>
    public bool Raw;

    public void Fail(string why) => Why ??= why;

    public void Reset()
    {
        Cats.Clear();
        Why = null;
        Raw = false;
    }
}

/// <summary>Bit fields, least significant bit first, as the descriptor
/// path, the codec and the packers lay them out.</summary>
internal static class Bits
{
    public static long Get(byte[] b, int at, int size)
    {
        long v = 0;
        for (int i = 0; i < size; i++)
        {
            int k = at + i;
            if ((k >> 3) < b.Length && ((b[k >> 3] >> (k & 7)) & 1) != 0) v |= 1L << i;
        }
        return v;
    }

    public static void Put(byte[] b, int at, int size, long v)
    {
        for (int i = 0; i < size; i++)
        {
            int k = at + i;
            if ((k >> 3) >= b.Length) continue;
            if (((v >> i) & 1) != 0) b[k >> 3] |= (byte)(1 << (k & 7));
            else b[k >> 3] &= (byte)~(1 << (k & 7));
        }
    }

    static long Mask(int size) => size >= 63 ? long.MaxValue : (1L << size) - 1;

    /// <summary>One field: the base must hold <paramref name="old"/>, and the
    /// expected output holds <paramref name="now"/>, counted as
    /// <paramref name="cat"/> when it moves. A field a short read cuts off
    /// is held to the bits the output carries.</summary>
    public static void Field(byte[] before, byte[] expected, int at, int size, long old, long now, string? cat,
                             Tally t, string what, double input)
    {
        size = Math.Min(size, before.Length * 8 - at);
        if (size <= 0) return;
        long mask = Mask(size);
        long got = Get(before, at, size);
        if (got != (old & mask))
        {
            t.Fail($"{what} at bit {at}: the base holds {got}, its arithmetic gives {old & mask} for input {input:R}");
            return;
        }
        if (((now ^ old) & mask) == 0) return;
        Put(expected, at, size, now);
        if (cat != null) t.Cats.Add(cat);
    }

    /// <summary>A field the rule writes whatever the base held there.</summary>
    public static void Replace(byte[] before, byte[] expected, int at, int size, long now, string cat, Tally t)
    {
        size = Math.Min(size, before.Length * 8 - at);
        if (size <= 0) return;
        if (Get(before, at, size) == (now & Mask(size))) return;
        Put(expected, at, size, now);
        t.Cats.Add(cat);
    }
}

/// <summary>The rules' arithmetic, transliterated from the design note
/// (decision (a), mechanism 1), not bound to the SDK's.</summary>
internal static class Arith
{
    public static long Linear(double v, long min, long max, bool invert)
    {
        double c = Math.Max(0.0, Math.Min(v, 1.0));
        if (invert) c = 1.0 - c;
        return min + (long)Math.Round(c * (max - min), MidpointRounding.ToEven);
    }

    public static long Centered(double v, long min, long center, long max, bool invert)
    {
        double c = Math.Max(0.0, Math.Min(v, 1.0));
        if (invert) c = 1.0 - c;
        double t = c - 0.5;
        double low = 2.0 * (center - min), high = 2.0 * (max - center);
        return center + (long)Math.Round(t * low + Math.Max(t, 0.0) * (high - low), MidpointRounding.ToEven);
    }

    public static long NearestZero(long min, long max) => Math.Clamp(0, min, max);

    public static short Scaled(float v, double numerator, double denominator)
        => (short)Math.Clamp(Math.Round((double)v * numerator / denominator, MidpointRounding.ToEven), short.MinValue, short.MaxValue);

    /// <summary>The smallest 10-bit g whose g * 255 / 1023, companion.c's
    /// integer decode, is <paramref name="t"/>.</summary>
    public static int XusbG(long t)
    {
        for (int g = (int)(t * 1023 / 255); g <= 1023; g++)
            if (g * 255 / 1023 == t) return g;
        throw new InvalidOperationException($"No 10-bit g decodes to {t}.");
    }

    /// <summary>One linear site under the lane's rules: decision (a) on
    /// [min, max] for a number, the nan rule for NaN, and
    /// <paramref name="old"/> where no rule applies.</summary>
    public static long Site(double v, long min, long max, bool invert, double rest, long old, out string? cat)
    {
        var r = ExplainRules.Active;
        if (double.IsNaN(v))
        {
            cat = "nan";
            return r.Nan switch
            {
                "rest" => Linear(rest, min, max, invert),
                "nearest-zero" => NearestZero(min, max),
                _ => old,
            };
        }
        cat = "round";
        return r.Round ? Linear(v, min, max, invert) : old;
    }

    /// <summary>A calibrated motion value under the lane's rules.</summary>
    public static short Motion(float v, double n, double d, short old, out string? cat)
    {
        var r = ExplainRules.Active;
        if (float.IsNaN(v)) { cat = "nan"; return r.Nan != null ? (short)0 : old; }
        cat = "imu";
        return r.Imu ? Scaled(v, n, d) : old;
    }

    /// <summary>An 8-bit trigger's digital bit under the lane's rules.</summary>
    public static bool Digital(double v, bool old, out string? cat)
    {
        var r = ExplainRules.Active;
        cat = "digital";
        if (double.IsNaN(v)) return r.Nan != null ? false : old;
        return r.Round ? Linear(v, 0, 255, false) > 0 : old;
    }

    /// <summary>CRC-32/ISO-HDLC over a scope, as VendorBlobCodec's
    /// ComputeCrc32 reads it.</summary>
    public static uint Crc32(CrcScope scope, byte[] buffer)
    {
        uint crc = 0xFFFFFFFFu;
        if (scope.Prefix != null) foreach (byte p in scope.Prefix) crc = Step(crc, p);
        int to = Math.Min(scope.To, buffer.Length - 1);
        for (int i = scope.From; i <= to; i++) crc = Step(crc, buffer[i]);
        return crc ^ 0xFFFFFFFFu;

        static uint Step(uint c, byte b)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }
    }
}

/// <summary>Each site's arithmetic at the base (master 3dd5e2a, the same
/// as 9bd6279 at these sites), transliterated line for line from the code
/// it names, so a field's base bytes can be checked against its
/// input.</summary>
internal static class Old
{
    // HidReportBuilder.BuildReportInto's WriteField: truncation of a double.
    public static long Descriptor(double normalized, HidReportBuilder.InputField f)
    {
        int raw = (int)(normalized * (f.LogicalMax - f.LogicalMin) + f.LogicalMin);
        return Math.Clamp(raw, f.LogicalMin, f.LogicalMax);
    }

    // VendorBlobCodec.EncodeInput: float products.
    public static long U8Axis(float v, int center, bool invert)
    {
        if (invert) v = 1f - Math.Clamp(v, 0f, 1f);
        int raw = (int)Math.Round(Math.Clamp(v, 0f, 1f) * 255);
        if (center != 128)
            raw = center + (int)Math.Round((Math.Clamp(v, 0f, 1f) - 0.5f) * 254);
        return (byte)Math.Clamp(raw, 0, 255);
    }

    public static long U8Trigger(float v) => (byte)Math.Clamp((int)Math.Round(Math.Clamp(v, 0f, 1f) * 255), 0, 255);
    public static long Stick12(float v) => Math.Clamp((int)Math.Round(Math.Clamp(v, 0f, 1f) * 4095), 0, 4095);

    public static long I16Axis(float sv, bool yAxis)
    {
        float centered = Math.Clamp(sv, 0f, 1f) - 0.5f;
        if (yAxis) centered = -centered;
        return (short)Math.Clamp((int)Math.Round(centered * 2f * 32767f), -32767, 32767);
    }

    public static long I16Pad(int raw, bool isY)
    {
        float span = isY ? 1079f : 1919f;
        float unit = Math.Clamp(raw / span, 0f, 1f) - 0.5f;
        if (isY) unit = -unit;
        return (short)Math.Clamp((int)Math.Round(unit * 2f * 32767f), -32767, 32767);
    }

    public static long U16Trigger(float tv) => (ushort)(int)Math.Round(Math.Clamp(tv, 0f, 1f) * 32767f);
    public static int TriggerByte(float v) => (int)Math.Round(Math.Clamp(v, 0f, 1f) * 255);

    // HMController's XUSB slice.
    public static long GipStick(double v) => (ushort)(v * 65535);
    public static long GipTrigger(double v) => (ushort)(v * 1023);

    // SwitchProPacker.StickRaw and ImuRaw.
    public static long SwitchStick(double v, bool invert)
    {
        double centered = (Math.Clamp(v, 0.0, 1.0) * 2.0) - 1.0;
        if (invert) centered = -centered;
        int raw = 0x800 + (int)(centered * 0x600);
        return (ushort)Math.Clamp(raw, 0, 0xFFF);
    }

    public static short Imu(float value, float scale)
        => (short)Math.Clamp((int)MathF.Round(value * scale), short.MinValue, short.MaxValue);

    // Switch2ProPacker.StickRaw and TriggerByte.
    public static long Switch2Stick(double v, bool invert)
    {
        double c = Math.Clamp(v, 0.0, 1.0);
        if (invert) c = 1.0 - c;
        return (ushort)Math.Clamp((int)Math.Round(c * 4095.0), 0, 4095);
    }

    public static int Switch2TriggerByte(double v) => (int)Math.Round(Math.Clamp(v, 0.0, 1.0) * 255);
}

/// <summary>The input of the outputs that follow, as a case sets it
/// (GoldenCase.SetInput).</summary>
internal sealed class ExplainInput
{
    public HMGamepadState State;
    public double[]? Slots;
    public bool Set;
    /// <summary>For case 8's transfers: whether the next output carries an
    /// input report the device built from the controller's frame.</summary>
    public bool Report;
}

/// <summary>The clock a case gives each controller's encoder state, when
/// the tree's encoder reads one it can set (a TimestampClock field): a
/// timestamp in 1/3 microsecond ticks that moves 4 ms and a varying part on
/// every read, so the bytes it stamps are an oracle and not the time of the
/// run. Each read is kept for --explain, which replays the accumulator from
/// them.</summary>
internal sealed class ScriptedClock
{
    public const long Frequency = 3_000_000;
    long _now = 1_000_000_000;
    int _k;
    readonly Queue<long> _reads = new();

    long Read()
    {
        _k++;
        _now += 12000 + (_k % 7) * 1500;
        if (GoldenCase.ExplainDir != null) _reads.Enqueue(_now);
        return _now;
    }

    /// <summary>The reads since the last call.</summary>
    public bool TryTake(out long now) => _reads.TryDequeue(out now);

    static readonly ConditionalWeakTable<object, ScriptedClock> s_of = new();
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly Type? s_stateType = typeof(HMController).Assembly.GetType("HIDMaestro.Internal.VendorBlobCodec+EncoderState");
    static readonly FieldInfo? s_clockField = s_stateType?.GetField("TimestampClock", Any);
    static readonly FieldInfo? s_frequencyField = s_stateType?.GetField("TimestampFrequency", Any);

    public static ScriptedClock? Of(object? encoderState) =>
        encoderState != null && s_of.TryGetValue(encoderState, out var c) ? c : null;

    /// <summary>Gives each encoder state of the controller and its ports a
    /// clock of its own. Nothing on a tree whose encoder has no such
    /// field.</summary>
    public static void Install(HMController c)
    {
        if (s_stateType is not { } stateType || s_clockField is not { } clockField || s_frequencyField is not { } frequencyField) return;
        void Give(object? state)
        {
            if (state == null || s_of.TryGetValue(state, out _)) return;
            var clock = new ScriptedClock();
            s_of.Add(state, clock);
            clockField.SetValue(state, new Func<long>(clock.Read));
            frequencyField.SetValue(state, Frequency);
        }
        foreach (var f in typeof(HMController).GetFields(Any))
            if (f.FieldType == stateType) Give(f.GetValue(c));
        if (c.Ports.GetType().GetField("_encoders", Any)?.GetValue(c.Ports) is Array ports)
            foreach (var s in ports) Give(s);
    }
}

/// <summary>The design note's timestamp accumulator (mechanism 1), in 1/3
/// microsecond ticks: the clock until the first nonzero consumer stamp, then
/// the consumer's stamps, the frame that switches adding the clock's time
/// since the previous frame. A later stamp adds (s - s_prev) mod 2^32, and
/// an advance of 2^31 or more one nominal interval. A stamp of 0 after the
/// switch adds the clock's time and moves the last stamp on by as much (the
/// lane's statement, 3aaacb2).</summary>
internal sealed class Accumulator
{
    bool _started, _consumer;
    long _origin, _prevTicks;
    uint _prevStamp;
    public ulong Value { get; private set; }

    public void Advance(uint stamp, long now, long frequency, uint nominal)
    {
        if (!_started)
        {
            _started = true;
            _origin = now;
        }
        long elapsed = Math.Max(0, now - _origin);
        long ticks = elapsed / frequency * 3_000_000 + elapsed % frequency * 3_000_000 / frequency;
        ulong clock = (ulong)Math.Max(0, ticks - _prevTicks);
        _prevTicks = Math.Max(ticks, _prevTicks);
        if (!_consumer)
        {
            Value += clock;
            if (stamp != 0)
            {
                _consumer = true;
                _prevStamp = stamp;
            }
        }
        else if (stamp == 0)
        {
            Value += clock;
            _prevStamp = unchecked(_prevStamp + (uint)clock);
        }
        else
        {
            uint d = unchecked(stamp - _prevStamp);
            Value += d >= 0x8000_0000u ? nominal : d;
            _prevStamp = stamp;
        }
    }
}

/// <summary>The descriptor path's input report: each analog field of the
/// controller's report builder from the state, as BuildReportInto reads
/// it, and every other bit kept.</summary>
internal sealed class DescriptorPart
{
    readonly HidReportBuilder _b;
    readonly HashSet<HMAxis> _sticks;
    readonly Dictionary<HMAxis, int>? _centers;
    readonly HashSet<int> _overlay = new();
    readonly HMAxis _canonLt, _canonRt;
    readonly List<(HMAxis Key, HidReportBuilder.InputField Field)> _axes;

    public DescriptorPart(ControllerProfile p, HidReportBuilder b)
    {
        _b = b;
        // The tree's sticks, read by name: absent at the base.
        _sticks = b.GetType().GetField("_stickAxes", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(b) is IEnumerable<HMAxis> s
            ? new HashSet<HMAxis>(s) : new HashSet<HMAxis>();
        ExplainRules.Active.Centers.TryGetValue(p.Id, out _centers);
        if (p.InputDefaults != null) foreach (var d in p.InputDefaults) _overlay.Add(d.Byte);
        _canonLt = HMController.ResolveCanonicalAxis(b.AxisMap, "lefttrigger", HMAxis.Z);
        _canonRt = HMController.ResolveCanonicalAxis(b.AxisMap, "righttrigger", HMAxis.Rz);
        _axes = b.AxisFields.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public byte Id => _b.InputReportId;

    /// <summary>A whole report, its ID first when the descriptor declares
    /// IDs, so <paramref name="idOffset"/> is 8 for one and 0
    /// otherwise.</summary>
    public void Expect(byte[] before, byte[] expected, int idOffset, in HMGamepadState s, Tally t)
    {
        var r = ExplainRules.Active;
        var axes = s.Axes;
        // Each field's final write: its input at the base, the input the
        // rules give it, whether it rests by the rest rule, its rest value,
        // and whether it is a trigger the combined-Z synthesis writes.
        var plan = new Dictionary<HidReportBuilder.InputField, (HMAxis Key, double Base, double Now, bool Rested, double RestV, bool Synth)>(ReferenceEqualityComparer.Instance);
        foreach (var (key, f) in _axes)
        {
            float raw = 0f;
            bool written = axes != null && axes.TryGetValue(key, out raw);
            double def = f.LogicalMin < 0 ? 0.5 : 0.0;
            double vBase = written ? Math.Clamp(raw, 0f, 1f) : def;
            bool stick = _sticks.Contains(key);
            double rest = r.Rest && stick ? 0.5 : def;
            bool rested = !written && r.Rest && stick && def != 0.5;
            plan[f] = (key, vBase, rested ? 0.5 : vBase, rested, rest, false);
        }
        if (_b.CombinedTrigger != null && _b.LeftTrigger != null && _b.RightTrigger != null)
        {
            var lk = (HMAxis)((_b.LeftTrigger.UsagePage << 8) | _b.LeftTrigger.Usage);
            var rk = (HMAxis)((_b.RightTrigger.UsagePage << 8) | _b.RightTrigger.Usage);
            double Trigger(HMAxis canonical, HMAxis fieldKey)
            {
                if (axes != null)
                {
                    if (axes.TryGetValue(canonical, out var c)) return Math.Clamp(c, 0f, 1f);
                    if (axes.TryGetValue(fieldKey, out var f)) return Math.Clamp(f, 0f, 1f);
                }
                return 0.0;
            }
            double lt = Trigger(_canonLt, lk), rt = Trigger(_canonRt, rk);
            double combined = Math.Clamp(0.5 + (rt - lt) * 0.5, 0.0, 1.0);
            // Under rule nan to=rest a NaN trigger is at its rest, 0, before
            // the combined axis is formed from the two, so the combined axis
            // carries the other trigger.
            double ltNow = r.Nan == "rest" && double.IsNaN(lt) ? 0.0 : lt;
            double rtNow = r.Nan == "rest" && double.IsNaN(rt) ? 0.0 : rt;
            double combinedNow = Math.Clamp(0.5 + (rtNow - ltNow) * 0.5, 0.0, 1.0);
            plan[_b.CombinedTrigger] = (HMAxis.Z, combined, combinedNow, false, 0.5, true);
            plan[_b.LeftTrigger] = (lk, lt, lt, false, 0.0, true);
            plan[_b.RightTrigger] = (rk, rt, rt, false, 0.0, true);
        }
        foreach (var (f, w) in plan)
        {
            int at = f.BitOffset + idOffset;
            // A byte the profile's inputDefaults overlays keeps its value.
            bool overlaid = false;
            for (int k = at >> 3; k <= (at + f.BitSize - 1) >> 3 && !overlaid; k++) overlaid = _overlay.Contains(k);
            if (overlaid) continue;
            long old = Old.Descriptor(w.Base, f);
            long now;
            string? cat;
            int c = 0;
            bool centered = !w.Synth && _centers != null && _centers.TryGetValue(w.Key, out c);
            if (double.IsNaN(w.Now))
            {
                cat = "nan";
                now = r.Nan switch
                {
                    "rest" => centered ? Arith.Centered(w.RestV, f.LogicalMin, c, f.LogicalMax, false) : Arith.Linear(w.RestV, f.LogicalMin, f.LogicalMax, false),
                    "nearest-zero" => Arith.NearestZero(f.LogicalMin, f.LogicalMax),
                    _ => old,
                };
            }
            else if (centered)
            {
                cat = "centered";
                now = Arith.Centered(w.Now, f.LogicalMin, c, f.LogicalMax, false);
            }
            else
            {
                cat = "round";
                now = r.Round ? Arith.Linear(w.Now, f.LogicalMin, f.LogicalMax, false) : Old.Descriptor(w.Now, f);
            }
            // A field whose input was NaN counts as nan whatever the rule
            // made of it: the combined axis of a NaN trigger under rule nan
            // to=rest carries the other trigger, a number.
            if (double.IsNaN(w.Base)) cat = "nan";
            if (w.Rested) cat = "rest";
            Bits.Field(before, expected, at, f.BitSize, old, now, cat, t, $"descriptor axis {w.Key}", w.Base);
        }
    }
}

/// <summary>A report the codec encodes from an extendedReport: each
/// normalized field of the tree's compiled program at its base range, the
/// CRC over the result, and the stamp rule's field.</summary>
internal sealed class CodecPart
{
    readonly VendorBlobProgram _prog;
    readonly ExtendedReportSpec _spec;
    readonly StampRule? _stamp;

    public CodecPart(ExtendedReportSpec spec, string profileId, bool stamped)
    {
        _spec = spec;
        _prog = VendorBlobProgram.Get(spec);
        if (stamped) ExplainRules.Active.Stamps.TryGetValue(profileId, out _stamp);
    }

    public int Size => _spec.Size;

    /// <summary>The report at <paramref name="at"/> in the buffers, its
    /// fields limited to bytes [lo, hi] when given (a port block's data
    /// bytes).</summary>
    public void Expect(byte[] before, byte[] expected, int at, in HMGamepadState s, float lx, float ly, float rx, float ry,
                       float lt, float rt, ulong acc, Tally t, int lo = 0, int hi = int.MaxValue)
    {
        int size = _spec.Size;
        var fields = _prog.Fields;
        var crcs = new List<int>();
        for (int fi = 0; fi < fields.Length; fi++)
        {
            ref readonly var f = ref fields[fi];
            var (flo, fhi) = VendorBlobProgram.Extent(in f, size);
            if (flo < 0 || fhi >= size || flo < lo || fhi > hi || at + flo >= before.Length) continue;
            int bit = (at + flo) * 8;
            switch (f.Op)
            {
                case VendorBlobProgram.FieldOp.U8Axis:
                {
                    float v = f.Source switch
                    {
                        VendorBlobProgram.SrcOp.LeftStickX => lx,
                        VendorBlobProgram.SrcOp.LeftStickY => ly,
                        VendorBlobProgram.SrcOp.RightStickX => rx,
                        VendorBlobProgram.SrcOp.RightStickY => ry,
                        _ => 0.5f,
                    };
                    long old = Old.U8Axis(v, f.Center, f.Invert);
                    string? cat;
                    long now = f.Center == 128
                        ? Arith.Site(v, 0, 255, f.Invert, 0.5, old, out cat)
                        : Math.Clamp(Arith.Site(v, f.Center - 127, f.Center + 127, f.Invert, 0.5, old, out cat), 0, 255);
                    Bits.Field(before, expected, bit, 8, old, now, cat, t, "codec uint8-axis", v);
                    break;
                }
                case VendorBlobProgram.FieldOp.U8Trigger:
                {
                    float v = f.Source switch
                    {
                        VendorBlobProgram.SrcOp.LeftTrigger => lt,
                        VendorBlobProgram.SrcOp.RightTrigger => rt,
                        _ => 0f,
                    };
                    long old = Old.U8Trigger(v);
                    long now = Arith.Site(v, 0, 255, false, 0.0, old, out var cat);
                    Bits.Field(before, expected, bit, 8, old, now, cat, t, "codec uint8-trigger", v);
                    break;
                }
                case VendorBlobProgram.FieldOp.Stick12Pair:
                {
                    bool right = f.Source == VendorBlobProgram.SrcOp.RightStick;
                    float vx = right ? rx : lx, vy = right ? ry : ly;
                    long ox = Old.Stick12(vx), oy = Old.Stick12(vy);
                    long nx = Arith.Site(vx, 0, 4095, false, 0.5, ox, out var cx);
                    long ny = Arith.Site(vy, 0, 4095, false, 0.5, oy, out var cy);
                    Bits.Field(before, expected, bit, 12, ox, nx, cx, t, "codec stick12-pair x", vx);
                    Bits.Field(before, expected, bit + 12, 12, oy, ny, cy, t, "codec stick12-pair y", vy);
                    break;
                }
                case VendorBlobProgram.FieldOp.I16Axis:
                {
                    float sv = f.Source switch
                    {
                        VendorBlobProgram.SrcOp.LeftStickX => lx,
                        VendorBlobProgram.SrcOp.LeftStickY => ly,
                        VendorBlobProgram.SrcOp.RightStickX => rx,
                        VendorBlobProgram.SrcOp.RightStickY => ry,
                        _ => 0.5f,
                    };
                    bool y = f.Source is VendorBlobProgram.SrcOp.LeftStickY or VendorBlobProgram.SrcOp.RightStickY;
                    long old = Old.I16Axis(sv, y);
                    long now = Arith.Site(sv, -32767, 32767, y, 0.5, old, out var cat);
                    Bits.Field(before, expected, bit, 16, old, now, cat, t, "codec int16-axis", sv);
                    break;
                }
                case VendorBlobProgram.FieldOp.I16Pad:
                case VendorBlobProgram.FieldOp.I16PadOrStick:
                {
                    bool isLeft = f.Source is VendorBlobProgram.SrcOp.LeftPadX or VendorBlobProgram.SrcOp.LeftPadY;
                    bool isY = f.Source is VendorBlobProgram.SrcOp.LeftPadY or VendorBlobProgram.SrcOp.RightPadY;
                    bool down = isLeft ? s.TouchpadFinger0Active : s.TouchpadFinger1Active;
                    if (down)
                    {
                        // A pad coordinate is an integer input, outside the
                        // rule: it keeps its arithmetic.
                        int raw = isLeft ? (isY ? s.TouchpadFinger0Y : s.TouchpadFinger0X)
                                         : (isY ? s.TouchpadFinger1Y : s.TouchpadFinger1X);
                        long old = Old.I16Pad(raw, isY);
                        Bits.Field(before, expected, bit, 16, old, old, null, t, "codec pad coordinate", raw);
                    }
                    else if (f.Op == VendorBlobProgram.FieldOp.I16PadOrStick)
                    {
                        float sv = isLeft ? (isY ? ly : lx) : (isY ? ry : rx);
                        long old = Old.I16Axis(sv, isY);
                        long now = Arith.Site(sv, -32767, 32767, isY, 0.5, old, out var cat);
                        Bits.Field(before, expected, bit, 16, old, now, cat, t, "codec pad-or-stick", sv);
                    }
                    break;
                }
                case VendorBlobProgram.FieldOp.U16Trigger:
                {
                    float tv = f.Source switch
                    {
                        VendorBlobProgram.SrcOp.LeftTrigger => lt,
                        VendorBlobProgram.SrcOp.RightTrigger => rt,
                        _ => 0f,
                    };
                    long old = Old.U16Trigger(tv);
                    long now = Arith.Site(tv, 0, 32767, false, 0.0, old, out var cat);
                    Bits.Field(before, expected, bit, 16, old, now, cat, t, "codec uint16-trigger", tv);
                    break;
                }
                case VendorBlobProgram.FieldOp.ButtonMask when f.ButtonBits != null:
                    for (int i = 0; i < f.ButtonBits.Length && f.BitLo + i <= f.BitHi; i++)
                    {
                        ulong b = f.ButtonBits[i];
                        if (b != VendorBlobProgram.ButtonLtDigital && b != VendorBlobProgram.ButtonRtDigital) continue;
                        float v = b == VendorBlobProgram.ButtonLtDigital ? lt : rt;
                        bool old = Old.TriggerByte(v) > 0;
                        bool now = Arith.Digital(v, old, out var cat);
                        Bits.Field(before, expected, (at + f.B) * 8 + f.BitLo + i, 1, old ? 1 : 0, now ? 1 : 0, cat, t, "codec trigger bit", v);
                    }
                    break;
                case VendorBlobProgram.FieldOp.Crc32:
                    crcs.Add(fi);
                    break;
            }
        }
        if (_stamp != null)
        {
            ulong field = _stamp.Width == 32 ? acc / _stamp.Divisor % (1UL << 32) : acc / _stamp.Divisor % (1UL << 16);
            Bits.Replace(before, expected, (at + _stamp.Byte) * 8, _stamp.Width, (long)field, "stamp", t);
        }
        // CRCs last, over the bytes the fields above left.
        foreach (int fi in crcs)
        {
            ref readonly var f = ref fields[fi];
            if (f.Src.Scope == null) continue;
            int dst = f.CrcDst >= 0 ? f.CrcDst : size - 4;
            // A short read carries no CRC to hold.
            if (at + size > before.Length) continue;
            var b0 = before.AsSpan(at, size).ToArray();
            var b1 = expected.AsSpan(at, size).ToArray();
            long old = Arith.Crc32(f.Src.Scope, b0);
            long now = Arith.Crc32(f.Src.Scope, b1);
            Bits.Field(before, expected, (at + dst) * 8, 32, old, now, "crc", t, "codec crc", 0);
        }
    }
}

/// <summary>SwitchProPacker.BuildBody's 48 bytes at an offset: the sticks
/// at 5 to 10 and the three motion frames at 12 to 47.</summary>
internal static class SwitchProBody
{
    static readonly (int Index, float Scale, double N, double D)[] Motion =
    {
        (0, 4096.0f, 4096, 1), (1, 4096.0f, 4096, 1), (2, 4096.0f, 4096, 1),
        (3, 13371.0f / 936.0f, 13371, 936), (4, 13371.0f / 936.0f, 13371, 936), (5, 13371.0f / 936.0f, 13371, 936),
    };

    public static void Expect(byte[] before, byte[] expected, int at, in HMGamepadState s,
                              double lx, double ly, double rx, double ry, Tally t)
    {
        Stick(before, expected, (at + 5) * 8, lx, false, t);
        Stick(before, expected, (at + 5) * 8 + 12, ly, true, t);
        Stick(before, expected, (at + 8) * 8, rx, false, t);
        Stick(before, expected, (at + 8) * 8 + 12, ry, true, t);
        // wireX = -sdlZ, wireY = -sdlX, wireZ = +sdlY, for both sensors.
        float[] values = { -s.AccelGZ, -s.AccelGX, s.AccelGY, -s.GyroDpsZ, -s.GyroDpsX, s.GyroDpsY };
        for (int frame = 0; frame < 3; frame++)
            foreach (var (i, scale, n, d) in Motion)
            {
                short old = Old.Imu(values[i], scale);
                short now = Arith.Motion(values[i], n, d, old, out var cat);
                Bits.Field(before, expected, (at + 12 + frame * 12 + i * 2) * 8, 16, (ushort)old, (ushort)now, cat, t, "switch-pro motion", values[i]);
            }
    }

    static void Stick(byte[] before, byte[] expected, int bit, double v, bool invert, Tally t)
    {
        long old = Old.SwitchStick(v, invert);
        long now = Arith.Site(v, 0x200, 0xE00, invert, 0.5, old, out var cat);
        Bits.Field(before, expected, bit, 12, old, now, cat, t, "switch-pro stick", v);
    }
}

/// <summary>Switch2ProPacker.BuildBody's 24 bytes at an offset: ZL and ZR
/// in the button word, the sticks at 4 to 11, the motion at 12 to
/// 23.</summary>
internal static class Switch2Body
{
    static readonly float AccelOld = 32767.0f / 8.0f;
    static readonly float GyroOld = (float)(Math.PI / 180.0 * 32767.0 / 34.8);
    const double GyroNew = Math.PI / 180.0 * 32767.0 / 34.8;

    public static void Expect(byte[] before, byte[] expected, int at, in HMGamepadState s,
                              double lx, double ly, double rx, double ry, double lt, double rt, Tally t)
    {
        Digital(before, expected, at, Switch2ProPacker.BitZR, rt, t);
        Digital(before, expected, at, Switch2ProPacker.BitZL, lt, t);
        Stick(before, expected, at + 4, lx, false, t);
        Stick(before, expected, at + 6, ly, true, t);
        Stick(before, expected, at + 8, rx, false, t);
        Stick(before, expected, at + 10, ry, true, t);
        Motion(before, expected, at + 12, s.AccelGX, AccelOld, 32767, 8, t);
        Motion(before, expected, at + 14, -s.AccelGZ, AccelOld, 32767, 8, t);
        Motion(before, expected, at + 16, s.AccelGY, AccelOld, 32767, 8, t);
        Motion(before, expected, at + 18, s.GyroDpsX, GyroOld, GyroNew, 1, t);
        Motion(before, expected, at + 20, -s.GyroDpsZ, GyroOld, GyroNew, 1, t);
        Motion(before, expected, at + 22, s.GyroDpsY, GyroOld, GyroNew, 1, t);
    }

    static void Digital(byte[] before, byte[] expected, int at, uint mask, double v, Tally t)
    {
        bool old = Old.Switch2TriggerByte(v) > 0;
        bool now = Arith.Digital(v, old, out var cat);
        Bits.Field(before, expected, at * 8 + System.Numerics.BitOperations.TrailingZeroCount(mask), 1, old ? 1 : 0, now ? 1 : 0, cat, t, "switch2 trigger bit", v);
    }

    static void Stick(byte[] before, byte[] expected, int at, double v, bool invert, Tally t)
    {
        long old = Old.Switch2Stick(v, invert);
        long now = Arith.Site(v, 0, 4095, invert, 0.5, old, out var cat);
        Bits.Field(before, expected, at * 8, 16, old, now, cat, t, "switch2 stick", v);
    }

    static void Motion(byte[] before, byte[] expected, int at, float v, float oldScale, double n, double d, Tally t)
    {
        short old = Old.Imu(v, oldScale);
        short now = Arith.Motion(v, n, d, old, out var cat);
        Bits.Field(before, expected, at * 8, 16, (ushort)old, (ushort)now, cat, t, "switch2 motion", v);
    }
}

/// <summary>The XUSB slice's 14 bytes: thumbs at 0 to 7, triggers at 8 to
/// 11, from the six simple slots.</summary>
internal static class XusbSlice
{
    public static void Expect(byte[] before, byte[] expected, int at, double lx, double ly, double rx, double ry,
                              double lt, double rt, Tally t)
    {
        ThumbX(before, expected, at, lx, t);
        ThumbY(before, expected, at + 2, ly, t);
        ThumbX(before, expected, at + 4, rx, t);
        ThumbY(before, expected, at + 6, ry, t);
        Trigger(before, expected, at + 8, lt, t);
        Trigger(before, expected, at + 10, rt, t);
    }

    /// <summary>companion.c reads X as g - 32768.</summary>
    static void ThumbX(byte[] before, byte[] expected, int at, double v, Tally t)
    {
        long old = Old.GipStick(v);
        long now = Arith.Site(v, 0, 65535, false, 0.5, old, out var cat);
        Bits.Field(before, expected, at * 8, 16, old, now, cat, t, "xusb thumb x", v);
    }

    /// <summary>XInput's sThumbY grows upward: sThumbY is the inverted field
    /// on [-32768, 32767], and the slice carries 32767 - sThumbY.</summary>
    static void ThumbY(byte[] before, byte[] expected, int at, double v, Tally t)
    {
        long old = Old.GipStick(v);
        long thumb = Arith.Site(v, -32768, 32767, true, 0.5, 32767 - old, out var cat);
        Bits.Field(before, expected, at * 8, 16, old, 32767 - thumb, cat, t, "xusb thumb y", v);
    }

    static void Trigger(byte[] before, byte[] expected, int at, double v, Tally t)
    {
        long old = Old.GipTrigger(v);
        var r = ExplainRules.Active;
        long now;
        string? cat;
        if (double.IsNaN(v)) { cat = "nan"; now = r.Nan != null ? 0 : old; }
        else { cat = "xusb"; now = r.Round ? Arith.XusbG(Arith.Linear(v, 0, 255, false)) : old; }
        Bits.Field(before, expected, at * 8, 16, old, now, cat, t, "xusb trigger", v);
    }
}

/// <summary>Reads the controller's paths and parts once a stream starts.</summary>
internal sealed class ControllerParts
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    public readonly HMController C;
    public readonly ControllerProfile P;
    public readonly DescriptorPart? Desc;
    public readonly CodecPart? Codec;
    public readonly PortLayout? Ports;
    public readonly CodecPart? PortBlock;
    public readonly bool SwitchPro, Switch2, Gip;
    readonly ScriptedClock? _clock;
    readonly Accumulator _acc = new();
    readonly uint _nominal;
    static readonly FieldInfo? s_armed = typeof(HMController).GetField("_extendedModeArmed", Instance);

    public ControllerParts(HMController c)
    {
        C = c;
        P = c.Profile.Inner;
        SwitchPro = typeof(HMController).GetField("_switchProtocol", Instance)?.GetValue(c) is true;
        Switch2 = typeof(HMController).GetField("_switch2Protocol", Instance)?.GetValue(c) is true;
        Gip = typeof(HMController).GetField("_packsGipBuffer", Instance)?.GetValue(c) is true;
        if (P.HasDescriptor && typeof(HMController).GetField("_reportBuilder", Instance)?.GetValue(c) is HidReportBuilder b)
            Desc = new DescriptorPart(P, b);
        if (P.ExtendedReport is { } spec)
        {
            Ports = PortLayout.Of(spec, "ports", output: false);
            if (Ports != null) PortBlock = new CodecPart(Ports.FieldSpec, P.Id, stamped: false);
            else Codec = new CodecPart(spec, P.Id, stamped: true);
            _nominal = (uint)Math.Clamp((long)spec.IdleFrameIntervalMs * 3000, 0, uint.MaxValue);
        }
        _clock = ScriptedClock.Of(typeof(HMController).GetField("_extEncoderState", Instance)?.GetValue(c));
    }

    public bool Armed => s_armed?.GetValue(C) is true;

    /// <summary>Advances the accumulator over each clock read since the last
    /// output, one read per encode, with the state's stamp, and returns
    /// it.</summary>
    public ulong Stamp(in HMGamepadState s)
    {
        if (_clock != null)
            while (_clock.TryTake(out long now)) _acc.Advance(s.SensorTimestamp, now, ScriptedClock.Frequency, _nominal);
        return _acc.Value;
    }

    /// <summary>The six slots as the base resolved them. The base passed a
    /// NaN axis through as a NaN slot, which the rules' base check and
    /// <c>rule nan</c> read. A tree that gives a NaN its rest resolves it to
    /// a number, so the slots are resolved twice more with every NaN set to
    /// two different numbers: a slot that follows the substitute came from a
    /// NaN, and is a NaN again.</summary>
    public void Slots(in HMGamepadState s, out float lx, out float ly, out float rx, out float ry, out float lt, out float rt)
    {
        C.ResolveSlots(in s, out lx, out ly, out rx, out ry, out lt, out rt);
        if (s.Axes is not { } axes || !axes.Values.Any(float.IsNaN)) return;
        var a = Substitute(in s, 0.25f);
        var b = Substitute(in s, 0.75f);
        C.ResolveSlots(in a, out float alx, out float aly, out float arx, out float ary, out float alt, out float art);
        C.ResolveSlots(in b, out float blx, out float bly, out float brx, out float bry, out float blt, out float brt);
        if (alx != blx) lx = float.NaN;
        if (aly != bly) ly = float.NaN;
        if (arx != brx) rx = float.NaN;
        if (ary != bry) ry = float.NaN;
        if (alt != blt) lt = float.NaN;
        if (art != brt) rt = float.NaN;
    }

    static HMGamepadState Substitute(in HMGamepadState s, float value)
    {
        var copy = s;
        copy.Axes = s.Axes!.ToDictionary(kv => kv.Key, kv => float.IsNaN(kv.Value) ? value : kv.Value);
        return copy;
    }
}

/// <summary>An output that is a submit's frame (Case1Submit.Frame):
/// DataSize and Data, the extended lane's size and bytes, the XUSB slice,
/// and <paramref name="tail"/> more bytes that must not change. A frame
/// the last state wrote is recomputed whole: one tagged "frame" or "state",
/// and an idle repeat of the state. A raw submit's frame keeps its lanes,
/// and the slice the last state wrote.</summary>
internal sealed class FrameExplainer : IExplainer
{
    readonly ControllerParts _c;
    readonly int _tail;

    public FrameExplainer(HMController c, int tail = 0)
    {
        _c = new ControllerParts(c);
        _tail = tail;
    }

    public void Expect(string tag, byte[] before, byte[] expected, ExplainInput input, Tally t)
    {
        ulong acc = input.Set ? _c.Stamp(in input.State) : 0;
        if (!input.Set || tag.EndsWith("throw", StringComparison.Ordinal)) return;
        if (before.Length < 4 + 14 + _tail) return;
        int dataLen = before[0] | (before[1] << 8);
        int o = 2 + dataLen;
        if (o + 2 > before.Length) { t.Fail("not a frame"); return; }
        int extLen = before[o] | (before[o + 1] << 8);
        int ext = o + 2;
        int gip = ext + extLen;
        if (gip + 14 + _tail != before.Length) { t.Fail("not a frame"); return; }
        var s = input.State;
        _c.Slots(in s, out float lx, out float ly, out float rx, out float ry, out float lt, out float rt);
        if (tag is "frame" or "state")
        {
            State(before, expected, dataLen, ext, extLen, gip, in s, lx, ly, rx, ry, lt, rt, acc, t);
            return;
        }
        // Another tag: a repeat. Of the last state, when the base's frame
        // follows it.
        var probe = new Tally();
        var e = (byte[])before.Clone();
        State(before, e, dataLen, ext, extLen, gip, in s, lx, ly, rx, ry, lt, rt, acc, probe);
        if (probe.Why == null)
        {
            e.CopyTo(expected, 0);
            foreach (var c in probe.Cats) t.Cats.Add(c);
            return;
        }
        // Of a raw submit: its lanes are the consumer's bytes, and the
        // slice is the last state's when it holds that state's slice.
        t.Raw = true;
        if (!_c.Gip) return;
        probe = new Tally();
        e = (byte[])before.Clone();
        XusbSlice.Expect(before, e, gip, lx, ly, rx, ry, lt, rt, probe);
        if (probe.Why != null) return;
        Array.Copy(e, gip, expected, gip, 14);
        foreach (var c in probe.Cats) t.Cats.Add(c);
    }

    void State(byte[] before, byte[] expected, int dataLen, int ext, int extLen, int gip, in HMGamepadState s,
               float lx, float ly, float rx, float ry, float lt, float rt, ulong acc, Tally t)
    {
        if (dataLen > 0)
        {
            if (_c.SwitchPro) SwitchProBody.Expect(before, expected, 2, in s, lx, ly, rx, ry, t);
            else if (_c.Desc != null)
            {
                // The data lane is the report without its ID byte.
                int id = _c.Desc.Id != 0 ? 1 : 0;
                var b0 = new byte[dataLen + id];
                var b1 = new byte[dataLen + id];
                if (id != 0) b0[0] = b1[0] = _c.Desc.Id;
                Array.Copy(before, 2, b0, id, dataLen);
                Array.Copy(before, 2, b1, id, dataLen);
                _c.Desc.Expect(b0, b1, id * 8, in s, t);
                Array.Copy(b1, id, expected, 2, dataLen);
            }
        }
        if (extLen > 0)
        {
            if (_c.Switch2) Switch2Body.Expect(before, expected, ext, in s, lx, ly, rx, ry, lt, rt, t);
            else if (_c.Ports != null && _c.PortBlock != null)
            {
                // Port 1 takes SubmitState. The other blocks keep what they
                // held.
                int block = ext + _c.Ports.BlockAt(0);
                int lo = _c.Ports.DataLo >= 0 ? _c.Ports.DataLo : 0;
                int hi = _c.Ports.DataLo >= 0 ? _c.Ports.DataLo + _c.Ports.DataLength - 1 : _c.Ports.Stride - 1;
                if (_c.Ports.DataLo < 0 && _c.Ports.StatusByte >= 0)
                {
                    // Every byte but the status byte: the two runs around it.
                    if (_c.Ports.StatusByte > lo) _c.PortBlock.Expect(before, expected, block, in s, lx, ly, rx, ry, lt, rt, 0, t, lo, _c.Ports.StatusByte - 1);
                    if (_c.Ports.StatusByte < hi) _c.PortBlock.Expect(before, expected, block, in s, lx, ly, rx, ry, lt, rt, 0, t, _c.Ports.StatusByte + 1, hi);
                }
                else _c.PortBlock.Expect(before, expected, block, in s, lx, ly, rx, ry, lt, rt, 0, t, lo, hi);
            }
            else if (_c.Codec != null && _c.Armed) _c.Codec.Expect(before, expected, ext, in s, lx, ly, rx, ry, lt, rt, acc, t);
        }
        if (_c.Gip) XusbSlice.Expect(before, expected, gip, lx, ly, rx, ry, lt, rt, t);
    }
}

/// <summary>Outputs that carry an input report a USB/IP device sends, after
/// <paramref name="prefix"/> bytes that must not change, built from the
/// controller's frame: a codec report, a descriptor report with its ID, a
/// Switch 2 Pro report the device builds from the packer's body, and the
/// body itself ("body"). Case 6's reports are its "in" outputs, case 8's
/// the transfers it marks (ExplainInput.Report).</summary>
internal sealed class UsbipReportExplainer : IExplainer
{
    readonly ControllerParts _c;
    readonly int _prefix;
    readonly bool _alwaysArmed;
    byte[]? _bodyBefore, _bodyNow;
    readonly SortedSet<string> _bodyCats = new(StringComparer.Ordinal);

    public UsbipReportExplainer(HMController c, int prefix)
    {
        _c = new ControllerParts(c);
        _prefix = prefix;
        _alwaysArmed = _c.P.ExtendedReport?.AlwaysArmed == true;
    }

    public void Expect(string tag, byte[] before, byte[] expected, ExplainInput input, Tally t)
    {
        ulong acc = input.Set ? _c.Stamp(in input.State) : 0;
        if (!input.Set) return;
        var s = input.State;
        _c.Slots(in s, out float lx, out float ly, out float rx, out float ry, out float lt, out float rt);
        if (_c.Switch2 && tag is "body" or "body-not-taken")
        {
            Switch2Body.Expect(before, expected, 0, in s, lx, ly, rx, ry, lt, rt, t);
            _bodyBefore = (byte[])before.Clone();
            _bodyNow = (byte[])expected.Clone();
            _bodyCats.Clear();
            _bodyCats.UnionWith(t.Cats);
            return;
        }
        bool report = tag == "in" || tag.StartsWith("in.", StringComparison.Ordinal) || input.Report;
        if (!report || before.Length <= _prefix) return;
        int n = before.Length - _prefix;
        if (_c.Switch2)
        {
            if (before[_prefix] is Switch2ProDevice.ReportPro or Switch2ProDevice.ReportCommon) Switch2Report(before, expected, _prefix, t);
            return;
        }
        // A codec report by its report ID, or by its length when it has
        // none. A read shorter than the report is its first bytes.
        byte id = before[_prefix];
        bool codec = _c.Codec != null && (_c.P.ExtendedReport!.ReportIdByte != 0 ? id == _c.P.ExtendedReport.ReportIdByte : n == _c.Codec.Size);
        bool armed = _alwaysArmed || _c.Armed;
        // A report the base did not build from the last state carries a raw
        // submit's bytes, and keeps them.
        var probe = new Tally();
        var e = (byte[])before.Clone();
        if (codec && armed)
            _c.Codec!.Expect(before, e, _prefix, in s, lx, ly, rx, ry, lt, rt, acc, probe);
        else if (_c.Desc != null && (_c.Desc.Id == 0 || id == _c.Desc.Id))
        {
            var r0 = before.AsSpan(_prefix).ToArray();
            var r1 = (byte[])r0.Clone();
            _c.Desc.Expect(r0, r1, _c.Desc.Id != 0 ? 8 : 0, in s, probe);
            r1.CopyTo(e, _prefix);
        }
        else if (codec)
            _c.Codec!.Expect(before, e, _prefix, in s, lx, ly, rx, ry, lt, rt, acc, probe);
        if (probe.Why != null) { t.Raw = true; return; }
        e.CopyTo(expected, 0);
        foreach (var c in probe.Cats) t.Cats.Add(c);
    }

    /// <summary>Switch2ProDevice.BuildReport from the last body: report
    /// 0x09's sticks at 6 to 11 and buttons at 3 to 5, report 0x05's
    /// sticks at 0x0B to 0x10, ZR and ZL at 0x05 and 0x07 bit 7, and its
    /// motion at 0x31 to 0x3C while its IMU timestamp runs. A report that
    /// moves counts the categories its body moved by.</summary>
    void Switch2Report(byte[] before, byte[] expected, int at, Tally t)
    {
        if (_bodyBefore == null || _bodyNow == null) return;
        bool pro = before[at] == Switch2ProDevice.ReportPro;
        int n = before.Length - at;
        int s = Switch2ProPacker.SticksOffset;
        int[] sticks = pro ? new[] { 6, 9 } : new[] { 0x0B, 0x0E };
        for (int k = 0; k < 2; k++)
        {
            long ox = _bodyBefore[s + 4 * k] | (_bodyBefore[s + 4 * k + 1] << 8);
            long oy = _bodyBefore[s + 4 * k + 2] | (_bodyBefore[s + 4 * k + 3] << 8);
            long nx = _bodyNow[s + 4 * k] | (_bodyNow[s + 4 * k + 1] << 8);
            long ny = _bodyNow[s + 4 * k + 2] | (_bodyNow[s + 4 * k + 3] << 8);
            int bit = (at + sticks[k]) * 8;
            Bits.Field(before, expected, bit, 12, ox, nx, null, t, "switch2 report stick x", ox);
            Bits.Field(before, expected, bit + 12, 12, oy, ny, null, t, "switch2 report stick y", oy);
        }
        uint b0 = BitConverter.ToUInt32(_bodyBefore, 0), b1 = BitConverter.ToUInt32(_bodyNow, 0);
        if (pro)
        {
            for (int bit = 0; bit < 21; bit++)
                if ((((b0 ^ b1) >> bit) & 1) != 0)
                    Bits.Field(before, expected, (at + 3) * 8 + bit, 1, (b0 >> bit) & 1, (b1 >> bit) & 1, null, t, "switch2 report button", bit);
        }
        else
        {
            foreach (var (mask, bit) in new[] { (Switch2ProPacker.BitZR, 0x05 * 8 + 7), (Switch2ProPacker.BitZL, 0x07 * 8 + 7) })
                if (((b0 ^ b1) & mask) != 0)
                    Bits.Field(before, expected, at * 8 + bit, 1, (b0 & mask) != 0 ? 1 : 0, (b1 & mask) != 0 ? 1 : 0, null, t, "switch2 report trigger bit", bit);
            // The IMU timestamp at 0x2B is 1 + n * 4000, never 0, while the
            // report carries motion.
            if (n >= 0x2F && BitConverter.ToUInt32(before, at + 0x2B) != 0)
                for (int i = 0; i < 12; i += 2)
                {
                    int m = Switch2ProPacker.MotionOffset + i;
                    long om = _bodyBefore[m] | (_bodyBefore[m + 1] << 8), nm = _bodyNow[m] | (_bodyNow[m + 1] << 8);
                    Bits.Field(before, expected, (at + 0x31 + i) * 8, 16, om, nm, null, t, "switch2 report motion", om);
                }
        }
        if (!before.AsSpan().SequenceEqual(expected)) t.Cats.UnionWith(_bodyCats);
    }
}

/// <summary>Case 2's bodies: each from the slot values the case hands the
/// packer, and the state.</summary>
internal sealed class BodyExplainer : IExplainer
{
    readonly bool _pro;
    public BodyExplainer(bool switchPro) => _pro = switchPro;

    public void Expect(string tag, byte[] before, byte[] expected, ExplainInput input, Tally t)
    {
        if (!input.Set || input.Slots is not { } v) { t.Fail("no slot values for the body"); return; }
        var s = input.State;
        if (_pro) SwitchProBody.Expect(before, expected, 0, in s, v[0], v[1], v[2], v[3], t);
        else Switch2Body.Expect(before, expected, 0, in s, v[0], v[1], v[2], v[3], v[4], v[5], t);
    }
}

/// <summary>Case 5's plan, whose golden output is its text's digest, and
/// the Switch Pro streams the driver serves from the plan's bodies. Each
/// body is recomputed from its input (Case5Driver.SwitchBodyInputs), and
/// every other line of the text must stay. A driver report is matched to
/// its body by the base's bytes.</summary>
internal sealed class PlanExplainer : IExplainer, INoteReader
{
    string[]? _before, _after;
    readonly Dictionary<string, (byte[] Base, byte[] Expected, SortedSet<string> Cats)> _bodies = new(StringComparer.Ordinal);
    string? _why;

    public void Note(string tag, byte[] before, byte[] after)
    {
        _before = Encoding.UTF8.GetString(before).Split('\n');
        _after = Encoding.UTF8.GetString(after).Split('\n');
        _bodies.Clear();
        _why = null;
        var inputs = Case5Driver.SwitchBodyInputs().ToDictionary(x => x.Name, StringComparer.Ordinal);
        foreach (var line in _before)
        {
            var b = line.Split(' ');
            if (b.Length != 3 || b[0] != "body") continue;
            if (!inputs.TryGetValue(b[1], out var input)) { _why ??= $"no input for body {b[1]}"; continue; }
            byte[] b0 = Convert.FromHexString(b[2]);
            var b1 = (byte[])b0.Clone();
            var t = new Tally();
            var s = input.State;
            SwitchProBody.Expect(b0, b1, 0, in s, input.Lx, input.Ly, input.Rx, input.Ry, t);
            if (t.Why != null) _why ??= $"body {b[1]}: {t.Why}";
            _bodies[b[1]] = (b0, b1, new SortedSet<string>(t.Cats, StringComparer.Ordinal));
        }
    }

    /// <summary>The plan's text with each body the rules give.</summary>
    string ExpectedText()
    {
        var lines = (string[])_before!.Clone();
        for (int i = 0; i < lines.Length; i++)
        {
            var b = lines[i].Split(' ');
            if (b.Length == 3 && b[0] == "body" && _bodies.TryGetValue(b[1], out var x))
                lines[i] = $"body {b[1]} {Convert.ToHexString(x.Expected)}";
        }
        return string.Join('\n', lines);
    }

    public void Expect(string tag, byte[] before, byte[] expected, ExplainInput input, Tally t)
    {
        if (_before == null) { t.Fail("no plan text was noted"); return; }
        if (_why != null) { t.Fail(_why); return; }
        if (tag == "sha256")
        {
            string text = ExpectedText();
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)).CopyTo(expected, 0);
            foreach (var (name, x) in _bodies)
                if (!x.Base.AsSpan().SequenceEqual(x.Expected)) foreach (var c in x.Cats) t.Cats.Add(c);
            var lines = text.Split('\n');
            for (int i = 0; i < Math.Max(lines.Length, _after!.Length); i++)
                if (i >= lines.Length || i >= _after.Length || lines[i] != _after[i])
                {
                    t.Fail($"plan line {i + 1}: the rules give \"{(i < lines.Length ? lines[i] : "")}\", the tree \"{(i < _after.Length ? _after[i] : "")}\"");
                    break;
                }
        }
    }

    /// <summary>A driver stream's outputs: a 12-byte record head and the
    /// input report. Report 0x30 carries the body's bytes 2 to 10 at 3 to
    /// 11 and, while the IMU runs, bytes 12 to 47 at 13 to 48 (driver.c
    /// SwitchFillLatestState). Report 0x21 carries bytes 2 to 10.</summary>
    public IExplainer Driver() => new DriverStream(this);

    sealed class DriverStream(PlanExplainer plan) : IExplainer
    {
        const int Head = 12;

        public void Expect(string tag, byte[] before, byte[] expected, ExplainInput input, Tally t)
        {
            if (before.Length < Head + 12 || before[Head] is not (0x30 or 0x21)) return;
            if (plan._why != null) { t.Fail(plan._why); return; }
            bool imu = before[Head] == 0x30 && before.Length >= Head + 49;
            // The bodies whose buttons and sticks, and motion where the
            // report carries it, the base's report holds.
            var hits = plan._bodies.Values.Where(x =>
                before.AsSpan(Head + 3, 9).SequenceEqual(x.Base.AsSpan(2, 9))
                && (!imu || before.AsSpan(Head + 13, 36).SequenceEqual(x.Base.AsSpan(12, 36))
                         || before.AsSpan(Head + 13, 36).IndexOfAnyExcept((byte)0) < 0)).ToList();
            // A report of raw bytes the harness wrote carries no body, and
            // keeps its bytes.
            if (hits.Count == 0) return;
            var first = hits[0];
            bool motion = imu && before.AsSpan(Head + 13, 36).SequenceEqual(first.Base.AsSpan(12, 36)) && before.AsSpan(Head + 13, 36).IndexOfAnyExcept((byte)0) >= 0;
            foreach (var h in hits.Skip(1))
                if (!h.Expected.AsSpan(2, 9).SequenceEqual(first.Expected.AsSpan(2, 9))
                    || (motion && !h.Expected.AsSpan(12, 36).SequenceEqual(first.Expected.AsSpan(12, 36))))
                {
                    t.Fail("the base's report matches bodies the rules move apart");
                    return;
                }
            first.Expected.AsSpan(2, 9).CopyTo(expected.AsSpan(Head + 3));
            if (motion) first.Expected.AsSpan(12, 36).CopyTo(expected.AsSpan(Head + 13));
            if (!before.AsSpan().SequenceEqual(expected)) foreach (var c in first.Cats) t.Cats.Add(c);
        }
    }
}
