using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using HIDMaestro.Internal.Programs;

namespace HIDMaestro.Internal.Usbip;

/// <summary>The running state of one device's protocol: its registers,
/// counters and memory images, replies waiting for the host, the command log,
/// and, for a device that builds its reports, the state block they are built
/// from. A device that sends the SDK's report also keeps that report, the
/// port snapshots and the message its input endpoint is sending packet by
/// packet. <see cref="UsbipEmulatedDevice"/> owns the endpoints and the clocks
/// and calls in here. Every member takes <see cref="_sync"/>. A command, a
/// reply and a report allocate nothing: replies go into buffers of the
/// longest reply, made here.</summary>
internal sealed unsafe class ProtocolEngine
{
    private readonly CompiledProtocol _p;
    private readonly CompiledModel _m;
    private readonly object _sync = new();

    // Registers and counters, pinned so a program reads them by pointer.
    private readonly uint[] _regs;
    private readonly uint[] _counters;
    private readonly uint[] _counterMask;

    // Memory images: segment-backed unless a rule writes one, then whole.
    private readonly (uint At, byte[] Bytes)[][] _segments;
    private readonly byte[]?[] _images;

    // Value parts' bytes, per rule and part, resolved for this device.
    private readonly Dictionary<CompiledProtocol.Part, byte[]> _values = new();

    // Replies waiting, oldest at _replyHead. Bulk replies leave the head a
    // chunk at a time, and _headOffset is how much of it has gone out.
    private readonly byte[][] _replyBuf;
    private readonly int[] _replyLen;
    private int _replyHead, _replyCount, _headOffset;

    // The command log: the last commands with their arrival times.
    private readonly long[] _logTicks;
    private readonly byte[][] _logBuf;
    private readonly int[] _logLen;
    private int _logNext, _logCount;

    // A device that builds its reports: its state block, pinned, with the
    // slack every read's 32-bit load needs, and its layouts.
    private readonly byte[] _state;
    private readonly LayoutRunner? _runner;
    private readonly byte[] _peek;
    // Per layout sent on change: the bytes it last sent, and whether it has
    // sent since the last attach or port reset.
    private readonly byte[][] _lastSent;
    private readonly bool[] _sentOnce;
    // Per layout, the frame counters its program writes. A frame counter
    // advances on a message that carries it.
    private readonly uint[] _framesWritten;
    private bool _inputOpen;
    private long _inputOpenedAt;
    private long _clockOrigin = Stopwatch.GetTimestamp();

    // A device that sends the SDK's report.
    private readonly byte[] _report;
    private int _reportLength;
    private readonly BoundProgram?[] _emits;
    private readonly byte[] _emitState;
    private readonly BoundProgram[] _live;
    private readonly byte[] _built;
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
    // The message the input endpoint is sending, a packet per poll.
    private readonly byte[] _message;
    private int _messageLength;
    private int _messageOffset;
    private long _lastReportTicks;
    private int _hostPollMicros;
    private uint _reportsSent;
    private uint _resets;

