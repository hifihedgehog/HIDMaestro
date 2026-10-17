using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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

    /// <summary>USB iSerialNumber string, when the device declares one.
    /// Valve's pads do; Sony's do not. Steam reads and logs it.</summary>
    [JsonPropertyName("serialString")]
    public string? SerialString { get; set; }

    /// <summary>USB iConfiguration string, when the configuration
    /// descriptor names one (the Deck calls its "Full-Speed").</summary>
    [JsonPropertyName("configurationString")]
    public string? ConfigurationString { get; set; }

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

    /// <summary>The input report a data-only frame fills, SubmitRawReport's
    /// and SubmitState's legacy path, and the driver sends it as, for a
    /// device where the SDK's own choice is wrong: the report
    /// HidReportBuilder lays out, and for a keyboard or mouse that gets
    /// every report delivered, the descriptor's first input report. A hex
    /// byte such as "0x01", naming an input report the descriptor
    /// declares.</summary>
    [JsonPropertyName("dataReportId")]
    public string? DataReportId { get; set; }

    /// <summary>v1.3.5: HID device version (the <c>bcdDevice</c> field returned
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

    /// <summary>v1.10.0 (PadForge discussion 476): a DualShock 3 presented
    /// the way Sony's sixaxis.sys and DsHidMini's SXS mode present one, which
    /// is what PCSX2 (SDL_HINT_JOYSTICK_HIDAPI_PS3_SIXAXIS_DRIVER) and RPCS3
    /// read on Windows. The profile's extendedReport is the native 49-byte
    /// DualShock 3 report. The driver serves it as the report 0 feature
    /// reply, derives the 12-byte joystick input report from it, and folds
    /// report 0 output commands into the native output report it hands the
    /// SDK.</summary>
    [JsonPropertyName("sixaxisCompatible")]
    public bool? SixaxisCompatible { get; set; }

    /// <summary>v1.3.5: fixed bytes the SDK overlays into the legacy input
    /// report after the descriptor-driven encoder fills it. Each entry is
    /// <c>{ "byte": N, "value": V }</c> and writes byte V at on-wire offset N.
    /// Used by Edge profiles to satisfy real-firmware status bytes that
    /// dualsense-tester (and any other parser that knows the Edge layout)
    /// reads to decide whether the device is in normal mode vs profile-edit
    /// mode: e.g. the activeProfile byte at struct offset 48 must be
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

    /// <summary>The descriptor read from the real device, kept beside the
    /// one the profile presents when the two differ. A record for the
    /// profile's author: no code reads it.</summary>
    [JsonPropertyName("nativeDescriptor")]
    public string? NativeDescriptor { get; set; }

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

    /// <summary>v2.0.0: the wire value a descriptor-path axis carries at
    /// 0.5, by axis name (<c>"X"</c>, <c>"Rz"</c>), for a device whose hosts
    /// read its sticks at rest somewhere other than the field's midpoint.
    /// The field then runs from its logical minimum at 0 through the center
    /// at 0.5 to its logical maximum at 1, each half on its own slope. SDL's
    /// Luna driver reads a stick byte of exactly 0x7F as zero
    /// (SDL_hidapi_luna.c), so the two Luna profiles center their sticks
    /// there. Absent, an axis has no center of its own.</summary>
    [JsonPropertyName("axisCenters")]
    public Dictionary<string, int>? AxisCenters { get; set; }

    /// <summary>Optional SDL gamepad mapping body for profiles SDL's own
    /// database does not cover.
    ///
    /// <para>SDL promotes a joystick to its gamepad layer only when a
    /// mapping exists for the device's GUID. SDL synthesizes one for
    /// devices its HIDAPI, RawInput, WGI or XInput backends claim, but a
    /// device that reaches SDL through DirectInput gets a mapping only
    /// from the database (SDL_gamepad_db.h) or from the application. A
    /// pad too new to be in that database, or one whose vendor protocol
    /// SDL drives over a transport a HID profile cannot offer, therefore
    /// enumerates as a nameless joystick with no button roles.</para>
    ///
    /// <para>This field holds everything after the GUID and name in an
    /// SDL mapping string, ending in a trailing comma, e.g.
    /// <c>"a:b0,b:b1,...,platform:Windows,"</c>. The GUID is per-device
    /// and only exists once SDL has enumerated the pad, so consumers
    /// prepend it themselves: read
    /// <c>SDL_GetJoystickGUIDForID</c>, format it, and pass
    /// <c>$"{guid},{profile.Name},{profile.SdlMapping}"</c> to
    /// <c>SDL_AddGamepadMapping</c>. See
    /// <see cref="HIDMaestro.HMProfile.SdlMapping"/>.</para></summary>
    [JsonPropertyName("sdlMapping")]
    public string? SdlMapping { get; set; }

    /// <summary>v1.3.9: structured per-profile physical-design declaration.
    /// Discriminated by <c>kind</c>; concrete shape depends on the kind
    /// (gamepad / joystick / flight_stick / hotas / wheel / pedals / etc.).
    /// When present, <see cref="HIDMaestro.HMProfile.Layout"/> exposes the
    /// typed record and the simple <c>StickCount</c> / <c>TriggerCount</c>
    /// derived views read from this rather than the descriptor heuristic.
    /// When absent, classic classifier-derived behavior applies: backward
    /// compatible with v1.3.8 and earlier.</summary>
    [JsonPropertyName("layout")]
    public HMLayout? Layout { get; set; }

    /// <summary>If true, skip main HID device: use XUSB companion only.
    /// DI reads from XInput (5 axes), browser reads from XInput (separate triggers).
    /// Used for Xbox 360 where real hardware uses xusb22.sys (no HID).</summary>
    [JsonPropertyName("companionOnly")]
    public bool CompanionOnly { get; set; }

    /// <summary>v1.3.5: optional vendor-blob input report layout. When present,
    /// the SDK emits this report ID via VendorBlobCodec instead of the
    /// descriptor's first declared input. Used for protocols where the
    /// descriptor declares an opaque vendor blob (Sony BT 0x31 / 0x11, etc.).</summary>
    [JsonPropertyName("extendedReport")]
    public ExtendedReportSpec? ExtendedReport { get; set; }

    /// <summary>v1.3.5: optional vendor-blob output report layout. When
    /// present, the SDK decodes incoming output reports of the declared
    /// report ID and surfaces parsed-field events via HMController.OutputDecoded.</summary>
    [JsonPropertyName("extendedOutputReport")]
    public ExtendedReportSpec? ExtendedOutputReport { get; set; }

    /// <summary>Issue #39. Which instantiation path creates this device.
    /// Absent or <c>"umdf2"</c> is the default: one HID device via the
    /// UMDF2 driver. <c>"usbip"</c> marks a composite USB persona, which
    /// rides the USB transport bundled inside HIDMaestro.Core.dll and
    /// deployed on first use.
    ///
    /// <para>This is a property of the DEVICE the profile describes, not
    /// a mode switch on an existing one. A profile that presented four
    /// interfaces on machines with the backend and one without would
    /// name a device plus a machine state. Composite personas therefore
    /// ship as separate profiles, and the five existing USB Sony
    /// profiles are untouched.</para></summary>
    [JsonPropertyName("backend")]
    public string? Backend { get; set; }

    /// <summary>Issue #39. The USB configuration this profile presents
    /// when <see cref="Backend"/> is <c>"usbip"</c>. Null for every
    /// UMDF2 profile, where Windows composes the configuration itself
    /// and only the HID report descriptor is ours to author.</summary>
    [JsonPropertyName("usbConfiguration")]
    public UsbConfigurationSpec? UsbConfiguration { get; set; }

    /// <summary>v2.0.0: how the device behaves beyond its descriptors: its
    /// registers, the commands it takes and the replies they draw, when it
    /// reports and how often, its output decoders, and when it stops
    /// answering (design note, mechanism 3). Null for a device that reports
    /// whatever is submitted as it is submitted. The model groups are read by
    /// the shared parse (<see cref="ProfilePrograms"/>), the one the build
    /// tool runs, not by this object's own JSON context.</summary>
    [JsonIgnore]
    [JsonPropertyName("protocol")]
    public ProtocolSpec? Protocol { get; set; }

    /// <summary>v2.0.0: the bytes SubmitState fills for a device that builds
    /// its own reports (mechanism 2).</summary>
    [JsonIgnore]
    [JsonPropertyName("stateBlock")]
    public StateBlockSpec? StateBlock { get; set; }

    /// <summary>v2.0.0: <see cref="StateBlock"/>'s field list in the codec
    /// vocabulary, read from its <c>fields</c>.</summary>
    [JsonIgnore]
    public List<FieldSpec>? StateBlockFields { get; set; }

    /// <summary>v2.0.0: the reports a device builds from its state block.</summary>
    [JsonIgnore]
    [JsonPropertyName("inputReports")]
    public List<InputReportSpec>? InputReports { get; set; }

    /// <summary>v2.0.0: feature reports whose answer is the current state.</summary>
    [JsonIgnore]
    [JsonPropertyName("liveFeatureReports")]
    public List<LiveFeatureReportSpec>? LiveFeatureReports { get; set; }

    /// <summary>v2.0.0: the device keeps reporting while the consumer is
    /// quiet, as the real device streams. See <see cref="StreamSpec"/>.</summary>
    [JsonPropertyName("stream")]
    public StreamSpec? Stream { get; set; }

    private CompiledModel? _model;

    /// <summary>The model groups compiled into the validated form, once per
    /// profile instance. Throws <see cref="Programs.FormException"/> with the
    /// JSON path of a value it refuses.</summary>
    internal CompiledModel GetOrCompileModel() =>
        _model ??= ModelCompiler.Compile(new DeviceModelGroups
        {
            Protocol = Protocol, StateBlock = StateBlock, InputReports = InputReports, LiveFeatureReports = LiveFeatureReports,
        });

    /// <summary>True when the device builds its reports from a state block
    /// it takes at SubmitState, so a raw submit is a state block too.</summary>
    [JsonIgnore]
    public bool BuildsItsOwnReports => InputReports is { Count: > 0 };

    /// <summary>Reads a profile file: the profile through the SDK's context,
    /// and its model groups through the shared parse the build tool runs.</summary>
    internal static ControllerProfile? Parse(ReadOnlyMemory<byte> utf8)
    {
        utf8 = ProfilePrograms.WithoutBom(utf8);
        var profile = JsonSerializer.Deserialize(utf8.Span, HMJsonContext.Default.ControllerProfile);
        if (profile == null) return null;
        DeviceModelGroups groups;
        try { groups = ProfilePrograms.Read(utf8); }
        catch (Programs.FormException e) { throw new InvalidOperationException(e.ForProfile(profile.Id), e); }
        profile.Protocol = groups.Protocol;
        profile.StateBlock = groups.StateBlock;
        profile.InputReports = groups.InputReports;
        profile.LiveFeatureReports = groups.LiveFeatureReports;
        if (groups.StateBlock?.Fields is { } fields)
            profile.StateBlockFields = fields.Deserialize(HMJsonContext.Default.ListFieldSpec);
        return profile;
    }

    /// <summary>Issue #56. Feature-report answers this persona serves, for
    /// a device whose claiming software interrogates it over
    /// GET_REPORT(Feature). Only meaningful on the <c>usbip</c> backend;
    /// see <see cref="Usbip.FeatureStubTable"/> for the keying rules.</summary>
    [JsonPropertyName("featureStubs")]
    public FeatureStubSpec? FeatureStubs { get; set; }

    /// <summary>v2.0.0: the named per-controller values a feature answer's
    /// patches read beyond the built-in <c>index</c> and <c>serial</c>, such
    /// as a <c>keyedHexFile</c> (<see cref="ValueSourceSpec"/>).</summary>
    [JsonPropertyName("valueSources")]
    public Dictionary<string, ValueSourceSpec>? ValueSources { get; set; }

    /// <summary>True when this profile is a composite USB persona and
    /// therefore takes the USB/IP create path rather than the UMDF2
    /// one.</summary>
    [JsonIgnore]
    public bool RequiresUsbipBackend =>
        Backend?.Equals("usbip", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>v1.13.0: check the profile's port blocks. They fit their
    /// reports, an output report's ports pair with the input report's, every
    /// port field compiles, and a report's own fields leave the blocks to
    /// them. Throws <see cref="InvalidOperationException"/> naming the JSON
    /// path.</summary>
    internal void ValidatePorts()
    {
        var input = PortLayout.Of(ExtendedReport, $"Profile '{Id}': extendedReport", output: false);
        var output = PortLayout.Of(ExtendedOutputReport, $"Profile '{Id}': extendedOutputReport", output: true);
        // The SDK composes a report with ports from the port blocks and the
        // status flags, and encodes none of the report's own fields: those
        // take their values from one gamepad state, where each port has its
        // own. Device-wide status bits are ports.status.flags.
        if (input != null && ExtendedReport!.Fields is { Count: > 0 } beside)
            throw new InvalidOperationException(
                $"Profile '{Id}': extendedReport.fields[0] ('{beside[0].Type}') is beside ports. A report with ports is " +
                "written from its port blocks alone, so the field would never be written. Move it into ports.fields.");
        if (output == null) return;
        if (input == null)
            throw new InvalidOperationException(
                $"Profile '{Id}': extendedOutputReport.ports needs extendedReport.ports, which number the ports.");
        if (output.Count != input.Count)
            throw new InvalidOperationException(
                $"Profile '{Id}': extendedOutputReport.ports.count {output.Count} differs from extendedReport.ports.count {input.Count}.");
        // A report whose ports end past the output ring's data bytes never
        // reaches the ports whole.
        int extent = output.BlockAt(output.Count);
        if (extent - 1 > OutputRingData)
            throw new InvalidOperationException(
                $"Profile '{Id}': extendedOutputReport.ports end at byte {extent}. An output report reaches the SDK " +
                $"with at most {OutputRingData} bytes after its report ID.");
        // An output report's own fields are decoded beside its ports, each
        // under its own name, so none may read a block's bytes.
        var prog = VendorBlobProgram.Get(ExtendedOutputReport!);
        int blocksLo = output.Offset, blocksHi = extent - 1;
        for (int i = 0; i < prog.Fields.Length; i++)
        {
            if (prog.Fields[i].Op == VendorBlobProgram.FieldOp.Crc32 && prog.Fields[i].CrcDst < 0) continue;
            var (lo, hi) = VendorBlobProgram.Extent(in prog.Fields[i], ExtendedOutputReport!.Size);
            if (lo <= blocksHi && hi >= blocksLo)
                throw new InvalidOperationException(
                    $"Profile '{Id}': extendedOutputReport.fields[{i}] ('{prog.Fields[i].Src.Type}') at bytes {lo}-{hi} " +
                    $"overlaps the port blocks at bytes {blocksLo}-{blocksHi}.");
        }
        output.PrepareOutput($"Profile '{Id}': extendedOutputReport");
    }

    /// <summary>The output ring keeps a report's first byte apart and holds
    /// at most this many bytes after it, on both transports (driver.h
    /// HIDMAESTRO_OUTPUT_SLOT_DATA_CAP).</summary>
    private const int OutputRingData = SharedMemoryIO.OUTPUT_SLOT_SIZE - SharedMemoryIO.OUTPUT_SLOT_OFFSET_DATA;

    /// <summary>A dataReportId must be a hex byte that names an input report
    /// of the profile's descriptor, and is refused beside an always-armed
    /// extendedReport, whose report the data frames fill instead.</summary>
    private void ValidateDataReport()
    {
        if (DataReportId == null) return;
        if (ExtendedReport is { AlwaysArmed: true })
            throw new InvalidOperationException(
                $"Profile '{Id}': dataReportId \"{DataReportId}\" is ignored beside extendedReport.alwaysArmed, " +
                $"whose report {ExtendedReport.ReportId} data frames fill. Remove dataReportId.");
        byte id;
        try { id = ReportIds.Parse(DataReportId); }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            throw new InvalidOperationException($"Profile '{Id}': dataReportId \"{DataReportId}\" is not a hex byte.");
        }
        if (GetOrBuildReportMap().Find(HidReportKind.Input, id) == null)
            throw new InvalidOperationException(
                $"Profile '{Id}': dataReportId 0x{id:X2} is not an input report of the profile's descriptor.");
    }

    /// <summary>v1.13.0: check the profile's vendor-blob reports, the top
    /// level of <see cref="ExtendedReport"/> and <see cref="ExtendedOutputReport"/>,
    /// with the field rules <see cref="VendorBlobProgram.Check"/> applies to
    /// port blocks. An input report the SDK sends must fit the shared
    /// section's extended lane, which carries it whole or not at all.</summary>
    private void ValidateExtendedReports()
    {
        CheckExtendedReport(ExtendedReport, "extendedReport", output: false);
        CheckExtendedReport(ExtendedOutputReport, "extendedOutputReport", output: true);
        var input = ExtendedReport;
        bool sent = input != null && (input.AlwaysArmed || input.Ports != null || input.ArmOn is { Count: > 0 });
        if (sent && input!.Size > SharedMemoryIO.EXTENDED_DATA_CAPACITY)
            throw new InvalidOperationException(
                $"Profile '{Id}': extendedReport.size {input.Size} is larger than the {SharedMemoryIO.EXTENDED_DATA_CAPACITY} " +
                "bytes the extended report lane carries.");
    }

    private void CheckExtendedReport(ExtendedReportSpec? spec, string name, bool output)
    {
        if (spec == null) return;
        string where = $"Profile '{Id}': {name}";
        try { _ = spec.ReportIdByte; }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            throw new InvalidOperationException($"{where}.reportId \"{spec.ReportId}\" is not a hex byte.");
        }
        if (spec.Size < 1)
            throw new InvalidOperationException($"{where}.size {spec.Size} must be 1 or more, the report ID's byte included.");
        if (spec.ArmOn != null)
        {
            for (int i = 0; i < spec.ArmOn.Count; i++)
            {
                var t = spec.ArmOn[i] ?? throw new InvalidOperationException($"{where}.armOn[{i}] is null.");
                // The types the output thread arms on (HMController's reader).
                if (t.Type is not ("featureWrite" or "outputWrite" or "featureRead"))
                    throw new InvalidOperationException(
                        $"{where}.armOn[{i}].type '{t.Type}' is not one of featureWrite, outputWrite, featureRead.");
                try { _ = t.ReportIdByte; }
                catch (Exception e) when (e is FormatException or OverflowException)
                {
                    throw new InvalidOperationException($"{where}.armOn[{i}].reportId \"{t.ReportId}\" is not a hex byte.");
                }
            }
        }
        VendorBlobProgram.Check(spec, spec.Size, where, output, portBlock: false);
        if (!output) return;
        // A field that ends past the ring's data bytes is never in the
        // report the SDK decodes. A CRC with no declared place is read at
        // the end of the report as received, wherever that is.
        var prog = VendorBlobProgram.Get(spec);
        for (int i = 0; i < prog.Fields.Length; i++)
        {
            if (prog.Fields[i].Op == VendorBlobProgram.FieldOp.Crc32 && prog.Fields[i].CrcDst < 0) continue;
            var (lo, hi) = VendorBlobProgram.Extent(in prog.Fields[i], spec.Size);
            // A packed-uint field writes no bytes and reads its parts' bits.
            if (prog.Fields[i].Parts is { } parts)
                foreach (var part in parts) { lo = 0; hi = (int)Math.Max(hi, Math.Min(int.MaxValue, part.LastBit / 8)); }
            if (hi > OutputRingData)
                throw new InvalidOperationException(
                    $"{where}.fields[{i}] ('{prog.Fields[i].Src.Type}') at bytes {lo}-{hi} ends past byte {OutputRingData}. " +
                    $"An output report reaches the SDK with at most {OutputRingData} bytes after its report ID.");
        }
    }

    /// <summary>v2.0.0: the device model a profile takes, by name, for the
    /// groups its own keys leave empty: a named model a loaded models folder
    /// holds, or a catalog profile (design note, "Profiles made outside the
    /// catalog"). A name nothing holds, or whose model does not fit the
    /// profile, leaves the identity default and is listed as the profile's
    /// <see cref="HIDMaestro.HMProfile.DeviceModelProblem"/>.</summary>
    [JsonPropertyName("deviceModelFrom")]
    public string? DeviceModelFrom { get; set; }

    /// <summary>v2.0.0: <c>"none"</c> takes no model by name or by identity,
    /// by the author's choice. The profile's own keys still apply.</summary>
    [JsonPropertyName("deviceModel")]
    public string? DeviceModel { get; set; }

    /// <summary>What the profile resolved to, set by the loader with the
    /// registry it loaded under. A profile no loader resolved, one built
    /// in code or constructed by a program, resolves against the catalog's
    /// on first use.</summary>
    [JsonIgnore]
    internal ResolvedModel? ResolvedCache { get; set; }

    /// <summary>The named models and identity defaults the profile resolved
    /// under: its load's, which holds the models folder of the directory it
    /// came from. A copy the builder makes, and ResolveDeviceModel's
    /// profile, resolve under it too. Null for a profile no loader
    /// resolved, which takes the catalog's.</summary>
    [JsonIgnore]
    internal ModelRegistry? Registry { get; set; }

    /// <summary>Whether <see cref="Registry"/>'s identity defaults for
    /// profiles outside the catalog apply: false for a catalog profile.</summary>
    [JsonIgnore]
    internal bool OutsideCatalog { get; set; } = true;

    /// <summary>Resolves the device model again from what the profile holds
    /// now, under the registry it loaded with, for a create: a program can
    /// edit a loaded profile before it creates, and the create serves what
    /// the profile then holds. Throws for a defect of the profile's own
    /// keys, as the load does.</summary>
    internal void ResolveForCreate() =>
        ResolvedCache = ModelRegistry.Resolve(this, Registry ?? ModelRegistry.Catalog, OutsideCatalog);

    /// <summary>Drops everything compiled from the profile's keys: the
    /// parsed descriptor, its report map, the report builder, the keyboard
    /// and mouse packer, and the programs and port layouts of the extended
    /// reports. The load's checks compile them to refuse a bad file, and
    /// keep none of it, so the first create compiles from what the profile
    /// holds then, edits since the load included, as it did through
    /// v1.13.0.</summary>
    internal void ForgetCompiled()
    {
        _cachedKbmPacker = null;
        _cachedReportMap = null;
        _cachedDescriptor = null;
        _descriptorCached = false;
        _cachedReportBuilder = null;
        foreach (var spec in new[] { ExtendedReport, ExtendedOutputReport })
        {
            if (spec == null) continue;
            spec.CompiledProgramCache = null;
            spec.PortLayoutCache = null;
        }
    }

    /// <summary>The device model the profile runs with: its own keys, the
    /// model it names, or its identity default's, group by group.</summary>
    [JsonIgnore]
    internal ResolvedModel Resolved => ResolvedCache ??= ModelRegistry.Resolve(this, ModelRegistry.Catalog,
        outsideCatalog: SourceFile?.StartsWith(ProfileDatabase.CatalogLabel, StringComparison.Ordinal) != true);

    /// <summary>The driver PID the resolved model gives: the profile's
    /// own, or its model's.</summary>
    [JsonIgnore]
    internal string? ResolvedDriverPid => Resolved.Model.DriverPid;

    /// <summary>The predicate of the design note's decision (c),
    /// alternative B: the profile reaches none of the device code a driver
    /// older than 2.0.0 carries, so a create of it may run on such a driver
    /// that other programs hold. Its resolved model is empty in every
    /// group, no identity default's entry matches it, and it has no XUSB
    /// companion and no xinputhid parent. Read only when
    /// <see cref="DriverDeploy.Coexistence"/> is alternative B.</summary>
    internal bool RunsOnOlderDriver =>
        Resolved.Model.IsEmpty && !Resolved.IdentityMatched && !RequiresXusbCompanion && !UsesUpperFilter;

    /// <summary>True when the profile carries keys of its own in a model
    /// group, names a model, or says it takes none: a profile
    /// <see cref="HIDMaestro.HMDeviceExtractor.ResolveDeviceModel"/> leaves
    /// as it is.</summary>
    [JsonIgnore]
    internal bool StatesItsModel =>
        DeviceModelFrom != null || DeviceModel != null || !global::HIDMaestro.Internal.DeviceModel.OwnOf(this).IsEmpty;

    /// <summary>The file the profile was loaded from, for its load issues
    /// and <see cref="HIDMaestro.HMProfileException"/>: a path, or
    /// <c>catalog:&lt;path&gt;</c> for the embedded catalog. Null for a
    /// profile built in code.</summary>
    [JsonIgnore]
    internal string? SourceFile { get; set; }

    /// <summary>v2.0.0: every check a profile meets when it loads, when
    /// <see cref="HIDMaestro.HMProfileBuilder"/> builds it and again before a
    /// create: the descriptor's hex, the device model
    /// (<see cref="ValidateDeviceModel"/>) and the layout against the
    /// descriptor, which until v2.0.0 a create checked only once it built
    /// its first report. Throws what the failing check throws, with the
    /// JSON path in its message (<see cref="ProfileDefects.Locate"/>).</summary>
    internal void ValidateAtLoad()
    {
        // The identity first: the identity defaults match on it below.
        CheckHexId(Vid, "$.vid");
        CheckHexId(Pid, "$.pid");
        AnswerModels.Validate(this, global::HIDMaestro.Internal.DeviceModel.OwnOf(this), taken: false, new List<string>());
        if (DeviceModel != null && DeviceModel != "none")
            throw new ProfileDefectException("$.deviceModel", $"\"{DeviceModel}\" is not a value it takes. The one value is \"none\".");
        if (!HasDescriptor) return;
        ValidateDeviceModel();
        _ = GetOrBuildReportBuilder();
    }

    /// <summary>A VID or PID as <see cref="VendorId"/> and <see cref="ProductId"/>
    /// read it: hex, at most 16 bits, "0x" optional.</summary>
    private static void CheckHexId(string? value, string path)
    {
        if (string.IsNullOrEmpty(value))
            throw new ProfileDefectException(path, "is required, a 16-bit hex ID such as \"0x045E\".");
        try { _ = Convert.ToUInt16(value, 16); }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            throw new ProfileDefectException(path, $"\"{value}\" is not a 16-bit hex ID such as \"0x045E\".");
        }
    }

    /// <summary>v1.13.0: compile everything the profile declares about its
    /// device beyond the descriptors: the port blocks, the USB/IP protocol
    /// against the profile's own descriptors, and the WinUSB package. Runs
    /// before a create takes anything machine-wide, so a mis-authored
    /// profile is refused with nothing to undo.</summary>
    internal void ValidateDeviceModel()
    {
        // The HID descriptor first: a string that is not whole hex bytes
        // throws FormatException here rather than after the lease.
        _ = GetDescriptorBytes();
        ValidateDataReport();
        ValidateExtendedReports();
        ValidatePorts();
        ValidateProgramModel();
        var cfg = UsbConfiguration;
        if (Protocol == null && cfg?.WinUsb == null) return;
        if (!RequiresUsbipBackend)
            throw new InvalidOperationException(
                $"Profile '{Id}': protocol and usbConfiguration.winUsb apply to the usbip backend in this release, " +
                "where the SDK serves the device's endpoints.");
        var descriptors = new Usbip.UsbDescriptorSet(this);
        Usbip.CompiledProtocol.Compile(this, descriptors);
        WinUsbBinding.PackageFor(this, descriptors);
    }

    /// <summary>v2.0.0: the model groups compile, the state block's fields fit
    /// it, and the stream's keys name what the profile declares.</summary>
    private void ValidateProgramModel()
    {
        CompiledModel model;
        try { model = GetOrCompileModel(); }
        catch (Programs.FormException e) { throw new InvalidOperationException(e.ForProfile(Id), e); }
        // A report has one decode: OutputDecoded fires once for it.
        if (ExtendedOutputReport != null)
            foreach (var d in model.Decoders)
                if (Array.IndexOf(d.Reports, ExtendedOutputReport.ReportIdByte) is int at and >= 0)
                    throw new InvalidOperationException(
                        $"Profile '{Id}': protocol.decoders.{d.Name}.reports[{at}] 0x{d.Reports[at]:X2} is extendedOutputReport's report, which the codec decodes. A report has one decode.");
        if (SwitchProPacker.IsSwitchPro(VendorId, ProductId))
            foreach (var d in model.Decoders)
                for (int at = 0; at < d.Reports.Length; at++)
                    if (d.Reports[at] is 0x01 or 0x10)
                        throw new InvalidOperationException(
                            $"Profile '{Id}': protocol.decoders.{d.Name}.reports[{at}] 0x{d.Reports[at]:X2} is the Switch Pro's rumble report, which the SDK decodes. A report has one decode.");
        if (StateBlock != null)
        {
            if (StateBlockFields == null)
                throw new InvalidOperationException($"Profile '{Id}': stateBlock.fields is missing: the state block's fields say what SubmitState writes.");
            VendorBlobProgram.Check(StateBlockSpecForCodec(), StateBlock.Size, $"Profile '{Id}': stateBlock", output: false, portBlock: false);
        }
        if (BuildsItsOwnReports && Protocol == null)
            throw new InvalidOperationException($"Profile '{Id}': inputReports: a device that builds its reports needs a protocol that says when it sends them.");
        if (Stream != null) StreamClock.Compile(this, model.Registers);
    }

    /// <summary>The state block of a device at rest, before any SubmitState:
    /// a default state with the sticks centered and the triggers released,
    /// encoded as SubmitState encodes one.</summary>
    internal byte[] StateBlockAtRest()
    {
        var spec = StateBlockSpecForCodec();
        var block = new byte[spec.Size];
        var rest = new HMGamepadState();
        VendorBlobCodec.EncodeInput(spec, in rest, 0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f, block, new VendorBlobCodec.EncoderState());
        return block;
    }

    private ExtendedReportSpec? _stateBlockSpec;

    /// <summary>The state block as a codec spec: its size and fields, no
    /// report ID, so the codec encodes it as it encodes any report.</summary>
    internal ExtendedReportSpec StateBlockSpecForCodec() =>
        _stateBlockSpec ??= new ExtendedReportSpec { Size = StateBlock!.Size, Fields = StateBlockFields ?? new List<FieldSpec>() };

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

    /// <summary>Built by <see cref="HIDMaestro.HMProfileBuilder"/> rather
    /// than loaded from JSON. A JSON profile describes a real device and
    /// reads through Windows exactly as that device does. Only a profile
    /// built in code can get the GameInput table
    /// (<see cref="GameInputMapping.Applies"/>).</summary>
    [JsonIgnore]
    public bool BuiltInCode { get => _builtInCode; init => _builtInCode = value; }
    private bool _builtInCode;

    /// <summary>Sets <see cref="BuiltInCode"/> on a profile already made,
    /// as a copy and the builder do. The public accessor stays init, as it
    /// was through v1.13.0.</summary>
    internal void MarkBuiltInCode(bool value) => _builtInCode = value;

    /// <summary>A copy of the profile's JSON keys, read again, with where it
    /// came from and whether it was built in code. Nothing a create or a
    /// resolution cached comes with it.</summary>
    internal ControllerProfile Clone()
    {
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(this, HMJsonContext.Default.ControllerProfile),
            HMJsonContext.Default.ControllerProfile)!;
        copy.SourceFile = SourceFile;
        copy.MarkBuiltInCode(BuiltInCode);
        return copy;
    }

    /// <summary>A keyboard or mouse profile (issue #67): <c>type</c> is
    /// <c>keyboard</c> or <c>mouse</c>. These deliver every report in order,
    /// and have no joy.cpl entry.</summary>
    [JsonIgnore]
    public bool IsKeyboardOrMouse =>
        string.Equals(Type, "keyboard", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Type, "mouse", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    private KeyboardMousePacker? _cachedKbmPacker;

    /// <summary>The keyboard, mouse and consumer-control packer for this
    /// profile's descriptor, built once.</summary>
    internal KeyboardMousePacker GetOrBuildKeyboardMousePacker() =>
        _cachedKbmPacker ??= new KeyboardMousePacker(GetOrBuildReportMap());

    [JsonIgnore]
    private HidReportMap? _cachedReportMap;

    /// <summary>The descriptor's reports, parsed once and read by both
    /// the report builder and the keyboard and mouse packer.</summary>
    internal HidReportMap GetOrBuildReportMap() =>
        _cachedReportMap ??= HidReportMap.Parse(GetDescriptorBytes() ?? Array.Empty<byte>());

    /// <summary>Issue #67. A keyboard or mouse profile gets every frame
    /// delivered, and so does an "other" profile whose descriptor is a
    /// mouse or a keyboard, the shape a consumer built before the types
    /// existed.</summary>
    internal bool DeliversEveryReport(KeyboardMousePacker kbm) =>
        IsKeyboardOrMouse
        || (string.Equals(Type, "other", StringComparison.OrdinalIgnoreCase)
            && (kbm.HasMouse || kbm.HasKeyboard));

    /// <summary>The report a keyboard or mouse that gets every report
    /// delivered sends a data-only frame as: its dataReportId, else the
    /// descriptor's first input report.</summary>
    internal byte KeyboardMouseDataReportId(KeyboardMousePacker kbm) =>
        DataReportId != null ? ReportIds.Parse(DataReportId) : kbm.FirstInputReportId;

    // Lazy-cached parsed descriptor. v1.3.0: GetDescriptorBytes is called
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
    /// cached on the instance after the first call, so it is safe to call
    /// repeatedly. Spaces and dashes between digits are ignored.</summary>
    /// <exception cref="FormatException">The string holds a character that
    /// is not a hex digit, or an odd number of digits, whose last byte would
    /// be incomplete. The message names the profile and its descriptor
    /// field.</exception>
    public byte[]? GetDescriptorBytes()
    {
        // The flag is read and written with acquire and release order: a
        // load resolves profiles in parallel, and one profile's resolution
        // can read a profile it names while that one resolves.
        if (System.Threading.Volatile.Read(ref _descriptorCached)) return _cachedDescriptor;
        if (string.IsNullOrEmpty(Descriptor))
        {
            _cachedDescriptor = null;
            System.Threading.Volatile.Write(ref _descriptorCached, true);
            return null;
        }
        var hex = Descriptor.Replace(" ", "").Replace("-", "");
        // Refused whole, never cut to whole bytes: until v1.13.0 an odd
        // count dropped the last digit and the device served a descriptor a
        // byte short.
        if (hex.Length % 2 != 0)
            throw new FormatException(
                $"Profile '{Id}': descriptor has an odd number of hex digits ({hex.Length}), so its last byte is incomplete.");
        byte[] bytes;
        try { bytes = Convert.FromHexString(hex); }
        catch (FormatException)
        {
            throw new FormatException($"Profile '{Id}': descriptor holds a character that is not a hex digit.");
        }
        _cachedDescriptor = bytes;
        System.Threading.Volatile.Write(ref _descriptorCached, true);
        return bytes;
    }

    // v1.3.0 T10: lazy-cached parsed HidReportBuilder. The HMController constructor
    // calls HidReportBuilder.Parse(descriptor, axisMap) on every
    // CreateController, which walks the descriptor byte-by-byte building
    // InputFields + ResolveSemantics + ApplyAxisMap. Same descriptor +
    // same axisMap = identical output, so a per-profile cache eliminates
    // the per-controller parse cost. The builder is configured once
    // (ButtonMap + TriggerButtons set immediately after Parse) and then
    // only read by SubmitState: safe to share across HMController
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
        // Issue #58. An alwaysArmed profile never emits through the
        // legacy path, so the report it declares in extendedReport IS its
        // input report and every prepend site should agree. A profile that
        // arms on demand keeps position-based selection, because its
        // pre-arm report is a different, real report that joy.cpl and
        // RawInput read (every Sony BT profile depends on that).
        // A dataReportId names the report a data-only frame fills.
        byte preferredRid = ExtendedReport is { AlwaysArmed: true }
            ? ExtendedReport.ReportIdByte
            : DataReportId != null ? ReportIds.Parse(DataReportId) : (byte)0;
        var b = HidReportBuilder.Parse(GetOrBuildReportMap(), AxisMap, preferredRid);
        b.ButtonMap = ButtonMap;
        b.TriggerButtons = TriggerButtons;
        // v1.3.9: when the profile authors a layout block, its role-tagged
        // axes deterministically override the classifier's semantic-slot
        // resolution. Validates against the descriptor first; throws
        // HMLayoutValidationException with a structured path on mismatch.
        if (Layout is not null and not HMUnspecifiedLayout)
        {
            HMLayoutValidator.Validate(Layout, b);
            b.ApplyLayoutSemantics(Layout);
        }
        b.SetStickAxes(StickAxesOf(Layout, b));
        try { b.SetAxisCenters(ParseAxisCenters()); }
        catch (InvalidOperationException e) { throw new InvalidOperationException($"Profile '{Id}': {e.Message}", e); }
        _cachedReportBuilder = b;
        return b;
    }

    private Dictionary<HMAxis, int>? ParseAxisCenters()
    {
        if (AxisCenters == null) return null;
        var centers = new Dictionary<HMAxis, int>(AxisCenters.Count);
        foreach (var (name, center) in AxisCenters)
        {
            // TryParse also reads a number and a comma list, which no axis
            // name is.
            if (name.Length == 0 || !char.IsLetter(name[0]) || name.Contains(',')
                || !Enum.TryParse<HMAxis>(name, ignoreCase: true, out var axis) || axis == HMAxis.None || !Enum.IsDefined(axis))
                throw new InvalidOperationException($"axisCenters.{name}: not an axis name such as X, Y, Z or Rz.");
            if (!centers.TryAdd(axis, center))
                throw new InvalidOperationException($"axisCenters.{name}: the axis {axis} is named twice.");
        }
        return centers;
    }

    /// <summary>The axes the layout names as sticks, whose fields rest at
    /// their center when a consumer leaves them unwritten. A layout of a
    /// kind with sticks names them. A profile with no layout takes the
    /// sticks the descriptor's classifier found, as
    /// <see cref="HMProfile.Sticks"/> lists them. Wheels, pedals and the
    /// other kinds name none.</summary>
    internal static List<HMAxis> StickAxesOf(HMLayout? layout, HidReportBuilder b)
    {
        var axes = new List<HMAxis>();
        void Add(HMStick s) { axes.Add(s.XAxis); axes.Add(s.YAxis); }
        void Slot(HidReportBuilder.InputField? f) { if (f != null) axes.Add((HMAxis)((f.UsagePage << 8) | f.Usage)); }
        switch (layout)
        {
            case HMGamepadLayout g: foreach (var s in g.Sticks) Add(s); break;
            case HMJoystickLayout j: Add(j.Stick); break;
            case HMFlightStickLayout f: Add(f.Stick); break;
            case HMHotasLayout h: Add(h.Stick); break;
            case null:
            case HMUnspecifiedLayout:
                Slot(b.LeftStickX); Slot(b.LeftStickY); Slot(b.RightStickX); Slot(b.RightStickY);
                Slot(b.ThirdStickX); Slot(b.ThirdStickY); Slot(b.FourthStickX); Slot(b.FourthStickY);
                break;
        }
        return axes;
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

    /// <summary>The compiled <see cref="PortLayout"/> of <see cref="Ports"/>,
    /// built on first use. Not serialized.</summary>
    internal object? PortLayoutCache;

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

    /// <summary>Emit this report from the first frame, with no host
    /// handshake. For controllers whose real firmware powers up already
    /// streaming the vendor report rather than switching into it: the
    /// Switch 2 Pro emits report 0x09 from construction and only leaves
    /// it when a host sends subUSBSelectReport (VIIPER device.go sets
    /// activeReportID = ReportIDPro at construction).
    ///
    /// <para>Mutually exclusive with <see cref="ArmOn"/> in practice. A
    /// Sony BT profile arms on a Get_Feature because its real firmware
    /// does; a profile that sets this one has no handshake to wait for,
    /// and waiting would leave every consumer reading the descriptor's
    /// first declared report, which for these controllers is an opaque
    /// vendor blob with no buttons or axes in it.</para></summary>
    [JsonPropertyName("alwaysArmed")]
    public bool AlwaysArmed { get; set; }

    /// <summary>Frame interval, in milliseconds, at which this device keeps
    /// streaming while the consumer is quiet. 0 leaves the device
    /// event-driven, which is the default and what every profile before
    /// issue #56 wanted.
    ///
    /// Valve's own drivers require a stream. SDL_hidapi_steamdeck.c's
    /// InitDevice reads with a 16 ms timeout to work out which of the three
    /// same-VID/PID HID interfaces is the controller, and returns false when
    /// that read comes back empty, so an idle persona is rejected before it
    /// ever reaches the joystick layer. Real hardware streams at about
    /// 4 ms whether or not anything is moving.</summary>
    [JsonPropertyName("idleFrameIntervalMs")]
    public int IdleFrameIntervalMs { get; set; }

    /// <summary>Ordered field descriptors. See VendorBlobCodec for the type
    /// vocabulary.</summary>
    [JsonPropertyName("fields")]
    public List<FieldSpec> Fields { get; set; } = new();

    /// <summary>v1.13.0: the report's repeated per-port blocks, for a device
    /// that carries several controllers in one report (a multi-port adapter,
    /// a multitap, a receiver). An input report with ports has no fields
    /// of its own: <see cref="Fields"/> stays empty. An output report's
    /// own fields sit outside its port blocks. See <see cref="PortsSpec"/>.</summary>
    [JsonPropertyName("ports")]
    public PortsSpec? Ports { get; set; }

    /// <summary>Why the report is laid out as it is. Documentation only,
    /// as on a feature answer.</summary>
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonIgnore]
    public byte ReportIdByte => ReportIds.Parse(ReportId);
}

