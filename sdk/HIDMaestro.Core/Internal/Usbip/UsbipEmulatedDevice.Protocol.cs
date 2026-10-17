using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace HIDMaestro.Internal.Usbip;

// A device whose profile declares a protocol and sends the SDK's report: its
// IN endpoint served packet by packet on a modeled host poll clock, its
// command endpoint, its control answers and its halt. ProtocolEngine holds the
// state, and CompiledProtocol the profile's rules. docs/design/v1.13.0.md
// describes the model. The poll clock is a DeviceClock with the nextPoll rule.
internal sealed partial class UsbipEmulatedDevice
{
    /// <summary>An output-ring entry the device writes when its protocol
    /// returns to its initial state, at attach, detach and port reset. The
    /// SDK reads it to set every port output back to zero and never
    /// surfaces it as output. The driver's sources are 0 to 3.</summary>
    public const byte SourceDeviceReset = 0xF0;

    private readonly ProtocolEngine? _engine;

    /// <summary>The protocol state of a device whose profile declares one,
    /// else null.</summary>
    internal ProtocolEngine? Protocol => _engine;

    // One IN transfer on the protocol's input endpoint, filled a packet per
    // host poll the way a host controller fills it. The buffer is rented and
    // the node reused, so a read allocates nothing once the pools warm.
    private sealed class ProtocolRead
    {
        public uint Seq;
        public int Capacity;
        public byte[] Buffer = Array.Empty<byte>();
        public int Filled;
    }

    // Parked reads, oldest first, and spare nodes. Guarded by _hidLock.
    private readonly LinkedList<ProtocolRead> _protocolReads = new();
    private readonly Stack<LinkedListNode<ProtocolRead>> _spareReads = new();

    private DeviceClock<HostPollTick>? _protocolClock;
    // The host's poll grid starts at attach. A host controller polls an
    // interrupt endpoint on a fixed schedule whatever the transfers do.
    private long _protocolClockOrigin = Stopwatch.GetTimestamp();

    /// <summary>The host's poll clock for the input endpoint. A host
    /// controller polls an interrupt endpoint every period while a transfer
    /// is queued, on a grid that starts at attach, and a read that arrives
    /// between polls waits for the next one. The period is the endpoint
    /// descriptor's unless the program models a faster host. While no read
    /// could complete, because nothing is parked or the device has nothing to
    /// send, the clock sleeps until something changes.</summary>
    private struct HostPollTick : IClockTick
    {
        private readonly UsbipEmulatedDevice _dev;
        public HostPollTick(UsbipEmulatedDevice dev) { _dev = dev; }

        public bool Running(out long origin)
        {
            origin = Volatile.Read(ref _dev._protocolClockOrigin);
            bool parked;
            lock (_dev._hidLock) parked = _dev._protocolReads.Count > 0;
            return parked && _dev._engine!.CanSend;
        }

        public long Period => Math.Max(1, Stopwatch.Frequency * _dev._engine!.HostPollMicros / 1_000_000);
        public long NotBefore => 0;
        public void Tick(long due) => _dev.ProtocolPoll(due);
        public void RunEnded() { }
    }

    private void StartProtocolClock()
    {
        _protocolClock = new DeviceClock<HostPollTick>(new HostPollTick(this), MissedTick.NextPoll, $"HMUsbipProtocolClock_{_index}");
        _protocolClock.Start();
    }

    /// <summary>Initial state: at attach, at detach and on a port reset, where
    /// the registers the profile marks keepOnPortReset keep their values. The
    /// host cancels the transfers it had queued, and the reads still parked
    /// here go with them.</summary>
    private void ResetProtocolState(bool portReset = false)
    {
        var engine = _engine;
        if (engine == null) return;
        if (portReset) engine.ResetPort();
        else engine.Reset();
        lock (_hidLock)
        {
            while (_protocolReads.First is { } node)
            {
                _protocolReads.RemoveFirst();
                RecycleRead(node);
            }
        }
        Volatile.Write(ref _protocolClockOrigin, Stopwatch.GetTimestamp());
        // The ports' outputs go back to zero with the device's state.
        if (engine.Protocol.Ports != null) PublishOutput(SourceDeviceReset, 0, ReadOnlySpan<byte>.Empty);
        WakeProtocolClock();
        WakeLayoutClock();
    }

    /// <summary>Caller holds _hidLock.</summary>
    private void RecycleRead(LinkedListNode<ProtocolRead> node)
    {
        var read = node.Value;
        if (read.Buffer.Length > 0) ArrayPool<byte>.Shared.Return(read.Buffer);
        read.Buffer = Array.Empty<byte>();
        if (_spareReads.Count < 16) _spareReads.Push(node);
    }