    /// <param name="serial">The serial the device serves, for a value
    /// source.</param>
    /// <param name="index">The transport index, for a value source.</param>
    /// <param name="runner">The layouts' programs, generated or interpreted,
    /// for a device that builds its reports.</param>
    public ProtocolEngine(CompiledProtocol protocol, string? serial = null, int index = 0, LayoutRunner? runner = null)
    {
        _p = protocol;
        _m = protocol.Model;
        _regs = GC.AllocateArray<uint>(Math.Max(1, _m.Registers.Names.Length), pinned: true);
        _counters = GC.AllocateArray<uint>(ProgramForm.MaxCounters, pinned: true);
        _counterMask = new uint[_m.CounterNames.Length];
        for (int i = 0; i < _counterMask.Length; i++) _counterMask[i] = _m.CounterBits[i] == 32 ? uint.MaxValue : (1u << _m.CounterBits[i]) - 1;

        var written = new bool[protocol.Memories.Length];
        foreach (var r in protocol.Rules) foreach (var w in r.Writes) written[w.Memory] = true;
        _segments = new (uint, byte[])[protocol.Memories.Length][];
        _images = new byte[]?[protocol.Memories.Length];
        for (int i = 0; i < protocol.Memories.Length; i++)
        {
            var img = protocol.Memories[i];
            var segs = new (uint, byte[])[img.Segments.Length];
            for (int k = 0; k < segs.Length; k++)
            {
                var s = img.Segments[k];
                segs[k] = (s.At, s.Bytes ?? CompiledProtocol.ValueBytes(s.Value!, s.Encoding, s.Length, s.Add, serial, index));
            }
            _segments[i] = segs;
            if (written[i])
            {
                var whole = new byte[img.Size];
                Array.Fill(whole, img.Fill);
                foreach (var (at, bytes) in segs) bytes.CopyTo(whole, (int)at);
                _images[i] = whole;
            }
        }
        void Values(CompiledProtocol.Part[]? parts)
        {
            if (parts == null) return;
            foreach (var part in parts)
                if (part.Kind == CompiledProtocol.PartKind.Value)
                    _values[part] = CompiledProtocol.ValueBytes(part.ValueSource!, part.Encoding, part.Width, part.Add, serial, index);
        }
        Values(protocol.ReplyPrefix);
        foreach (var r in protocol.Rules) Values(r.Reply);

        int reply = Math.Max(1, protocol.MaxReplyLength);
        _replyBuf = new byte[protocol.PendingReplies][];
        for (int i = 0; i < _replyBuf.Length; i++) _replyBuf[i] = new byte[reply];
        _replyLen = new int[protocol.PendingReplies];

        _logTicks = new long[protocol.LogCapacity];
        _logBuf = new byte[protocol.LogCapacity][];
        _logLen = new int[protocol.LogCapacity];

        _state = GC.AllocateArray<byte>(Math.Max(1, _m.StateSize) + ProgramForm.Slack + 1, pinned: true);
        _runner = runner;
        // The reports built off the clock: a command's emitted report, read
        // from the command, and a live feature report, read from the state
        // block.
        _emits = new BoundProgram?[_m.RuleEmits.Length];
        int emitState = 1;
        for (int i = 0; i < _emits.Length; i++)
            if (_m.RuleEmits[i] is { } e)
            {
                _emits[i] = ProgramBinder.Bind(e.Program, runner is { Generated: false });
                emitState = Math.Max(emitState, e.Program.Spec.StateSize);
            }
        _emitState = GC.AllocateArray<byte>(emitState + ProgramForm.Slack + 1, pinned: true);
        _live = Array.ConvertAll(_m.LiveFeatures, v => ProgramBinder.Bind(v, runner is { Generated: false }));
        _built = GC.AllocateArray<byte>(ProgramForm.MaxReport, pinned: true);
        int largest = 1;
        foreach (var l in _m.Layouts) largest = Math.Max(largest, l.Size);
        _peek = GC.AllocateArray<byte>(largest, pinned: true);
        _lastSent = new byte[_m.Layouts.Length][];
        for (int i = 0; i < _lastSent.Length; i++) _lastSent[i] = new byte[_m.Layouts[i].Size];
        _sentOnce = new bool[_m.Layouts.Length];
        _framesWritten = new uint[_m.Layouts.Length];
        for (int i = 0; i < _framesWritten.Length; i++)
            _framesWritten[i] = CountersWritten(_m.Layouts[i].Program.Spec.Ops);

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
        InitialRegistersLocked();
        WriteEmptyReport();
    }

    public CompiledProtocol Protocol => _p;

    /// <summary>Called after a command or a reset changed the registers, on
    /// the thread that ran it, once this engine's lock is released, so a
    /// clock that reads them looks again: the idle repeat of a stream whose
    /// condition or period reads a register (HMController). A bulk OUT
    /// command runs it under the USB/IP device's report lock
    /// (UsbipEmulatedDevice.HandleBulkCommand holds _streamSendLock), so a
    /// handler takes no lock that is ever held while that one is taken.
    /// HMController's takes only the idle clock's own lock, a leaf
    /// (DeviceClock.Wake). Null while nothing reads them that way. Set on
    /// the controller's thread and read on the threads that run commands,
    /// which share no lock with it.</summary>
    internal Action? RegistersChanged
    {
        get => System.Threading.Volatile.Read(ref _registersChanged);
        set => System.Threading.Volatile.Write(ref _registersChanged, value);
    }
    private Action? _registersChanged;

    private void InitialRegistersLocked(bool portReset = false)
    {
        var table = _m.Registers;
        for (int i = 0; i < table.Initial.Length; i++)
            if (!portReset || !table.KeepOnPortReset[i]) _regs[i] = table.Initial[i];
        Array.Clear(_counters);
        _m.CounterInitial.CopyTo(_counters, 0);
        _inputOpen = _p.InputWhile == null || _p.InputWhile.Holds(_regs);
    }

    /// <summary>The report a device with nothing submitted sends: the report
    /// ID, each port's status byte showing the empty connection and the flags
    /// that default on, and zeros.</summary>
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

    /// <summary>An attach: power on, every register at its initial value.</summary>
    public void Reset() => Reset(portReset: false);

    /// <summary>A port reset: as an attach, except that a register the
    /// profile marks keepOnPortReset keeps its value.</summary>
    public void ResetPort() => Reset(portReset: true);

    /// <summary>The state at attach and after a port reset: the declared
    /// initial registers and counters, no reply waiting, nothing half sent.
    /// The ports keep what the SDK last wrote, as the controllers stay
    /// plugged in, and each held port's snapshots are taken again from it.
    /// The state block keeps the consumer's last frame, and the command log
    /// keeps its history.</summary>
    private void Reset(bool portReset)
    {
        lock (_sync)
        {
            InitialRegistersLocked(portReset);
            Array.Clear(_sentOnce);
            _replyHead = _replyCount = _headOffset = 0;
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
            _clockOrigin = Stopwatch.GetTimestamp();
            _resets++;
        }
        RegistersChanged?.Invoke();
    }

    /// <summary>Counts attaches and port resets.</summary>
    public uint Resets { get { lock (_sync) return _resets; } }

