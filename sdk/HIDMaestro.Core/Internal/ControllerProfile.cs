using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HIDMaestro.Internal;

/// <summary>
/// A controller profile loaded from the profiles database.
/// Contains everything needed to masquerade as a specific real controller.
/// </summary>
public sealed class ControllerProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("vendor")]
    public string Vendor { get; set; } = "";

    [JsonPropertyName("vid")]
    public string Vid { get; set; } = "";

    [JsonPropertyName("pid")]
    public string Pid { get; set; } = "";

    [JsonPropertyName("productString")]
    public string ProductString { get; set; } = "";

    [JsonPropertyName("manufacturerString")]
    public string? ManufacturerString { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("connection")]
    public string Connection { get; set; } = "";

    [JsonPropertyName("descriptor")]
    public string? Descriptor { get; set; }

    [JsonPropertyName("inputReportSize")]
    public int? InputReportSize { get; set; }

    [JsonPropertyName("deviceDescription")]
    public string? DeviceDescription { get; set; }

    [JsonPropertyName("triggerMode")]
    public string? TriggerMode { get; set; }

    [JsonPropertyName("driverMode")]
    public string? DriverMode { get; set; }

    /// <summary>PID override for hardware ID (driver matching only). Apps still see real PID.</summary>
    [JsonPropertyName("driverPid")]
    public string? DriverPid { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    /// <summary>Optional button remapping table. Maps HMButton bit positions
    /// (index) to descriptor button indices (value). When present, BuildReport
    /// uses this to place semantic buttons (A, B, X, Y, LB, RB, etc.) at the
    /// correct descriptor positions for the profile's controller family.
    /// When null, identity mapping is assumed (bit N → descriptor button N).
    /// Example: Sony DS4 maps HMButton.A (bit 0) to descriptor button 2 (Cross).
    /// </summary>
    [JsonPropertyName("buttonMap")]
    public int[]? ButtonMap { get; set; }

    /// <summary>Optional trigger-to-button derivation. When a DS4 or DualSense
    /// trigger is nonzero, the corresponding digital button should also engage
    /// (real hardware reports both the analog axis and a digital button for L2/R2).
    /// Array of two descriptor button indices: [LT_button, RT_button].
    /// When present, BuildReport sets these buttons whenever the corresponding
    /// trigger axis is nonzero. When null, no derivation occurs.</summary>
    [JsonPropertyName("triggerButtons")]
    public int[]? TriggerButtons { get; set; }

    /// <summary>Optional axis semantic override. Maps HID usage codes to
    /// semantic roles when the default heuristic gets it wrong. Keys are
    /// HID usage codes (e.g. "0x32" for Z), values are semantic names:
    /// "leftStickX", "leftStickY", "rightStickX", "rightStickY",
    /// "leftTrigger", "rightTrigger". When present, overrides
    /// ResolveSemantics for the specified usages. When null, the default
    /// heuristic applies (which assumes Z=trigger, Rz=trigger).
    /// Sony profiles need this because Z/Rz = right stick, Rx/Ry = triggers.
    /// </summary>
    [JsonPropertyName("axisMap")]
    public Dictionary<string, string>? AxisMap { get; set; }

    /// <summary>If true, skip main HID device — use XUSB companion only.
    /// DI reads from XInput (5 axes), browser reads from XInput (separate triggers).
    /// Used for Xbox 360 where real hardware uses xusb22.sys (no HID).</summary>
    [JsonPropertyName("companionOnly")]
    public bool CompanionOnly { get; set; }

    /// <summary>v1.3.5 — optional vendor-blob input report layout. When present,
    /// the SDK emits this report ID via VendorBlobCodec instead of the
    /// descriptor's first declared input. Used for protocols where the
    /// descriptor declares an opaque vendor blob (Sony BT 0x31 / 0x11, etc.).</summary>
    [JsonPropertyName("extendedReport")]
    public ExtendedReportSpec? ExtendedReport { get; set; }

    /// <summary>v1.3.5 — optional vendor-blob output report layout. When
    /// present, the SDK decodes incoming output reports of the declared
    /// report ID and surfaces parsed-field events via HMController.OutputDecoded.</summary>
    [JsonPropertyName("extendedOutputReport")]
    public ExtendedReportSpec? ExtendedOutputReport { get; set; }

    /// <summary>
    /// Whether triggers are combined into a single Z axis (true for Xbox on Windows).
    /// Combined: Z centers at 50%, LT pulls toward 0%, RT pulls toward 100%.
    /// Separate: Z and Rz each go 0-100% independently.
    /// </summary>
    [JsonIgnore]
    public bool HasCombinedTriggers => TriggerMode?.Equals("combined", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Whether this controller uses an upper filter driver for XInput.
    /// xinputhid: Xbox One+ controllers (GIP descriptor, xinputhid filter)
    /// xusb22: Xbox 360 controllers (xusb22 filter)
    /// hid: no filter, direct HID access only
    /// </summary>
    [JsonIgnore]
    public bool UsesXinputhid => DriverMode?.Equals("xinputhid", StringComparison.OrdinalIgnoreCase) == true;

    [JsonIgnore]
    public bool UsesXusb22 => DriverMode?.Equals("xusb22", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Whether this profile uses any upper filter (xinputhid or xusb22).</summary>
    [JsonIgnore]
    public bool UsesUpperFilter => UsesXinputhid || UsesXusb22;

    /// <summary>The upper filter service name, or null.</summary>
    [JsonIgnore]
    public string? UpperFilterName => UsesXinputhid ? "xinputhid" : UsesXusb22 ? "xusb22" : null;

    /// <summary>Parsed VID as ushort.</summary>
    [JsonIgnore]
    public ushort VendorId => Convert.ToUInt16(Vid, 16);

    /// <summary>Parsed PID as ushort.</summary>
    [JsonIgnore]
    public ushort ProductId => Convert.ToUInt16(Pid, 16);

    /// <summary>Device Manager display name. Uses deviceDescription if set, otherwise productString.</summary>
    [JsonIgnore]
    public string DisplayName => DeviceDescription ?? ProductString;

    /// <summary>True if this profile has a HID descriptor ready to use.</summary>
    [JsonIgnore]
    public bool HasDescriptor => !string.IsNullOrEmpty(Descriptor);

    // Lazy-cached parsed descriptor. v1.3.0 — GetDescriptorBytes is called
    // multiple times per CreateController (HMController ctor +
    // WriteInstanceConfig + DriverBuilder validation), each time re-running
    // the Replace + Substring + Convert.ToByte parse loop over the hex
    // string. Caching the result on the instance saves N–1 parses where
    // N is the number of times the bytes are needed (typically 2–3).
    [JsonIgnore]
    private byte[]? _cachedDescriptor;
    [JsonIgnore]
    private bool _descriptorCached;

    /// <summary>Parses the hex descriptor string into raw bytes. Result is
    /// cached on the instance after the first call; safe to call repeatedly.</summary>
    public byte[]? GetDescriptorBytes()
    {
        if (_descriptorCached) return _cachedDescriptor;
        if (string.IsNullOrEmpty(Descriptor))
        {
            _cachedDescriptor = null;
            _descriptorCached = true;
            return null;
        }
        var hex = Descriptor.Replace(" ", "").Replace("-", "");
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        _cachedDescriptor = bytes;
        _descriptorCached = true;
        return bytes;
    }

    // v1.3.0 T10 — lazy-cached parsed HidReportBuilder. HMController.ctor
    // calls HidReportBuilder.Parse(descriptor, axisMap) on every
    // CreateController, which walks the descriptor byte-by-byte building
    // InputFields + ResolveSemantics + ApplyAxisMap. Same descriptor +
    // same axisMap = identical output, so a per-profile cache eliminates
    // the per-controller parse cost. The builder is configured once
    // (ButtonMap + TriggerButtons set immediately after Parse) and then
    // only read by SubmitState — safe to share across HMController
    // instances using the same profile.
    [JsonIgnore]
    private HidReportBuilder? _cachedReportBuilder;

    /// <summary>Returns a parsed + configured HidReportBuilder for this
    /// profile. Cached on the instance after the first call. Subsequent
    /// CreateController calls for the same profile reuse the cached
    /// instance (read-only after configuration).</summary>
    internal HidReportBuilder GetOrBuildReportBuilder()
    {
        if (_cachedReportBuilder != null) return _cachedReportBuilder;
        var b = HidReportBuilder.Parse(GetDescriptorBytes()!, AxisMap);
        b.ButtonMap = ButtonMap;
        b.TriggerButtons = TriggerButtons;
        _cachedReportBuilder = b;
        return b;
    }
}

/// <summary>v1.3.5 — vendor-blob report layout (input or output direction).
/// Profile JSON describes the byte layout of a vendor blob; the codec walks
/// the field list to encode/decode reports in either direction.</summary>
public sealed class ExtendedReportSpec
{
    /// <summary>Hex string for the report ID, e.g. "0x31".</summary>
    [JsonPropertyName("reportId")]
    public string ReportId { get; set; } = "";

    /// <summary>Total bytes including report ID byte at offset 0.</summary>
    [JsonPropertyName("size")]
    public int Size { get; set; }

    /// <summary>Host-side write triggers that switch this controller into
    /// emitting the extended report. Until any trigger fires, the descriptor's
    /// first declared input report ID is emitted instead. Output direction
    /// ignores this field. Empty/missing means "never armed" (input direction
    /// stays on the legacy report).</summary>
    [JsonPropertyName("armOn")]
    public List<ArmTrigger>? ArmOn { get; set; }

    /// <summary>Ordered field descriptors. See VendorBlobCodec for the type
    /// vocabulary.</summary>
    [JsonPropertyName("fields")]
    public List<FieldSpec> Fields { get; set; } = new();

    [JsonIgnore]
    public byte ReportIdByte => string.IsNullOrEmpty(ReportId) ? (byte)0
        : Convert.ToByte(ReportId.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ReportId.Substring(2) : ReportId, 16);
}

/// <summary>v1.3.5 — host-side write trigger that arms extended-report emission.
/// Type "featureWrite" matches an outgoing HID feature SetFeature; "outputWrite"
/// matches an outgoing HID output report. ReportId is hex.</summary>
public sealed class ArmTrigger
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("reportId")]
    public string ReportId { get; set; } = "";

    [JsonIgnore]
    public byte ReportIdByte => string.IsNullOrEmpty(ReportId) ? (byte)0
        : Convert.ToByte(ReportId.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ReportId.Substring(2) : ReportId, 16);
}

