// The intended changes a replay accepts, each with the lane that made it
// and its reason. Replay reads changes.txt beside this source when it
// exists, and every file --changes names. One change a line, # starts a
// comment:
//
//   stream <case>/<stream> count <n> digest <sha-256> [<category>=<n> ...] | <lane> | <reason>
//   config <profile-id> <value name> absent|<registry type>:<hex> | <lane> | <reason>
//   rule <name> [<key>=<value> ...] | <lane> | <reason>
//
// A stream entry pins what the tree must now produce: the stream's output
// count and the SHA-256 of its outputs (GoldenStream.Digest), or count 0
// and digest none for a stream the tree no longer records. A stream that
// differs from its golden file passes only when the last entry naming it
// pins its count and digest, and is printed as [CHANGED] with that entry's
// lane and reason. A later lane that moves the stream again adds its own
// entry after the earlier ones, which stay as the stream's chain: each
// lane names a stream once, entries follow the order of the lanes, which
// is the order of each lane's first stream entry in the list, and each
// entry pins other bytes than the one before it. A stream back at its
// golden bytes is pinned so by its last entry. Load refuses a chain that
// breaks these.
//
// The categories are what replay --explain counted for the stream against
// the lane's base (Explain.cs), as fields, one per category with a nonzero
// count, "other" for the outputs no rule of the lane gives. --explain
// requires them to equal its counts exactly. An entry from before a lane
// ran --explain, or one --explain does not reach (a stream that equals its
// base), may carry none.
//
// A rule entry states a rule-based change of its lane, which --explain
// --lane <lane> recomputes output by output (Explain.cs names the rules and
// their keys). A config entry is one --plan-change line for case 5: a value
// the tree's SDK writes otherwise than the kept plan holds. An entry that
// matches nothing in a case the replay ran fails the replay, so the list
// names exactly the changes the tree makes and no more.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

internal sealed class ChangeList
{
    public sealed class Entry
    {
        public string Kind = "", Target = "", Lane = "", Reason = "", Source = "";
        public bool Used;

        /// <summary>A stream entry's pins: the output count and the
        /// digest, "none" for a stream the tree no longer records.</summary>
        public long Count;
        public string Digest = "";

        /// <summary>A stream entry's categories, or a rule entry's keys.</summary>
        public readonly SortedDictionary<string, string> Fields = new(StringComparer.Ordinal);

        public string Case => Target.Split('/')[0];

        /// <summary>The categories as counts.</summary>
        public SortedDictionary<string, long> Categories =>
            new(Fields.ToDictionary(kv => kv.Key, kv => long.Parse(kv.Value)), StringComparer.Ordinal);

        /// <summary>The line as the file carries it.</summary>
        public string Line()
        {
            string fields = string.Concat(Fields.Select(kv => $" {kv.Key}={kv.Value}"));
            string head = Kind switch
            {
                "stream" => $"stream {Target} count {Count} digest {Digest}{fields}",
                "rule" => $"rule {Target}{fields}",
                _ => $"{Kind} {Target}",
            };
            return $"{head} | {Lane} | {Reason}";
        }
    }

    public readonly List<Entry> Entries = new();

    static readonly Regex StreamHead = new(
        @"^stream (?<t>[^ *]+/[^ *]+) count (?<n>\d+) digest (?<d>[0-9A-F]{64}|none)(?<f>( [a-z][a-z-]*=\d+)*)$",
        RegexOptions.CultureInvariant);
    static readonly Regex RuleHead = new(
        @"^rule (?<t>[a-z][a-z-]*)(?<f>( [A-Za-z][A-Za-z0-9-]*=[^ =]+)*)$", RegexOptions.CultureInvariant);
    static readonly Regex Field = new(@" (?<k>[^ =]+)=(?<v>[^ ]+)", RegexOptions.CultureInvariant);