/// <summary>Issue #56, v2.0.0: the answers a device gives on request, one
/// table on both transports (design note, mechanism 4). Each entry says
/// which GET it answers: a plain entry answers the report it names, and a
/// message entry answers it only while the last SET_FEATURE on its
/// <c>onReport</c> carried its message. Sony's report-ID answers and
/// Valve's command channel are both entries of this table.</summary>
public sealed class FeatureStubSpec
{
    /// <summary>The table-level matching of v1.7.0 to v1.13.0, read so a
    /// copy of a profile from those releases keeps loading.
    /// <c>"lastMessage"</c> makes every entry without a <c>message</c> a
    /// message entry whose message is its <c>id</c>, at
    /// <see cref="MessageByte"/> of the SET's data on the channel report
    /// (report 0 for a descriptor without report IDs, else the first
    /// feature report the descriptor declares), answered on that report
    /// with the report ID in byte 0. <c>"reportId"</c> is the plain form.
    /// An entry of either form publishes a feature-read notification, as
    /// every answer did on USB/IP then. New profiles write the entries'
    /// own keys.</summary>
    [JsonPropertyName("match")]
    public string? Match { get; set; }

    /// <summary>Under <see cref="Match"/> <c>"lastMessage"</c>, the byte of
    /// the SET's data that carries the message, its parameter two bytes
    /// on: 0 for a descriptor without report IDs, 1 when the report ID
    /// precedes it.</summary>
    [JsonPropertyName("messageByte")]
    public int MessageByte { get; set; }

