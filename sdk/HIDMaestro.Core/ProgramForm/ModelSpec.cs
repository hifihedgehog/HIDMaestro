// v2.0.0. The device model groups a profile describes in JSON beside its
// descriptor (design note, mechanisms 2 and 3): the protocol a host speaks to
// the device, the state block SubmitState fills, and the reports the device
// builds from it. These types live in the shared ProgramForm folder because
// the build tool reads the same groups to generate the catalog's kernels.
// ModelJson reads them, strictly, for the tool and for the SDK alike.
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HIDMaestro.Internal;

/// <summary>How a device behaves beyond its descriptors: its registers, memory
/// images and counters, the commands a host writes and the replies they draw,
/// when it reports, its output decoders, and when it stops answering.</summary>
public sealed class ProtocolSpec
{
    /// <summary>The device's registers. Each returns to its initial value at
    /// attach and at a port reset.</summary>
    [JsonPropertyName("state")]
    public List<RegisterSpec>? State { get => _state; set { _state = value; StateWritten = true; } }
    private List<RegisterSpec>? _state;

    /// <summary>True when the file writes the key, so a null list is told
    /// apart from a device with no registers, which leaves it out.</summary>
    [JsonIgnore]
    public bool StateWritten { get; private set; }

    /// <summary>Named memory images a reply reads and a command writes.</summary>
    [JsonPropertyName("memory")]
    public List<MemorySpec>? Memory { get; set; }

    /// <summary>Counters, which reply parts and report programs write.</summary>
    [JsonPropertyName("counters")]
    public List<CounterSpec>? Counters { get; set; }

    /// <summary>The endpoint the device reports on, and when.</summary>
    [JsonPropertyName("input")]
    public ProtocolInputSpec? Input { get; set; }

    /// <summary>Commands a host writes.</summary>
    [JsonPropertyName("commands")]
    public ProtocolCommandsSpec? Commands { get; set; }

    /// <summary>Where replies wait and what a full queue drops.</summary>
    [JsonPropertyName("replies")]
    public ProtocolRepliesSpec? Replies { get; set; }

    /// <summary>Named groups of output fields, raised on OutputDecoded.</summary>
    [JsonPropertyName("decoders")]
    public Dictionary<string, DecoderSpec>? Decoders { get; set; }

    /// <summary>When the device stops answering every transfer.</summary>
    [JsonPropertyName("halt")]
    public ProtocolHaltSpec? Halt { get; set; }

    /// <summary>The HID class SET_PROTOCOL answer: <c>"ack"</c>, or
    /// <c>"stall"</c>, the default, as a device outside the boot subclass
    /// may.</summary>
    [JsonPropertyName("setProtocol")]
    public string? SetProtocol { get; set; }

    /// <summary>GET_STATUS for the device: <c>"descriptor"</c> answers the
    /// self-powered bit the configuration's bmAttributes declares, and the
    /// remote-wakeup bit clear, since the host's arming never reaches the
    /// server. Without it GET_STATUS answers self-powered, as every persona
    /// before it did.</summary>
    [JsonPropertyName("getStatus")]
    public string? GetStatus { get; set; }

    /// <summary>The HID class GET_REPORT(Input) answer. <c>"input"</c>, the
    /// default: the input report as it stands, for its own ID.
    /// <c>"reportId"</c>: the requested report ID alone, and a stall for
    /// report ID 0 or a one-byte request, as tinyusb answers for a firmware
    /// that supplies no report. <c>"deviceReport"</c>: a declared ID gets its
    /// layout built now without counting it, and report ID 0 the selected
    /// layout.</summary>
    [JsonPropertyName("getReport")]
    public string? GetReport { get; set; }

    /// <summary>How many of the last commands the device keeps, with their
    /// arrival times, for the SDK's own probes to read. No public member
    /// returns them. Absent keeps none.</summary>
    [JsonPropertyName("log")]
    public int? Log { get; set; }
}

/// <summary>One register: a name, a width of 1 to 32 bits, and an initial
/// value. A one-bit register is the on/off state the never-shipped 1.13.0 builds called a state.</summary>
public sealed class RegisterSpec
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>1 to 32. Absent is 1.</summary>
    [JsonPropertyName("bits")]
    public int? Bits { get; set; }

    /// <summary>A number, a hex string, or true and false for 1 and 0.</summary>
    [JsonPropertyName("initial")]
    public HexNumber? Initial { get; set; }

    /// <summary>True for a register a port reset leaves as it is, as a
    /// setting the device holds while it stays powered. An attach returns
    /// every register to its initial value.</summary>
    [JsonPropertyName("keepOnPortReset")]
    public bool? KeepOnPortReset { get; set; }
}

