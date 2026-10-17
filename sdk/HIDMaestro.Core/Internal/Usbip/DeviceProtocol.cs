using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using HIDMaestro.Internal.Programs;

namespace HIDMaestro.Internal.Usbip;

/// <summary>Where a protocol's commands come from.</summary>
internal enum CommandSource : byte { None, InterruptOut, BulkOut, SetReport, OutputReport }

/// <summary>Where a protocol's replies wait.</summary>
internal enum ReplyChannel : byte { Input, BulkIn }

/// <summary>What a reply that finds the queue full does.</summary>
internal enum ReplyOverflow : byte { DropNewest, DropOldest, DropOldestUnstarted }

/// <summary>What GET_REPORT(Input) answers.</summary>
internal enum GetReportMode : byte { Input, ReportId, DeviceReport }

/// <summary>A profile's protocol, checked against its descriptors and compiled
/// once when the device is built (design note, mechanism 3). Every name
/// becomes an index, every rule an offset with a mask and value array and a
/// short action list, and every reply a list of parts with fixed offsets, so
/// the device runs on numbers alone. A spec that names something the device
/// lacks is refused with the JSON path of the mistake.</summary>
internal sealed class CompiledProtocol
{
    public const int MaxPorts = 32;
    public const int MaxMemory = 1 << 24;
    public const int MaxLog = 4096;

    public string ProfileId { get; }
    public CompiledModel Model { get; }
    public RegisterTable Registers => Model.Registers;
    /// <summary>The register names, which v1.13.0's one-bit states became.</summary>
    public string[] StateNames => Model.Registers.Names;

    // ── Input ──
    /// <summary>Interrupt IN endpoint the device reports on, 0 when it has none.</summary>
    public byte InputEndpoint { get; }
    public int InputPacketSize { get; }
    /// <summary>When reports flow, null for always.</summary>
    public Condition? InputWhile { get; }
    /// <summary>The host poll period the endpoint descriptor gives, in microseconds.</summary>
    public int DescriptorPollMicros { get; }
    /// <summary>The device's speed as the import reply gives it: 1 low, 2 full, 3 high.</summary>
    public uint Speed { get; }
    /// <summary>The device's own controller poll in milliseconds, 0 when it
    /// has a report for every host poll.</summary>
    public double SourcePollMs { get; }
    public (uint PortsMask, double PeriodMs)[] SourcePollRules { get; }

    /// <summary>True when the device builds its own reports from a state block
    /// and sends them on its own clock.</summary>
    public bool BuildsReports => Model.Layouts.Length > 0;
    public double ClockPeriodMs { get; }
    public ClockOrigin ClockOrigin { get; }
    public MissedTick MissedTick { get; }

    // ── Commands ──
    public CommandSource Source { get; }
    /// <summary>OUT endpoint that takes commands, 0 when none.</summary>
    public byte CommandEndpoint { get; }
    public int CommandPacketSize { get; }
    /// <summary>A SET_REPORT is a command too.</summary>
    public bool CommandsFromSetReport { get; }
    public bool SetReportStripsId { get; }
    public int CommandMinLength { get; }
    /// <summary>No command is raised on OutputReceived.</summary>
    public bool ConsumesCommands { get; }
    public Rule[] Rules { get; }
    public Part[]? ReplyPrefix { get; }
    public byte DefaultStatus { get; }

    // ── Replies ──
    public ReplyChannel Channel { get; }
    public byte ReplyEndpoint { get; }
    public int PendingReplies { get; } = 1;
    public ReplyOverflow Overflow { get; }
    public int MaxReplyLength { get; }

    // ── Memory ──
    public MemoryImage[] Memories { get; }

    // ── Halt and control answers ──
    public bool HasHalt => HaltWhile != null;
    public Condition? HaltWhile { get; }
    /// <summary>The negative Linux status halted transfers fail with.</summary>
    public int HaltStatus { get; }
    public bool AckSetProtocol { get; }
    public bool GetStatusFromDescriptor { get; }
    public GetReportMode GetReport { get; }
    public bool GetReportIdOnly => GetReport == GetReportMode.ReportId;
    public bool SelfPowered { get; }
    public int LogCapacity { get; }

    // ── Ports ──
    public PortLayout? Ports { get; }
    public string[] SnapshotNames { get; }
    /// <summary>The input report the SDK writes, for a device that sends it:
    /// its ID (0 for none) and size.</summary>
    public byte ReportId { get; }
    public int ReportSize { get; }

    internal sealed class Rule
    {
        public int At;
        public byte[] Match = Array.Empty<byte>();
        public byte[] MatchMask = Array.Empty<byte>();
        public int MinLength;
        public int MaxLength = int.MaxValue;
        public (int Reg, uint Value)[] Sets = Array.Empty<(int, uint)>();
        public Store[] Stores = Array.Empty<Store>();
        public Record? StoreRecord;
        public (int Reg, int Byte, int MaskReg)[] Ors = Array.Empty<(int, int, int)>();
        public (int Reg, int Byte, int MaskReg)[] AndNots = Array.Empty<(int, int, int)>();
        public MemoryWrite[] Writes = Array.Empty<MemoryWrite>();
        public bool Consume;
        public int Emit = -1;
        public int Decode = -1;
        public Part[]? Reply;
        public bool UsePrefix = true;
        public byte Status;
        /// <summary>The reply's length, zero padded, or -1 for its parts' own.</summary>
        public int ReplyLength = -1;
    }

    internal readonly record struct Store(int Reg, int Byte, int Bytes, bool BigEndian, uint[]? In, bool Nonzero);

    internal sealed record Record(int At, int Size, int CountByte, int DivideBy, int KeyOffset, byte KeyValue,
                                  (int Reg, int Offset, int Bytes, bool BigEndian)[] Fields);

    internal readonly record struct MemoryWrite(int Memory, NumberSource Address, int Lo, int Hi);