/// <summary>v1.3.5 — single-field descriptor inside an ExtendedReportSpec.
/// Either <see cref="Byte"/> (single byte position) or <see cref="Bytes"/>
/// (range like "15-22") locates the field; <see cref="Bits"/> further narrows
/// to a sub-byte bit range. <see cref="Type"/> selects the codec from the
/// VendorBlobCodec vocabulary.</summary>
public sealed class FieldSpec
{
    [JsonPropertyName("byte")]
    public int? Byte { get; set; }

    [JsonPropertyName("bytes")]
    public string? Bytes { get; set; }

    [JsonPropertyName("bits")]
    public string? Bits { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("semantic")]
    public string? Semantic { get; set; }

    [JsonPropertyName("buttons")]
    public List<string>? Buttons { get; set; }

    [JsonPropertyName("center")]
    public int? Center { get; set; }

    [JsonPropertyName("neutralValue")]
    public int? NeutralValue { get; set; }

    [JsonPropertyName("scope")]
    public CrcScope? Scope { get; set; }

    [JsonPropertyName("initial")]
    public int? Initial { get; set; }
}

/// <summary>v1.3.5 — CRC32 scope spec for a crc32-le field. The CRC is
/// computed over <see cref="Prefix"/> bytes followed by the report bytes
/// from offset <see cref="From"/> through <see cref="To"/> inclusive.</summary>
public sealed class CrcScope
{
    [JsonPropertyName("prefix")]
    public List<byte> Prefix { get; set; } = new();

