using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HIDMaestro.Internal;

/// <summary>The groups of a device model, each resolved on its own
/// (design note, "Profiles made outside the catalog"): the answers a device
/// gives on request, its protocol, and how its devnode presents itself.</summary>
internal enum ModelGroup
{
    /// <summary><c>featureStubs</c> and <c>valueSources</c>.</summary>
    Answers = 0,

    /// <summary>The keys of the protocol engine and report programs. v2.0.0's
    /// UMDF2 profiles carry none yet, so the group resolves to nothing.</summary>
    Protocol = 1,

    /// <summary><c>driverPid</c>.</summary>
    Presentation = 2,
}

/// <summary>A device model: the keys of each group a profile takes from its
/// own JSON, a model it names, or its identity default. Immutable once
/// built.</summary>
internal sealed class DeviceModel
{
    public FeatureStubSpec? FeatureStubs { get; init; }
    public Dictionary<string, ValueSourceSpec>? ValueSources { get; init; }
    public string? DriverPid { get; init; }

    public static readonly DeviceModel Empty = new();

    public static readonly ModelGroup[] Groups = { ModelGroup.Answers, ModelGroup.Protocol, ModelGroup.Presentation };

    public bool Covers(ModelGroup g) => g switch
    {
        ModelGroup.Answers => FeatureStubs != null || ValueSources != null,
        ModelGroup.Protocol => false,
        ModelGroup.Presentation => DriverPid != null,
        _ => false,
    };

    public bool IsEmpty => !Groups.Any(Covers);

    /// <summary>The keys a profile carries itself.</summary>
    public static DeviceModel OwnOf(ControllerProfile p) =>
        new() { FeatureStubs = p.FeatureStubs, ValueSources = p.ValueSources, DriverPid = p.DriverPid };

    public static DeviceModel Of(DeviceModelFile f) =>
        new() { FeatureStubs = f.FeatureStubs, ValueSources = f.ValueSources, DriverPid = f.DriverPid };

    /// <summary>One group of this model, the others empty.</summary>
    public DeviceModel Only(ModelGroup g) => g switch
    {
        ModelGroup.Answers => new() { FeatureStubs = FeatureStubs, ValueSources = ValueSources },
        ModelGroup.Presentation => new() { DriverPid = DriverPid },
        _ => Empty,
    };

    /// <summary>This model with one group's keys taken from another.</summary>
    public DeviceModel With(ModelGroup g, DeviceModel from) => g switch
    {
        ModelGroup.Answers => new() { FeatureStubs = from.FeatureStubs, ValueSources = from.ValueSources, DriverPid = DriverPid },
        ModelGroup.Presentation => new() { FeatureStubs = FeatureStubs, ValueSources = ValueSources, DriverPid = from.DriverPid },
        _ => this,
    };

    public static string GroupName(ModelGroup g) => g switch
    {
        ModelGroup.Answers => "answers",
        ModelGroup.Protocol => "protocol",
        ModelGroup.Presentation => "presentation",
        _ => g.ToString(),
    };
}

/// <summary>What a profile resolved to: the model, where each group came
/// from, the identity default its identity matched, and what went wrong
/// with a model it took.</summary>
internal sealed class ResolvedModel
{
    public DeviceModel Model { get; init; } = DeviceModel.Empty;

    /// <summary>By group name: <c>own</c>, <c>named:&lt;id&gt;</c>,
    /// <c>identity:&lt;entry&gt;</c> or <c>none</c>.</summary>
    public IReadOnlyDictionary<string, string> Sources { get; init; } = new Dictionary<string, string>();

    /// <summary>The identity default whose entry matched the profile, as
    /// <c>identity:&lt;entry&gt;</c>, whether or not it filled a group. Null
    /// when none matched or the profile says <c>deviceModel: "none"</c>.</summary>
    public string? IdentityEntry { get; init; }

    /// <summary>The model the matched identity default gives, by name: a
    /// named model or a catalog profile. Null when none matched.</summary>
    public string? IdentityModel { get; init; }

