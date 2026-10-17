using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HIDMaestro.Internal;

/// <summary>The answers an XUSB companion gives, compiled at create from the
/// profile's resolved <c>xinput</c> block into the <c>XusbAnswers</c> blob
/// the companion reads at add (<c>companion.c</c>, CompanionLoadAnswers).
/// The companion holds no answer of its own, and neither does this class:
/// the replies, their layouts and the values a block leaves out are the
/// catalog's XUSB model, <c>profiles/models/xusb-companion.json</c>,
/// embedded under <see cref="ModelResourceName"/> and read once. Each
/// answer is the model's bytes with this controller's values written at
/// the offsets the model names.
///
/// <para>Blob, little-endian: <c>Length</c> (u32, the whole blob),
/// <c>Format</c> (u32, <see cref="Format"/>), <c>Count</c> (u32), then
/// <c>Count</c> entries of 12 bytes: <c>IoControlCode</c> (u32),
/// <c>MinOutput</c> (u16), <c>Mode</c> (u16), <c>Offset</c> (u16, into the
/// arena after the entries) and <c>Size</c> (u16), then the arena. A
/// request takes the entry of its code with the largest
/// <c>MinOutput</c> its output buffer meets, or the smallest one when it
/// meets none. <see cref="ModeExact"/> completes with all <c>Size</c>
/// bytes or STATUS_BUFFER_TOO_SMALL, <see cref="ModeUpTo"/> with as many as
/// the buffer holds.</para></summary>
internal static class XusbAnswers
{
    /// <summary>The blob format of protocol 4. A later protocol adds
    /// values and changes none.</summary>
    internal const uint Format = 4;
    /// <summary>The companion's limits, the HM_XUSB_ANSWERS_* constants of
    /// <c>companion.c</c>, which a test compares with these.</summary>
    internal const int MaxEntries = 8;
    internal const int MaxData = 64;
    internal const int MaxBlob = 512;
    internal const int HeaderSize = 12;
    internal const int EntrySize = 12;

    internal const ushort ModeExact = 0;
    internal const ushort ModeUpTo = 1;

    internal const string ModelResourceName = "HIDMaestro.Models.xusb-companion.json";