/// <summary>A memory image: <see cref="Size"/> bytes of <see cref="Fill"/>
/// with segments over them. A reply reads it, and addresses wrap at its
/// size.</summary>
public sealed class MemorySpec
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>1 to 0x100000000 bytes. An image a rule writes is held whole
    /// and has at most 0x1000000. An image only replies read is held as its
    /// segments, so it can span the whole 32-bit address space, where an
    /// address and a length wrap as 32-bit sums do.</summary>
    [JsonPropertyName("size")]
    public HexSize Size { get; set; }

    /// <summary>One hex byte, what an address no segment covers reads.
    /// Absent is 00.</summary>
    [JsonPropertyName("fill")]
    public string? Fill { get; set; }

    [JsonPropertyName("segments")]
    public List<MemorySegmentSpec>? Segments { get; set; }
}

/// <summary>Bytes at an address: constant hex, or a per-device value.</summary>
public sealed class MemorySegmentSpec
{
    [JsonPropertyName("at")]
    public HexNumber At { get; set; }

    [JsonPropertyName("hex")]
    public string? Hex { get; set; }

    /// <summary>A value source: <c>"serial"</c>, the serial the device
    /// serves, or <c>"index"</c>, the transport index.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>Bytes a value segment covers. Shorter values are zero
    /// padded, longer ones cut.</summary>
    [JsonPropertyName("length")]
    public int? Length { get; set; }

    /// <summary><c>"ascii"</c>, <c>"uint8"</c>, <c>"uint16-le"</c> or
    /// <c>"uint32-le"</c>.</summary>
    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    /// <summary>Added to a numeric value before it is written.</summary>
    [JsonPropertyName("add")]
    public int? Add { get; set; }
}

/// <summary>A counter: a slot of up to 32 bits that advances on an event.</summary>
public sealed class CounterSpec
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>8 to 32: the counter wraps at 2^bits. Absent is 32.</summary>
    [JsonPropertyName("bits")]
    public int? Bits { get; set; }

    /// <summary><c>"report"</c>, each layout sent. <c>"frame"</c>, each
    /// message that carries it: a reply that writes it, or a layout whose
    /// program writes it, so one counter numbers a device's replies and the
    /// reports that stamp it. <c>"clock"</c>, the time since the
    /// device clock's origin over <see cref="PeriodUs"/>, computed from the
    /// origin on every write. <c>"read"</c>, each time a reply part writes
    /// it.</summary>
    [JsonPropertyName("on")]
    public string On { get; set; } = "";

    [JsonPropertyName("periodUs")]
    public double? PeriodUs { get; set; }

    [JsonPropertyName("initial")]
    public HexNumber? Initial { get; set; }
}

/// <summary>The endpoint a device reports on, and when.</summary>
public sealed class ProtocolInputSpec
{
    /// <summary>The interrupt IN endpoint, hex.</summary>
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    /// <summary>When a poll can get a report.</summary>
    [JsonPropertyName("while")]
    public ConditionText? While { get; set; }

    /// <summary>How the device paces its reports.</summary>
    [JsonPropertyName("clock")]
    public ProtocolClockSpec? Clock { get; set; }
}

/// <summary>A device's report pacing, one of two forms. A device whose report
/// is the SDK's frame answers each host poll, and a device that reads its
/// controllers on its own period has a new report only once one of those
/// reads has happened since its last report (<see cref="SourcePollMs"/>). A
/// device that builds its reports sends them on its own clock
/// (<see cref="PeriodMs"/>).</summary>
public sealed class ProtocolClockSpec
{
    [JsonPropertyName("sourcePollMs")]
    public double? SourcePollMs { get; set; }

    [JsonPropertyName("sourcePollRules")]
    public List<ProtocolPollRuleSpec>? SourcePollRules { get; set; }

    /// <summary>The device clock's period.</summary>
    [JsonPropertyName("periodMs")]
    public double? PeriodMs { get; set; }

    /// <summary>Where the clock's grid starts: <c>"attach"</c>, or
    /// <c>"inputOpened"</c>, the command that let reports flow.</summary>
    [JsonPropertyName("origin")]
    public string? Origin { get; set; }

    /// <summary>What a tick does when no read waits for its report:
    /// <c>"owe"</c>, the next read gets one at once, or <c>"nextPoll"</c>, the
    /// next read waits for the next tick.</summary>
    [JsonPropertyName("missedTick")]
    public string? MissedTick { get; set; }
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

/// <summary>Where commands come from, and the rules that answer them.</summary>
public sealed class ProtocolCommandsSpec
{
    /// <summary><c>"interruptOut"</c> or <c>"bulkOut"</c> with
    /// <see cref="Endpoint"/>, <c>"setReport"</c> for SET_REPORT alone, or
    /// <c>"outputReport"</c> for a UMDF2 device's output reports. Absent is
    /// the endpoint's own kind.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>The OUT endpoint, hex. A transfer reaches an interrupt
    /// endpoint as packets of its wMaxPacketSize, each a command, and a bulk
    /// endpoint as one command.</summary>
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    /// <summary>True when a SET_REPORT on the control endpoint is a command
    /// too: its whole data stage, report ID first, as one command.</summary>
    [JsonPropertyName("setReport")]
    public bool SetReport { get; set; }

