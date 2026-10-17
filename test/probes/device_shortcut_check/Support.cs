// The catalog's IDs and the word rule shared by every check.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HIDMaestro.Internal;

internal sealed class Catalog
{
    public IReadOnlyList<ControllerProfile> All = Array.Empty<ControllerProfile>();
    public readonly HashSet<ulong> Vids = new(), Pids = new();
    public readonly HashSet<(ulong, ulong)> Pairs = new();
    public readonly HashSet<string> Ids = new(StringComparer.OrdinalIgnoreCase);
    public Regex HyphenatedIds = null!;
    public DeviceWords Words = null!;

    /// <summary>Profiles the SDK embeds and did not load: resource name and
    /// id. A profile is an embedded JSON object with an "id" and a "vid".</summary>
    public readonly List<(string Resource, string Id)> NotLoaded = new();

    public static Catalog Load(string? root = null)
    {
        var c = new Catalog { All = ProfileDatabase.LoadEmbedded().All };
        var loaded = new HashSet<string>(c.All.Select(p => p.Id), StringComparer.Ordinal);
        var asm = typeof(ProfileDatabase).Assembly;
        foreach (string name in asm.GetManifestResourceNames()
                     .Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) continue;
            string? id = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(stream);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("vid", out _)
                    && doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    id = idEl.GetString();
            }
            catch (System.Text.Json.JsonException) { id = "(not JSON)"; }
            if (id != null && !loaded.Contains(id)) c.NotLoaded.Add((name, id));
        }
        foreach (var p in c.All)
        {
            c.Ids.Add(p.Id);
            c.Vids.Add(p.VendorId);
            c.Pids.Add(p.ProductId);
            c.Pairs.Add((p.VendorId, p.ProductId));
            if (!string.IsNullOrEmpty(p.DriverPid))
            {
                ushort d = Convert.ToUInt16(p.DriverPid, 16);
                c.Pids.Add(d);
                c.Pairs.Add((p.VendorId, d));
            }
        }
        // The device words: vendor folders (from the resource names and the
        // tree's profiles folder) and the strings of every profile.
        var texts = new List<(string, string)>();
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal)) continue;
            string rest = name["HIDMaestro.Profiles.".Length..];
            int sep = rest.IndexOfAny(new[] { '\\', '/' });
            if (sep > 0) folders.Add(rest[..sep]);
        }
        // profiles\models holds the identity defaults and named models, no
        // vendor's profiles: the SDK embeds it under HIDMaestro.Models.
        if (root != null && Directory.Exists(Path.Combine(root, "profiles")))
            foreach (string d in Directory.EnumerateDirectories(Path.Combine(root, "profiles")))
                if (!string.Equals(Path.GetFileName(d), "models", StringComparison.OrdinalIgnoreCase))
                    folders.Add(Path.GetFileName(d));
        foreach (string f in folders) texts.Add((f, "vendor folder " + f));
        foreach (var p in c.All)
        {
            texts.Add((p.Id, "id of " + p.Id));
            texts.Add((p.Name, "name of " + p.Id));
            texts.Add((p.Vendor, "vendor of " + p.Id));
            texts.Add((p.ProductString, "product string of " + p.Id));
            texts.Add((p.ManufacturerString ?? "", "manufacturer string of " + p.Id));
            texts.Add((p.DeviceDescription ?? "", "description of " + p.Id));
        }
        c.Words = DeviceWords.Build(texts);
        var hy = c.Ids.Where(i => i.Contains('-')).OrderByDescending(i => i.Length).ThenBy(i => i, StringComparer.Ordinal).Select(Regex.Escape);
        c.HyphenatedIds = new Regex(@"(?<![A-Za-z0-9-])(?:" + string.Join("|", hy) + @")(?![A-Za-z0-9-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        return c;
    }
}

internal static class Words
{
    public static List<string> Split(string name)
    {
        var words = new List<string>();
        int start = -1;
        for (int i = 0; i <= name.Length; i++)
        {
            char c = i < name.Length ? name[i] : '\0';
            if (i == name.Length || !char.IsLetterOrDigit(c))
            {
                if (start >= 0) words.Add(name.Substring(start, i - start));
                start = -1;
                continue;
            }
            if (start < 0) { start = i; continue; }
            char prev = name[i - 1];
            char next = i + 1 < name.Length ? name[i + 1] : '\0';
            bool split =
                (char.IsUpper(c) && (char.IsLower(prev) || char.IsDigit(prev)))
                || (char.IsUpper(c) && char.IsUpper(prev) && char.IsLower(next))
                || (char.IsDigit(c) != char.IsDigit(prev));
            if (split)
            {
                words.Add(name.Substring(start, i - start));
                start = i;
            }
        }
        return words;
    }

    public readonly record struct Result(bool Hit, List<string> Tokens);
}

