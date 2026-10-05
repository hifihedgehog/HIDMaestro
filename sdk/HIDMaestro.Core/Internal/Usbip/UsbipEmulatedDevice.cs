using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace HIDMaestro.Internal.Usbip;

/// <summary>One emulated composite USB device behind the USB/IP server
/// (issue #39). Owns the control-transfer state machine, the endpoint
/// dispatch, the pending-URB bookkeeping, and the bridges to the SDK's
/// existing per-controller shared-memory contract, so an
/// <see cref="HIDMaestro.HMController"/> works over this backend without
/// a single change to its own code:
///
/// <list type="bullet">
/// <item>Input: a pump thread waits on Global\HIDMaestroInputEvent&lt;N&gt;
/// (the same event <c>SubmitState</c> signals), seqlock-reads the input
/// section, and builds the wire report exactly as driver.c does: the
/// report is InputReportByteLength bytes, zero-filled, with
/// FirstInputReportId prepended on the legacy path and the extended
/// buffer passed through verbatim when ExtendedReportSize is set.</item>
/// <item>Output: interrupt-OUT transfers and SET_REPORT writes publish to
/// the Global\HIDMaestroOutput&lt;N&gt; ring with driver.c's discipline
/// (reserve Head atomically, fill the slot, fence, publish slot.SeqNo,
/// doorbell last), so <c>HMController.OutputReceived</c> and
/// <c>OutputDecoded</c> fire identically to the UMDF2 path.</item>
/// <item>Feature reads: the Sony Get_Feature table is driver.c's, byte for
/// byte (0x05/41 and 0x02/37-41 carrying the neutral calibration, 0x09/20
/// and 0x12/16 with the synthetic MAC for DS5 and DS4 respectively,
/// 0x20/64 and 0xA3/49 carrying real firmware info, 0x22/64 answering the
/// Bluetooth-patch read, VID 0x054C gated), including the HID_FEATURE_READ
/// ring notification that drives the extendedReport.armOn watcher.</item>
/// </list>
///
/// <para>PID FFB feature serving is deliberately absent here: no usbip
/// profile declares a PID block (they are Sony composite personas), and
/// the UMDF2 backend remains the PID path. A future PID-over-usbip
/// profile must port driver.c's PID Get/SetFeature handling first.</para>
///
/// <para>Isochronous endpoints route to <see cref="UsbAudioEngine"/>;
/// completions come back on the pacing thread and are serialized onto the
/// connection by the server's send lock.</para></summary>
internal sealed class UsbipEmulatedDevice : IDisposable
{
    public UsbDescriptorSet Descriptors { get; }
    public UsbAudioEngine Audio { get; }
    public string BusId { get; }

    /// <summary>Controller index, kept so the synthetic pairing MAC in
    /// BuildFeatureStub is stable and unique per controller (#43).</summary>
    private readonly int _index;
    public uint Devid { get; }

    private readonly HidReportBuilder _builder;
    private readonly IntPtr _inputView;
    private readonly IntPtr _outputView;
    private readonly IntPtr _outputEvent;
    private IntPtr _inputWaitEvent;
    private readonly Thread _inputThread;
    private volatile bool _stop;

    // The connection's serialized sender; null until attached. Claimed
    // and released with CompareExchange so a racing second import can
    // never steal or clear another connection's ownership.
    private UsbipServer.Connection? _connection;

    private readonly object _hidLock = new();
    private readonly Queue<byte[]> _frameQueue = new();          // built wire reports awaiting a read
    // Parked interrupt-IN URBs, keyed by endpoint address (issue #56). A
    // composite with more than one interrupt-IN endpoint must never answer
    // one endpoint's read with another endpoint's data: the Steam Deck
    // persona's keyboard and mouse interfaces poll their own endpoints
    // forever while only the controller endpoint has frames, and a single
    // shared queue would have handed a 64-byte controller report to the
    // 8-byte keyboard pipe.
    private readonly Dictionary<byte, Queue<uint>> _pendingInterruptIn = new();
    private byte[] _lastInputReport;
    private uint _lastSharedSeqNo;

    // Device state.
    private volatile byte _configurationValue;

    private const int MaxFrameQueue = 8;

    /// <summary>The interrupt-IN endpoint on the primary HID interface: the
    /// one the input pump feeds. Any other interrupt-IN endpoint belongs to
    /// a secondary HID interface and simply parks its reads.</summary>
    private readonly byte _primaryInEndpoint;

    /// <summary>Profile-declared feature-report answers (issue #56), and the
    /// state one needs. Sony's stubs are keyed by report id; the Steam
    /// Deck's protocol has no report ids and instead answers GET_REPORT
    /// according to the message id of the SET_REPORT that preceded it, which
    /// is what <c>match: "lastMessage"</c> selects.</summary>
    private readonly FeatureStubTable? _stubs;
    private byte _lastFeatureMessage;
    private int _lastFeatureParam = -1;
    private byte[]? _lastFeaturePayload;

    // Switch 2 Pro Controller persona (issue #66). Non-null exactly for
    // 057E:2069: the command responder behind the vendor interface's bulk
    // pair, and the state each input report is built from. A device with
    // no responder stalls bulk transfers, as every persona before it did.
    private readonly Switch2ProDevice? _switch2;

    /// <summary>The Switch 2 Pro persona's protocol state and its log of
    /// every bulk OUT payload, or null on any other persona.</summary>
    internal Switch2ProDevice? Switch2 => _switch2;

    private readonly byte _bulkInEndpoint;
    // Buffer lengths of parked bulk IN reads, by seqnum. The seqnums park
    // in _pendingInterruptIn beside the interrupt reads, which is where
    // HandleUnlink looks.
    private readonly Dictionary<uint, int> _parkedBulkLength = new();
    private int _bulkReadsCanceled;

    /// <summary>Bulk IN reads a host gave up on before a reply came. Under
    /// usbip-win2 such a read is trouble for the host, not for this device:
    /// the driver completes a canceled transfer with its length untouched,
    /// and WinUSB then hands the host stale bytes. A host that only reads
    /// after a command, as SDL does, never adds to this.</summary>
    internal int BulkReadsCanceled => Volatile.Read(ref _bulkReadsCanceled);

    // The persona's report clock. The pad sends one report every 4.000 ms
    // once a host has started it and nothing before: 50,899 reports in the
    // capture this persona is modeled on, none off that step. Each report's
    // motion timestamp advances 4000 microseconds and SDL hands it on as
    // the sample's time, so the step belongs to the device and never
    // follows the consumer's submit rate.
    private const int Switch2ReportIntervalMs = 4;
    private readonly Thread? _streamThread;
    private readonly AutoResetEvent? _streamWake;
    private readonly IntPtr _streamTimer;
    // A tick that found no read parked leaves one report owed, and the next
    // read is answered as it arrives: the pad had its report ready and the
    // host polled late. Guarded by _hidLock.
    private bool _reportOwed;
    // Held from building a report to sending it, by the clock and by a late
    // read alike, so two reports can never reach the wire out of order.
    private readonly object _streamSendLock = new();

