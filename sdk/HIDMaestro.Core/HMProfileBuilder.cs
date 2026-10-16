using System;
using System.Collections.Generic;
using HIDMaestro.Internal;

namespace HIDMaestro;

/// <summary>
/// Fluent builder for creating custom <see cref="HMProfile"/> instances from
/// scratch. Use this to spoof controllers that aren't in the built-in catalog,
/// or to create modified variants of existing profiles with different
/// descriptors, button counts, or axis layouts.
///
/// <para>Example: clone an existing profile with a different PID:</para>
/// <code>
/// var existing = ctx.GetProfile("dualsense")!;
/// var custom = new HMProfileBuilder()
///     .FromProfile(existing)
///     .Id("dualsense-custom")
///     .Pid(0x1234)
///     .Build();
/// using var ctrl = ctx.CreateController(custom);
/// </code>
///
/// <para>Example: build a completely custom controller:</para>
/// <code>
/// var custom = new HMProfileBuilder()
///     .Id("my-joystick")
///     .Name("My Custom Joystick")
///     .Vid(0x1234).Pid(0x5678)
///     .ProductString("Custom Stick")
///     .Connection("usb")
///     .Descriptor(myDescriptorBytes)
///     .InputReportSize(18)
///     .Build();
/// </code>
/// </summary>
public sealed class HMProfileBuilder
{
    private string _id = "custom";
    private string _name = "Custom Controller";
    private string _vendor = "Custom";
    private ushort? _vid;
    private ushort? _pid;
    private string _productString = "HID-compliant game controller";
    private string? _manufacturerString;
    private string _type = "gamepad";
    private string _connection = "usb";
    private string? _bluetoothTransport;
    private string? _bluetoothVendorIdSource;
    private string? _driverMode;
    private XInputSpec? _xinput;
    private string? _triggerMode;
    private string? _descriptorHex;
    private int? _inputReportSize;
    private string? _dataReportId;
    private string? _deviceDescription;
    private string? _notes;
    private int[]? _buttonMap;
    private int[]? _triggerButtons;
    private Dictionary<string, string>? _axisMap;
    private string? _deviceModelFrom;
    private string? _deviceModel;
    private string? _driverPid;
    private FeatureStubSpec? _featureStubs;
    private Dictionary<string, ValueSourceSpec>? _valueSources;
    // FromProfile's copy of the whole source profile, read again from its
    // JSON. Build starts from it, so every key no setter names comes with
    // the copy: the extended reports, the layout, the USB configuration.
    private ControllerProfile? _copy;
    // The registry the copied profile resolved under, so a copy of a
    // directory's profile still finds the directory's models.
    private ModelRegistry? _registry;

    /// <summary>Set the profile ID slug (must be unique if registered with a context).</summary>
    public HMProfileBuilder Id(string id) { _id = id; return this; }

    /// <summary>Set the human-readable display name.</summary>
    public HMProfileBuilder Name(string name) { _name = name; return this; }

    /// <summary>Set the vendor name (for display only).</summary>
    public HMProfileBuilder Vendor(string vendor) { _vendor = vendor; return this; }

    /// <summary>Set the USB Vendor ID. On a copy of a USB/IP persona,
    /// <see cref="Build"/> writes it into the persona's USB device
    /// descriptor too.</summary>
    public HMProfileBuilder Vid(ushort vid) { _vid = vid; return this; }

    /// <summary>Set the USB Product ID. On a copy of a USB/IP persona,
    /// <see cref="Build"/> writes it into the persona's USB device
    /// descriptor too.</summary>
    public HMProfileBuilder Pid(ushort pid) { _pid = pid; return this; }

    /// <summary>Set the product string reported to the OS.</summary>
    public HMProfileBuilder ProductString(string s) { _productString = s; return this; }

    /// <summary>Set the manufacturer string reported to the OS.</summary>
    public HMProfileBuilder ManufacturerString(string s) { _manufacturerString = s; return this; }

    /// <summary>Set the controller type: "gamepad", "wheel", "joystick", "hotas",
    /// "flightstick", "pedals", "arcadestick", "other".</summary>
    public HMProfileBuilder Type(string type) { _type = type; return this; }