    /// <summary>An identity default's entry matches the profile, whether
    /// or not it filled a group, <c>deviceModel: "none"</c> included. The
    /// entries are the gates a driver older than 2.0.0 keys on.</summary>
    public bool IdentityMatched { get; init; }

    /// <summary>Why a model the profile names, or its identity gives, was
    /// not taken. Null when nothing went wrong.</summary>
    public string? Problem { get; init; }

    /// <summary>Where <see cref="Problem"/> lives: the file of a model that
    /// does not fit, null for the profile's own file, and the JSON path in
    /// that file. A name that finds no model is the profile's
    /// <c>$.deviceModelFrom</c>, a model that does not fit is the model's key
    /// that does not.</summary>
    public string? ProblemFile { get; init; }
    public string ProblemPath { get; init; } = "$.deviceModelFrom";

    /// <summary>The file each filled group's keys came from: the profile's
    /// own file, a named model's, or, through a catalog profile, the file
    /// that profile's group came from. A problem with a model taken through
    /// a catalog profile is listed in the file that holds the key.</summary>
    internal IReadOnlyDictionary<ModelGroup, string> GroupFiles { get; init; } = new Dictionary<ModelGroup, string>();

    /// <summary>What resolution noted without refusing: an answer for a
    /// report the profile's descriptor does not declare, kept because
    /// today's code keeps it, and a name that filled no group.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>A named model file: <c>profiles/models/&lt;id&gt;.json</c>, or a
/// <c>models</c> folder of a directory a program loads. It carries the keys
/// of one or more groups and nothing else of a profile.</summary>
internal sealed class DeviceModelFile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>What the model is and where its bytes come from.
    /// Documentation only.</summary>
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("featureStubs")]
    public FeatureStubSpec? FeatureStubs { get; set; }

    [JsonPropertyName("valueSources")]
    public Dictionary<string, ValueSourceSpec>? ValueSources { get; set; }

    [JsonPropertyName("driverPid")]
    public string? DriverPid { get; set; }
}

/// <summary><c>identity-defaults.json</c>: what a profile with no keys of
/// its own in a group, and no name, takes from its identity.</summary>
internal sealed class IdentityDefaultsFile
{
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("entries")]
    public List<IdentityDefaultEntry>? Entries { get; set; }
}

/// <summary>One identity default: the identity it matches and the model it
/// gives, a named model or a catalog profile.</summary>
internal sealed class IdentityDefaultEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>The gate the entry replaces. Documentation only.</summary>
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("match")]
    public IdentityMatchSpec? Match { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }
}

/// <summary>What an identity default matches. Every field given must hold:
/// the VID, one of the PIDs, the connection, each of <c>keys</c> (a profile
/// key and the JSON value it must hold), and <c>textContains</c> in one of
/// <c>textFields</c> (<c>id</c>, <c>name</c>, <c>productString</c>),
/// compared without case.</summary>
internal sealed class IdentityMatchSpec
{
    [JsonPropertyName("vid")]
    public string? Vid { get; set; }

    /// <summary>One PID or a list of them.</summary>
    [JsonPropertyName("pid")]
    [JsonConverter(typeof(StringOrListConverter))]
    public List<string>? Pid { get; set; }

    [JsonPropertyName("connection")]
    public string? Connection { get; set; }

    /// <summary>Profile keys and the values they must hold, compared as
    /// JSON, so an entry can match on any key a profile carries.</summary>
    [JsonPropertyName("keys")]
    public Dictionary<string, JsonElement>? Keys { get; set; }

    [JsonPropertyName("textContains")]
    public string? TextContains { get; set; }

    [JsonPropertyName("textFields")]
    public List<string>? TextFields { get; set; }
}

/// <summary>A string or a list of strings, read as a list.</summary>
internal sealed class StringOrListConverter : JsonConverter<List<string>>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String) return new List<string> { reader.GetString()! };
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("expected a string or a list of strings");
        var list = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("expected a string in the list");
            list.Add(reader.GetString()!);
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        if (value.Count == 1) { writer.WriteStringValue(value[0]); return; }
        writer.WriteStartArray();
        foreach (var s in value) writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}

