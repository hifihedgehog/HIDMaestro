using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HIDMaestro.Internal.Usbip;

/// <summary>A profile's <c>gip</c> block: the identity of the catalog's GIP
/// model (<c>profiles/models/gip-gamepad.json</c>) the profile presents on
/// the USB/IP backend when it reaches XInput through xinputhid.</summary>
public sealed class GipSpec
{
    /// <summary>A key of the model's <c>gipFunction.identities</c>.</summary>
    [JsonPropertyName("identity")]
    public string? Identity { get; set; }
}

/// <summary>The GIP function: the USB device a profile whose resolved
/// <c>xinput.via</c> is <c>xinputhid</c> presents on the USB/IP backend. It
/// is a gamepad that speaks the Gaming Input Protocol on one vendor
/// interface, which Windows gives its own GIP stack (dc1-controller.sys with
/// xboxgip.sys above it), and whose HID child xinputhid.sys gives an XInput
/// slot. The device is the catalog's GIP model, embedded under
/// <see cref="ModelResourceName"/>: a profile's groups (usbConfiguration,
/// stateBlock, inputReports, protocol) with slots for each identity's bytes,
/// and the identities. <see cref="FormOf"/> fills the slots for one
/// profile's identity, VID and PID, and returns the USB/IP form of the
/// profile: a profile with those groups, as
/// <c>profiles/microsoft/xbox-one-s-full.json</c> carries them, which the
/// backend presents as it presents any authored persona. The code knows no
/// device: every byte it writes is the model's or the profile's.
///
/// <para>The slots: the device descriptor's idVendor, idProduct and
/// bcdDevice; <c>@helloIdentity</c>, the Hello's VID, PID, firmware version
/// and the identity's hardware and protocol version bytes;
/// <c>@metadataFirst</c>, <c>@metadataRest</c> and
/// <c>@metadataComplete</c>, the identity's metadata in fragments with
/// <c>@metadataFirstEnd</c> and <c>@metadataEnd</c>, the byte counts the
/// host's acknowledgements carry; <c>@inputSize</c> and
/// <c>@inputLength</c>, the input report's size and payload length, and the
/// identity's program ops after the 14-byte gamepad report; and
/// <c>@guideBits</c>, where the Guide message carries the button.</para></summary>
internal static class GipFunction
{
    internal const string ModelResourceName = "HIDMaestro.Models.gip-gamepad.json";

    /// <summary>The interface a device lists in its metadata to start
    /// without the security exchange over USB ([MS-GIPUSB] 5196-5200).
    /// Windows' GIP driver challenges a device that lists none and starts
    /// no XInput pad until it answers.</summary>
    internal static readonly Guid SecurityOptOut = new("7a34ce77-7de2-45c6-8ca4-0042c08bd94a");

    /// <summary>Payload bytes per metadata fragment, as GP2040-CE's
    /// generatePacket cuts them (xgip_protocol.cpp, GIP_MAX_CHUNK_SIZE).</summary>
    private const int ChunkSize = 0x3A;