    /// <summary>True when the device's USB stack drops a SET_REPORT's report
    /// ID before the firmware reads it, as tinyusb does.</summary>
    [JsonPropertyName("setReportStripsId")]
    public bool SetReportStripsId { get; set; }

    /// <summary>True when no command from this source is raised on
    /// OutputReceived, as a device's vendor channel never is.</summary>
    [JsonPropertyName("consume")]
    public bool Consume { get; set; }

    /// <summary>A command shorter than this matches no rule.</summary>
    [JsonPropertyName("minLength")]
    public int? MinLength { get; set; }

    /// <summary>Parts every reply starts with, unless its rule says
    /// <c>"prefix": false</c>.</summary>
    [JsonPropertyName("replyPrefix")]
    public List<ReplyPartSpec>? ReplyPrefix { get; set; }

    /// <summary>The status byte a prefix's status part writes, hex, unless
    /// the rule gives its own.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Tried in order. The first rule that matches applies, and a
    /// command no rule matches changes nothing.</summary>
    [JsonPropertyName("rules")]
    public List<ProtocolRuleSpec>? Rules { get; set; }
}

/// <summary>One command rule.</summary>
public sealed class ProtocolRuleSpec
{
    /// <summary>The bytes the command holds at <see cref="At"/>, hex, with
    /// <c>..</c> for a byte the rule does not test.</summary>
    [JsonPropertyName("match")]
    public string Match { get; set; } = "";

    [JsonPropertyName("at")]
    public int? At { get; set; }

    [JsonPropertyName("minLength")]
    public int? MinLength { get; set; }

    [JsonPropertyName("maxLength")]
    public int? MaxLength { get; set; }

    /// <summary>Registers the rule sets to fixed values.</summary>
    [JsonPropertyName("set")]
    public Dictionary<string, HexNumber>? Set { get; set; }

    /// <summary>Registers the rule sets from command bytes.</summary>
    [JsonPropertyName("store")]
    public Dictionary<string, StoreSpec>? Store { get; set; }

    /// <summary>Registers set from a list of fixed-size records.</summary>
    [JsonPropertyName("storeRecord")]
    public StoreRecordSpec? StoreRecord { get; set; }

    /// <summary>Registers ORed with a command byte under another register.</summary>
    [JsonPropertyName("or")]
    public Dictionary<string, MaskedStoreSpec>? Or { get; set; }

    /// <summary>Registers cleared of a command byte's bits under another
    /// register.</summary>
    [JsonPropertyName("andNot")]
    public Dictionary<string, MaskedStoreSpec>? AndNot { get; set; }

    /// <summary>Command bytes written into a memory image.</summary>
    [JsonPropertyName("write")]
    public List<MemoryWriteSpec>? Write { get; set; }

    /// <summary>True when the command is not raised on OutputReceived.</summary>
    [JsonPropertyName("consume")]
    public bool? Consume { get; set; }

    /// <summary>A report raised on OutputReceived in place of the command,
    /// built from the command's bytes.</summary>
    [JsonPropertyName("emitOutput")]
    public EmitOutputSpec? EmitOutput { get; set; }

    /// <summary>A decoder of <see cref="ProtocolSpec.Decoders"/> run on the
    /// command.</summary>
    [JsonPropertyName("decode")]
    public string? Decode { get; set; }

    /// <summary>The reply, built from the registers before the rule sets
    /// anything.</summary>
    [JsonPropertyName("reply")]
    public List<ReplyPartSpec>? Reply { get; set; }

    /// <summary>The reply's length, zero padded. Absent is the parts' own.</summary>
    [JsonPropertyName("replyLength")]
    public int? ReplyLength { get; set; }

    /// <summary>The status byte for this rule's reply, hex.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>False for a reply with its own head. Absent is true.</summary>
    [JsonPropertyName("prefix")]
    public bool? Prefix { get; set; }
}

/// <summary>A register set from command bytes.</summary>
public sealed class StoreSpec
{
    [JsonPropertyName("byte")]
    public int Byte { get; set; }

    /// <summary>1 to 4. Absent is 1.</summary>
    [JsonPropertyName("bytes")]
    public int? Bytes { get; set; }

    /// <summary><c>"le"</c>, the default, or <c>"be"</c>.</summary>
    [JsonPropertyName("order")]
    public string? Order { get; set; }

    /// <summary>Hex values: the register is set only when the byte is one of
    /// them.</summary>
    [JsonPropertyName("in")]
    public List<string>? In { get; set; }

