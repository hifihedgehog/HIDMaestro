// selftest: the change list's own rules, checked on small lists and
// streams written here, with no case run and no golden file read.
//
//   writer   --write-changes never changes another lane's entry: a stream
//            it pins that the replay gives otherwise gets a new entry after
//            it, of the running lane or of lane "?", reason "?". The running
//            lane's own entry is re-pinned in place, with categories only
//            from --explain's counts.
//   chain    Load refuses a lane that names a stream twice, an entry that
//            follows a later lane's, and one that pins its predecessor's
//            bytes.
//   base     --explain refuses a base dump whose stream is not what the list
//            pinned before the lane, or the golden file where nothing did.
// Exit 0 when every check passes, 1 otherwise.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

internal static class SelfTest
{
    static int s_failures;

    static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) s_failures++;
    }

    const string A = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    const string B = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    const string C = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";

    public static int Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"hmoracle-selftest-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        try
        {
            Writer(dir);
            Chain(dir);
            Base(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
        Console.WriteLine($"device_oracle_check selftest: {(s_failures == 0 ? "PASS" : $"FAIL, {s_failures} check(s)")}");
        return s_failures == 0 ? 0 : 1;
    }

    static ChangeList List(string dir, params string[] lines)
    {
        string path = Path.Combine(dir, "list.txt");
        File.WriteAllLines(path, lines);
        return ChangeList.Load(new[] { path });
    }

    static List<string> Write(string dir, ChangeList list, string? lane, (long, string) replay,
                              ExplainSession.StreamResult? explained = null)
    {
        string path = Path.Combine(dir, "written.txt");
        var pins = new Dictionary<string, (long, string)> { ["c/s"] = replay };
        var moved = new Dictionary<string, (long, string)> { ["c/s"] = replay };
        var x = new Dictionary<string, ExplainSession.StreamResult>(StringComparer.Ordinal);
        if (explained != null) x["c/s"] = explained;
        ChangeWriter.Write(path, list, new HashSet<string> { "c" }, moved, pins, x, lane);
        return File.ReadAllLines(path).Where(l => !l.StartsWith('#')).ToList();
    }

    static void Writer(string dir)
    {
        string w3 = $"stream c/s count 1 digest {A} | W3 | an earlier lane's change";
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal) { ["round"] = 7 };

        var lines = Write(dir, List(dir, w3), lane: null, (2, B));
        Check(lines.Count == 2 && lines[0] == w3 && lines[1] == $"stream c/s count 2 digest {B} | ? | ?",
              "writer without --lane keeps lane W3's entry and adds one of lane ? after it");

        lines = Write(dir, List(dir, w3), lane: "X", (2, B));
        Check(lines.Count == 2 && lines[0] == w3 && lines[1] == $"stream c/s count 2 digest {B} | X | ?",
              "writer with --lane X and no --explain keeps lane W3's entry and adds one of lane X");

        lines = Write(dir, List(dir, w3), lane: "X", (2, B), new ExplainSession.StreamResult(0, 5, counts));
        Check(lines.Count == 2 && lines[0] == w3 && lines[1] == $"stream c/s count 2 digest {B} round=7 | X | ?",
              "writer with --explain adds lane X's entry with its counts after lane W3's");

        lines = Write(dir, List(dir, w3), lane: "X", (1, A));
        Check(lines.Count == 1 && lines[0] == w3, "writer adds nothing where lane W3's entry pins what the replay gives");

        string own = $"stream c/s count 1 digest {A} round=3 | X | the lane's own change";
        lines = Write(dir, List(dir, w3.Replace("count 1", "count 9"), own), lane: "X", (2, B));
        Check(lines.Count == 2 && lines[1] == $"stream c/s count 2 digest {B} | X | the lane's own change",
              "writer re-pins lane X's own entry in place, without the stale categories, with no --explain");

        lines = Write(dir, List(dir, w3.Replace("count 1", "count 9"), own), lane: "X", (2, B), new ExplainSession.StreamResult(0, 5, counts));
        Check(lines.Count == 2 && lines[1] == $"stream c/s count 2 digest {B} round=7 | X | the lane's own change",
              "writer re-pins lane X's own entry with --explain's counts");

        lines = Write(dir, List(dir, w3.Replace("count 1", "count 9"), own), lane: "X", (9, A));
        Check(lines.Count == 1 && lines[0] == w3.Replace("count 1", "count 9"),
              "writer leaves out lane X's own entry where the stream is back at lane W3's bytes");

        lines = Write(dir, List(dir, w3.Replace("count 1", "count 9"), own), lane: "Y", (2, B));
        Check(lines.Count == 3 && lines[1] == own && lines[2] == $"stream c/s count 2 digest {B} | Y | ?",
              "writer with --lane Y keeps lane X's entry and stacks one of lane Y on it");

        string other = $"stream d/o count 1 digest {C} | W3 | r";
        lines = Write(dir, List(dir, w3, other), lane: "X", (2, B));
        Check(lines.Count == 3 && lines[0] == w3 && lines[1] == other && lines[2] == $"stream c/s count 2 digest {B} | X | ?",
              "writer puts the new entry at the end of the list, after every earlier lane's entries");
    }

    // Streams of one case, written as a base dump and replayed against it.
    static readonly (string Tag, byte[] Data)[] B0 = { ("o", new byte[] { 1, 2 }), ("o", new byte[] { 3 }) };
    static readonly (string Tag, byte[] Data)[] T1 = { ("o", new byte[] { 1, 9 }), ("o", new byte[] { 3 }) };
    static readonly (string Tag, byte[] Data)[] T2 = { ("o", new byte[] { 7, 9 }), ("o", new byte[] { 3 }) };

    static (long Count, string Digest) Pin((string Tag, byte[] Data)[] outputs)
    {
        var s = new GoldenStream("s");
        foreach (var (t, d) in outputs) s.Add(t, d);
        return (s.Count, s.Digest);
    }

    static string Entry((string Tag, byte[] Data)[] outputs, string lane, string fields = "") =>
        $"stream c/s count {Pin(outputs).Count} digest {Pin(outputs).Digest}{fields} | {lane} | r";

    /// <summary>Writes <paramref name="base"/> as case c's dump, replays
    /// <paramref name="tree"/> against it under --explain --lane, and
    /// returns the failures.</summary>
    static int Explain(string dir, (string Tag, byte[] Data)[] @base, (string Tag, byte[] Data)[] tree,
                       (string Tag, byte[] Data)[] golden, string lane, params string[] list)
    {
        string dumps = Path.Combine(dir, "dump");
        GoldenCase.DumpDir = dumps;
        var b = new GoldenCase("c", 1, dump: true);
        b.Stream("s");
        foreach (var (t, d) in @base) b.Out(t, d);
        b.CloseDump();
        GoldenCase.DumpDir = null;
        var changes = List(dir, list);
        ExplainSession.Changes = changes;
        ExplainSession.Lane = lane;
        ExplainSession.Golden = new Dictionary<string, (long, string)> { ["s"] = Pin(golden) };
        ExplainRules.Active = ExplainRules.From(changes, lane);
        GoldenCase.ExplainDir = dumps;
        try
        {
            var g = new GoldenCase("c", 1, dump: true);
            g.Stream("s");
            foreach (var (t, d) in tree) g.Out(t, d);
            g.CloseDump();
            var ex = g.Explained!;
            foreach (var line in ex.Lines) Console.WriteLine("    " + line.Trim());
            return ex.Failures;
        }
        finally
        {
            GoldenCase.ExplainDir = null;
            ExplainRules.Active = new ExplainRules();
        }
    }

    static void Base(string dir)
    {
        Check(Explain(dir, B0, B0, golden: B0, "X") == 0, "base: a dump equal to the golden file passes");
        Check(Explain(dir, T1, T1, golden: B0, "X", Entry(T1, "X", " other=1")) > 0,
              "base: a dump of the lane's own tree fails, though the lane's entry pins its bytes");
        Check(Explain(dir, T1, T2, golden: B0, "X", Entry(T1, "W3"), Entry(T2, "X", " other=1")) == 0,
              "base: a dump equal to lane W3's pin before lane X passes");
        Check(Explain(dir, B0, T2, golden: B0, "X", Entry(T1, "W3"), Entry(T2, "X", " other=1")) > 0,
              "base: a dump from before lane W3's pin fails");
    }

    static void Chain(string dir)
    {
        bool Refused(params string[] lines)
        {
            try { List(dir, lines); return false; }
            catch (InvalidDataException) { return true; }
        }
        string w3 = $"stream c/s count 1 digest {A} | W3 | r";
        string x = $"stream c/s count 2 digest {B} | X | r";
        Check(!Refused(w3, x), "chain: lane X's entry after lane W3's loads");
        Check(Refused(w3, x, $"stream c/s count 3 digest {C} | W3 | r"), "chain: lane W3 naming the stream twice is refused");
        Check(Refused($"stream c/o count 1 digest {A} | X | r", w3, x),
              "chain: lane X's entry after a later lane's entry is refused");
        Check(Refused(w3, x.Replace($"count 2 digest {B}", $"count 1 digest {A}")), "chain: an entry that pins its predecessor's bytes is refused");
    }
}