    [JsonPropertyName("reports")]
    public List<FeatureStubReport> Reports { get; set; } = new();
}

/// <summary>One answer of a <see cref="FeatureStubSpec"/> table.</summary>
public sealed class FeatureStubReport
{
    /// <summary>The report ID the GET arrives on, hex, 0 for a descriptor
    /// that declares none. Under the table-level <c>lastMessage</c> form of
    /// earlier releases, the message ID.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonIgnore]
    public byte IdByte => ReportIds.Parse(Id);

    /// <summary>The report's wire length, report ID included. The device
    /// pads to it.</summary>
    [JsonPropertyName("size")]
    public int Size { get; set; }

    /// <summary>The answer's leading bytes, hex, verbatim from a real
    /// device's reply. Shorter than <see cref="Size"/> is normal: the tail
    /// is zeros.</summary>
    [JsonPropertyName("data")]
    public string? Data { get; set; }

    /// <summary>The SET this answer follows: the entry answers only while
    /// the last SET_FEATURE on <see cref="AnswerMessageSpec.OnReport"/>
    /// carried <see cref="AnswerMessageSpec.Value"/> at
    /// <see cref="AnswerMessageSpec.At"/>. Absent for a plain entry, which
    /// answers its report whatever was set.</summary>
    [JsonPropertyName("message")]
    public AnswerMessageSpec? Message { get; set; }