    [JsonPropertyName("from")]
    public int From { get; set; }

    [JsonPropertyName("to")]
    public int To { get; set; }
}

/// <summary>
/// Loads and queries controller profiles from the profiles/ directory.
/// </summary>
public sealed class ProfileDatabase
{
    private readonly List<ControllerProfile> _profiles = new();

    public IReadOnlyList<ControllerProfile> All => _profiles;

    /// <summary>
    /// Loads all .json profile files from the given directory (recursively).
    /// Skips schema.json and any files that fail to parse.
    /// </summary>
    public static ProfileDatabase Load(string profilesDir)
    {
        var db = new ProfileDatabase();

        if (!Directory.Exists(profilesDir))
            throw new DirectoryNotFoundException($"Profiles directory not found: {profilesDir}");

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // v1.3.0 — parallel parse mirroring LoadEmbedded. Disk reads are
        // serialized at the kernel level on most filesystems, but JSON
        // parse is CPU-bound and benefits from cores. For a directory
        // with hundreds of profiles, this matters more than the embedded
        // case because disk-load amortization dominates.
        var files = Directory.EnumerateFiles(profilesDir, "*.json", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("schema.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var parsed = new System.Collections.Concurrent.ConcurrentBag<ControllerProfile>();
        System.Threading.Tasks.Parallel.ForEach(files, file =>
        {
            try
            {
                var json = File.ReadAllText(file);
                var profile = JsonSerializer.Deserialize<ControllerProfile>(json, options);
                if (profile != null && !string.IsNullOrEmpty(profile.Id))
                    parsed.Add(profile);
            }
            catch
            {
                // A single malformed JSON shouldn't take down the whole load
                // pass. Caller's profile lookups will simply miss this entry.
            }
        });

        db._profiles.AddRange(parsed);
        db._profiles.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
        return db;
    }

    /// <summary>Loads every profile JSON embedded in the HIDMaestro.Core
    /// assembly under the logical-name prefix "HIDMaestro.Profiles.". This
    /// is the no-disk path used by HMContext.LoadDefaultProfiles() — the
    /// SDK ships with the entire profile catalog baked in so consumers
    /// don't need to ship a sibling profiles/ directory.</summary>
    // v1.3.0 — process-wide cache. Embedded JSONs are static for the
    // lifetime of the process; reparsing on every HMContext.LoadDefaultProfiles
    // is wasted work. First call populates the cache (parallel parse);
    // subsequent calls return the same instance. Multiple HMContexts share.
    private static ProfileDatabase? s_cachedEmbedded;
    private static readonly object s_cachedEmbeddedLock = new();

    public static ProfileDatabase LoadEmbedded()
    {
        if (s_cachedEmbedded != null) return s_cachedEmbedded;
        lock (s_cachedEmbeddedLock)
        {
            if (s_cachedEmbedded != null) return s_cachedEmbedded;
            s_cachedEmbedded = LoadEmbeddedFresh();
            return s_cachedEmbedded;
        }
    }

    private static ProfileDatabase LoadEmbeddedFresh()
    {
        var db = new ProfileDatabase();
        var asm = typeof(ProfileDatabase).Assembly;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        const string prefix = "HIDMaestro.Profiles.";

        // Collect resource names first so we can parse in parallel. The
        // serial JSON parse over 224 profiles is the dominant fresh-launch
        // cost in HMContext init — Parallel.ForEach across 4–16 cores
        // drops it from 200–500 ms cold to 50–150 ms.
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)
                     && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var parsed = new System.Collections.Concurrent.ConcurrentBag<ControllerProfile>();
        System.Threading.Tasks.Parallel.ForEach(names, name =>
        {
            try
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s == null) return;
                using var reader = new StreamReader(s);
                string json = reader.ReadToEnd();
                var profile = JsonSerializer.Deserialize<ControllerProfile>(json, options);
                if (profile != null && !string.IsNullOrEmpty(profile.Id))
                    parsed.Add(profile);
            }
            catch
            {
                // Silent — embedded resources should always parse, but if a
                // future profile has bad JSON we don't want to take down
                // every consumer.
            }
        });

        db._profiles.AddRange(parsed);
        db._profiles.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
        return db;
    }

}