/// <summary>The models and identity defaults a profile can take: the
/// catalog's, embedded in the SDK, and those of each directory a context
/// loaded. Named models resolve by id, before the catalog profiles a name
/// can also take. Identity tables are tried in order, a directory's before
/// the catalog's, and a directory's apply to profiles outside the catalog
/// only.</summary>
internal sealed class ModelRegistry
{
    /// <summary>The prefix of the embedded model files.</summary>
    internal const string ResourcePrefix = "HIDMaestro.Models.";

    /// <summary>The identity table's file name, in the catalog and in a
    /// directory's models folder.</summary>
    internal const string IdentityDefaultsName = "identity-defaults.json";

    private readonly Dictionary<string, (DeviceModelFile File, string Source)> _models;
    private readonly List<(string Label, IdentityDefaultsFile Table, bool OutsideCatalogOnly)> _tables;
    private readonly Func<string, ControllerProfile?> _profiles;

    private ModelRegistry(Dictionary<string, (DeviceModelFile File, string Source)> models,
                          List<(string Label, IdentityDefaultsFile Table, bool OutsideCatalogOnly)> tables,
                          Func<string, ControllerProfile?> profiles)
    {
        _models = models;
        _tables = tables;
        _profiles = profiles;
    }

    /// <summary>The model ids, for a check that lists them.</summary>
    internal IEnumerable<string> ModelIds => _models.Keys;

    /// <summary>The catalog's registry over the given catalog profiles.
    /// Throws when an embedded model or the table does not load: the SDK
    /// was built with a broken model.</summary>
    internal static ModelRegistry ForCatalog(IReadOnlyList<ControllerProfile> catalog)
    {
        var byId = catalog.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var (models, tables) = s_embedded.Value;
        return new ModelRegistry(models, tables, id => byId.TryGetValue(id, out var p) ? p : null);
    }

    /// <summary>The SDK's embedded models and identity table, read once.</summary>
    private static readonly Lazy<(Dictionary<string, (DeviceModelFile File, string Source)> Models,
                                  List<(string Label, IdentityDefaultsFile Table, bool OutsideCatalogOnly)> Tables)> s_embedded =
        new(ReadEmbedded, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private static (Dictionary<string, (DeviceModelFile File, string Source)>, List<(string Label, IdentityDefaultsFile Table, bool OutsideCatalogOnly)>) ReadEmbedded()
    {
        var models = new Dictionary<string, (DeviceModelFile File, string Source)>(StringComparer.OrdinalIgnoreCase);
        var tables = new List<(string Label, IdentityDefaultsFile Table, bool OutsideCatalogOnly)>();
        var asm = typeof(ModelRegistry).Assembly;
        foreach (string name in asm.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            string label = ProfileDatabase.CatalogLabel + "models/" + name.Substring(ResourcePrefix.Length).Replace('\\', '/');
            using var s = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"The SDK's {label} cannot be opened.");
            using var reader = new StreamReader(s);
            string text = reader.ReadToEnd();
            // The catalog's entries read identity:<entry>, with no file.
            if (label.EndsWith("/" + IdentityDefaultsName, StringComparison.OrdinalIgnoreCase))
            {
                tables.Add(("", ReadTable(text, label), false));
                continue;
            }
            var file = ReadModel(text, label);
            if (!models.TryAdd(file.Id, (file, label)))
                throw new InvalidOperationException($"{label}: the model id '{file.Id}' repeats one the SDK already carries.");
        }
        return (models, tables);
    }

