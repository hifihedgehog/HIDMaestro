// The change list's history, checked by every replay: a lane changes no
// other lane's line.
//
// The newest lane is the one whose lane line comes last. Its base list is
// changes.txt as the most recent commit in the file's history held it
// before that lane line was there. Every line of another lane in the list
// now, lane lines, stream, config and rule entries alike, must be that base
// list's line of that lane, in the same order, and no other lane's line may
// be added or dropped. A lane that edits an earlier lane's entry, by hand
// or by tool, then fails plain replay, whatever dump its --explain used.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

internal static class ChangeHistory
{
    /// <summary>The problems with <paramref name="path"/> against its base
    /// list, and where that base came from.</summary>
    public static List<string> Check(string path, out string basis)
    {
        var problems = new List<string>();
        var now = Lines(File.ReadAllText(path));
        var lanes = now.Where(l => l.StartsWith("lane ", StringComparison.Ordinal)).Select(LaneOf).ToList();
        if (lanes.Count == 0)
        {
            basis = "no lane lines";
            return problems;
        }
        string newest = lanes[^1];
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var (code, root) = Git(dir, "rev-parse", "--show-toplevel");
        if (code != 0)
        {
            basis = "";
            problems.Add($"git cannot read the history of {path}, so no other lane's lines can be checked");
            return problems;
        }
        string rel = Path.GetRelativePath(root.Trim(), Path.GetFullPath(path)).Replace('\\', '/');
        var (logCode, log) = Git(dir, "log", "--format=%H", "--", ":(top)" + rel);
        if (logCode != 0)
        {
            basis = "";
            problems.Add($"git log of {rel} failed");
            return problems;
        }
        List<string> before = new();
        basis = "the list's first version";
        foreach (string commit in log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (showCode, text) = Git(dir, "show", $"{commit}:{rel}");
            if (showCode != 0) continue;
            var lines = Lines(text);
            if (lines.Any(l => l.StartsWith("lane ", StringComparison.Ordinal) && LaneOf(l) == newest)) continue;
            before = lines;
            basis = $"commit {commit[..8]}, before lane {newest}'s line";
            break;
        }
        var mine = now.Where(l => LaneOf(l) != newest).ToList();
        var theirs = before.Where(l => LaneOf(l) != newest).ToList();
        int n = Math.Max(mine.Count, theirs.Count);
        for (int i = 0; i < n && problems.Count < 8; i++)
        {
            string? a = i < theirs.Count ? theirs[i] : null, b = i < mine.Count ? mine[i] : null;
            if (a == b) continue;
            problems.Add(a == null ? $"lane {newest} adds lane {LaneOf(b!)}'s line \"{Cut(b!)}\""
                : b == null ? $"lane {newest} drops lane {LaneOf(a)}'s line \"{Cut(a)}\""
                : $"lane {newest} changes lane {LaneOf(a)}'s line \"{Cut(a)}\" to \"{Cut(b)}\"");
        }
        return problems;
    }

    static string Cut(string line) => line.Length > 120 ? line[..120] + "..." : line;

    /// <summary>Each line that is not blank or a comment, its whitespace
    /// runs made one space.</summary>
    static List<string> Lines(string text) =>
        text.Split('\n').Select(l => string.Join(' ', l.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

    static string LaneOf(string line)
    {
        var parts = line.Split('|');
        return parts.Length >= 3 ? parts[1].Trim() : "";
    }

    static (int Code, string Output) Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(dir);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            _ = err.Result;
            return (p.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "");
        }
    }
}