    /// <summary>True: the register takes 1 when the value is nonzero and 0
    /// otherwise.</summary>
    [JsonPropertyName("nonzero")]
    public bool? Nonzero { get; set; }
}

/// <summary>Registers set from a list of fixed-size records in the command:
/// the count from a length byte, each record's key byte compared, and the
/// matching record's value bytes stored.</summary>
public sealed class StoreRecordSpec
{
    /// <summary>The first record's offset in the command.</summary>
    [JsonPropertyName("at")]
    public int At { get; set; }

    [JsonPropertyName("size")]
    public int Size { get; set; }

    [JsonPropertyName("count")]
    public RecordCountSpec Count { get; set; } = new();

    [JsonPropertyName("key")]
    public RecordKeySpec Key { get; set; } = new();

    [JsonPropertyName("store")]
    public Dictionary<string, RecordFieldSpec> Store { get; set; } = new();
}

public sealed class RecordCountSpec
{
    [JsonPropertyName("byte")]
    public int Byte { get; set; }

    [JsonPropertyName("divideBy")]
    public int? DivideBy { get; set; }
}

public sealed class RecordKeySpec
{
    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    /// <summary>Hex byte.</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

public sealed class RecordFieldSpec
{
    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("bytes")]
    public int? Bytes { get; set; }

    [JsonPropertyName("order")]
    public string? Order { get; set; }
}

/// <summary>A command byte masked by a register.</summary>
public sealed class MaskedStoreSpec
{
    [JsonPropertyName("byte")]
    public int Byte { get; set; }

    /// <summary>The register whose bits the byte is masked by.</summary>
    [JsonPropertyName("mask")]
    public string Mask { get; set; } = "";
}

/// <summary>Command bytes written into a memory image.</summary>
public sealed class MemoryWriteSpec
{
    [JsonPropertyName("memory")]
    public string Memory { get; set; } = "";

    [JsonPropertyName("address")]
    public ByteSourceSpec Address { get; set; } = new();

    /// <summary>The command bytes written, a range such as "16-31".</summary>
    [JsonPropertyName("from")]
    public string? From { get; set; }

    /// <summary>In place of <see cref="From"/>: one byte, written whole,
    /// whose bits are set for the command bytes that are nonzero, as
    /// [command byte, bit] pairs. A command that gives each of a device's
    /// lights a mode byte, of which the device keeps only on or off, writes
    /// its light bits this way.</summary>
    [JsonPropertyName("nonzero")]
    public List<List<int>>? Nonzero { get; set; }
}

/// <summary>A report raised in place of the command: a program over the
/// command's bytes, or the report ID and then a memory image's first bytes,
/// read after the rule's writes, for a device that folds each command into
/// an output report it keeps.</summary>
public sealed class EmitOutputSpec
{
    [JsonPropertyName("reportId")]
    public string ReportId { get; set; } = "";

    [JsonPropertyName("size")]
    public int Size { get; set; }

    /// <summary>A memory image of <see cref="ProtocolSpec.Memory"/>: the
    /// report is the report ID, then the image's first
    /// <see cref="Size"/> - 1 bytes. It takes no program.</summary>
    [JsonPropertyName("memory")]
    public string? Memory { get; set; }

    [JsonPropertyName("program")]
    public List<ProgramOpSpec> Program { get; set; } = new();
}

/// <summary>One part of a reply. Exactly one of the part kinds.</summary>
public sealed class ReplyPartSpec
{
    /// <summary>Constant bytes.</summary>
    [JsonPropertyName("hex")]
    public string? Hex { get; set; }

    /// <summary>One byte, 1 when the named register is nonzero, else 0.</summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    /// <summary>A port snapshot's bytes for every port in turn, zero for a
    /// port that has none.</summary>
    [JsonPropertyName("snapshot")]
    public string? Snapshot { get; set; }

    /// <summary>The range of each port's block a snapshot part carries, or
    /// the range of a layout an input part carries.</summary>
    [JsonPropertyName("bytes")]
    public string? Bytes { get; set; }

    /// <summary>Command bytes, a range such as "10-15".</summary>
    [JsonPropertyName("echo")]
    public string? Echo { get; set; }

    /// <summary>With a one-byte echo: the byte, at most this.</summary>
    [JsonPropertyName("max")]
    public int? Max { get; set; }

    /// <summary>A register's value.</summary>
    [JsonPropertyName("register")]
    public string? Register { get; set; }

    /// <summary>Bytes a register or counter part writes, 1 to 4. Absent is 1.</summary>
    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("order")]
    public string? Order { get; set; }

    /// <summary>A counter's value, after which the counter advances.</summary>
    [JsonPropertyName("counter")]
    public string? Counter { get; set; }

    /// <summary>A memory image's bytes from <see cref="Address"/> for
    /// <see cref="Length"/> bytes, addresses wrapping at the image's size.</summary>
    [JsonPropertyName("memory")]
    public string? Memory { get; set; }

