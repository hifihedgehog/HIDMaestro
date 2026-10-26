using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
/// <item>Feature reads: the profile's answer table
/// (<see cref="FeatureStubTable"/>), the one table the UMDF2 driver serves
/// as well, including the HID_FEATURE_READ ring notification that drives
/// the extendedReport.armOn watcher for an entry that publishes one.</item>
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
internal sealed partial class UsbipEmulatedDevice : IDisposable
{
    public UsbDescriptorSet Descriptors { get; }
    public UsbAudioEngine Audio { get; }
    public string BusId { get; }

    /// <summary>The vhci port this device is attached on, 0 while it is
    /// not. The server sets it on every import, the driver's own re-attach
    /// included, which lands on a new port. Detaching goes by location,
    /// never by this number (<see cref="VhciClient.DetachLocation"/>).</summary>
    internal int VhciPort => Volatile.Read(ref _vhciPort);
    private int _vhciPort;

    /// <summary>Called with the new vhci port whenever it changes, for the
    /// owner record, under <see cref="_portLock"/>.</summary>
    internal Action<int>? VhciPortChanged { get; set; }

    /// <summary>Called with the vhci port each time the device is plugged
    /// in on one, the host controller's own re-imports after a drop
    /// included, under <see cref="_portLock"/>. A WinUSB binding sets it to
    /// bind the device again (<see cref="WinUsbBinding"/>).</summary>
    internal Action<int>? Reattached { get; set; }

    // Every change of the vhci port, in memory and in the record, and the
    // release of the connection on a drop, so a port found for an import
    // is never written after the drop that ended it.
    private readonly object _portLock = new();

    /// <summary>The port the create's own attach returned, unless the
    /// import has dropped already.</summary>
    internal void SetVhciPortIfAttached(int port)
    {
        lock (_portLock) if (Volatile.Read(ref _connection) != null) SetVhciPortLocked(port);
    }

    /// <summary>The port the driver plugged <paramref name="connection"/>'s
    /// import into, while that connection still holds the device.</summary>
    internal void SetVhciPortFor(UsbipServer.Connection connection, int port)
    {
        lock (_portLock) if (Volatile.Read(ref _connection) == connection) SetVhciPortLocked(port);
    }

    private void SetVhciPortLocked(int port)
    {
        if (Interlocked.Exchange(ref _vhciPort, port) == port) return;
        try { VhciPortChanged?.Invoke(port); } catch { /* the record is best-effort */ }
        if (port > 0)
            try { Reattached?.Invoke(port); } catch { /* the binding logs its own failures */ }
    }

    /// <summary>Whether <paramref name="connection"/> holds the device.</summary>
    internal bool IsClaimedBy(UsbipServer.Connection connection) => Volatile.Read(ref _connection) == connection;

    /// <summary>True once Dispose has begun. The device answers nothing
    /// after that, so its connection's reader ends the connection.</summary>
    internal bool IsDisposed => _stop;

    /// <summary>The controller's transport index.</summary>
    private readonly int _index;
    public uint Devid { get; }

    private readonly HidReportBuilder _builder;
    private readonly IntPtr _inputView;
    private readonly IntPtr _outputView;
    private readonly IntPtr _outputEvent;
    private IntPtr _inputWaitEvent;
    // Set once the thread has started, as are the clocks' threads below.
    private readonly Thread? _inputThread;
    private volatile bool _stop;

    // Teardown. StopAndRelease sets _stop under _lifetimeLock, and the input
    // pump reads the input section under it, so no read follows Dispose's
    // return and the controller unmaps the section after that. Each worker
    // thread closes the handles it waits on as it ends, and a clock's event
    // is set from other threads only under this lock while it is still
    // open: a thread Dispose's bounded join gave up on never waits on a
    // closed handle, and a late call from the connection never sets a
    // closed event. No lock is held across consumer code, so Dispose never
    // waits for a handler on the connection's thread. It waits for one on
    // the audio pump (OutFrames) or the audio event thread (ControlChanged,
    // StreamingChanged) only through that thread's bounded join, 500 ms
    // each, so up to a second when both hold a handler.
    private readonly object _lifetimeLock = new();
    // The input pump's wait handle, closed by the pump as it ends.
    private bool _inputWakeClosed;
    // Set under _publishLock: nothing writes the output section after
    // Dispose.
    private bool _outputClosed;

    // The connection's serialized sender; null until attached. Claimed
    // and released with CompareExchange so a racing second import can
    // never steal or clear another connection's ownership.
    private UsbipServer.Connection? _connection;