    /// <summary>A number from the command or given.</summary>
    internal readonly record struct NumberSource(int Kind, int Offset, uint Value, int Max)
    {
        public const int Fixed = 0, Byte = 1, Le16 = 2, Le32 = 3;
        public uint Read(ReadOnlySpan<byte> cmd)
        {
            uint v = Kind switch
            {
                Byte => Offset < cmd.Length ? cmd[Offset] : 0u,
                Le16 => Offset + 2 <= cmd.Length ? (uint)(cmd[Offset] | (cmd[Offset + 1] << 8)) : 0u,
                Le32 => Offset + 4 <= cmd.Length ? (uint)(cmd[Offset] | (cmd[Offset + 1] << 8) | (cmd[Offset + 2] << 16) | (cmd[Offset + 3] << 24)) : 0u,
                _ => Value,
            };
            return Max >= 0 && v > (uint)Max ? (uint)Max : v;
        }
        public int Largest => Kind switch
        {
            Fixed => (int)Math.Min(Value, (uint)(Max >= 0 ? Max : int.MaxValue)),
            Byte => Max >= 0 ? Math.Min(Max, 255) : 255,
            Le16 => Max >= 0 ? Math.Min(Max, 65535) : 65535,
            _ => Max >= 0 ? Max : int.MaxValue,
        };
    }

    internal enum PartKind : byte { Hex, State, Snapshot, Echo, Register, Counter, Memory, Bitmap, Input, Value, Status }

    internal sealed class Part
    {
        public PartKind Kind;
        public byte[]? Bytes;            // hex, a value source's resolved bytes are made per engine
        public int Index;                // register, counter, snapshot, memory, layout, value source
        public int Lo, Hi;               // echo, snapshot or input byte range
        public int Max = -1;             // a one-byte echo's ceiling
        public int Width = 1;            // register or counter bytes, or a value's length
        public bool BigEndian;
        public NumberSource Address, Length;   // memory
        public int BitmapByte, BitmapSize;
        public (int Bit, int At, byte[] Hex)[] BitmapSet = Array.Empty<(int, int, byte[])>();
        public string? ValueSource;      // "serial" or "index"
        public string? Encoding;
        public int Add;
        public Condition? While;
        /// <summary>The part's length when it does not depend on the command, else -1.</summary>
        public int FixedLength;
        public int LargestLength;
    }

    /// <summary>A memory image: a size, a fill byte and segments, sorted by
    /// address. A value segment's bytes are resolved per engine.</summary>
    internal sealed record MemoryImage(string Name, int Size, byte Fill, (int At, byte[]? Bytes, string? Value, string? Encoding, int Length, int Add)[] Segments);

