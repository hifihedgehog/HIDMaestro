using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HIDMaestro.Internal.Programs;

namespace HIDMaestro.Internal;

/// <summary>The model groups of one profile or model file, as the shared
/// parse reads them.</summary>
public sealed class DeviceModelGroups
{
    public ProtocolSpec? Protocol { get; set; }
    public StateBlockSpec? StateBlock { get; set; }
    public List<InputReportSpec>? InputReports { get; set; }
    public List<LiveFeatureReportSpec>? LiveFeatureReports { get; set; }

    public bool IsEmpty => Protocol == null && StateBlock == null && InputReports == null && LiveFeatureReports == null;
}

/// <summary>The one JSON context that reads the model groups, compiled into
/// the SDK and into the build tool. Strict: a key the model lacks, or one
/// written twice, is refused with its path.</summary>
[JsonSourceGenerationOptions(
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProtocolSpec))]
[JsonSerializable(typeof(StateBlockSpec))]
[JsonSerializable(typeof(List<InputReportSpec>))]
[JsonSerializable(typeof(List<LiveFeatureReportSpec>))]
internal sealed partial class ModelJsonContext : JsonSerializerContext { }

/// <summary>Reads the model groups of one profile or model file. The build
/// tool and the run-time loader both call this, so a group parses one way and
/// an error carries one JSON path in both. The file's root is the profile
/// loader's to read: this reads only the keys <see cref="GroupKeys"/> names,
/// through the strict shared context.</summary>
public static class ProfilePrograms
{
    /// <summary>The root keys this reads.</summary>
    public static readonly string[] GroupKeys = { "protocol", "stateBlock", "inputReports", "liveFeatureReports" };

    /// <summary>The document is read leniently about repeated keys, so the
    /// strict context, reading each group, names a repeated key by its
    /// path.</summary>
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static DeviceModelGroups Read(ReadOnlyMemory<byte> utf8)
    {
        utf8 = WithoutBom(utf8);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(utf8, DocumentOptions); }
        catch (JsonException e) { throw new FormException("$", $"the file does not parse: {e.Message}"); }
        using (doc) return Read(doc.RootElement);
    }

    public static DeviceModelGroups Read(JsonElement root)
    {
        var groups = new DeviceModelGroups();
        if (root.ValueKind != JsonValueKind.Object) return groups;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
        {
            string key = prop.Name;
            if (string.Equals(key, "usbConfiguration", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Object)
            {
                // Before v2.0.0 the protocol sat here. The SDK's context no
                // longer maps it and would skip it, leaving a device with no
                // protocol.
                foreach (var inner in prop.Value.EnumerateObject())
                    if (string.Equals(inner.Name, "protocol", StringComparison.OrdinalIgnoreCase))
                        throw new FormException($"$.{key}.{inner.Name}", "the protocol is the root key \"protocol\" since v2.0.0, beside usbConfiguration");
                continue;
            }
            if (Array.IndexOf(GroupKeys, key) < 0)
            {
                // The SDK's context reads a profile's keys in any case and
                // skips these four, so a case variant would load as a profile
                // without the group.
                foreach (var g in GroupKeys)
                    if (string.Equals(key, g, StringComparison.OrdinalIgnoreCase))
                        throw new FormException("$." + key, $"the key is spelled \"{key}\", and the model group is \"{g}\"");
                continue;
            }
            if (!seen.Add(key)) throw new FormException("$." + key, "the key is written twice");
            try
            {
                switch (key)
                {
                    case "protocol": groups.Protocol = prop.Value.Deserialize(ModelJsonContext.Default.ProtocolSpec); break;
                    case "stateBlock": groups.StateBlock = prop.Value.Deserialize(ModelJsonContext.Default.StateBlockSpec); break;
                    case "inputReports": groups.InputReports = prop.Value.Deserialize(ModelJsonContext.Default.ListInputReportSpec); break;
                    case "liveFeatureReports": groups.LiveFeatureReports = prop.Value.Deserialize(ModelJsonContext.Default.ListLiveFeatureReportSpec); break;
                }
            }
            catch (JsonException e)
            {
                // The serializer's path is relative to the element it read.
                string inner = e.Path is { Length: > 1 } p ? p[1..] : "";
                throw new FormException("$." + key + inner, FirstLine(e.Message));
            }
        }
        return groups;
    }

    /// <summary>The bytes after a UTF-8 byte order mark, which some files
    /// carry and a JSON reader does not skip.</summary>
    public static ReadOnlyMemory<byte> WithoutBom(ReadOnlyMemory<byte> utf8) =>
        utf8.Length >= 3 && utf8.Span[0] == 0xEF && utf8.Span[1] == 0xBB && utf8.Span[2] == 0xBF ? utf8[3..] : utf8;

    static string FirstLine(string message)
    {
        // The serializer appends "Path: ... | LineNumber: ..." to its own
        // message. The path is carried separately.
        int cut = message.IndexOf(" Path: ", StringComparison.Ordinal);
        return cut > 0 ? message[..cut] : message;
    }
}

/// <summary>The spellings model text uses, parsed one way everywhere.</summary>
internal static class ModelText
{
    /// <summary>Hex, with or without 0x, up to 32 bits.</summary>
    public static bool TryParseHex(string? text, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return s.Length is > 0 and <= 8 && uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Hex bytes, spaces allowed, with or without 0x.</summary>
    public static bool TryParseBytes(string? text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (text == null) return false;
        string s = text.Replace(" ", "", StringComparison.Ordinal);
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length == 0 || (s.Length & 1) != 0) return false;
        try { bytes = Convert.FromHexString(s); return true; }
        catch (FormatException) { return false; }
    }

    /// <summary>A range "a-b" or a single offset "a", inclusive.</summary>
    public static bool TryParseRange(string? text, out int lo, out int hi)
    {
        lo = hi = -1;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        int dash = s.IndexOf('-');
        if (dash < 0)
        {
            if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out lo)) return false;
            hi = lo;
            return true;
        }
        return int.TryParse(s.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out lo)
            && int.TryParse(s.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out hi)
            && hi >= lo;
    }
}
