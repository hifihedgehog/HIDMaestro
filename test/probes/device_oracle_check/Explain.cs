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
//   rule stamp profile=<id> byte=<n> width=16|32 divisor=<n> interval-ms=<n>
//                   the sensor timestamp at that byte of the profile's codec
//                   report: (acc / divisor) mod 2^width, acc the design
//                   note's accumulator (mechanism 1) over the states' stamps
//                   and the readings of the clock each controller's encoder
//                   reads (ScriptedClock). A stamp that runs backward adds
//                   one nominal interval, the profile's idleFrameIntervalMs,
//                   which the rule states. A stamp of 0 after the switch to
//                   the consumer's stamps adds the clock's time and moves
//                   the last stamp on by as much. Counts stamp
// A CRC field is recomputed over the bytes it covers, and counts crc. An
// output the rules do not give counts other: a lane claims those outputs
// by number, and only its entry's digest states their bytes.
//
// The base dump must be the lane's base: each base stream must equal the
// last pin before the lane in the list, or the golden file's. A stream a
// later lane pinned since is not read from the tree, whose bytes are the
// later lane's: the lane's bytes are rebuilt from the base by its rules
// and held to its own pin, so the lane's --explain re-runs at any later
// tip. An entry that claims other outputs keeps them as its own tip's run
// held them.
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

    /// <summary>The rules the lane states.</summary>
    public static ExplainRules Active { get; set; } = new();

    /// <summary>The base's arithmetic: the rules of every lane before this
    /// one, in the list's lane order, each later lane's over the earlier's.
    /// A field's base bytes are checked against these.</summary>
    public static ExplainRules Base { get; set; } = new();

    /// <summary>The tree's arithmetic: the base's with the lane's rules
    /// over them.</summary>
    public static ExplainRules Now { get; set; } = new();

    /// <summary><paramref name="over"/>'s rules on top of
    /// <paramref name="under"/>'s.</summary>
    public static ExplainRules Merge(ExplainRules under, ExplainRules over)
    {
        var r = new ExplainRules
        {
            Round = under.Round || over.Round,
            Imu = under.Imu || over.Imu,
            Rest = under.Rest || over.Rest,
            Nan = over.Nan ?? under.Nan,
        };
        foreach (var (k, v) in under.Centers) r.Centers[k] = v;
        foreach (var (k, v) in over.Centers) r.Centers[k] = v;
        foreach (var (k, v) in under.Stamps) r.Stamps[k] = v;
        foreach (var (k, v) in over.Stamps) r.Stamps[k] = v;
        return r;
    }

    /// <summary>Sets the three rule sets for <paramref name="lane"/>.</summary>
    public static void For(ChangeList list, string lane)
    {
        var before = new ExplainRules();
        int order = list.LaneOrder(lane);
        foreach (var earlier in list.Lanes.Where(l => l != lane && list.LaneOrder(l) < order))
            before = Merge(before, From(list, earlier));
        Active = From(list, lane);
        Base = before;
        Now = Merge(before, Active);
    }

    /// <summary>Back to no rules.</summary>
    public static void Clear()
    {
        Active = new ExplainRules();
        Base = new ExplainRules();
        Now = new ExplainRules();
    }

    public override string ToString()
    {
        var names = new List<string>();
        if (Round) names.Add("round");
        if (Imu) names.Add("imu");
        if (Rest) names.Add("rest");
        if (Nan != null) names.Add($"nan to={Nan}");
        foreach (var (p, c) in Centers) names.Add($"centered {p} {string.Join(' ', c.Select(kv => $"{kv.Key}={kv.Value}"))}");
        foreach (var (p, s) in Stamps) names.Add($"stamp {p} byte={s.Byte} width={s.Width} divisor={s.Divisor} interval-ms={s.IntervalMs}");
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
                    Only("profile", "byte", "width", "divisor", "interval-ms");
                    int width = ParseInt(Need("width"));
                    if (width is not (16 or 32)) throw new InvalidDataException($"{e.Source}: rule stamp width= takes 16 or 32");
                    uint divisor = (uint)ParseInt(Need("divisor"));
                    if (divisor == 0) throw new InvalidDataException($"{e.Source}: rule stamp divisor= is at least 1");
                    int interval = ParseInt(Need("interval-ms"));
                    if (interval < 1) throw new InvalidDataException($"{e.Source}: rule stamp interval-ms= is at least 1");
                    r.Stamps[Need("profile")] = new StampRule(ParseInt(Need("byte")), width, divisor, interval);
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

internal sealed record StampRule(int Byte, int Width, uint Divisor, int IntervalMs);

/// <summary>The base dump of one case read in step with the tree's outputs,
/// each output recomputed by the lane's rules, and each stream held twice
/// against the change list: the base's bytes against what the list pinned
/// before the lane, and the lane's counts against its own entry.</summary>
internal sealed class ExplainSession
{
    /// <summary>The change list and the lane whose entries --explain holds
    /// each moved stream to.</summary>
    public static ChangeList? Changes { get; set; }
    public static string Lane { get; set; } = "";

    /// <summary>--earlier: the lane is re-run at a later tip, so a stream a
    /// later lane of the list pinned since is rebuilt rather than read.
    /// Without it every stream is read from the tree.</summary>
    public static bool Earlier { get; set; }

    /// <summary>The golden file's count and digest of each stream of the
    /// case, for a stream no entry before the lane pins.</summary>
    public static IReadOnlyDictionary<string, (long Count, string Digest)> Golden { get; set; } =
        new Dictionary<string, (long, string)>();

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

    // The base stream as read, hashed as the golden file hashes a stream.
    GoldenStream? _baseStream;
    // A lane after this one pinned the stream: the tree's bytes are that
    // lane's, and this lane's are rebuilt from the base by its rules.
    bool _later;
    GoldenStream? _rebuilt;
    string? _rebuildBroken;

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
        _baseStream = new GoldenStream(name);
        var chain = Changes?.Chain($"{_case}/{name}") ?? new List<ChangeList.Entry>();
        int order = Changes?.LaneOrder(Lane) ?? int.MaxValue;
        _later = Earlier && chain.Any(e => e.Lane != Lane && Changes!.LaneOrder(e.Lane) > order);
        _rebuilt = _later ? new GoldenStream(name) : null;
        _rebuildBroken = null;
    }

    public void Out(string tag, ReadOnlySpan<byte> data, IExplainer? explainer, ExplainInput input)
    {
        int kind = _base.ReadByte();
        if (kind != 'O') { Fail($"{_stream}: output {tag}, the base has record {(char)kind} here"); throw new InvalidDataException("The replay and the base dump fell out of step."); }
        string baseTag = Encoding.ASCII.GetString(_base.ReadBytes(_base.ReadByte()));
        byte[] before = _base.ReadBytes(checked((int)_base.ReadUInt32()));
        _baseStream!.Add(baseTag, before);
        bool rules = ExplainRules.Active.Any;
        if (_later)
        {
            Rebuild(baseTag, before, rules ? explainer : null, input);
            return;
        }
        bool same = baseTag == tag && data.SequenceEqual(before);
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

    /// <summary>This lane's output rebuilt from the base by its rules, for a
    /// stream a later lane pinned since. The tree's bytes are the later
    /// lane's and are not read.</summary>
    void Rebuild(string tag, byte[] before, IExplainer? explainer, ExplainInput input)
    {
        byte[] expected = before;
        if (explainer != null)
        {
            expected = (byte[])before.Clone();
            _tally.Reset();
            explainer.Expect(tag, before, expected, input, _tally);
            if (_tally.Raw) _raw++;
            if (_tally.Why != null)
            {
                _rebuildBroken ??= $"{tag}: {_tally.Why}";
                expected = before;
            }
            else if (!expected.AsSpan().SequenceEqual(before))
            {
                _changed++;
                foreach (var c in _tally.Cats) _categories[c] = _categories.GetValueOrDefault(c) + 1;
            }
            else _same++;
        }
        else _same++;
        _rebuilt!.Add(tag, expected);
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

    /// <summary>The pin a stream had before this lane: the last entry of a
    /// lane before it in the list, or the golden file's.</summary>
    (long Count, string Digest, string Where) PinBefore(List<ChangeList.Entry> chain)
    {
        int order = Changes?.LaneOrder(Lane) ?? int.MaxValue;
        var e = chain.LastOrDefault(x => x.Lane != Lane && Changes!.LaneOrder(x.Lane) < order);
        if (e != null) return (e.Count, e.Digest, $"{e.Source} (lane {e.Lane})");
        var (n, d) = Golden.TryGetValue(_stream, out var g) ? g : (0, "none");
        return (n, d, "the golden file");
    }

    /// <summary>Holds the stream to the list. Its base must be what the list
    /// pinned before this lane, so a dump of another tree fails at its
    /// first moved stream. A stream that moved against the base, or that
    /// the rules move and the tree does not, needs an entry of this lane
    /// whose categories equal the counts, "other" counting the outputs the
    /// rules do not give. Where a later lane pinned the stream since, this
    /// lane's entry, or the pin before it, must be what its rules rebuild
    /// from the base, unless the entry claims outputs no rule gives, whose
    /// bytes its own tip's run held.</summary>
    void Flush()
    {
        if (_stream.Length == 0) return;
        string where = $"{_case}/{_stream}";
        var chain = Changes?.Chain(where) ?? new List<ChangeList.Entry>();
        var own = chain.FirstOrDefault(e => e.Lane == Lane);
        var before = PinBefore(chain);
        if (_baseStream!.Count != before.Count || _baseStream.Digest != before.Digest)
        {
            Lines.Add($"  [FAIL] {where}: the base dump holds count {_baseStream.Count} digest {Short(_baseStream.Digest)}, " +
                      $"and {before.Where} pins count {before.Count} digest {Short(before.Digest)} before lane {Lane}: the dump is not this lane's base");
            Failures++;
        }
        var counts = new SortedDictionary<string, long>(_categories, StringComparer.Ordinal);
        if (_unexplainedCount > 0) counts["other"] = _unexplainedCount;
        string tally = $"{_same} identical, {_changed} changed ({string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}"))})"
                     + (_raw == 0 ? "" : $", {_raw} carrying raw bytes");
        if (_later)
        {
            // The lane's bytes, rebuilt.
            var pin = own != null ? (own.Count, own.Digest) : (before.Count, before.Digest);
            var claimed = own?.Categories ?? new SortedDictionary<string, long>(StringComparer.Ordinal);
            string? problem = null;
            string verdict = "REBUILT";
            if (claimed.ContainsKey("other")) verdict = "KEPT";
            else if (_rebuildBroken != null)
                problem = $"the rules cannot rebuild lane {Lane}'s output, {_rebuildBroken}";
            else if (_rebuilt!.Count != pin.Item1 || _rebuilt.Digest != pin.Item2)
                problem = $"the rules rebuild count {_rebuilt.Count} digest {Short(_rebuilt.Digest)}, and lane {Lane} pins count {pin.Item1} digest {Short(pin.Item2)}";
            else if (!counts.SequenceEqual(claimed))
                problem = $"{own?.Source ?? "no entry"} claims {(claimed.Count == 0 ? "no categories" : string.Join(" ", claimed.Select(kv => $"{kv.Key}={kv.Value}")))}";
            if (problem != null) { verdict = "FAIL"; Failures++; }
            if (problem != null || own != null)
                Lines.Add($"  [{verdict}] {where}: pinned by a later lane, lane {Lane}'s output rebuilt from the base: {tally}" +
                          (problem != null ? $". {problem}" : verdict == "KEPT" ? $". {own!.Source} claims outputs no rule gives, held at lane {Lane}'s own tip" : ""));
        }
        else if (_changed > 0 || _unexplainedCount > 0)
        {
            Results[_stream] = new StreamResult(_same, _changed, counts);
            string? problem = null;
            var last = chain.LastOrDefault();
            if (last != null && last != own && Changes!.LaneOrder(last.Lane) > Changes.LaneOrder(Lane))
                problem = $"its last entry, {last.Source}, is lane {last.Lane}'s, which comes after lane {Lane}. --earlier re-runs lane {Lane} at a later tip";
            else if (own == null)
                problem = chain.Count == 0 ? "no entry names it" : $"no entry of lane {Lane} names it";
            else if (!counts.SequenceEqual(own.Categories))
                problem = $"{own.Source} claims {(own.Fields.Count == 0 ? "no categories" : string.Join(" ", own.Fields.Select(kv => $"{kv.Key}={kv.Value}")))}";
            string verdict = problem != null ? "FAIL" : _unexplainedCount == 0 ? "EXPLAINED" : "CLAIMED";
            if (problem != null) Failures++;
            Lines.Add($"  [{verdict}] {where}: {tally}{(problem == null ? "" : $". {problem}")}");
            foreach (var u in _unexplained) Lines.Add("      " + u);
        }
        _same = _changed = _unexplainedCount = _raw = 0;
        _categories.Clear();
        _unexplained.Clear();
    }

    static string Short(string digest) => digest.Length > 16 ? digest[..16] : digest;

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

    /// <summary>One linear site under a rule set: decision (a) on [min,
    /// max] for a number, the nan rule for NaN, and <paramref name="old"/>,
    /// master's arithmetic, where no rule applies. Called with the base's
    /// rules for the value the base must hold, and with the tree's for the
    /// value it must give.</summary>
    public static long Site(ExplainRules r, double v, long min, long max, bool invert, double rest, long old, out string? cat)
    {
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
    public static short Motion(ExplainRules r, float v, double n, double d, short old, out string? cat)
    {
        if (float.IsNaN(v)) { cat = "nan"; return r.Nan != null ? (short)0 : old; }
        cat = "imu";
        return r.Imu ? Scaled(v, n, d) : old;
    }

    /// <summary>An 8-bit trigger's digital bit under the lane's rules.</summary>
    public static bool Digital(ExplainRules r, double v, bool old, out string? cat)
    {
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
/// <summary>The accumulator under the base's stamp rule and under the
/// tree's.</summary>
internal readonly record struct StampAcc(ulong Base, ulong Now);

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
    readonly Dictionary<HMAxis, int>? _centersBase, _centersNow;
    readonly HashSet<int> _overlay = new();
    readonly HMAxis _canonLt, _canonRt;
    readonly List<(HMAxis Key, HidReportBuilder.InputField Field)> _axes;

    public DescriptorPart(ControllerProfile p, HidReportBuilder b)
    {
        _b = b;
        // The tree's sticks, read by name: absent at the base.
        _sticks = b.GetType().GetField("_stickAxes", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(b) is IEnumerable<HMAxis> s
            ? new HashSet<HMAxis>(s) : new HashSet<HMAxis>();
        ExplainRules.Base.Centers.TryGetValue(p.Id, out _centersBase);
        ExplainRules.Now.Centers.TryGetValue(p.Id, out _centersNow);
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
        var axes = s.Axes;
        // Each field's final write: its key, whether the state wrote it and
        // with what, its unsigned default, whether it is a stick, and for a
        // field the combined-Z synthesis writes, which one.
        var plan = new Dictionary<HidReportBuilder.InputField, (HMAxis Key, bool Written, double Raw, double Def, bool Stick, Synth Synth)>(ReferenceEqualityComparer.Instance);
        double lt = 0, rt = 0;
        foreach (var (key, f) in _axes)
        {
            float raw = 0f;
            bool written = axes != null && axes.TryGetValue(key, out raw);
            plan[f] = (key, written, written ? Math.Clamp(raw, 0f, 1f) : 0, f.LogicalMin < 0 ? 0.5 : 0.0, _sticks.Contains(key), Synth.None);
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
            lt = Trigger(_canonLt, lk);
            rt = Trigger(_canonRt, rk);
            plan[_b.CombinedTrigger] = (HMAxis.Z, true, 0, 0.5, false, Synth.Combined);
            plan[_b.LeftTrigger] = (lk, true, lt, 0.0, false, Synth.Left);
            plan[_b.RightTrigger] = (rk, true, rt, 0.0, false, Synth.Right);
        }
        foreach (var (f, w) in plan)
        {
            int at = f.BitOffset + idOffset;
            // A byte the profile's inputDefaults overlays keeps its value.
            bool overlaid = false;
            for (int k = at >> 3; k <= (at + f.BitSize - 1) >> 3 && !overlaid; k++) overlaid = _overlay.Contains(k);
            if (overlaid) continue;
            // The field's input under a rule set: the state's value, an
            // unwritten stick's rest, or the combined axis of the two
            // triggers, a NaN trigger at its rest under rule nan to=rest.
            double Input(ExplainRules r)
            {
                if (w.Synth == Synth.Combined)
                {
                    double l = r.Nan == "rest" && double.IsNaN(lt) ? 0.0 : lt;
                    double g = r.Nan == "rest" && double.IsNaN(rt) ? 0.0 : rt;
                    return Math.Clamp(0.5 + (g - l) * 0.5, 0.0, 1.0);
                }
                return w.Written ? w.Raw : r.Rest && w.Stick ? 0.5 : w.Def;
            }
            double RestOf(ExplainRules r) => w.Synth == Synth.Combined ? 0.5 : w.Synth != Synth.None ? 0.0 : r.Rest && w.Stick ? 0.5 : w.Def;
            Dictionary<HMAxis, int>? Centers(ExplainRules r) => ReferenceEquals(r, ExplainRules.Base) ? _centersBase : _centersNow;
            long Value(ExplainRules r, double v)
            {
                int c = 0;
                bool centered = w.Synth == Synth.None && Centers(r) is { } cs && cs.TryGetValue(w.Key, out c);
                if (double.IsNaN(v))
                    return r.Nan switch
                    {
                        "rest" => centered ? Arith.Centered(RestOf(r), f.LogicalMin, c, f.LogicalMax, false) : Arith.Linear(RestOf(r), f.LogicalMin, f.LogicalMax, false),
                        "nearest-zero" => Arith.NearestZero(f.LogicalMin, f.LogicalMax),
                        _ => Old.Descriptor(v, f),
                    };
                if (centered) return Arith.Centered(v, f.LogicalMin, c, f.LogicalMax, false);
                return r.Round ? Arith.Linear(v, f.LogicalMin, f.LogicalMax, false) : Old.Descriptor(v, f);
            }
            double vBase = Input(ExplainRules.Base), vNow = Input(ExplainRules.Now);
            long old = Value(ExplainRules.Base, vBase);
            long now = Value(ExplainRules.Now, vNow);
            // A field whose input was NaN counts as nan whatever the rule
            // made of it: the combined axis of a NaN trigger under rule nan
            // to=rest carries the other trigger, a number.
            bool nan = w.Synth == Synth.Combined ? double.IsNaN(lt) || double.IsNaN(rt) : double.IsNaN(Input(new ExplainRules()));
            bool centeredNow = w.Synth == Synth.None && _centersNow != null && _centersNow.ContainsKey(w.Key);
            string cat = !w.Written && vNow != vBase ? "rest" : nan ? "nan" : centeredNow ? "centered" : "round";
            Bits.Field(before, expected, at, f.BitSize, old, now, cat, t, $"descriptor axis {w.Key}", vBase);
        }
    }

    enum Synth { None, Combined, Left, Right }
}

/// <summary>A report the codec encodes from an extendedReport: each
/// normalized field of the tree's compiled program at its base range, the
/// CRC over the result, and the stamp rule's field.</summary>
internal sealed class CodecPart
{
    readonly VendorBlobProgram _prog;
    readonly ExtendedReportSpec _spec;
    readonly StampRule? _stampBase, _stampNow;

    public CodecPart(ExtendedReportSpec spec, string profileId, bool stamped)
    {
        _spec = spec;
        _prog = VendorBlobProgram.Get(spec);
        if (stamped)
        {
            ExplainRules.Base.Stamps.TryGetValue(profileId, out _stampBase);
            ExplainRules.Now.Stamps.TryGetValue(profileId, out _stampNow);
        }
    }

    public int Size => _spec.Size;

    /// <summary>The report at <paramref name="at"/> in the buffers, its
    /// fields limited to bytes [lo, hi] when given (a port block's data
    /// bytes).</summary>
    public void Expect(byte[] before, byte[] expected, int at, in HMGamepadState s, float lx, float ly, float rx, float ry,
                       float lt, float rt, StampAcc acc, Tally t, int lo = 0, int hi = int.MaxValue)
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
                    long o = Old.U8Axis(v, f.Center, f.Invert);
                    int center = f.Center;
                    bool inv = f.Invert;
                    long Under(ExplainRules r, out string? c) => center == 128
                        ? Arith.Site(r, v, 0, 255, inv, 0.5, o, out c)
                        : Math.Clamp(Arith.Site(r, v, center - 127, center + 127, inv, 0.5, o, out c), 0, 255);
                    long old = Under(ExplainRules.Base, out _);
                    long now = Under(ExplainRules.Now, out var cat);
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
                    long o = Old.U8Trigger(v);
                    long old = Arith.Site(ExplainRules.Base, v, 0, 255, false, 0.0, o, out _);
                    long now = Arith.Site(ExplainRules.Now, v, 0, 255, false, 0.0, o, out var cat);
                    Bits.Field(before, expected, bit, 8, old, now, cat, t, "codec uint8-trigger", v);
                    break;
                }
                case VendorBlobProgram.FieldOp.Stick12Pair:
                {
                    bool right = f.Source == VendorBlobProgram.SrcOp.RightStick;
                    float vx = right ? rx : lx, vy = right ? ry : ly;
                    long ox = Arith.Site(ExplainRules.Base, vx, 0, 4095, false, 0.5, Old.Stick12(vx), out _);
                    long oy = Arith.Site(ExplainRules.Base, vy, 0, 4095, false, 0.5, Old.Stick12(vy), out _);
                    long nx = Arith.Site(ExplainRules.Now, vx, 0, 4095, false, 0.5, Old.Stick12(vx), out var cx);
                    long ny = Arith.Site(ExplainRules.Now, vy, 0, 4095, false, 0.5, Old.Stick12(vy), out var cy);
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
                    long o = Old.I16Axis(sv, y);
                    long old = Arith.Site(ExplainRules.Base, sv, -32767, 32767, y, 0.5, o, out _);
                    long now = Arith.Site(ExplainRules.Now, sv, -32767, 32767, y, 0.5, o, out var cat);
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
                        long o = Old.I16Axis(sv, isY);
                        long old = Arith.Site(ExplainRules.Base, sv, -32767, 32767, isY, 0.5, o, out _);
                        long now = Arith.Site(ExplainRules.Now, sv, -32767, 32767, isY, 0.5, o, out var cat);
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
                    long o = Old.U16Trigger(tv);
                    long old = Arith.Site(ExplainRules.Base, tv, 0, 32767, false, 0.0, o, out _);
                    long now = Arith.Site(ExplainRules.Now, tv, 0, 32767, false, 0.0, o, out var cat);
                    Bits.Field(before, expected, bit, 16, old, now, cat, t, "codec uint16-trigger", tv);
                    break;
                }
                case VendorBlobProgram.FieldOp.ButtonMask when f.ButtonBits != null:
                    for (int i = 0; i < f.ButtonBits.Length && f.BitLo + i <= f.BitHi; i++)
                    {
                        ulong b = f.ButtonBits[i];
                        if (b != VendorBlobProgram.ButtonLtDigital && b != VendorBlobProgram.ButtonRtDigital) continue;
                        float v = b == VendorBlobProgram.ButtonLtDigital ? lt : rt;
                        bool o = Old.TriggerByte(v) > 0;
                        bool old = Arith.Digital(ExplainRules.Base, v, o, out _);
                        bool now = Arith.Digital(ExplainRules.Now, v, o, out var cat);
                        Bits.Field(before, expected, (at + f.B) * 8 + f.BitLo + i, 1, old ? 1 : 0, now ? 1 : 0, cat, t, "codec trigger bit", v);
                    }
                    break;
                case VendorBlobProgram.FieldOp.Crc32:
                    crcs.Add(fi);
                    break;
            }
        }
        static long Field(StampRule r, ulong acc) => (long)(r.Width == 32 ? acc / r.Divisor % (1UL << 32) : acc / r.Divisor % (1UL << 16));
        if (_stampNow != null)
        {
            // A base whose own lane accumulated holds its stamp, which is
            // checked. Otherwise the field held something else at the base,
            // and the rule replaces it.
            if (_stampBase != null && _stampBase.Byte == _stampNow.Byte && _stampBase.Width == _stampNow.Width)
                Bits.Field(before, expected, (at + _stampNow.Byte) * 8, _stampNow.Width, Field(_stampBase, acc.Base), Field(_stampNow, acc.Now), "stamp", t, "codec stamp", 0);
            else
                Bits.Replace(before, expected, (at + _stampNow.Byte) * 8, _stampNow.Width, Field(_stampNow, acc.Now), "stamp", t);
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
                short o = Old.Imu(values[i], scale);
                short old = Arith.Motion(ExplainRules.Base, values[i], n, d, o, out _);
                short now = Arith.Motion(ExplainRules.Now, values[i], n, d, o, out var cat);
                Bits.Field(before, expected, (at + 12 + frame * 12 + i * 2) * 8, 16, (ushort)old, (ushort)now, cat, t, "switch-pro motion", values[i]);
            }
    }

    static void Stick(byte[] before, byte[] expected, int bit, double v, bool invert, Tally t)
    {
        long o = Old.SwitchStick(v, invert);
        long old = Arith.Site(ExplainRules.Base, v, 0x200, 0xE00, invert, 0.5, o, out _);
        long now = Arith.Site(ExplainRules.Now, v, 0x200, 0xE00, invert, 0.5, o, out var cat);
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
        bool o = Old.Switch2TriggerByte(v) > 0;
        bool old = Arith.Digital(ExplainRules.Base, v, o, out _);
        bool now = Arith.Digital(ExplainRules.Now, v, o, out var cat);
        Bits.Field(before, expected, at * 8 + System.Numerics.BitOperations.TrailingZeroCount(mask), 1, old ? 1 : 0, now ? 1 : 0, cat, t, "switch2 trigger bit", v);
    }

    static void Stick(byte[] before, byte[] expected, int at, double v, bool invert, Tally t)
    {
        long o = Old.Switch2Stick(v, invert);
        long old = Arith.Site(ExplainRules.Base, v, 0, 4095, invert, 0.5, o, out _);
        long now = Arith.Site(ExplainRules.Now, v, 0, 4095, invert, 0.5, o, out var cat);
        Bits.Field(before, expected, at * 8, 16, old, now, cat, t, "switch2 stick", v);
    }

    static void Motion(byte[] before, byte[] expected, int at, float v, float oldScale, double n, double d, Tally t)
    {
        short o = Old.Imu(v, oldScale);
        short old = Arith.Motion(ExplainRules.Base, v, n, d, o, out _);
        short now = Arith.Motion(ExplainRules.Now, v, n, d, o, out var cat);
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
        long o = Old.GipStick(v);
        long old = Arith.Site(ExplainRules.Base, v, 0, 65535, false, 0.5, o, out _);
        long now = Arith.Site(ExplainRules.Now, v, 0, 65535, false, 0.5, o, out var cat);
        Bits.Field(before, expected, at * 8, 16, old, now, cat, t, "xusb thumb x", v);
    }

    /// <summary>XInput's sThumbY grows upward: sThumbY is the inverted field
    /// on [-32768, 32767], and the slice carries 32767 - sThumbY.</summary>
    static void ThumbY(byte[] before, byte[] expected, int at, double v, Tally t)
    {
        long o = Old.GipStick(v);
        long old = 32767 - Arith.Site(ExplainRules.Base, v, -32768, 32767, true, 0.5, 32767 - o, out _);
        long thumb = Arith.Site(ExplainRules.Now, v, -32768, 32767, true, 0.5, 32767 - o, out var cat);
        Bits.Field(before, expected, at * 8, 16, old, 32767 - thumb, cat, t, "xusb thumb y", v);
    }

    static void Trigger(byte[] before, byte[] expected, int at, double v, Tally t)
    {
        long o = Old.GipTrigger(v);
        long Under(ExplainRules r, out string c)
        {
            if (double.IsNaN(v)) { c = "nan"; return r.Nan != null ? 0 : o; }
            c = "xusb";
            return r.Round ? Arith.XusbG(Arith.Linear(v, 0, 255, false)) : o;
        }
        long old = Under(ExplainRules.Base, out _);
        long now = Under(ExplainRules.Now, out var cat);
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
    readonly Accumulator _accBase = new(), _accNow = new();
    readonly uint _nominalBase, _nominalNow;
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
        }
        _clock = ScriptedClock.Of(typeof(HMController).GetField("_extEncoderState", Instance)?.GetValue(c));
        // The nominal interval is the stamp rule's, not the tree's profile.
        if (ExplainRules.Base.Stamps.TryGetValue(P.Id, out var stampBase))
            _nominalBase = (uint)Math.Clamp((long)stampBase.IntervalMs * 3000, 0, uint.MaxValue);
        if (ExplainRules.Now.Stamps.TryGetValue(P.Id, out var stampNow))
            _nominalNow = (uint)Math.Clamp((long)stampNow.IntervalMs * 3000, 0, uint.MaxValue);
    }

    public bool Armed => s_armed?.GetValue(C) is true;

    /// <summary>Advances the accumulator over each clock read since the last
    /// output, one read per encode, with the state's stamp, and returns
    /// it.</summary>
    public StampAcc Stamp(in HMGamepadState s)
    {
        if (_clock != null)
            while (_clock.TryTake(out long now))
            {
                _accBase.Advance(s.SensorTimestamp, now, ScriptedClock.Frequency, _nominalBase);
                _accNow.Advance(s.SensorTimestamp, now, ScriptedClock.Frequency, _nominalNow);
            }
        return new StampAcc(_accBase.Value, _accNow.Value);
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
        StampAcc acc = input.Set ? _c.Stamp(in input.State) : default;
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
               float lx, float ly, float rx, float ry, float lt, float rt, StampAcc acc, Tally t)
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
                    if (_c.Ports.StatusByte > lo) _c.PortBlock.Expect(before, expected, block, in s, lx, ly, rx, ry, lt, rt, default, t, lo, _c.Ports.StatusByte - 1);
                    if (_c.Ports.StatusByte < hi) _c.PortBlock.Expect(before, expected, block, in s, lx, ly, rx, ry, lt, rt, default, t, _c.Ports.StatusByte + 1, hi);
                }
                else _c.PortBlock.Expect(before, expected, block, in s, lx, ly, rx, ry, lt, rt, default, t, lo, hi);
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
        StampAcc acc = input.Set ? _c.Stamp(in input.State) : default;
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
