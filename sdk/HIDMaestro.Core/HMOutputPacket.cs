using System;

namespace HIDMaestro;

/// <summary>
/// A captured output report from a host application targeting a virtual
/// controller — rumble, haptics, force feedback, adaptive trigger config,
/// LED color, etc. The SDK does not classify these by type; it surfaces
/// the raw bytes plus enough context (<see cref="Source"/>, <see cref="ReportId"/>)
/// for the consumer to decode them per the active profile.
///
/// All three semantic distinctions (rumble vs haptics, FFB vs simple
/// vibration, adaptive triggers vs lightbar) live in the same payload —
/// just at different byte offsets. The consumer reads whichever offsets
/// the profile defines.
/// </summary>
public readonly struct HMOutputPacket
{
    /// <summary>Which API the host used to send this packet.</summary>
    public readonly HMOutputSource Source;

    /// <summary>HID Report ID byte. 0 if the descriptor uses no report IDs,
    /// or for non-HID sources (e.g. XInput rumble).</summary>
    public readonly byte ReportId;

    /// <summary>The raw payload bytes. Length is <see cref="Data"/>.Length.</summary>
    public readonly ReadOnlyMemory<byte> Data;

    /// <summary>Monotonic sequence number assigned by the driver. Useful for
    /// detecting dropped packets if the consumer's reader is slower than the
    /// producer.</summary>
    public readonly uint SeqNo;

    public HMOutputPacket(HMOutputSource source, byte reportId, ReadOnlyMemory<byte> data, uint seqNo)
    {
        Source = source;
        ReportId = reportId;
        Data = data;
        SeqNo = seqNo;
    }
}

/// <summary>Which API the host used to send an output packet. Tells the
/// consumer how to interpret the bytes — XInput rumble has a different
/// wire format than a HID output report on the same controller.</summary>
public enum HMOutputSource : byte
{
    /// <summary>HidD_SetOutputReport / IOCTL_HID_WRITE_REPORT / dinput8 PID effects.
    /// Bytes are the raw HID output report payload (no Report ID byte — that's
    /// in <see cref="HMOutputPacket.ReportId"/>).</summary>
    HidOutput  = 0,

    /// <summary>HidD_SetFeature. Used by some controllers (DualSense, DualShock 4)
    /// for configuration writes.</summary>
    HidFeature = 1,

    /// <summary>XInputSetState. Bytes are the XUSB-wire-format vibration packet,
    /// typically 5 bytes: cmd + size + lo motor + hi motor + reserved.</summary>
    XInput     = 2,

    /// <summary>v1.3.5 — host-issued HidD_GetFeature. Notifies the consumer
    /// that the host requested a feature READ (not a write) for this report
    /// ID. Bytes are typically empty — this source is informational, used
    /// by the SDK's extendedReport.armOn watcher to switch Sony BT
    /// controllers from legacy Report 0x01 to vendor-blob Report 0x31 / 0x11
    /// after the host issues Get_Feature 0x05 (calibration), 0x09 (pairing),
    /// or 0x20 (firmware) — the same handshake real Sony firmware uses.</summary>
    HidFeatureRead = 3,
}