    /// <summary>Set the connection type: "usb", "bluetooth", or "wireless-adapter".</summary>
    public HMProfileBuilder Connection(string conn) { _connection = conn; return this; }

    /// <summary>For a Bluetooth profile, the transport a real device of its
    /// kind uses, "classic" or "le", which picks the hardware and compatible
    /// IDs its devnode carries. Null means classic.</summary>
    public HMProfileBuilder BluetoothTransport(string? transport) { _bluetoothTransport = transport; return this; }

    /// <summary>For a Bluetooth profile, who assigned its VID: "usb" (the
    /// USB-IF, the default) or "bluetooth" (the Bluetooth SIG).</summary>
    public HMProfileBuilder BluetoothVendorIdSource(string? source) { _bluetoothVendorIdSource = source; return this; }

    /// <summary>Set the driver mode: "xinputhid" for Xbox BT controllers, or null for standard HID.</summary>
    public HMProfileBuilder DriverMode(string? mode) { _driverMode = mode; return this; }

    /// <summary>The XInput presentation: the JSON of the profile's
    /// <c>xinput</c> block (the design note's mechanism 6), read as a
    /// profile file's is. <c>{"via":"xusbCompanion"}</c> gives an XUSB
    /// companion beside the HID device, <c>{"via":"xinputhid"}</c>
    /// Microsoft's xinputhid, <c>{}</c> no XInput. Null takes the
    /// presentation from <see cref="DriverMode"/>, the model the profile
    /// names or its identity default (a Microsoft VID with "xbox" in the
    /// id, name or product string gets the companion).</summary>
    /// <exception cref="HMProfileException">The JSON does not read as an
    /// <c>xinput</c> block, at its path under <c>$.xinput</c>.</exception>
    public HMProfileBuilder XInput(string? json)
    {
        if (json == null) { _xinput = null; return this; }
        try
        {
            _xinput = System.Text.Json.JsonSerializer.Deserialize(json, HMJsonContext.Default.XInputSpec)
                ?? throw new HMProfileException(_id, null, "$.xinput", "is null. Pass null to take the presentation the profile's model gives.");
        }
        catch (System.Text.Json.JsonException e)
        {
            string inner = string.IsNullOrEmpty(e.Path) || e.Path == "$" ? "" : e.Path[1..];
            throw new HMProfileException(_id, null, "$.xinput" + inner, ProfileDefects.Locate(e).Reason, e);
        }
        return this;
    }

    /// <summary>Set the trigger mode: "combined", "separate", or null.</summary>
    public HMProfileBuilder TriggerMode(string? mode) { _triggerMode = mode; return this; }

    /// <summary>Set the Device Manager display name (falls back to ProductString if null).</summary>
    public HMProfileBuilder DeviceDescription(string? desc) { _deviceDescription = desc; return this; }

    /// <summary>Set the HID report descriptor from raw bytes.
    /// <para>Replacing a descriptor the builder already holds (from
    /// <see cref="FromProfile"/> or an earlier call) with different bytes
    /// drops every key the old descriptor's report format governs, since
    /// each one describes that descriptor's reports and fields:
    /// <c>inputReportSize</c> and <c>dataReportId</c> (the length and ID of
    /// the input report a frame fills), <c>layout</c> (the axes and buttons
    /// the descriptor declares), <c>buttonMap</c>, <c>triggerButtons</c>,
    /// <c>axisMap</c> and <c>triggerMode</c> (indices, usages and the trigger
    /// encoding of its fields), <c>axisCenters</c> (the wire value of its
    /// axes at rest), <c>inputDefaults</c> (byte offsets in its input
    /// report), <c>sdlMapping</c> (SDL's button and axis indices for it),
    /// <c>extendedReport</c> and <c>extendedOutputReport</c> (its vendor
    /// reports), <c>sixaxisCompatible</c> (the DualShock 3 reports it
    /// carries), <c>featureStubs</c> and <c>valueSources</c> (answers for
    /// its feature reports), <c>nativeDescriptor</c> (the real device's copy
    /// of it), <c>usbConfiguration</c> with <c>backend</c>, whose HID class
    /// descriptor declares its length and which the usbip backend serves,
    /// and <c>protocol</c>, <c>stateBlock</c>, <c>inputReports</c> and
    /// <c>liveFeatureReports</c> (its reports' contents and timing).
    /// The identity, the presentation (<c>driverMode</c>, <c>driverPid</c>,
    /// <c>versionNumber</c>, <c>companionOnly</c>) and the model the profile
    /// names stay. Set any dropped key again after the descriptor. The same
    /// bytes drop nothing, and a builder's first descriptor drops nothing
    /// set before it.</para>
    /// </summary>
    public HMProfileBuilder Descriptor(byte[] bytes)
    {
        SetDescriptor(Convert.ToHexString(bytes).ToLowerInvariant());
        return this;
    }