    private CompiledProtocol(ControllerProfile profile, UsbDescriptorSet descriptors, CompiledModel model)
    {
        var spec = profile.Protocol!;
        ProfileId = profile.Id;
        Model = model;
        const string root = "$.protocol";
        var regs = model.Registers;

        Ports = PortLayout.Of(profile.ExtendedReport, $"Profile '{profile.Id}': extendedReport", output: false);
        if (Ports != null && Ports.Count > MaxPorts)
            throw Fail($"extendedReport.ports.count {Ports.Count}: a device with a protocol has at most {MaxPorts} ports.");
        SnapshotNames = Ports != null ? new List<string>(Ports.Snapshots).ToArray() : Array.Empty<string>();
        ReportId = profile.ExtendedReport?.ReportIdByte ?? 0;
        ReportSize = profile.ExtendedReport?.Size ?? profile.GetOrBuildReportBuilder().InputReportByteSize;
        Speed = descriptors.Speed;

        // Memory.
        var memories = new List<MemoryImage>();
        if (spec.Memory != null)
            for (int i = 0; i < spec.Memory.Count; i++)
                memories.Add(Memory(spec.Memory[i], $"{root}.memory[{i}]", memories));
        Memories = memories.ToArray();

        // Input.
        if (spec.Input != null)
        {
            string at = $"{root}.input";
            if (spec.Input.Endpoint == null) throw Fail($"{at}.endpoint is missing: a USB/IP device reports on an interrupt IN endpoint.");
            InputEndpoint = Endpoint(descriptors, spec.Input.Endpoint, $"{at}.endpoint", wantIn: true, transferType: 3);
            var ep = descriptors.Endpoints[InputEndpoint];
            InputPacketSize = ep.MaxPacketSize & 0x7FF;
            if (InputPacketSize == 0) throw Fail($"{at}.endpoint 0x{InputEndpoint:X2} has a wMaxPacketSize of 0.");
            DescriptorPollMicros = DescriptorPeriodMicros(ep.Interval, Speed);
            InputWhile = Cond(spec.Input.While, $"{at}.while");
            var clock = spec.Input.Clock;
            SourcePollRules = Array.Empty<(uint, double)>();
            if (BuildsReports)
            {
                if (clock?.PeriodMs is not double period || !(period > 0) || double.IsInfinity(period))
                    throw Fail($"{at}.clock.periodMs: a device that builds its reports sends them on its own clock, so it needs a period above 0.");
                if (clock.SourcePollMs != null || clock.SourcePollRules != null)
                    throw Fail($"{at}.clock: a source poll goes with a device that sends the SDK's report, and this one builds its own.");
                ClockPeriodMs = period;
                ClockOrigin = clock.Origin switch
                {
                    null or "attach" => ClockOrigin.Attach,
                    "inputOpened" => ClockOrigin.InputOpened,
                    _ => throw Fail($"{at}.clock.origin \"{clock.Origin}\" is not one of attach, inputOpened."),
                };
                MissedTick = clock.MissedTick switch
                {
                    null or "owe" => MissedTick.Owe,
                    "nextPoll" => MissedTick.NextPoll,
                    _ => throw Fail($"{at}.clock.missedTick \"{clock.MissedTick}\" is not one of owe, nextPoll."),
                };
            }
            else if (clock != null)
            {
                if (clock.PeriodMs != null || clock.Origin != null || clock.MissedTick != null)
                    throw Fail($"{at}.clock: a period, origin and missedTick go with inputReports, which this device does not declare.");
                SourcePollMs = clock.SourcePollMs ?? 0;
                if (clock.SourcePollMs is double ms && !(ms > 0))
                    throw Fail($"{at}.clock.sourcePollMs must be above 0.");
                var rules = clock.SourcePollRules ?? new List<ProtocolPollRuleSpec>();
                if (rules.Count > 0 && SourcePollMs == 0)
                    throw Fail($"{at}.clock.sourcePollRules need a sourcePollMs to fall back on.");
                SourcePollRules = new (uint, double)[rules.Count];
                for (int i = 0; i < rules.Count; i++)
                {
                    string rat = $"{at}.clock.sourcePollRules[{i}]";
                    if (rules[i] == null) throw Fail($"{rat} is null.");
                    if (rules[i].PortsConnected == null) throw Fail($"{rat}.portsConnected is null.");
                    if (Ports == null) throw Fail($"{rat}.portsConnected needs extendedReport.ports.");
                    if (!(rules[i].PeriodMs > 0)) throw Fail($"{rat}.periodMs must be above 0.");
                    uint mask = 0;
                    foreach (int port in rules[i].PortsConnected)
                    {
                        if (port < 1 || port > Ports.Count)
                            throw Fail($"{rat}.portsConnected names port {port}. The device has ports 1 to {Ports.Count}.");
                        mask |= 1u << (port - 1);
                    }
                    SourcePollRules[i] = (mask, rules[i].PeriodMs);
                }
            }
        }
        else
        {
            SourcePollRules = Array.Empty<(uint, double)>();
            if (BuildsReports) throw Fail($"{root}.input is missing: a device that builds its reports sends them on an input endpoint.");
        }

        // Replies.
        var replies = spec.Replies;
        Channel = replies?.Channel switch
        {
            null or "input" => ReplyChannel.Input,
            "bulkIn" => ReplyChannel.BulkIn,
            _ => throw Fail($"{root}.replies.channel \"{replies!.Channel}\" is not one of input, bulkIn."),
        };
        if (Channel == ReplyChannel.BulkIn)
        {
            if (replies!.Endpoint == null) throw Fail($"{root}.replies.endpoint is missing: bulk replies need a bulk IN endpoint.");
            ReplyEndpoint = Endpoint(descriptors, replies.Endpoint, $"{root}.replies.endpoint", wantIn: true, transferType: 2);
        }
        else if (replies?.Endpoint != null) throw Fail($"{root}.replies.endpoint goes with the bulkIn channel.");
        PendingReplies = replies?.Pending ?? 1;
        if (PendingReplies < 1 || PendingReplies > 1024) throw Fail($"{root}.replies.pending {PendingReplies} is outside 1 to 1024.");
        Overflow = replies?.Overflow switch
        {
            null or "dropNewest" => ReplyOverflow.DropNewest,
            "dropOldest" => ReplyOverflow.DropOldest,
            "dropOldestUnstarted" => ReplyOverflow.DropOldestUnstarted,
            _ => throw Fail($"{root}.replies.overflow \"{replies!.Overflow}\" is not one of dropNewest, dropOldest, dropOldestUnstarted."),
        };

        // Commands.
        var commands = spec.Commands;
        if (commands != null)
        {
            string at = $"{root}.commands";
            if (commands.Endpoint != null)
            {
                byte address = PortLayout.ParseByte(commands.Endpoint, FormException.ProfileText(ProfileId, $"{at}.endpoint"));
                if (!descriptors.Endpoints.TryGetValue(address, out var ep) || ep.IsIn || ep.TransferType is not (2 or 3))
                    throw Fail($"{at}.endpoint 0x{address:X2} is not an interrupt or bulk OUT endpoint of the configuration descriptor.");
                CommandEndpoint = address;
                CommandPacketSize = ep.MaxPacketSize & 0x7FF;
                if (CommandPacketSize == 0) throw Fail($"{at}.endpoint 0x{address:X2} has a wMaxPacketSize of 0.");
                Source = ep.TransferType == 3 ? CommandSource.InterruptOut : CommandSource.BulkOut;
            }
            var named = commands.Source switch
            {
                null => Source,
                "interruptOut" => CommandSource.InterruptOut,
                "bulkOut" => CommandSource.BulkOut,
                "setReport" => CommandSource.SetReport,
                "outputReport" => CommandSource.OutputReport,
                _ => throw Fail($"{at}.source \"{commands.Source}\" is not one of interruptOut, bulkOut, setReport, outputReport."),
            };
            if (named == CommandSource.OutputReport)
                throw Fail($"{at}.source \"outputReport\" is a UMDF2 device's, and this profile runs on USB/IP.");
            if (named == CommandSource.SetReport)
            {
                if (CommandEndpoint != 0) throw Fail($"{at}.source \"setReport\" takes no endpoint: give the endpoint's source and \"setReport\": true for both.");
                Source = CommandSource.SetReport;
                CommandsFromSetReport = true;
            }
            else
            {
                if (named != Source) throw Fail($"{at}.source \"{commands.Source}\" does not match endpoint {commands.Endpoint}, which is {(Source == CommandSource.BulkOut ? "bulk" : Source == CommandSource.InterruptOut ? "interrupt" : "absent")}.");
                if (Source == CommandSource.None) throw Fail($"{at} names no endpoint and no source.");
                CommandsFromSetReport = commands.SetReport;
            }
            SetReportStripsId = commands.SetReportStripsId;
            if (SetReportStripsId && !CommandsFromSetReport)
                throw Fail($"{at}.setReportStripsId applies only to commands from SET_REPORT.");
            CommandMinLength = commands.MinLength ?? 0;
            ConsumesCommands = commands.Consume;
            if (CommandMinLength < 0) throw Fail($"{at}.minLength must be 0 or more.");
            DefaultStatus = commands.Status == null ? (byte)0 : HexByte(commands.Status, $"{at}.status");
            if (commands.ReplyPrefix != null)
                ReplyPrefix = Parts(commands.ReplyPrefix, $"{at}.replyPrefix", CommandMinLength, allowStatus: true);
            if (commands.Rules == null)
                throw Fail($"{at}.rules is null. Give an empty list for a device that ignores every command.");
            Rules = new Rule[commands.Rules.Count];
            for (int i = 0; i < Rules.Length; i++)
            {
                string rat = $"{at}.rules[{i}]";
                Rules[i] = CompileRule(commands.Rules[i] ?? throw Fail($"{rat} is null."), rat, model.RuleEmits.Length > i ? model.RuleEmits[i] : null);
            }

            // An output report written to the HID interface's OUT endpoint
            // reaches the device a packet at a time, and the ports read each
            // packet as a report of its own. A SET_REPORT(Output) reaches it
            // whole, through the protocol or, without setReport, as the
            // report it is. Only a stack that drops the report ID first
            // leaves no path that delivers the report as written, so ports
            // that end past one packet are refused there alone.
            if (Source == CommandSource.InterruptOut)
            {
                var outPorts = PortLayout.Of(profile.ExtendedOutputReport, $"Profile '{profile.Id}': extendedOutputReport", output: true);
                bool noWholeReport = SetReportStripsId
                    && descriptors.Endpoints[CommandEndpoint].InterfaceNumber == descriptors.HidInterfaceNumber;
                if (noWholeReport && outPorts != null && outPorts.BlockAt(outPorts.Count) > CommandPacketSize)
                    throw Fail(
                        $"extendedOutputReport.ports end at byte {outPorts.BlockAt(outPorts.Count)}, past the " +
                        $"{CommandPacketSize}-byte packets {at}.endpoint 0x{CommandEndpoint:X2} delivers, " +
                        "and setReportStripsId drops a SET_REPORT's report ID.");
                if (noWholeReport && profile.ExtendedOutputReport is { } outSpec)
                {
                    var outProg = VendorBlobProgram.Get(outSpec);
                    for (int i = 0; i < outProg.Fields.Length; i++)
                    {
                        ref readonly var f = ref outProg.Fields[i];
                        if (f.Op == VendorBlobProgram.FieldOp.Crc32 && f.CrcDst < 0) continue;
                        int end = VendorBlobProgram.Extent(in f, outSpec.Size).Hi + 1;
                        if (end > CommandPacketSize)
                            throw Fail(
                                $"extendedOutputReport.fields[{i}] ('{f.Src.Type}') ends at byte {end}, past the " +
                                $"{CommandPacketSize}-byte packets {at}.endpoint 0x{CommandEndpoint:X2} delivers, " +
                                "and setReportStripsId drops a SET_REPORT's report ID.");
                    }
                }
            }
        }
        else
        {
            Rules = Array.Empty<Rule>();
        }
        foreach (var r in Rules)
        {
            if (r.Reply == null) continue;
            if (Channel == ReplyChannel.Input && InputEndpoint == 0)
                throw Fail($"{root}.commands has a rule with a reply, and the protocol declares no input endpoint to send it on.");
            MaxReplyLength = Math.Max(MaxReplyLength, Largest(r));
        }

        // Halt.
        if (spec.Halt != null)
        {
            if (spec.Halt.While == null) throw Fail($"{root}.halt.while names no register, so the device would never answer.");
            HaltWhile = Cond(spec.Halt.While, $"{root}.halt.while");
            HaltStatus = spec.Halt.Status switch
            {
                "EPROTO" => -UsbipProtocol.EProto,
                "EPIPE" => -UsbipProtocol.EPipe,
                _ => throw Fail($"{root}.halt.status \"{spec.Halt.Status}\" is not one of EPROTO, EPIPE."),
            };
        }

        AckSetProtocol = spec.SetProtocol switch
        {
            null or "stall" => false,
            "ack" => true,
            _ => throw Fail($"{root}.setProtocol \"{spec.SetProtocol}\" is not one of ack, stall."),
        };
        GetStatusFromDescriptor = spec.GetStatus switch
        {
            null => false,
            "descriptor" => true,
            _ => throw Fail($"{root}.getStatus \"{spec.GetStatus}\" is not \"descriptor\"."),
        };
        GetReport = spec.GetReport switch
        {
            null or "input" => GetReportMode.Input,
            "reportId" => GetReportMode.ReportId,
            "deviceReport" => GetReportMode.DeviceReport,
            _ => throw Fail($"{root}.getReport \"{spec.GetReport}\" is not one of input, reportId, deviceReport."),
        };
        if (GetReport == GetReportMode.DeviceReport && !BuildsReports)
            throw Fail($"{root}.getReport \"deviceReport\" builds a layout, and the profile declares no inputReports.");
        LogCapacity = spec.Log ?? 0;
        if (LogCapacity < 0 || LogCapacity > MaxLog) throw Fail($"{root}.log {LogCapacity} is outside 0 to {MaxLog}.");
        SelfPowered = (descriptors.ConfigurationDescriptor[7] & 0x40) != 0;
    }