    /// <summary>This registry with a directory's models folder added: its
    /// named models, and its identity table tried before the ones already
    /// here, for profiles outside the catalog. A model file that does not
    /// load, or repeats a model id already held, is listed and left out.</summary>
    internal ModelRegistry WithDirectory(string modelsDir, List<HIDMaestro.HMProfileLoadIssue> issues)
    {
        var models = new Dictionary<string, (DeviceModelFile File, string Source)>(_models, StringComparer.OrdinalIgnoreCase);
        var tables = new List<(string Label, IdentityDefaultsFile Table, bool OutsideCatalogOnly)>();
        if (Directory.Exists(modelsDir))
        {
            foreach (string file in Directory.EnumerateFiles(modelsDir, "*.json", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    string text = File.ReadAllText(file);
                    if (Path.GetFileName(file).Equals(IdentityDefaultsName, StringComparison.OrdinalIgnoreCase))
                    {
                        var table = ReadTable(text, file);
                        // An entry name another loaded table holds is refused,
                        // as a repeated model id is.
                        var taken = _tables.SelectMany(t => t.Table.Entries ?? new List<IdentityDefaultEntry>())
                            .Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var kept = new List<IdentityDefaultEntry>();
                        for (int i = 0; table.Entries != null && i < table.Entries.Count; i++)
                        {
                            if (!taken.Contains(table.Entries[i].Name)) { kept.Add(table.Entries[i]); continue; }
                            issues.Add(new HIDMaestro.HMProfileLoadIssue(file, null, $"$.entries[{i}].name",
                                $"the entry name '{table.Entries[i].Name}' is already loaded. Give the entry a name of its own.",
                                HIDMaestro.HMProfileLoadIssueKind.Refused));
                        }
                        table.Entries = kept;
                        tables.Add((file, table, true));
                        continue;
                    }
                    var m = ReadModel(text, file);
                    if (!models.TryAdd(m.Id, (m, file)))
                        issues.Add(new HIDMaestro.HMProfileLoadIssue(file, null, "$.id",
                            $"the model id '{m.Id}' is already loaded from {models[m.Id].Source}. Give the model an id of its own.",
                            HIDMaestro.HMProfileLoadIssueKind.Refused));
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    var (path, reason) = ProfileDefects.Locate(e);
                    issues.Add(new HIDMaestro.HMProfileLoadIssue(file, null, path, reason, HIDMaestro.HMProfileLoadIssueKind.Refused));
                }
            }
        }
        tables.AddRange(_tables);
        return new ModelRegistry(models, tables, _profiles);
    }

    private static DeviceModelFile ReadModel(string text, string label)
    {
        var m = JsonSerializer.Deserialize(text, HMJsonContext.Default.DeviceModelFile)
            ?? throw new ProfileDefectException("$", "the model file is empty.");
        if (string.IsNullOrWhiteSpace(m.Id)) throw new ProfileDefectException("$.id", "a model needs an id.");
        return m;
    }

    private static IdentityDefaultsFile ReadTable(string text, string label)
    {
        var t = JsonSerializer.Deserialize(text, HMJsonContext.Default.IdentityDefaultsFile)
            ?? throw new ProfileDefectException("$", "the identity table is empty.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < (t.Entries?.Count ?? 0); i++)
        {
            var e = t.Entries![i] ?? throw new ProfileDefectException($"$.entries[{i}]", "an entry is null.");
            if (string.IsNullOrWhiteSpace(e.Name)) throw new ProfileDefectException($"$.entries[{i}].name", "an entry needs a name.");
            if (!names.Add(e.Name)) throw new ProfileDefectException($"$.entries[{i}].name", $"the entry name '{e.Name}' repeats.");
            if (e.Match == null) throw new ProfileDefectException($"$.entries[{i}].match", "an entry needs a match.");
            if (string.IsNullOrWhiteSpace(e.Model)) throw new ProfileDefectException($"$.entries[{i}].model", "an entry names the model it gives.");
            CheckMatch(e.Match, $"$.entries[{i}].match");
        }
        return t;
    }

    private static void CheckMatch(IdentityMatchSpec m, string at)
    {
        if (m.Vid != null) _ = ParseId(m.Vid, at + ".vid");
        if (m.Pid != null)
            for (int i = 0; i < m.Pid.Count; i++) _ = ParseId(m.Pid[i], $"{at}.pid[{i}]");
        if (m.TextContains != null)
            foreach (string f in m.TextFields ?? new List<string>())
                if (f is not ("id" or "name" or "productString"))
                    throw new ProfileDefectException(at + ".textFields", $"'{f}' is not id, name or productString.");
    }

    internal static ushort ParseId(string s, string at)
    {
        string digits = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Substring(2) : s;
        if (digits.Length is < 1 or > 4
            || !ushort.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort v))
            throw new ProfileDefectException(at, $"\"{s}\" is not a hex ID of one to four digits.");
        return v;
    }