    // Diagnostic (HIDMAESTRO_DIAG_READS=<path>): who actually reads us.
    // A consumer that claims the device but never submits an interrupt-IN
    // URB on the controller endpoint cannot see input no matter what the
    // send side does, and nothing else in the stack reveals that.
    private static readonly string? DiagPath =
        Environment.GetEnvironmentVariable("HIDMAESTRO_DIAG_READS");
    private readonly Dictionary<byte, long> _readsPerEp = new();
    private readonly Dictionary<byte, long> _completionsPerEp = new();
    private long _diagLastDump;

    private Queue<uint> PendingFor(byte epAddr)
    {
        if (!_pendingInterruptIn.TryGetValue(epAddr, out var q))
        {
            q = new Queue<uint>();
            _pendingInterruptIn[epAddr] = q;
        }
        return q;
    }

    public UsbipEmulatedDevice(ControllerProfile profile, int index, DeviceIdentity? identity = null)
    {
        _index = index;
        Descriptors = new UsbDescriptorSet(profile, index, identity);
        _stubs = FeatureStubTable.From(profile);
        _primaryInEndpoint = 0;
        foreach (var kv in Descriptors.Endpoints)
        {
            var ep = kv.Value;
            if (ep.TransferType == 3 && ep.IsIn && ep.InterfaceNumber == Descriptors.HidInterfaceNumber)
            {
                _primaryInEndpoint = ep.Address;
                break;
            }
        }
        BusId = $"1-{index + 1}";
        Devid = (1u << 16) | (uint)(index + 1);

        if (Switch2ProPacker.IsSwitch2Pro(Descriptors.VendorId, Descriptors.ProductId))
        {
            foreach (var kv in Descriptors.Endpoints)
            {
                if (kv.Value.TransferType == 2 && kv.Value.IsIn)
                {
                    _bulkInEndpoint = kv.Key;
                    break;
                }
            }
            _switch2 = new Switch2ProDevice(Descriptors.SerialString, BusId);
        }

        _builder = profile.GetOrBuildReportBuilder();
        _lastInputReport = new byte[Math.Max(1, _builder.InputReportByteSize)];
        if (_builder.InputReportId != 0) _lastInputReport[0] = _builder.InputReportId;

        // Sections and events first, so both this device and the
        // HMController constructed after it see the same objects.
        _inputView = SharedMemoryIO.EnsureInputMapping(index);
        _outputView = SharedMemoryIO.EnsureOutputMapping(index);
        _outputEvent = SharedMemoryIO.EnsureOutputEvent(index);
        _inputWaitEvent = SharedMemoryIO.OpenInputEventForWait(index);

        Audio = new UsbAudioEngine(profile, CompleteIsoOnWire);

        _inputThread = new Thread(InputPumpLoop)
        {
            IsBackground = true,
            Name = $"HMUsbipInput_{index}",
            Priority = ThreadPriority.AboveNormal,
        };
        _inputThread.Start();

        if (_switch2 != null)
        {
            _streamWake = new AutoResetEvent(false);
            _streamTimer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero,
                CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            _streamThread = new Thread(Switch2StreamLoop)
            {
                IsBackground = true,
                Name = $"HMUsbipSwitch2Stream_{index}",
                Priority = ThreadPriority.AboveNormal,
            };
            _streamThread.Start();
        }
    }

    /// <summary>Claim this device for one connection. A second import
    /// while attached is refused ST_DEV_BUSY by the server.</summary>
    public bool TryClaimConnection(UsbipServer.Connection connection)
    {
        if (Interlocked.CompareExchange(ref _connection, connection, null) != null)
            return false;
        // Attach is power-on for the Switch 2 Pro persona's protocol state.
        ResetSwitch2State();
        return true;
    }

    public void DetachConnection(UsbipServer.Connection connection)
    {
        if (Interlocked.CompareExchange(ref _connection, null, connection) != connection)
            return; // a different connection owns the device
        Audio.Clear();
        lock (_hidLock)
        {
            foreach (var q in _pendingInterruptIn.Values) q.Clear();
            _frameQueue.Clear();
        }
        ResetSwitch2State();
    }

    /// <summary>The Switch 2 Pro persona as it is at attach and after a
    /// port reset: reports off, report 0x09 selected, no features, no reply
    /// waiting, no read owed (issue #66). Nothing for any other persona.</summary>
    private void ResetSwitch2State()
    {
        if (_switch2 == null) return;
        lock (_hidLock)
        {
            _parkedBulkLength.Clear();
            _reportOwed = false;
        }
        _switch2.Reset();
    }

    // ── Input pump: shared section → interrupt IN ────────────────────────

    private void InputPumpLoop()
    {
        while (!_stop)
        {
            try
            {
                if (_inputWaitEvent != IntPtr.Zero)
                    WaitForSingleObject(_inputWaitEvent, 500);
                else
                    Thread.Sleep(4);
                if (_stop) break;

                if (DiagPath != null)
                {
                    long now = Environment.TickCount64;
                    if (now - _diagLastDump > 2000)
                    {
                        _diagLastDump = now;
                        try
                        {
                            lock (_hidLock)
                            {
                                var sb = new System.Text.StringBuilder();
                                sb.Append($"[{DateTime.Now:HH:mm:ss}] primaryEp=0x{_primaryInEndpoint:X2} reads:");
                                foreach (var kv in _readsPerEp) sb.Append($" 0x{kv.Key:X2}={kv.Value}");
                                sb.Append("  completions:");
                                foreach (var kv in _completionsPerEp) sb.Append($" 0x{kv.Key:X2}={kv.Value}");
                                sb.Append($"  queued={_frameQueue.Count}");
                                foreach (var kv in _pendingInterruptIn)
                                    sb.Append($" parked[0x{kv.Key:X2}]={kv.Value.Count}");
                                System.IO.File.AppendAllText(DiagPath, sb.ToString() + Environment.NewLine);
                            }
                        }
                        catch { }
                    }
                }
                if (!TryReadInputFrame(out var report)) continue;

                // The Switch 2 Pro persona sends nothing from here. The
                // frame is the state its own clock builds the next report
                // from (issue #66).
                if (_switch2 != null)
                {
                    _switch2.SetBody(report);
                    continue;
                }

                uint seq;
                lock (_hidLock)
                {
                    _lastInputReport = report;
                    var pending = PendingFor(_primaryInEndpoint);
                    if (pending.Count > 0)
                    {
                        seq = pending.Dequeue();
                        if (DiagPath != null)
                            _completionsPerEp[_primaryInEndpoint] =
                                _completionsPerEp.TryGetValue(_primaryInEndpoint, out var cc) ? cc + 1 : 1;
                    }
                    else
                    {
                        if (_frameQueue.Count >= MaxFrameQueue) _frameQueue.Dequeue();
                        _frameQueue.Enqueue(report);
                        continue;
                    }
                }
                SendInterruptInReply(seq, report);
            }
            catch
            {
                // Same containment contract as HMController.OutputPollLoop:
                // a transient failure must not kill the pump. The SDK's
                // per-frame SetEvent redelivers within one frame interval.
            }
        }
    }

