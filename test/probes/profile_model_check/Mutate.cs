// One bad value in a user's profile file refuses that file at its JSON path,
// and the rest of the directory loads (design note, "Where errors appear").
//
// A load reads each file through the strict reader and the load checks.
// Any exception a check throws for a file's content, whatever its type,
// refuses that file. Never the load. At b22324e the loader caught every
// exception per file (ControllerProfile.cs:1195), so a profile with a null
// where a list belongs loaded and threw at create. The strict loader of
// 248af75c caught only the defect types, so a NullReferenceException from
// a check escaped Parallel.For and failed the whole directory with no
// issue listed.
//
//   mutate   (in check) each of the 18 places a null reached a check
//            in the review's mutation run, as a file beside a good one in
//            one directory, refused at the null's own path while the good
//            file loads, and a directory load of the same files
//            and every node of every catalog profile file replaced in turn
//            by null, -1, "zz", 100000, [], {} and "", each loaded as a
//            directory loads it: no exception leaves a load, and no file is
//            refused by the loader's catch-all for an exception no check
//            placed, and none at $ (about 95,000 loads, 5 s)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HIDMaestro.Internal;

internal static class Mutate
{
    /// <summary>The replacement values, as the review's mutation run used
    /// them.</summary>
    static readonly string[] Values = { "null", "-1", "\"zz\"", "100000", "[]", "{}", "\"\"" };

    static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The 18 escaping stacks of the review's run (DD2rs-fuzz.log),
    /// each as one catalog file with one value set to null, and the path the
    /// refusal must name.</summary>
    static readonly (string Profile, string Path, string Site)[] CrashPoints =
    {
        ("dualsense", "$.axisMap.0x32", "HidReportBuilder.ApplyAxisMap, a key's value"),
        ("dualsense-bt", "$.axisMap.0x32", "HidReportBuilder.ApplyAxisMap, a key's value"),
        ("dualsense", "$.layout.sticks", "HMLayoutValidator.WalkLayout, sticks"),
        ("dualsense", "$.layout.triggers", "HMLayoutValidator.WalkLayout, triggers"),
        ("dualsense", "$.layout.faceButtons", "HMLayoutValidator.CheckBindings, the list"),
        ("dualsense", "$.layout.faceButtons[0]", "HMLayoutValidator.CheckBindings, one binding"),
        ("gamecube-adapter", "$.usbConfiguration.interfaces", "UsbDescriptorSet, interfaces"),
        ("gamecube-adapter", "$.usbConfiguration.interfaces[0]", "UsbDescriptorSet, one interface"),
        ("gamecube-adapter", "$.usbConfiguration.interfaces[0].altSettings", "UsbDescriptorSet, altSettings"),
        ("gamecube-adapter", "$.usbConfiguration.interfaces[0].altSettings[0]", "UsbDescriptorSet, one alternate setting"),
        ("gamecube-adapter", "$.usbConfiguration.interfaces[0].altSettings[0].endpoints", "UsbDescriptorSet, endpoints"),
        ("gamecube-adapter", "$.usbConfiguration.interfaces[0].altSettings[0].endpoints[0]", "UsbDescriptorSet, one endpoint"),
        ("heusinkveld-ultimate-pedals", "$.layout.pedals", "HMLayoutValidator.WalkLayout, pedals"),
        ("heusinkveld-ultimate-pedals", "$.layout.pedals[0]", "HMLayoutValidator.WalkLayout, one pedal"),
        ("logitech-g29", "$.layout.wheel", "HMLayoutValidator.WalkLayout, wheel"),
        ("logitech-g29", "$.layout.pedals", "HMLayoutValidator.WalkLayout, a wheel's pedals"),
        ("logitech-g29", "$.layout.shifters", "HMLayoutValidator.WalkLayout, shifters"),
        ("logitech-g29", "$.layout.rotaryEncoders", "HMLayoutValidator.WalkLayout, rotaryEncoders"),
    };