    /// <summary>A further byte of the same SET, the message's parameter,
    /// when one message has several answers: Valve's
    /// ID_GET_STRING_ATTRIBUTE (0xAE) answers a different string for each
    /// index. An entry with a parameter that matches comes before one
    /// without. Earlier releases wrote a bare number, the byte two after
    /// the message.</summary>
    [JsonPropertyName("param")]
    [JsonConverter(typeof(AnswerParamConverter))]
    public AnswerParamSpec? Param { get; set; }

    /// <summary>Answers served in turn by successive GETs after the
    /// matching SET, hex each, the last one repeating. A SET on the
    /// message's report starts them again. A message entry only. When
    /// given, <see cref="Data"/> is left out.</summary>
    [JsonPropertyName("pages")]
    public List<string>? Pages { get; set; }

    /// <summary>Answer by echoing the message that was written, padded to
    /// <see cref="Size"/>, rather than with a fixed <see cref="Data"/>.
    /// Valve's ID_SET_SETTINGS_VALUES (0x87) reads back as the settings
    /// block the host just wrote, so no constant can serve it. Observed on
    /// a real Steam Deck answering a real Steam client.</summary>
    [JsonPropertyName("echo")]
    public bool Echo { get; set; }

    /// <summary><c>"requestReportId"</c>: byte 0 of the answer is the report
    /// ID the GET was asked on.</summary>
    [JsonPropertyName("replyHead")]
    public string? ReplyHead { get; set; }