    /// <summary>Seqlock read of the shared input section plus the wire
    /// report build, mirroring driver.c ReadSharedInput + the report
    /// assembly in its worker (driver.c:1240-1296).</summary>
    private bool TryReadInputFrame(out byte[] report)
    {
        report = Array.Empty<byte>();
        IntPtr view = _inputView;
        if (view == IntPtr.Zero) return false;

        Span<byte> snap = stackalloc byte[SharedMemoryIO.SHARED_INPUT_SIZE];
        uint seq1, seq2;
        int retries = 4;
        do
        {
            seq1 = (uint)Marshal.ReadInt32(view, 0);
            Thread.MemoryBarrier();
            unsafe
            {
                fixed (byte* dst = snap)
                    Buffer.MemoryCopy((void*)view, dst, snap.Length, snap.Length);
            }
            Thread.MemoryBarrier();
            seq2 = (uint)Marshal.ReadInt32(view, 0);
        } while ((seq1 != seq2 || (seq1 & 1) != 0) && --retries > 0);
        if (seq1 != seq2 || (seq1 & 1) != 0) return false;
        if (seq1 == _lastSharedSeqNo) return false; // no new frame
        _lastSharedSeqNo = seq1;

        int expectedSize = _builder.InputReportByteSize > 0 ? _builder.InputReportByteSize : 17;
        int extSize = BitConverter.ToInt32(snap[SharedMemoryIO.EXTENDED_SIZE_OFFSET..]);
        if (extSize > 0 && extSize <= SharedMemoryIO.EXTENDED_DATA_CAPACITY)
        {
            report = snap.Slice(SharedMemoryIO.EXTENDED_DATA_OFFSET, extSize).ToArray();
            return true;
        }

        int dataLen = BitConverter.ToInt32(snap[4..]);
        bool hasReportId = _builder.InputReportId != 0;
        int maxData = hasReportId ? Math.Max(expectedSize - 1, 16) : expectedSize;
        if (hasReportId && expectedSize > 1) maxData = expectedSize - 1;
        if (dataLen > maxData) dataLen = maxData;
        if (dataLen > SharedMemoryIO.DATA_CAPACITY) dataLen = SharedMemoryIO.DATA_CAPACITY;
        if (dataLen < 0) dataLen = 0;

        var r = new byte[expectedSize];
        if (hasReportId)
        {
            r[0] = _builder.InputReportId;
            snap.Slice(SharedMemoryIO.DATA_OFFSET, dataLen).CopyTo(r.AsSpan(1));
        }
        else
        {
            snap.Slice(SharedMemoryIO.DATA_OFFSET, dataLen).CopyTo(r);
        }
        report = r;
        return true;
    }

    // ── Output ring publish (driver.c PublishOutput discipline) ──────────

    public const byte SourceHidOutput = 0;       // driver.h HIDMAESTRO_OUTPUT_SOURCE_*
    public const byte SourceHidFeature = 1;
    public const byte SourceHidFeatureRead = 3;

    private readonly object _publishLock = new();

    private void PublishOutput(byte source, byte reportId, ReadOnlySpan<byte> data)
    {
        IntPtr view = _outputView;
        if (view == IntPtr.Zero) return;
        int size = Math.Min(data.Length, SharedMemoryIO.DATA_CAPACITY);
        lock (_publishLock)
        {
            uint newSeq;
            unsafe { newSeq = (uint)Interlocked.Increment(ref *(int*)view); }
            int slotIdx = (int)((newSeq - 1) % SharedMemoryIO.OUTPUT_RING_SLOTS);
            int slotBase = SharedMemoryIO.OUTPUT_HEADER_SIZE + slotIdx * SharedMemoryIO.OUTPUT_SLOT_SIZE;
            Marshal.WriteByte(view, slotBase + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SOURCE, source);
            Marshal.WriteByte(view, slotBase + SharedMemoryIO.OUTPUT_SLOT_OFFSET_REPORT_ID, reportId);
            Marshal.WriteInt16(view, slotBase + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SIZE, (short)size);
            if (size > 0)
            {
                unsafe
                {
                    fixed (byte* src = data)
                        Buffer.MemoryCopy(src, (byte*)view + slotBase + SharedMemoryIO.OUTPUT_SLOT_OFFSET_DATA,
                            SharedMemoryIO.DATA_CAPACITY, size);
                }
            }
            Thread.MemoryBarrier();
            Marshal.WriteInt32(view, slotBase + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SEQNO, (int)newSeq);
        }
        if (_outputEvent != IntPtr.Zero) SetEvent(_outputEvent);
    }

    // ── URB dispatch (called on the connection reader thread) ────────────