    /// <summary>The protocol of <paramref name="profile"/>, or null when it
    /// declares none.</summary>
    public static CompiledProtocol? Compile(ControllerProfile profile, UsbDescriptorSet descriptors)
    {
        if (profile.Protocol == null) return null;
        CompiledModel model;
        try { model = profile.GetOrCompileModel(); }
        catch (FormException e) { throw new InvalidOperationException(e.ForProfile(profile.Id), e); }
        return new CompiledProtocol(profile, descriptors, model);
    }

    /// <summary>The poll period Windows gives an interrupt endpoint, as the
    /// tables under USB_ENDPOINT_DESCRIPTOR's bInterval give it (Microsoft
    /// Learn), at a USB/IP speed: 1 low, 2 full, 3 high. Low speed: 8 frames
    /// of 1 ms for bInterval 0 to 15, 16 for 16 to 35, 32 beyond. Full speed:
    /// bInterval frames rounded down to a power of two, at most 32. High
    /// speed: 2^(bInterval-1) microframes of 125 microseconds, at most 32 of
    /// them.</summary>
    internal static int DescriptorPeriodMicros(byte bInterval, uint speed)
    {
        if (speed >= 3)
            return 125 << (Math.Clamp((int)bInterval, 1, 6) - 1);
        if (speed == 1)
            return (bInterval <= 15 ? 8 : bInterval <= 35 ? 16 : 32) * 1000;
        int ms = Math.Max(1, (int)bInterval);
        int pow = 1;
        while (pow * 2 <= ms && pow < 32) pow *= 2;
        return pow * 1000;
    }