    [JsonPropertyName("address")]
    public ByteSourceSpec? Address { get; set; }

    [JsonPropertyName("length")]
    public ByteSourceSpec? Length { get; set; }

    /// <summary>Bytes chosen by the set bits of a command byte.</summary>
    [JsonPropertyName("bitmap")]
    public BitmapSpec? Bitmap { get; set; }

    /// <summary>Bytes of a layout as it would go out now, by layout name.</summary>
    [JsonPropertyName("input")]
    public string? Input { get; set; }

    /// <summary>A value source, with <see cref="MemorySegmentSpec"/>'s
    /// length, encoding and add.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    [JsonPropertyName("add")]
    public int? Add { get; set; }

    /// <summary>True: the status byte, the rule's or the commands'.</summary>
    [JsonPropertyName("status")]
    public bool? Status { get; set; }

    /// <summary>When the part carries data. Otherwise it is zeros of the
    /// same length.</summary>
    [JsonPropertyName("while")]
    public ConditionText? While { get; set; }
}

/// <summary>A number taken from the command or given: an address or a
/// length.</summary>
public sealed class ByteSourceSpec
{
    /// <summary>A 32-bit little-endian value at this command offset.</summary>
    [JsonPropertyName("le32")]
    public int? Le32 { get; set; }

    /// <summary>A 16-bit little-endian value at this command offset.</summary>
    [JsonPropertyName("le16")]
    public int? Le16 { get; set; }

    /// <summary>One command byte at this offset.</summary>
    [JsonPropertyName("byte")]
    public int? Byte { get; set; }

    /// <summary>A fixed value.</summary>
    [JsonPropertyName("value")]
    public HexNumber? Value { get; set; }

    /// <summary>The value, at most this.</summary>
    [JsonPropertyName("max")]
    public int? Max { get; set; }
}

/// <summary>Bytes chosen by the set bits of a command byte. A command too
/// short to hold the byte reads it as 0.</summary>
public sealed class BitmapSpec
{
    [JsonPropertyName("byte")]
    public int Byte { get; set; }

    [JsonPropertyName("size")]
    public int Size { get; set; }

    [JsonPropertyName("set")]
    public List<BitmapEntrySpec> Set { get; set; } = new();
}

/// <summary>While bit <see cref="Bit"/> is set, <see cref="Hex"/> at
/// <see cref="At"/>.</summary>
public sealed class BitmapEntrySpec
{
    [JsonPropertyName("bit")]
    public int Bit { get; set; }

    [JsonPropertyName("at")]
    public int At { get; set; }

    [JsonPropertyName("hex")]
    public string Hex { get; set; } = "";
}

/// <summary>Where replies wait for the host.</summary>
public sealed class ProtocolRepliesSpec
{
    /// <summary><c>"input"</c>, ahead of reports on the input endpoint, or
    /// <c>"bulkIn"</c>, on a bulk IN endpoint in chunks of each read's
    /// length.</summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    /// <summary>The bulk IN endpoint, hex.</summary>
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    /// <summary>How many replies can wait. Absent is 1.</summary>
    [JsonPropertyName("pending")]
    public int? Pending { get; set; }

    /// <summary>What a reply that finds the queue full does:
    /// <c>"dropNewest"</c>, the default, it is dropped. <c>"dropOldest"</c>,
    /// the oldest waiting reply makes room. <c>"dropOldestUnstarted"</c>, the
    /// oldest makes room unless part of it has gone out, and then the new
    /// reply is dropped.</summary>
    [JsonPropertyName("overflow")]
    public string? Overflow { get; set; }
}

public sealed class ProtocolHaltSpec
{
    /// <summary>When every transfer fails. A port reset or a new attach
    /// clears the registers.</summary>
    [JsonPropertyName("while")]
    public ConditionText? While { get; set; }

    /// <summary>The Linux status the transfers fail with: <c>"EPROTO"</c> for
    /// a device that does not answer, or <c>"EPIPE"</c>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "EPROTO";
}

/// <summary>A named group of output fields, decoded from an output report or
/// a command and raised on OutputDecoded.</summary>
public sealed class DecoderSpec
{
    /// <summary>Output report IDs, hex, the controller decodes with this
    /// wherever they arrive.</summary>
    [JsonPropertyName("reports")]
    public List<string>? Reports { get; set; }

    /// <summary>The bytes a report needs, its ID included. A shorter one is
    /// not decoded.</summary>
    [JsonPropertyName("minLength")]
    public int MinLength { get; set; }

    [JsonPropertyName("fields")]
    public List<DecodeFieldSpec> Fields { get; set; } = new();
}

/// <summary>One decoded field.</summary>
public sealed class DecodeFieldSpec
{
    /// <summary>The name OutputDecoded raises the value under.</summary>
    [JsonPropertyName("semantic")]
    public string Semantic { get; set; } = "";