    /// <summary>The values a model's <c>fields</c> can name, with the
    /// width each is written at, little-endian.</summary>
    internal static readonly IReadOnlyDictionary<string, int> ValueWidths = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["subType"] = 1,
        ["flags"] = 2,
        ["information.vid"] = 2,
        ["information.pid"] = 2,
        ["capabilities.vid"] = 2,
        ["capabilities.pid"] = 2,
        ["battery.type"] = 1,
        ["battery.level"] = 1,
        ["controllerIndex"] = 1,
    };

    /// <summary>A model's answer, parsed once.</summary>
    internal sealed record CompiledAnswer(uint Code, ushort MinOutput, ushort Mode, byte[] Template,
                                          (int At, string Value)[] Fields);

    /// <summary>A model, parsed and checked once: its answers and the
    /// values a profile's block leaves out.</summary>
    internal sealed record CompiledModel(string Id, CompiledAnswer[] Answers, byte SubType, ushort Flags,
                                         byte BatteryType, byte BatteryLevel);

    static readonly Lazy<CompiledModel> s_model = new(() => LoadModel(ModelResourceName));

    /// <summary>The catalog's XUSB model.</summary>
    internal static CompiledModel Model => s_model.Value;

    static CompiledModel LoadModel(string resource)
    {
        using Stream? s = typeof(XusbAnswers).Assembly.GetManifestResourceStream(resource);
        if (s == null)
            throw new InvalidOperationException($"The SDK carries no {resource}.");
        using var reader = new StreamReader(s);
        return CompileModelJson(reader.ReadToEnd(), resource);
    }

    /// <summary>Reads a model file through the catalog's strict JSON
    /// options, which refuse a key the format lacks and a key written
    /// twice, then compiles its <c>xusbAnswers</c>. Throws
    /// <see cref="InvalidOperationException"/> naming the JSON path.</summary>
    internal static CompiledModel CompileModelJson(string json, string source)
    {
        DeviceModelFile? file;
        try { file = JsonSerializer.Deserialize(json, HMJsonContext.Default.DeviceModelFile); }
        catch (JsonException e)
        {
            var (path, reason) = ProfileDefects.Locate(e);
            throw new InvalidOperationException($"{source}: {path}: {reason}", e);
        }
        if (file == null) throw new InvalidOperationException($"{source}: $ is empty.");
        var spec = file.XusbAnswers ?? throw new InvalidOperationException($"{source}: $.xusbAnswers is missing.");
        return CompileModel(spec, string.IsNullOrEmpty(file.Id) ? source : file.Id, source);
    }

    /// <summary>Parses and checks a model's <c>xusbAnswers</c>. Throws
    /// <see cref="InvalidOperationException"/> naming the JSON path of
    /// anything it cannot use: a value out of range, a field past its
    /// answer, and a blob past the companion's limits.</summary>
    internal static CompiledModel CompileModel(XusbModelSpec model, string id, string source)
    {
        const string root = "$.xusbAnswers";
        var d = model.Defaults ?? throw new InvalidOperationException($"{source}: {root}.defaults is missing.");
        XInputPresentation.Validate(id, d, root + ".defaults");
        byte subType = XInputPresentation.SubTypeByte(id, d, root + ".defaults")
            ?? throw new InvalidOperationException($"{source}: {root}.defaults.subType is missing.");
        ushort flags = XInputPresentation.FlagWord(id, d, root + ".defaults")
            ?? throw new InvalidOperationException($"{source}: {root}.defaults.flags is missing.");
        var (batteryType, batteryLevel) = XInputPresentation.BatteryBytes(id, d, root + ".defaults");
        if (batteryType == null || batteryLevel == null)
            throw new InvalidOperationException($"{source}: {root}.defaults.battery needs a type and a level.");

        var answers = model.Answers ?? throw new InvalidOperationException($"{source}: {root}.answers is missing.");
        var compiled = new CompiledAnswer[answers.Count];
        for (int i = 0; i < answers.Count; i++)
        {
            var a = answers[i];
            string at = $"{source}: {root}.answers[{i}]";
            string code = a.Ioctl ?? throw new InvalidOperationException($"{at}.ioctl is missing.");
            if (!code.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || !uint.TryParse(code.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint ioctl))
                throw new InvalidOperationException($"{at}.ioctl is \"{code}\", not a hex control code.");
            ushort mode = a.Mode switch
            {
                "exact" => ModeExact,
                "upTo" => ModeUpTo,
                _ => throw new InvalidOperationException($"{at}.mode is \"{a.Mode}\", not \"exact\" or \"upTo\"."),
            };
            if (a.MinOutput is < 0 or > ushort.MaxValue)
                throw new InvalidOperationException($"{at}.minOutput is {a.MinOutput}, not 0 to 65535.");
            byte[] bytes;
            try { bytes = Convert.FromHexString((a.Bytes ?? "").Replace(" ", "")); }
            catch (FormatException) { throw new InvalidOperationException($"{at}.bytes is not hex bytes."); }
            int size = a.Size ?? bytes.Length;
            if (size < bytes.Length || size < 1 || size > MaxData)
                throw new InvalidOperationException($"{at}.size is {size}, not from the {bytes.Length} bytes given to the companion's {MaxData}.");
            var template = new byte[size];
            bytes.CopyTo(template, 0);
            var fields = new (int, string)[(a.Fields ?? new List<XusbFieldSpec>()).Count];
            for (int f = 0; f < fields.Length; f++)
            {
                var field = a.Fields![f];
                string value = field.Value ?? "";
                if (!ValueWidths.TryGetValue(value, out int width))
                    throw new InvalidOperationException(
                        $"{at}.fields[{f}].value is \"{value}\", not one of {string.Join(", ", ValueWidths.Keys)}.");
                if (field.At < 0 || field.At + width > size)
                    throw new InvalidOperationException($"{at}.fields[{f}] writes {width} bytes at {field.At}, past the answer's {size}.");
                fields[f] = (field.At, value);
            }
            compiled[i] = new CompiledAnswer(ioctl, (ushort)a.MinOutput, mode, template, fields);
        }
        if (compiled.Length > MaxEntries)
            throw new InvalidOperationException($"{source}: {compiled.Length} answers, past the companion's {MaxEntries}.");
        int blob = HeaderSize + compiled.Length * EntrySize + compiled.Sum(c => c.Template.Length);
        if (blob > MaxBlob)
            throw new InvalidOperationException($"{source}: {root}.answers make a blob of {blob} bytes, past the companion's {MaxBlob}.");
        return new CompiledModel(id, compiled, subType, flags, batteryType.Value, batteryLevel.Value);
    }

    /// <summary>The blob for <paramref name="profile"/> at transport index
    /// <paramref name="controllerIndex"/>, or null for a profile whose
    /// presentation makes no companion.</summary>
    internal static byte[]? Compile(ControllerProfile profile, int controllerIndex)
    {
        var xi = XInputPresentation.Resolve(profile);
        if (!xi.XusbCompanion) return null;
        var x = xi.Spec!;
        var m = Model;
        string id = profile.Id;
        byte subType = XInputPresentation.SubTypeByte(id, x) ?? m.SubType;
        ushort flags = XInputPresentation.FlagWord(id, x) ?? m.Flags;
        var (batteryType, batteryLevel) = XInputPresentation.BatteryBytes(id, x);
        ushort infoVid = profile.VendorId, infoPid = profile.ProductId;
        if (x.Information is { } info)
        {
            infoVid = XInputPresentation.ParseId(info.Vid!, "$.xinput.information.vid");
            infoPid = XInputPresentation.ParseId(info.Pid!, "$.xinput.information.pid");
        }
        ushort capVid = profile.VendorId, capPid = profile.ProductId;
        if (x.Capabilities?.Vid is { } cv) capVid = XInputPresentation.ParseId(cv, "$.xinput.capabilities.vid");
        if (x.Capabilities?.Pid is { } cp) capPid = XInputPresentation.ParseId(cp, "$.xinput.capabilities.pid");

        uint ValueOf(string name) => name switch
        {
            "subType" => subType,
            "flags" => flags,
            "information.vid" => infoVid,
            "information.pid" => infoPid,
            "capabilities.vid" => capVid,
            "capabilities.pid" => capPid,
            "battery.type" => batteryType ?? m.BatteryType,
            "battery.level" => batteryLevel ?? m.BatteryLevel,
            "controllerIndex" => (uint)controllerIndex,
            _ => throw new InvalidOperationException($"XUSB model '{m.Id}' names the value \"{name}\", which has no source."),
        };

        var entries = new (uint, ushort, ushort, byte[])[m.Answers.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            var a = m.Answers[i];
            var data = (byte[])a.Template.Clone();
            foreach (var (at, value) in a.Fields)
            {
                uint v = ValueOf(value);
                for (int b = 0; b < ValueWidths[value]; b++)
                    data[at + b] = (byte)(v >> (8 * b));
            }
            entries[i] = (a.Code, a.MinOutput, a.Mode, data);
        }
        return Build(entries);
    }

    /// <summary>Lays the entries out as the blob, within the companion's
    /// limits.</summary>
    internal static byte[] Build(IReadOnlyList<(uint Code, ushort MinOutput, ushort Mode, byte[] Data)> entries)
    {
        if (entries.Count > MaxEntries)
            throw new InvalidOperationException($"XUSB answers: {entries.Count} entries, past the companion's {MaxEntries}.");
        int arena = 0;
        foreach (var e in entries)
        {
            if (e.Data.Length > MaxData)
                throw new InvalidOperationException($"XUSB answers: an answer of {e.Data.Length} bytes, past the companion's {MaxData}.");
            arena += e.Data.Length;
        }
        int length = HeaderSize + entries.Count * EntrySize + arena;
        if (length > MaxBlob)
            throw new InvalidOperationException($"XUSB answers: a blob of {length} bytes, past the companion's {MaxBlob}.");
        var blob = new byte[length];
        var s = blob.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], Format);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)entries.Count);
        int at = HeaderSize, offset = 0, data = HeaderSize + entries.Count * EntrySize;
        foreach (var e in entries)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s[at..], e.Code);
            BinaryPrimitives.WriteUInt16LittleEndian(s[(at + 4)..], e.MinOutput);
            BinaryPrimitives.WriteUInt16LittleEndian(s[(at + 6)..], e.Mode);
            BinaryPrimitives.WriteUInt16LittleEndian(s[(at + 8)..], (ushort)offset);
            BinaryPrimitives.WriteUInt16LittleEndian(s[(at + 10)..], (ushort)e.Data.Length);
            e.Data.CopyTo(blob, data + offset);
            offset += e.Data.Length;
            at += EntrySize;
        }
        return blob;
    }
}