    /// <summary>Set the HID report descriptor from a hex string (same format
    /// as profile JSON). Replacing one drops the keys its report format
    /// governs, as <see cref="Descriptor(byte[])"/> lists.</summary>
    public HMProfileBuilder DescriptorHex(string hex)
    {
        SetDescriptor(hex);
        return this;
    }

    private void SetDescriptor(string? hex)
    {
        // Null clears the descriptor, as before v2.0.0, and a cleared
        // descriptor governs no report format either.
        if (_descriptorHex != null && (hex == null || !SameBytes(_descriptorHex, hex))) DropReportFormat();
        _descriptorHex = hex;
    }

    /// <summary>Two hex strings that give the same bytes, read as
    /// ControllerProfile.GetDescriptorBytes reads them: spaces and dashes
    /// between digits ignored, either case.</summary>
    private static bool SameBytes(string a, string b)
    {
        static string Bare(string h) => h.Replace(" ", "").Replace("-", "");
        try { return Convert.FromHexString(Bare(a)).AsSpan().SequenceEqual(Convert.FromHexString(Bare(b))); }
        catch (FormatException) { return string.Equals(Bare(a), Bare(b), StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>The keys <see cref="Descriptor(byte[])"/> lists, dropped
    /// from the builder and from the copy it holds.</summary>
    private void DropReportFormat()
    {
        _inputReportSize = null;
        _dataReportId = null;
        _buttonMap = null;
        _triggerButtons = null;
        _axisMap = null;
        _triggerMode = null;
        _featureStubs = null;
        _valueSources = null;
        if (_copy == null) return;
        _copy.Layout = null;
        _copy.AxisCenters = null;
        _copy.InputDefaults = null;
        _copy.SdlMapping = null;
        _copy.ExtendedReport = null;
        _copy.ExtendedOutputReport = null;
        _copy.SixaxisCompatible = null;
        _copy.NativeDescriptor = null;
        _copy.UsbConfiguration = null;
        _copy.Backend = null;
        DropModelGroups(_copy);
    }

    /// <summary>The model groups a profile's reports are built and answered
    /// by: the protocol, the state block and the input and live feature
    /// reports.</summary>
    private static void DropModelGroups(ControllerProfile p)
    {
        p.Protocol = null;
        p.StateBlock = null;
        p.StateBlockFields = null;
        p.InputReports = null;
        p.LiveFeatureReports = null;
    }

    /// <summary>Set the input report size in bytes.</summary>
    public HMProfileBuilder InputReportSize(int size) { _inputReportSize = size; return this; }

    /// <summary>Take both the HID descriptor bytes and the input report
    /// wire size from a fluent <see cref="HidDescriptorBuilder"/>. The
    /// wire size is derived as <c>InputReportByteSize + 1</c> when the
    /// descriptor carries a HID Report ID Global item (auto-injected by
    /// <see cref="HidDescriptorBuilder.AddPidFfbBlock"/>, or emitted
    /// manually via <see cref="HidDescriptorBuilder.AddRaw"/>), and
    /// <c>InputReportByteSize</c> otherwise. Replaces the
    /// <c>.Descriptor(b.Build()).InputReportSize(b.InputReportByteSize + 1)</c>
    /// pair that's easy to get wrong (PadForge tracked this down across
    /// several iterations of issue #16: the kernel sized the input
    /// buffer wrong when the +1 was missing, HidClass preparsed data
    /// was misaligned, and pid.dll resolved Feature reports to Report
    /// ID 0 instead of the declared values).</summary>
    public HMProfileBuilder FromDescriptorBuilder(HidDescriptorBuilder builder)
    {
        // Replacing a descriptor drops the keys its report format governs
        // (Descriptor), the input size among them, which is set below.
        Descriptor(builder.Build());
        bool hasRid = builder.DescriptorContainsReportId();
        _inputReportSize = builder.InputReportByteSize + (hasRid ? 1 : 0);
        return this;
    }

    /// <summary>Set notes/comments (for documentation only, not functional).</summary>
    public HMProfileBuilder Notes(string? notes) { _notes = notes; return this; }

    /// <summary>Set button remapping table. Maps HMButton bit positions (index)
    /// to descriptor button indices (value). Pass null for identity mapping (Xbox).
    /// Sony DS4/DualSense use: [1, 2, 0, 3, 4, 5, 8, 9, 10, 11, 12, 13].</summary>
    public HMProfileBuilder ButtonMap(int[]? map) { _buttonMap = map; return this; }

    /// <summary>Set trigger-to-button derivation. Array of two descriptor button
    /// indices: [LT_button, RT_button]. When triggers are nonzero, the corresponding
    /// buttons engage automatically (DS4/DualSense L2/R2 digital buttons).</summary>
    public HMProfileBuilder TriggerButtons(int[]? map) { _triggerButtons = map; return this; }

    /// <summary>Set axis semantic override map. Keys are hex HID usage codes
    /// (e.g. "0x32"), values are semantic names (leftStickX, rightStickY, etc.).
    /// Sony: { "0x32": "rightStickX", "0x35": "rightStickY", "0x33": "leftTrigger", "0x34": "rightTrigger" }</summary>
    public HMProfileBuilder AxisMap(Dictionary<string, string>? map) { _axisMap = map; return this; }

    /// <summary>Name the device model the profile takes for the groups its
    /// own keys leave empty: a named model a loaded models folder holds, or
    /// a catalog profile (<see cref="HMProfile.DeviceModelSource"/>). Null
    /// takes the model the profile's identity gives it. A name drops an
    /// earlier <see cref="DeviceModel"/> "none".</summary>
    public HMProfileBuilder DeviceModelFrom(string? name)
    {
        _deviceModelFrom = name;
        if (name != null) _deviceModel = null;
        return this;
    }

    /// <summary>The profile's <c>deviceModel</c>: "none" takes no model by
    /// name or by identity, so a profile built in code with a Sony or
    /// Nintendo identity gets no answers it did not set itself. It drops
    /// a name <see cref="DeviceModelFrom"/> set. Null takes what the name
    /// or the identity gives. Build refuses any other value at
    /// <c>$.deviceModel</c>.</summary>
    public HMProfileBuilder DeviceModel(string? value)
    {
        _deviceModel = value;
        if (value != null) _deviceModelFrom = null;
        return this;
    }

    /// <summary>The product ID the driver presents in the HID attributes,
    /// where an XInput presentation through xinputhid binds only on a PID
    /// xinputhid's INF lists (the presentation group of the device model).
    /// Hex, as the profile's <c>driverPid</c>. Null takes none.</summary>
    public HMProfileBuilder DriverPid(string? pid) { _driverPid = pid; return this; }

    /// <summary>The answers the device gives to GET_REPORT(Feature): the
    /// JSON of the profile's <c>featureStubs</c> key (the design note's
    /// mechanism 4), read as a profile file's is. Null takes the answers
    /// of the model the profile names or its identity gives it.</summary>
    /// <exception cref="HMProfileException">The JSON does not read as an
    /// answer table, at its path under <c>$.featureStubs</c>.</exception>
    public HMProfileBuilder FeatureAnswers(string? json)
    {
        if (json == null) { _featureStubs = null; return this; }
        try
        {
            _featureStubs = System.Text.Json.JsonSerializer.Deserialize(json, HMJsonContext.Default.FeatureStubSpec)
                ?? throw new HMProfileException(_id, null, "$.featureStubs", "is null. Pass null to take the model's answers.");
        }
        catch (System.Text.Json.JsonException e)
        {
            string inner = string.IsNullOrEmpty(e.Path) || e.Path == "$" ? "" : e.Path[1..];
            throw new HMProfileException(_id, null, "$.featureStubs" + inner, ProfileDefects.Locate(e).Reason, e);
        }
        return this;
    }

    /// <summary>Drops the device model keys tied to the descriptor's
    /// reports, the answers and their value sources, the protocol, the
    /// state block and the input and live feature reports, and the model
    /// the profile names or its "none", and keeps the presentation
    /// (<c>driverPid</c>), which describes the device node rather than its
    /// reports. The model the identity the builder sets gives then applies,
    /// as for any profile made outside the catalog. Replacing the
    /// descriptor drops the answers already (<see cref="Descriptor(byte[])"/>),
    /// so call this to drop the named model too.</summary>
    public HMProfileBuilder ClearDeviceModel()
    {
        _featureStubs = null;
        _valueSources = null;
        _deviceModelFrom = null;
        _deviceModel = null;
        if (_copy != null) DropModelGroups(_copy);
        return this;
    }

    /// <summary>Initialize all fields from an existing profile: the whole
    /// profile, its own device model keys and the model it names included,
    /// and not what an identity default gave it, which the copy resolves
    /// again under its own identity. Call individual setters afterward to
    /// override specific properties. Before v2.0.0 the copy dropped the
    /// feature answers, <c>driverPid</c> and the extended reports.</summary>
    public HMProfileBuilder FromProfile(HMProfile source)
    {
        _copy = source.Inner.Clone();
        _registry = source.Inner.Registry;
        _deviceModelFrom = source.Inner.DeviceModelFrom;
        _deviceModel = source.Inner.DeviceModel;
        _driverPid = source.Inner.DriverPid;
        _featureStubs = _copy.FeatureStubs;
        _valueSources = _copy.ValueSources;
        _id = source.Id;
        _name = source.Name;
        _vendor = source.Vendor;
        _vid = source.VendorId;
        _pid = source.ProductId;
        _productString = source.ProductString;
        _manufacturerString = source.ManufacturerString;
        _type = source.Type;
        _connection = source.Connection;
        _bluetoothTransport = source.Inner.BluetoothTransport;
        _bluetoothVendorIdSource = source.Inner.BluetoothVendorIdSource;
        _driverMode = source.DriverMode;
        _xinput = source.Inner.XInput?.Clone();
        _triggerMode = source.TriggerMode;
        _descriptorHex = source.DescriptorHex;
        _inputReportSize = source.InputReportSize > 0 ? source.InputReportSize : null;
        _dataReportId = source.Inner.DataReportId;
        _deviceDescription = source.Inner.DeviceDescription;
        _notes = source.Notes;
        _buttonMap = source.ButtonMap;
        _triggerButtons = source.Inner.TriggerButtons;
        _axisMap = source.AxisMap;
        return this;
    }

    /// <summary>A USB/IP persona presents its IDs in its USB device
    /// descriptor as well, idVendor at byte 8 and idProduct at byte 10,
    /// little-endian (USB 2.0 section 9.6.1), and its load checks them
    /// against $.vid and $.pid. A copy given other IDs carries them there
    /// too. A descriptor that is not whole hex or is too short is left for
    /// that check to refuse.</summary>
    private static void StampDeviceDescriptor(ControllerProfile p, ushort vid, ushort pid)
    {
        if (p.UsbConfiguration?.DeviceDescriptorHex is not { } hex) return;
        byte[] d;
        try { d = Convert.FromHexString(hex); }
        catch (FormatException) { return; }
        if (d.Length < 12) return;
        if (d[8] == (byte)vid && d[9] == (byte)(vid >> 8) && d[10] == (byte)pid && d[11] == (byte)(pid >> 8)) return;
        d[8] = (byte)vid; d[9] = (byte)(vid >> 8);
        d[10] = (byte)pid; d[11] = (byte)(pid >> 8);
        p.UsbConfiguration.DeviceDescriptorHex = Convert.ToHexString(d).ToLowerInvariant();
    }

    /// <summary>A value read again from its JSON, so the profile and the
    /// builder share no object.</summary>
    private static T CopyOf<T>(T value) where T : class
    {
        var info = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)HMJsonContext.Default.GetTypeInfo(typeof(T))!;
        return System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, info), info)!;
    }