    /// <summary><c>"packed-uint"</c>, boxed as a byte.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("parts")]
    public List<PackedPartSpec> Parts { get; set; } = new();
}

/// <summary>v2.0.0: one part of a <c>packed-uint</c> field. Its value is
/// assembled from <see cref="Pieces"/>, lowest first, in each of
/// <see cref="Frames"/>. A value plus <see cref="Offset"/> maps to 0 outside
/// <see cref="Range"/>, and otherwise to itself times <see cref="Scale"/>,
/// rounded by <see cref="Round"/> and clamped to 0..255. The field is the
/// largest mapped value of every part and frame.</summary>
public sealed class PackedPartSpec
{
    /// <summary>The bit groups, lowest first: each <c>{ "at", "count" }</c>,
    /// a bit offset from the report's first byte, its ID included, and a
    /// width.</summary>
    [JsonPropertyName("pieces")]
    public List<PackedPieceSpec> Pieces { get; set; } = new();

    /// <summary><c>[count, stride]</c>: the pieces read in each of
    /// <c>count</c> frames, <c>stride</c> bytes apart. Absent is one
    /// frame.</summary>
    [JsonPropertyName("frames")]
    public List<int>? Frames { get; set; }

    /// <summary>Added to the value before the range test.</summary>
    [JsonPropertyName("offset")]
    public int? Offset { get; set; }

    /// <summary><c>[low, high]</c>: a value outside maps to 0. Absent
    /// accepts every value.</summary>
    [JsonPropertyName("range")]
    public List<int>? Range { get; set; }

    /// <summary>The value's factor, a number or <c>[numerator,
    /// denominator]</c>. Absent is 1.</summary>
    [JsonPropertyName("scale")]
    public ScaleSpec? Scale { get; set; }

    /// <summary><c>"down"</c> truncates the scaled value, as integer
    /// division does. <c>"nearest"</c>, the default, rounds it to nearest,
    /// ties to even.</summary>
    [JsonPropertyName("round")]
    public string? Round { get; set; }

    /// <summary>A copy of every JSON key, for ExtendedReportSpec.Clone. The
    /// kernel generator compiles this file without SpecLists, so the lists
    /// are copied here.</summary>
    internal PackedPartSpec Clone() => new()
    {
        Pieces = Pieces?.ConvertAll(p => p?.Clone()!)!,
        Frames = Frames == null ? null : new List<int>(Frames),
        Offset = Offset,
        Range = Range == null ? null : new List<int>(Range),
        Scale = Scale?.Clone(),
        Round = Round,
    };
}

/// <summary>v2.0.0: one bit group of a <see cref="PackedPartSpec"/>.</summary>
public sealed class PackedPieceSpec
{
    [JsonPropertyName("at")]
    public int At { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>A copy of every JSON key, for ExtendedReportSpec.Clone.</summary>
    internal PackedPieceSpec Clone() => new() { At = At, Count = Count };
}

/// <summary>v2.0.0: a <c>scale</c> as JSON writes it, a number or an exact
/// ratio <c>[numerator, denominator]</c>.</summary>
[JsonConverter(typeof(ScaleSpecJsonConverter))]
public sealed class ScaleSpec
{
    public double Numerator { get; set; }
    public double Denominator { get; set; } = 1;

    /// <summary>A copy of both terms, for ExtendedReportSpec.Clone.</summary>
    internal ScaleSpec Clone() => new() { Numerator = Numerator, Denominator = Denominator };
}

/// <summary>Reads and writes <see cref="ScaleSpec"/> as a number or a
/// two-number array.</summary>
public sealed class ScaleSpecJsonConverter : JsonConverter<ScaleSpec>
{
    public override ScaleSpec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return new ScaleSpec { Numerator = reader.GetDouble(), Denominator = 1 };
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("A scale is a number or an array [numerator, denominator].");
        reader.Read();
        if (reader.TokenType != JsonTokenType.Number) throw new JsonException("A scale's numerator is a number.");
        double n = reader.GetDouble();
        reader.Read();
        if (reader.TokenType != JsonTokenType.Number) throw new JsonException("A scale's denominator is a number.");
        double d = reader.GetDouble();
        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("A scale's array holds two numbers.");
        return new ScaleSpec { Numerator = n, Denominator = d };
    }

    public override void Write(Utf8JsonWriter writer, ScaleSpec value, JsonSerializerOptions options)
    {
        if (value.Denominator == 1)
        {
            writer.WriteNumberValue(value.Numerator);
            return;
        }
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Numerator);
        writer.WriteNumberValue(value.Denominator);
        writer.WriteEndArray();
    }
}