    /// <summary>The changes of every file given, in order. A line that is
    /// not one of the three forms, or whose lane or reason is empty or
    /// "?", fails, naming the file and line.</summary>
    public static ChangeList Load(IEnumerable<string> paths)
    {
        var list = new ChangeList();
        foreach (string path in paths)
        {
            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                string where = $"{path}:{i + 1}";
                var parts = line.Split('|').Select(p => p.Trim()).ToArray();
                if (parts.Length != 3 || parts[1].Length == 0 || parts[2].Length == 0)
                    throw new InvalidDataException($"{where}: not \"kind target | lane | reason\"");
                if (parts[1] == "?" || parts[2] == "?")
                    throw new InvalidDataException($"{where}: the lane and the reason are still to be filled in");
                string head = string.Join(' ', parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries));
                var e = new Entry { Lane = parts[1], Reason = parts[2], Source = where };
                Match m;
                if ((m = StreamHead.Match(head)).Success)
                {
                    e.Kind = "stream";
                    e.Target = m.Groups["t"].Value;
                    e.Count = long.Parse(m.Groups["n"].Value);
                    e.Digest = m.Groups["d"].Value;
                    if ((e.Digest == "none") != (e.Count == 0))
                        throw new InvalidDataException($"{where}: digest none goes with count 0 and only with it");
                    AddFields(e, m.Groups["f"].Value, where);
                }
                else if ((m = RuleHead.Match(head)).Success)
                {
                    e.Kind = "rule";
                    e.Target = m.Groups["t"].Value;
                    AddFields(e, m.Groups["f"].Value, where);
                }
                else if (head.StartsWith("config ", StringComparison.Ordinal))
                {
                    e.Kind = "config";
                    e.Target = head["config ".Length..];
                }
                else
                    throw new InvalidDataException(
                        $"{where}: not \"stream <case>/<stream> count <n> digest <sha-256> [<category>=<n> ...]\", \"config ...\" or \"rule <name> [<key>=<value> ...]\"");
                list.Entries.Add(e);
            }
        }
        list.CheckChains();
        return list;
    }

    /// <summary>A lane's place in the history of every stream: where its
    /// first stream entry stands in the list, or for a lane that moved no
    /// stream, its first line of any kind. A lane not in the list comes
    /// after every lane in it.</summary>
    public int LaneOrder(string lane)
    {
        for (int i = 0; i < Entries.Count; i++)
            if (Entries[i].Kind == "stream" && Entries[i].Lane == lane) return i;
        for (int i = 0; i < Entries.Count; i++)
            if (Entries[i].Lane == lane) return i;
        return int.MaxValue;
    }

    /// <summary>A stream's entries in order.</summary>
    public List<Entry> Chain(string target) =>
        Entries.Where(e => e.Kind == "stream" && string.Equals(e.Target, target, StringComparison.Ordinal)).ToList();

    void CheckChains()
    {
        foreach (var chain in Entries.Where(e => e.Kind == "stream").GroupBy(e => e.Target, StringComparer.Ordinal))
        {
            Entry? prev = null;
            var lanes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in chain)
            {
                if (!lanes.Add(e.Lane))
                    throw new InvalidDataException($"{e.Source}: lane {e.Lane} names {e.Target} twice. A lane re-pins its own entry in place");
                if (prev != null && LaneOrder(prev.Lane) > LaneOrder(e.Lane))
                    throw new InvalidDataException($"{e.Source}: lane {e.Lane}'s entry for {e.Target} follows lane {prev.Lane}'s ({prev.Source}), which comes later in the list");
                if (prev != null && prev.Count == e.Count && prev.Digest == e.Digest)
                    throw new InvalidDataException($"{e.Source}: pins the bytes {prev.Source} already pins for {e.Target}");
                prev = e;
            }
        }
    }

    static void AddFields(Entry e, string text, string where)
    {
        foreach (Match f in Field.Matches(text))
            if (!e.Fields.TryAdd(f.Groups["k"].Value, f.Groups["v"].Value))
                throw new InvalidDataException($"{where}: {f.Groups["k"].Value} twice");
    }

    /// <summary>The last stream entry that names this case's stream. Every
    /// entry that names it is marked used: the earlier ones are its
    /// history.</summary>
    public Entry? Stream(string caseName, string stream)
    {
        string target = $"{caseName}/{stream}";
        Entry? last = null;
        foreach (var e in Entries)
        {
            if (e.Kind != "stream" || !string.Equals(e.Target, target, StringComparison.Ordinal)) continue;
            e.Used = true;
            last = e;
        }
        return last;
    }

    /// <summary>The last stream entry naming this stream, without marking
    /// anything.</summary>
    public Entry? Peek(string caseName, string stream)
    {
        string target = $"{caseName}/{stream}";
        return Entries.LastOrDefault(e => e.Kind == "stream" && string.Equals(e.Target, target, StringComparison.Ordinal));
    }

    /// <summary>The config entries as --plan-change lines.</summary>
    public IEnumerable<Entry> Config => Entries.Where(e => e.Kind == "config");

    /// <summary>The rule entries of one lane.</summary>
    public IEnumerable<Entry> Rules(string lane) => Entries.Where(e => e.Kind == "rule" && e.Lane == lane);
}