    private MemoryImage Memory(MemorySpec? m, string at, List<MemoryImage> earlier)
    {
        if (m == null) throw Fail($"{at} is null.");
        if (string.IsNullOrWhiteSpace(m.Name)) throw Fail($"{at}.name: a memory image needs a name.");
        foreach (var e in earlier) if (e.Name == m.Name) throw Fail($"{at}.name '{m.Name}' names two memory images.");
        if (m.Size.Value < 1 || m.Size.Value > MaxMemory) throw Fail($"{at}.size 0x{m.Size.Value:X} is outside 1 to 0x{MaxMemory:X}.");
        int size = (int)m.Size.Value;
        byte fill = m.Fill == null ? (byte)0 : HexByte(m.Fill, $"{at}.fill");
        var segs = new List<(int, byte[]?, string?, string?, int, int)>();
        if (m.Segments != null)
        {
            for (int i = 0; i < m.Segments.Count; i++)
            {
                string sat = $"{at}.segments[{i}]";
                var s = m.Segments[i] ?? throw Fail($"{sat} is null.");
                int start = (int)Math.Min(s.At.Value, int.MaxValue);
                if ((s.Hex == null) == (s.Value == null)) throw Fail($"{sat} gives exactly one of hex and value.");
                if (s.Hex != null)
                {
                    if (s.Length != null || s.Encoding != null || s.Add != null) throw Fail($"{sat}: length, encoding and add go with a value segment.");
                    var bytes = Bytes(s.Hex, $"{sat}.hex");
                    if ((long)start + bytes.Length > size) throw Fail($"{sat} at 0x{start:X} runs past the {size}-byte image.");
                    segs.Add((start, bytes, null, null, bytes.Length, 0));
                }
                else
                {
                    int length = ValueSourceLength(s.Value!, s.Encoding, s.Length, sat);
                    if ((long)start + length > size) throw Fail($"{sat} at 0x{start:X} runs past the {size}-byte image.");
                    segs.Add((start, null, s.Value, s.Encoding, length, s.Add ?? 0));
                }
            }
        }
        segs.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        for (int i = 1; i < segs.Count; i++)
            if (segs[i - 1].Item1 + segs[i - 1].Item5 > segs[i].Item1)
                throw Fail($"{at}.segments: the segment at 0x{segs[i - 1].Item1:X} overlaps the one at 0x{segs[i].Item1:X}.");
        return new MemoryImage(m.Name, size, fill, segs.ToArray());
    }

    private int ValueSourceLength(string source, string? encoding, int? length, string at)
    {
        if (source is not ("serial" or "index")) throw Fail($"{at}.value \"{source}\" is not one of serial, index.");
        int natural = encoding switch
        {
            null or "ascii" => -1,
            "uint8" => 1,
            "uint16-le" => 2,
            "uint32-le" => 4,
            _ => throw Fail($"{at}.encoding \"{encoding}\" is not one of ascii, uint8, uint16-le, uint32-le."),
        };
        if (natural < 0)
        {
            if (length is not int n || n < 1 || n > 256) throw Fail($"{at}.length: an ascii value needs a length of 1 to 256 bytes.");
            return n;
        }
        if (length != null && length != natural) throw Fail($"{at}.length {length} does not match the {natural}-byte {encoding}.");
        return natural;
    }

    /// <summary>The bytes of a value source for one device.</summary>
    internal static byte[] ValueBytes(string source, string? encoding, int length, int add, string? serial, int index)
    {
        var b = new byte[length];
        if (encoding is null or "ascii")
        {
            string text = source == "serial" ? serial ?? "" : (index + add).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var ascii = Encoding.ASCII.GetBytes(text);
            Array.Copy(ascii, b, Math.Min(ascii.Length, b.Length));
            return b;
        }
        long v = source == "index" ? index : long.TryParse(serial, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long s) ? s : 0;
        v += add;
        for (int i = 0; i < length; i++) b[i] = (byte)(v >> (8 * i));
        return b;
    }

