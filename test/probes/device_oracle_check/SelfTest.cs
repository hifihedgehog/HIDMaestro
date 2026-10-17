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
//   lanes    Load refuses an entry of a lane no lane line names, and a lane
//            line twice. The writer adds a new lane's line before its
//            first entry.
//   history  replay refuses a list in which the newest lane changed, added or
//            dropped a line of another lane since its base list in git.
//   base     --explain refuses a base dump whose stream is not what the list
//            pinned before the lane, or the golden file where nothing did.
//   later    --explain --earlier re-runs a lane after a later lane pinned
//            the same stream: it rebuilds the lane's bytes from the base and
//            holds them to the lane's own pin. Without --earlier the stream
//            is read from the tree, and fails.
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
            Lanes(dir);
            History(dir);
            Base(dir);
            Later(dir);
        }
        finally
        {
            // git writes its objects read-only.
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        Console.WriteLine($"device_oracle_check selftest: {(s_failures == 0 ? "PASS" : $"FAIL, {s_failures} check(s)")}");
        return s_failures == 0 ? 0 : 1;
    }

    // The lanes of a list that names none.
    static readonly string[] Roster = { "lane W3 | W3 | r", "lane X | X | r", "lane Y | Y | r", "lane DD-6 | DD-6 | r" };

    static ChangeList List(string dir, params string[] lines)
    {
        string path = Path.Combine(dir, "list.txt");
        File.WriteAllLines(path, lines.Any(l => l.StartsWith("lane ", StringComparison.Ordinal)) ? lines : Roster.Concat(lines));
        return ChangeList.Load(new[] { path });
    }

    /// <summary>A written list's entries, its lane lines and comments left
    /// out.</summary>
    static List<string> Entries(string path) =>
        File.ReadAllLines(path).Where(l => !l.StartsWith('#') && !l.StartsWith("lane ", StringComparison.Ordinal)).ToList();

    static List<string> Write(string dir, ChangeList list, string? lane, (long, string) replay,
                              ExplainSession.StreamResult? explained = null)
    {
        string path = Path.Combine(dir, "written.txt");
        var pins = new Dictionary<string, (long, string)> { ["c/s"] = replay };
        var moved = new Dictionary<string, (long, string)> { ["c/s"] = replay };
        var x = new Dictionary<string, ExplainSession.StreamResult>(StringComparer.Ordinal);
        if (explained != null) x["c/s"] = explained;
        ChangeWriter.Write(path, list, new HashSet<string> { "c" }, moved, pins, x, lane);
        return Entries(path);
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

        string c3 = "config p InputReportId 3:0102 | W3 | r";
        string path = Path.Combine(dir, "written.txt");
        ChangeWriter.Write(path, List(dir, w3, c3), new HashSet<string>(), new Dictionary<string, (long, string)>(),
                           new Dictionary<string, (long, string)>(), new Dictionary<string, ExplainSession.StreamResult>(),
                           "X", new[] { "p InputReportId 3:0103" });
        lines = Entries(path);
        Check(lines.Count == 3 && lines[1] == c3 && lines[2] == "config p InputReportId 3:0103 | X | ?",
              "writer keeps lane W3's config entry and adds lane X's for the value it now writes");
        string c6 = "config p InputReportId 3:0103 | X | the lane's own value";
        ChangeWriter.Write(path, List(dir, w3, c3, c6), new HashSet<string>(), new Dictionary<string, (long, string)>(),
                           new Dictionary<string, (long, string)>(), new Dictionary<string, ExplainSession.StreamResult>(),
                           "X", new[] { "p InputReportId 3:0104" });
        lines = Entries(path);
        Check(lines.Count == 3 && lines[1] == c3 && lines[2] == "config p InputReportId 3:0104 | X | the lane's own value",
              "writer rewrites lane X's own config entry in place");

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
        ExplainRules.For(changes, lane);
        GoldenCase.ExplainDir = dumps;
        ExplainSession.Earlier = s_earlier;
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
            ExplainSession.Earlier = false;
            ExplainRules.Clear();
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

    static bool s_earlier;

    static void Later(string dir)
    {
        Check(Explain(dir, B0, T2, golden: B0, "X", Entry(T1, "X", " other=1"), Entry(T2, "Y")) > 0,
              "later: without --earlier a stream lane Y pinned after lane X is read, and fails lane X");
        s_earlier = true;
        try { LaterEarlier(dir); }
        finally { s_earlier = false; }
    }

    static void LaterEarlier(string dir)
    {
        Check(Explain(dir, B0, T2, golden: B0, "X", Entry(T1, "X", " other=1"), Entry(T2, "Y")) == 0,
              "later: lane X's claim of outputs no rule gives is kept once lane Y pins the stream");
        Check(Explain(dir, B0, T2, golden: B0, "X", "rule round | X | r", Entry(T2, "Y")) == 0,
              "later: a stream lane X left alone and lane Y pinned since rebuilds to the golden bytes");
        Check(Explain(dir, B0, T2, golden: B0, "X", Entry(T1, "X"), Entry(T2, "Y")) > 0,
              "later: lane X's pin that its rules do not rebuild fails");
    }

    static void Lanes(string dir)
    {
        bool Refused(params string[] lines)
        {
            try { List(dir, lines); return false; }
            catch (InvalidDataException) { return true; }
        }
        Check(Refused("lane DD-1 | DD-1 | r", $"stream c/s count 1 digest {A} | DD1 | r"),
              "lanes: an entry of lane DD1, which no lane line names, is refused");
        Check(Refused("lane W3 | W3 | r", "lane W3 | W3 | r", $"stream c/s count 1 digest {A} | W3 | r"),
              "lanes: a second lane line for lane W3 is refused");
        Check(Refused("lane W3 | X | r", $"stream c/s count 1 digest {A} | W3 | r"),
              "lanes: a lane line filed under another lane is refused");
        string path = Path.Combine(dir, "written.txt");
        var replay = new Dictionary<string, (long, string)> { ["c/s"] = (2, B) };
        ChangeWriter.Write(path, List(dir, "lane W3 | W3 | r", $"stream c/s count 1 digest {A} | W3 | r"), new HashSet<string> { "c" },
                           replay, replay, new Dictionary<string, ExplainSession.StreamResult>(), "Z");
        var lines = File.ReadAllLines(path).Where(l => !l.StartsWith('#')).ToList();
        Check(lines.Count == 4 && lines[2] == "lane Z | Z | ?" && lines[3] == $"stream c/s count 2 digest {B} | Z | ?",
              "lanes: the writer adds lane Z's line before its first entry");
    }

    static void History(string dir)
    {
        string repo = Path.Combine(dir, "repo");
        Directory.CreateDirectory(repo);
        string list = Path.Combine(repo, "changes.txt");
        bool Git(params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-C", repo, "-c", "user.name=selftest", "-c", "user.email=selftest@invalid", "-c", "commit.gpgsign=false" }.Concat(args))
                psi.ArgumentList.Add(a);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        void Commit(params string[] lines)
        {
            File.WriteAllLines(list, lines);
            Git("add", "changes.txt");
            Git("commit", "-q", "-m", "list");
        }
        int Problems(params string[] lines)
        {
            File.WriteAllLines(list, lines);
            return ChangeHistory.Check(list, out _).Count;
        }
        if (!Git("init", "-q"))
        {
            Check(false, "history: git init");
            return;
        }
        string w3l = "lane W3 | W3 | r", w3 = $"stream c/s count 1 digest {A} | W3 | r", c3 = "config p InputReportId 3:0102 | W3 | r";
        string xl = "lane X | X | r", x = $"stream c/s count 2 digest {B} | X | r";
        Commit(w3l, w3, c3);
        Commit(w3l, w3, c3, xl, x);
        Check(Problems(w3l, w3, c3, xl, x) == 0, "history: the list as committed passes");
        Check(Problems(w3l, w3, c3, xl, x.Replace(B, C)) == 0, "history: the newest lane may re-pin its own entry");
        Check(Problems(w3l, w3.Replace(A, C), c3, xl, x) > 0, "history: the newest lane editing lane W3's entry fails");
        Check(Problems(w3l, w3, c3.Replace("0102", "0103"), xl, x) > 0, "history: the newest lane editing lane W3's config entry fails");
        Check(Problems(w3l, w3, xl, x) > 0, "history: the newest lane dropping lane W3's entry fails");
        Check(Problems(w3l, w3, c3, $"stream c/o count 1 digest {A} | W3 | r", xl, x) > 0, "history: the newest lane adding an entry under lane W3 fails");
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
        Check(Refused(x, w3),
              "chain: lane X's entry after a later lane's entry is refused");
        Check(Refused(w3, x.Replace($"count 2 digest {B}", $"count 1 digest {A}")), "chain: an entry that pins its predecessor's bytes is refused");

        // A profile's configured value, which a later lane writes otherwise
        // again: lane DD-6's InputReportId after lane W3's.
        string c3 = "config xbox-360-flight-stick InputReportId 3:0102 | W3 | r";
        string c6 = "config xbox-360-flight-stick InputReportId 3:0103 | DD-6 | r";
        bool loads = !Refused(w3, x, c3, c6);
        Check(loads, "chain: lane DD-6's config entry after lane W3's for the same value loads");
        if (loads)
        {
            var current = ChangeList.Current(List(dir, w3, x, c3, c6).Config);
            Check(current.Count == 1 && current[0].Lane == "DD-6", "chain: the last config entry of a value is its pin");
        }
        Check(Refused(w3, x, c3, c3.Replace("3:0102", "3:0103")), "chain: lane W3 naming a configured value twice is refused");
        Check(Refused(w3, x, c3, c6.Replace("3:0103", "3:0102")), "chain: a config entry that pins its predecessor's value is refused");
    }
}