    private readonly object _hidLock = new();
    // Reports waiting for a read, each in a buffer of _reportCapacity bytes
    // the device keeps. A buffer goes back to _spareFrames once its read is
    // answered or the queue drops it, so a frame allocates nothing: at most
    // MaxFrameQueue + 1 buffers ever exist, the queue's and one the
    // connection's reader is sending from. Guarded by _hidLock.
    private readonly Queue<QueuedFrame> _frameQueue = new();
    private readonly Stack<byte[]> _spareFrames = new();
    // Parked interrupt-IN URBs, keyed by endpoint address (issue #56). A
    // composite with more than one interrupt-IN endpoint must never answer
    // one endpoint's read with another endpoint's data: the Steam Deck
    // persona's keyboard and mouse interfaces poll their own endpoints
    // forever while only the controller endpoint has frames, and a single
    // shared queue would have handed a 64-byte controller report to the
    // 8-byte keyboard pipe.
    private readonly Dictionary<byte, Queue<uint>> _pendingInterruptIn = new();
    // The latest report of each input report the descriptor declares, for
    // GET_REPORT(Input), copied in place: a report never sent reads as its
    // ID and zeros. _inputSlot maps a report ID to its slot, -1 for one the
    // descriptor does not declare. Guarded by _hidLock.
    private readonly byte[][] _inputReports;
    private readonly int[] _inputReportLength;
    private readonly int[] _inputSlot = new int[256];
    private readonly bool _inputReportIds;
    private uint _lastSharedSeqNo;

    // The longest report the input section yields: the builder's report,
    // or an extended report.
    private readonly int _reportCapacity;
    // The input pump's report, rebuilt from the section for each frame.
    // Only the pump touches it.
    private readonly byte[] _pumpReport;
    // The data stage of a control reply the device builds for one request,
    // an input report copied from its slot or a layout built now. Only the
    // connection's reader touches it, and it serves one control transfer at
    // a time (FeatureStubTable's answer buffer relies on the same).
    private readonly byte[] _controlReply;
    // The setup packets the diag log has a line for, since this import or
    // the last port reset. Null while HIDMAESTRO_DIAG is off. Only the
    // connection's reader touches it.
    private HashSet<ulong>? _loggedSetups;

    private readonly struct QueuedFrame
    {
        public readonly byte[] Buffer;
        public readonly int Length;
        public QueuedFrame(byte[] buffer, int length) { Buffer = buffer; Length = length; }
    }

    // Device state.
    private volatile byte _configurationValue;

    private const int MaxFrameQueue = 8;

    /// <summary>The interrupt-IN endpoint on the primary HID interface: the
    /// one the input pump feeds. Any other interrupt-IN endpoint belongs to
    /// a secondary HID interface and simply parks its reads.</summary>
    private readonly byte _primaryInEndpoint;

    /// <summary>The parked reads of <see cref="_primaryInEndpoint"/>, the
    /// queue <see cref="_pendingInterruptIn"/> holds for it. Guarded by
    /// <see cref="_hidLock"/>.</summary>
    private readonly Queue<uint> _primaryPending;

    /// <summary>The answers the device gives on request (issue #56, design
    /// note mechanism 4), compiled for this controller: plain entries by
    /// report ID, message entries by the last SET on their report, which the
    /// table keeps itself. Null for a profile with none.</summary>
    private readonly FeatureStubTable? _stubs;

    /// <summary>The feature answers this device serves, compiled at its
    /// create. A check reads the served bytes off them.</summary>
    internal FeatureStubTable? FeatureAnswers => _stubs;

    // Buffer lengths of parked bulk IN reads, by seqnum, for a protocol
    // whose replies go out on a bulk IN endpoint. The seqnums park in
    // _pendingInterruptIn beside the interrupt reads, which is where
    // HandleUnlink looks.
    private readonly System.Collections.Generic.Dictionary<uint, int> _parkedBulkLength = new();
    private int _bulkReadsCanceled;

    /// <summary>Bulk IN reads a host gave up on before a reply came. Under
    /// usbip-win2 such a read is trouble for the host, not for this device:
    /// the driver completes a canceled transfer with its length untouched,
    /// and WinUSB then hands the host stale bytes. A host that only reads
    /// after a command, as SDL does, never adds to this.</summary>
    internal int BulkReadsCanceled => Volatile.Read(ref _bulkReadsCanceled);

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

    /// <summary>The bus id the server exports a transport index under.</summary>
    internal static string BusIdFor(int index) => $"1-{index + 1}";