/// <summary>The bytes SubmitState fills for a device that builds its own
/// reports. <see cref="Fields"/> is a field list in the codec vocabulary of
/// <c>extendedReport</c>, which the SDK reads.</summary>
public sealed class StateBlockSpec
{
    [JsonPropertyName("size")]
    public int Size { get; set; }

    [JsonPropertyName("fields")]
    public JsonElement? Fields { get; set; }
}

/// <summary>One report a device builds from its state block.</summary>
public sealed class InputReportSpec
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Hex. Absent for a device without report IDs.</summary>
    [JsonPropertyName("reportId")]
    public string? ReportId { get; set; }

    [JsonPropertyName("size")]
    public int Size { get; set; }

    /// <summary>While the layout is the one that goes out.</summary>
    [JsonPropertyName("while")]
    public ConditionText? While { get; set; }

    /// <summary><c>"clock"</c>, the default: on the device clock.
    /// <c>"change"</c>: when its bytes differ from the last it sent.
    /// <c>"submit"</c>: on every SubmitState.</summary>
    [JsonPropertyName("send")]
    public string? Send { get; set; }

    [JsonPropertyName("program")]
    public List<ProgramOpSpec> Program { get; set; } = new();
}

/// <summary>A feature report whose answer is the current state, built by a
/// program on every frame.</summary>
public sealed class LiveFeatureReportSpec
{
    [JsonPropertyName("reportId")]
    public string ReportId { get; set; } = "";

    [JsonPropertyName("size")]
    public int Size { get; set; }

    [JsonPropertyName("program")]
    public List<ProgramOpSpec> Program { get; set; } = new();
}

/// <summary>One op of a report program, as a profile writes it. Exactly one
/// op key is set, and every other key belongs to that op: <c>const</c>,
/// <c>copy</c>, <c>bits</c>, <c>xor</c>, <c>swap16</c>, <c>field</c>,
/// <c>lookup</c>, <c>hat8</c>, <c>pack12</c>, <c>counter</c> or
/// <c>when</c>. The design note's mechanism 2 gives each op's effect.</summary>
public sealed class ProgramOpSpec
{
    /// <summary>Report offset to hex bytes.</summary>
    [JsonPropertyName("const")]
    public Dictionary<string, string>? Const { get; set; }

    /// <summary>A state byte range such as "0-1".</summary>
    [JsonPropertyName("copy")]
    public string? Copy { get; set; }

    /// <summary>[state bit, report bit] pairs.</summary>
    [JsonPropertyName("bits")]
    public List<List<int>>? Bits { get; set; }

    [JsonPropertyName("xor")]
    public string? Xor { get; set; }

    [JsonPropertyName("swap16")]
    public string? Swap16 { get; set; }

    [JsonPropertyName("field")]
    public ReadSpec? Field { get; set; }

    [JsonPropertyName("lookup")]
    public ReadSpec? Lookup { get; set; }

    [JsonPropertyName("hat8")]
    public List<ReadSpec>? Hat8 { get; set; }

    /// <summary>[x offset, y offset] of two 16-bit state values.</summary>
    [JsonPropertyName("pack12")]
    public List<int>? Pack12 { get; set; }

    /// <summary>A counter's name.</summary>
    [JsonPropertyName("counter")]
    public string? Counter { get; set; }

    [JsonPropertyName("when")]
    public ConditionText? When { get; set; }

    [JsonPropertyName("ops")]
    public List<ProgramOpSpec>? Ops { get; set; }

    /// <summary>The report offset the op writes at.</summary>
    [JsonPropertyName("to")]
    public int? To { get; set; }

    /// <summary>A counter's report offset.</summary>
    [JsonPropertyName("at")]
    public int? At { get; set; }

    /// <summary>Hex byte: a copy's mask makes it bit moves, an xor's mask.</summary>
    [JsonPropertyName("mask")]
    public string? Mask { get; set; }

    [JsonPropertyName("center")]
    public int? Center { get; set; }

    [JsonPropertyName("negate")]
    public bool? Negate { get; set; }

    /// <summary>A field's ratio [numerator, denominator], or a counter's
    /// integer factor.</summary>
    [JsonPropertyName("scale")]
    public ScaleSpec? Scale { get; set; }

    [JsonPropertyName("offset")]
    public int? Offset { get; set; }

    /// <summary>[min, max]. Absent is the output width's unsigned range.</summary>
    [JsonPropertyName("clamp")]
    public List<int>? Clamp { get; set; }

    /// <summary><c>"wrap16"</c>: wrapped as an unsigned 16-bit value, not
    /// clamped.</summary>
    [JsonPropertyName("overflow")]
    public string? Overflow { get; set; }

    /// <summary>Bytes a field writes, 1 or 2. Absent is 1.</summary>
    [JsonPropertyName("bytes")]
    public int? Bytes { get; set; }

    /// <summary><c>"le"</c>, the default, or <c>"be"</c>.</summary>
    [JsonPropertyName("order")]
    public string? Order { get; set; }

