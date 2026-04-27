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
/// <para>Example — clone an existing profile with a different PID:</para>
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
/// <para>Example — build a completely custom controller:</para>
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
    private ushort _vid;
    private ushort _pid;
    private string _productString = "HID-compliant game controller";
    private string? _manufacturerString;
    private string _type = "gamepad";
    private string _connection = "usb";
    private string? _driverMode;
    private string? _triggerMode;
    private string? _descriptorHex;
    private int? _inputReportSize;
    private string? _deviceDescription;
    private string? _notes;
    private int[]? _buttonMap;
    private int[]? _triggerButtons;
    private Dictionary<string, string>? _axisMap;

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

    /// <summary>Set the HID report descriptor from raw bytes.</summary>
    public HMProfileBuilder Descriptor(byte[] bytes)
    {
        _descriptorHex = Convert.ToHexString(bytes).ToLowerInvariant();
        return this;
    }

    /// <summary>Set the HID report descriptor from a hex string (same format as profile JSON).</summary>
    public HMProfileBuilder DescriptorHex(string hex)
    {
        _descriptorHex = hex;
        return this;
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
    /// several iterations of issue #16 — the kernel sized the input
    /// buffer wrong when the +1 was missing, HidClass preparsed data
    /// was misaligned, and pid.dll resolved Feature reports to Report
    /// ID 0 instead of the declared values).</summary>
    public HMProfileBuilder FromDescriptorBuilder(HidDescriptorBuilder builder)
    {
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

    /// <summary>Initialize all fields from an existing profile. Call individual
    /// setters afterward to override specific properties.</summary>
    public HMProfileBuilder FromProfile(HMProfile source)
    {
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
        _deviceDescription = source.Inner.DeviceDescription;
        _notes = source.Notes;
        _buttonMap = source.ButtonMap;
        _triggerButtons = source.Inner.TriggerButtons;
        _axisMap = source.AxisMap;
        return this;
    }

    /// <summary>Build the <see cref="HMProfile"/>. The returned profile can be
    /// passed directly to <see cref="HMContext.CreateController(HMProfile)"/>.</summary>
    public HMProfile Build()
    {
        if (_vid == 0) throw new InvalidOperationException("VID must be set (use .Vid(0x045E)).");
        if (_pid == 0) throw new InvalidOperationException("PID must be set (use .Pid(0x028E)).");

        var inner = new ControllerProfile
        {
            Id = _id,
            Name = _name,
            Vendor = _vendor,
            Vid = $"0x{_vid:X4}",
            Pid = $"0x{_pid:X4}",
            ProductString = _productString,
            ManufacturerString = _manufacturerString,
            Type = _type,
            Connection = _connection,
            DriverMode = _driverMode,
            TriggerMode = _triggerMode,
            Descriptor = _descriptorHex,
            InputReportSize = _inputReportSize,
            DeviceDescription = _deviceDescription,
            Notes = _notes,
            ButtonMap = _buttonMap,
            TriggerButtons = _triggerButtons,
            AxisMap = _axisMap,
        };

        return new HMProfile(inner);
    }
}