    /// <summary>A request shorter than this fails: STATUS_BUFFER_TOO_SMALL
    /// on UMDF2, a stall on USB/IP. Absent, a shorter request gets the
    /// answer cut to its length.</summary>
    [JsonPropertyName("minLength")]
    public int? MinLength { get; set; }

    /// <summary>The sizes the answer comes in, the largest the caller's
    /// buffer holds served: report 0x02 of a DualShock 4 is 41 bytes to a
    /// caller with room for them and 37 to one with 37 to 40. A request
    /// smaller than the smallest fails.</summary>
    [JsonPropertyName("sizes")]
    public List<int>? Sizes { get; set; }

    /// <summary>A CRC-32 the SDK computes over the answer from its report ID,
    /// after a seed, and writes little-endian in its last four bytes, as a
    /// Sony pad ends a feature report over Bluetooth.</summary>
    [JsonPropertyName("crc32")]
    public AnswerCrcSpec? Crc32 { get; set; }

    /// <summary>Serving the answer publishes a feature-read notification,
    /// which arms an extended report whose <c>armOn</c> names the read.
    /// Absent, it is true on the usbip backend, which through v1.13.0
    /// published one on every answered GET, and false on UMDF2, whose
    /// driver notified only the Sony reads the Sony models name.</summary>
    [JsonPropertyName("notify")]
    public bool? Notify { get; set; }

    /// <summary>v2.0.0: bytes of the answer that differ per controller,
    /// each from a value source (<see cref="AnswerPatchSpec"/>). Absent for
    /// every answer whose bytes are fixed.</summary>
    [JsonPropertyName("patch")]
    public List<AnswerPatchSpec>? Patch { get; set; }

