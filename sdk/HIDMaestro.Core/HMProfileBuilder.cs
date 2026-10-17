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
    private string? _driverMode;
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

    /// <summary>Set the profile ID slug (must be unique if registered with a context).</summary>
    public HMProfileBuilder Id(string id) { _id = id; return this; }

    /// <summary>Set the human-readable display name.</summary>
    public HMProfileBuilder Name(string name) { _name = name; return this; }

    /// <summary>Set the vendor name (for display only).</summary>
    public HMProfileBuilder Vendor(string vendor) { _vendor = vendor; return this; }

    /// <summary>Set the USB Vendor ID.</summary>
    public HMProfileBuilder Vid(ushort vid) { _vid = vid; return this; }

    /// <summary>Set the USB Product ID.</summary>
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

    /// <summary>Set the driver mode: "xinputhid" for Xbox BT controllers, or null for standard HID.</summary>
    public HMProfileBuilder DriverMode(string? mode) { _driverMode = mode; return this; }

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
    /// of it), and <c>usbConfiguration</c> with <c>backend</c>, whose HID class
    /// descriptor declares its length and which the usbip backend serves.
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

    private void SetDescriptor(string hex)
    {
        if (_descriptorHex != null && !SameBytes(_descriptorHex, hex)) DropReportFormat();
        _descriptorHex = hex;
    }

    private static bool SameBytes(string a, string b)
    {
        try { return Convert.FromHexString(a).AsSpan().SequenceEqual(Convert.FromHexString(b)); }
        catch (FormatException) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
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
    /// takes the model the profile's identity gives it.</summary>
    public HMProfileBuilder DeviceModelFrom(string? name) { _deviceModelFrom = name; return this; }

    /// <summary>The product ID the driver presents in the HID attributes,
    /// where an XInput presentation through xinputhid binds only on a PID
    /// xinputhid's INF lists (the presentation group of the device model).
    /// Hex, as the profile's <c>driverPid</c>. Null takes none.</summary>
    public HMProfileBuilder DriverPid(string? pid) { _driverPid = pid; return this; }

    /// <summary>The answers the device gives to GET_REPORT(Feature), the
    /// profile's <c>featureStubs</c> (the design note's mechanism 4). Null
    /// takes the answers of the model the profile names or its identity
    /// gives it.</summary>
    public HMProfileBuilder FeatureAnswers(FeatureStubSpec? answers) { _featureStubs = answers; return this; }

    /// <summary>Drops the device model keys tied to the descriptor's
    /// reports, the answers and their value sources, and the model the
    /// profile names or its "none", and keeps the presentation
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
        _driverMode = source.DriverMode;
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

        // A copy keeps every key of its source but the ones set here.
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
        inner.DriverMode = _driverMode;
        inner.TriggerMode = _triggerMode;
        inner.Descriptor = _descriptorHex;
        inner.InputReportSize = _inputReportSize;
        inner.DataReportId = _dataReportId;
        inner.DeviceDescription = _deviceDescription;
        inner.Notes = _notes;
        inner.ButtonMap = _buttonMap;
        inner.TriggerButtons = _triggerButtons;
        inner.AxisMap = _axisMap;
        inner.DeviceModelFrom = _deviceModelFrom;
        inner.DeviceModel = _deviceModel;
        inner.DriverPid = _driverPid;
        inner.FeatureStubs = _featureStubs;
        inner.ValueSources = _valueSources;
        // Read again from its JSON, so two profiles built from one builder,
        // and a builder's caller, share no object with the profile.
        inner = inner.Clone();
        inner.SourceFile = null;
        inner.BuiltInCode = true;

        try
        {
            inner.ValidateAtLoad();
            inner.ResolvedCache = ModelRegistry.Resolve(inner, ModelRegistry.Catalog, outsideCatalog: true);
        }
        catch (Exception e) when (ProfileDefects.IsDefect(e))
        {
            throw ProfileDefects.ToProfileException(inner, e);
        }
        return new HMProfile(inner);
    }
}
