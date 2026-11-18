using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HIDMaestro.Internal.Usbip;

/// <summary>
/// The XUSB functions: the USB devices an XUSB companion profile
/// (<c>xinput.via: "xusbCompanion"</c>) presents on the USB/IP backend,
/// which Windows gives its inbox XUSB driver, xusb22.sys. The devices are
/// data, the <c>usbFunction</c> of the catalog's XUSB model
/// (<c>profiles/models/xusb-companion.json</c>), read once. Each function
/// carries its device and configuration descriptors, its strings, the
/// values a profile writes into them, the input report SubmitState fills,
/// the messages it sends when the host configures it, the output reports it
/// answers or turns into the companion's packets, and the condition on the
/// profile that picks it. The identities no function takes are listed
/// beside them. This class names no device.
/// </summary>
internal static class XusbFunction
{
    /// <summary>The values a function's <c>fields</c> can name, with the
    /// width each is written at, little-endian.</summary>
    internal static readonly IReadOnlyDictionary<string, int> ValueWidths = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["vid"] = 2,
        ["pid"] = 2,
        ["subType"] = 1,
        ["information.vid"] = 2,
        ["information.pid"] = 2,
    };

    /// <summary>The values a message's <c>fields</c> can name, with their
    /// width. <c>serialHex</c> is the last eight hex digits of the device's
    /// serial as four bytes in the order they are written.</summary>
    internal static readonly IReadOnlyDictionary<string, int> MessageValueWidths = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["serialHex"] = 4,
        ["subType"] = 1,
    };

    /// <summary>A message the device sends on an IN endpoint: its bytes, and
    /// the values written into them for each device.</summary>
    internal sealed record Message(byte[] Template, (int At, string Value)[] Fields);

    /// <summary>One output report the device answers: a report that starts
    /// with <see cref="Match"/>, at least <see cref="MinLength"/> bytes
    /// long, on one of <see cref="Interfaces"/> (null for the interface the
    /// reports travel on). <see cref="Replies"/> go out on that interface's
    /// IN endpoint, then <see cref="Packet"/>, when there is one, goes to the
    /// output ring under <see cref="Source"/> with <see cref="Copies"/> taken
    /// from the report, each byte ANDed with its mask. A rule with no packet
    /// and no replies drops the report.</summary>
    internal sealed record OutputPacket(string Name, byte[] Match, int MinLength, byte[]? Interfaces, byte Source, byte[]? Packet,
                                        (int From, int To, byte Mask)[] Copies, Message[] Replies);

    /// <summary>One function, parsed and checked once.</summary>
    internal sealed record Function(
        string Name, string? WhenConnection,
        byte[] DeviceDescriptor, byte[] ConfigurationDescriptor, UsbConfigurationSpec Configuration,
        IReadOnlyDictionary<byte, string> Strings, (string Descriptor, int At, string Value)[] Fields,
        ExtendedReportSpec Report, OutputPacket[] OutputPackets, Message[] StartMessages);

    /// <summary>The model's functions and the identities none takes.</summary>
    internal sealed record Compiled(Function[] Functions, IReadOnlyList<(string Inf, ushort Vid, ushort Pid)> RefusedIds);

    static readonly Lazy<Compiled> s_model = new(() => Load(XusbAnswers.ModelResourceName));

    /// <summary>The catalog's XUSB functions.</summary>
    internal static Compiled Model => s_model.Value;

    static Compiled Load(string resource)
    {
        using Stream? s = typeof(XusbFunction).Assembly.GetManifestResourceStream(resource);
        if (s == null)
            throw new InvalidOperationException($"The SDK carries no {resource}.");
        using var reader = new StreamReader(s);
        return CompileJson(reader.ReadToEnd(), resource);
    }

    /// <summary>Reads a model file through the catalog's strict JSON options
    /// and compiles its <c>usbFunction</c>. Throws
    /// <see cref="InvalidOperationException"/> naming the JSON path of
    /// anything it cannot use.</summary>
    internal static Compiled CompileJson(string json, string source)
    {
        DeviceModelFile? file;
        try { file = JsonSerializer.Deserialize(json, HMJsonContext.Default.DeviceModelFile); }
        catch (JsonException e)
        {
            var (path, reason) = ProfileDefects.Locate(e);
            throw new InvalidOperationException($"{source}: {path}: {reason}", e);
        }
        var spec = file?.UsbFunction ?? throw new InvalidOperationException($"{source}: $.usbFunction is missing.");
        return Compile(spec, source);
    }

    static byte[] Hex(string? text, string at)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException($"{at} is missing.");
        try { return Convert.FromHexString(text.Replace(" ", "")); }
        catch (FormatException) { throw new InvalidOperationException($"{at} is not whole hex bytes."); }
    }

    internal static Compiled Compile(XusbFunctionsSpec spec, string source)
    {
        var functions = spec.Functions ?? throw new InvalidOperationException($"{source}: $.usbFunction.functions is missing.");
        if (functions.Count == 0) throw new InvalidOperationException($"{source}: $.usbFunction.functions is empty.");
        var compiled = new Function[functions.Count];
        for (int i = 0; i < compiled.Length; i++)
            compiled[i] = CompileFunction(functions[i] ?? throw new InvalidOperationException($"{source}: $.usbFunction.functions[{i}] is null."),
                                          source, $"$.usbFunction.functions[{i}]");
        if (compiled[^1].WhenConnection != null)
            throw new InvalidOperationException($"{source}: $.usbFunction.functions[{compiled.Length - 1}].when: the last function takes every profile the others leave, so it has no condition.");

        var refused = new List<(string, ushort, ushort)>();
        var lists = spec.RefusedIds ?? new List<XusbRefusedIdsSpec>();
        for (int i = 0; i < lists.Count; i++)
        {
            string at = $"{source}: $.usbFunction.refusedIds[{i}]";
            string inf = lists[i].Inf ?? throw new InvalidOperationException($"{at}.inf is missing.");
            foreach (var id in lists[i].Ids ?? new List<string>())
            {
                var parts = id.Split(':');
                if (parts.Length != 2
                    || !ushort.TryParse(parts[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort v)
                    || !ushort.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort pid))
                    throw new InvalidOperationException($"{at}.ids: \"{id}\" is not a VID:PID pair such as \"045E:02EA\".");
                refused.Add((inf, v, pid));
            }
        }
        return new Compiled(compiled, refused);
    }

    static Message CompileMessage(XusbMessageSpec? m, string at)
    {
        if (m == null) throw new InvalidOperationException($"{at} is null.");
        byte[] bytes = Hex(m.Bytes, $"{at}.bytes");
        if (bytes.Length is 0 or > 64) throw new InvalidOperationException($"{at}.bytes is {bytes.Length} bytes, not 1 to 64.");
        var fields = new (int, string)[(m.Fields ?? new List<XusbFieldSpec>()).Count];
        for (int f = 0; f < fields.Length; f++)
        {
            var field = m.Fields![f];
            string value = field.Value ?? "";
            if (!MessageValueWidths.TryGetValue(value, out int width))
                throw new InvalidOperationException($"{at}.fields[{f}].value is \"{value}\", not one of {string.Join(", ", MessageValueWidths.Keys)}.");
            if (field.At < 0 || field.At + width > bytes.Length)
                throw new InvalidOperationException($"{at}.fields[{f}] writes {width} bytes at {field.At}, past the message's {bytes.Length}.");
            fields[f] = (field.At, value);
        }
        return new Message(bytes, fields);
    }

    static Function CompileFunction(XusbFunctionSpec spec, string source, string root)
    {
        string At(string rest) => $"{source}: {root}.{rest}";
        string name = spec.Name ?? throw new InvalidOperationException(At("name is missing."));
        var cfg = spec.UsbConfiguration ?? throw new InvalidOperationException(At("usbConfiguration is missing."));
        byte[] device = Hex(cfg.DeviceDescriptorHex, At("usbConfiguration.deviceDescriptor"));
        byte[] configuration = Hex(cfg.ConfigurationDescriptorHex, At("usbConfiguration.configurationDescriptor"));
        if (device.Length != 18)
            throw new InvalidOperationException(At($"usbConfiguration.deviceDescriptor is {device.Length} bytes, not 18."));
        if (cfg.Interfaces is not { Count: > 0 } || !cfg.Interfaces.Exists(x => x?.Function == "xusb"))
            throw new InvalidOperationException(At("usbConfiguration.interfaces names no interface \"xusb\", the one the reports travel on."));

        var strings = new Dictionary<byte, string>();
        foreach (var kv in spec.Strings ?? new Dictionary<string, string>())
        {
            if (!byte.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out byte index) || index is 0 or 0xEE)
                throw new InvalidOperationException(At($"strings.\"{kv.Key}\" is not a string index from 1 to 255 other than 0xEE."));
            strings[index] = kv.Value ?? throw new InvalidOperationException(At($"strings.\"{kv.Key}\" is null."));
        }

        var fields = new (string, int, string)[(spec.Fields ?? new List<XusbFunctionFieldSpec>()).Count];
        for (int i = 0; i < fields.Length; i++)
        {
            var f = spec.Fields![i];
            string fat = $"fields[{i}]";
            byte[] target = f.Descriptor switch
            {
                "device" => device,
                "configuration" => configuration,
                _ => throw new InvalidOperationException(At($"{fat}.descriptor is \"{f.Descriptor}\", not \"device\" or \"configuration\".")),
            };
            if (f.Value == null || !ValueWidths.TryGetValue(f.Value, out int width))
                throw new InvalidOperationException(At($"{fat}.value is \"{f.Value}\", not one of {string.Join(", ", ValueWidths.Keys)}."));
            if (f.At < 0 || f.At + width > target.Length)
                throw new InvalidOperationException(At($"{fat} writes {width} bytes at {f.At}, past the {target.Length}-byte {f.Descriptor} descriptor."));
            fields[i] = (f.Descriptor!, f.At, f.Value);
        }

        var report = spec.Report ?? throw new InvalidOperationException(At("report is missing."));
        if (report.Size <= 0 || !report.AlwaysArmed)
            throw new InvalidOperationException(At("report needs a size and alwaysArmed true: it is the one report the function sends."));
        try { _ = report.ReportIdByte; _ = VendorBlobProgram.Get(report); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new InvalidOperationException(At($"report does not compile: {e.Message}"), e);
        }

        var interfaceNumbers = cfg.Interfaces.Select(x => x.InterfaceNumber).ToHashSet();
        var packets = new OutputPacket[(spec.OutputPackets ?? new List<XusbOutputPacketSpec>()).Count];
        for (int i = 0; i < packets.Length; i++)
        {
            var p = spec.OutputPackets![i];
            string pat = $"outputPackets[{i}]";
            byte[] match = Hex(p.Match, At($"{pat}.match"));
            byte[]? packet = p.Packet == null ? null : Hex(p.Packet, At($"{pat}.packet"));
            byte sourceByte = p.Packet == null ? (byte)0 : p.Source switch
            {
                "xinput" => (byte)HMOutputSource.XInput,
                "hidOutput" => (byte)HMOutputSource.HidOutput,
                _ => throw new InvalidOperationException(At($"{pat}.source is \"{p.Source}\", not \"xinput\" or \"hidOutput\".")),
            };
            int min = Math.Max(p.MinLength, match.Length);
            var copies = new (int, int, byte)[(p.Copy ?? new List<XusbCopySpec>()).Count];
            if (copies.Length > 0 && packet == null)
                throw new InvalidOperationException(At($"{pat}.copy has no packet to copy into."));
            for (int c = 0; c < copies.Length; c++)
            {
                var cp = p.Copy![c];
                if (cp.From < 0 || cp.From >= min || cp.To < 0 || cp.To >= packet!.Length)
                    throw new InvalidOperationException(At($"{pat}.copy[{c}] copies byte {cp.From} to {cp.To}: the report's minLength is {min} and the packet {packet!.Length} bytes."));
                if (cp.Mask is < 1 or > 255)
                    throw new InvalidOperationException(At($"{pat}.copy[{c}].mask {cp.Mask} is not 1 to 255."));
                copies[c] = (cp.From, cp.To, (byte)(cp.Mask ?? 0xFF));
            }
            if (packet != null && packet.Length is 0 or > SharedMemoryIO.DATA_CAPACITY)
                throw new InvalidOperationException(At($"{pat}.packet is {packet.Length} bytes, not 1 to {SharedMemoryIO.DATA_CAPACITY}."));
            byte[]? on = null;
            if (p.Interfaces != null)
            {
                on = new byte[p.Interfaces.Count];
                for (int n = 0; n < on.Length; n++)
                {
                    if (p.Interfaces[n] is < 0 or > 255 || !interfaceNumbers.Contains((byte)p.Interfaces[n]))
                        throw new InvalidOperationException(At($"{pat}.interfaces[{n}] {p.Interfaces[n]} is no interface of the configuration."));
                    on[n] = (byte)p.Interfaces[n];
                }
            }
            var replies = new Message[(p.Replies ?? new List<XusbMessageSpec>()).Count];
            for (int r = 0; r < replies.Length; r++) replies[r] = CompileMessage(p.Replies![r], At($"{pat}.replies[{r}]"));
            packets[i] = new OutputPacket(p.Name ?? pat, match, min, on, sourceByte, packet, copies, replies);
        }

        var start = new Message[(spec.StartMessages ?? new List<XusbMessageSpec>()).Count];
        for (int i = 0; i < start.Length; i++) start[i] = CompileMessage(spec.StartMessages![i], At($"startMessages[{i}]"));

        string? when = spec.When?.Connection;
        if (spec.When != null && when == null)
            throw new InvalidOperationException(At("when names no condition. The one it takes is connection."));

        // The configuration's structure, with the blobs written without
        // spaces, as the descriptor set reads them.
        var configuration0 = new UsbConfigurationSpec
        {
            ConfigurationValue = cfg.ConfigurationValue,
            Attributes = cfg.Attributes,
            MaxPowerMilliamps = cfg.MaxPowerMilliamps,
            BusSpeed = cfg.BusSpeed,
            DeviceDescriptorHex = Convert.ToHexString(device),
            ConfigurationDescriptorHex = Convert.ToHexString(configuration),
            Interfaces = cfg.Interfaces,
            MicrosoftOs = cfg.MicrosoftOs,
        };
        return new Function(name, when, device, configuration, configuration0, strings, fields, report, packets, start);
    }

    /// <summary>The function <paramref name="profile"/> presents: the first
    /// whose condition its data meets.</summary>
    internal static Function For(ControllerProfile profile)
    {
        foreach (var f in Model.Functions)
            if (f.WhenConnection == null || string.Equals(f.WhenConnection, profile.Connection, StringComparison.Ordinal))
                return f;
        return Model.Functions[^1];
    }

    /// <summary>The resolved XInput subtype of <paramref name="profile"/>,
    /// the one the UMDF2 companion answers.</summary>
    internal static byte SubType(ControllerProfile profile)
    {
        var xi = XInputPresentation.Resolve(profile);
        return (xi.Spec is { } x ? XInputPresentation.SubTypeByte(profile.Id, x) : null) ?? XusbAnswers.Model.SubType;
    }

    /// <summary>The configuration <paramref name="profile"/> presents: its
    /// function's blobs with the profile's values written at the function's
    /// fields.</summary>
    internal static UsbConfigurationSpec Build(ControllerProfile profile, Function f)
    {
        var device = (byte[])f.DeviceDescriptor.Clone();
        var configuration = (byte[])f.ConfigurationDescriptor.Clone();
        var info = XInputPresentation.Resolve(profile).Spec?.Information;
        foreach (var (descriptor, at, value) in f.Fields)
        {
            uint v = value switch
            {
                "vid" => profile.VendorId,
                "pid" => profile.ProductId,
                "subType" => SubType(profile),
                "information.vid" => info?.Vid is { } iv ? XInputPresentation.ParseId(iv, "$.xinput.information.vid") : profile.VendorId,
                "information.pid" => info?.Pid is { } ip ? XInputPresentation.ParseId(ip, "$.xinput.information.pid") : profile.ProductId,
                _ => throw new InvalidOperationException($"The XUSB function names the value \"{value}\", which has no source."),
            };
            var target = descriptor == "device" ? device : configuration;
            for (int b = 0; b < ValueWidths[value]; b++)
                target[at + b] = (byte)(v >> (8 * b));
        }
        var c = f.Configuration;
        return new UsbConfigurationSpec
        {
            ConfigurationValue = c.ConfigurationValue,
            Attributes = c.Attributes,
            MaxPowerMilliamps = c.MaxPowerMilliamps,
            BusSpeed = c.BusSpeed,
            DeviceDescriptorHex = Convert.ToHexString(device),
            ConfigurationDescriptorHex = Convert.ToHexString(configuration),
            Interfaces = c.Interfaces,
            MicrosoftOs = c.MicrosoftOs,
        };
    }

    /// <summary>The bytes of <paramref name="m"/> for one device: its serial
    /// and the profile's subtype written at the message's fields.</summary>
    internal static byte[] Bytes(Message m, string? serial, byte subType)
    {
        var b = (byte[])m.Template.Clone();
        foreach (var (at, value) in m.Fields)
        {
            if (value == "subType") { b[at] = subType; continue; }
            // serialHex: the last eight hex digits of the serial, in the
            // order they are written, as four bytes. A serial with fewer
            // hex digits at its end leaves the template's bytes before them.
            string digits = new string((serial ?? "").Reverse().TakeWhile(Uri.IsHexDigit).Take(8).Reverse().ToArray());
            int bytes = digits.Length / 2;
            for (int i = 0; i < bytes; i++)
                b[at + 4 - bytes + i] = byte.Parse(digits.AsSpan(digits.Length - 2 * (bytes - i), 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }
        return b;
    }

    /// <summary>Why <paramref name="profile"/>'s identity is one the XUSB
    /// function never takes, or null. A profile whose XInput presentation is
    /// the companion is refused for such an identity on either backend, at
    /// load when its own block names the companion and at create when a
    /// model gives it: on UMDF2 the companion's &amp;IG_00 HID child of that
    /// identity takes xinputhid, which publishes the XUSB interface a second
    /// time.</summary>
    internal static string? RefusedIdentityReason(ControllerProfile profile, bool companion = false)
    {
        // The identity first, which is cheap, then the presentation, which
        // resolves the profile's device model unless the caller knows it.
        ushort vid = profile.VendorId, pid = profile.ProductId;
        var infs = Model.RefusedIds.Where(r => r.Vid == vid && r.Pid == pid).Select(r => r.Inf).Distinct().ToList();
        if (infs.Count == 0 || !(companion || XInputPresentation.Resolve(profile).XusbCompanion)) return null;
        return $"{vid:X4}:{pid:X4} is an identity Windows' {string.Join(" and ", infs)} {(infs.Count == 1 ? "binds" : "bind")}, and an XUSB companion's controller " +
               "takes xusb22 alone: with that identity Windows would give the one controller a second XInput provider or its GIP driver in xusb22's place.";
    }
}

/// <summary>The <c>usbFunction</c> of a model file
/// (<see cref="DeviceModelFile"/>): the functions and the identities none
/// takes.</summary>
internal sealed class XusbFunctionsSpec
{
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    /// <summary>The functions, in order: a profile presents the first whose
    /// <c>when</c> its data meets, and the last has no condition.</summary>
    [JsonPropertyName("functions")]
    public List<XusbFunctionSpec>? Functions { get; set; }

    [JsonPropertyName("refusedIds")]
    public List<XusbRefusedIdsSpec>? RefusedIds { get; set; }
}

/// <summary>One XUSB function.</summary>
internal sealed class XusbFunctionSpec
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("when")]
    public XusbWhenSpec? When { get; set; }

    [JsonPropertyName("usbConfiguration")]
    public UsbConfigurationSpec? UsbConfiguration { get; set; }

    /// <summary>String descriptors by index, which the device serves in
    /// place of the profile's strings. Index 3, the serial, is the
    /// controller's own unless given here.</summary>
    [JsonPropertyName("strings")]
    public Dictionary<string, string>? Strings { get; set; }

    [JsonPropertyName("fields")]
    public List<XusbFunctionFieldSpec>? Fields { get; set; }

    [JsonPropertyName("report")]
    public ExtendedReportSpec? Report { get; set; }

    /// <summary>Messages the reporting interface sends, in order, on its
    /// first reads after the device is attached.</summary>
    [JsonPropertyName("startMessages")]
    public List<XusbMessageSpec>? StartMessages { get; set; }

    [JsonPropertyName("outputPackets")]
    public List<XusbOutputPacketSpec>? OutputPackets { get; set; }
}

