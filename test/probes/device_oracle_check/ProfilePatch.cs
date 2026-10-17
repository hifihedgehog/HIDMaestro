// replay --profile-patch <file>: this run's catalog with some profiles
// replaced by edited copies of their embedded JSON, so a check of the change
// list can mutate a profile without editing the tree
// (mutations\triton-left-trigger-max.txt). One edit a line, # starts a
// comment:
//
//   <profile id> | <regular expression> | <replacement>
//
// The expression must match the profile's JSON exactly once, and the edited
// JSON must load. Every case reads the catalog through LoadEmbedded's
// process-wide cache, which the patch edits before the first case runs.
// Record refuses a patch: no golden file comes from edited data.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using HIDMaestro.Internal;

internal static class ProfilePatch
{
    public static void Apply(string path)
    {
        var db = ProfileDatabase.LoadEmbedded();
        var list = typeof(ProfileDatabase).GetField("_profiles", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(db) as List<ControllerProfile>
            ?? throw new MissingFieldException(nameof(ProfileDatabase), "_profiles");
        var asm = typeof(ProfileDatabase).Assembly;
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in asm.GetManifestResourceNames().Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal)
                                                                      && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
            string json = reader.ReadToEnd();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var id) && id.GetString() is { } s)
                    texts[s] = json;
            }
            catch (JsonException) { }
        }
        var lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string where = $"{path}:{i + 1}";
            var parts = line.Split(" | ", 3);
            if (parts.Length != 3) throw new InvalidDataException($"{where}: not \"<profile id> | <regular expression> | <replacement>\"");
            string id = parts[0].Trim();
            if (!texts.TryGetValue(id, out var text)) throw new InvalidDataException($"{where}: no profile {id} in the catalog");
            int hits = Regex.Matches(text, parts[1]).Count;
            if (hits != 1) throw new InvalidDataException($"{where}: the expression matches {id}'s JSON {hits} times, not once");
            string edited = Regex.Replace(text, parts[1], parts[2]);
            var p = JsonSerializer.Deserialize(edited, HMJsonContext.Default.ControllerProfile)
                ?? throw new InvalidDataException($"{where}: {id}'s edited JSON does not load");
            int at = list.FindIndex(x => x.Id == id);
            list[at] = p;
            texts[id] = edited;
            Console.WriteLine($"  [PATCHED] {id}: {parts[1]} becomes {parts[2]} ({where})");
        }
    }
}