    /// <summary>Every catalog profile file under profiles\, by id.</summary>
    static Dictionary<string, string> CatalogFiles()
    {
        string profilesDir = Path.Combine(Program.RepoRoot(), "profiles");
        string models = Path.Combine(profilesDir, "models") + Path.DirectorySeparatorChar;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string f in Directory.EnumerateFiles(profilesDir, "*.json", SearchOption.AllDirectories)
                     .Where(f => !Path.GetFileName(f).Equals("schema.json", StringComparison.OrdinalIgnoreCase)
                                 && !f.StartsWith(models, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            if (JsonNode.Parse(File.ReadAllText(f), documentOptions: DocOptions)?["id"]?.GetValue<string>() is { } id)
                map[id] = f;
        }
        return map;
    }

    /// <summary>The node at a path of the form $.a.b[2].c, where a key may
    /// hold any character but '.' and '['.</summary>
    static (JsonNode Parent, string? Key, int Index) Locate(JsonNode root, string path)
    {
        var segments = Segments(path);
        JsonNode node = root;
        for (int i = 0; i < segments.Count - 1; i++)
            node = segments[i].Key != null ? node[segments[i].Key!]! : node[segments[i].Index]!;
        var last = segments[^1];
        return (node, last.Key, last.Index);
    }

    static List<(string? Key, int Index)> Segments(string path)
    {
        var list = new List<(string?, int)>();
        int i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                int j = i + 1;
                while (j < path.Length && path[j] != '.' && path[j] != '[') j++;
                list.Add((path[(i + 1)..j], -1));
                i = j;
            }
            else
            {
                int j = path.IndexOf(']', i);
                list.Add((null, int.Parse(path[(i + 1)..j])));
                i = j + 1;
            }
        }
        return list;
    }

    static void Set(JsonNode root, string path, JsonNode? value)
    {
        var (parent, key, index) = Locate(root, path);
        if (key != null) parent.AsObject()[key] = value;
        else parent.AsArray()[index] = value;
    }

    /// <summary>One load of one file's text, as a directory load reads it:
    /// the issue it lists, or null when the file loads. An exception that
    /// leaves the load is returned as the second value.</summary>
    static (Api.Issue? Issue, Exception? Escaped) LoadOne(string file, string text)
    {
        try
        {
            var db = ProfileDatabase.LoadTexts(new[] { (file, text) }, ModelRegistry.Catalog);
            var issues = Api.Issues(db, "Issues").Where(i => i.Kind != "ModelProblem").ToList();
            return (issues.FirstOrDefault(), null);
        }
        catch (Exception e)
        {
            return (null, e);
        }
    }

    public static int Run()
    {
        var c = new Checks("mutate");
        var files = CatalogFiles();
        foreach (var (profile, path, site) in CrashPoints)
        {
            c.Run($"{profile} with {path} null ({site}) is refused at {path}", () =>
            {
                var root = JsonNode.Parse(File.ReadAllText(files[profile]), documentOptions: DocOptions)!;
                root["id"] = "user-" + profile;
                Set(root, path, null);
                var (issue, escaped) = LoadOne(Path.Combine(Path.GetTempPath(), "user-" + profile + ".json"), root.ToJsonString());
                if (escaped != null) return (false, $"the load threw {escaped.GetType().Name}: {escaped.Message}");
                return (issue != null && issue.Path == path, issue == null ? "loaded" : $"{issue.Path}: {issue.Reason}");
            });
        }
        c.Run("a directory holding a profile with a null layout list and a good profile: the good one loads and the bad one is listed at its path", () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "hm-pmc-mutate-" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            try
            {
                var bad = JsonNode.Parse(File.ReadAllText(files["xbox-360-wired"]), documentOptions: DocOptions)!;
                bad["id"] = "user-null-sticks";
                bad["layout"]!["sticks"] = null;
                var good = JsonNode.Parse(File.ReadAllText(files["xbox-360-wired"]), documentOptions: DocOptions)!;
                good["id"] = "user-good";
                File.WriteAllText(Path.Combine(dir, "a-bad.json"), bad.ToJsonString());
                File.WriteAllText(Path.Combine(dir, "b-good.json"), good.ToJsonString());
                var db = ProfileDatabase.Load(dir);
                var issues = Api.Issues(db, "Issues");
                bool ok = db.All.Count == 1 && db.All[0].Id == "user-good"
                          && issues.Count == 1 && issues[0].Path == "$.layout.sticks" && issues[0].File.EndsWith("a-bad.json");
                return (ok, $"{db.All.Count} loaded, issues: {string.Join("; ", issues)}");
            }
            finally { Directory.Delete(dir, true); }
        });
        Catalog(c, files);
        return c.Failures;
    }

    /// <summary>Every node of every catalog profile, each replaced by each
    /// value in turn.</summary>
    static void Catalog(Checks c, Dictionary<string, string> files)
    {
        var ids = files.Keys.ToArray();
        long loads = 0, loaded = 0, refused = 0;
        var escaped = new ConcurrentDictionary<string, (long Count, string Example)>();
        var backstop = new ConcurrentDictionary<string, (long Count, string Example)>();
        var unplaced = new ConcurrentDictionary<string, (long Count, string Example)>();
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(ids, id =>
        {
            string text = File.ReadAllText(files[id]);
            var root = JsonNode.Parse(text, documentOptions: DocOptions)!;
            var paths = new List<string>();
            Walk(root, "$", paths);
            foreach (string path in paths)
            {
                foreach (string v in Values)
                {
                    var copy = root.DeepClone();
                    Set(copy, path, JsonNode.Parse(v));
                    var (issue, e) = LoadOne(files[id], copy.ToJsonString());
                    Interlocked.Increment(ref loads);
                    if (e != null)
                    {
                        var frame = new StackTrace(e, true).GetFrames().FirstOrDefault(f => f.GetMethod()?.DeclaringType?.Namespace?.StartsWith("HIDMaestro") == true);
                        string sig = $"{e.GetType().Name} at {frame?.GetMethod()?.DeclaringType?.Name}.{frame?.GetMethod()?.Name}:{frame?.GetFileLineNumber()}";
                        escaped.AddOrUpdate(sig, _ => (1, $"{id} {path} := {v}: {e.Message}"), (_, o) => (o.Count + 1, o.Example));
                        continue;
                    }
                    if (issue == null) { Interlocked.Increment(ref loaded); continue; }
                    Interlocked.Increment(ref refused);
                    // The loader's catch-all refuses a file whose check threw
                    // something no check placed: a site still to fix.
                    if (issue.Reason.StartsWith(Backstop, StringComparison.Ordinal))
                        backstop.AddOrUpdate(issue.Reason.Split(':')[0], _ => (1, $"{id} {path} := {v}: {issue.Reason}"), (_, o) => (o.Count + 1, o.Example));
                    // Every value replaced here has a path of its own, so a
                    // refusal at $ names no place in the file.
                    else if (issue.Path == "$")
                        unplaced.AddOrUpdate(issue.Reason.Split(':')[0], _ => (1, $"{id} {path} := {v}: {issue.Reason}"), (_, o) => (o.Count + 1, o.Example));
                }
            }
        });
        Console.WriteLine($"  {ids.Length} profile(s), {loads} mutated load(s) in {sw.Elapsed.TotalSeconds:F0} s: {loaded} loaded, {refused} refused");
        c.Check($"no exception leaves a load of {loads} mutated files", escaped.IsEmpty,
            string.Join("", escaped.OrderByDescending(kv => kv.Value.Count).Select(kv => $"\n      {kv.Value.Count}x {kv.Key}\n        e.g. {kv.Value.Example}")));
        c.Check("no refusal comes from the loader's catch-all, which only an unchecked failure reaches", backstop.IsEmpty,
            string.Join("", backstop.OrderByDescending(kv => kv.Value.Count).Select(kv => $"\n      {kv.Value.Count}x {kv.Key}\n        e.g. {kv.Value.Example}")));
        c.Check("every refused file is refused at a path inside it, never at $", unplaced.IsEmpty,
            string.Join("", unplaced.OrderByDescending(kv => kv.Value.Count).Select(kv => $"\n      {kv.Value.Count}x {kv.Key}\n        e.g. {kv.Value.Example}")));
    }

    /// <summary>How the loader words a refusal for an exception that no
    /// check placed (ProfileDefects.LocateAny).</summary>
    const string Backstop = "a load check failed with ";

    static void Walk(JsonNode? node, string path, List<string> paths)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var kv in o)
                {
                    string p = $"{path}.{kv.Key}";
                    paths.Add(p);
                    Walk(kv.Value, p, paths);
                }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    string p = $"{path}[{i}]";
                    paths.Add(p);
                    Walk(a[i], p, paths);
                }
                break;
        }
    }
}