    /// <summary>A counter's width in bits: 8, 16, 24 or 32.</summary>
    [JsonPropertyName("width")]
    public int? Width { get; set; }

    /// <summary>Added to a counter's scaled value.</summary>
    [JsonPropertyName("add")]
    public HexNumber? Add { get; set; }

    /// <summary>Hex: a lookup's 2^bits entries, or a hat's 9.</summary>
    [JsonPropertyName("table")]
    public string? Table { get; set; }

    [JsonPropertyName("deadzone")]
    public int? Deadzone { get; set; }

    /// <summary>True when the hat's Y value grows upward.</summary>
    [JsonPropertyName("yUp")]
    public bool? YUp { get; set; }
}

/// <summary>A value read from the state block: <c>{ "bit", "bits" }</c>, N
/// bits at a bit offset, <c>{ "byte" }</c>, the 8 bits of a byte, or
/// <c>{ "word", "order" }</c>, a 16-bit word at a byte offset.</summary>
public sealed class ReadSpec
{
    [JsonPropertyName("bit")]
    public int? Bit { get; set; }

    [JsonPropertyName("bits")]
    public int? Bits { get; set; }

    [JsonPropertyName("byte")]
    public int? Byte { get; set; }

    [JsonPropertyName("word")]
    public int? Word { get; set; }

    [JsonPropertyName("order")]
    public string? Order { get; set; }
}

/// <summary>A number as a profile writes it: a JSON number, a hex string with
/// or without 0x, or true and false for 1 and 0.</summary>
[JsonConverter(typeof(HexNumberConverter))]
public readonly record struct HexNumber(uint Value);

public sealed class HexNumberConverter : JsonConverter<HexNumber>
{
    public override HexNumber Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True: return new HexNumber(1);
            case JsonTokenType.False: return new HexNumber(0);
            case JsonTokenType.Number:
                if (reader.TryGetUInt32(out uint n)) return new HexNumber(n);
                throw new JsonException("A number here is a whole number from 0 to 4294967295.");
            case JsonTokenType.String:
                if (ModelText.TryParseHex(reader.GetString(), out uint h)) return new HexNumber(h);
                throw new JsonException("A string here is hex, such as \"0x30\" or \"30\".");
            default:
                throw new JsonException("Expected a number, a hex string, true or false.");
        }
    }

    public override void Write(Utf8JsonWriter writer, HexNumber value, JsonSerializerOptions options) =>
        writer.WriteStringValue("0x" + value.Value.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>A memory image's size: a JSON number or a hex string, up to
/// 0x100000000, one more than a 32-bit number holds.</summary>
[JsonConverter(typeof(HexSizeConverter))]
public readonly record struct HexSize(ulong Value);

public sealed class HexSizeConverter : JsonConverter<HexSize>
{
    public const ulong Max = 0x1_0000_0000UL;

    public override HexSize Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetUInt64(out ulong n) && n <= Max) return new HexSize(n);
                break;
            case JsonTokenType.String:
                string s = (reader.GetString() ?? "").Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
                if (s.Length is > 0 and <= 9 && ulong.TryParse(s, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out ulong h) && h <= Max)
                    return new HexSize(h);
                break;
        }
        throw new JsonException("A size is a number or a hex string from 1 to 0x100000000.");
    }

    public override void Write(Utf8JsonWriter writer, HexSize value, JsonSerializerOptions options) =>
        writer.WriteStringValue("0x" + value.Value.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>A condition as a profile writes it: one string in the condition
/// grammar, or a list of terms that must all hold.</summary>
[JsonConverter(typeof(ConditionTextConverter))]
public sealed record ConditionText(string Text)
{
    /// <summary>The index of the first null in the list, or -1. The parse
    /// keeps the profile so the create refuses it at the term's path.</summary>
    public int NullTerm { get; init; } = -1;
}

public sealed class ConditionTextConverter : JsonConverter<ConditionText>
{
    public override ConditionText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new ConditionText(reader.GetString() ?? "");
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("A condition is a string, or a list of terms that must all hold.");
        var terms = new List<string>();
        int nullTerm = -1;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                if (nullTerm < 0) nullTerm = terms.Count;
                terms.Add("");
                continue;
            }
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("A condition's list holds strings.");
            terms.Add(reader.GetString() ?? "");
        }
        if (terms.Count == 0) throw new JsonException("A condition's list needs a term.");
        foreach (var t in terms)
            if (t.Contains("||", StringComparison.Ordinal) || t.Contains("&&", StringComparison.Ordinal))
                throw new JsonException("A term in a condition's list joins nothing: write one string to use && or ||.");
        return new ConditionText(string.Join(" && ", terms)) { NullTerm = nullTerm };
    }

    public override void Write(Utf8JsonWriter writer, ConditionText value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Text);
}
