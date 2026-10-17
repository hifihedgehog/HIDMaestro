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
// entry, and the earlier ones stay as its history.
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

        /// <summary>Set by --explain for an entry whose categories it held
        /// the stream to (ExplainSession.Flush).</summary>
        public bool Explained;

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
        return list;
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
/// stream that differs from its golden file, or under --explain from the
/// base, pinned to what the replay gave. The last entry of a stream takes
/// the new count and digest, and under --explain the categories counted
/// against the base. A stream the lane moved against its base, whose last
/// entry is another lane's, gets a new entry of the lane after it. A stream
/// no entry names gets a new one. Each new entry's reason is "?", which the
/// list refuses until it is filled in. An entry whose stream now equals both
/// its golden file and its base is left out as a comment.</summary>
internal static class ChangeWriter
{
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
        foreach (var e in changes.Entries)
        {
            if (e.Kind != "stream" || !ran.Contains(e.Case)) { lines.Add(e.Line()); continue; }
            explained.TryGetValue(e.Target, out var x);
            if (!moved.ContainsKey(e.Target) && x == null) { lines.Add("# the stream now equals its golden file and its base: " + e.Line()); continue; }
            if (!ReferenceEquals(last[e.Target], e)) { lines.Add(e.Line()); continue; }
            done.Add(e.Target);
            var pin = pins[e.Target];
            if (x != null && lane != null && e.Lane != lane)
            {
                lines.Add(e.Line());
                lines.AddRange(New(e.Target, pin, x, lane));
                continue;
            }
            var copy = new ChangeList.Entry { Kind = "stream", Target = e.Target, Lane = e.Lane, Reason = e.Reason, Count = pin.Count, Digest = pin.Digest };
            if (x == null) foreach (var kv in e.Fields) copy.Fields[kv.Key] = kv.Value;
            else foreach (var kv in x.Categories) copy.Fields[kv.Key] = kv.Value.ToString();
            lines.Add(copy.Line());
        }
        foreach (var target in moved.Keys.Concat(explained.Keys))
        {
            if (!done.Add(target)) continue;
            explained.TryGetValue(target, out var x);
            lines.AddRange(New(target, pins[target], x, lane ?? "?"));
        }
        File.WriteAllLines(path, lines);
    }

    static IEnumerable<string> New(string target, (long Count, string Digest) pin, ExplainSession.StreamResult? x, string lane)
    {
        var e = new ChangeList.Entry { Kind = "stream", Target = target, Lane = lane, Reason = "?", Count = pin.Count, Digest = pin.Digest };
        if (x != null) foreach (var kv in x.Categories) e.Fields[kv.Key] = kv.Value.ToString();
        yield return e.Line();
    }
}