/// <summary>What a profile's data must hold for a function to be its.</summary>
internal sealed class XusbWhenSpec
{
    /// <summary>The profile's <c>connection</c>.</summary>
    [JsonPropertyName("connection")]
    public string? Connection { get; set; }
}

/// <summary>A value the function writes into one of its descriptors.</summary>
internal sealed class XusbFunctionFieldSpec
{
    /// <summary><c>"device"</c> or <c>"configuration"</c>.</summary>
    [JsonPropertyName("descriptor")]
    public string? Descriptor { get; set; }

    [JsonPropertyName("at")]
    public int At { get; set; }

    /// <summary>One of <see cref="XusbFunction.ValueWidths"/>.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

/// <summary>A message a function sends: its bytes, hex, and the values
/// written into them (<see cref="XusbFunction.MessageValueWidths"/>).</summary>
internal sealed class XusbMessageSpec
{
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("bytes")]
    public string? Bytes { get; set; }

    [JsonPropertyName("fields")]
    public List<XusbFieldSpec>? Fields { get; set; }
}

/// <summary>An output report the function answers.</summary>
internal sealed class XusbOutputPacketSpec
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The bytes the report starts with, hex.</summary>
    [JsonPropertyName("match")]
    public string? Match { get; set; }

    [JsonPropertyName("minLength")]
    public int MinLength { get; set; }

    /// <summary>The interfaces whose OUT endpoint the rule reads, or absent
    /// for the one the reports travel on.</summary>
    [JsonPropertyName("interfaces")]
    public List<int>? Interfaces { get; set; }

    /// <summary>Messages sent back on the same interface's IN endpoint.</summary>
    [JsonPropertyName("replies")]
    public List<XusbMessageSpec>? Replies { get; set; }

    /// <summary><c>"xinput"</c> or <c>"hidOutput"</c>, with a packet.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>The packet's bytes before the copies, hex. Absent for a rule
    /// that publishes nothing.</summary>
    [JsonPropertyName("packet")]
    public string? Packet { get; set; }

    [JsonPropertyName("copy")]
    public List<XusbCopySpec>? Copy { get; set; }
}

/// <summary>One byte of the report copied into the packet.</summary>
internal sealed class XusbCopySpec
{
    [JsonPropertyName("from")]
    public int From { get; set; }

    [JsonPropertyName("to")]
    public int To { get; set; }

    /// <summary>The bits of the byte that are copied, all when absent.</summary>
    [JsonPropertyName("mask")]
    public int? Mask { get; set; }
}

/// <summary>Identities an INF binds that no function takes.</summary>
internal sealed class XusbRefusedIdsSpec
{
    [JsonPropertyName("inf")]
    public string? Inf { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("ids")]
    public List<string>? Ids { get; set; }
}
