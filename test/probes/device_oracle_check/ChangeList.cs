// The intended changes a replay accepts, each with the lane that made it
// and its reason. Replay reads changes.txt beside this source when it
// exists, and every file --changes names. One change a line, # starts a
// comment:
//
//   stream <case>/<stream> count <n> digest <sha-256> | <lane> | <reason>
//   config <profile-id> <value name> absent|<registry type>:<hex> | <lane> | <reason>
//
// A stream entry pins what the tree must now produce: the stream's output
// count and the SHA-256 of its outputs (GoldenStream.Digest), or count 0
// and digest none for a stream the tree no longer records. A stream that
// differs from its golden file passes only when the last entry naming it
// pins its count and digest, and is printed as [CHANGED] with that entry's
// lane and reason. A later lane that moves the stream again adds its own
// entry, and the earlier ones stay as its history.
//
// A config entry is one --plan-change line for case 5: a value the tree's
// SDK writes otherwise than the kept plan holds. An entry that matches
// nothing in a case the replay ran fails the replay, so the list names
// exactly the changes the tree makes and no more.
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

        public string Case => Target.Split('/')[0];
    }

    public readonly List<Entry> Entries = new();

    static readonly Regex StreamHead = new(
        @"^stream (?<t>[^ *]+/[^ *]+) count (?<n>\d+) digest (?<d>[0-9A-F]{64}|none)$", RegexOptions.CultureInvariant);

    /// <summary>The changes of every file given, in order. A line that is
    /// not one of the two forms, or whose lane or reason is empty, fails,
    /// naming the file and line.</summary>
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
                }
                else if (head.StartsWith("config ", StringComparison.Ordinal))
                {
                    e.Kind = "config";
                    e.Target = head["config ".Length..];
                }
                else
                    throw new InvalidDataException(
                        $"{where}: not \"stream <case>/<stream> count <n> digest <sha-256>\" or \"config ...\"");
                list.Entries.Add(e);
            }
        }
        return list;
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

    /// <summary>The config entries as --plan-change lines.</summary>
    public IEnumerable<Entry> Config => Entries.Where(e => e.Kind == "config");
}