    /// <summary>Reports the input endpoint sent of the SDK's frames.</summary>
    public uint ReportsSent { get { lock (_sync) return _reportsSent; } }

    public bool IsHalted { get { lock (_sync) return HaltedLocked(); } }

    public bool HasReply { get { lock (_sync) return _replyCount > 0; } }

    /// <summary>Whether a reply waits, read without the lock: the protocol
    /// host's clock reads it on every tick, where a lock costs more than the
    /// report it builds. A reply queued as it reads goes out on the next
    /// tick, and TakeReply, under the lock, says whether one is there.</summary>
    public bool ReplyWaiting => System.Threading.Volatile.Read(ref _replyCount) > 0;

    /// <summary>True while a poll can get something: a reply, a message
    /// under way, or reports the registers allow.</summary>
    public bool CanSend
    {
        get
        {
            lock (_sync)
                return !HaltedLocked() && (_replyCount > 0 || _messageLength > 0 || _inputOpen);
        }
    }

    /// <summary>Whether a register is nonzero.</summary>
    public bool GetState(int index)
    {
        lock (_sync) return _regs[index] != 0;
    }

    public uint GetRegister(int index)
    {
        lock (_sync) return _regs[index];
    }

    /// <summary>The registers, for a reader that tests a condition on them
    /// without the lock: each is one aligned 32-bit word, written whole.</summary>
    internal uint[] RegisterWords => _regs;

    public uint GetCounter(int index)
    {
        lock (_sync) return _counters[index];
    }

    /// <summary>Whether reports flow, and the time of the command that let
    /// them flow, or of the attach when they always do. A device clock with
    /// origin inputOpened starts its grid there.</summary>
    public bool InputOpenSince(out long timestamp)
    {
        lock (_sync)
        {
            timestamp = _p.ClockOrigin == ClockOrigin.InputOpened ? _inputOpenedAt : _clockOrigin;
            return _inputOpen && !HaltedLocked();
        }
    }

    /// <summary>The host poll period in microseconds: the endpoint
    /// descriptor's, or the faster host a program models.</summary>
    public int HostPollMicros
    {
        get { lock (_sync) return _hostPollMicros; }
        set { lock (_sync) _hostPollMicros = value; }
    }

    private bool HaltedLocked() => _p.HaltWhile != null && _p.HaltWhile.Holds(_regs);

    // ── The command log ──────────────────────────────────────────────────

    private void LogLocked(ReadOnlySpan<byte> command, long timestamp)
    {
        int cap = _logTicks.Length;
        if (cap == 0) return;
        int slot = _logNext;
        // A slot's buffer grows to the longest command it held, so a steady
        // stream of commands allocates nothing.
        if (_logBuf[slot] == null || _logBuf[slot].Length < command.Length) _logBuf[slot] = new byte[Math.Max(64, command.Length)];
        command.CopyTo(_logBuf[slot]);
        _logLen[slot] = command.Length;
        _logTicks[slot] = timestamp;
        _logNext = (slot + 1) % cap;
        if (_logCount < cap) _logCount++;
    }

