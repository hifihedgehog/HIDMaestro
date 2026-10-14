using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace HIDMaestro.Internal.Usbip;

/// <summary>A condition on a device's protocol state: each state it names
/// has the value it gives. Compiled to a mask and the values under it, so a
/// test is one AND and one compare.</summary>
internal readonly struct StateCondition
{
    public readonly ulong Mask;
    public readonly ulong Value;

    public StateCondition(ulong mask, ulong value)
    {
        Mask = mask;
        Value = value;
    }

    public bool Holds(ulong state) => (state & Mask) == Value;
}

/// <summary>A profile's <see cref="DeviceProtocolSpec"/>, checked against its
/// descriptors and compiled once when the device is built. Every name becomes
/// a bit, every rule a byte pattern and two masks, and every reply a list of
/// parts with fixed offsets, so the device's endpoints run on numbers alone.
/// A spec that names something the device lacks is refused with the JSON path
/// of the mistake.</summary>
internal sealed class CompiledProtocol
{
    public const int MaxStates = 64;

    public string ProfileId { get; }
    public string[] StateNames { get; }
    public ulong InitialState { get; }

    /// <summary>Interrupt IN endpoint the device reports on, 0 when it has
    /// none.</summary>
    public byte InputEndpoint { get; }
    public int InputPacketSize { get; }
    public StateCondition InputWhile { get; }

    /// <summary>The host poll period the endpoint descriptor gives, in
    /// microseconds.</summary>
    public int DescriptorPollMicros { get; }
    public bool HighSpeed { get; }

    /// <summary>The device's own controller poll in milliseconds, 0 when it
    /// has a report for every host poll.</summary>
    public double SourcePollMs { get; }
    public (uint PortsMask, double PeriodMs)[] SourcePollRules { get; }

    /// <summary>Interrupt OUT endpoint that takes commands, 0 when none.</summary>
    public byte CommandEndpoint { get; }
    public int CommandPacketSize { get; }
    public bool CommandsFromSetReport { get; }
    public Rule[] Rules { get; }

    public bool HasHalt { get; }
    public StateCondition HaltWhile { get; }
    /// <summary>The negative Linux status halted transfers fail with.</summary>
    public int HaltStatus { get; }

    public bool AckSetProtocol { get; }
    public bool GetStatusFromDescriptor { get; }
    public bool SelfPowered { get; }

    public PortLayout? Ports { get; }
    public string[] SnapshotNames { get; }

    /// <summary>The input report the SDK writes: its ID (0 for none) and
    /// size.</summary>
    public byte ReportId { get; }
    public int ReportSize { get; }

    public int MaxReplyLength { get; }

    internal sealed class Rule
    {
        public byte[] Match = Array.Empty<byte>();
        public int MinLength;
        public int MaxLength = int.MaxValue;
        public ulong SetMask;
        public ulong SetValue;
        public ReplyPart[]? Reply;
        public int ReplyLength;
    }

    internal enum PartKind : byte { Hex, State, Snapshot }

    internal readonly struct ReplyPart
    {
        public readonly PartKind Kind;
        public readonly byte[]? Hex;
        public readonly int Index;          // state bit, or snapshot index
        public readonly int Lo, Hi;         // snapshot bytes within a block
        public readonly StateCondition While;

        public ReplyPart(PartKind kind, byte[]? hex, int index, int lo, int hi, StateCondition when)
        {
            Kind = kind; Hex = hex; Index = index; Lo = lo; Hi = hi; While = when;
        }
    }

    private CompiledProtocol(ControllerProfile profile, UsbDescriptorSet descriptors)
    {
        var spec = profile.UsbConfiguration!.Protocol!;
        ProfileId = profile.Id;
        const string root = "usbConfiguration.protocol";

        // State.
        if (spec.State.Count > MaxStates)
            throw Fail($"{root}.state declares {spec.State.Count} states. The limit is {MaxStates}.");
        StateNames = new string[spec.State.Count];
        ulong initial = 0;
        for (int i = 0; i < spec.State.Count; i++)
        {
            string name = spec.State[i].Name;
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('!'))
                throw Fail($"{root}.state[{i}] needs a name that does not start with '!'.");
            if (Array.IndexOf(StateNames, name, 0, i) >= 0)
                throw Fail($"{root}.state names '{name}' twice.");
            StateNames[i] = name;
            if (spec.State[i].Initial) initial |= 1UL << i;
        }
        InitialState = initial;

