using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HIDMaestro.Internal;

// v1.13.0. The device model a profile describes in JSON, so a device's wire
// format and protocol are data and the SDK holds only general mechanisms:
//
//  - Ports (ExtendedReportSpec.Ports): one report that repeats a block per
//    controller port, each with a status byte and its own fields. A
//    multi-port adapter, a multitap, a receiver with several pads, an
//    encoder with several players.
//  - The device protocol (UsbConfigurationSpec.Protocol): commands a host
//    writes to an OUT endpoint, the state they set, the replies they draw,
//    when the device reports and how often, and when it stops answering.
//  - WinUSB (UsbConfigurationSpec.WinUsb): a USB/IP persona libusb hosts
//    open, whose real device Windows gives no usable driver.
//
// docs/design/v1.13.0.md describes each piece and the Wii U GameCube adapter
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

/// <summary>How a USB/IP device behaves beyond its descriptors.</summary>
public sealed class DeviceProtocolSpec
{
    /// <summary>The device's named on/off state. It returns to its initial
    /// values at attach and at a port reset.</summary>
    [JsonPropertyName("state")]
    public List<ProtocolStateSpec> State { get; set; } = new();

    /// <summary>The interrupt IN endpoint the device reports on, and
    /// when.</summary>
    [JsonPropertyName("input")]
    public ProtocolInputSpec? Input { get; set; }

    /// <summary>Commands a host writes.</summary>
    [JsonPropertyName("commands")]
    public ProtocolCommandsSpec? Commands { get; set; }

    /// <summary>When the device stops answering every transfer.</summary>
    [JsonPropertyName("halt")]
    public ProtocolHaltSpec? Halt { get; set; }

    /// <summary>The HID class SET_PROTOCOL answer: <c>"ack"</c>, or
    /// <c>"stall"</c>, the default, as a device outside the boot subclass
    /// may.</summary>
    [JsonPropertyName("setProtocol")]
    public string? SetProtocol { get; set; }

    /// <summary>GET_STATUS for the device: <c>"descriptor"</c> answers the
    /// self-powered bit the configuration's bmAttributes declares. The
    /// remote-wakeup bit reads clear, because UdeCx completes
    /// SET_FEATURE(DEVICE_REMOTE_WAKEUP) itself and the device never sees the
    /// host arm it. Without this, GET_STATUS answers self-powered whatever
    /// the configuration says, as every persona before it did.</summary>
    [JsonPropertyName("getStatus")]
    public string? GetStatus { get; set; }

    /// <summary>The HID class GET_REPORT answer. <c>"input"</c>, the
    /// default: the input report as it stands, for the input report's own
    /// ID, since HID 1.11 section 7.2 makes GET_REPORT mandatory. Another
    /// input report the descriptor declares answers its ID and zeros, since
    /// the protocol sends only its own report, and an ID the descriptor
    /// does not declare stalls (USB 2.0 section 9.2.7). The other USB/IP
    /// personas answer a declared ID with a sent report's latest bytes, or
    /// its ID and zeros before the first, and stall the same way, but for
    /// the Switch 2 Pro persona, which answers a declared ID with the report
    /// it would send next and report ID 0 with its selected report.
    /// <c>"reportId"</c>: the requested report ID alone, and a stall for
    /// report ID 0 or a one-byte request, the answer tinyusb gives for a
    /// firmware that supplies no report (hid_device.c, GET_REPORT).</summary>
    [JsonPropertyName("getReport")]
    public string? GetReport { get; set; }
}

public sealed class ProtocolStateSpec
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("initial")]
    public bool Initial { get; set; }
}

public sealed class ProtocolInputSpec
{
    /// <summary>The interrupt IN endpoint, hex.</summary>
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "";

    /// <summary>Conditions on the state under which a poll gets a report,
    /// all of which must hold. "name" is true, "!name" false.</summary>
    [JsonPropertyName("while")]
    public List<string>? While { get; set; }

    /// <summary>How the device paces its reports.</summary>
    [JsonPropertyName("clock")]
    public ProtocolClockSpec? Clock { get; set; }
}

/// <summary>A device's report pacing. The host polls at the endpoint's
/// bInterval unless a program models a faster host. A device that reads its
/// controllers on its own period has a new report only once one of those
/// reads has happened since its last report.</summary>
public sealed class ProtocolClockSpec
{
    [JsonPropertyName("sourcePollMs")]
    public double? SourcePollMs { get; set; }