    public void HandleSubmit(in UsbipProtocol.CommandHeader cmd, byte[]? outPayload,
                             (uint Offset, uint Length)[]? isoPackets)
    {
        if (cmd.Ep == 0)
        {
            HandleControl(cmd, outPayload);
            return;
        }

        byte epAddr = (byte)(cmd.Ep | (cmd.IsIn ? 0x80u : 0u));
        if (!Descriptors.Endpoints.TryGetValue(epAddr, out var ep))
        {
            SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
            return;
        }

        if (ep.TransferType == 1) // isochronous → audio engine
        {
            var urb = new UsbAudioEngine.PendingIso
            {
                Seqnum = cmd.Seqnum,
                IsIn = cmd.IsIn,
                TransferBufferLength = cmd.TransferBufferLength,
                Packets = isoPackets ?? Array.Empty<(uint, uint)>(),
                OutPayload = outPayload,
                InterfaceNumber = ep.InterfaceNumber,
                AltSetting = ep.AltSetting,
            };
            Audio.SubmitIso(urb);
            return;
        }

        if (ep.TransferType == 3) // interrupt
        {
            if (cmd.IsIn)
            {
                if (_switch2 != null && epAddr == _primaryInEndpoint)
                {
                    SubmitSwitch2Read(cmd.Seqnum);
                    return;
                }

                byte[]? frame = null;
                lock (_hidLock)
                {
                    // Only the primary HID interface's endpoint has frames.
                    // A secondary interface (the Deck's keyboard and mouse)
                    // parks its reads and never completes them, which is
                    // exactly what the real device does once lizard mode is
                    // off: those pipes go quiet rather than reporting.
                    if (DiagPath != null)
                        _readsPerEp[epAddr] = _readsPerEp.TryGetValue(epAddr, out var rc) ? rc + 1 : 1;
                    if (epAddr == _primaryInEndpoint && _frameQueue.Count > 0)
                        frame = _frameQueue.Dequeue();
                    else
                        PendingFor(epAddr).Enqueue(cmd.Seqnum);
                }
                if (frame != null) SendInterruptInReply(cmd.Seqnum, frame);
                return;
            }

            // Interrupt OUT: a full output report, Report ID first when the
            // descriptor declares IDs. Same split as driver.c WRITE_REPORT.
            var payload = outPayload ?? Array.Empty<byte>();
            byte rid = payload.Length > 0 ? payload[0] : (byte)0;
            PublishOutput(SourceHidOutput, rid,
                payload.Length > 0 ? payload.AsSpan(1) : ReadOnlySpan<byte>.Empty);
            SendRetSubmit(cmd.Seqnum, 0, payload.Length, null);
            return;
        }

        if (ep.TransferType == 2 && _switch2 != null) // bulk: the Switch 2 command channel
        {
            if (cmd.IsIn)
            {
                // Park first, then serve in arrival order. A read that finds
                // no reply stays parked until a command produces one or the
                // host unlinks it.
                lock (_hidLock)
                {
                    PendingFor(epAddr).Enqueue(cmd.Seqnum);
                    _parkedBulkLength[cmd.Seqnum] = Math.Max(0, cmd.TransferBufferLength);
                }
                if (DeviceOrchestrator.DiagEnabled)
                    DeviceOrchestrator.LogDiag($"switch2 bulk in {BusId}: read {cmd.Seqnum} for {cmd.TransferBufferLength} bytes");
                CompleteBulkIn();
                return;
            }

            var command = outPayload ?? Array.Empty<byte>();
            bool started;
            // Under the report lock, so a report built before this command
            // is on the wire before the command's own answer. A host that
            // selects report 0x05 then never sees a 0x09 behind the reply.
            lock (_streamSendLock)
            {
                started = _switch2.HandleBulkOut(command, System.Diagnostics.Stopwatch.GetTimestamp());
                // A start owes nothing from before it. Its first report is
                // the clock's, one interval after this command.
                if (started) lock (_hidLock) _reportOwed = false;
            }
            SendRetSubmit(cmd.Seqnum, 0, command.Length, null);
            CompleteBulkIn();
            if (started) _streamWake!.Set();
            return;
        }

        SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
    }

    // ── Switch 2 Pro Controller: bulk replies and the report clock ───────

    /// <summary>Hand waiting replies to parked bulk IN reads, oldest of each
    /// first. A read gets at most its buffer length of one reply and the
    /// rest stays at the head of the queue, so the pad's 80-byte flash reply
    /// goes out as 64 bytes and then 16 to a host that reads in 64s, which
    /// is how SDL reads it. Only the connection's reader thread calls
    /// this.</summary>
    private void CompleteBulkIn()
    {
        while (true)
        {
            uint seq;
            byte[] chunk;
            lock (_hidLock)
            {
                var pending = PendingFor(_bulkInEndpoint);
                if (pending.Count == 0 || !_switch2!.HasReply) return;
                seq = pending.Dequeue();
                _parkedBulkLength.Remove(seq, out int capacity);
                chunk = _switch2.TakeReply(capacity) ?? Array.Empty<byte>();
            }
            if (DeviceOrchestrator.DiagEnabled)
                DeviceOrchestrator.LogDiag($"switch2 bulk in {BusId}: read {seq} gets {chunk.Length} bytes {Convert.ToHexString(chunk)}");
            SendRetSubmit(seq, 0, chunk.Length, chunk);
        }
    }

    /// <summary>An interrupt IN read on the persona's HID endpoint. It parks
    /// until the clock's next tick, unless a tick already passed with no
    /// read to answer. With reports off it parks and stays parked: the pad
    /// answers every poll with NAK until a host sends 03/0D.</summary>
    private void SubmitSwitch2Read(uint seqnum)
    {
        lock (_streamSendLock)
        {
            byte[] report;
            lock (_hidLock)
            {
                if (DiagPath != null)
                    _readsPerEp[_primaryInEndpoint] =
                        _readsPerEp.TryGetValue(_primaryInEndpoint, out var rc) ? rc + 1 : 1;
                if (!_reportOwed || !_switch2!.ReportsOn)
                {
                    PendingFor(_primaryInEndpoint).Enqueue(seqnum);
                    return;
                }
                _reportOwed = false;
                report = NextSwitch2Report();
            }
            SendInterruptInReply(seqnum, report);
        }
    }

    /// <summary>One tick of the report clock: answer the oldest parked read
    /// with a report built now, or leave one owed.
    /// <paramref name="startedAt"/> names the run the tick belongs to, by
    /// the time of the command that started it. A tick of a run that has
    /// ended, or that a later start has replaced, sends nothing.</summary>
    private void SendSwitch2Report(long startedAt)
    {
        lock (_streamSendLock)
        {
            uint seq;
            byte[] report;
            lock (_hidLock)
            {
                if (!_switch2!.ReportsOnSince(out long since) || since != startedAt) return;
                var pending = PendingFor(_primaryInEndpoint);
                if (pending.Count == 0)
                {
                    _reportOwed = true;
                    return;
                }
                seq = pending.Dequeue();
                report = NextSwitch2Report();
            }
            SendInterruptInReply(seq, report);
        }
    }

    /// <summary>Caller holds <see cref="_hidLock"/>.</summary>
    private byte[] NextSwitch2Report()
    {
        var report = _switch2!.BuildNextReport();
        if (DiagPath != null)
            _completionsPerEp[_primaryInEndpoint] =
                _completionsPerEp.TryGetValue(_primaryInEndpoint, out var cc) ? cc + 1 : 1;
        return report;
    }