    /// <summary>The logged commands, oldest first, each with the Stopwatch
    /// time it arrived.</summary>
    public List<(long Ticks, byte[] Command)> SnapshotLog()
    {
        lock (_sync)
        {
            var list = new List<(long, byte[])>(_logCount);
            int cap = _logTicks.Length;
            for (int i = 0; i < _logCount; i++)
            {
                int slot = (_logNext - _logCount + i + cap) % cap;
                list.Add((_logTicks[slot], _logBuf[slot].AsSpan(0, _logLen[slot]).ToArray()));
            }
            return list;
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────

    [Flags]
    public enum Effect
    {
        None = 0,
        /// <summary>A reply waits for the host.</summary>
        Reply = 1,
        /// <summary>Reports became allowed.</summary>
        InputOpened = 2,
        /// <summary>The device stopped answering.</summary>
        Halted = 4,
        /// <summary>A register changed.</summary>
        StateChanged = 8,
        /// <summary>Reports stopped.</summary>
        InputClosed = 16,
    }

    /// <summary>What one command did, and what the controller does with it:
    /// keep it from OutputReceived, raise a translated report, or decode it.</summary>
    public readonly record struct CommandResult(Effect Effect, int Rule, bool Consume, int Emit, int Decoder);

    /// <summary>A command no rule ran on: nothing consumed, emitted or decoded.</summary>
    private static readonly CommandResult NoRule = new(Effect.None, -1, false, -1, -1);

    /// <summary>Run one command through the rules, logged first. The first
    /// rule that matches applies, and a command none matches changes nothing.
    /// A rule's reply is built from the registers before the rule sets
    /// anything.</summary>
    public Effect HandlePacket(ReadOnlySpan<byte> packet) => HandleCommand(packet, Stopwatch.GetTimestamp()).Effect;

    public CommandResult HandleCommand(ReadOnlySpan<byte> command, long timestamp)
    {
        if (DeviceOrchestrator.DiagEnabled)
            DeviceOrchestrator.LogDiag($"protocol command {_p.ProfileId}: {Convert.ToHexString(command)}");
        var result = ApplyCommand(command, timestamp);
        if ((result.Effect & Effect.StateChanged) != 0) RegistersChanged?.Invoke();
        return result;
    }

    /// <summary>HandleCommand's work, under the engine's lock.</summary>
    private CommandResult ApplyCommand(ReadOnlySpan<byte> command, long timestamp)
    {
        lock (_sync)
        {
            LogLocked(command, timestamp);
            if (HaltedLocked() || command.Length == 0) return NoRule;
            int ri = MatchLocked(command);
            if (ri < 0) return NoRule;
            var rule = _p.Rules[ri];

            var effect = Effect.None;
            if (rule.Reply != null && EnqueueReplyLocked(rule, command)) effect |= Effect.Reply;

            Span<uint> before = stackalloc uint[_m.Registers.Names.Length];
            _regs.AsSpan(0, before.Length).CopyTo(before);
            ApplyLocked(rule, command);
            if (!before.SequenceEqual(_regs.AsSpan(0, before.Length))) effect |= Effect.StateChanged;

            if (HaltedLocked())
            {
                _replyHead = _replyCount = _headOffset = 0;
                _messageLength = 0;
                _messageOffset = 0;
                return new CommandResult(effect | Effect.Halted, ri, rule.Consume, rule.Emit, rule.Decode);
            }
            bool open = _p.InputWhile == null || _p.InputWhile.Holds(_regs);
            if (open && !_inputOpen)
            {
                // The first report after a start owes nothing to the clock's
                // history.
                _lastReportTicks = 0;
                _inputOpenedAt = timestamp;
                effect |= Effect.InputOpened;
            }
            else if (!open && _inputOpen) effect |= Effect.InputClosed;
            _inputOpen = open;
            return new CommandResult(effect, ri, rule.Consume, rule.Emit, rule.Decode);
        }
    }

    /// <summary>The rule a command matches, without running it, or -1. The
    /// match reads only the command, so a controller's output reader finds
    /// the rule the device ran.</summary>
    public int Match(ReadOnlySpan<byte> command)
    {
        lock (_sync) return MatchLocked(command);
    }

    private int MatchLocked(ReadOnlySpan<byte> command)
    {
        var rules = _p.Rules;
        for (int i = 0; i < rules.Length; i++)
        {
            var r = rules[i];
            if (command.Length < r.MinLength || command.Length > r.MaxLength) continue;
            int end = r.At + r.Match.Length;
            if (command.Length < end) continue;
            bool hit = true;
            for (int k = 0; k < r.Match.Length && hit; k++)
                hit = (command[r.At + k] & r.MatchMask[k]) == r.Match[k];
            if (hit) return i;
        }
        return -1;
    }

    private void ApplyLocked(CompiledProtocol.Rule rule, ReadOnlySpan<byte> cmd)
    {
        foreach (var (reg, value) in rule.Sets) _regs[reg] = value;
        foreach (var s in rule.Stores)
        {
            uint v = ReadValue(cmd, s.Byte, s.Bytes, s.BigEndian);
            if (s.Nonzero) v = v != 0 ? 1u : 0u;
            else if (s.In != null && Array.IndexOf(s.In, v) < 0) continue;
            _regs[s.Reg] = v & _m.Registers.WidthMask(s.Reg);
        }
        if (rule.StoreRecord is { } rec)
        {
            int count = cmd[rec.CountByte] / rec.DivideBy;
            for (int k = 0; k < count; k++)
            {
                int at = rec.At + k * rec.Size;
                if (at + rec.Size > cmd.Length) break;
                if (cmd[at + rec.KeyOffset] != rec.KeyValue) continue;
                foreach (var (reg, offset, bytes, be) in rec.Fields)
                    _regs[reg] = ReadValue(cmd, at + offset, bytes, be) & _m.Registers.WidthMask(reg);
            }
        }
        foreach (var (reg, b, mask) in rule.Ors) _regs[reg] |= cmd[b] & _regs[mask] & _m.Registers.WidthMask(reg);
        foreach (var (reg, b, mask) in rule.AndNots) _regs[reg] &= ~(cmd[b] & _regs[mask]);
        foreach (var w in rule.Writes)
        {
            var img = _images[w.Memory]!;
            uint address = w.Address.Read(cmd);
            if (w.Flags is { } flags)
            {
                int bits = 0;
                foreach (var (b, bit) in flags) if (cmd[b] != 0) bits |= 1 << bit;
                img[(int)(address % (uint)img.Length)] = (byte)bits;
                continue;
            }
            for (int k = 0; k <= w.Hi - w.Lo; k++) img[(int)((address + (uint)k) % (uint)img.Length)] = cmd[w.Lo + k];
        }
    }

    private static uint ReadValue(ReadOnlySpan<byte> cmd, int at, int bytes, bool bigEndian)
    {
        uint v = 0;
        for (int k = 0; k < bytes; k++)
        {
            byte b = cmd[at + k];
            v = bigEndian ? (v << 8) | b : v | ((uint)b << (8 * k));
        }
        return v;
    }

    // ── Replies ──────────────────────────────────────────────────────────

    /// <summary>Queue the rule's reply under the protocol's overflow rule.
    /// Returns false when it was dropped.</summary>
    private bool EnqueueReplyLocked(CompiledProtocol.Rule rule, ReadOnlySpan<byte> cmd)
    {
        if (_replyCount >= _p.PendingReplies)
        {
            switch (_p.Overflow)
            {
                case ReplyOverflow.DropNewest:
                    return false;
                case ReplyOverflow.DropOldestUnstarted:
                    // Never the head while part of it has gone out: the host
                    // is halfway through reading that one.
                    if (_headOffset > 0) return false;
                    goto case ReplyOverflow.DropOldest;
                case ReplyOverflow.DropOldest:
                    _replyHead = (_replyHead + 1) % _replyBuf.Length;
                    _replyCount--;
                    _headOffset = 0;
                    break;
            }
        }
        int slot = (_replyHead + _replyCount) % _replyBuf.Length;
        _replyLen[slot] = BuildReplyLocked(rule, cmd, _replyBuf[slot]);
        _replyCount++;
        return true;
    }

    private int BuildReplyLocked(CompiledProtocol.Rule rule, ReadOnlySpan<byte> cmd, byte[] dst)
    {
        int at = 0;
        if (rule.UsePrefix && _p.ReplyPrefix != null)
            foreach (var part in _p.ReplyPrefix) at = WritePartLocked(part, rule, cmd, dst, at);
        foreach (var part in rule.Reply!) at = WritePartLocked(part, rule, cmd, dst, at);
        if (rule.ReplyLength > at)
        {
            dst.AsSpan(at, rule.ReplyLength - at).Clear();
            at = rule.ReplyLength;
        }
        return at;
    }

    private int WritePartLocked(CompiledProtocol.Part part, CompiledProtocol.Rule rule, ReadOnlySpan<byte> cmd, byte[] dst, int at)
    {
        int length = part.FixedLength >= 0 ? part.FixedLength : (int)part.Length.Read(cmd);
        var span = dst.AsSpan(at, length);
        span.Clear();
        if (part.While != null && !part.While.Holds(_regs)) return at + length;
        switch (part.Kind)
        {
            case CompiledProtocol.PartKind.Hex:
                part.Bytes!.CopyTo(span);
                break;
            case CompiledProtocol.PartKind.State:
                span[0] = (byte)(_regs[part.Index] != 0 ? 1 : 0);
                break;
            case CompiledProtocol.PartKind.Snapshot:
            {
                var ports = _p.Ports!;
                int width = part.Hi - part.Lo + 1;
                for (int p = 0; p < ports.Count; p++)
                    if (_snapshotTaken[p])
                        _snapshots[part.Index].AsSpan(p * ports.Stride + part.Lo, width).CopyTo(span[(p * width)..]);
                break;
            }
            case CompiledProtocol.PartKind.Echo:
                cmd.Slice(part.Lo, length).CopyTo(span);
                if (part.Max >= 0 && span[0] > part.Max) span[0] = (byte)part.Max;
                break;
            case CompiledProtocol.PartKind.Register:
                WriteNumber(span, _regs[part.Index], part.BigEndian);
                break;
            case CompiledProtocol.PartKind.Counter:
            {
                int c = part.Index;
                WriteNumber(span, _counters[c], part.BigEndian);
                var on = _m.CounterOn[c];
                if (on is CounterEvent.Read or CounterEvent.Frame) _counters[c] = (_counters[c] + 1) & _counterMask[c];
                break;
            }
            case CompiledProtocol.PartKind.Memory:
                ReadMemoryLocked(part.Index, part.Address.Read(cmd), span);
                break;
            case CompiledProtocol.PartKind.Bitmap:
            {
                int flags = part.BitmapByte < cmd.Length ? cmd[part.BitmapByte] : 0;
                foreach (var (bit, pos, hex) in part.BitmapSet)
                    if ((flags & (1 << bit)) != 0) hex.CopyTo(span[pos..]);
                break;
            }
            case CompiledProtocol.PartKind.Input:
                if (_runner != null)
                {
                    BuildLayoutLocked(part.Index, _peek, count: false);
                    _peek.AsSpan(part.Lo, length).CopyTo(span);
                }
                break;
            case CompiledProtocol.PartKind.Value:
                _values[part].CopyTo(span);
                break;
            case CompiledProtocol.PartKind.Status:
                span[0] = rule.Status;
                break;
        }
        return at + length;
    }

    private static void WriteNumber(Span<byte> dst, uint v, bool bigEndian)
    {
        for (int k = 0; k < dst.Length; k++)
            dst[k] = (byte)(v >> (8 * (bigEndian ? dst.Length - 1 - k : k)));
    }

    /// <summary>A memory image's bytes from <paramref name="address"/>,
    /// addresses wrapping at the image's size, and an image of 0x100000000
    /// bytes at the 32-bit sum. A byte no segment covers reads as the
    /// image's fill.</summary>
    public void ReadMemory(int memory, uint address, Span<byte> dst)
    {
        lock (_sync) ReadMemoryLocked(memory, address, dst);
    }

    private void ReadMemoryLocked(int memory, uint address, Span<byte> dst)
    {
        var img = _p.Memories[memory];
        if (_images[memory] is { } whole)
        {
            for (int i = 0; i < dst.Length; i++) dst[i] = whole[(int)((address + (uint)i) % (ulong)img.Size)];
            return;
        }
        var segs = _segments[memory];
        for (int i = 0; i < dst.Length; i++)
        {
            uint a = (uint)((address + (uint)i) % (ulong)img.Size);
            byte value = img.Fill;
            foreach (var (start, bytes) in segs)
            {
                if (a >= (uint)start && a - (uint)start < (uint)bytes.Length)
                {
                    value = bytes[a - (uint)start];
                    break;
                }
            }
            dst[i] = value;
        }
    }

    /// <summary>Up to <paramref name="capacity"/> bytes of the oldest reply,
    /// into <paramref name="dst"/>. A reply longer than one read spans
    /// several, and two replies never share a read. Returns -1 when nothing
    /// waits. A capacity of 0 takes none of the reply and leaves it waiting.</summary>
    public int TakeReply(int capacity, Span<byte> dst)
    {
        lock (_sync)
        {
            if (_replyCount == 0) return -1;
            int len = _replyLen[_replyHead];
            int n = Math.Min(Math.Max(0, capacity), len - _headOffset);
            _replyBuf[_replyHead].AsSpan(_headOffset, n).CopyTo(dst);
            _headOffset += n;
            if (_headOffset >= len)
            {
                _replyHead = (_replyHead + 1) % _replyBuf.Length;
                _replyCount--;
                _headOffset = 0;
            }
            return n;
        }
    }

    // ── A device that builds its reports ─────────────────────────────────

    /// <summary>Replace the state the next report is built from. A block of
    /// another size is not the device's, and is ignored.</summary>
    public void SetStateBlock(ReadOnlySpan<byte> block)
    {
        if (block.Length != _m.StateSize) return;
        lock (_sync) block.CopyTo(_state);
    }

    /// <summary>The report a command's rule emits in place of the command:
    /// its program over the command's bytes, the registers and the counters,
    /// or the report ID and a memory image's first bytes as the rule's writes
    /// left them. Report ID first. Returns its length.</summary>
    public int BuildEmit(int emit, ReadOnlySpan<byte> command, Span<byte> dst)
    {
        var rule = _p.Rules[emit];
        if (rule.EmitMemory >= 0)
        {
            lock (_sync)
            {
                dst[0] = rule.EmitReportId;
                ReadMemoryLocked(rule.EmitMemory, 0, dst[1..rule.EmitSize]);
            }
            return rule.EmitSize;
        }
        var bound = _emits[emit]!;
        int stateSize = bound.Program.Spec.StateSize, size = bound.Program.Spec.Size;
        lock (_sync)
        {
            Array.Clear(_emitState);
            command[..Math.Min(command.Length, stateSize)].CopyTo(_emitState);
            UpdateClockCountersLocked();
            fixed (byte* s = _emitState)
            fixed (byte* d = _built)
            fixed (uint* r = _regs, c = _counters)
                bound.Run(s, d, r, c);
            _built.AsSpan(0, size).CopyTo(dst);
        }
        return size;
    }

    /// <summary>The live feature report of <paramref name="reportId"/>, built
    /// now from the state block, the registers and the counters, report ID
    /// first, or -1 when the profile declares none for it.</summary>
    public int BuildLiveFeature(byte reportId, Span<byte> dst)
    {
        lock (_sync) return LiveFeatureLocked(reportId, dst);
    }

    private int LiveFeatureLocked(byte reportId, Span<byte> dst)
    {
        int i = Array.IndexOf(_m.LiveFeatureIds, reportId);
        if (i < 0) return -1;
        var bound = _live[i];
        int size = bound.Program.Spec.Size;
        UpdateClockCountersLocked();
        fixed (byte* s = _state)
        fixed (byte* d = _built)
        fixed (uint* r = _regs, c = _counters)
            bound.Run(s, d, r, c);
        _built.AsSpan(0, Math.Min(size, dst.Length)).CopyTo(dst);
        return size;
    }

    /// <summary>A submit under one lock: the state block replaced, the report
    /// the selected layout sends on submit built and counted into
    /// <paramref name="dst"/>, and the live feature report of
    /// <paramref name="featureId"/> built into <paramref name="feature"/>.
    /// Returns the report's length, 0 when the selected layout goes out on
    /// the clock or on change or none holds, and sets
    /// <paramref name="featureLength"/> to the feature's, or -1 for none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Submit<TSet>(ref TSet set, ReadOnlySpan<byte> block, Span<byte> dst, int featureId, Span<byte> feature, out int featureLength)
        where TSet : struct, IReportSet
    {
        lock (_sync)
        {
            if (block.Length == _m.StateSize) block.CopyTo(_state);
            int n = BuildLocked(ref set, dst, SendRule.Submit, commit: true, out _);
            featureLength = featureId >= 0 ? LiveFeatureLocked((byte)featureId, feature) : -1;
            return n;
        }
    }

    /// <summary>The state block as it stands.</summary>
    public byte[] CopyStateBlock()
    {
        lock (_sync) return _state.AsSpan(0, _m.StateSize).ToArray();
    }

    /// <summary>The layout that goes out now: the first whose condition
    /// holds, or -1.</summary>
    public int SelectedLayout
    {
        get { lock (_sync) return SelectLocked(); }
    }

    private int SelectLocked()
    {
        var layouts = _m.Layouts;
        for (int i = 0; i < layouts.Length; i++)
            if (layouts[i].While == null || layouts[i].While!.Holds(_regs)) return i;
        return -1;
    }

    /// <summary>Build the report a tick of the device clock sends and count
    /// it, through the set's own type so a generated program inlines here.
    /// Returns its length, or 0 when the tick sends nothing: no layout's
    /// condition holds, the selected layout goes out on submit, or it goes out
    /// on change and its bytes are the ones it last sent.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int BuildNext<TSet>(ref TSet set, Span<byte> dst) where TSet : struct, IReportSet
    {
        lock (_sync) return BuildLocked(ref set, dst, SendRule.Clock, commit: true, out _);
    }

    /// <summary>The same, with the layout the report was built in, or -1,
    /// chosen under the same hold of the lock: a command on another thread
    /// can select another layout the moment the lock is free, and the send
    /// rule that applies to the report is its own layout's.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int BuildNext<TSet>(ref TSet set, Span<byte> dst, out int layout) where TSet : struct, IReportSet
    {
        lock (_sync) return BuildLocked(ref set, dst, SendRule.Clock, commit: true, out layout);
    }

    /// <summary>Whether a tick now would send a report, for a tick that finds
    /// no read waiting: the device owes a report only when it has one.
    /// Counts nothing.</summary>
    public bool TickWouldSend<TSet>(ref TSet set) where TSet : struct, IReportSet
    {
        lock (_sync) return BuildLocked(ref set, _peek, SendRule.Clock, commit: false, out _) > 0;
    }

    /// <summary>The report a SubmitState sends, built and counted, when the
    /// selected layout goes out on submit. Returns its length, or 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int BuildOnSubmit<TSet>(ref TSet set, Span<byte> dst) where TSet : struct, IReportSet
    {
        lock (_sync) return BuildLocked(ref set, dst, SendRule.Submit, commit: true, out _);
    }

    /// <summary>Whether the selected layout goes out on submit.</summary>
    public bool SendsOnSubmit
    {
        get
        {
            lock (_sync)
            {
                int layout = SelectLocked();
                return layout >= 0 && _m.Layouts[layout].Send == SendRule.Submit;
            }
        }
    }

    /// <summary>The selected layout built into <paramref name="dst"/> when it
    /// goes out on <paramref name="occasion"/>: a tick sends a clock layout,
    /// and a change layout whose bytes differ from the ones it last sent, and
    /// a submit sends a submit layout. With <paramref name="commit"/> the
    /// report counts as sent. Returns its length, or 0, and the selected
    /// layout, or -1, in <paramref name="layout"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int BuildLocked<TSet>(ref TSet set, Span<byte> dst, SendRule occasion, bool commit, out int layout) where TSet : struct, IReportSet
    {
        layout = SelectLocked();
        if (layout < 0) return 0;
        var form = _m.Layouts[layout];
        var send = form.Send;
        if (send == SendRule.Submit ? occasion != SendRule.Submit : occasion == SendRule.Submit) return 0;
        // Only a layout sent on change needs its bytes to know whether it
        // sends.
        if (!commit && send != SendRule.Change) return form.Size;
        UpdateClockCountersLocked();
        fixed (byte* d = dst)
        fixed (byte* s = _state)
        fixed (uint* r = _regs, c = _counters)
            set.Run(layout, s, d, r, c);
        int size = form.Size;
        if (send == SendRule.Change)
        {
            var built = dst[..size];
            if (_sentOnce[layout] && built.SequenceEqual(_lastSent[layout])) return 0;
            if (commit)
            {
                built.CopyTo(_lastSent[layout]);
                _sentOnce[layout] = true;
            }
        }
        if (commit) CountLocked(layout);
        return size;
    }

    /// <summary>The report a GET_REPORT(Input) reads: what the next report
    /// would be, in the layout of <paramref name="reportId"/> whose condition
    /// holds, or the first layout of that ID when none holds, or the selected
    /// one for an ID no layout has, without counting it. Returns its length.</summary>
    public int Peek(byte reportId, Span<byte> dst)
    {
        lock (_sync)
        {
            int layout = -1, first = -1;
            var layouts = _m.Layouts;
            for (int i = 0; i < layouts.Length && layout < 0; i++)
            {
                if (layouts[i].ReportId != reportId) continue;
                if (first < 0) first = i;
                if (layouts[i].While == null || layouts[i].While!.Holds(_regs)) layout = i;
            }
            if (layout < 0) layout = first;
            if (layout < 0) layout = SelectLocked();
            if (layout < 0 || _runner == null) return 0;
            BuildLayoutLocked(layout, _peek, count: false);
            int n = layouts[layout].Size;
            _peek.AsSpan(0, n).CopyTo(dst);
            return n;
        }
    }

    private void BuildLayoutLocked(int layout, byte[] pinnedDst, bool count)
    {
        UpdateClockCountersLocked();
        fixed (byte* d = pinnedDst)
        fixed (byte* s = _state)
        fixed (uint* r = _regs, c = _counters)
            _runner!.Run(layout, s, d, r, c);
        if (count) CountLocked(layout);
    }

    /// <summary>A layout sent: every report counter advances, and each frame
    /// counter its program writes.</summary>
    private void CountLocked(int layout)
    {
        uint written = _framesWritten[layout];
        for (int i = 0; i < _counterMask.Length; i++)
            if (_m.CounterOn[i] == CounterEvent.Report
                || (_m.CounterOn[i] == CounterEvent.Frame && (written & (1u << i)) != 0))
                _counters[i] = (_counters[i] + 1) & _counterMask[i];
    }

    /// <summary>The counter slots a program's ops write, its conditional
    /// blocks' included.</summary>
    private static uint CountersWritten(Op[] ops)
    {
        uint mask = 0;
        foreach (var op in ops)
        {
            if (op is CounterOp k) mask |= 1u << k.Slot;
            else if (op is WhenOp w) mask |= CountersWritten(w.Ops);
        }
        return mask;
    }

    /// <summary>Clock counters: the time since the clock's origin over their
    /// period, computed from the origin on every write so no remainder is
    /// lost.</summary>
    private void UpdateClockCountersLocked()
    {
        for (int i = 0; i < _counterMask.Length; i++)
        {
            if (_m.CounterOn[i] != CounterEvent.Clock) continue;
            long origin = _p.ClockOrigin == ClockOrigin.InputOpened ? _inputOpenedAt : _clockOrigin;
            double us = (Stopwatch.GetTimestamp() - origin) * 1_000_000.0 / Stopwatch.Frequency;
            _counters[i] = (uint)((ulong)Math.Max(0, us / _m.CounterPeriodUs[i]) & _counterMask[i]);
        }
    }

    // ── A device that sends the SDK's report ─────────────────────────────

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

    /// <summary>The input report as it stands, for GET_REPORT. The poll
    /// clock does not take it, and nothing is counted.</summary>
    public byte[] CopyReport()
    {
        lock (_sync) return _report.AsSpan(0, _reportLength).ToArray();
    }

    /// <summary>The same report copied into <paramref name="dst"/>, which
    /// holds <see cref="ReportCapacity"/> bytes. Returns its length.</summary>
    public int CopyReport(Span<byte> dst)
    {
        lock (_sync)
        {
            _report.AsSpan(0, _reportLength).CopyTo(dst);
            return _reportLength;
        }
    }

    /// <summary>The most bytes <see cref="CopyReport(Span{byte})"/>
    /// copies.</summary>
    public int ReportCapacity => _report.Length;

    public enum PollResult { Nak, Packet, Babble }

    /// <summary>One host poll of the input endpoint at
    /// <paramref name="pollTicks"/>, for a device whose replies wait on the
    /// input endpoint. A message under way sends its next packet. Otherwise a
    /// waiting reply starts, else a report when the registers allow one and
    /// the device has read its controllers since its last report, else the
    /// poll gets a NAK. A packet longer than <paramref name="room"/> is
    /// babble: it is lost and the read fails.</summary>
    /// <param name="shortPacket">True when the packet is shorter than the
    /// endpoint's wMaxPacketSize, which ends the transfer.</param>
    public PollResult Poll(long pollTicks, long clockOriginTicks, Span<byte> room,
                           out int written, out bool shortPacket)
    {
        written = 0;
        shortPacket = false;
        lock (_sync)
        {
            if (HaltedLocked()) return PollResult.Nak;
            if (_messageLength == 0)
            {
                if (_replyCount > 0)
                {
                    int len = _replyLen[_replyHead];
                    _replyBuf[_replyHead].AsSpan(0, len).CopyTo(_message);
                    _messageLength = len;
                    _replyHead = (_replyHead + 1) % _replyBuf.Length;
                    _replyCount--;
                }
                else if (_inputOpen && HasNewsLocked(pollTicks, clockOriginTicks))
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

    /// <summary>True when the device read its controllers in (last report,
    /// <paramref name="pollTicks"/>]. Always true without a source poll, and
    /// for the first report after a start.</summary>
    private bool HasNewsLocked(long pollTicks, long originTicks)
    {
        if (_p.SourcePollMs <= 0 || _lastReportTicks == 0) return true;
        return SourcePolledBetweenLocked(_lastReportTicks, pollTicks, originTicks);
    }

    /// <summary>True when the device read its controllers in
    /// (<paramref name="fromTicks"/>, <paramref name="toTicks"/>], on a grid of
    /// its source poll period from <paramref name="originTicks"/>. The period
    /// follows the ports that hold a controller now.</summary>
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