        Ports = PortLayout.Of(profile.ExtendedReport, $"Profile '{profile.Id}': extendedReport");
        SnapshotNames = Ports != null ? new List<string>(Ports.Snapshots).ToArray() : Array.Empty<string>();
        ReportId = profile.ExtendedReport?.ReportIdByte ?? 0;
        ReportSize = profile.ExtendedReport?.Size ?? profile.GetOrBuildReportBuilder().InputReportByteSize;

        // Input.
        if (spec.Input != null)
        {
            InputEndpoint = Endpoint(descriptors, spec.Input.Endpoint, $"{root}.input.endpoint", wantIn: true);
            var ep = descriptors.Endpoints[InputEndpoint];
            InputPacketSize = ep.MaxPacketSize & 0x7FF;
            if (InputPacketSize == 0)
                throw Fail($"{root}.input.endpoint 0x{InputEndpoint:X2} has a wMaxPacketSize of 0.");
            HighSpeed = descriptors.Speed >= 3;
            DescriptorPollMicros = DescriptorPeriodMicros(ep.Interval, HighSpeed);
            InputWhile = Condition(spec.Input.While, $"{root}.input.while");

            var clock = spec.Input.Clock;
            if (clock != null)
            {
                SourcePollMs = clock.SourcePollMs ?? 0;
                if (clock.SourcePollMs is double ms && !(ms > 0))
                    throw Fail($"{root}.input.clock.sourcePollMs must be above 0.");
                var rules = clock.SourcePollRules ?? new List<ProtocolPollRuleSpec>();
                if (rules.Count > 0 && SourcePollMs == 0)
                    throw Fail($"{root}.input.clock.sourcePollRules need a sourcePollMs to fall back on.");
                SourcePollRules = new (uint, double)[rules.Count];
                for (int i = 0; i < rules.Count; i++)
                {
                    string at = $"{root}.input.clock.sourcePollRules[{i}]";
                    if (Ports == null)
                        throw Fail($"{at}.portsConnected needs extendedReport.ports.");
                    if (!(rules[i].PeriodMs > 0))
                        throw Fail($"{at}.periodMs must be above 0.");
                    uint mask = 0;
                    foreach (int port in rules[i].PortsConnected)
                    {
                        if (port < 1 || port > Ports.Count)
                            throw Fail($"{at}.portsConnected names port {port}. The device has ports 1 to {Ports.Count}.");
                        mask |= 1u << (port - 1);
                    }
                    SourcePollRules[i] = (mask, rules[i].PeriodMs);
                }
            }
            else
            {
                SourcePollRules = Array.Empty<(uint, double)>();
            }
        }
        else
        {
            SourcePollRules = Array.Empty<(uint, double)>();
        }

        // Commands.
        if (spec.Commands != null)
        {
            CommandEndpoint = Endpoint(descriptors, spec.Commands.Endpoint, $"{root}.commands.endpoint", wantIn: false);
            CommandPacketSize = descriptors.Endpoints[CommandEndpoint].MaxPacketSize & 0x7FF;
            if (CommandPacketSize == 0)
                throw Fail($"{root}.commands.endpoint 0x{CommandEndpoint:X2} has a wMaxPacketSize of 0.");
            CommandsFromSetReport = spec.Commands.SetReport;
            Rules = new Rule[spec.Commands.Rules.Count];
            for (int i = 0; i < Rules.Length; i++)
                Rules[i] = CompileRule(spec.Commands.Rules[i], $"{root}.commands.rules[{i}]");
        }
        else
        {
            Rules = Array.Empty<Rule>();
        }
        foreach (var r in Rules)
        {
            if (r.Reply != null && InputEndpoint == 0)
                throw Fail($"{root}.commands has a rule with a reply, and the protocol declares no input endpoint to send it on.");
            MaxReplyLength = Math.Max(MaxReplyLength, r.ReplyLength);
        }