    /// <summary>Build the <see cref="HMProfile"/>. The returned profile can be
    /// passed directly to <see cref="HMContext.CreateController(HMProfile)"/>.
    /// The profile meets the checks a profile file meets when it loads.</summary>
    /// <exception cref="HMProfileException">VID or PID is not set, the
    /// descriptor's hex does not parse, the layout names an axis or button
    /// the descriptor lacks, or the device model does not compile.</exception>
    public HMProfile Build()
    {
        // A copy carries its source's IDs, 0x0000 included, as moza-r16's PID
        // is in the catalog. Only a builder no ID was given refuses.
        if (_vid == null) throw new HMProfileException(_id, null, "$.vid", "VID must be set (use .Vid(0x045E)).");
        if (_pid == null) throw new HMProfileException(_id, null, "$.pid", "PID must be set (use .Pid(0x028E)).");

        // A copy keeps every key of its source but the ones set here, read
        // again from the source's JSON so two builds share no object.
        var inner = _copy?.Clone() ?? new ControllerProfile();
        inner.Id = _id;
        inner.Name = _name;
        inner.Vendor = _vendor;
        inner.Vid = $"0x{_vid:X4}";
        inner.Pid = $"0x{_pid:X4}";
        inner.ProductString = _productString;
        inner.ManufacturerString = _manufacturerString;
        inner.Type = _type;
        inner.Connection = _connection;
        inner.BluetoothTransport = _bluetoothTransport;
        inner.BluetoothVendorIdSource = _bluetoothVendorIdSource;
        inner.DriverMode = _driverMode;
        inner.XInput = _xinput?.Clone();
        inner.TriggerMode = _triggerMode;
        inner.Descriptor = _descriptorHex;
        inner.InputReportSize = _inputReportSize;
        inner.DataReportId = _dataReportId;
        inner.DeviceDescription = _deviceDescription;
        inner.Notes = _notes;
        // The values this builder holds are copied, so a caller that edits
        // the array or map it passed, or builds again, shares nothing with
        // this profile. A JSON round trip of the whole profile did this and
        // cost a program's first Build about 70 ms of serializer start-up.
        inner.ButtonMap = (int[]?)_buttonMap?.Clone();
        inner.TriggerButtons = (int[]?)_triggerButtons?.Clone();
        inner.AxisMap = _axisMap == null ? null : new Dictionary<string, string>(_axisMap, _axisMap.Comparer);
        inner.DeviceModelFrom = _deviceModelFrom;
        inner.DeviceModel = _deviceModel;
        inner.DriverPid = _driverPid;
        inner.FeatureStubs = _featureStubs == null ? null : CopyOf(_featureStubs);
        inner.ValueSources = _valueSources == null ? null : CopyOf(_valueSources);
        inner.SourceFile = null;
        inner.MarkBuiltInCode(true);
        StampDeviceDescriptor(inner, _vid.Value, _pid.Value);

        try
        {
            inner.ValidateAtLoad();
            inner.Registry = _registry ?? ModelRegistry.Catalog;
            inner.OutsideCatalog = true;
            inner.ResolvedCache = ModelRegistry.Resolve(inner, inner.Registry, outsideCatalog: true);
            // As a load does: the profile's public objects stay editable
            // until its first create, which compiles them.
            inner.ForgetCompiled();
        }
        catch (Exception e) when (ProfileDefects.IsDefect(e))
        {
            throw ProfileDefects.ToProfileException(inner, e);
        }
        return new HMProfile(inner);
    }
}
