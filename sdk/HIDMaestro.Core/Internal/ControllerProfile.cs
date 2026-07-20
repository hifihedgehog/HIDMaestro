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

    /// <summary>v1.3.5 — HID device version (the <c>bcdDevice</c> field returned
    /// by <c>HidD_GetAttributes</c>). Defaults to 0x0100 when omitted, which is
    /// what most generic gamepad consumers expect for USB devices. Real Sony
    /// firmware reports 0 over Bluetooth and 0x0100 over USB; Chromium's
    /// <c>Dualshock4Controller::BusTypeFromVersionNumber</c> reads this exact
    /// value to decide whether to route vibration writes through the BT
    /// (Report 0x11) or USB (Report 0x05) code path. Profiles that emulate a
    /// real Sony BT controller MUST set this to 0 or browser-driven vibration
    /// silently dispatches to the wrong report ID and the bytes get dropped at
    /// the HID class layer.</summary>
    [JsonPropertyName("versionNumber")]
    public ushort? VersionNumber { get; set; }

    /// <summary>v1.3.5 — fixed bytes the SDK overlays into the legacy input
    /// report after the descriptor-driven encoder fills it. Each entry is
    /// <c>{ "byte": N, "value": V }</c> and writes byte V at on-wire offset N.
    /// Used by Edge profiles to satisfy real-firmware status bytes that
    /// dualsense-tester (and any other parser that knows the Edge layout)
    /// reads to decide whether the device is in normal mode vs profile-edit
    /// mode — e.g. the activeProfile byte at struct offset 48 must be
    /// non-zero with bits 0-1 clear (real Sony firmware reports 0x80) or
    /// the page treats every frame as "controller in configuration mode."
    /// Applied AFTER <c>BuildReportInto</c> on the legacy path; the codec
    /// path declares the same constants as <c>uint8</c> fields with
    /// <c>initial</c> values directly inside <c>extendedReport.fields</c>
    /// (so they participate in CRC32 computation).</summary>
    [JsonPropertyName("inputDefaults")]
    public List<InputBytePatch>? InputDefaults { get; set; }

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

    /// <summary>v1.3.9 — structured per-profile physical-design declaration.
    /// Discriminated by <c>kind</c>; concrete shape depends on the kind
    /// (gamepad / joystick / flight_stick / hotas / wheel / pedals / etc.).
    /// When present, <see cref="HIDMaestro.HMProfile.Layout"/> exposes the
    /// typed record and the simple <c>StickCount</c> / <c>TriggerCount</c>
    /// derived views read from this rather than the descriptor heuristic.
    /// When absent, classic classifier-derived behavior applies — backward
    /// compatible with v1.3.8 and earlier.</summary>
    [JsonPropertyName("layout")]
    public HMLayout? Layout { get; set; }

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

    /// <summary>Whether the profile is an Xbox-family controller (matches
    /// "xbox" in id / name / product string, case-insensitive). Microsoft VID
    /// 0x045E covers more than Xbox: SideWinder joysticks, force-feedback
    /// wheels, mice, keyboards. The XUSB companion is meaningful only for
    /// Xbox-shape controllers; this property gates the companion-create path
    /// so a SideWinder Force Feedback 2 (VID=0x045E, joystick TLC) doesn't
    /// receive an XInput companion device.</summary>
    [JsonIgnore]
    public bool IsXboxBranded
    {
        get
        {
            const StringComparison cmp = StringComparison.OrdinalIgnoreCase;
            return (Id?.Contains("xbox", cmp) ?? false)
                || (Name?.Contains("xbox", cmp) ?? false)
                || (ProductString?.Contains("xbox", cmp) ?? false);
        }
    }

    /// <summary>Whether this profile needs an XUSB (XInput) companion devnode.
    /// True only for non-xinputhid Xbox-family controllers (Xbox 360 wired,
    /// the wheel/arcade-stick/dance-pad XInput accessories). xinputhid-bound
    /// profiles publish XUSB through the upper filter on the main HID device,
    /// not a companion. Non-Xbox Microsoft-VID profiles (SideWinder etc.)
    /// don't speak XInput at all.</summary>
    [JsonIgnore]
    public bool RequiresXusbCompanion =>
        VendorId == 0x045E && !UsesUpperFilter && IsXboxBranded;

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
        // v1.3.9 — when the profile authors a layout block, its role-tagged
        // axes deterministically override the classifier's semantic-slot
        // resolution. Validates against the descriptor first; throws
        // HMLayoutValidationException with a structured path on mismatch.
        if (Layout is not null and not HMUnspecifiedLayout)
        {
            HMLayoutValidator.Validate(Layout, b);
            b.ApplyLayoutSemantics(Layout);
        }
        _cachedReportBuilder = b;
        return b;
    }
}

/// <summary>v1.3.5: vendor-blob report layout (input or output direction).
/// Profile JSON describes the byte layout of a vendor blob; the codec walks
/// the field list to encode/decode reports in either direction.
///
/// <para>IMMUTABLE AFTER FIRST USE (issue #34): the codec compiles this
/// spec to numeric opcodes on its first encode/decode and reuses the
/// compiled program for the spec's lifetime, matching
/// <see cref="HMProfile"/>'s documented immutable-identity contract.
/// Mutating fields after a controller has used the spec has no effect on
/// the wire. Build a new profile (HMProfileBuilder or a fresh JSON load)
/// to change layouts.</para></summary>
public sealed class ExtendedReportSpec
{
    /// <summary>Compiled-opcode cache (issue #34). Fields are not
    /// serialized by System.Text.Json, so this never round-trips. Written
    /// once by <see cref="VendorBlobProgram.Get"/> (benign idempotent
    /// race); typed as object to keep the DTO layer free of codec types.</summary>
    internal object? CompiledProgramCache;

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

/// <summary>v1.3.5 — fixed-byte overlay applied to the legacy input report
/// after BuildReportInto. See <see cref="ControllerProfile.InputDefaults"/>.</summary>
public sealed class InputBytePatch
{
    [JsonPropertyName("byte")]
    public int Byte { get; set; }

    [JsonPropertyName("value")]
    public int Value { get; set; }
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

    /// <summary>v1.3.5 — increment step for <c>uint8-rolling</c>. Default 1.
    /// Sony BT effect output's <c>btTag</c> uses stride 16 so the byte cycles
    /// 0x10, 0x20, … 0xF0, 0x00 — real firmware drops packets whose tag
    /// doesn't match that pattern, hence the explicit setting.</summary>
    [JsonPropertyName("stride")]
    public int? Stride { get; set; }
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

        var options = HMLayoutJsonOptions.Default;

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
        var options = HMLayoutJsonOptions.Default;
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