    [JsonPropertyName("sourcePollRules")]
    public List<ProtocolPollRuleSpec>? SourcePollRules { get; set; }
}

/// <summary>A source poll period that applies while exactly the listed
/// ports hold something.</summary>
public sealed class ProtocolPollRuleSpec
{
    [JsonPropertyName("portsConnected")]
    public List<int> PortsConnected { get; set; } = new();

    [JsonPropertyName("periodMs")]
    public double PeriodMs { get; set; }
}

public sealed class ProtocolCommandsSpec
{
    /// <summary>The interrupt OUT endpoint, hex. A transfer reaches the
    /// device as packets of the endpoint's wMaxPacketSize, and each packet is
    /// a command.</summary>
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "";

    /// <summary>True when a SET_REPORT(Output) on the control endpoint is a
    /// command too: its whole data stage, report ID first, as one
    /// command.</summary>
    [JsonPropertyName("setReport")]
    public bool SetReport { get; set; }

    /// <summary>True when the device's USB stack drops a SET_REPORT's report
    /// ID before the firmware reads it: a data stage longer than one byte
    /// whose first byte is the ID wValue names loses that byte, and the
    /// command is what follows. tinyusb does this (hid_device.c,
    /// SET_REPORT). Applies with <see cref="SetReport"/> only.</summary>
    [JsonPropertyName("setReportStripsId")]
    public bool SetReportStripsId { get; set; }

    /// <summary>How many replies can wait for the IN endpoint at once. A
    /// reply that finds that many waiting is dropped, and its rule's states
    /// are still set, as a firmware drops a report it cannot queue while
    /// its endpoint is busy. 1 when absent: the endpoint holds one transfer
    /// at a time. A reply already part sent, longer than one IN packet,
    /// does not count, where a firmware's endpoint stays busy until it
    /// ends.</summary>
    [JsonPropertyName("pendingReplies")]
    public int? PendingReplies { get; set; }

    /// <summary>Tried in order. The first rule that matches a packet
    /// applies. A packet no rule matches is ignored.</summary>
    [JsonPropertyName("rules")]
    public List<ProtocolRuleSpec> Rules { get; set; } = new();
}

public sealed class ProtocolRuleSpec
{
    /// <summary>The bytes the packet starts with, hex.</summary>
    [JsonPropertyName("match")]
    public string Match { get; set; } = "";

    [JsonPropertyName("minLength")]
    public int? MinLength { get; set; }

    [JsonPropertyName("maxLength")]
    public int? MaxLength { get; set; }

    /// <summary>State values the rule sets.</summary>
    [JsonPropertyName("set")]
    public Dictionary<string, bool>? Set { get; set; }

    /// <summary>A reply the next IN poll gets, built from the state before
    /// the rule sets anything.</summary>
    [JsonPropertyName("reply")]
    public List<ProtocolReplyPartSpec>? Reply { get; set; }
}

/// <summary>One part of a reply. Exactly one of <see cref="Hex"/>,
/// <see cref="State"/> and <see cref="Snapshot"/>.</summary>
public sealed class ProtocolReplyPartSpec
{
    /// <summary>Constant bytes.</summary>
    [JsonPropertyName("hex")]
    public string? Hex { get; set; }

    /// <summary>One byte, 1 when the named state is on, else 0.</summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    /// <summary>A port snapshot's bytes for every port in turn, zero for a
    /// port that has none.</summary>
    [JsonPropertyName("snapshot")]
    public string? Snapshot { get; set; }

    /// <summary>The range of each port's block a snapshot part carries,
    /// such as "3-8".</summary>
    [JsonPropertyName("bytes")]
    public string? Bytes { get; set; }

    /// <summary>Conditions under which the part carries data. Otherwise it
    /// is zeros of the same length.</summary>
    [JsonPropertyName("while")]
    public List<string>? While { get; set; }
}

public sealed class ProtocolHaltSpec
{
    /// <summary>Conditions under which every transfer fails, all of which
    /// must hold. A port reset or a new attach clears the state.</summary>
    [JsonPropertyName("while")]
    public List<string> While { get; set; } = new();

    /// <summary>The Linux status the transfers fail with, as a USB/IP
    /// server sends it: <c>"EPROTO"</c> for a device that does not answer,
    /// which Windows sees as a transaction error.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "EPROTO";
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