    public UsbipEmulatedDevice(ControllerProfile profile, int index, DeviceIdentity? identity = null)
    {
        _index = index;
        Descriptors = new UsbDescriptorSet(profile, index, identity);
        BusId = BusIdFor(index);
        // Before any section or thread exists, so a profile whose protocol
        // does not fit its descriptors is refused with nothing to undo. A
        // device that builds its reports binds its layouts here: to the
        // kernels generated from the catalog's JSON, or to the interpreter.
        LayoutRunner? runner = null;
        if (CompiledProtocol.Compile(profile, Descriptors) is { } protocol)
        {
            runner = ProgramBinder.BindLayouts(protocol.Model);
            _engine = new ProtocolEngine(protocol, Descriptors.SerialString, index, runner);
            // A device that builds its reports starts from a pad at rest.
            if (protocol.BuildsReports) _engine.SetStateBlock(profile.StateBlockAtRest());
        }
        _stubs = FeatureStubTable.From(profile, index, Descriptors.SerialString, Descriptors.SerialVariant);
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
        // The input pump's queue, found once rather than per frame.
        _primaryPending = PendingFor(_primaryInEndpoint);
        Devid = (1u << 16) | (uint)(index + 1);

        _builder = profile.GetOrBuildReportBuilder();
        int expectedSize = _builder.InputReportByteSize > 0 ? _builder.InputReportByteSize : 17;
        _reportCapacity = Math.Max(expectedSize, SharedMemoryIO.EXTENDED_DATA_CAPACITY);
        _pumpReport = new byte[_reportCapacity];
        // One slot per declared input report, and one for the builder's
        // report when the descriptor declares none the parser read.
        var declared = _builder.InputReports.Count > 0
            ? _builder.InputReports
            : new[] { (Id: _builder.InputReportId, ByteSize: _builder.InputReportByteSize) };
        _inputReportIds = declared[0].Id != 0;
        Array.Fill(_inputSlot, -1);
        _inputReports = new byte[declared.Count][];
        _inputReportLength = new int[declared.Count];
        for (int r = 0; r < declared.Count; r++)
        {
            _inputReports[r] = new byte[_reportCapacity];
            _inputReports[r][0] = declared[r].Id;
            _inputReportLength[r] = Math.Clamp(declared[r].ByteSize, 1, _reportCapacity);
            _inputSlot[declared[r].Id] = r;
        }

        try
        {
            // The device clock owns a timer and an event, which the catch
            // releases if anything after this throws.
            if (runner != null) CreateLayoutClock(runner);
            _controlReply = new byte[Math.Max(Math.Max(_reportCapacity, _layoutReport.Length),
                                              _engine?.ReportCapacity ?? 0)];

            // Sections and events first, so both this device and the
            // HMController constructed after it see the same objects.
            _inputView = SharedMemoryIO.EnsureInputMapping(index);
            _outputView = SharedMemoryIO.EnsureOutputMapping(index);
            SharedMemoryIO.MarkOutputStart(index);
            _outputEvent = SharedMemoryIO.EnsureOutputEvent(index);
            _inputWaitEvent = SharedMemoryIO.OpenInputEventForWait(index);

            Audio = new UsbAudioEngine(profile, CompleteIsoOnWire);

            var input = new Thread(InputPumpLoop)
            {
                IsBackground = true,
                Name = $"HMUsbipInput_{index}",
                Priority = ThreadPriority.AboveNormal,
            };
            input.Start();
            _inputThread = input;
            AfterInputPumpStart?.Invoke(index);

            // A device that builds its reports sends them on its own clock.
            _layoutClock?.Start();

            // A device that sends the SDK's report on a declared protocol
            // answers its IN endpoint on the host's poll clock
            // (UsbipEmulatedDevice.Protocol.cs).
            if (_engine is { Protocol.BuildsReports: false, Protocol.InputEndpoint: > 0 })
                StartProtocolClock();
        }
        catch
        {
            // Nothing outside holds this device yet, so nobody disposes
            // it. Its threads stop here, before the caller unmaps the
            // sections the input pump reads.
            StopAndRelease(Timeout.Infinite);
            throw;
        }
    }

    /// <summary>Claim this device for one connection. A second import
    /// while attached is refused ST_DEV_BUSY by the server.</summary>
    public bool TryClaimConnection(UsbipServer.Connection connection)
    {
        // A device disposed after the server found it is gone.
        if (_stop) return false;
        if (Interlocked.CompareExchange(ref _connection, connection, null) != null)
            return false;
        if (_stop)
        {
            Interlocked.CompareExchange(ref _connection, null, connection);
            return false;
        }
        // Attach is power-on for a declared protocol's state.
        ResetLayoutState();
        ResetProtocolState();
        // Set before the import's reply goes out, on the reader that then
        // serves the import's requests.
        _loggedSetups = DeviceOrchestrator.DiagEnabled ? new HashSet<ulong>() : null;
        return true;
    }

    /// <summary>The connection's reader thread, as it ends. Dispose can be
    /// running on another thread: a controller's teardown plugs the device
    /// out first, which closes the socket (UsbipBackendHandle.Dispose).
    /// Everything here is either managed state or a write Dispose has
    /// closed off.</summary>
    public void DetachConnection(UsbipServer.Connection connection)
    {
        if (_stop) return; // Dispose released the device already
        // The import is gone with its connection. Cleared and released
        // under one lock, so neither the next import's port nor this one's
        // is ever overwritten by the other.
        lock (_portLock)
        {
            if (Volatile.Read(ref _connection) != connection)
                return; // a different connection owns the device
            SetVhciPortLocked(0);
            Interlocked.Exchange(ref _connection, null);
        }
        Audio.Clear();
        lock (_hidLock)
        {
            foreach (var q in _pendingInterruptIn.Values) q.Clear();
            ClearFrameQueue();
        }
        ResetLayoutState();
        ResetProtocolState();
    }

    // ── Input pump: shared section → interrupt IN ────────────────────────