    /// <summary>The key v1.9.1 to v1.13.0 used for Steam's firmware stamp,
    /// read only so a profile that carries it is refused naming its
    /// replacement, a <see cref="Patch"/> from a keyedHexFile value
    /// source.</summary>
    [JsonInclude]
    [JsonPropertyName("steamFirmwareStamp")]
    internal JsonElement? RemovedSteamFirmwareStamp { get; set; }

    /// <summary>Why this answer is what it is. Documentation only.</summary>
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

/// <summary>v2.0.0: the SET a message entry follows.</summary>
public sealed class AnswerMessageSpec
{
    /// <summary>The feature report the SET arrives on, hex, 0 for a
    /// descriptor that declares none.</summary>
    [JsonPropertyName("onReport")]
    public string OnReport { get; set; } = "";

    [JsonIgnore]
    public byte OnReportByte => ReportIds.Parse(OnReport);

    /// <summary>The offset of the message in the SET's data, counted from
    /// the report ID for a descriptor that declares report IDs and from the
    /// first data byte for one that declares none.</summary>
    [JsonPropertyName("at")]
    public int At { get; set; }

    /// <summary>The message's bytes, hex.</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

/// <summary>v2.0.0: a message's parameter, one byte of the SET.</summary>
public sealed class AnswerParamSpec
{
    /// <summary>Its offset, counted as <see cref="AnswerMessageSpec.At"/>
    /// is. Null in the bare-number form of earlier releases, which put it
    /// two bytes after the message.</summary>
    [JsonPropertyName("at")]
    public int? At { get; set; }

    /// <summary>The byte, a number or hex.</summary>
    [JsonPropertyName("value")]
    public int Value { get; set; }
}

/// <summary>Reads <see cref="AnswerParamSpec"/> from an object, or from the
/// bare number earlier releases wrote.</summary>
internal sealed class AnswerParamConverter : JsonConverter<AnswerParamSpec>
{
    public override AnswerParamSpec? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.Number) return new AnswerParamSpec { Value = reader.GetInt32() };
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("param is a number or { at, value }.");
        var p = new AnswerParamSpec();
        bool sawValue = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString()!;
            if (!seen.Add(name)) throw new JsonException($"param.{name} is written twice.");
            reader.Read();
            switch (name.ToLowerInvariant())
            {
                case "at": p.At = reader.GetInt32(); break;
                case "value":
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        string s = reader.GetString()!;
                        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
                        if (!byte.TryParse(s, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out byte b))
                            throw new JsonException($"param.value \"{reader.GetString()}\" is not a hex byte.");
                        p.Value = b;
                    }
                    else p.Value = reader.GetInt32();
                    sawValue = true;
                    break;
                default: throw new JsonException($"The JSON property 'param.{name}' could not be mapped. param takes at and value.");
            }
        }
        if (!sawValue) throw new JsonException("param needs its value.");
        return p;
    }

    public override void Write(Utf8JsonWriter writer, AnswerParamSpec value, JsonSerializerOptions options)
    {
        if (value.At == null) { writer.WriteNumberValue(value.Value); return; }
        writer.WriteStartObject();
        writer.WriteNumber("at", value.At.Value);
        writer.WriteString("value", $"{value.Value:X2}");
        writer.WriteEndObject();
    }
}

/// <summary>v2.0.0: the CRC-32 of a feature answer.</summary>
public sealed class AnswerCrcSpec
{
    /// <summary>Bytes the CRC runs over before the answer, hex: <c>A3</c> for
    /// a Sony feature report over Bluetooth.</summary>
    [JsonPropertyName("seed")]
    public string Seed { get; set; } = "";
}

/// <summary>v2.0.0: bytes of a feature answer that differ per controller
/// (design note, mechanism 5). <see cref="Source"/> names <c>index</c>, the
/// controller's transport index, <c>serial</c>, the serial the device
/// serves, or a value source the profile's <c>valueSources</c> declares.
/// The value is written at <see cref="At"/> in <see cref="Encoding"/>, over
/// <see cref="Length"/> bytes of the answer's data.</summary>
public sealed class AnswerPatchSpec
{
    /// <summary>The first byte written, counted from byte 0 of the answer,
    /// which is the report ID for a descriptor that declares report IDs.</summary>
    [JsonPropertyName("at")]
    public int At { get; set; }

    /// <summary>The bytes written: 1, 2 and 4 for <c>uint8</c>,
    /// <c>uint16-le</c> and <c>uint32-le</c>, any count for the two text
    /// encodings.</summary>
    [JsonPropertyName("length")]
    public int Length { get; set; }

    /// <summary><c>uint8</c>, <c>uint16-le</c>, <c>uint32-le</c>, <c>ascii</c>
    /// (a text value, zero padded or cut to the length) or
    /// <c>ascii-decimal</c> (a number's decimal digits, zero padded or cut
    /// to the length).</summary>
    [JsonPropertyName("encoding")]
    public string Encoding { get; set; } = "";

    /// <summary><c>index</c>, <c>serial</c> or a <c>valueSources</c>
    /// name.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    /// <summary>The value's key in a <c>keyedHexFile</c> source, such as
    /// <c>TRITON_FW_TS</c>.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>A number added to a numeric value before it is written,
    /// such as 0x30 for a MAC whose last byte is 0x30 plus the index.</summary>
    [JsonPropertyName("add")]
    public long Add { get; set; }
}

/// <summary>v2.0.0: a value a profile's patches read by name (design note,
/// mechanism 5). <c>keyedHexFile</c> is a value read from a
/// <c>KEY:HEX</c> line of a file found under the first of
/// <see cref="Roots"/> that exists, as Steam's own updater reads its
/// config. It is read when an answer is asked for, through a cache keyed on
/// the file's last write time, so a value that changes on disk is followed
/// without recreating the device.</summary>
public sealed class ValueSourceSpec
{
    /// <summary>The one kind: <c>keyedHexFile</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>Folders to look in, first that exists wins.</summary>
    [JsonPropertyName("roots")]
    public List<ValueRootSpec>? Roots { get; set; }

