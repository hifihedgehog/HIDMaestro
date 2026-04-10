using System;
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

    /// <summary>Bit size of each stick axis (typically 8 or 16).</summary>
    public int StickBits => GetLayout()?.LeftStickX?.BitSize ?? 0;

    /// <summary>Bit size of each trigger axis (typically 8 or 10).</summary>
    public int TriggerBits => GetLayout()?.LeftTrigger?.BitSize ?? 0;

    /// <summary>Notes from the profile JSON (descriptor provenance, quirks, etc.).</summary>
    public string? Notes => Inner.Notes;

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