    /// <summary>An IN transfer on the input endpoint. It parks until a host
    /// poll has a packet for it.</summary>
    private void ProtocolSubmitRead(uint seqnum, int length)
    {
        int capacity = Math.Max(0, length);
        lock (_hidLock)
        {
            var node = _spareReads.Count > 0 ? _spareReads.Pop() : new LinkedListNode<ProtocolRead>(new ProtocolRead());
            var read = node.Value;
            read.Seq = seqnum;
            read.Capacity = capacity;
            read.Filled = 0;
            read.Buffer = capacity > 0 ? ArrayPool<byte>.Shared.Rent(capacity) : Array.Empty<byte>();
            if (DiagPath != null)
                _readsPerEp[_engine!.Protocol.InputEndpoint] =
                    _readsPerEp.TryGetValue(_engine.Protocol.InputEndpoint, out var rc) ? rc + 1 : 1;
            _protocolReads.AddLast(node);
        }
        WakeProtocolClock();
    }

    /// <summary>Command bytes from the OUT endpoint. They reach the device
    /// as packets of the endpoint's wMaxPacketSize, each one a command, and
    /// each goes to the output ring as the host sent it.</summary>
    private void ProtocolHandleCommands(ReadOnlySpan<byte> payload)
    {
        var engine = _engine!;
        int size = engine.Protocol.CommandPacketSize;
        var effect = ProtocolEngine.Effect.None;
        long now = Stopwatch.GetTimestamp();
        for (int off = 0; off < payload.Length; off += size)
        {
            var packet = payload.Slice(off, Math.Min(size, payload.Length - off));
            var result = engine.HandleCommand(packet, now);
            effect |= result.Effect;
            PublishCommand(result, packet);
            if ((effect & ProtocolEngine.Effect.Halted) != 0) break;
        }
        ProtocolApply(effect);
    }

    /// <summary>A SET_REPORT(Output) the protocol reads as a command. Its
    /// data stage is one report, however many control packets carried it
    /// (HID 1.11 7.2.2), and the device reads it once, as one command, as
    /// tinyusb gathers the stage and calls its handler once (hid_device.c,
    /// SET_REPORT). Under setReportStripsId a stage longer than one byte
    /// that starts with <paramref name="reportId"/>, wValue's ID, loses
    /// that byte first, as tinyusb drops it before loopj's handler reads
    /// the first byte as the command (src/gcca.c:248-252). The command goes
    /// to the output ring once, as the device read it.</summary>
    private void ProtocolHandleReport(ReadOnlySpan<byte> report, byte reportId)
    {
        if (_engine!.Protocol.SetReportStripsId && reportId != 0 && report.Length > 1 && report[0] == reportId)
            report = report[1..];
        if (report.Length == 0) return;
        var result = _engine!.HandleCommand(report, Stopwatch.GetTimestamp());
        PublishCommand(result, report);
        ProtocolApply(result.Effect);
    }

    /// <summary>A command to the output ring: as the host sent it, as the
    /// report its rule emits in its place, or not at all when the protocol
    /// or its rule consumes it.</summary>
    private void PublishCommand(ProtocolEngine.CommandResult result, ReadOnlySpan<byte> command)
    {
        var engine = _engine!;
        if (engine.Protocol.ConsumesCommands || result.Consume || command.Length == 0) return;
        if (result.Emit < 0)
        {
            PublishOutput(SourceHidOutput, command[0], command[1..]);
            return;
        }
        Span<byte> emitted = stackalloc byte[Programs.ProgramForm.MaxReport];
        int n = engine.BuildEmit(result.Emit, command, emitted);
        PublishOutput(SourceHidOutput, emitted[0], emitted[1..n]);
    }

    /// <summary>What a command did: a halt fails every parked read, and any
    /// change wakes the poll clock.</summary>
    private void ProtocolApply(ProtocolEngine.Effect effect)
    {
        if ((effect & ProtocolEngine.Effect.Halted) != 0)
            FailProtocolReads(_engine!.Protocol.HaltStatus);
        if (effect != ProtocolEngine.Effect.None)
        {
            WakeProtocolClock();
            WakeLayoutClock();
        }
    }

    private void ProtocolSubmitOut(uint seqnum, ReadOnlySpan<byte> payload)
    {
        ProtocolHandleCommands(payload);
        SendRetSubmit(seqnum, 0, payload.Length, null);
    }

    /// <summary>Every parked read fails once the device stops answering.</summary>
    private void FailProtocolReads(int status)
    {
        Span<uint> seqs = stackalloc uint[16];
        while (true)
        {
            int n = 0;
            lock (_hidLock)
            {
                while (n < seqs.Length && _protocolReads.First is { } node)
                {
                    seqs[n++] = node.Value.Seq;
                    _protocolReads.RemoveFirst();
                    RecycleRead(node);
                }
            }
            if (n == 0) return;
            for (int i = 0; i < n; i++) SendError(seqs[i], status);
        }
    }

    private bool TryUnlinkProtocolRead(uint victim)
    {
        lock (_hidLock)
        {
            for (var node = _protocolReads.First; node != null; node = node.Next)
            {
                if (node.Value.Seq != victim) continue;
                _protocolReads.Remove(node);
                RecycleRead(node);
                return true;
            }
        }
        return false;
    }

