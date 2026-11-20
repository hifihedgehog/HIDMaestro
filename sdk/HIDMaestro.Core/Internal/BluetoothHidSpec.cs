using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HIDMaestro.Internal;

/// <summary>A profile's <c>bluetoothHid</c> block: the HID device a Bluetooth
/// pad that reaches XInput through xinputhid is over the air. Its report
/// descriptor, where its buttons sit in that descriptor, and the device
/// version it reports.
///
/// <para>Windows installs xinputhid for such a pad on the pad's transport
/// node, the Bluetooth device the HID driver runs on
/// (<c>xinputhid.inf</c>, sections Bth_Bus and Btle_Bus), and the pad's game
/// controller collection sits below it as a plain HID device whose path
/// carries <c>&amp;IG_</c>. On the USB/IP backend a profile with this block
/// presents the same arrangement: one HID interface with this descriptor
/// and the profile's VID and PID, and xinputhid on the device's own
/// transport node (<see cref="Usbip.UsbNodeStamp"/>). Windows then reads the
/// pad's reports through xinputhid as it reads the real pad's, and XInput,
/// Windows.Gaming.Input and SDL name the profile's pad.</para>
///
/// <para>The UMDF2 backend does not read the block. There the profile's
/// <c>descriptor</c> is the layout Windows' GIP driver gives xinputhid, and
/// xinputhid binds the HID child.</para></summary>
public sealed class BluetoothHidSpec
{
    /// <summary>The pad's own HID report descriptor over Bluetooth, as a hex
    /// string, as Windows' HID parser takes it.</summary>
    [JsonPropertyName("descriptor")]
    public string? Descriptor { get; set; }

    /// <summary>Where each <see cref="HIDMaestro.HMButton"/> bit sits among
    /// this descriptor's buttons, as the profile's <c>buttonMap</c> says it
    /// for the profile's <c>descriptor</c>. Null for the buttons in
    /// order.</summary>
    [JsonPropertyName("buttonMap")]
    public int[]? ButtonMap { get; set; }

    /// <summary>Which control each of this descriptor's axes is, where the
    /// SDK's own reading of the usages is wrong, as the profile's
    /// <c>axisMap</c> says it for the profile's <c>descriptor</c>.</summary>
    [JsonPropertyName("axisMap")]
    public Dictionary<string, string>? AxisMap { get; set; }

    /// <summary>The device version the pad reports over Bluetooth, the
    /// revision in its hardware IDs. Null for the profile's
    /// <c>versionNumber</c>.</summary>
    [JsonPropertyName("versionNumber")]
    public ushort? VersionNumber { get; set; }

    /// <summary>The product string the pad's HID device serves over
    /// Bluetooth. Null for the profile's <c>productString</c>.</summary>
    [JsonPropertyName("productString")]
    public string? ProductString { get; set; }

    internal BluetoothHidSpec Clone() => new()
    {
        Descriptor = Descriptor,
        ButtonMap = (int[]?)ButtonMap?.Clone(),
        AxisMap = AxisMap == null ? null : new Dictionary<string, string>(AxisMap, StringComparer.Ordinal),
        VersionNumber = VersionNumber,
        ProductString = ProductString,
    };
}
