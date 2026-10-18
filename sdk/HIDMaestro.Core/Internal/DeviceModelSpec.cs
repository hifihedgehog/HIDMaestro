using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HIDMaestro.Internal;

// v2.0.0. The device model a profile describes in JSON, so a device's wire
// format and protocol are data and the SDK holds only general mechanisms:
//
//  - Ports (ExtendedReportSpec.Ports): one report that repeats a block per
//    controller port, each with a status byte and its own fields. A
//    multi-port adapter, a multitap, a receiver with several pads, an
//    encoder with several players.
//  - The device protocol (ControllerProfile.Protocol, top-level "protocol"
//    from v2.0.0): commands a host writes, the registers they set, the
//    replies they draw, when the device reports and how often, and when it
//    stops answering. Its types are in ProgramForm/ModelSpec.cs, which the
//    build tool compiles too.
//  - The stream (StreamSpec): a device that keeps reporting while the
//    consumer is quiet, at its real interval.
//  - WinUSB (UsbConfigurationSpec.WinUsb): a USB/IP persona libusb hosts
//    open, whose real device Windows gives no usable driver.
//
// docs/design/coexistence.md describes each piece and the Wii U GameCube adapter
// profile that first uses them.

/// <summary>A report made of repeated per-port blocks.</summary>
public sealed class PortsSpec
{
    /// <summary>How many ports the report carries.</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>Byte offset of port 1's block in the report, the report ID
    /// at byte 0 included.</summary>
    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    /// <summary>Bytes from one port's block to the next.</summary>
    [JsonPropertyName("stride")]
    public int Stride { get; set; }

    /// <summary>The port's status byte: what is plugged in, and the
    /// device-wide flags every port shows. Absent for a block with no status
    /// byte, which an output report's ports usually are.</summary>
    [JsonPropertyName("status")]
    public PortStatusSpec? Status { get; set; }

    /// <summary>The block's data bytes as a range relative to the block,
    /// such as "1-8". An empty port's data bytes are zero.</summary>
    [JsonPropertyName("data")]
    public string? Data { get; set; }

    /// <summary>Copies of a port's block the device keeps, which a protocol
    /// reply can carry.</summary>
    [JsonPropertyName("snapshots")]
    public List<PortSnapshotSpec>? Snapshots { get; set; }

    /// <summary>The block's fields, byte offsets relative to the block,
    /// in the extendedReport field vocabulary.</summary>
    [JsonPropertyName("fields")]
    public List<FieldSpec> Fields { get; set; } = new();
}

/// <summary>A port block's status byte.</summary>
public sealed class PortStatusSpec
{
    /// <summary>Offset of the status byte in the block.</summary>
    [JsonPropertyName("byte")]
    public int Byte { get; set; }

    /// <summary>What can be plugged into a port and the bits each sets. The
    /// first is the empty port.</summary>
    [JsonPropertyName("connections")]
    public List<PortConnectionSpec> Connections { get; set; } = new();

    /// <summary>Device-wide switches whose bits every port's status byte
    /// carries while they are on.</summary>
    [JsonPropertyName("flags")]
    public List<PortFlagSpec>? Flags { get; set; }
}

/// <summary>One kind of connection a port can hold.</summary>
public sealed class PortConnectionSpec
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>The status bits, hex.</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = "0x00";
}

/// <summary>A device-wide switch shown in every port's status byte.</summary>
public sealed class PortFlagSpec
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>The status bits it sets, hex.</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = "0x00";

    [JsonPropertyName("default")]
    public bool Default { get; set; }
}

/// <summary>A copy of a port's block the device keeps.</summary>
public sealed class PortSnapshotSpec
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>When it is taken. <c>"connect"</c>: from the first report
    /// in which the port holds something other than the empty
    /// connection.</summary>
    [JsonPropertyName("on")]
    public string On { get; set; } = "connect";
}

/// <summary>A USB/IP persona libusb hosts open, whose real device Windows
/// gives no driver they can use.</summary>
public sealed class WinUsbSpec
{
    /// <summary>The DeviceInterfaceGUIDs value WinUSB registers, which libusb
    /// opens the device through.</summary>
    [JsonPropertyName("interfaceGuid")]
    public string InterfaceGuid { get; set; } = "";
}

/// <summary>v2.0.0: a device that keeps reporting while the consumer is quiet,
/// as the real device streams: the SDK repeats the consumer's last frame one
/// period after the last frame went out. A SubmitState frame is encoded again
/// for each repeat, so rolling counters advance as the device's do. A raw
/// frame is the consumer's own bytes and repeats as they are. The device
/// clock runs it (mechanism 2). <c>extendedReport.idleFrameIntervalMs</c> is
/// the same clock with a whole-millisecond period, and a profile gives one or
/// the other.</summary>
public sealed class StreamSpec
{
    /// <summary>The period, in milliseconds. Not a whole number for a device
    /// such as a DualSense over Bluetooth, every 1.25 ms.</summary>
    [JsonPropertyName("periodMs")]
    public double? PeriodMs { get; set; }

    /// <summary>A period a host sets: a protocol register's value times
    /// <see cref="StreamPeriodRegisterSpec.UnitMs"/>, and
    /// <see cref="PeriodMs"/> while the register holds 0.</summary>
    [JsonPropertyName("periodRegister")]
    public StreamPeriodRegisterSpec? PeriodRegister { get; set; }

    /// <summary>When the device streams, on the protocol's registers: a
    /// device that starts on a host's command, such as a Switch pad after it
    /// selects mode 0x30. Absent streams from creation.</summary>
    [JsonPropertyName("while")]
    public ConditionText? While { get; set; }

    /// <summary><c>"hold"</c>, the default: a repeat carries the consumer's
    /// last sensor timestamp. <c>"advance"</c>: a repeat's consumer timestamp
    /// moves on by the time since the frame it repeats, as a real device
    /// stamps every report it sends.</summary>
    [JsonPropertyName("timestamps")]
    public string? Timestamps { get; set; }
}

public sealed class StreamPeriodRegisterSpec
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("unitMs")]
    public double UnitMs { get; set; }
}