    /// <summary>The report clock. Parked while reports are off. Once a
    /// command turns them on, the first report is due one interval after
    /// that command (3.7 ms on the pad) and one every interval from then on,
    /// at a fixed rate from a high-resolution timer. A clock that was held
    /// up skips the ticks it missed and sends no burst. SubmitState only
    /// replaces the state a report is built from: this loop is the one
    /// thing that sends a report.</summary>
    private void Switch2StreamLoop()
    {
        long interval = System.Diagnostics.Stopwatch.Frequency * Switch2ReportIntervalMs / 1000;
        var waits = new[] { _streamTimer, _streamWake!.SafeWaitHandle.DangerousGetHandle() };
        bool running = false;
        long startedAt = 0, due = 0;
        while (!_stop)
        {
            try
            {
                if (!_switch2!.ReportsOnSince(out long since))
                {
                    if (running)
                    {
                        running = false;
                        lock (_hidLock) _reportOwed = false;
                    }
                    _streamWake.WaitOne(500);
                    continue;
                }

                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!running || since != startedAt)
                {
                    // A new run. It may follow a stop this loop never saw,
                    // so the command's time tells the runs apart.
                    running = true;
                    startedAt = since;
                    due = since + interval;
                }
                if (now >= due)
                {
                    SendSwitch2Report(startedAt);
                    // The next tick of the same grid. A clock that was held
                    // up past one or more ticks skips them.
                    due += interval;
                    if (due <= now) due += ((now - due) / interval + 1) * interval;
                    continue;
                }

                long wait = due - now;
                if (_streamTimer != IntPtr.Zero)
                {
                    long rel = -Math.Max(1, wait * 10_000_000L / System.Diagnostics.Stopwatch.Frequency); // relative, 100 ns units
                    if (SetWaitableTimer(_streamTimer, ref rel, 0, IntPtr.Zero, IntPtr.Zero, false)
                        && WaitForMultipleObjects(2, waits, false, 100) != WAIT_FAILED)
                        continue;
                }
                // No high-resolution timer (Windows 10 before 1803) or a
                // failed wait: the plain wait, coarser but still running.
                _streamWake.WaitOne((int)Math.Max(1, wait * 1000 / System.Diagnostics.Stopwatch.Frequency));
            }
            catch
            {
                // The input pump's containment contract: a transient
                // failure must not end the clock.
            }
        }
    }

    public void HandleUnlink(uint seqnum, uint victimSeqnum)
    {
        bool removed = Audio.TryUnlink(victimSeqnum);
        if (!removed)
        {
            lock (_hidLock)
            {
                // Queue<T> has no random removal; rebuild without the victim.
                Queue<uint>? holder = null;
                foreach (var q in _pendingInterruptIn.Values)
                    if (q.Contains(victimSeqnum)) { holder = q; break; }
                if (holder != null)
                {
                    var keep = new List<uint>(holder.Count);
                    while (holder.Count > 0)
                    {
                        uint s = holder.Dequeue();
                        if (s != victimSeqnum) keep.Add(s);
                        else removed = true;
                    }
                    foreach (var s in keep) holder.Enqueue(s);
                }
                if (_parkedBulkLength.Remove(victimSeqnum)) _bulkReadsCanceled++;
            }
        }
        // Protocol rule (usbip-win2 wsk_receive.cpp cites usbip_protocol.rst):
        // -ECONNRESET when the URB was still queued, 0 when already answered.
        if (DeviceOrchestrator.DiagEnabled)
            DeviceOrchestrator.LogDiag($"usbip unlink {BusId}: {victimSeqnum} {(removed ? "was parked" : "had already been answered")}");
        var conn = _connection;
        if (conn == null) return;
        try { conn.SendRetUnlink(seqnum, removed ? -UsbipProtocol.EConnReset : 0); } catch { }
    }

    // ── Control transfers ────────────────────────────────────────────────

    private void HandleControl(in UsbipProtocol.CommandHeader cmd, byte[]? outPayload)
    {
        Span<byte> setup = stackalloc byte[8];
        BitConverter.TryWriteBytes(setup, cmd.Setup);
        byte bmRequestType = setup[0];
        byte bRequest = setup[1];
        ushort wValue = (ushort)(setup[2] | (setup[3] << 8));
        ushort wIndex = (ushort)(setup[4] | (setup[5] << 8));
        ushort wLength = (ushort)(setup[6] | (setup[7] << 8));

        byte type = (byte)((bmRequestType >> 5) & 0x03);      // 0 standard, 1 class, 2 vendor
        byte recipient = (byte)(bmRequestType & 0x1F);        // 0 device, 1 interface, 2 endpoint, 3 other
        bool deviceToHost = (bmRequestType & 0x80) != 0;

        // Hub port reset arrives as USB_RT_PORT SET_FEATURE(PORT_RESET)
        // (usbip-win2 device_ioctl.cpp make_reset_port: "meaningless for a
        // server which ignores it"). Reset to the unconfigured state.
        if (bmRequestType == 0x23 && bRequest == 0x03)
        {
            ResetDeviceState();
            SendRetSubmit(cmd.Seqnum, 0, 0, null);
            return;
        }

        if (type == 0) // standard
        {
            switch (bRequest)
            {
                case 0x06: // GET_DESCRIPTOR
                {
                    byte descType = (byte)(wValue >> 8);
                    byte descIndex = (byte)(wValue & 0xFF);
                    byte[]? d = recipient == 1
                        ? Descriptors.GetHidDescriptor(descType, (byte)(wIndex & 0xFF))
                        : Descriptors.GetDescriptor(descType, descIndex, wIndex);
                    if (d == null) { SendError(cmd.Seqnum, -UsbipProtocol.EPipe); return; }
                    int n = Math.Min(d.Length, wLength);
                    SendRetSubmit(cmd.Seqnum, 0, n, d.AsSpan(0, n).ToArray());
                    return;
                }
                case 0x00: // GET_STATUS
                {
                    if (!deviceToHost) break;
                    // Device: self-powered bit from bmAttributes 0xC0. Interface/endpoint: zero.
                    ushort status = recipient == 0 ? (ushort)0x0001 : (ushort)0x0000;
                    var d = new[] { (byte)(status & 0xFF), (byte)(status >> 8) };
                    int n = Math.Min(d.Length, wLength);
                    SendRetSubmit(cmd.Seqnum, 0, n, d.AsSpan(0, n).ToArray());
                    return;
                }
                case 0x09: // SET_CONFIGURATION
                    _configurationValue = (byte)(wValue & 0xFF);
                    ResetAltSettings();
                    SendRetSubmit(cmd.Seqnum, 0, 0, null);
                    return;
                case 0x08: // GET_CONFIGURATION
                {
                    var d = new[] { _configurationValue };
                    int n = Math.Min(1, (int)wLength);
                    SendRetSubmit(cmd.Seqnum, 0, n, n > 0 ? d : null);
                    return;
                }
                case 0x0B: // SET_INTERFACE
                    Audio.SetAltSetting((byte)(wIndex & 0xFF), (byte)(wValue & 0xFF));
                    SendRetSubmit(cmd.Seqnum, 0, 0, null);
                    return;
                case 0x0A: // GET_INTERFACE
                {
                    var d = new[] { Audio.GetAltSetting((byte)(wIndex & 0xFF)) };
                    SendRetSubmit(cmd.Seqnum, 0, Math.Min(1, (int)wLength),
                        wLength > 0 ? d : null);
                    return;
                }
                case 0x01: // CLEAR_FEATURE (ENDPOINT_HALT arrives via ude clear_endpoint_stall)
                case 0x03: // SET_FEATURE
                    SendRetSubmit(cmd.Seqnum, 0, 0, null);
                    return;
                case 0x05: // SET_ADDRESS (UDE normally handles this itself)
                    SendRetSubmit(cmd.Seqnum, 0, 0, null);
                    return;
            }
            SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
            return;
        }

        if (type == 1 && recipient == 1) // class, interface recipient
        {
            byte ifaceNum = (byte)(wIndex & 0xFF);
            if (Descriptors.IsHidInterface(ifaceNum))
            {
                HandleHidClassRequest(cmd, bRequest, wValue, wLength, deviceToHost, outPayload,
                                      ifaceNum == Descriptors.HidInterfaceNumber);
                return;
            }

            // UAC1 feature-unit request: wIndex high byte is the entity.
            byte unitId = (byte)(wIndex >> 8);
            byte selector = (byte)(wValue >> 8);
            byte channel = (byte)(wValue & 0xFF);
            if (deviceToHost)
            {
                var d = Audio.HandleUacGet(bRequest, unitId, selector, channel, wLength);
                if (d == null) { SendError(cmd.Seqnum, -UsbipProtocol.EPipe); return; }
                int n = Math.Min(d.Length, wLength);
                SendRetSubmit(cmd.Seqnum, 0, n, d.AsSpan(0, n).ToArray());
            }
            else
            {
                bool ok = bRequest == 0x01 // SET_CUR
                    && Audio.HandleUacSet(unitId, selector, channel,
                        outPayload ?? Array.Empty<byte>());
                if (ok) SendRetSubmit(cmd.Seqnum, 0, outPayload?.Length ?? 0, null);
                else SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
            }
            return;
        }

        if (type == 1 && recipient == 2) // class, endpoint recipient: UAC sampling frequency
        {
            // The high-speed blob advertises no endpoint controls
            // (AS iso endpoint bmAttributes 0x00), so usbaudio does not
            // send these on the operating speed. Accept SET_CUR / answer
            // GET_CUR for SAMPLING_FREQ anyway: the device has exactly one
            // discrete rate and refusing a redundant set would fail a host
            // that sends it regardless.
            byte selector = (byte)(wValue >> 8);
            if (selector == 0x01)
            {
                if (deviceToHost)
                {
                    var freq = new byte[] { 0x80, 0xBB, 0x00 }; // 48000, 3-byte UAC1 rate
                    int n = Math.Min(freq.Length, wLength);
                    SendRetSubmit(cmd.Seqnum, 0, n, freq.AsSpan(0, n).ToArray());
                }
                else
                {
                    SendRetSubmit(cmd.Seqnum, 0, outPayload?.Length ?? 0, null);
                }
                return;
            }
            SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
            return;
        }

        // Microsoft OS 1.0 feature descriptors (issue #66), for a persona
        // that declares them. Windows asks with a vendor request whose
        // wIndex names the feature: 4 the extended compatible ID, 5 the
        // extended properties. Only wIndex is read. bRequest is the vendor
        // code Windows stored for this VID, PID and revision, which a real
        // pad of the same revision may have set before this persona was ever
        // attached, so every code is answered. Windows reads each header
        // before the whole descriptor, hence the cut to wLength.
        if (type == 2 && deviceToHost)
        {
            var feature = Descriptors.GetMicrosoftOsFeature(wIndex);
            if (feature != null)
            {
                int n = Math.Min(feature.Length, wLength);
                SendRetSubmit(cmd.Seqnum, 0, n, feature.AsSpan(0, n).ToArray());
                return;
            }
        }

        SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
    }

    private void HandleHidClassRequest(in UsbipProtocol.CommandHeader cmd, byte bRequest,
        ushort wValue, ushort wLength, bool deviceToHost, byte[]? outPayload,
        bool primaryInterface = true)
    {
        byte reportType = (byte)(wValue >> 8); // 1 input, 2 output, 3 feature
        byte reportId = (byte)(wValue & 0xFF);

        // A secondary HID interface (the Deck's keyboard and mouse) carries
        // no reports of its own here. SET_IDLE and friends still succeed,
        // because a host that cannot configure the interface treats the
        // device as broken; report traffic stalls, as it does on the real
        // pad with lizard mode off.
        if (!primaryInterface)
        {
            switch (bRequest)
            {
                case 0x0A: // SET_IDLE
                case 0x0B: // SET_PROTOCOL
                    SendRetSubmit(cmd.Seqnum, 0, 0, null);
                    return;
                case 0x02 when deviceToHost: // GET_IDLE
                    SendRetSubmit(cmd.Seqnum, 0, Math.Min(1, (int)wLength),
                        wLength > 0 ? new byte[] { 0 } : null);
                    return;
                default:
                    SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
                    return;
            }
        }

        switch (bRequest)
        {
            case 0x01 when deviceToHost: // GET_REPORT
            {
                if (reportType == 0x01)
                {
                    byte[] snapshot;
                    // The Switch 2 Pro persona answers with the report it
                    // would send next, in the layout asked for, and does
                    // not count it.
                    if (_switch2 != null) snapshot = _switch2.PeekReport(reportId);
                    else lock (_hidLock) snapshot = _lastInputReport;
                    int n = Math.Min(snapshot.Length, wLength);
                    SendRetSubmit(cmd.Seqnum, 0, n, snapshot.AsSpan(0, n).ToArray());
                    return;
                }
                if (reportType == 0x03)
                {
                    var stub = BuildFeatureStub(reportId, wLength);
                    if (stub == null) { SendError(cmd.Seqnum, -UsbipProtocol.EPipe); return; }
                    PublishOutput(SourceHidFeatureRead, reportId, ReadOnlySpan<byte>.Empty);
                    int n = Math.Min(stub.Length, wLength);
                    SendRetSubmit(cmd.Seqnum, 0, n, stub.AsSpan(0, n).ToArray());
                    return;
                }
                break;
            }
            case 0x09 when !deviceToHost: // SET_REPORT
            {
                var payload = outPayload ?? Array.Empty<byte>();
                byte rid = payload.Length > 0 ? payload[0] : reportId;
                var data = payload.Length > 0 ? payload.AsSpan(1) : ReadOnlySpan<byte>.Empty;
                // A protocol whose feature reports carry no report id (the
                // Steam Deck's) answers the NEXT GET_REPORT according to
                // the message this write opened. MessageByte says where
                // that message id sits: byte 0 when the descriptor declares
                // no report ids at all, byte 1 when one precedes it.
                if (reportType == 0x03 && _stubs != null && _stubs.MatchesLastMessage
                    && payload.Length > _stubs.MessageByte)
                {
                    _lastFeatureMessage = payload[_stubs.MessageByte];
                    // Valve frames a command-channel write as
                    // [message][length][parameters...], so the message's
                    // first parameter is two bytes on. It selects the
                    // answer for a message carrying several, which
                    // ID_GET_STRING_ATTRIBUTE does.
                    int paramAt = _stubs.MessageByte + 2;
                    _lastFeatureParam = payload.Length > paramAt ? payload[paramAt] : -1;
                    _lastFeaturePayload = payload;
                }
                PublishOutput(reportType == 0x03 ? SourceHidFeature : SourceHidOutput, rid, data);
                SendRetSubmit(cmd.Seqnum, 0, payload.Length, null);
                return;
            }
            case 0x0A when !deviceToHost: // SET_IDLE
                SendRetSubmit(cmd.Seqnum, 0, 0, null);
                return;
            case 0x02 when deviceToHost: // GET_IDLE
                SendRetSubmit(cmd.Seqnum, 0, Math.Min(1, (int)wLength),
                    wLength > 0 ? new byte[] { 0 } : null);
                return;
        }
        // GET/SET_PROTOCOL and anything else: the pad is not a boot device;
        // a real one stalls these.
        SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
    }

    /// <summary>driver.c's Sony Get_Feature stub table
    /// (IOCTL_UMDF_HID_GET_FEATURE handler), sized against wLength the way
    /// the driver sizes against the IOCTL output buffer.</summary>
    private byte[]? BuildFeatureStub(byte reportId, ushort wLength)
    {
        // Profile-declared stubs win (issue #56): a persona whose feature
        // protocol is its own carries the answers as data rather than as a
        // branch in here. The Sony table below stays code because it
        // synthesizes per-controller values (the pairing MAC) rather than
        // serving constants.
        if (_stubs != null)
        {
            byte key = _stubs.MatchesLastMessage ? _lastFeatureMessage : reportId;
            var declared = _stubs.Lookup(key, _lastFeatureParam, wLength, _lastFeaturePayload);
            if (declared != null)
            {
                // A protocol riding report ids answers with the id it was
                // asked on in byte 0, the shape the Sony table below builds
                // by hand. MessageByte is that prefix's width, and is zero
                // for a protocol declaring no report ids at all.
                if (_stubs.MessageByte > 0 && declared.Length > 0) declared[0] = reportId;
                return declared;
            }
        }

        if (Descriptors.VendorId != 0x054C) return null;
        switch (reportId)
        {
            case 0x05:
                if (wLength < 41) return null;
                {
                    // A DS4 orders this report's gyro pairs differently from
                    // the DualSense, exactly as driver.c serves it. Every
                    // composite persona is USB, so no Bluetooth CRC.
                    ushort pid = Descriptors.ProductId;
                    bool ds4 = pid == 0x05C4 || pid == 0x09CC || pid == 0x0BA0;
                    var p = new byte[41];
                    p[0] = reportId;
                    (ds4 ? SonyCalibrationDs4Bt : SonyCalibration).CopyTo(p, 1);
                    return p;
                }
            case 0x09:
                if (wLength < 20) return null;
                {
                    var p = new byte[20];
                    p[0] = reportId;
                    p[1] = 0x02; p[2] = 0x48; p[3] = 0x4D; // locally administered, 'H' 'M'
                    p[4] = 0x00; p[5] = 0x00; p[6] = (byte)_index;
                    return p;
                }
            case 0x20:
                if (wLength < 64) return null;
                {
                    var p = (byte[])Ds5FirmwareInfo.Clone();
                    // DualSense Edge is a different firmware line: Sony's
                    // updater data records the base pad as type 0x0004 and
                    // the Edge as type 0x0044. See driver.c for why those
                    // two offsets are identified rather than guessed, and
                    // for what in this blob is still the base pad's.
                    if (Descriptors.ProductId == 0x0DF2)
                    {
                        p[22] = 0x44; p[23] = 0x00;   // swSeries      0x0044
                        p[44] = 0x17; p[45] = 0x02;   // updateVersion 0x0217
                    }
                    return p;
                }
            case 0x12:
                // DS4 pairing info over USB. The DS4's equivalent of 0x09,
                // and the one Sony read whose absence is fatal rather than
                // cosmetic: hid-playstation's caller aborts device creation
                // when it fails. MAC at bytes 1..6, non-zero, locally
                // administered, matching the 0x09 path exactly.
                if (wLength < 16) return null;
                {
                    var p = new byte[16];
                    p[0] = reportId;
                    p[1] = 0x02; p[2] = 0x48; p[3] = 0x4D; // locally administered, 'H' 'M'
                    p[4] = 0x00; p[5] = 0x00; p[6] = (byte)_index;
                    return p;
                }
            case 0x22:
                // Bluetooth patch info. Zero past the report ID is the real
                // answer for a pad carrying no patch, and dualsense-tester
                // skips the row on a falsy value. It only has to exist
                // because the real 0x20 above opens the traceability branch
                // that reads it; see driver.c for the full reasoning.
                if (wLength < 64) return null;
                { var p = new byte[64]; p[0] = reportId; return p; }
            case 0x02:
                if (wLength >= 41) { var p = new byte[41]; p[0] = reportId; SonyCalibration.CopyTo(p, 1); return p; }
                if (wLength >= 37) { var p = new byte[37]; p[0] = reportId; SonyCalibration.CopyTo(p, 1); return p; }
                return null;
            case 0xA3:
                if (wLength < 49) return null;
                { var p = (byte[])Ds4FirmwareInfo.Clone(); return p; }
            default:
                return null;
        }
    }

    /// <summary>Sony motion calibration, byte-for-byte the same payload
    /// driver.c serves (g_SonyCalibration, issues #43 and #64). Written at
    /// offset 1, after the report id. It is the identity calibration for
    /// the pad's nominal units, 8192 per g and 16 per degree/second.
    /// driver.c shows the arithmetic in each reader. The composite lane must
    /// not diverge from the UMDF2 lane here: a consumer reading calibration
    /// from a composite persona has to see exactly what it sees from the
    /// plain profile, or the two backends disagree about the same
    /// device.</summary>
    private static readonly byte[] SonyCalibration =
    {
        0x00, 0x00,  // gyro_pitch_bias
        0x00, 0x00,  // gyro_yaw_bias
        0x00, 0x00,  // gyro_roll_bias
        0x40, 0x1F,  // gyro_pitch_plus    +8000
        0xC0, 0xE0,  // gyro_pitch_minus   -8000
        0x40, 0x1F,  // gyro_yaw_plus      +8000
        0xC0, 0xE0,  // gyro_yaw_minus     -8000
        0x40, 0x1F,  // gyro_roll_plus     +8000
        0xC0, 0xE0,  // gyro_roll_minus    -8000
        0xF4, 0x01,  // gyro_speed_plus     +500
        0xF4, 0x01,  // gyro_speed_minus    +500
        0x00, 0x20,  // acc_x_plus         +8192
        0x00, 0xE0,  // acc_x_minus        -8192
        0x00, 0x20,  // acc_y_plus         +8192
        0x00, 0xE0,  // acc_y_minus        -8192
        0x00, 0x20,  // acc_z_plus         +8192
        0x00, 0xE0,  // acc_z_minus        -8192
    };

    /// <summary>The same calibration in a DualShock 4's report 0x05 order,
    /// pitch+ yaw+ roll+ then pitch- yaw- roll-, byte-for-byte driver.c's
    /// g_SonyCalibrationDs4Bt (issue #64).</summary>
    private static readonly byte[] SonyCalibrationDs4Bt =
    {
        0x00, 0x00,  // gyro_pitch_bias
        0x00, 0x00,  // gyro_yaw_bias
        0x00, 0x00,  // gyro_roll_bias
        0x40, 0x1F,  // gyro_pitch_plus    +8000
        0x40, 0x1F,  // gyro_yaw_plus      +8000
        0x40, 0x1F,  // gyro_roll_plus     +8000
        0xC0, 0xE0,  // gyro_pitch_minus   -8000
        0xC0, 0xE0,  // gyro_yaw_minus     -8000
        0xC0, 0xE0,  // gyro_roll_minus    -8000
        0xF4, 0x01,  // gyro_speed_plus     +500
        0xF4, 0x01,  // gyro_speed_minus    +500
        0x00, 0x20,  // acc_x_plus         +8192
        0x00, 0xE0,  // acc_x_minus        -8192
        0x00, 0x20,  // acc_y_plus         +8192
        0x00, 0xE0,  // acc_y_minus        -8192
        0x00, 0x20,  // acc_z_plus         +8192
        0x00, 0xE0,  // acc_z_minus        -8192
    };

    /// <summary>DS5 firmware info, byte-for-byte the same payload driver.c
    /// serves (ds5FirmwareInfo, issue #43). Captured from a real wired
    /// DualSense during an F1 22 startup trace. ASCII build date
    /// "Jul  4 2025" and time "10:10:32", then fwType 3, hwInfo 0x1310 and
    /// the firmware versions.
    ///
    /// <para>Served verbatim. F1 22 validates this blob and abandons the
    /// device on the zeros it used to get, and which field it validates is
    /// not known, so no byte here is synthesized. See driver.c for the
    /// offset agreement between hid-playstation.c and dualsense-tester, and
    /// for why WinUHid's own default is not used.</para></summary>
    private static readonly byte[] Ds5FirmwareInfo =
    {
        0x20, 0x4A, 0x75, 0x6C, 0x20, 0x20, 0x34, 0x20,
        0x32, 0x30, 0x32, 0x35, 0x31, 0x30, 0x3A, 0x31,
        0x30, 0x3A, 0x33, 0x32, 0x03, 0x00, 0x04, 0x00,
        0x10, 0x13, 0x00, 0x00, 0x2A, 0x00, 0x10, 0x01,
        0x01, 0xC8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x30, 0x06, 0x00, 0x00,
        0x3C, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x02, 0x00,
        0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    /// <summary>DS4 firmware / hardware info, verbatim from WinUHid's
    /// WinUHidPS4.cpp. ASCII build date "Aug  3 2013" and time "07:01:12"
    /// followed by the hardware and firmware words.</summary>
    private static readonly byte[] Ds4FirmwareInfo =
    {
        0xA3, 0x41, 0x75, 0x67, 0x20, 0x20, 0x33, 0x20,
        0x32, 0x30, 0x31, 0x33, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x30, 0x37, 0x3A, 0x30, 0x31, 0x3A, 0x31,
        0x32, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x01, 0x00, 0x31, 0x03, 0x00, 0x00,
        0x00, 0x49, 0x00, 0x05, 0x00, 0x00, 0x80, 0x03,
        0x00
    };

    private void ResetDeviceState()
    {
        _configurationValue = 0;
        ResetAltSettings();
        Audio.Clear();
        lock (_hidLock)
        {
            foreach (var q in _pendingInterruptIn.Values) q.Clear();
            _frameQueue.Clear();
        }
        ResetSwitch2State();
    }

    private void ResetAltSettings()
    {
        foreach (var kv in Descriptors.Endpoints)
        {
            if (kv.Value.TransferType == 1)
                Audio.ResetAltSetting(kv.Value.InterfaceNumber);
        }
    }

    // ── Reply plumbing ───────────────────────────────────────────────────
    //
    // Sends are guarded: the pacing pump and the input pump complete URBs
    // from their own threads, and a socket being torn down mid-completion
    // must not take a background thread (and with it the process) down.
    // The connection's own reader thread notices the closed socket and
    // runs the detach path.

    private void SendInterruptInReply(uint seqnum, byte[] frame)
        => SendRetSubmit(seqnum, 0, frame.Length, frame);

    private void SendError(uint seqnum, int status)
        => SendRetSubmit(seqnum, status, 0, null);

    private void SendRetSubmit(uint seqnum, int status, int actualLength, byte[]? inPayload)
    {
        var c = _connection;
        if (c == null) return;
        try { c.SendRetSubmitNonIso(seqnum, status, actualLength, inPayload); } catch { }
    }

    /// <summary>Completion callback from the audio engine's pacing thread.
    /// Builds the isochronous RET_SUBMIT per the 0.9.7.5 receive rules.</summary>
    private void CompleteIsoOnWire(UsbAudioEngine.PendingIso p, byte[]? inCompacted, int perPacketActual)
    {
        var c = _connection;
        if (c == null) return;
        try { c.SendRetSubmitIso(p, inCompacted, perPacketActual); } catch { }
    }

    public void Dispose()
    {
        _stop = true;
        _connection = null;
        Audio.Dispose();
        try { _inputThread.Join(600); } catch { }
        if (_inputWaitEvent != IntPtr.Zero)
        {
            CloseHandle(_inputWaitEvent);
            _inputWaitEvent = IntPtr.Zero;
        }
        if (_streamThread != null)
        {
            // _stop is set above. Wake the clock and wait for it before the
            // handles it waits on go away.
            try { _streamWake!.Set(); } catch { }
            try { _streamThread.Join(600); } catch { }
            if (_streamTimer != IntPtr.Zero) CloseHandle(_streamTimer);
            _streamWake!.Dispose();
        }
    }

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;
    private const uint WAIT_FAILED = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, IntPtr name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period,
        IntPtr completionRoutine, IntPtr arg, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
}