    /// <summary>The file, relative to the root found.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

/// <summary>A folder a value source looks in: a registry value that holds a
/// folder, or a known folder with a path under it.</summary>
public sealed class ValueRootSpec
{
    /// <summary>A registry key, <c>HKCU\...</c> or <c>HKLM\...</c>, whose
    /// <see cref="Value"/> names a folder.</summary>
    [JsonPropertyName("registry")]
    public string? Registry { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>A <see cref="Environment.SpecialFolder"/> name, such as
    /// <c>ProgramFilesX86</c>, with <see cref="Path"/> under it.</summary>
    [JsonPropertyName("knownFolder")]
    public string? KnownFolder { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

/// <summary>v1.3.5: fixed-byte overlay applied to the legacy input report
/// after BuildReportInto. See <see cref="ControllerProfile.InputDefaults"/>.</summary>
public sealed class InputBytePatch
{
    [JsonPropertyName("byte")]
    public int Byte { get; set; }

    [JsonPropertyName("value")]
    public int Value { get; set; }
}

/// <summary>A report ID as profiles write it, "0x31" or "31", hex.</summary>
internal static class ReportIds
{
    /// <summary>Parse without allocating: the codec reads the ID on every
    /// frame it encodes and the output reader on every packet. Until
    /// v1.13.0 each read cut the "0x" off with Substring, 32 bytes a time.</summary>
    public static byte Parse(string? id)
    {
        if (string.IsNullOrEmpty(id)) return 0;
        ReadOnlySpan<char> hex = id.AsSpan();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        return byte.Parse(hex, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>v1.3.5: host-side write trigger that arms extended-report emission.
/// Type "featureWrite" matches an outgoing HID feature SetFeature; "outputWrite"
/// matches an outgoing HID output report. ReportId is hex.</summary>
public sealed class ArmTrigger
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("reportId")]
    public string ReportId { get; set; } = "";

    [JsonIgnore]
    public byte ReportIdByte => ReportIds.Parse(ReportId);
}

/// <summary>v1.3.5: single-field descriptor inside an ExtendedReportSpec.
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

    /// <summary>v1.3.5: increment step for <c>uint8-rolling</c>. Default 1.
    /// Sony BT effect output's <c>btTag</c> uses stride 16 so the byte cycles
    /// 0x10, 0x20, … 0xF0, 0x00: real firmware drops packets whose tag
    /// doesn't match that pattern, hence the explicit setting.</summary>
    [JsonPropertyName("stride")]
    public int? Stride { get; set; }

    /// <summary>Issue #64: for a <c>uint16-le</c> sensor timestamp, how many
    /// <see cref="HMGamepadState.SensorTimestamp"/> ticks (1/3 µs) make one
    /// wire tick. A DualShock 4 counts in 16/3 µs, so its profiles set 16.
    /// Default 1.</summary>
    [JsonPropertyName("divisor")]
    public int? Divisor { get; set; }

    /// <summary>Issue #64: for a <c>uint8-battery</c> field, the value the
    /// level bits carry while <see cref="HMGamepadState.BatteryFull"/> is
    /// set, in place of the 0..10 level. A DualShock 4 on a cable reports
    /// 11 when charged (Linux hid-playstation DS4_BATTERY_STATUS_FULL, SDL's
    /// SDL_POWERSTATE_CHARGED branch). Unset leaves the level as is.</summary>
    [JsonPropertyName("fullValue")]
    public int? FullValue { get; set; }

    /// <summary>v1.10.0: for a <c>uint10-be</c> sensor field, the wire
    /// counts per unit. A DualShock 3 reads 113 per g (hid-sony.c
    /// SIXAXIS_ACC_RES_PER_G) and 123 per 90 degrees/second (RPCS3
    /// ds4_pad_handler.cpp, "The ds3 resolution is 123/90°/sec").</summary>
    [JsonPropertyName("resolution")]
    public int? Resolution { get; set; }

    /// <summary>v1.10.0: for a <c>uint10-be</c> sensor field, the source's
    /// counts per the same unit: 8192 per g for the SDK's accelerometer
    /// fields and 1440 per 90 degrees/second for its gyro fields, which
    /// carry 16 per degree/second.</summary>
    [JsonPropertyName("sourceResolution")]
    public int? SourceResolution { get; set; }

    /// <summary>v1.10.0: for a <c>uint10-be</c> sensor field, true when the
    /// wire axis points opposite to the SDK's. v2.0.0: the same for every
    /// axis and trigger type (<c>uint8-axis</c>, <c>uint8-trigger</c>,
    /// <c>int16-axis</c>, <c>int16-pad</c>, <c>int16-pad-or-stick</c>,
    /// <c>uint16-trigger</c>, and the Y half of <c>stick12-pair</c>), which
    /// the SDK runs 0 at the left and at the top, such as a stick Y that
    /// grows upward.</summary>
    [JsonPropertyName("invert")]
    public bool? Invert { get; set; }

    /// <summary>v2.0.0: for a type that carries a normalized value
    /// (<c>uint8-axis</c>, <c>uint8-trigger</c>, <c>stick12-pair</c>,
    /// <c>int16-axis</c>, <c>int16-pad-or-stick</c>'s stick,
    /// <c>uint16-trigger</c>), the wire value at the SDK's 0, and for
    /// <c>uint16-pressure</c> the value with no contact. Defaults to the
    /// type's full range: 0, or -32767 for the signed axes.</summary>
    [JsonPropertyName("min")]
    public int? Min { get; set; }

    /// <summary>v2.0.0: the wire value at the SDK's 1, and for
    /// <c>uint16-pressure</c> the value of a contact. Defaults to 255,
    /// 4095, 32767 or 65535 by the type's width.</summary>
    [JsonPropertyName("max")]
    public int? Max { get; set; }

    /// <summary>v2.0.0: for an <c>int16-le</c> field of a calibrated
    /// source (<c>accelGX</c> to <c>accelGZ</c> in g, <c>gyroDpsX</c> to
    /// <c>gyroDpsZ</c> in degrees per second), the counts per unit: a
    /// number, or an exact ratio <c>[13371, 936]</c>.</summary>
    [JsonPropertyName("scale")]
    public ScaleSpec? Scale { get; set; }

    /// <summary>v2.0.0: with <see cref="Scale"/>, the calibrated value
    /// negated before it is scaled, for a wire axis that points the other
    /// way from the SDK's sensor frame.</summary>
    [JsonPropertyName("negate")]
    public bool? Negate { get; set; }

    /// <summary>v2.0.0: with <see cref="Scale"/>, <c>[count, stride]</c>:
    /// the field written <c>count</c> times, <c>stride</c> bytes apart, for
    /// a report that carries several motion frames of one sample.</summary>
    [JsonPropertyName("repeat")]
    public List<int>? Repeat { get; set; }

    /// <summary>v2.0.0: <c>"resolved"</c> on a <c>button-mask</c> with
    /// DPAD_* names or a <c>hat-octant</c>: the d-pad from
    /// <see cref="HMGamepadState.HatDegrees"/>, then HatHundredths, then
    /// HatRaw, then Hat, the first one set, where it is otherwise read from
    /// Hat alone.</summary>
    [JsonPropertyName("dpad")]
    public string? Dpad { get; set; }

    /// <summary>v2.0.0: for <c>uint8-level-map</c>, the byte for each
    /// <see cref="HMGamepadState.BatteryLevel"/> from 0 to 10, eleven
    /// entries.</summary>
    [JsonPropertyName("levels")]
    public List<int>? Levels { get; set; }

    /// <summary>v2.0.0: for <c>uint8-level-map</c>, the byte while
    /// <see cref="HMGamepadState.BatteryCharging"/> is set.
    /// <see cref="FullValue"/>, while BatteryFull is set, goes before
    /// it.</summary>
    [JsonPropertyName("chargingValue")]
    public int? ChargingValue { get; set; }

    /// <summary>v2.0.0: for <c>packed-uint</c>, an output field decoded
    /// from bit groups: each part's value, mapped, and the largest mapped
    /// value of every part and frame is the field's.</summary>
    [JsonPropertyName("parts")]
    public List<PackedPartSpec>? Parts { get; set; }

    /// <summary>v2.0.0: <c>"accumulate"</c> on a <c>uint16-le</c> or
    /// <c>uint32-le</c> sensorTimestamp: the field carries the encoder's
    /// accumulator over <see cref="Divisor"/>, modulo its width. The
    /// accumulator counts the controller's clock until the first nonzero
    /// consumer stamp and the consumer's stamps from then on, and adds the
    /// report's idleFrameIntervalMs for a stamp that restarted or ran
    /// backward. Without it a uint32-le carries the consumer's stamp as
    /// is.</summary>
    [JsonPropertyName("clock")]
    public string? Clock { get; set; }

    /// <summary>v2.0.0: for an accumulating sensorTimestamp, a condition on
    /// the profile's protocol registers while which the accumulator adds
    /// nothing and the field repeats its last value, as a Triton's stamp
    /// stays frozen while its IMU is off. The registers' initial values hold
    /// until a host's command sets them.</summary>
    [JsonPropertyName("holdWhile")]
    public string? HoldWhile { get; set; }
}

/// <summary>v1.3.5: CRC32 scope spec for a crc32-le field. The CRC is
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
/// Loads and queries controller profiles from a directory or the catalog
/// embedded in the SDK.
///
/// <para>v2.0.0 loads strictly and in a fixed order. Files are read in
/// ordinal order of their paths. A file that does not parse, carries a key
/// the schema lacks or a key written twice, or fails a check the profile
/// meets at load (<see cref="ControllerProfile.ValidateAtLoad"/>) is
/// refused, a file with no <c>id</c> is not a profile, and a second file
/// with an id already read is refused and names the first. Each becomes an
/// entry of <see cref="Issues"/> with the file, the JSON path and the
/// reason. Through v1.13.0 such a file was skipped without a word.</para>
/// </summary>
public sealed class ProfileDatabase
{
    private readonly List<ControllerProfile> _profiles = new();
    private readonly List<HIDMaestro.HMProfileLoadIssue> _issues = new();

    public IReadOnlyList<ControllerProfile> All => _profiles;

    /// <summary>Every file this load refused, in the order it read
    /// them.</summary>
    internal IReadOnlyList<HIDMaestro.HMProfileLoadIssue> Issues => _issues;

    /// <summary>The prefix of the catalog's files in their labels.</summary>
    internal const string CatalogLabel = "catalog:";

    /// <summary>The models and identity defaults this load resolved its
    /// profiles under: for the catalog, its own.</summary>
    internal ModelRegistry? Registry { get; private set; }

    /// <summary>
    /// Loads every .json profile file under the given directory, recursively,
    /// but schema.json and the <c>models</c> folder at its top, whose files
    /// are models and not profiles. The folder's named models and identity
    /// defaults join the catalog's for this load's profiles.
    /// </summary>
    public static ProfileDatabase Load(string profilesDir) => Load(profilesDir, ModelRegistry.Catalog);

    /// <summary>The same, with the models and identity defaults of earlier
    /// loads in <paramref name="registry"/>. <see cref="Registry"/> is that
    /// registry with this directory's models folder added.</summary>
    internal static ProfileDatabase Load(string profilesDir, ModelRegistry registry)
    {
        if (!Directory.Exists(profilesDir))
            throw new DirectoryNotFoundException($"Profiles directory not found: {profilesDir}");

        string root = Path.GetFullPath(profilesDir);
        string models = Path.Combine(root, "models") + Path.DirectorySeparatorChar;
        var modelIssues = new List<HIDMaestro.HMProfileLoadIssue>();
        registry = registry.WithDirectory(models, modelIssues);
        var files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("schema.json", StringComparison.OrdinalIgnoreCase)
                        && !f.StartsWith(models, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        // JSON parse and the load checks are CPU-bound, so they run in
        // parallel. The results are taken in path order.
        var read = new Parsed[files.Length];
        System.Threading.Tasks.Parallel.For(0, files.Length, i =>
        {
            string text;
            try { text = File.ReadAllText(files[i]); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                read[i] = new Parsed(files[i], null, new HIDMaestro.HMProfileLoadIssue(
                    files[i], null, "$", $"the file cannot be read: {e.Message}", HIDMaestro.HMProfileLoadIssueKind.Refused));
                return;
            }
            read[i] = Parse(files[i], text);
        });
        var db = Assemble(read, modelIssues);
        db.ResolveAll(registry, outsideCatalog: true);
        return db;
    }

    /// <summary>Files already read, each a label and its text, loaded as
    /// <see cref="Load(string, ModelRegistry)"/> loads a directory's: read
    /// strictly, checked, and resolved under <paramref name="registry"/>.</summary>
    internal static ProfileDatabase LoadTexts(IReadOnlyList<(string File, string Text)> files, ModelRegistry registry)
    {
        var read = new Parsed[files.Count];
        for (int i = 0; i < files.Count; i++) read[i] = Parse(files[i].File, files[i].Text);
        var db = Assemble(read);
        db.ResolveAll(registry, outsideCatalog: true);
        return db;
    }

    /// <summary>Resolves every profile's device model under a registry,
    /// and lists each model a profile named or took by identity that could
    /// not be taken. A profile whose own keys the resolution refuses was
    /// refused already by its load checks.</summary>
    private void ResolveAll(ModelRegistry registry, bool outsideCatalog)
    {
        Registry = registry;
        var refused = new List<ControllerProfile>();
        // Each profile resolves on its own, in parallel: the registry is
        // read-only here. The issues are listed in the profiles' order.
        var failed = new Exception?[_profiles.Count];
        System.Threading.Tasks.Parallel.For(0, _profiles.Count, i =>
        {
            var q = _profiles[i];
            q.Registry = registry;
            q.OutsideCatalog = outsideCatalog;
            try { q.ResolvedCache = ModelRegistry.Resolve(q, registry, outsideCatalog); }
            catch (Exception e) { failed[i] = e; }
        });
        for (int i = 0; i < _profiles.Count; i++)
        {
            var p = _profiles[i];
            if (failed[i] is Exception refusal)
            {
                // Whatever a check throws refuses this profile alone.
                var (path, reason) = ProfileDefects.LocateAny(refusal);
                if (_texts.TryGetValue(p, out var text)) path = ProfileDefects.Spell(text, path);
                _issues.Add(new HIDMaestro.HMProfileLoadIssue(p.SourceFile ?? p.Id, p.Id, path, reason,
                    HIDMaestro.HMProfileLoadIssueKind.Refused));
                refused.Add(p);
                continue;
            }
            var r = p.ResolvedCache!;
            if (r.Problem != null)
            {
                // The value that went wrong where its file holds it: the
                // profile's own deviceModelFrom, or the key of a model file.
                string path = r.ProblemPath;
                if (r.ProblemFile == null && _texts.TryGetValue(p, out var own)) path = ProfileDefects.Spell(own, path);
                else if (r.ProblemFile != null && File.Exists(r.ProblemFile))
                {
                    try { path = ProfileDefects.Spell(File.ReadAllText(r.ProblemFile), path); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // The model file is gone or locked since it loaded: the
                        // path keeps the schema's spelling, which names the same key.
                    }
                }
                _issues.Add(new HIDMaestro.HMProfileLoadIssue(r.ProblemFile ?? p.SourceFile ?? p.Id, p.Id, path,
                    r.Problem, HIDMaestro.HMProfileLoadIssueKind.ModelProblem));
            }
        }
        foreach (var p in refused) _profiles.Remove(p);
        _texts.Clear();
        // The checks compiled each profile. A program can still edit it
        // before its first create, which compiles it again.
        foreach (var p in _profiles) p.ForgetCompiled();
    }

    /// <summary>One file's result: the profile, or the issue that refused
    /// it.</summary>
    internal readonly record struct Parsed(string File, ControllerProfile? Profile, HIDMaestro.HMProfileLoadIssue? Issue, string? Text = null);

    /// <summary>Each loaded profile's file text, until this load has
    /// resolved them, so a refusal at resolve names the path as the file
    /// spells it.</summary>
    private readonly Dictionary<ControllerProfile, string> _texts = new(ReferenceEqualityComparer.Instance);

    /// <summary>Reads one profile file strictly and runs the load checks.</summary>
    internal static Parsed Parse(string file, string text)
    {
        ControllerProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize(text, HMJsonContext.Default.ControllerProfile);
        }
        catch (Exception e)
        {
            // A JSON file with no id is not a profile, whatever else it
            // carries: the catalog's folder keeps descriptor collections
            // beside the profiles. Anything a converter throws refuses this
            // file alone.
            var (id, isObject) = PeekId(text);
            if (isObject && id == null)
                return new Parsed(file, null, NotAProfile(file));
            var (path, reason) = ProfileDefects.LocateAny(e);
            return new Parsed(file, null, new HIDMaestro.HMProfileLoadIssue(
                file, id, ProfileDefects.Spell(text, path), reason, HIDMaestro.HMProfileLoadIssueKind.Refused));
        }
        if (profile == null || string.IsNullOrEmpty(profile.Id))
            return new Parsed(file, null, NotAProfile(file));
        profile.SourceFile = file;
        try
        {
            profile.ValidateAtLoad();
        }
        catch (Exception e)
        {
            // A check that fails on this file's content, in whatever way,
            // refuses this file and never the load.
            var (path, reason) = ProfileDefects.LocateAny(e);
            return new Parsed(file, null, new HIDMaestro.HMProfileLoadIssue(
                file, profile.Id, ProfileDefects.Spell(text, path), reason, HIDMaestro.HMProfileLoadIssueKind.Refused));
        }
        return new Parsed(file, profile, null, text);
    }

    private static HIDMaestro.HMProfileLoadIssue NotAProfile(string file) =>
        new(file, null, "$.id", "the file has no id, so it is not a profile", HIDMaestro.HMProfileLoadIssueKind.NotAProfile);

    /// <summary>The id of a file that did not deserialize, when it is
    /// plain JSON with a string id, and whether it is a JSON object at
    /// all.</summary>
    private static (string? Id, bool IsObject) PeekId(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, false);
            // The reader matches keys without regard to case, so "ID" is the
            // id here too.
            foreach (var p in doc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, "id", StringComparison.OrdinalIgnoreCase))
                    return (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } s ? s : null, true);
            return (null, true);
        }
        catch (JsonException) { return (null, false); }
    }