        // Halt.
        if (spec.Halt != null)
        {
            if (spec.Halt.While.Count == 0)
                throw Fail($"{root}.halt.while names no state, so the device would never answer.");
            HasHalt = true;
            HaltWhile = Condition(spec.Halt.While, $"{root}.halt.while");
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
        SelfPowered = (descriptors.ConfigurationDescriptor[7] & 0x40) != 0;
    }

    /// <summary>The protocol of <paramref name="profile"/>, or null when its
    /// usbConfiguration declares none.</summary>
    public static CompiledProtocol? Compile(ControllerProfile profile, UsbDescriptorSet descriptors)
        => profile.UsbConfiguration?.Protocol == null ? null : new CompiledProtocol(profile, descriptors);

    /// <summary>The poll period Windows gives an interrupt endpoint, as the
    /// tables under USB_ENDPOINT_DESCRIPTOR's bInterval give it (Microsoft
    /// Learn). Full speed: bInterval frames of 1 ms rounded down to a power
    /// of two, at most 32. High speed: 2^(bInterval-1) microframes of 125
    /// microseconds, at most 32 of them.</summary>
    internal static int DescriptorPeriodMicros(byte bInterval, bool highSpeed)
    {
        if (highSpeed)
            return 125 << (Math.Clamp((int)bInterval, 1, 6) - 1);
        int ms = Math.Max(1, (int)bInterval);
        int pow = 1;
        while (pow * 2 <= ms && pow < 32) pow *= 2;
        return pow * 1000;
    }

    private Rule CompileRule(ProtocolRuleSpec r, string at)
    {
        var rule = new Rule
        {
            Match = Hex(r.Match, $"{at}.match"),
            MinLength = r.MinLength ?? 0,
            MaxLength = r.MaxLength ?? int.MaxValue,
        };
        if (rule.Match.Length == 0)
            throw Fail($"{at}.match is empty. A rule matches the bytes a packet starts with.");
        if (rule.MinLength < 0 || rule.MaxLength < rule.MinLength)
            throw Fail($"{at}: minLength {rule.MinLength} and maxLength {r.MaxLength} admit no packet.");
        if (rule.MaxLength < rule.Match.Length)
            throw Fail($"{at}.maxLength {rule.MaxLength} is shorter than its {rule.Match.Length}-byte match.");
        if (r.Set != null)
        {
            foreach (var kv in r.Set)
            {
                int bit = StateBit(kv.Key, $"{at}.set");
                rule.SetMask |= 1UL << bit;
                if (kv.Value) rule.SetValue |= 1UL << bit;
            }
        }
        if (r.Reply != null)
        {
            var parts = new ReplyPart[r.Reply.Count];
            int length = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                var p = r.Reply[i];
                string pat = $"{at}.reply[{i}]";
                int kinds = (p.Hex != null ? 1 : 0) + (p.State != null ? 1 : 0) + (p.Snapshot != null ? 1 : 0);
                if (kinds != 1)
                    throw Fail($"{pat} must give exactly one of hex, state and snapshot.");
                var when = Condition(p.While, $"{pat}.while");
                if (p.Hex != null)
                {
                    if (p.Bytes != null) throw Fail($"{pat}.bytes applies to a snapshot part only.");
                    var bytes = Hex(p.Hex, $"{pat}.hex");
                    parts[i] = new ReplyPart(PartKind.Hex, bytes, 0, 0, 0, when);
                    length += bytes.Length;
                }
                else if (p.State != null)
                {
                    if (p.Bytes != null) throw Fail($"{pat}.bytes applies to a snapshot part only.");
                    parts[i] = new ReplyPart(PartKind.State, null, StateBit(p.State, $"{pat}.state"), 0, 0, when);
                    length += 1;
                }
                else
                {
                    if (Ports == null)
                        throw Fail($"{pat}.snapshot needs extendedReport.ports.");
                    int snap = Array.IndexOf(SnapshotNames, p.Snapshot);
                    if (snap < 0)
                        throw Fail($"{pat}.snapshot '{p.Snapshot}' is not one of extendedReport.ports.snapshots.");
                    int lo = 0, hi = Ports.Stride - 1;
                    if (p.Bytes != null
                        && (!PortLayout.TryParseRange(p.Bytes, out lo, out hi) || lo < 0 || hi >= Ports.Stride || lo > hi))
                        throw Fail($"{pat}.bytes \"{p.Bytes}\" must be a range inside the {Ports.Stride}-byte port block.");
                    parts[i] = new ReplyPart(PartKind.Snapshot, null, snap, lo, hi, when);
                    length += Ports.Count * (hi - lo + 1);
                }
            }
            if (length == 0)
                throw Fail($"{at}.reply is empty. Leave reply out for a command with no answer.");
            rule.Reply = parts;
            rule.ReplyLength = length;
        }
        return rule;
    }

    private StateCondition Condition(List<string>? names, string at)
    {
        if (names == null) return default;
        ulong mask = 0, value = 0;
        foreach (var raw in names)
        {
            bool negated = raw.StartsWith('!');
            int bit = StateBit(negated ? raw[1..] : raw, at);
            ulong b = 1UL << bit;
            bool want = !negated;
            if ((mask & b) != 0 && ((value & b) != 0) != want)
                throw Fail($"{at} asks for '{StateNames[bit]}' both on and off.");
            mask |= b;
            if (want) value |= b;
        }
        return new StateCondition(mask, value);
    }

    private int StateBit(string name, string at)
    {
        int i = Array.IndexOf(StateNames, name);
        if (i < 0)
            throw Fail($"{at} names '{name}', which usbConfiguration.protocol.state does not declare.");
        return i;
    }

    private byte Endpoint(UsbDescriptorSet d, string hex, string at, bool wantIn)
    {
        byte address = PortLayout.ParseByte(hex, at);
        if (!d.Endpoints.TryGetValue(address, out var ep))
            throw Fail($"{at} 0x{address:X2} is not an endpoint of the configuration descriptor.");
        if (ep.TransferType != 3 || ep.IsIn != wantIn)
            throw Fail($"{at} 0x{address:X2} must be an interrupt {(wantIn ? "IN" : "OUT")} endpoint.");
        return address;
    }

    private byte[] Hex(string text, string at)
    {
        string s = text.Replace(" ", "", StringComparison.Ordinal);
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        try { return Convert.FromHexString(s); }
        catch (FormatException) { throw Fail($"{at} \"{text}\" is not hex bytes."); }
    }

    private InvalidOperationException Fail(string message)
        => new($"Profile '{ProfileId}': {message}");
}