    private Rule CompileRule(ProtocolRuleSpec r, string at, EmitForm? emit)
    {
        var rule = new Rule
        {
            At = r.At ?? 0,
            MinLength = Math.Max(r.MinLength ?? 0, CommandMinLength),
            MaxLength = r.MaxLength ?? int.MaxValue,
        };
        if (rule.At < 0) throw Fail($"{at}.at must be 0 or more.");
        ParseMatch(r.Match, $"{at}.match", out rule.Match, out rule.MatchMask);
        if (rule.Match.Length == 0) throw Fail($"{at}.match is empty. A rule matches the bytes a command holds.");
        if (rule.MaxLength < rule.MinLength)
            throw Fail($"{at}: minLength {rule.MinLength} and maxLength {r.MaxLength} admit no command.");
        if (rule.MaxLength < rule.At + rule.Match.Length)
            throw Fail($"{at}.maxLength {rule.MaxLength} is shorter than its match, which ends at byte {rule.At + rule.Match.Length}.");
        // Commands from an interrupt endpoint arrive as packets of its
        // wMaxPacketSize, so a rule that needs more bytes than one packet
        // holds never matches.
        if (Source == CommandSource.InterruptOut)
        {
            if (rule.At + rule.Match.Length > CommandPacketSize)
                throw Fail($"{at}.match ends at byte {rule.At + rule.Match.Length}, past the {CommandPacketSize}-byte packets " +
                           $"endpoint 0x{CommandEndpoint:X2} delivers, so it never matches.");
            if (rule.MinLength > CommandPacketSize)
                throw Fail($"{at}.minLength {rule.MinLength} is longer than the {CommandPacketSize}-byte packets " +
                           $"endpoint 0x{CommandEndpoint:X2} delivers, so it never matches.");
        }
        var regs = Model.Registers;
        int Reg(string name, string where)
        {
            int i = regs.IndexOf(name);
            if (i < 0) throw Fail($"{where} names '{name}', which protocol.state does not declare.");
            return i;
        }
        if (r.Set != null)
        {
            var sets = new List<(int, uint)>();
            foreach (var (name, value) in r.Set)
            {
                int i = Reg(name, $"{at}.set");
                if ((value.Value & ~regs.WidthMask(i)) != 0) throw Fail($"{at}.set.{name} 0x{value.Value:X} does not fit {regs.Bits[i]} bits.");
                sets.Add((i, value.Value));
            }
            rule.Sets = sets.ToArray();
        }
        if (r.Store != null)
        {
            var stores = new List<Store>();
            foreach (var (name, s) in r.Store)
            {
                string sat = $"{at}.store.{name}";
                int i = Reg(name, $"{at}.store");
                if (s == null) throw Fail($"{sat} is null.");
                int bytes = s.Bytes ?? 1;
                if (bytes < 1 || bytes > 4) throw Fail($"{sat}.bytes is 1 to 4.");
                if (s.Byte < 0) throw Fail($"{sat}.byte must be 0 or more.");
                if (s.Byte + bytes > rule.MinLength) throw Fail($"{sat}: bytes {s.Byte} to {s.Byte + bytes - 1} are past the rule's minLength {rule.MinLength}, so a shorter command would store nothing it holds.");
                bool nonzero = s.Nonzero ?? false;
                if (!nonzero && bytes * 8 > regs.Bits[i] && regs.Bits[i] < 32) throw Fail($"{sat}: {bytes} bytes is wider than the {regs.Bits[i]}-bit register.");
                uint[]? set = null;
                if (s.In != null)
                {
                    if (nonzero) throw Fail($"{sat}: in and nonzero do not go together.");
                    set = new uint[s.In.Count];
                    for (int k = 0; k < set.Length; k++)
                        if (!ModelText.TryParseHex(s.In[k], out set[k]) || (set[k] & ~regs.WidthMask(i)) != 0)
                            throw Fail($"{sat}.in[{k}] \"{s.In[k]}\" is not a hex value the register holds.");
                }
                stores.Add(new Store(i, s.Byte, bytes, Order(s.Order, $"{sat}.order"), set, nonzero));
            }
            rule.Stores = stores.ToArray();
        }
        if (r.StoreRecord is { } rec)
        {
            string rat = $"{at}.storeRecord";
            if (rec.Size < 1 || rec.At < 0) throw Fail($"{rat}: records start at 0 or later and are 1 byte or more.");
            int divide = rec.Count?.DivideBy ?? 1;
            if (divide < 1) throw Fail($"{rat}.count.divideBy must be 1 or more.");
            if (rec.Count == null || rec.Count.Byte < 0) throw Fail($"{rat}.count.byte must be 0 or more.");
            if (rec.Count.Byte >= rule.MinLength) throw Fail($"{rat}.count.byte {rec.Count.Byte} is past the rule's minLength {rule.MinLength}.");
            if (rec.Key == null || rec.Key.Offset < 0 || rec.Key.Offset >= rec.Size) throw Fail($"{rat}.key.offset must be inside the {rec.Size}-byte record.");
            byte key = HexByte(rec.Key.Value, $"{rat}.key.value");
            if (rec.Store == null || rec.Store.Count == 0) throw Fail($"{rat}.store names no register.");
            var fields = new List<(int, int, int, bool)>();
            foreach (var (name, f) in rec.Store)
            {
                string fat = $"{rat}.store.{name}";
                int i = Reg(name, $"{rat}.store");
                int bytes = f?.Bytes ?? 1;
                if (f == null || f.Offset < 0 || bytes < 1 || bytes > 4 || f.Offset + bytes > rec.Size) throw Fail($"{fat}: 1 to 4 bytes inside the {rec.Size}-byte record.");
                if (bytes * 8 > regs.Bits[i] && regs.Bits[i] < 32) throw Fail($"{fat}: {bytes} bytes is wider than the {regs.Bits[i]}-bit register.");
                fields.Add((i, f.Offset, bytes, Order(f.Order, $"{fat}.order")));
            }
            rule.StoreRecord = new Record(rec.At, rec.Size, rec.Count.Byte, divide, rec.Key.Offset, key, fields.ToArray());
        }
        (int, int, int)[] Masked(Dictionary<string, MaskedStoreSpec>? d, string kind)
        {
            if (d == null) return Array.Empty<(int, int, int)>();
            var list = new List<(int, int, int)>();
            foreach (var (name, m) in d)
            {
                string mat = $"{at}.{kind}.{name}";
                if (m == null) throw Fail($"{mat} is null.");
                if (m.Byte < 0 || m.Byte >= rule.MinLength) throw Fail($"{mat}.byte {m.Byte} is past the rule's minLength {rule.MinLength}.");
                list.Add((Reg(name, $"{at}.{kind}"), m.Byte, Reg(m.Mask, $"{mat}.mask")));
            }
            return list.ToArray();
        }
        rule.Ors = Masked(r.Or, "or");
        rule.AndNots = Masked(r.AndNot, "andNot");
        if (r.Write != null)
        {
            var writes = new List<MemoryWrite>();
            for (int k = 0; k < r.Write.Count; k++)
            {
                string wat = $"{at}.write[{k}]";
                var w = r.Write[k] ?? throw Fail($"{wat} is null.");
                int mem = MemoryIndex(w.Memory, $"{wat}.memory");
                if (!ModelText.TryParseRange(w.From, out int lo, out int hi) || hi >= rule.MinLength)
                    throw Fail($"{wat}.from \"{w.From}\" is not a range inside the rule's minLength {rule.MinLength}.");
                writes.Add(new MemoryWrite(mem, Number(w.Address, $"{wat}.address", rule.MinLength), lo, hi));
            }
            rule.Writes = writes.ToArray();
        }
        rule.Consume = r.Consume ?? false;
        if (emit != null) rule.Emit = Array.IndexOf(Model.RuleEmits, emit);
        if (r.Decode != null)
        {
            rule.Decode = Array.FindIndex(Model.Decoders, d => d.Name == r.Decode);
            if (rule.Decode < 0) throw Fail($"{at}.decode '{r.Decode}' is not one of protocol.decoders.");
            if (Model.Decoders[rule.Decode].MinLength > rule.MinLength && r.MinLength != null)
                throw Fail($"{at}.decode '{r.Decode}' reads {Model.Decoders[rule.Decode].MinLength} bytes, past the rule's minLength {rule.MinLength}.");
        }
        rule.UsePrefix = r.Prefix ?? true;
        rule.Status = r.Status == null ? DefaultStatus : HexByte(r.Status, $"{at}.status");
        if (r.Reply != null)
        {
            rule.Reply = Parts(r.Reply, $"{at}.reply", rule.MinLength, allowStatus: !rule.UsePrefix || ReplyPrefix == null);
            int own = 0;
            foreach (var p in rule.Reply) own += p.LargestLength;
            if (own == 0 && (ReplyPrefix == null || !rule.UsePrefix)) throw Fail($"{at}.reply is empty. Leave reply out for a command with no answer.");
            if (r.ReplyLength is int len)
            {
                if (len < Largest(rule)) throw Fail($"{at}.replyLength {len} is shorter than the reply's parts, {Largest(rule)} bytes.");
                rule.ReplyLength = len;
            }
            if (Channel == ReplyChannel.Input && Largest(rule) > ReplyLimit)
                throw Fail($"{at}.reply is {Largest(rule)} bytes, longer than the {ReplyLimit}-byte reports its channel carries.");
        }
        else if (r.ReplyLength != null || r.Status != null || r.Prefix != null)
            throw Fail($"{at}: replyLength, status and prefix go with a reply.");
        return rule;
    }

    /// <summary>The longest reply the input channel carries: the SDK frame's
    /// size, past which the input report path's buffers end.</summary>
    private int ReplyLimit => Math.Max(Math.Max(ReportSize, SharedMemoryIO.EXTENDED_DATA_CAPACITY), SharedMemoryIO.DATA_CAPACITY + 1);