    /// <summary>A model by name: a named model, else a catalog profile's
    /// resolved model. Null when nothing holds the name. The files are the
    /// file each group came from: the named model's for every group, or,
    /// group by group, the catalog profile's own file or the file of the
    /// model it took.</summary>
    private (DeviceModel Model, IReadOnlyDictionary<ModelGroup, string> Files)? Find(string name, HashSet<string> resolving)
    {
        if (_models.TryGetValue(name, out var m))
            return (DeviceModel.Of(m.File), DeviceModel.Groups.ToDictionary(g => g, _ => m.Source));
        var p = _profiles(name);
        if (p == null) return null;
        if (!resolving.Add(p.Id))
            throw new ProfileDefectException("$.deviceModelFrom", $"'{name}' names a profile whose model names this one back.");
        try
        {
            var r = Resolve(p, this, outsideCatalog: false, resolving);
            return (r.Model, r.GroupFiles);
        }
        finally { resolving.Remove(p.Id); }
    }

    /// <summary>The file that holds the key a taken model's check names:
    /// <c>$.driverPid</c> is the presentation group's, every other key the
    /// answers group's (AnswerModels.Validate). Null, the profile's own
    /// file, when no file filled that group.</summary>
    private static string? FileAt(IReadOnlyDictionary<ModelGroup, string> files, string path)
    {
        var g = path == "$.driverPid" ? ModelGroup.Presentation : ModelGroup.Answers;
        return files.TryGetValue(g, out var f) ? f : null;
    }

    /// <summary>The first identity default whose match holds for the
    /// profile, and the label <c>identity:&lt;entry&gt;</c> or, for a
    /// directory's table, <c>identity:&lt;file&gt;#&lt;entry&gt;</c>.</summary>
    private (IdentityDefaultEntry Entry, string Label)? MatchIdentity(ControllerProfile p, bool outsideCatalog)
    {
        foreach (var (file, table, outsideOnly) in _tables)
        {
            if (outsideOnly && !outsideCatalog) continue;
            foreach (var e in table.Entries ?? new List<IdentityDefaultEntry>())
                if (Matches(e.Match!, p))
                    return (e, file.Length == 0 ? $"identity:{e.Name}" : $"identity:{file}#{e.Name}");
        }
        return null;
    }