    // The model as its text and as an immutable document, read once. A
    // JsonObject fills its property table on first access, so one shared
    // between threads is written by its readers. The lookups read the
    // JsonDocument, whose elements are safe to read from any thread, and
    // each build parses a JsonObject of its own from the text.
    static readonly Lazy<(string Json, JsonDocument Doc, string Source)> s_model = new(() => LoadModel(ModelResourceName),
        System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The catalog model's <c>gipFunction</c>, a tree the caller
    /// owns: each call parses a new one.</summary>
    internal static JsonObject Model => JsonNode.Parse(s_model.Value.Json)!.AsObject();

    static JsonElement Root => s_model.Value.Doc.RootElement;

    static (string, JsonDocument, string) LoadModel(string resource)
    {
        using Stream? s = typeof(GipFunction).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The SDK carries no {resource}.");
        using var reader = new StreamReader(s);
        string json = FunctionOf(reader.ReadToEnd(), resource).ToJsonString();
        return (json, JsonDocument.Parse(json), resource);
    }

    /// <summary>The model's identity of that name, or false.</summary>
    static bool HasIdentity(string name) =>
        Root.TryGetProperty("identities", out var ids) && ids.ValueKind == JsonValueKind.Object && ids.TryGetProperty(name, out _);

    /// <summary>The VID, PID and version of the HID child Windows' GIP
    /// driver makes for a GIP device, from the model's <c>hidChild</c>: the
    /// device under whose XInput child xinputhid sits.</summary>
    internal static (ushort Vid, ushort Pid, ushort Version) HidChild
    {
        get
        {
            var c = Root.GetProperty("hidChild");
            return (ModelRegistry.ParseId(c.GetProperty("vid").GetString()!, "$.gipFunction.hidChild.vid"),
                    ModelRegistry.ParseId(c.GetProperty("pid").GetString()!, "$.gipFunction.hidChild.pid"),
                    ModelRegistry.ParseId(c.GetProperty("version").GetString()!, "$.gipFunction.hidChild.version"));
        }
    }

    /// <summary>The product string Windows' GIP driver gives the HID child
    /// it makes, from the model's <c>hidChild</c>.</summary>
    internal static string HidChildProduct => Root.GetProperty("hidChild").GetProperty("product").GetString()!;

    /// <summary>A model file's <c>gipFunction</c>, read through the
    /// catalog's strict model format first, so a key the format lacks is
    /// refused as in any model.</summary>
    internal static JsonObject FunctionOf(string json, string source)
    {
        DeviceModelFile? file;
        try { file = JsonSerializer.Deserialize(json, HMJsonContext.Default.DeviceModelFile); }
        catch (JsonException e)
        {
            var (path, reason) = ProfileDefects.Locate(e);
            throw new InvalidOperationException($"{source}: {path}: {reason}", e);
        }
        if (file?.GipFunction is not { ValueKind: JsonValueKind.Object } element)
            throw new InvalidOperationException($"{source}: $.gipFunction is missing.");
        return JsonNode.Parse(element.GetRawText())!.AsObject();
    }

    /// <summary>The identities the catalog's model holds.</summary>
    internal static IReadOnlyList<string> Identities =>
        Root.TryGetProperty("identities", out var ids) && ids.ValueKind == JsonValueKind.Object
            ? ids.EnumerateObject().Select(p => p.Name).ToList() : Array.Empty<string>();

    /// <summary>The Microsoft OS compatible ID a GIP device serves, from its
    /// extended compatible ID descriptor (8 bytes at offset 18), or null.
    /// Windows matches it as <c>USB\MS_COMP_</c> and the ID.</summary>
    internal static string? CompatibleIdOf(ControllerProfile form)
    {
        string? hex = form.UsbConfiguration?.MicrosoftOs?.ExtendedCompatIdHex;
        if (string.IsNullOrEmpty(hex) || hex.Length < 52) return null;
        try { return Encoding.ASCII.GetString(Convert.FromHexString(hex.Substring(36, 16))).TrimEnd('\0'); }
        catch (FormatException) { return null; }
    }

    /// <summary>True when <paramref name="profile"/> presents the GIP
    /// function on the USB/IP backend: it reaches XInput through xinputhid
    /// and authors no USB device of its own.</summary>
    internal static bool Applies(ControllerProfile profile) =>
        profile.UsbConfiguration == null && XInputPresentation.Resolve(profile).Xinputhid;

    /// <summary>What is wrong with a <c>gip</c> block's identity, or null
    /// when the model holds it.</summary>
    internal static string? IdentityDefect(GipSpec gip) =>
        string.IsNullOrEmpty(gip.Identity) ? "is required: the identity of the GIP model the profile presents."
        : !HasIdentity(gip.Identity) ? $"\"{gip.Identity}\" is not one of the GIP model's identities ({string.Join(", ", Identities)})."
        : null;

    /// <summary>Why the GIP function cannot present
    /// <paramref name="profile"/>, or null when it can.</summary>
    internal static string? RefusedReason(ControllerProfile profile)
    {
        string? id = profile.Gip?.Identity;
        if (string.IsNullOrEmpty(id))
            return "it reaches XInput through xinputhid, and it names no GIP identity (gip.identity) for the USB/IP backend's GIP function to present.";
        if (!HasIdentity(id))
            return $"its gip.identity \"{id}\" is not one of the GIP model's identities ({string.Join(", ", Identities)}).";
        return null;
    }

    /// <summary>The USB/IP form of <paramref name="profile"/>, built once
    /// per profile instance and checked as a profile load checks one.
    /// Throws <see cref="HMBackendNotSupportedException"/> when the profile
    /// names no identity of the model, and
    /// <see cref="ProfileDefectException"/> for a model value that does not
    /// fit.</summary>
    internal static ControllerProfile FormOf(ControllerProfile profile)
    {
        if (profile.GipFormCache is { } cached) return cached;
        if (RefusedReason(profile) is { } reason)
            throw new HMBackendNotSupportedException(profile.Id, HMBackend.Usbip, reason);
        var form = Build(profile, Model, s_model.Value.Source);
        profile.GipFormCache = form;
        return form;
    }

    /// <summary>The form of <paramref name="profile"/> under the model
    /// <paramref name="function"/>, for <see cref="FormOf"/> and for a check
    /// that builds a model of its own.</summary>
    internal static ControllerProfile Build(ControllerProfile profile, JsonObject function, string source)
    {
        var form = BuildJson(profile, function, source);
        var parsed = ControllerProfile.Parse(Encoding.UTF8.GetBytes(form.ToJsonString()))
            ?? throw new ProfileDefectException("$", "the GIP form is empty.");
        parsed.SourceFile = profile.SourceFile;
        parsed.Registry = profile.Registry;
        parsed.OutsideCatalog = profile.OutsideCatalog;
        parsed.MarkBuiltInCode(profile.BuiltInCode);
        parsed.ValidateAtLoad();
        return parsed;
    }

    /// <summary>The form's JSON, the profile file it would be.</summary>
    internal static JsonObject BuildJson(ControllerProfile profile, JsonObject function, string source)
    {
        string identityName = profile.Gip?.Identity
            ?? throw new ProfileDefectException("$.gip.identity", "is required for the GIP function.");
        var identity = function["identities"]?[identityName] as JsonObject
            ?? throw new ProfileDefectException("$.gip.identity", $"\"{identityName}\" is not an identity of {source}.");
        string at = $"{source}: $.gipFunction.identities.{identityName}";

        ushort vid = profile.VendorId, pid = profile.ProductId;
        ushort major = Word(function["firmware"]?["major"], $"{source}: $.gipFunction.firmware.major");
        ushort minor = Word(function["firmware"]?["minor"], $"{source}: $.gipFunction.firmware.minor");
        ushort build = Word(identity["firmware"]?["build"], $"{at}.firmware.build");
        ushort revision = Word(identity["firmware"]?["revision"], $"{at}.firmware.revision");
        ushort bcdDevice = Word(identity["bcdDevice"], $"{at}.bcdDevice");
        byte[] hardware = HexBytes(identity["hardware"], $"{at}.hardware");
        if (hardware.Length != 8)
            throw new ProfileDefectException($"{at}.hardware", "is the Hello's last 8 bytes: the hardware version and the three protocol versions.");
        int inputSize = (int)Word(identity["inputSize"], $"{at}.inputSize");
        if (inputSize < 18 || inputSize > 64)
            throw new ProfileDefectException($"{at}.inputSize", $"{inputSize} is outside 18 to 64: a 4-byte header and at least the 14-byte gamepad report, in one packet.");

        // The device descriptor: the model's, with this profile's identity.
        var usb = Clone(function["usbConfiguration"], $"{source}: $.gipFunction.usbConfiguration").AsObject();
        byte[] device = HexBytes(usb["deviceDescriptor"], $"{source}: $.gipFunction.usbConfiguration.deviceDescriptor");
        if (device.Length != 18)
            throw new ProfileDefectException($"{source}: $.gipFunction.usbConfiguration.deviceDescriptor", "is not 18 bytes.");
        Le(device, 8, vid); Le(device, 10, pid); Le(device, 12, bcdDevice);
        usb["deviceDescriptor"] = Convert.ToHexString(device).ToLowerInvariant();

        // The Hello's identity bytes.
        var hello = new byte[20];
        Le(hello, 0, vid); Le(hello, 2, pid); Le(hello, 4, major); Le(hello, 6, minor); Le(hello, 8, build); Le(hello, 10, revision);
        hardware.CopyTo(hello, 12);

        // The metadata and its fragments.
        byte[] metadata = Metadata(function, identity, major, minor, inputSize - 4, source, at);
        var packets = Fragments(metadata);
        // The first fragment answers the request, the rest answer the host's
        // acknowledgement of it, and the completion answers the host's
        // acknowledgement of the last: one ACME packet each, which a
        // metadata of five fragments or fewer keeps (generatePacket sets
        // ACME on every fifth).
        int acme = packets.Count(p => p.Length > 1 && (p[1] & 0x10) != 0);
        if (packets.Count < 4 || acme != 2)
            throw new ProfileDefectException(at,
                $"gives {metadata.Length} bytes of metadata, {packets.Count - 1} fragments with {acme} acknowledged: the model's rules answer three to five fragments, the first and the last acknowledged.");
        var rest = packets.Skip(1).Take(packets.Count - 2).ToList();

        var protocol = Clone(function["protocol"], $"{source}: $.gipFunction.protocol").AsObject();
        int filled = 0;
        foreach (var t in protocol["timers"]?.AsArray() ?? new JsonArray())
            foreach (var part in t?["reply"]?.AsArray() ?? new JsonArray())
                if (part?["hex"]?.GetValue<string>() == "@helloIdentity") { part["hex"] = Convert.ToHexString(hello); filled++; }
        foreach (var rule in protocol["commands"]?["rules"]?.AsArray() ?? new JsonArray())
        {
            if (rule is not JsonObject r) continue;
            if (r["match"]?.GetValue<string>() is { } m && m.Contains('@'))
                r["match"] = m.Replace("@metadataFirstEnd", HexSpaced(Le32(ChunkSize)), StringComparison.Ordinal)
                              .Replace("@metadataEnd", HexSpaced(Le32(metadata.Length)), StringComparison.Ordinal);
            switch (r["reply"]?.GetValueKind() == JsonValueKind.String ? r["reply"]!.GetValue<string>() : null)
            {
                case "@metadataFirst": r["reply"] = Packet(packets[0]); filled++; break;
                case "@metadataComplete": r["reply"] = Packet(packets[^1]); filled++; break;
            }
            if (r["replies"]?.GetValueKind() == JsonValueKind.String && r["replies"]!.GetValue<string>() == "@metadataRest")
            {
                r["replies"] = new JsonArray(rest.Select(p => (JsonNode)Packet(p)).ToArray());
                filled++;
            }
        }
        if (filled != 4)
            throw new ProfileDefectException($"{source}: $.gipFunction.protocol",
                "holds the slots @helloIdentity, @metadataFirst, @metadataRest and @metadataComplete once each.");

        // The input report and the Guide message.
        var reports = Clone(function["inputReports"], $"{source}: $.gipFunction.inputReports").AsArray();
        int reportsFilled = 0;
        foreach (var node in reports)
        {
            if (node is not JsonObject report) continue;
            if (report["size"]?.GetValueKind() == JsonValueKind.String && report["size"]!.GetValue<string>() == "@inputSize")
            {
                report["size"] = inputSize;
                var program = report["program"]!.AsArray();
                foreach (var op in program)
                    if (op?["const"] is JsonObject c)
                        foreach (var key in c.Select(kv => kv.Key).ToList())
                            if (c[key]?.GetValue<string>() == "@inputLength") c[key] = (inputSize - 4).ToString("X2", CultureInfo.InvariantCulture);
                foreach (var op in identity["inputProgram"]?.AsArray() ?? new JsonArray())
                    program.Add(op?.DeepClone());
                reportsFilled++;
            }
            if (report["program"] is JsonArray guide)
                for (int i = 0; i < guide.Count; i++)
                    if (guide[i]?.GetValueKind() == JsonValueKind.String && guide[i]!.GetValue<string>() == "@guideBits")
                    {
                        var bits = identity["guideBits"] ?? function["guideBits"]
                            ?? throw new ProfileDefectException($"{source}: $.gipFunction.guideBits", "is required: the Guide message's bit.");
                        guide[i] = new JsonObject { ["bits"] = bits.DeepClone() };
                        reportsFilled++;
                    }
        }
        if (reportsFilled != 2)
            throw new ProfileDefectException($"{source}: $.gipFunction.inputReports", "holds the slots @inputSize and @guideBits once each.");

        // The form: the profile's identity, the model's device.
        var form = new JsonObject
        {
            ["id"] = profile.Id,
            ["name"] = profile.Name,
            ["vendor"] = profile.Vendor,
            ["vid"] = profile.Vid,
            ["pid"] = profile.Pid,
            ["productString"] = function["strings"]?["product"]?.DeepClone() ?? profile.ProductString,
            ["manufacturerString"] = function["strings"]?["manufacturer"]?.DeepClone() ?? profile.ManufacturerString,
            ["type"] = profile.Type,
            ["connection"] = profile.Connection,
            ["backend"] = "usbip",
            ["deviceModel"] = "none",
            ["gip"] = new JsonObject { ["identity"] = identityName },
            ["layout"] = Clone(function["layout"], $"{source}: $.gipFunction.layout"),
            ["usbConfiguration"] = usb,
            ["stateBlock"] = Clone(function["stateBlock"], $"{source}: $.gipFunction.stateBlock"),
            ["inputReports"] = reports,
            ["protocol"] = protocol,
        };
        if (profile.SerialString != null) form["serialString"] = profile.SerialString;
        if (profile.Notes != null) form["notes"] = profile.Notes;
        return form;
    }

    /// <summary>The metadata in the layout Microsoft's GIP Metadata Compiler
    /// writes: a 16-byte header (MetadataHeader), the device block with its
    /// offsets (DeviceMetadata: firmware versions, audio formats, the in and
    /// out system message lists, preferred types, interfaces, no HID
    /// descriptor), then the message count and 23-byte messages
    /// (MessageMetadata).</summary>
    internal static byte[] Metadata(JsonObject function, JsonObject identity, ushort major, ushort minor, int inputLength, string source, string at)
    {
        var interfaces = new List<Guid>();
        var list = identity["interfaces"]?.AsArray()
            ?? throw new ProfileDefectException($"{at}.interfaces", "is required: the metadata's SupportedInterfaces.");
        for (int i = 0; i < list.Count; i++)
        {
            if (!Guid.TryParse(list[i]?.GetValue<string>(), out var g))
                throw new ProfileDefectException($"{at}.interfaces[{i}]", "is not a GUID.");
            interfaces.Add(g);
        }
        if (!interfaces.Contains(SecurityOptOut))
            throw new ProfileDefectException($"{at}.interfaces",
                $"lists no {SecurityOptOut}: without the security opt-out Windows' GIP driver challenges the device and starts no XInput pad.");
        var types = (identity["preferredTypes"] ?? function["preferredTypes"])?.AsArray().Select(n => n!.GetValue<string>()).ToList()
            ?? throw new ProfileDefectException($"{source}: $.gipFunction.preferredTypes", "is required: the metadata's PreferredTypes.");
        byte[] inSystem = HexBytes(function["systemMessages"]?["in"], $"{source}: $.gipFunction.systemMessages.in");
        byte[] outSystem = HexBytes(function["systemMessages"]?["out"], $"{source}: $.gipFunction.systemMessages.out");

        var messages = new List<byte[]>();
        var msgs = (identity["messages"] ?? function["messages"])?.AsArray()
            ?? throw new ProfileDefectException($"{source}: $.gipFunction.messages", "is required: the metadata's messages.");
        for (int i = 0; i < msgs.Count; i++)
        {
            string mat = $"{source}: $.gipFunction.messages[{i}]";
            var m = msgs[i]!.AsObject();
            byte type = (byte)Word(m["type"], mat + ".type");
            int length = m["length"]?.GetValueKind() == JsonValueKind.String && m["length"]!.GetValue<string>() == "input"
                ? inputLength : Word(m["length"], mat + ".length");
            ushort dataType = Word(m["dataType"], mat + ".dataType");
            uint flags = Dword(m["flags"], mat + ".flags");
            ushort period = m["period"] != null ? Word(m["period"], mat + ".period") : (ushort)0;
            ushort persistence = m["persistence"] != null ? Word(m["persistence"], mat + ".persistence") : (ushort)0;
            var e = new byte[23];
            Le(e, 0, 23); e[2] = type; Le(e, 3, (ushort)length); Le(e, 5, dataType);
            e[7] = (byte)flags; e[8] = (byte)(flags >> 8); e[9] = (byte)(flags >> 16); e[10] = (byte)(flags >> 24);
            Le(e, 11, period); Le(e, 13, persistence);
            messages.Add(e);
        }
        return Compile(new[] { (major, minor) }, inSystem, outSystem, types, interfaces, messages);
    }

    /// <summary>The compiler's byte layout for the given fields.</summary>
    internal static byte[] Compile(IReadOnlyList<(ushort Major, ushort Minor)> firmware, byte[] inSystem, byte[] outSystem,
                                   IReadOnlyList<string> preferredTypes, IReadOnlyList<Guid> interfaces, IReadOnlyList<byte[]> messages)
    {
        const int offsetsSize = 22;
        var sec = new List<byte>();
        int oFirmware = offsetsSize;
        sec.Add((byte)firmware.Count);
        foreach (var (maj, mnr) in firmware) { sec.Add((byte)maj); sec.Add((byte)(maj >> 8)); sec.Add((byte)mnr); sec.Add((byte)(mnr >> 8)); }
        int oAudio = offsetsSize + sec.Count;
        sec.Add(0);
        int oIn = offsetsSize + sec.Count;
        sec.Add((byte)inSystem.Length); sec.AddRange(inSystem);
        int oOut = offsetsSize + sec.Count;
        sec.Add((byte)outSystem.Length); sec.AddRange(outSystem);
        int oTypes = offsetsSize + sec.Count;
        sec.Add((byte)preferredTypes.Count);
        foreach (var t in preferredTypes)
        {
            byte[] s = Encoding.ASCII.GetBytes(t);
            sec.Add((byte)s.Length); sec.Add((byte)(s.Length >> 8)); sec.AddRange(s);
        }
        int oInterfaces = offsetsSize + sec.Count;
        sec.Add((byte)interfaces.Count);
        foreach (var g in interfaces) sec.AddRange(g.ToByteArray());
        int deviceLength = offsetsSize + sec.Count;

        var blob = new List<byte>();
        void Word16(int v) { blob.Add((byte)v); blob.Add((byte)(v >> 8)); }
        int total = 16 + deviceLength + 1 + messages.Sum(m => m.Length);
        Word16(16); Word16(1); Word16(0); Word16(0); Word16(0); Word16(0); Word16(0); Word16(total);
        foreach (int v in new[] { deviceLength, oFirmware, oAudio, oIn, oOut, oTypes, oInterfaces, 0, 0, 0, 0 }) Word16(v);
        blob.AddRange(sec);
        blob.Add((byte)messages.Count);
        foreach (var m in messages) blob.AddRange(m);
        return blob.ToArray();
    }

    /// <summary>A message cut into fragments as GP2040-CE's generatePacket
    /// (src/drivers/shared/xgip_protocol.cpp, 8013b41) cuts it, ported line
    /// for line: 58 bytes a packet, each with its own header, sequence 0
    /// where the request's sequence goes, then the zero-length completion.</summary>
    internal static List<byte[]> Fragments(byte[] data)
    {
        int dataLength = data.Length;
        int totalDataSent = 0, numberOfChunksSent = 0, totalChunkSent = 0;
        // The total the first packet and the completion carry, in the
        // generatePacket encoding.
        int i = dataLength, j = 0;
        while (true)
        {
            if (i < ChunkSize)
            {
                j = j / 0x100 != (j + i) / 0x100 ? j + (i | 0x80) : j + i;
                i = 0;
            }
            else
            {
                if (j + ChunkSize > 0x80 && j + ChunkSize < 0x100) j = j + ChunkSize + 0x100;
                else j = j / 0x100 != (j + ChunkSize) / 0x100 ? j + (ChunkSize | 0x80) : j + ChunkSize;
                i -= ChunkSize;
            }
            if (i == 0) break;
        }
        int totalChunkLength = j;
        var packets = new List<byte[]>();
        while (true)
        {
            if (numberOfChunksSent > 0 && totalDataSent == dataLength)
            {
                packets.Add(new byte[] { 0x04, 0xA0, 0x00, 0x00, (byte)(totalChunkLength & 0xFF), (byte)(totalChunkLength >> 8) });
                return packets;
            }
            bool chunkStart = numberOfChunksSent == 0;
            bool needsAck = numberOfChunksSent == 0 || (numberOfChunksSent + 1) % 5 == 0;
            int dataToSend = ChunkSize;
            if (dataLength - totalDataSent < dataToSend) { dataToSend = dataLength - totalDataSent; needsAck = true; }
            int length = (numberOfChunksSent > 0 && totalChunkSent < 0x100) || (numberOfChunksSent == 0 && ChunkSize < dataLength && dataLength < 0x80)
                ? dataToSend | 0x80 : dataToSend;
            byte flags = (byte)(0x80 | (chunkStart ? 0x40 : 0) | 0x20 | (needsAck ? 0x10 : 0));
            int value = numberOfChunksSent == 0 ? totalChunkLength : totalChunkSent;
            byte b4 = value < 0x100 ? (byte)0x00 : (byte)(value & 0xFF);
            byte b5 = value < 0x100 ? (byte)value : (byte)(value >> 8);
            var p = new byte[6 + dataToSend];
            p[0] = 0x04; p[1] = flags; p[2] = 0x00; p[3] = (byte)length; p[4] = b4; p[5] = b5;
            Array.Copy(data, totalDataSent, p, 6, dataToSend);
            packets.Add(p);
            if (totalChunkSent < 0x100 && totalChunkSent + dataToSend > 0x80) totalChunkSent = totalChunkSent + dataToSend + 0x100;
            else if ((totalChunkSent + dataToSend) / 0x100 > totalChunkSent / 0x100) totalChunkSent += dataToSend | 0x80;
            else totalChunkSent += dataToSend;
            totalDataSent += dataToSend;
            numberOfChunksSent++;
        }
    }

    /// <summary>A packet as reply parts: its first two bytes, the request's
    /// sequence echoed, then the rest.</summary>
    private static JsonArray Packet(byte[] p) => new(new JsonNode[]
    {
        new JsonObject { ["hex"] = Convert.ToHexString(p, 0, 2) },
        new JsonObject { ["echo"] = "2" },
        new JsonObject { ["hex"] = Convert.ToHexString(p, 3, p.Length - 3) },
    });

    private static JsonNode Clone(JsonNode? node, string path) =>
        node?.DeepClone() ?? throw new ProfileDefectException(path, "is required.");

    private static void Le(byte[] b, int at, ushort v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }

    private static byte[] Le32(int v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };

    private static string HexSpaced(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>A 16-bit number: a JSON number, or a hex string with or
    /// without 0x.</summary>
    private static ushort Word(JsonNode? node, string path)
    {
        long v = Number(node, path);
        if (v is < 0 or > 0xFFFF) throw new ProfileDefectException(path, $"{v} is not a 16-bit value.");
        return (ushort)v;
    }

    private static uint Dword(JsonNode? node, string path)
    {
        long v = Number(node, path);
        if (v is < 0 or > 0xFFFFFFFF) throw new ProfileDefectException(path, $"{v} is not a 32-bit value.");
        return (uint)v;
    }

    private static long Number(JsonNode? node, string path)
    {
        if (node == null) throw new ProfileDefectException(path, "is required.");
        if (node.GetValueKind() == JsonValueKind.Number && node is JsonValue n)
        {
            if (n.TryGetValue(out long l)) return l;
            if (n.TryGetValue(out int i)) return i;
        }
        if (node.GetValueKind() == JsonValueKind.String)
        {
            string s = node.GetValue<string>();
            string digits = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
            if (long.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long v)) return v;
        }
        throw new ProfileDefectException(path, "is not a number or a hex string such as \"0x0203\".");
    }

    private static byte[] HexBytes(JsonNode? node, string path)
    {
        if (node?.GetValueKind() != JsonValueKind.String) throw new ProfileDefectException(path, "is required, as hex.");
        try { return Convert.FromHexString(node.GetValue<string>().Replace(" ", "", StringComparison.Ordinal)); }
        catch (FormatException) { throw new ProfileDefectException(path, "is not whole hex bytes."); }
    }
}