/// <summary>replay --write-changes: the loaded list as one file, with every
/// stream of the cases that ran pinned to what the replay gave. Only the
/// running lane's own entry (--lane) is re-pinned in place, and it keeps
/// categories only from --explain's counts. Another lane's entry is never
/// changed: a stream it pins that the replay gives otherwise gets a new
/// entry, of the running lane, or of lane "?" without --lane, as does a
/// stream no entry names. New entries go at the end of the list, so a
/// lane's first stream entry follows every earlier lane's. A new entry's
/// reason is "?", which the list refuses until it is filled in.</summary>
internal static class ChangeWriter
{
    /// <param name="moved">The streams that differ from their golden
    /// files.</param>
    /// <param name="pins">What the replay gave for every stream of the
    /// cases that ran, and count 0 digest none for one it lacks.</param>
    public static void Write(string path, ChangeList changes, ISet<string> ran,
                             IReadOnlyDictionary<string, (long Count, string Digest)> moved,
                             IReadOnlyDictionary<string, (long Count, string Digest)> pins,
                             IReadOnlyDictionary<string, ExplainSession.StreamResult> explained, string? lane)
    {
        var last = new Dictionary<string, ChangeList.Entry>(StringComparer.Ordinal);
        foreach (var e in changes.Entries) if (e.Kind == "stream") last[e.Target] = e;
        var lines = new List<string>
        {
            "# Written by device_oracle_check replay --write-changes (ChangeList.cs). Fill in each \"?\".",
        };
        var done = new HashSet<string>(StringComparer.Ordinal);
        var added = new List<string>();
        foreach (var e in changes.Entries)
        {
            if (e.Kind != "stream" || !ran.Contains(e.Case) || !ReferenceEquals(last[e.Target], e)) { lines.Add(e.Line()); continue; }
            done.Add(e.Target);
            explained.TryGetValue(e.Target, out var x);
            if (!pins.TryGetValue(e.Target, out var pin)) pin = (0, "none");
            if (lane != null && e.Lane == lane)
            {
                // The running lane's own entry, left out where the stream is
                // back at the bytes the entry before it pins, or at its
                // golden file's with none before it.
                var chain = changes.Chain(e.Target);
                var before = chain.Count > 1 ? chain[^2] : null;
                if (before != null ? before.Count == pin.Count && before.Digest == pin.Digest : !moved.ContainsKey(e.Target))
                {
                    lines.Add("# the stream is back at the bytes before this lane: " + e.Line());
                    continue;
                }
                var copy = new ChangeList.Entry { Kind = "stream", Target = e.Target, Lane = e.Lane, Reason = e.Reason, Count = pin.Count, Digest = pin.Digest };
                if (x != null) foreach (var kv in x.Categories) copy.Fields[kv.Key] = kv.Value.ToString();
                lines.Add(copy.Line());
                continue;
            }
            lines.Add(e.Line());
            if (e.Count == pin.Count && e.Digest == pin.Digest) continue;
            added.AddRange(New(e.Target, pin, x, lane ?? "?"));
        }
        foreach (var target in moved.Keys.Concat(explained.Keys))
        {
            if (!done.Add(target)) continue;
            explained.TryGetValue(target, out var x);
            added.AddRange(New(target, pins[target], x, lane ?? "?"));
        }
        File.WriteAllLines(path, lines.Concat(added));
    }

    static IEnumerable<string> New(string target, (long Count, string Digest) pin, ExplainSession.StreamResult? x, string lane)
    {
        var e = new ChangeList.Entry { Kind = "stream", Target = target, Lane = lane, Reason = "?", Count = pin.Count, Digest = pin.Digest };
        if (x != null && lane != "?") foreach (var kv in x.Categories) e.Fields[kv.Key] = kv.Value.ToString();
        yield return e.Line();
    }
}