/// <summary>The running state of one device's protocol: the named states,
/// replies waiting for the IN endpoint, the input report and the port
/// snapshots, and the report the endpoint is sending packet by packet.
/// <see cref="UsbipEmulatedDevice"/> owns the endpoints and the clock and
/// calls in here. Every member takes <see cref="_sync"/>, and nothing here
/// allocates per report or per poll.</summary>
internal sealed class ProtocolEngine
{
    private readonly CompiledProtocol _p;
    private readonly object _sync = new();

    private ulong _state;
    private readonly Queue<byte[]> _replies = new();

    // The latest input report from the SDK.
    private readonly byte[] _report;
    private int _reportLength;

    // With a source poll: the report as the device's latest read of its
    // controllers found it, and when the SDK last wrote one. A read between
    // that write and a host poll found the latest report. Otherwise the
    // report carries what the read before the write found.
    private readonly byte[] _sampled;
    private int _sampledLength;
    private long _lastSetTicks;

    // Per snapshot, each port's whole block, and whether it holds one.
    private readonly byte[][] _snapshots;
    private readonly bool[] _snapshotTaken;
    private readonly bool[] _held;
    private uint _connectedMask;

    // The message the IN endpoint is sending, a packet per poll.
    private readonly byte[] _message;
    private int _messageLength;
    private int _messageOffset;

    private long _lastReportTicks;
    private int _hostPollMicros;
    private uint _reportsSent;
    private uint _resets;

    public ProtocolEngine(CompiledProtocol protocol)
    {
        _p = protocol;
        int capacity = Math.Max(protocol.ReportSize,
            Math.Max(SharedMemoryIO.EXTENDED_DATA_CAPACITY, SharedMemoryIO.DATA_CAPACITY + 1));
        _report = new byte[capacity];
        _sampled = new byte[capacity];
        _message = new byte[Math.Max(capacity, protocol.MaxReplyLength)];
        var ports = protocol.Ports;
        int portCount = ports?.Count ?? 0;
        _held = new bool[portCount];
        _snapshotTaken = new bool[portCount];
        _snapshots = new byte[protocol.SnapshotNames.Length][];
        for (int i = 0; i < _snapshots.Length; i++)
            _snapshots[i] = new byte[portCount * (ports?.Stride ?? 0)];
        _hostPollMicros = protocol.DescriptorPollMicros;
        _state = protocol.InitialState;
        WriteEmptyReport();
    }