    private void InputPumpLoop()
    {
        try
        {
            InputPump();
        }
        finally
        {
            // Under the lock, so StopAndRelease never sets a closed handle.
            lock (_lifetimeLock) _inputWakeClosed = true;
            if (_inputWaitEvent != IntPtr.Zero) CloseHandle(_inputWaitEvent);
        }
    }

    private void InputPump()
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
                            // Build the line under the lock, which the counters
                            // need, then release it before the write. The write
                            // impersonates the user and touches the disk
                            // (UserLog.Append), neither of which belongs under
                            // the lock the send path takes.
                            string line;
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
                                line = sb.ToString();
                            }
                            // Written as the user: the path comes from the
                            // environment, and the SDK runs elevated. Every
                            // device of every program appends its line whole
                            // at the log's end.
                            UserLog.Append(DiagPath, line + Environment.NewLine);
                        }
                        catch { }
                    }
                }
                int length;
                lock (_lifetimeLock)
                {
                    if (_stop) break;
                    if (!TryReadInputFrame(_pumpReport, out length)) continue;
                }
                var report = new ReadOnlySpan<byte>(_pumpReport, 0, length);

                // A device that builds its reports sends nothing from here.
                // The frame is the state block its own clock builds the next
                // report from.
                if (_engine is { Protocol.BuildsReports: true })
                {
                    _engine.SetStateBlock(report);
                    SubmitLayoutReport();
                    continue;
                }

                // A declared protocol that sends the SDK's report sends
                // nothing from here either. The frame is the report its next
                // host poll gets.
                if (_engine is { Protocol.InputEndpoint: > 0 })
                {
                    _engine.SetReport(report, System.Diagnostics.Stopwatch.GetTimestamp(),
                                      Volatile.Read(ref _protocolClockOrigin));
                    continue;
                }

                uint seq;
                lock (_hidLock)
                {
                    int slot = _inputSlot[_inputReportIds && length > 0 ? report[0] : 0];
                    if (slot >= 0)
                    {
                        report.CopyTo(_inputReports[slot]);
                        _inputReportLength[slot] = length;
                    }
                    var pending = _primaryPending;
                    if (pending.Count > 0)
                    {
                        seq = pending.Dequeue();
                        if (DiagPath != null)
                            _completionsPerEp[_primaryInEndpoint] =
                                _completionsPerEp.TryGetValue(_primaryInEndpoint, out var cc) ? cc + 1 : 1;
                    }
                    else
                    {
                        // A full queue drops its oldest report and takes
                        // over that report's buffer.
                        byte[] buffer = _frameQueue.Count >= MaxFrameQueue ? _frameQueue.Dequeue().Buffer
                            : _spareFrames.Count > 0 ? _spareFrames.Pop()
                            : new byte[_reportCapacity];
                        report.CopyTo(buffer);
                        _frameQueue.Enqueue(new QueuedFrame(buffer, length));
                        continue;
                    }
                }
                SendRetSubmitSpan(seq, 0, length, report);
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
    /// assembly in its worker (driver.c:1240-1296). The report goes into
    /// <paramref name="report"/>, <see cref="_reportCapacity"/> bytes long.</summary>
    private unsafe bool TryReadInputFrame(Span<byte> report, out int length)
    {
        length = 0;
        IntPtr view = _inputView;
        if (view == IntPtr.Zero) return false;

        // Between the two reads of the sequence number, only the bytes the
        // report takes are copied: the two sizes, then the extended report
        // or the data. A size read while the writer was mid-frame is caught
        // by the sequence check like any other byte, and each length is cut
        // to its region before the copy.
        int expectedSize = _builder.InputReportByteSize > 0 ? _builder.InputReportByteSize : 17;
        bool hasReportId = _builder.InputReportId != 0;
        int maxData = hasReportId ? Math.Max(expectedSize - 1, 16) : expectedSize;
        if (hasReportId && expectedSize > 1) maxData = expectedSize - 1;
        uint seq1, seq2;
        int retries = 4;
        do
        {
            seq1 = (uint)Marshal.ReadInt32(view, 0);
            Thread.MemoryBarrier();
            int extSize = Unsafe.ReadUnaligned<int>((byte*)view + SharedMemoryIO.EXTENDED_SIZE_OFFSET);
            if (extSize > 0 && extSize <= SharedMemoryIO.EXTENDED_DATA_CAPACITY)
            {
                new ReadOnlySpan<byte>((byte*)view + SharedMemoryIO.EXTENDED_DATA_OFFSET, extSize).CopyTo(report);
                length = extSize;
            }
            else
            {
                int dataLen = Unsafe.ReadUnaligned<int>((byte*)view + 4);
                if (dataLen > maxData) dataLen = maxData;
                if (dataLen > SharedMemoryIO.DATA_CAPACITY) dataLen = SharedMemoryIO.DATA_CAPACITY;
                if (dataLen < 0) dataLen = 0;

                // The report is expectedSize bytes, zero past the data.
                var r = report[..expectedSize];
                r.Clear();
                var data = new ReadOnlySpan<byte>((byte*)view + SharedMemoryIO.DATA_OFFSET, dataLen);
                if (hasReportId)
                {
                    r[0] = _builder.InputReportId;
                    data[..Math.Min(dataLen, expectedSize - 1)].CopyTo(r[1..]);
                }
                else
                {
                    data.CopyTo(r);
                }
                length = expectedSize;
            }
            Thread.MemoryBarrier();
            seq2 = (uint)Marshal.ReadInt32(view, 0);
        } while ((seq1 != seq2 || (seq1 & 1) != 0) && --retries > 0);
        if (seq1 != seq2 || (seq1 & 1) != 0) { length = 0; return false; }
        if (seq1 == _lastSharedSeqNo) { length = 0; return false; } // no new frame
        _lastSharedSeqNo = seq1;
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
            if (_outputClosed) return;
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
            // Under the lock too: the controller closes the event with the
            // section.
            if (_outputEvent != IntPtr.Zero) SetEvent(_outputEvent);
        }
    }

    // ── URB dispatch (called on the connection reader thread) ────────────

    /// <summary>A CMD_SUBMIT. One the connection's reader passed in while
    /// Dispose ran goes unanswered, and the reader ends the connection at
    /// the next one (<see cref="IsDisposed"/>).</summary>
    public void HandleSubmit(in UsbipProtocol.CommandHeader cmd, ReadOnlySpan<byte> outPayload,
                             ReadOnlySpan<(uint Offset, uint Length)> isoPackets)
    {
        if (_stop) return;
        if (cmd.Ep == 0)
        {
            HandleControl(cmd, outPayload);
            return;
        }

        // A halted protocol answers nothing until a port reset.
        if (_engine is { Protocol.HasHalt: true } halting && halting.IsHalted)
        {
            SendError(cmd.Seqnum, halting.Protocol.HaltStatus);
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
            // The engine's own URB, since the packets and data arrived in the
            // reader's reused buffers and the URB waits for its due tick.
            var urb = Audio.RentUrb(isoPackets.Length, cmd.IsIn ? 0 : outPayload.Length);
            urb.Seqnum = cmd.Seqnum;
            urb.IsIn = cmd.IsIn;
            urb.TransferBufferLength = cmd.TransferBufferLength;
            urb.InterfaceNumber = ep.InterfaceNumber;
            urb.AltSetting = ep.AltSetting;
            isoPackets.CopyTo(urb.Packets);
            if (!cmd.IsIn) outPayload.CopyTo(urb.OutPayload);
            Audio.SubmitIso(urb);
            return;
        }

        if (ep.TransferType == 3) // interrupt
        {
            if (cmd.IsIn)
            {
                if (_engine is { Protocol.BuildsReports: true } && epAddr == _engine.Protocol.InputEndpoint)
                {
                    SubmitLayoutRead(cmd.Seqnum);
                    return;
                }
                if (_engine != null && epAddr == _engine.Protocol.InputEndpoint)
                {
                    ProtocolSubmitRead(cmd.Seqnum, cmd.TransferBufferLength);
                    return;
                }

                QueuedFrame frame = default;
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
                if (frame.Buffer != null)
                {
                    // The buffer is this thread's until it goes back.
                    SendRetSubmitSpan(cmd.Seqnum, 0, frame.Length, frame.Buffer.AsSpan(0, frame.Length));
                    lock (_hidLock) _spareFrames.Push(frame.Buffer);
                }
                return;
            }

            // A declared protocol reads its command endpoint as packets of
            // commands.
            if (_engine != null && _engine.Protocol.Source == CommandSource.InterruptOut && epAddr == _engine.Protocol.CommandEndpoint)
            {
                ProtocolSubmitOut(cmd.Seqnum, outPayload);
                return;
            }

            // Interrupt OUT: a full output report, Report ID first when the
            // descriptor declares IDs. Same split as driver.c WRITE_REPORT.
            var payload = outPayload;
            byte rid = payload.Length > 0 ? payload[0] : (byte)0;
            PublishOutput(SourceHidOutput, rid,
                payload.Length > 0 ? payload[1..] : ReadOnlySpan<byte>.Empty);
            SendRetSubmit(cmd.Seqnum, 0, payload.Length, null);
            return;
        }

        if (ep.TransferType == 2 && _engine != null) // bulk: a protocol's command channel
        {
            var p = _engine.Protocol;
            if (cmd.IsIn && p.Channel == ReplyChannel.BulkIn && epAddr == p.ReplyEndpoint)
            {
                SubmitBulkRead(cmd.Seqnum, cmd.TransferBufferLength);
                return;
            }
            if (!cmd.IsIn && p.Source == CommandSource.BulkOut && epAddr == p.CommandEndpoint)
            {
                HandleBulkCommand(cmd.Seqnum, outPayload);
                return;
            }
        }

        SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
    }

    public void HandleUnlink(uint seqnum, uint victimSeqnum)
    {
        if (_stop) return;
        bool removed = Audio.TryUnlink(victimSeqnum);
        // Reads a host-polled protocol holds park in its own list. Every
        // other read, a device that builds its reports included, parks by
        // endpoint below.
        if (!removed && _engine is { Protocol.BuildsReports: false, Protocol.InputEndpoint: > 0 })
            removed = TryUnlinkProtocolRead(victimSeqnum);
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

    private void HandleControl(in UsbipProtocol.CommandHeader cmd, ReadOnlySpan<byte> outPayload)
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
        // Each setup packet once per import and port reset: the requests a
        // host sets the device up with, and every distinct one after. A
        // request a host repeats while the device runs, a feature report
        // read again, costs one line rather than a line and a disk write
        // per transfer on the thread that answers it.
        if (_loggedSetups != null && _loggedSetups.Add(cmd.Setup))
            DeviceOrchestrator.LogDiag($"usbip control {BusId}: {Convert.ToHexString(setup)} seq {cmd.Seqnum} buffer {cmd.TransferBufferLength}");

        // Hub port reset arrives as USB_RT_PORT SET_FEATURE(PORT_RESET)
        // (usbip-win2 device_ioctl.cpp make_reset_port: "meaningless for a
        // server which ignores it"). Reset to the unconfigured state.
        if (bmRequestType == 0x23 && bRequest == 0x03)
        {
            ResetDeviceState();
            _loggedSetups?.Clear();
            SendRetSubmit(cmd.Seqnum, 0, 0, null);
            return;
        }

        if (_engine != null && ProtocolControl(cmd, bmRequestType, bRequest, wLength))
            return;

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
                    SendRetSubmitSpan(cmd.Seqnum, 0, n, d.AsSpan(0, n));
                    return;
                }
                case 0x00: // GET_STATUS
                {
                    if (!deviceToHost) break;
                    // Device: self-powered bit from bmAttributes 0xC0. Interface/endpoint: zero.
                    ushort status = recipient == 0 ? (ushort)0x0001 : (ushort)0x0000;
                    Span<byte> d = stackalloc byte[2];
                    d[0] = (byte)(status & 0xFF);
                    d[1] = (byte)(status >> 8);
                    int n = Math.Min(d.Length, wLength);
                    SendRetSubmitSpan(cmd.Seqnum, 0, n, d[..n]);
                    return;
                }
                case 0x09: // SET_CONFIGURATION
                    _configurationValue = (byte)(wValue & 0xFF);
                    ResetAltSettings();
                    SendRetSubmit(cmd.Seqnum, 0, 0, null);
                    return;
                case 0x08: // GET_CONFIGURATION
                {
                    Span<byte> d = stackalloc byte[1];
                    d[0] = _configurationValue;
                    int n = Math.Min(1, (int)wLength);
                    SendRetSubmitSpan(cmd.Seqnum, 0, n, d[..n]);
                    return;
                }
                case 0x0B: // SET_INTERFACE
                    Audio.SetAltSetting((byte)(wIndex & 0xFF), (byte)(wValue & 0xFF));
                    SendRetSubmit(cmd.Seqnum, 0, 0, null);
                    return;
                case 0x0A: // GET_INTERFACE
                {
                    Span<byte> d = stackalloc byte[1];
                    d[0] = Audio.GetAltSetting((byte)(wIndex & 0xFF));
                    int n = Math.Min(1, (int)wLength);
                    SendRetSubmitSpan(cmd.Seqnum, 0, n, d[..n]);
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
                Span<byte> d = stackalloc byte[2];
                int size = Audio.HandleUacGet(bRequest, unitId, selector, channel, wLength, d);
                if (size < 0) { SendError(cmd.Seqnum, -UsbipProtocol.EPipe); return; }
                int n = Math.Min(size, wLength);
                SendRetSubmitSpan(cmd.Seqnum, 0, n, d[..n]);
            }
            else
            {
                bool ok = bRequest == 0x01 // SET_CUR
                    && Audio.HandleUacSet(unitId, selector, channel, outPayload);
                if (ok) SendRetSubmit(cmd.Seqnum, 0, outPayload.Length, null);
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
                    ReadOnlySpan<byte> freq = [0x80, 0xBB, 0x00]; // 48000, 3-byte UAC1 rate
                    int n = Math.Min(freq.Length, wLength);
                    SendRetSubmitSpan(cmd.Seqnum, 0, n, freq[..n]);
                }
                else
                {
                    SendRetSubmit(cmd.Seqnum, 0, outPayload.Length, null);
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
                SendRetSubmitSpan(cmd.Seqnum, 0, n, feature.AsSpan(0, n));
                return;
            }
        }

        SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
    }

    private void HandleHidClassRequest(in UsbipProtocol.CommandHeader cmd, byte bRequest,
        ushort wValue, ushort wLength, bool deviceToHost, ReadOnlySpan<byte> outPayload,
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
                    SendIdleRate(cmd.Seqnum, wLength);
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
                if (_engine != null && ProtocolGetReport(cmd, reportType, reportId, wLength))
                    return;
                if (reportType == 0x01)
                {
                    int length;
                    // A report ID the descriptor does not declare is a
                    // value the request cannot take, which USB 2.0 9.2.7
                    // answers with a STALL. A firmware stack stalls the
                    // same way when its class driver cannot answer
                    // (tinyusb usbd.c, the failed-request path, and
                    // hid_device.c's TU_ASSERT(xferlen > 0) on an empty
                    // report). No host in the references asks for an
                    // undeclared ID. A protocol whose getReport is
                    // deviceReport also answers report ID 0 with its
                    // selected layout, as VIIPER's ns2pro does
                    // (device.go:171-176).
                    bool deviceReport = _engine is { Protocol.GetReport: GetReportMode.DeviceReport };
                    bool declaredId = deviceReport && reportId == 0
                        || (reportId != 0) == _inputReportIds && _inputSlot[reportId] >= 0;
                    if (!declaredId)
                    {
                        SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
                        return;
                    }
                    if (deviceReport)
                    {
                        // The layout asked for, built now and not counted.
                        length = _engine!.Peek(reportId, _controlReply);
                    }
                    else
                    {
                        // Copied under the lock the input pump writes the
                        // slot under, and sent after it.
                        int slot = _inputSlot[reportId];
                        lock (_hidLock)
                        {
                            length = _inputReportLength[slot];
                            _inputReports[slot].AsSpan(0, length).CopyTo(_controlReply);
                        }
                    }
                    int n = Math.Min(length, wLength);
                    SendRetSubmitSpan(cmd.Seqnum, 0, n, _controlReply.AsSpan(0, n));
                    return;
                }
                if (reportType == 0x03 && _engine != null)
                {
                    // A live feature report: its program over the state
                    // block as it stands, built for this read.
                    Span<byte> live = stackalloc byte[Internal.Programs.ProgramForm.MaxReport];
                    int size = _engine.BuildLiveFeature(reportId, live);
                    if (size >= 0)
                    {
                        PublishOutput(SourceHidFeatureRead, reportId, ReadOnlySpan<byte>.Empty);
                        int n = Math.Min(size, wLength);
                        SendRetSubmitSpan(cmd.Seqnum, 0, n, live[..n]);
                        return;
                    }
                }
                if (reportType == 0x03)
                {
                    // An answer the table holds, cut to what the host asked
                    // for. No entry, or a request shorter than the entry
                    // serves, stalls, as a real device does.
                    bool notify = false;
                    int n = _stubs?.Lookup(reportId, wLength, out notify) ?? -1;
                    if (n < 0) { SendError(cmd.Seqnum, -UsbipProtocol.EPipe); return; }
                    if (notify) PublishOutput(SourceHidFeatureRead, reportId, ReadOnlySpan<byte>.Empty);
                    SendRetSubmitSpan(cmd.Seqnum, 0, n, _stubs!.Answer(n));
                    return;
                }
                break;
            }
            case 0x09 when !deviceToHost && reportType == 0x02
                           && _engine is { Protocol.CommandsFromSetReport: true, Protocol.Source: not CommandSource.SetReport }: // SET_REPORT(Output)
            {
                // The protocol reads the data stage as one command, report
                // ID first.
                var payload = outPayload;
                ProtocolHandleReport(payload, reportId);
                SendRetSubmit(cmd.Seqnum, 0, payload.Length, null);
                return;
            }
            case 0x0B when !deviceToHost && _engine is { Protocol.AckSetProtocol: true }: // SET_PROTOCOL
                SendRetSubmit(cmd.Seqnum, 0, 0, null);
                return;
            case 0x09 when !deviceToHost: // SET_REPORT
            {
                var payload = outPayload;
                // A protocol whose commands come from SET_REPORT alone reads
                // the data stage as one command too, report ID first, and the
                // device answers and publishes it as before: the answer table
                // keeps a feature report, and the ring gets both kinds.
                ProtocolEngine.CommandResult? command = _engine is { Protocol.Source: CommandSource.SetReport }
                    ? _engine.HandleCommand(payload, System.Diagnostics.Stopwatch.GetTimestamp())
                    : null;
                byte rid = payload.Length > 0 ? payload[0] : reportId;
                var data = payload.Length > 0 ? payload[1..] : ReadOnlySpan<byte>.Empty;
                // The data a message entry follows, as the SET carried it:
                // from the report ID for a descriptor that declares report
                // IDs, from the first data byte for one that declares none.
                if (reportType == 0x03) _stubs?.OnSetFeature(reportId, payload);
                // After the answer table has read the write, the command goes
                // to the ring as every protocol's commands do: as the host
                // sent it, as the report its rule emits, or not at all when
                // the rule or the protocol consumes it. It keeps its kind.
                byte source = reportType == 0x03 ? SourceHidFeature : SourceHidOutput;
                if (command is { } c && payload.Length > 0) PublishCommand(c, payload, source);
                else PublishOutput(source, rid, data);
                // What the command did, as on the OUT endpoint and the
                // SET_REPORT(Output) path: a halt fails the parked reads, and
                // a change wakes the clocks.
                if (command is { } applied) ProtocolApply(applied.Effect);
                SendRetSubmit(cmd.Seqnum, 0, payload.Length, null);
                return;
            }
            case 0x0A when !deviceToHost: // SET_IDLE
                SendRetSubmit(cmd.Seqnum, 0, 0, null);
                return;
            case 0x02 when deviceToHost: // GET_IDLE
                SendIdleRate(cmd.Seqnum, wLength);
                return;
        }
        // GET/SET_PROTOCOL and anything else: the pad is not a boot device;
        // a real one stalls these.
        SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
    }

    /// <summary>GET_IDLE: an idle rate of 0, cut to the request's
    /// length.</summary>
    private void SendIdleRate(uint seqnum, ushort wLength)
    {
        Span<byte> rate = stackalloc byte[1];
        rate[0] = 0;
        int n = Math.Min(1, (int)wLength);
        SendRetSubmitSpan(seqnum, 0, n, rate[..n]);
    }

    private void ResetDeviceState()
    {
        _configurationValue = 0;
        ResetAltSettings();
        Audio.Clear();
        lock (_hidLock)
        {
            foreach (var q in _pendingInterruptIn.Values) q.Clear();
            ClearFrameQueue();
        }
        ResetLayoutState();
        ResetProtocolState(portReset: true);
    }

    /// <summary>Drop the queued reports and keep their buffers. Caller
    /// holds <see cref="_hidLock"/>.</summary>
    private void ClearFrameQueue()
    {
        while (_frameQueue.Count > 0) _spareFrames.Push(_frameQueue.Dequeue().Buffer);
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

    private void SendError(uint seqnum, int status)
        => SendRetSubmit(seqnum, status, 0, null);

    private void SendRetSubmit(uint seqnum, int status, int actualLength, byte[]? inPayload)
    {
        var c = _connection;
        if (c == null) return;
        try { c.SendRetSubmitNonIso(seqnum, status, actualLength, inPayload); } catch { }
    }

    /// <summary>The same reply from a span, for a caller that answers from
    /// a pooled or stack buffer.</summary>
    private void SendRetSubmitSpan(uint seqnum, int status, int actualLength, ReadOnlySpan<byte> inPayload)
    {
        var c = _connection;
        if (c == null) return;
        try { c.SendRetSubmitNonIso(seqnum, status, actualLength, inPayload); } catch { }
    }

    /// <summary>Completion callback from the audio engine's pacing thread.
    /// Builds the isochronous RET_SUBMIT per the 0.9.7.5 receive rules.</summary>
    private void CompleteIsoOnWire(UsbAudioEngine.PendingIso p, ReadOnlySpan<byte> inCompacted, int perPacketActual)
    {
        var c = _connection;
        if (c == null) return;
        try { c.SendRetSubmitIso(p, inCompacted, perPacketActual); } catch { }
    }

    public void Dispose() => StopAndRelease(600);

    /// <summary>Stop every thread this device started and close what it
    /// opened. Dispose ends here, and so does a constructor that throws
    /// partway, so every field may still be unset. Each loop checks
    /// <c>_stop</c> after a wait of at most 500 ms, and each wait is woken
    /// here, so Dispose waits for none of them to time out.
    ///
    /// <para>The controller unmaps the sections once Dispose returns, so
    /// nothing here waits on the connection, and no thread reads or writes
    /// a section afterward. A thread that started closes the handles it
    /// waits on as it ends. What a constructor opened for a thread it never
    /// started is closed here. A sweep disposes the device and the
    /// controller's own teardown disposes it again: the second call
    /// returns.</para></summary>
    private void StopAndRelease(int joinMs)
    {
        lock (_lifetimeLock)
        {
            if (_stop) return;
            _stop = true;
            _connection = null;
            // Inside the lifetime lock, so a second Dispose racing this one
            // returns only once nothing can write the output section.
            lock (_publishLock) _outputClosed = true;
            // The input pump waits up to 500 ms for a frame and sees _stop
            // only when it wakes. Wake it now, as the clocks are woken
            // below, so Dispose does not wait out its timeout. It ends while
            // the audio engine stops.
            if (!_inputWakeClosed && _inputWaitEvent != IntPtr.Zero) SetEvent(_inputWaitEvent);
        }
        Audio?.Dispose();
        if (_inputThread is { } input)
            try { input.Join(joinMs); } catch { }
        else if (_inputWaitEvent != IntPtr.Zero)
        {
            lock (_lifetimeLock) _inputWakeClosed = true;
            CloseHandle(_inputWaitEvent);
        }
        _layoutClock?.Stop(joinMs);
        DisposeProtocol(joinMs);
    }

    /// <summary>Called on the constructor's thread right after the input
    /// pump starts. Coexistence probe section U sets it through reflection
    /// to fail a constructor at that point: no real failure there can be
    /// caused safely, since thread and event creation fail only when the
    /// process runs out of resources. Null outside the probe.</summary>
    internal static Action<int>? AfterInputPumpStart;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr handle, uint ms);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(IntPtr h);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr h);
}
