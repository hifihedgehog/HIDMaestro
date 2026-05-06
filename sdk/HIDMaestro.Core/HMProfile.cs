using System;
using System.Collections.Generic;
using HIDMaestro.Internal;

namespace HIDMaestro;

/// <summary>
/// A controller profile — the description of a real-world controller that a
/// virtual device can masquerade as. Profiles are immutable, identified by a
/// stable string ID slug like "xbox-360-wired" or "dualsense".
///
/// <para>Get built-in instances via <see cref="HMContext.GetProfile(string)"/>
/// or <see cref="HMContext.AllProfiles"/>. Create custom profiles from scratch
/// via <see cref="HMProfileBuilder"/>.</para>
///
/// <para>All profile characteristics — VID/PID, descriptor bytes, axis layout,
/// button count, connection type — are publicly accessible for inspection and
/// for building modified variants.</para>
/// </summary>
public sealed class HMProfile
{
    internal ControllerProfile Inner { get; }

    internal HMProfile(ControllerProfile inner) { Inner = inner; }

    // ── Identity ─────────────────────────────────────────────────────────

    /// <summary>Stable identifier slug, e.g. "xbox-series-xs-bt".</summary>
    public string Id => Inner.Id;

    /// <summary>Human-readable name shown in UIs, e.g. "Xbox Series X|S Controller (Bluetooth)".</summary>
    public string Name => Inner.Name;

    /// <summary>Vendor name, e.g. "Microsoft", "Sony", "Logitech".</summary>
    public string Vendor => Inner.Vendor;

    /// <summary>USB Vendor ID as a 16-bit integer.</summary>
    public ushort VendorId => Inner.VendorId;

    /// <summary>USB Product ID as a 16-bit integer.</summary>
    public ushort ProductId => Inner.ProductId;

    /// <summary>The product string the device reports to the OS, e.g. "Wireless Controller".</summary>
    public string ProductString => Inner.ProductString;

    /// <summary>The manufacturer string the device reports, e.g. "Microsoft".</summary>
    public string ManufacturerString => Inner.ManufacturerString ?? Inner.Vendor ?? "";

    /// <summary>Device Manager display name. Falls back to <see cref="ProductString"/>.</summary>
    public string DisplayName => Inner.DisplayName;

    /// <summary>Controller category — "gamepad", "wheel", "joystick", "arcade", etc.</summary>
    public string Type => Inner.Type;

    // ── Connection + driver characteristics ───────────────────────────────

    /// <summary>Connection type: "usb", "bluetooth", or "wireless-adapter".</summary>
    public string Connection => Inner.Connection ?? "usb";

    /// <summary>Driver mode: "xinputhid" for Xbox BT controllers that bind
    /// Microsoft's xinputhid.sys, or null for standard HID profiles.</summary>
    public string? DriverMode => Inner.DriverMode;

    /// <summary>Trigger axis layout: "combined" (Xbox 360 shared Z axis),
    /// "separate" (independent LT/RT), or null (non-gamepad).</summary>
    public string? TriggerMode => Inner.TriggerMode;

    // ── HID descriptor ───────────────────────────────────────────────────

    /// <summary>True if this profile has a HID descriptor and can be deployed
    /// as a virtual controller. Some catalog entries are placeholders.</summary>
    public bool IsDeployable => Inner.HasDescriptor;

    /// <summary>Input report size in bytes (including Report ID byte if any).
    /// Returns 0 if not specified in the profile.</summary>
    public int InputReportSize => Inner.InputReportSize ?? 0;

    /// <summary>The raw HID report descriptor bytes. Returns a copy — modifying
    /// the returned array does not affect the profile. Returns null if the
    /// profile has no descriptor (not deployable).</summary>
    public byte[]? GetDescriptorBytes()
    {
        var src = Inner.GetDescriptorBytes();
        if (src == null) return null;
        var copy = new byte[src.Length];
        Array.Copy(src, copy, src.Length);
        return copy;
    }

    /// <summary>The HID report descriptor as a hex string (same format as the
    /// profile JSON's "descriptor" field). Null if no descriptor.</summary>
    public string? DescriptorHex => Inner.Descriptor;

    // ── Parsed descriptor layout ─────────────────────────────────────────

    /// <summary>Number of buttons declared in the HID descriptor.</summary>
    public int ButtonCount => GetLayout()?.Buttons.Count ?? 0;