    /// <summary>The longest the rule's reply can be.</summary>
    internal int Largest(Rule r)
    {
        if (r.Reply == null) return 0;
        int n = 0;
        if (r.UsePrefix && ReplyPrefix != null) foreach (var p in ReplyPrefix) n += p.LargestLength;
        foreach (var p in r.Reply) n += p.LargestLength;
        return Math.Max(n, r.ReplyLength);
    }

    private Part[] Parts(List<ReplyPartSpec> specs, string at, int minLength, bool allowStatus)
    {
        var parts = new Part[specs.Count];
        var regs = Model.Registers;
        for (int i = 0; i < parts.Length; i++)
        {
            string pat = $"{at}[{i}]";
            var p = specs[i] ?? throw Fail($"{pat} is null.");
            int kinds = (p.Hex != null ? 1 : 0) + (p.State != null ? 1 : 0) + (p.Snapshot != null ? 1 : 0) + (p.Echo != null ? 1 : 0)
                      + (p.Register != null ? 1 : 0) + (p.Counter != null ? 1 : 0) + (p.Memory != null ? 1 : 0) + (p.Bitmap != null ? 1 : 0)
                      + (p.Input != null ? 1 : 0) + (p.Value != null ? 1 : 0) + (p.Status == true ? 1 : 0);
            if (kinds != 1)
                throw Fail($"{pat} must give exactly one of hex, state, snapshot, echo, register, counter, memory, bitmap, input, value and status.");
            var part = new Part { While = Cond(p.While, $"{pat}.while") };
            void Only(params string[] keys)
            {
                void Check(object? v, string k) { if (v != null && Array.IndexOf(keys, k) < 0) throw Fail($"{pat}.{k} does not go with this part."); }
                Check(p.Bytes, "bytes"); Check(p.Max, "max"); Check(p.Width, "width"); Check(p.Order, "order");
                Check(p.Address, "address"); Check(p.Length, "length"); Check(p.Encoding, "encoding"); Check(p.Add, "add");
            }
            if (p.Hex != null)
            {
                Only();
                part.Kind = PartKind.Hex;
                part.Bytes = Bytes(p.Hex, $"{pat}.hex");
                part.FixedLength = part.Bytes.Length;
            }
            else if (p.State != null)
            {
                Only();
                part.Kind = PartKind.State;
                part.Index = regs.IndexOf(p.State);
                if (part.Index < 0) throw Fail($"{pat}.state names '{p.State}', which protocol.state does not declare.");
                part.FixedLength = 1;
            }
            else if (p.Snapshot != null)
            {
                Only("bytes");
                if (Ports == null) throw Fail($"{pat}.snapshot needs extendedReport.ports.");
                part.Kind = PartKind.Snapshot;
                part.Index = Array.IndexOf(SnapshotNames, p.Snapshot);
                if (part.Index < 0) throw Fail($"{pat}.snapshot '{p.Snapshot}' is not one of extendedReport.ports.snapshots.");
                int lo = 0, hi = Ports.Stride - 1;
                if (p.Bytes != null && (!PortLayout.TryParseRange(p.Bytes, out lo, out hi) || lo < 0 || hi >= Ports.Stride || lo > hi))
                    throw Fail($"{pat}.bytes \"{p.Bytes}\" must be a range inside the {Ports.Stride}-byte port block.");
                part.Lo = lo; part.Hi = hi;
                part.FixedLength = Ports.Count * (hi - lo + 1);
            }
            else if (p.Echo != null)
            {
                Only("max");
                part.Kind = PartKind.Echo;
                if (!ModelText.TryParseRange(p.Echo, out part.Lo, out part.Hi)) throw Fail($"{pat}.echo \"{p.Echo}\" is not a byte range such as \"10-15\".");
                if (part.Hi >= minLength) throw Fail($"{pat}.echo \"{p.Echo}\" reaches byte {part.Hi}, past the minLength {minLength} every command it answers has.");
                if (p.Max != null)
                {
                    if (part.Lo != part.Hi) throw Fail($"{pat}.max goes with a one-byte echo.");
                    if (p.Max < 0 || p.Max > 255) throw Fail($"{pat}.max is 0 to 255.");
                    part.Max = p.Max.Value;
                }
                part.FixedLength = part.Hi - part.Lo + 1;
            }
            else if (p.Register != null)
            {
                Only("width", "order");
                part.Kind = PartKind.Register;
                part.Index = regs.IndexOf(p.Register);
                if (part.Index < 0) throw Fail($"{pat}.register names '{p.Register}', which protocol.state does not declare.");
                part.Width = p.Width ?? 1;
                if (part.Width < 1 || part.Width > 4) throw Fail($"{pat}.width is 1 to 4 bytes.");
                part.BigEndian = Order(p.Order, $"{pat}.order");
                part.FixedLength = part.Width;
            }
            else if (p.Counter != null)
            {
                Only("width", "order");
                part.Kind = PartKind.Counter;
                part.Index = Array.IndexOf(Model.CounterNames, p.Counter);
                if (part.Index < 0) throw Fail($"{pat}.counter names '{p.Counter}', which protocol.counters does not declare.");
                part.Width = p.Width ?? 1;
                if (part.Width < 1 || part.Width > 4) throw Fail($"{pat}.width is 1 to 4 bytes.");
                part.BigEndian = Order(p.Order, $"{pat}.order");
                part.FixedLength = part.Width;
            }
            else if (p.Memory != null)
            {
                Only("address", "length");
                part.Kind = PartKind.Memory;
                part.Index = MemoryIndex(p.Memory, $"{pat}.memory");
                part.Address = Number(p.Address ?? throw Fail($"{pat}.address is missing."), $"{pat}.address", minLength);
                part.Length = Number(p.Length ?? throw Fail($"{pat}.length is missing."), $"{pat}.length", minLength);
                part.FixedLength = part.Length.Kind == NumberSource.Fixed ? part.Length.Largest : -1;
                if (part.Length.Largest > 4096) throw Fail($"{pat}.length can reach {part.Length.Largest} bytes: give it a max of 4096 or less.");
            }
            else if (p.Bitmap != null)
            {
                Only();
                var b = p.Bitmap;
                part.Kind = PartKind.Bitmap;
                if (b.Byte < 0) throw Fail($"{pat}.bitmap.byte must be 0 or more.");
                if (b.Size < 1 || b.Size > 256) throw Fail($"{pat}.bitmap.size is 1 to 256.");
                part.BitmapByte = b.Byte; part.BitmapSize = b.Size;
                var set = new List<(int, int, byte[])>();
                for (int k = 0; k < (b.Set?.Count ?? 0); k++)
                {
                    var e = b.Set![k] ?? throw Fail($"{pat}.bitmap.set[{k}] is null.");
                    var hex = Bytes(e.Hex, $"{pat}.bitmap.set[{k}].hex");
                    if (e.Bit < 0 || e.Bit > 7) throw Fail($"{pat}.bitmap.set[{k}].bit is 0 to 7.");
                    if (e.At < 0 || e.At + hex.Length > b.Size) throw Fail($"{pat}.bitmap.set[{k}] runs past the {b.Size}-byte bitmap.");
                    set.Add((e.Bit, e.At, hex));
                }
                part.BitmapSet = set.ToArray();
                part.FixedLength = b.Size;
            }
            else if (p.Input != null)
            {
                Only("bytes");
                part.Kind = PartKind.Input;
                part.Index = Array.FindIndex(Model.Layouts, l => l.Name == p.Input);
                if (part.Index < 0) throw Fail($"{pat}.input '{p.Input}' is not one of inputReports.");
                int size = Model.Layouts[part.Index].Size;
                int lo = 0, hi = size - 1;
                if (p.Bytes != null && (!ModelText.TryParseRange(p.Bytes, out lo, out hi) || hi >= size))
                    throw Fail($"{pat}.bytes \"{p.Bytes}\" must be a range inside the {size}-byte layout.");
                part.Lo = lo; part.Hi = hi;
                part.FixedLength = hi - lo + 1;
            }
            else if (p.Value != null)
            {
                Only("length", "encoding", "add");
                part.Kind = PartKind.Value;
                part.ValueSource = p.Value;
                part.Encoding = p.Encoding;
                part.Add = p.Add ?? 0;
                if (p.Length != null && (p.Length.Value == null || p.Length.Byte != null || p.Length.Le16 != null || p.Length.Le32 != null))
                    throw Fail($"{pat}.length of a value is a fixed number.");
                part.Width = ValueSourceLength(p.Value, p.Encoding, p.Length?.Value is { } lv ? (int)lv.Value : null, pat);
                part.FixedLength = part.Width;
            }
            else
            {
                Only();
                if (!allowStatus) throw Fail($"{pat}.status: the prefix writes the status, so a rule's own reply does not.");
                part.Kind = PartKind.Status;
                part.FixedLength = 1;
            }
            part.LargestLength = part.FixedLength >= 0 ? part.FixedLength : part.Length.Largest;
            parts[i] = part;
        }
        return parts;
    }