    /// <summary>One host poll of the input endpoint at
    /// <paramref name="pollTicks"/>. The oldest parked read takes the next
    /// packet, if the device has one. A short packet or a full buffer ends
    /// the read. A packet with no room left in it is babble, and the read
    /// fails with what it had.</summary>
    private void ProtocolPoll(long pollTicks)
    {
        var engine = _engine!;
        lock (_streamSendLock)
        {
            LinkedListNode<ProtocolRead>? done = null;
            int status = 0;
            lock (_hidLock)
            {
                var node = _protocolReads.First;
                if (node == null) return;
                var read = node.Value;
                var room = read.Buffer.AsSpan(read.Filled, read.Capacity - read.Filled);
                var result = engine.Poll(pollTicks, Volatile.Read(ref _protocolClockOrigin), room,
                                         out int written, out bool shortPacket);
                if (result == ProtocolEngine.PollResult.Nak) return;
                if (DiagPath != null)
                    _completionsPerEp[engine.Protocol.InputEndpoint] =
                        _completionsPerEp.TryGetValue(engine.Protocol.InputEndpoint, out var cc) ? cc + 1 : 1;
                if (result == ProtocolEngine.PollResult.Babble)
                {
                    status = -UsbipProtocol.EOverflow;
                    _protocolReads.RemoveFirst();
                    done = node;
                }
                else
                {
                    read.Filled += written;
                    if (shortPacket || read.Filled == read.Capacity)
                    {
                        _protocolReads.RemoveFirst();
                        done = node;
                    }
                }
            }
            if (done == null) return;
            var r = done.Value;
            SendRetSubmitSpan(r.Seq, status, r.Filled, r.Buffer.AsSpan(0, r.Filled));
            lock (_hidLock) RecycleRead(done);
        }
    }

    /// <summary>Control requests a protocol answers its own way. Returns
    /// false for every request the device's general handling answers.</summary>
    private bool ProtocolControl(in UsbipProtocol.CommandHeader cmd, byte bmRequestType, byte bRequest,
                                 ushort wLength)
    {
        var p = _engine!.Protocol;
        if (p.HasHalt && _engine.IsHalted)
        {
            SendError(cmd.Seqnum, p.HaltStatus);
            return true;
        }
        if (p.GetStatusFromDescriptor && bmRequestType == 0x80 && bRequest == 0x00) // GET_STATUS, device (USB 2.0 9.4.5)
        {
            // Bit 0 is the self-powered bit the configuration declares. Bit
            // 1, remote wakeup, reads clear, because the device never sees
            // the host arm it: SET_FEATURE(DEVICE_REMOTE_WAKEUP) does not
            // reach the server (measured). Which layer above usbip-win2
            // completes it no source shows, so that part is inference.
            // usbip-win2 learns of the arming only as d0_exit's wake
            // setting, which it does not send on (drivers/ude/device.cpp).
            Span<byte> d = stackalloc byte[2];
            d[0] = (byte)(p.SelfPowered ? 0x01 : 0x00);
            d[1] = 0x00;
            int n = Math.Min(d.Length, wLength);
            SendRetSubmitSpan(cmd.Seqnum, 0, n, d[..n]);
            return true;
        }
        return false;
    }

    /// <summary>GET_REPORT, as the protocol's getReport declares it. Returns
    /// false for a request the device's general handling answers.</summary>
    private bool ProtocolGetReport(in UsbipProtocol.CommandHeader cmd, byte reportType, byte reportId, ushort wLength)
    {
        var engine = _engine!;
        if (engine.Protocol.GetReportIdOnly)
        {
            // tinyusb puts a nonzero requested ID in the data stage, adds
            // what the firmware supplies, and stalls an answer of no bytes
            // (hid_device.c GET_REPORT, TU_ASSERT(xferlen > 0)). A firmware
            // that supplies nothing, as loopj's returns 0 (src/gcca.c
            // tud_hid_get_report_cb), answers the ID alone, whatever the
            // report type.
            if (reportId == 0 || wLength <= 1)
            {
                SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
                return true;
            }
            Span<byte> id = stackalloc byte[1];
            id[0] = reportId;
            SendRetSubmitSpan(cmd.Seqnum, 0, 1, id);
            return true;
        }
        // A device that builds its reports answers from its layouts, in the
        // device's general handling.
        if (reportType != 0x01 || engine.Protocol.GetReport == GetReportMode.DeviceReport
            || engine.Protocol.BuildsReports || engine.Protocol.InputEndpoint == 0) return false;
        // The input report as it stands, for its own ID. Any other ID goes
        // to the device's general handling: another report the descriptor
        // declares answers its ID and zeros, since the engine sends only its
        // own report and never fills the others, and an ID the descriptor
        // does not declare stalls.
        if (reportId != engine.Protocol.ReportId)
            return false;
        var report = engine.CopyReport();
        int n = Math.Min(report.Length, wLength);
        SendRetSubmitSpan(cmd.Seqnum, 0, n, report.AsSpan(0, n));
        return true;
    }

    /// <summary>Wake the poll clock, unless it has ended.</summary>
    private void WakeProtocolClock() => _protocolClock?.Wake();

    /// <summary>A started clock closes its own handles as it ends.</summary>
    private void DisposeProtocol(int joinMs) => _protocolClock?.Stop(joinMs);
}