    /// <summary>Number of axes (sticks + triggers) declared in the descriptor.</summary>
    public int AxisCount
    {
        get
        {
            var l = GetLayout();
            if (l == null) return 0;
            int n = 0;
            if (l.LeftStickX != null) n++;
            if (l.LeftStickY != null) n++;
            if (l.RightStickX != null) n++;
            if (l.RightStickY != null) n++;
            if (l.LeftTrigger != null) n++;
            if (l.RightTrigger != null) n++;
            if (l.CombinedTrigger != null) n++;
            return n;
        }
    }

    /// <summary>True if the descriptor includes a hat switch (D-pad).</summary>
    public bool HasHat => GetLayout()?.HatSwitch != null;

    /// <summary>The descriptor's Hat Switch LogicalMin, or null if the profile
    /// has no hat usage. Use with <see cref="HMGamepadState.HatRaw"/> when
    /// you need bit-exact descriptor values.</summary>
    public int? HatLogicalMin => GetLayout()?.HatSwitch?.LogicalMin;

    /// <summary>The descriptor's Hat Switch LogicalMax, or null if the profile
    /// has no hat usage. Together with <see cref="HatLogicalMin"/>, the count
    /// of distinct hat positions is <c>HatLogicalMax - HatLogicalMin + 1</c>
    /// (typically 8 for octant hats, 16 for 22.5° hats, more for HOTAS).</summary>
    public int? HatLogicalMax => GetLayout()?.HatSwitch?.LogicalMax;

    /// <summary>Bit size of each stick axis (typically 8 or 16).</summary>
    public int StickBits => GetLayout()?.LeftStickX?.BitSize ?? 0;

    /// <summary>Bit size of each trigger axis (typically 8 or 10).</summary>
    public int TriggerBits => GetLayout()?.LeftTrigger?.BitSize ?? 0;

    /// <summary>Notes from the profile JSON (descriptor provenance, quirks, etc.).</summary>
    public string? Notes => Inner.Notes;

    /// <summary>Button remapping table. Maps HMButton bit positions (index) to
    /// descriptor button indices (value). Null means identity mapping (Xbox layout).
    /// Sony profiles remap so HMButton.A → Cross, HMButton.X → Square, etc.</summary>
    public int[]? ButtonMap => Inner.ButtonMap;

    /// <summary>Axis semantic override map. Keys are hex HID usage codes (e.g.
    /// "0x32" for Z), values are semantic names (leftStickX, rightStickY,
    /// leftTrigger, etc.). Sony profiles override Z/Rz→rightStick and
    /// Rx/Ry→triggers. Null means default heuristic mapping.</summary>
    public Dictionary<string, string>? AxisMap => Inner.AxisMap;

    /// <summary>v1.3.5 — vendor-blob input-report spec, or null. When set,
    /// HMController.SubmitState emits this report ID via the data-driven
    /// codec instead of the descriptor-based encoder. Profile-level metadata
    /// exposed for inspection by consumers and regression probes; field-level
    /// access goes through <c>Fields</c> on the spec.</summary>
    public ExtendedReportSpec? ExtendedReport => Inner.ExtendedReport;

    /// <summary>v1.3.5 — vendor-blob output-report spec, or null. When set,
    /// <see cref="HMController.OutputDecoded"/> surfaces parsed-field events
    /// for matching inbound report IDs and <see cref="HMOutputEncoder.Encode"/>
    /// can produce wire-format bytes from parsed-field dictionaries.</summary>
    public ExtendedReportSpec? ExtendedOutputReport => Inner.ExtendedOutputReport;

    /// <summary>True if the profile declares a vendor-blob input report
    /// (e.g. Sony BT Report 0x31).</summary>
    public bool HasExtendedInput => Inner.ExtendedReport != null;

    /// <summary>True if the profile declares a vendor-blob output report
    /// for parsed-field decoding.</summary>
    public bool HasExtendedOutput => Inner.ExtendedOutputReport != null;

    public override string ToString() => $"{Id} ({Name})";

    // Lazily parsed layout cache
    private HidReportBuilder? _layout;
    private HidReportBuilder? GetLayout()
    {
        if (_layout != null) return _layout;
        var bytes = Inner.GetDescriptorBytes();
        if (bytes == null) return null;
        _layout = HidReportBuilder.Parse(bytes);
        return _layout;
    }
}