    private static bool Matches(IdentityMatchSpec m, ControllerProfile p)
    {
        if (m.Vid != null && ParseId(m.Vid, "match.vid") != p.VendorId) return false;
        if (m.Pid != null && !m.Pid.Any(x => ParseId(x, "match.pid") == p.ProductId)) return false;
        if (m.Connection != null && !string.Equals(m.Connection, p.Connection, StringComparison.OrdinalIgnoreCase)) return false;
        if (m.Keys is { Count: > 0 })
        {
            // Each key's value as the profile's JSON writes it, read through
            // the key's own property alone: serializing the whole profile
            // for every entry cost the catalog's load about 13 ms.
            var info = HMJsonContext.Default.ControllerProfile;
            foreach (var (key, want) in m.Keys)
            {
                var prop = info.Properties.FirstOrDefault(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase));
                object? value = prop?.Get?.Invoke(p);
                if (value == null) return false;
                var have = JsonSerializer.SerializeToNode(value, HMJsonContext.Default.GetTypeInfo(prop!.PropertyType)!);
                if (have == null || !JsonNode.DeepEquals(have, JsonNode.Parse(want.GetRawText()))) return false;
            }
        }
        if (m.TextContains != null)
        {
            bool any = false;
            foreach (string field in m.TextFields ?? new List<string>())
            {
                string? value = field switch { "id" => p.Id, "name" => p.Name, "productString" => p.ProductString, _ => null };
                if (value != null && value.Contains(m.TextContains, StringComparison.OrdinalIgnoreCase)) { any = true; break; }
            }
            if (!any) return false;
        }
        return true;
    }

    /// <summary>Resolves a profile's model, group by group: its own keys,
    /// else the model it names, else the identity default its identity
    /// matches, else nothing. <c>deviceModel: "none"</c> leaves names and
    /// identity defaults out. A name nothing holds, a name whose model is
    /// empty, or a model that does not fit the profile's descriptor is
    /// recorded as the problem and the identity default applies. Own keys
    /// that do not validate throw, with the JSON path: the profile is
    /// refused.</summary>
    internal static ResolvedModel Resolve(ControllerProfile p, ModelRegistry registry, bool outsideCatalog,
                                          HashSet<string>? resolving = null)
    {
        resolving ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase) { p.Id };
        bool none = false;
        if (p.DeviceModel != null)
        {
            if (p.DeviceModel != "none")
                throw new ProfileDefectException("$.deviceModel", $"\"{p.DeviceModel}\" is not a value it takes. The one value is \"none\".");
            if (p.DeviceModelFrom != null)
                throw new ProfileDefectException("$.deviceModel", "\"none\" and deviceModelFrom both given. A profile names a model or says it takes none.");
            none = true;
        }
        var notes = new List<string>();
        List<string> namedNotes = new(), identityNotes = new();
        var own = DeviceModel.OwnOf(p);
        // Own keys are the profile's own and validated strictly.
        AnswerModels.Validate(p, own, taken: false, notes);

        DeviceModel? named = null;
        IReadOnlyDictionary<ModelGroup, string>? namedFiles = null, identityFiles = null;
        string? namedSource = null;
        string? problem = null, problemFile = null;
        string problemPath = "$.deviceModelFrom";
        if (p.DeviceModelFrom is { } name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ProfileDefectException("$.deviceModelFrom", "names no model.");
            var found = string.Equals(name, p.Id, StringComparison.OrdinalIgnoreCase) ? null : registry.Find(name, resolving);
            if (string.Equals(name, p.Id, StringComparison.OrdinalIgnoreCase))
                problem = $"deviceModelFrom names the profile itself. The profile takes the model its identity gives it.";
            else if (found == null)
                problem = $"deviceModelFrom names '{name}', which no loaded model or catalog profile holds. The profile takes the model its identity gives it.";
            else if (found.Value.Model.IsEmpty)
                problem = $"deviceModelFrom names '{name}', which carries no device model. The profile takes the model its identity gives it.";
            else
            {
                try
                {
                    AnswerModels.Validate(p, found.Value.Model, taken: true, namedNotes);
                    named = found.Value.Model;
                    namedFiles = found.Value.Files;
                    namedSource = $"named:{name}";
                }
                catch (Exception e) when (ProfileDefects.IsDefect(e))
                {
                    var (path, reason) = ProfileDefects.Locate(e);
                    problem = $"{ProfileLabel(p)} names '{name}' in deviceModelFrom, and the model does not fit it at {path}: {reason} The profile takes the model its identity gives it.";
                    problemFile = FileAt(found.Value.Files, path);
                    problemPath = path;
                }
            }
        }

        DeviceModel? identity = null;
        string? identityLabel = null, identityModel = null;
        var matched = registry.MatchIdentity(p, outsideCatalog);
        if (!none && matched is { } hit)
        {
            identityLabel = hit.Label;
            identityModel = hit.Entry.Model;
            // An entry that names the profile itself, as the catalog's own
            // switch-pro and dualshock-3-full are their identities' models,
            // gives it nothing it does not carry already.
            var found = string.Equals(hit.Entry.Model, p.Id, StringComparison.OrdinalIgnoreCase)
                ? null : registry.Find(hit.Entry.Model!, resolving);
            if (found != null && !found.Value.Model.IsEmpty)
            {
                try
                {
                    AnswerModels.Validate(p, found.Value.Model, taken: true, identityNotes);
                    identity = found.Value.Model;
                    identityFiles = found.Value.Files;
                }
                catch (Exception e) when (ProfileDefects.IsDefect(e))
                {
                    var (path, reason) = ProfileDefects.Locate(e);
                    if (problem == null)
                    {
                        problem = $"the identity default {hit.Label} gives {ProfileLabel(p)} '{hit.Entry.Model}', and the model does not fit it at {path}: {reason}";
                        problemFile = FileAt(found.Value.Files, path);
                        problemPath = path;
                    }
                }
            }
        }

        var model = DeviceModel.Empty;
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var groupFiles = new Dictionary<ModelGroup, string>();
        bool nameUsed = false;
        foreach (var g in DeviceModel.Groups)
        {
            string key = DeviceModel.GroupName(g);
            if (own.Covers(g)) { model = model.With(g, own); sources[key] = "own"; groupFiles[g] = p.SourceFile ?? p.Id; }
            else if (named != null && named.Covers(g))
            {
                model = model.With(g, named); sources[key] = namedSource!; nameUsed = true;
                if (namedFiles!.TryGetValue(g, out var f)) groupFiles[g] = f;
            }
            else if (identity != null && identity.Covers(g))
            {
                model = model.With(g, identity); sources[key] = identityLabel!;
                if (identityFiles!.TryGetValue(g, out var f)) groupFiles[g] = f;
            }
            else sources[key] = "none";
        }
        // The notes on a taken model's answers stand only when the profile
        // takes its answers from that model.
        string answersFrom = sources[DeviceModel.GroupName(ModelGroup.Answers)];
        if (answersFrom == namedSource) notes.AddRange(namedNotes);
        else if (answersFrom == identityLabel) notes.AddRange(identityNotes);
        if (named != null && !nameUsed)
            notes.Add($"deviceModelFrom names '{p.DeviceModelFrom}', whose model fills no group this profile leaves empty, so the name is unused.");
        return new ResolvedModel
        {
            Model = model,
            Sources = sources,
            IdentityEntry = none ? null : identityLabel,
            IdentityModel = none ? null : identityModel,
            IdentityMatched = matched != null,
            Problem = problem,
            ProblemFile = problemFile,
            ProblemPath = problemPath,
            GroupFiles = groupFiles,
            Notes = notes,
        };
    }

    /// <summary>A profile as a problem names it: its id and its file.</summary>
    private static string ProfileLabel(ControllerProfile p) =>
        p.SourceFile != null ? $"profile '{p.Id}' ({p.SourceFile})" : $"profile '{p.Id}'";

    /// <summary>The catalog's registry, built once with the embedded
    /// catalog. Profiles built in code and profiles a program constructs
    /// resolve against it.</summary>
    internal static ModelRegistry Catalog => s_catalog.Value;

    /// <summary>The catalog's registry for profiles outside the catalog:
    /// the embedded models and identity table, and the catalog profiles a
    /// name can take, which load only when a name asks for one. A profile
    /// built in code, whose identity names no catalog profile, resolves
    /// without loading the catalog, where its first Build once paid the
    /// catalog's whole load.</summary>
    private static readonly Lazy<ModelRegistry> s_catalog = new(() =>
    {
        var (models, tables) = s_embedded.Value;
        return new ModelRegistry(models, tables, id => ProfileDatabase.EmbeddedProfile(id));
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
}

/// <summary>The checks a device model's keys meet, the profile's own when
/// it loads and a model it takes when it resolves: the answers and their
/// value sources (<see cref="Usbip.FeatureStubTable.Validate"/>), the
/// answers against the profile's descriptor
/// (<see cref="Usbip.FeatureStubTable.ValidateAgainst"/>), and the driver
/// PID. Throws <see cref="ProfileDefectException"/> with the JSON path.</summary>
internal static class AnswerModels
{
    internal static void Validate(ControllerProfile p, DeviceModel m, bool taken, List<string> notes)
    {
        Usbip.FeatureStubTable.Validate(m.FeatureStubs, m.ValueSources, p.RequiresUsbipBackend);
        if (m.FeatureStubs != null && p.HasDescriptor)
            Usbip.FeatureStubTable.ValidateAgainst(p, m.FeatureStubs, taken, notes);
        if (m.DriverPid != null) _ = ModelRegistry.ParseId(m.DriverPid, "$.driverPid");
    }
}