    /// <summary>The profiles and issues of files read in path order. A
    /// second file with an id the first already took is refused.</summary>
    private static ProfileDatabase Assemble(Parsed[] read, List<HIDMaestro.HMProfileLoadIssue>? priorIssues = null)
    {
        var db = new ProfileDatabase();
        if (priorIssues != null) db._issues.AddRange(priorIssues);
        var first = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in read)
        {
            if (r.Issue != null) { db._issues.Add(r.Issue); continue; }
            var p = r.Profile!;
            if (first.TryGetValue(p.Id, out var earlier))
            {
                db._issues.Add(new HIDMaestro.HMProfileLoadIssue(r.File, p.Id, "$.id",
                    $"the id '{p.Id}' is already loaded from {earlier}. Give the profile an id of its own.",
                    HIDMaestro.HMProfileLoadIssueKind.Refused));
                continue;
            }
            first[p.Id] = r.File;
            db._profiles.Add(p);
            if (r.Text != null) db._texts[p] = r.Text;
        }
        db._profiles.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
        return db;
    }

    /// <summary>Loads every profile JSON embedded in the HIDMaestro.Core
    /// assembly under the logical-name prefix "HIDMaestro.Profiles.". This
    /// is the no-disk path used by HMContext.LoadDefaultProfiles(): the
    /// SDK ships with the entire profile catalog baked in so consumers
    /// don't need to ship a sibling profiles/ directory.</summary>
    // v1.3.0: process-wide cache. Embedded JSONs are static for the
    // lifetime of the process; reparsing on every HMContext.LoadDefaultProfiles
    // is wasted work. First call populates the cache (parallel parse);
    // subsequent calls return the same instance. Multiple HMContexts share.
    private static ProfileDatabase? s_cachedEmbedded;
    private static readonly object s_cachedEmbeddedLock = new();

    /// <summary>The catalog profile with this id, the catalog loaded if it
    /// is not yet. Null for an id the catalog lacks. Never called by the
    /// catalog's own load, which resolves over the profiles it read.</summary>
    internal static ControllerProfile? EmbeddedProfile(string id)
    {
        // The catalog's own load resolves over the profiles it read. A
        // lookup from inside it would load the catalog again on this thread,
        // or wait on the load's lock from one of its parallel workers.
        if (Volatile.Read(ref s_loadingEmbedded) && s_cachedEmbedded == null)
            throw new InvalidOperationException(
                $"Profile '{id}' was looked up in the catalog while the catalog loads. A check that runs at load reads the profile's own keys, not the catalog's registry.");
        var db = LoadEmbedded();
        return db._byId.TryGetValue(id, out var p) ? p : null;
    }

    private readonly Dictionary<string, ControllerProfile> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while the embedded catalog loads.</summary>
    private static bool s_loadingEmbedded;

    public static ProfileDatabase LoadEmbedded()
    {
        if (s_cachedEmbedded != null) return s_cachedEmbedded;
        lock (s_cachedEmbeddedLock)
        {
            if (s_cachedEmbedded != null) return s_cachedEmbedded;
            Volatile.Write(ref s_loadingEmbedded, true);
            try { s_cachedEmbedded = LoadEmbeddedFresh(); }
            finally { Volatile.Write(ref s_loadingEmbedded, false); }
            return s_cachedEmbedded;
        }
    }

    private static ProfileDatabase LoadEmbeddedFresh()
    {
        var asm = typeof(ProfileDatabase).Assembly;
        const string prefix = "HIDMaestro.Profiles.";

        // Collect resource names first so we can parse in parallel. The
        // serial JSON parse over the catalog is the dominant fresh-launch
        // cost in HMContext init: Parallel.For across 4 to 16 cores drops
        // it from 200 to 500 ms cold to 50 to 150 ms.
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)
                     && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                     && !n.Equals(prefix + "schema.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var read = new Parsed[names.Length];
        System.Threading.Tasks.Parallel.For(0, names.Length, i =>
        {
            string label = CatalogLabel + names[i].Substring(prefix.Length).Replace('\\', '/');
            using var s = asm.GetManifestResourceStream(names[i]);
            if (s == null)
            {
                read[i] = new Parsed(label, null, new HIDMaestro.HMProfileLoadIssue(
                    label, null, "$", "the embedded resource cannot be opened", HIDMaestro.HMProfileLoadIssueKind.Refused));
                return;
            }
            using var reader = new StreamReader(s);
            read[i] = Parse(label, reader.ReadToEnd());
        });
        var db = Assemble(read);
        // The catalog's own registry, over the profiles just read: a catalog
        // profile names a model the SDK carries or another catalog profile.
        db.ResolveAll(ModelRegistry.ForCatalog(db._profiles), outsideCatalog: false);
        foreach (var p in db._profiles) db._byId[p.Id] = p;
        return db;
    }
}