// The device and vendor words of rule A, derived from the catalog at run
// time: the words of every vendor folder name under profiles\ and of every
// profile's id, name, vendor, product, manufacturer and description
// strings, less Rules.GenericWords (each with its reason), plus
// Rules.ExtraWords. A name matches a word three ways:
//   - one word of the name is the word (Switch2ProDevice: switch),
//   - a run of up to four words spells a word the catalog writes as one
//     chunk of several words (DualSense, SteelSeries, PS4, 8BitDo, and the
//     folder names and ids joined at their hyphens), or one of
//     Rules.SpanningWords (Ds3, XBox, JoyCon),
//   - one word of the name holds a word of four letters or more, or one of
//     Rules.ShortCompoundWords, inside it (dualsensehelper, xboxone,
//     steamdeck, wiimote), unless the word is one of
//     Rules.CompoundExceptions or adds only an ending of
//     Rules.CompoundEndings (switching, decks).
// So SixAxis (six Axis) is not the catalog's Sixaxis, which it writes as
// one word.
internal sealed class DeviceWords
{
    public readonly HashSet<string> Single = new(StringComparer.Ordinal);
    public readonly HashSet<string> Spanning = new(StringComparer.Ordinal);
    public readonly List<string> Compound = new();
    /// <summary>For each word, the first catalog source that gave it.</summary>
    public readonly SortedDictionary<string, string> From = new(StringComparer.Ordinal);
    /// <summary>The catalog words Rules.GenericWords removed, with the
    /// source that gave each.</summary>
    public readonly SortedDictionary<string, string> Removed = new(StringComparer.Ordinal);

    static readonly string[] s_digits = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };

    public static DeviceWords Build(IEnumerable<(string Text, string From)> sources)
    {
        var w = new DeviceWords();
        var generic = new HashSet<string>(Rules.GenericWords.SelectMany(g => g.Words), StringComparer.Ordinal);
        var extra = new HashSet<string>(Rules.ExtraWords.Select(e => e.Word).Concat(Rules.SpanningWords), StringComparer.Ordinal);
        void Add(string word, string from, bool spanning)
        {
            if (word.Length == 0 || word.All(char.IsDigit)) return;
            if (extra.Contains(word)) return;
            if (generic.Contains(word) || word.Count(char.IsLetter) <= 2) { w.Removed.TryAdd(word, from); return; }
            (spanning ? w.Spanning : w.Single).Add(word);
            w.From.TryAdd(word, from);
        }
        foreach (var (text, from) in sources)
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (Match chunk in Regex.Matches(text, "[A-Za-z0-9]+"))
            {
                var parts = Words.Split(chunk.Value);
                foreach (string part in parts) Add(part.ToLowerInvariant(), from, spanning: false);
                if (parts.Count < 2) continue;
                string joined = chunk.Value.ToLowerInvariant();
                Add(joined, from, spanning: true);
                // A vendor folder that starts with one digit, spelled as an
                // identifier must spell it (8BitDo, EightBitDo).
                if (parts[0].Length == 1 && char.IsDigit(parts[0][0]) && from.StartsWith("vendor folder ", StringComparison.Ordinal))
                    Add(s_digits[parts[0][0] - '0'] + joined[1..], from, spanning: true);
            }
            // A hyphenated folder name or id, joined.
            if (text.Contains('-') && Regex.IsMatch(text, "^[A-Za-z0-9-]+$"))
                Add(text.Replace("-", "").ToLowerInvariant(), from, spanning: true);
        }
        foreach (var (word, reason) in Rules.ExtraWords)
        {
            w.Single.Add(word);
            w.From.TryAdd(word, reason);
        }
        foreach (string s in Rules.SpanningWords)
        {
            w.Spanning.Add(s);
            w.From.TryAdd(s, "Rules.SpanningWords");
        }
        foreach (string word in w.Single.Concat(w.Spanning).Distinct())
            if (word.Length >= 4 || Rules.ShortCompoundWords.Contains(word)) w.Compound.Add(word);
        w.Compound.Sort(StringComparer.Ordinal);
        return w;
    }

    public bool Contains(string word) => Single.Contains(word) || Spanning.Contains(word);

    // The device word at this place sits inside a word of
    // Rules.CompoundExceptions the compound holds (vendor holds endor,
    // and vendorid holds vendor).
    static bool Covered(string word, string d, int at)
    {
        foreach (var e in Rules.CompoundExceptions)
            for (int x = word.IndexOf(e.Word, StringComparison.Ordinal); x >= 0; x = word.IndexOf(e.Word, x + 1, StringComparison.Ordinal))
                if (x <= at && at + d.Length <= x + e.Word.Length) return true;
        return false;
    }

    // The device words a name holds. A run of words spells a word only
    // inside one part of the name between underscores, so s_inputHandles is
    // not the catalog's SInput.
    public Words.Result Match(string name)
    {
        var w = Words.Split(name).Select(x => x.ToLowerInvariant()).ToList();
        var hits = new List<string>();
        foreach (string part in Regex.Split(name, "[^A-Za-z0-9]+"))
        {
            var pw = Words.Split(part).Select(x => x.ToLowerInvariant()).ToList();
            for (int a = 0; a < pw.Count; a++)
            {
                string run = "";
                for (int b = a; b < pw.Count && b < a + 4; b++)
                {
                    run += pw[b];
                    bool hit = b == a ? Single.Contains(run) || Spanning.Contains(run) : Spanning.Contains(run);
                    if (hit && !hits.Contains(run)) hits.Add(run);
                }
            }
        }
        foreach (string word in w)
        {
            if (word.Length < 4) continue;
            foreach (string d in Compound)
            {
                if (word.Length <= d.Length) continue;
                int at = word.IndexOf(d, StringComparison.Ordinal);
                if (at < 0) continue;
                if (at == 0 && Rules.CompoundEndings.Contains(word[d.Length..])) continue;
                if (Covered(word, d, at)) continue;
                if (!hits.Contains(d)) hits.Add(d);
            }
        }
        return new Words.Result(hits.Count > 0, hits);
    }
}
