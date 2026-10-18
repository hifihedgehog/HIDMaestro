// tool: the profile check tool the release ZIP ships beside the profile
// extractor (tools\HIDMaestroProfileCheck), run as a JSON author runs it,
// from its own build.
//
//   - A directory of three broken copies of a catalog profile: the tool
//     exits 1 and prints each file's issue with its file and JSON path, a
//     key the schema lacks and a key written twice refused, and a model name
//     no model answers loaded with a model problem.
//   - One good copy: exit 0, and the profile loaded with its device model's
//     source.
//   - --cost on every catalog profile with programs, copied out as user
//     files with ids of their own: one line per program with its p50 and
//     p95 per report and its compile, a line for each whole model's compile,
//     and the runtime, tiering, CPU and instruction set lines.
//
// The tool is a separate process, as a user runs it, with no window.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using HIDMaestro.Internal;

internal static class ProfileCheckTool
{
    const string BaseId = "heusinkveld-ultimate-pedals";

    public static int Run()
    {
        var c = new Checks("tool");
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        string exe = Path.Combine(Program.RepoRoot(), "tools", "HIDMaestroProfileCheck", "bin", "Release", "net10.0-windows10.0.26100.0",
                                  "win-" + arch, "HIDMaestroProfileCheck.exe");
        if (!File.Exists(exe))
        {
            c.Check("the profile check tool is built", false, $"{exe} is missing. Build tools\\HIDMaestroProfileCheck");
            return c.Failures;
        }
        string root = Path.Combine(Path.GetTempPath(), "HIDMaestro_profile_check_tool_" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(root);
            Broken(c, exe, Path.Combine(root, "broken"));
            Good(c, exe, Path.Combine(root, "good"));
            Cost(c, exe, Path.Combine(root, "cost"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return c.Failures;
    }

    static (int Exit, string Output) Tool(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output + err.Result);
    }

    /// <summary>A catalog profile's file as the repository holds it.</summary>
    static string CatalogText(string id)
    {
        var p = ProfileDatabase.LoadEmbedded().All.First(x => x.Id == id);
        string rel = p.SourceFile!.Substring(ProfileDatabase.CatalogLabel.Length);
        return File.ReadAllText(Path.Combine(Program.RepoRoot(), "profiles", rel));
    }

    static string WithId(string text, string id)
    {
        var node = JsonNode.Parse(text)!.AsObject();
        node["id"] = id;
        return node.ToJsonString();
    }

    static void Broken(Checks c, string exe, string dir)
    {
        Directory.CreateDirectory(dir);
        string text = CatalogText(BaseId);
        var unknown = JsonNode.Parse(WithId(text, "tool-unknown-key"))!.AsObject();
        unknown["inptuReports"] = new JsonObject();
        File.WriteAllText(Path.Combine(dir, "a-unknown-key.json"), unknown.ToJsonString());
        // A key written twice: the name again, as text, since a JSON
        // object model holds each key once.
        string twice = WithId(text, "tool-twice");
        twice = "{\"name\":\"first\"," + twice.TrimStart()[1..];
        File.WriteAllText(Path.Combine(dir, "b-twice.json"), twice);
        var named = JsonNode.Parse(WithId(text, "tool-model-name"))!.AsObject();
        named["deviceModelFrom"] = "no-such-model";
        File.WriteAllText(Path.Combine(dir, "c-model-name.json"), named.ToJsonString());

        var (exit, output) = Tool(exe, dir);
        c.Check("a directory with broken files exits 1", exit == 1, $"exit {exit}");
        foreach (var (file, path, what) in new[]
                 {
                     ("a-unknown-key.json", "$.inptuReports", "refused"),
                     ("b-twice.json", "$.name", "refused"),
                     ("c-model-name.json", "$.deviceModelFrom", "loaded with a model problem"),
                 })
        {
            string line = output.Split('\n').FirstOrDefault(l => l.Contains(file, StringComparison.OrdinalIgnoreCase) && l.Contains(" at ", StringComparison.Ordinal))?.Trim() ?? "";
            c.Check($"{file}: one line with its file and {path}, {what}",
                    line.StartsWith(what + ": ", StringComparison.Ordinal) && line.Contains(Path.Combine(dir, file), StringComparison.OrdinalIgnoreCase)
                    && line.Contains($" at {path}: ", StringComparison.Ordinal), line.Length == 0 ? "no line names it" : line);
        }
        // The file named alone reports that file and no other.
        var (exitOne, one) = Tool(exe, Path.Combine(dir, "a-unknown-key.json"));
        c.Check("a broken file named alone exits 1 and reports only itself",
                exitOne == 1 && one.Contains("$.inptuReports", StringComparison.Ordinal) && !one.Contains("b-twice.json", StringComparison.OrdinalIgnoreCase)
                && !one.Contains("c-model-name.json", StringComparison.OrdinalIgnoreCase), $"exit {exitOne}");
    }

    static void Good(Checks c, string exe, string dir)
    {
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "good.json");
        File.WriteAllText(file, WithId(CatalogText(BaseId), "tool-good"));
        var (exit, output) = Tool(exe, file);
        c.Check("a good file exits 0 and loads its profile", exit == 0 && output.Contains("loaded tool-good, device model: ", StringComparison.Ordinal)
                && output.Contains("load: no issue", StringComparison.Ordinal), $"exit {exit}: {output.Trim().Replace('\n', '|')}");
    }

    static void Cost(Checks c, string exe, string dir)
    {
        Directory.CreateDirectory(dir);
        // Every catalog profile whose model builds or decodes a report, and
        // how many programs each has.
        var want = new List<string>();
        foreach (var p in ProfileDatabase.LoadEmbedded().All)
        {
            var m = p.GetOrCompileModel();
            int programs = m.Layouts.Length + m.LiveFeatures.Length + m.RuleEmits.Count(e => e != null) + m.Decoders.Length + (m.StateProgram != null ? 1 : 0);
            if (programs == 0) continue;
            string id = p.Id + "-user";
            File.WriteAllText(Path.Combine(dir, id + ".json"), WithId(CatalogText(p.Id), id));
            for (int i = 0; i < programs; i++) want.Add(id);
        }
        c.Check("the catalog has profiles with programs to copy out", want.Count > 0, $"{want.Count} programs");
        var (exit, output) = Tool(exe, "--cost", "--launches", "3", "--runs", "3", "--reports", "20000", dir);
        c.Check("--cost exits 0", exit == 0, $"exit {exit}");
        var lines = output.Split('\n').Select(l => l.Trim()).ToList();
        var costLines = lines.Where(l => l.Contains(" ns per report over 3 launches", StringComparison.Ordinal)).ToList();
        foreach (var id in want.Distinct())
        {
            int n = costLines.Count(l => l.StartsWith(id + ": ", StringComparison.Ordinal) && l.Contains(": p50 ", StringComparison.Ordinal)
                                         && l.Contains(" ns, p95 ", StringComparison.Ordinal) && l.Contains(", compile p50 ", StringComparison.Ordinal));
            c.Check($"{id}: one line per program with p50, p95 and compile", n == want.Count(x => x == id), $"{n} of {want.Count(x => x == id)}");
            c.Check($"{id}: the whole model's compile", lines.Any(l => l.StartsWith(id + ": the whole model's compile at create, p50 ", StringComparison.Ordinal)), "");
        }
        foreach (string label in new[] { "runtime: ", "tiering: ", "CPU: ", "instruction set: " })
            c.Check($"the {label.TrimEnd(' ', ':')} line", lines.Any(l => l.StartsWith(label, StringComparison.Ordinal)),
                    lines.FirstOrDefault(l => l.StartsWith(label, StringComparison.Ordinal)) ?? "missing");
    }
}