/// <summary>The <c>xusbAnswers</c> of a model file
/// (<see cref="DeviceModelFile"/>), <c>profiles/models/xusb-companion.json</c>.</summary>
internal sealed class XusbModelSpec
{
    /// <summary>The values a profile's <c>xinput</c> block leaves out, in
    /// the block's own form.</summary>
    [JsonPropertyName("defaults")]
    public XInputSpec? Defaults { get; set; }

    [JsonPropertyName("answers")]
    public List<XusbAnswerSpec>? Answers { get; set; }
}

/// <summary>One answer of an XUSB model.</summary>
internal sealed class XusbAnswerSpec
{
    /// <summary>What the answer is, for a reader.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The control code, hex: <c>"0x8000E004"</c>.</summary>
    [JsonPropertyName("ioctl")]
    public string? Ioctl { get; set; }

    /// <summary>The output buffer size from which this answer applies.</summary>
    [JsonPropertyName("minOutput")]
    public int MinOutput { get; set; }

    /// <summary><c>"exact"</c> or <c>"upTo"</c>.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>The answer's bytes, hex, spaces allowed.</summary>
    [JsonPropertyName("bytes")]
    public string? Bytes { get; set; }

    /// <summary>The answer's length when longer than <c>bytes</c>, the
    /// rest zeros.</summary>
    [JsonPropertyName("size")]
    public int? Size { get; set; }

    [JsonPropertyName("fields")]
    public List<XusbFieldSpec>? Fields { get; set; }
}

/// <summary>A value an answer carries: one of
/// <see cref="XusbAnswers.ValueWidths"/>, written at <c>at</c>.</summary>
internal sealed class XusbFieldSpec
{
    [JsonPropertyName("at")]
    public int At { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}
