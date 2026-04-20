namespace HIDMaestro;

/// <summary>Lightweight description of a currently-connected HID device,
/// returned by <see cref="HMDeviceExtractor.ListDevices"/>. Opaque fields
/// (device path, reconstructed descriptor bytes) are deliberately not
/// exposed here; use <see cref="HMDeviceExtractor.Extract"/> to produce
/// a full <see cref="HMProfile"/>.</summary>
public sealed class HMHidDeviceInfo
{
    /// <summary>USB Vendor ID as reported by the device's HID attributes.</summary>
    public ushort VendorId { get; }

    /// <summary>USB Product ID.</summary>
    public ushort ProductId { get; }

    /// <summary>Device release/version (HID bcdDevice).</summary>
    public ushort VersionNumber { get; }

    /// <summary>iProduct string returned by <c>HidD_GetProductString</c>.
    /// This is what native DirectInput, WebHID, and Chrome's raw Gamepad
    /// API display. Null if the device didn't return one.</summary>
    public string? ProductString { get; }

    /// <summary>iManufacturer string. Null if the device didn't return one.</summary>
    public string? ManufacturerString { get; }

    /// <summary>iSerialNumber string. Null if the device didn't return one
    /// (common for most game controllers).</summary>
    public string? SerialNumberString { get; }

    /// <summary>Top-level-collection Usage Page from <c>HidP_GetCaps</c>.
    /// 0x01 is Generic Desktop (gamepads, keyboards, mice).</summary>
    public ushort TopLevelUsagePage { get; }

    /// <summary>Top-level-collection Usage. For Generic Desktop,
    /// 0x04 = Joystick, 0x05 = Game Pad, 0x06 = Keyboard, 0x02 = Mouse.
    /// HIDMaestro profile <c>type</c> is inferred from this.</summary>
    public ushort TopLevelUsage { get; }

    /// <summary>Declared input report size in bytes (HidP_GetCaps).</summary>
    public ushort InputReportByteLength { get; }

    /// <summary>Opaque device path used internally by
    /// <see cref="HMDeviceExtractor"/>. Consumers generally do not need
    /// to inspect this; <see cref="VendorId"/> + <see cref="ProductId"/>
    /// plus <see cref="ProductString"/> identifies the device to users.</summary>
    public string DevicePath { get; }

    internal HMHidDeviceInfo(
        ushort vendorId, ushort productId, ushort versionNumber,
        string? productString, string? manufacturerString, string? serialNumberString,
        ushort topLevelUsagePage, ushort topLevelUsage, ushort inputReportByteLength,
        string devicePath)
    {
        VendorId = vendorId;
        ProductId = productId;
        VersionNumber = versionNumber;
        ProductString = productString;
        ManufacturerString = manufacturerString;
        SerialNumberString = serialNumberString;
        TopLevelUsagePage = topLevelUsagePage;
        TopLevelUsage = topLevelUsage;
        InputReportByteLength = inputReportByteLength;
        DevicePath = devicePath;
    }

    /// <summary>Best-effort display label for dropdowns/lists:
    /// <c>"VID_XXXX:PID_YYYY — ProductString"</c>.</summary>
    public override string ToString()
    {
        string product = ProductString ?? "(unknown)";
        return $"VID_{VendorId:X4}:PID_{ProductId:X4} — {product}";
    }
}