    private NumberSource Number(ByteSourceSpec s, string at, int minLength)
    {
        int given = (s.Le32 != null ? 1 : 0) + (s.Le16 != null ? 1 : 0) + (s.Byte != null ? 1 : 0) + (s.Value != null ? 1 : 0);
        if (given != 1) throw Fail($"{at} gives exactly one of le32, le16, byte and value.");
        int max = s.Max ?? -1;
        if (s.Max is < 0) throw Fail($"{at}.max must be 0 or more.");
        if (s.Value is HexNumber v) return new NumberSource(NumberSource.Fixed, 0, v.Value, max);
        int kind = s.Le32 != null ? NumberSource.Le32 : s.Le16 != null ? NumberSource.Le16 : NumberSource.Byte;
        int offset = s.Le32 ?? s.Le16 ?? s.Byte ?? 0;
        int width = kind == NumberSource.Le32 ? 4 : kind == NumberSource.Le16 ? 2 : 1;
        if (offset < 0 || offset + width > minLength)
            throw Fail($"{at} reads bytes {offset} to {offset + width - 1}, past the minLength {minLength} every command it answers has.");
        return new NumberSource(kind, offset, 0, max);
    }

    private int MemoryIndex(string name, string at)
    {
        for (int i = 0; i < Memories.Length; i++) if (Memories[i].Name == name) return i;
        throw Fail($"{at} '{name}' is not one of protocol.memory.");
    }

    private Condition? Cond(ConditionText? text, string at)
    {
        if (text == null) return null;
        try { return ConditionParser.Parse(text.Text, Model.Registers, at); }
        catch (FormException e) { throw Fail($"{e.Path}: {e.Reason}", e); }
    }

    private void ParseMatch(string text, string at, out byte[] match, out byte[] mask)
    {
        var bytes = new List<byte>();
        var masks = new List<byte>();
        foreach (var tok in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok == "..") { bytes.Add(0); masks.Add(0); continue; }
            if (!ModelText.TryParseBytes(tok, out var b))
                throw Fail($"{at} \"{tok}\" is neither hex bytes nor ..");
            foreach (byte x in b) { bytes.Add(x); masks.Add(0xFF); }
        }
        match = bytes.ToArray();
        mask = masks.ToArray();
    }

    private byte Endpoint(UsbDescriptorSet d, string hex, string at, bool wantIn, int transferType)
    {
        byte address = PortLayout.ParseByte(hex, FormException.ProfileText(ProfileId, at));
        if (!d.Endpoints.TryGetValue(address, out var ep))
            throw Fail($"{at} 0x{address:X2} is not an endpoint of the configuration descriptor.");
        if (ep.TransferType != transferType || ep.IsIn != wantIn)
            throw Fail($"{at} 0x{address:X2} must be {(transferType == 3 ? "an interrupt" : "a bulk")} {(wantIn ? "IN" : "OUT")} endpoint.");
        return address;
    }

    private bool Order(string? order, string at) => order switch
    {
        null or "le" => false,
        "be" => true,
        _ => throw Fail($"{at} \"{order}\" is not le or be."),
    };

    private byte HexByte(string text, string at) =>
        ModelText.TryParseBytes(text, out var b) && b.Length == 1 ? b[0] : throw Fail($"{at} \"{text}\" is not one hex byte.");

    private byte[] Bytes(string? text, string at) =>
        ModelText.TryParseBytes(text, out var b) ? b : throw Fail($"{at} \"{text}\" is not hex bytes.");

    private InvalidOperationException Fail(string message, Exception? inner = null) => new(FormException.ProfileText(ProfileId, message), inner);
}