    public CompiledProtocol Protocol => _p;

    /// <summary>The report a device with nothing submitted sends: the
    /// report ID, each port's status byte showing the empty connection and
    /// the flags that default on, and zeros.</summary>
    private void WriteEmptyReport()
    {
        Array.Clear(_report);
        _reportLength = Math.Min(_p.ReportSize, _report.Length);
        if (_p.ReportId != 0) _report[0] = _p.ReportId;
        var ports = _p.Ports;
        if (ports != null && ports.StatusByte >= 0)
        {
            Span<bool> flags = stackalloc bool[ports.Flags.Count];
            for (int f = 0; f < flags.Length; f++) flags[f] = ports.FlagDefault(f);
            byte status = ports.Status(0, flags);
            for (int p = 0; p < ports.Count; p++)
                _report[ports.BlockAt(p) + ports.StatusByte] = status;
        }
        _report.AsSpan(0, _reportLength).CopyTo(_sampled);
        _sampledLength = _reportLength;
    }

    /// <summary>The state at attach and after a port reset: the declared
    /// initial values, no reply waiting, nothing half sent. The ports keep
    /// what the SDK last wrote, as the controllers stay plugged in, and each
    /// held port's snapshots are taken again from it.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _state = _p.InitialState;
            _replies.Clear();
            _messageLength = 0;
            _messageOffset = 0;
            _lastReportTicks = 0;
            _reportsSent = 0;
            for (int p = 0; p < _held.Length; p++)
            {
                if (_held[p]) TakeSnapshots(p);
                else ClearSnapshots(p);
            }
            // The device reads its controllers afresh after the reset, so
            // its view is the report as it stands.
            _report.AsSpan(0, _reportLength).CopyTo(_sampled);
            _sampledLength = _reportLength;
            _lastSetTicks = 0;
            _resets++;
        }
    }

    /// <summary>Counts attaches and port resets.</summary>
    public uint Resets { get { lock (_sync) return _resets; } }

    public uint ReportsSent { get { lock (_sync) return _reportsSent; } }

    public bool IsHalted { get { lock (_sync) return HaltedLocked(_state); } }

    public bool HasReply { get { lock (_sync) return _replies.Count > 0; } }

    /// <summary>True while a poll can get something: a reply, a message
    /// under way, or reports allowed by the state.</summary>
    public bool CanSend
    {
        get
        {
            lock (_sync)
                return !HaltedLocked(_state)
                    && (_replies.Count > 0 || _messageLength > 0 || _p.InputWhile.Holds(_state));
        }
    }

    public bool GetState(int index)
    {
        lock (_sync) return (_state & (1UL << index)) != 0;
    }

    /// <summary>The host poll period in microseconds: the endpoint
    /// descriptor's, or the faster host a program models.</summary>
    public int HostPollMicros
    {
        get { lock (_sync) return _hostPollMicros; }
        set { lock (_sync) _hostPollMicros = value; }
    }

    private bool HaltedLocked(ulong state) => _p.HasHalt && _p.HaltWhile.Holds(state);

    /// <summary>Take the latest input report the SDK wrote, at
    /// <paramref name="nowTicks"/>. A port whose status newly shows a
    /// controller has its snapshots taken from this report, and an emptied
    /// port loses them. With a source poll, a read of the controllers since
    /// the last write found the report this one replaces, which becomes what
    /// the device last read.</summary>
    public void SetReport(ReadOnlySpan<byte> report, long nowTicks, long clockOriginTicks)
    {
        lock (_sync)
        {
            if (_p.SourcePollMs > 0 && _lastSetTicks != 0
                && SourcePolledBetweenLocked(_lastSetTicks, nowTicks, clockOriginTicks))
            {
                _report.AsSpan(0, _reportLength).CopyTo(_sampled);
                _sampledLength = _reportLength;
            }
            _lastSetTicks = nowTicks;

            int n = Math.Min(report.Length, _report.Length);
            report[..n].CopyTo(_report);
            _reportLength = n;

            var ports = _p.Ports;
            if (ports == null || ports.StatusByte < 0) return;
            if (n < ports.BlockAt(ports.Count)) return;
            if (_p.ReportId != 0 && report[0] != _p.ReportId) return;
            uint mask = 0;
            for (int p = 0; p < ports.Count; p++)
            {
                bool has = ports.HoldsController(_report[ports.BlockAt(p) + ports.StatusByte]);
                if (has) mask |= 1u << p;
                if (has && !_held[p]) TakeSnapshots(p);
                else if (!has && _held[p]) ClearSnapshots(p);
                _held[p] = has;
            }
            _connectedMask = mask;
        }
    }

    private void TakeSnapshots(int port)
    {
        var ports = _p.Ports!;
        for (int s = 0; s < _snapshots.Length; s++)
            _report.AsSpan(ports.BlockAt(port), ports.Stride).CopyTo(_snapshots[s].AsSpan(port * ports.Stride));
        _snapshotTaken[port] = true;
    }

    private void ClearSnapshots(int port)
    {
        var ports = _p.Ports!;
        for (int s = 0; s < _snapshots.Length; s++)
            _snapshots[s].AsSpan(port * ports.Stride, ports.Stride).Clear();
        _snapshotTaken[port] = false;
    }

    /// <summary>The input report as it stands, for GET_REPORT.</summary>
    public byte[] CopyReport()
    {
        lock (_sync) return _report.AsSpan(0, _reportLength).ToArray();
    }

    [Flags]
    public enum Effect
    {
        None = 0,
        /// <summary>A reply waits for the IN endpoint.</summary>
        Reply = 1,
        /// <summary>Reports became allowed.</summary>
        InputOpened = 2,
        /// <summary>The device stopped answering.</summary>
        Halted = 4,
        /// <summary>The state changed.</summary>
        StateChanged = 8,
    }

    /// <summary>Run one command packet through the rules. The first rule
    /// that matches applies, and a packet none matches changes nothing. A
    /// rule's reply is built from the state before the rule sets
    /// anything.</summary>
    public Effect HandlePacket(ReadOnlySpan<byte> packet)
    {
        lock (_sync)
        {
            ulong before = _state;
            if (HaltedLocked(before) || packet.Length == 0) return Effect.None;
            CompiledProtocol.Rule? rule = null;
            foreach (var r in _p.Rules)
            {
                if (packet.Length < r.MinLength || packet.Length > r.MaxLength) continue;
                if (packet.Length < r.Match.Length || !packet[..r.Match.Length].SequenceEqual(r.Match)) continue;
                rule = r;
                break;
            }
            if (rule == null) return Effect.None;

            var effect = Effect.None;
            if (rule.Reply != null)
            {
                _replies.Enqueue(BuildReply(rule, before));
                effect |= Effect.Reply;
            }
            ulong after = (before & ~rule.SetMask) | rule.SetValue;
            _state = after;
            if (after != before) effect |= Effect.StateChanged;

            if (HaltedLocked(after))
            {
                _replies.Clear();
                _messageLength = 0;
                _messageOffset = 0;
                return effect | Effect.Halted;
            }
            if (!_p.InputWhile.Holds(before) && _p.InputWhile.Holds(after))
            {
                // The first report after a start owes nothing to the
                // clock's history.
                _lastReportTicks = 0;
                effect |= Effect.InputOpened;
            }
            return effect;
        }
    }

    private byte[] BuildReply(CompiledProtocol.Rule rule, ulong state)
    {
        var reply = new byte[rule.ReplyLength];
        int at = 0;
        foreach (var part in rule.Reply!)
        {
            bool on = part.While.Holds(state);
            switch (part.Kind)
            {
                case CompiledProtocol.PartKind.Hex:
                    if (on) part.Hex!.CopyTo(reply, at);
                    at += part.Hex!.Length;
                    break;
                case CompiledProtocol.PartKind.State:
                    if (on) reply[at] = (byte)((state >> part.Index) & 1);
                    at += 1;
                    break;
                case CompiledProtocol.PartKind.Snapshot:
                {
                    var ports = _p.Ports!;
                    int width = part.Hi - part.Lo + 1;
                    for (int p = 0; p < ports.Count; p++, at += width)
                    {
                        if (!on || !_snapshotTaken[p]) continue;
                        _snapshots[part.Index].AsSpan(p * ports.Stride + part.Lo, width).CopyTo(reply.AsSpan(at));
                    }
                    break;
                }
            }
        }
        return reply;
    }

    public enum PollResult { Nak, Packet, Babble }

    /// <summary>One host poll of the IN endpoint at
    /// <paramref name="pollTicks"/>. A message under way sends its next
    /// packet. Otherwise a waiting reply starts, else a report when the state
    /// allows one and the device has read its controllers since its last
    /// report, else the poll gets a NAK. A packet longer than
    /// <paramref name="room"/> is babble: it is lost and the read fails.</summary>
    /// <param name="shortPacket">True when the packet is shorter than the
    /// endpoint's wMaxPacketSize, which ends the transfer.</param>
    public PollResult Poll(long pollTicks, long clockOriginTicks, Span<byte> room,
                           out int written, out bool shortPacket)
    {
        written = 0;
        shortPacket = false;
        lock (_sync)
        {
            if (HaltedLocked(_state)) return PollResult.Nak;
            if (_messageLength == 0)
            {
                if (_replies.Count > 0)
                {
                    var reply = _replies.Dequeue();
                    reply.CopyTo(_message, 0);
                    _messageLength = reply.Length;
                }
                else if (_p.InputWhile.Holds(_state) && HasNewsLocked(pollTicks, clockOriginTicks))
                {
                    // The report as the device's latest read of its
                    // controllers found it: the SDK's latest when a read
                    // came after it, else the one before.
                    bool latest = _p.SourcePollMs <= 0 || _lastSetTicks == 0
                        || SourcePolledBetweenLocked(_lastSetTicks, pollTicks, clockOriginTicks);
                    var source = latest ? _report.AsSpan(0, _reportLength) : _sampled.AsSpan(0, _sampledLength);
                    source.CopyTo(_message);
                    _messageLength = source.Length;
                    _lastReportTicks = pollTicks;
                    _reportsSent++;
                }
                else
                {
                    return PollResult.Nak;
                }
                _messageOffset = 0;
            }

            int size = Math.Min(_p.InputPacketSize, _messageLength - _messageOffset);
            var packet = _message.AsSpan(_messageOffset, size);
            _messageOffset += size;
            if (_messageOffset >= _messageLength)
            {
                _messageLength = 0;
                _messageOffset = 0;
            }
            if (size > room.Length) return PollResult.Babble;
            packet.CopyTo(room);
            written = size;
            shortPacket = size < _p.InputPacketSize;
            return PollResult.Packet;
        }
    }

    /// <summary>True when the device read its controllers in
    /// (last report, <paramref name="pollTicks"/>]. Always true without a
    /// source poll, and for the first report after a start.</summary>
    private bool HasNewsLocked(long pollTicks, long originTicks)
    {
        if (_p.SourcePollMs <= 0 || _lastReportTicks == 0) return true;
        return SourcePolledBetweenLocked(_lastReportTicks, pollTicks, originTicks);
    }

    /// <summary>True when the device read its controllers in
    /// (<paramref name="fromTicks"/>, <paramref name="toTicks"/>], on a grid
    /// of its source poll period from <paramref name="originTicks"/>. The
    /// period follows the ports that hold a controller now.</summary>
    private bool SourcePolledBetweenLocked(long fromTicks, long toTicks, long originTicks)
    {
        double period = _p.SourcePollMs;
        foreach (var (portsMask, periodMs) in _p.SourcePollRules)
        {
            if (portsMask == _connectedMask)
            {
                period = periodMs;
                break;
            }
        }
        double ticksPerPeriod = Stopwatch.Frequency / 1000.0 * period;
        long a = (long)Math.Floor((fromTicks - originTicks) / ticksPerPeriod);
        long b = (long)Math.Floor((toTicks - originTicks) / ticksPerPeriod);
        return b > a;
    }
}
