using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace HIDMaestro.Internal.Usbip;

/// <summary>The isochronous engine for one emulated composite device
/// (issue #39): paces both audio directions at the endpoint's 1 ms service
/// interval, delivers speaker/haptic PCM to the consumer, and sources
/// microphone PCM from the consumer's feed.
///
/// <para>Pacing model, from the pacing spike this design gated on: a
/// dedicated highest-priority thread parks on a high-resolution waitable
/// timer armed at the next URB's absolute due time, with a short spin to
/// land the completion. A stream cursor advances one service interval per
/// isochronous packet, so an URB carrying N packets completes N ms after
/// the previous one, which makes this device the audio clock exactly the
/// way a real adaptive-sink pad is. The host's own multi-URB lead is the
/// jitter buffer; submit-side outliers are absorbed because due times
/// come from the cursor, not from arrival time. When nothing is pending
/// the thread parks on an event and costs nothing, which is what keeps
/// the idle-cost story clean.</para>
///
/// <para>Wire rules for the completions come from usbip-win2's
/// wsk_receive.cpp, read at 0.9.7.5, and 0.9.8.1's receive path applies
/// the same checks: OUT isochronous replies carry descriptors only; IN
/// replies carry compacted data (no inter-packet padding) followed by
/// descriptors whose offsets must echo the submit's offsets, with
/// actual_length equal to the sum of per-packet actual lengths.</para></summary>
internal sealed partial class UsbAudioEngine : IDisposable
{
    /// <summary>A pending isochronous URB, parked until its due tick. The
    /// engine keeps spent ones and hands them out again
    /// (<see cref="RentUrb"/>), so their arrays are at least as long as the
    /// counts say.</summary>
    internal sealed class PendingIso
    {
        public uint Seqnum;
        public bool IsIn;
        public int TransferBufferLength;
        public (uint Offset, uint Length)[] Packets = Array.Empty<(uint, uint)>();
        public int PacketCount;
        public byte[] OutPayload = Array.Empty<byte>();     // OUT only: the PCM the host sent
        public int OutLength;
        public long DueTick;            // Stopwatch timestamp when the last packet's window closes
        public int FrameAtCompletion;   // virtual frame counter value to report
        public byte InterfaceNumber;    // the streaming interface the endpoint belongs to
        public byte AltSetting;         // the alternate setting that declares the endpoint
        public UsbipServer.Connection? Import; // the connection the URB came on, which its answer goes to
        public bool Unlinked;           // an unlink came while the pump completed it
        public uint UnlinkSeqnum;       // that unlink's seqnum
    }

    private readonly object _lock = new();
    private readonly List<PendingIso> _pending = new();
    // URBs the pump has taken from _pending and not yet answered, which an
    // unlink finds here (TryUnlink). Guarded by _lock.
    private readonly List<PendingIso> _completing = new(8);
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly IntPtr _timer;
    private readonly IntPtr[] _waitPair = new IntPtr[2];
    private volatile bool _stop;

    /// <summary>Thread starts to fail before the next real one.
    /// usbip_server_check sets it through reflection: no real failure of
    /// Thread.Start can be caused safely. Zero outside the probe.</summary>
    internal static int FailThreadStarts;

    private static void StartThread(Thread thread)
    {
        if (Volatile.Read(ref FailThreadStarts) > 0 && Interlocked.Decrement(ref FailThreadStarts) >= 0)
            throw new OutOfMemoryException("usbip_server_check failed this thread start.");
        thread.Start();
    }

    /// <summary>Puts a completed URB on the wire: IN, the compacted data
    /// and each packet's actual length. Called on the pump thread. The data
    /// is the pump's own buffer, valid for the call.</summary>
    internal delegate void IsoCompletion(PendingIso urb, ReadOnlySpan<byte> inData, int perPacketActual);

    /// <summary>Answers, with status 0, an unlink that came while the pump
    /// completed a URB, once the URB's RET_SUBMIT is on the wire: on
    /// <paramref name="import"/>, the connection the URB came on. Called on
    /// the pump thread.</summary>
    internal delegate void IsoUnlinkAnswer(UsbipServer.Connection? import, uint unlinkSeqnum);

    private readonly IsoCompletion _complete;
    private readonly IsoUnlinkAnswer? _unlinkAnswer;

    // Spent URBs, handed out again by RentUrb, and the pump's microphone
    // buffer, so a transfer allocates nothing once they have grown to the
    // host's sizes. Spare URBs are guarded by _lock. The buffer is the
    // pump's alone.
    private readonly Stack<PendingIso> _spareUrbs = new();
    private const int MaxSpareUrbs = 64;
    private byte[] _micOut = Array.Empty<byte>();

    // Stream cursors, one per direction, in Stopwatch ticks. Zero means
    // "no stream in flight; next URB re-anchors to now".
    private long _outCursor;
    private long _inCursor;

    // The earliest due tick the pump's last pass found, which it waits for
    // next, or long.MaxValue while it parks with nothing pending. Written by
    // the pump at each pass and read by SubmitIso, both under _lock.
    private long _armedDue = long.MaxValue;
    private static readonly long TicksPerMs = Stopwatch.Frequency / 1000;
    private static readonly double TicksPerUs = Stopwatch.Frequency / 1_000_000.0;

    // Virtual USB frame counter: milliseconds since engine start. Reported
    // in ret_submit.start_frame, mirroring a real HC's running frame index.
    private readonly long _epoch = Stopwatch.GetTimestamp();

    // Microphone source ring. The consumer feeds PCM via SubmitMicSamples;
    // the pump drains one service interval per packet. Underrun reads
    // silence, which is what a muted real microphone delivers.
    //
    // The ring is a byte FIFO carrying one continuous interleaved stream,
    // so consecutive submits concatenate and a frame may span them. What
    // it cannot survive is a *partial* frame going missing (issue #41):
    // drop three bytes of a four-byte frame and every later sample
    // reaches the host shifted one byte, its low byte arriving as the
    // high byte, which is full-scale noise. Nothing downstream re-aligns
    // it, so the corruption is permanent for the life of the device.
    // Bytes are therefore only ever dropped on a frame boundary, and only
    // whole frames are handed to the host.
    private readonly byte[] _micRing;
    private int _micHead, _micTail; // byte indices; lock-protected
    private readonly int _micBytesPerInterval;
    private readonly int _micFrameBytes;
    private long _micDroppedBytes;  // running total, lock-protected
    private bool _micDropLogged;

    /// <summary>Raised on the pacing thread when a paced OUT URB completes,
    /// with the interleaved PCM the host sent for that window. The memory
    /// is only valid for the duration of the callback.</summary>
    public event Action<ReadOnlyMemory<byte>>? OutFrames;

    /// <summary>Raised when the host selects an alternate setting on a
    /// streaming interface: (interfaceNumber, altSetting). Raised on the
    /// engine's event thread, in order with <see cref="ControlChanged"/>.</summary>
    public event Action<byte, byte>? AltSettingChanged;

    /// <summary>Raised when the host writes a UAC control:
    /// (unitId, selector, channel, rawValue). Selector 1 is Mute,
    /// 2 is Volume, per UAC1 Feature Unit control selectors. Raised on the
    /// engine's event thread, in order with
    /// <see cref="AltSettingChanged"/>.</summary>
    public event Action<byte, byte, byte, short>? ControlChanged;

    // UAC1 feature-unit control state, keyed by (unitId, selector, channel).
    private readonly Dictionary<(byte Unit, byte Selector, byte Channel), short> _controlCur = new();
    private readonly Dictionary<(byte Unit, byte Selector, byte Channel), (short Min, short Max, short Res)> _controlRange = new();
    private readonly HashSet<byte> _knownUnits = new();

    // Alternate-setting state per interface number.
    private readonly Dictionary<byte, byte> _altSetting = new();

    // Stream state inferred from isochronous traffic, for a transport that
    // never forwards SET_INTERFACE. usbip-win2's filter driver tells its
    // host controller driver about each SELECT_INTERFACE with a marker
    // request, and the 0.9.8.1 host controller driver recognizes it only
    // with bmRequestType 0x80. Every earlier filter sends 0x00: the 0.9.7.x
    // and 0.9.8.0 headers initialize the union's bitfield member, which
    // truncates 0x80 to 0, and 0.9.8.1 initializes the byte member. With an
    // older filter still bound beside a 0.9.8.1 host controller driver, the
    // marker reaches this device as an ordinary request and SET_INTERFACE
    // never does, while the audio itself flows. So an interface that has
    // never received a SET_INTERFACE is open while its endpoint carries
    // traffic, and closes StopAfterTicks after the traffic stops. Once the
    // host delivers a SET_INTERFACE for an interface, only SET_INTERFACE
    // decides it.
    private readonly HashSet<byte> _explicitAlt = new();
    private readonly HashSet<byte> _inferredOpen = new();
    private readonly Dictionary<byte, long> _lastIsoTick = new();
    private static readonly long StopAfterTicks = 200 * TicksPerMs;
    private static readonly long InferCheckTicks = 50 * TicksPerMs;
    private long _nextInferCheck;                       // pump thread only
    private readonly List<byte> _inferClosing = new(4); // pump thread only

    // The control and stream events wait here, in the order the changes
    // happened, for the engine's event thread, which raises them. A
    // consumer's handler runs there, so one that blocks stalls no transfer:
    // the connection's reader takes the host's commands and the pump paces
    // the audio as before. Guarded by _lock. The queue holds structs, so an
    // event allocates nothing once it has grown. The thread starts with the
    // first event and closes its own wake event as it ends. Dispose closes
    // the wake event of an engine whose thread never started. A change
    // queued before Dispose is still raised.
    private readonly struct AudioEvent
    {
        public readonly bool IsControl;
        public readonly byte A, B, C;   // a control's unit, selector and channel, or a stream's interface and alternate setting
        public readonly short Value;
        public AudioEvent(bool isControl, byte a, byte b, byte c, short value)
        {
            IsControl = isControl; A = a; B = b; C = c; Value = value;
        }
    }

    // At most one control write per control waits here, the newest: a
    // write to a control that has one waiting replaces it, at the back, so
    // a host that keeps writing while a handler blocks does not grow the
    // queue. Stream changes all wait, in order.
    private readonly List<AudioEvent> _events = new();
    private Thread? _eventThread;
    private readonly AutoResetEvent _eventWake = new(false);
    private bool _eventWakeClosed;

    // The alternate setting last raised per interface, so a setting equal
    // to the last one is not raised again. The event thread's alone.
    private readonly Dictionary<byte, byte> _altRaised = new();

    public UsbAudioEngine(ControllerProfile profile, IsoCompletion complete) : this(profile, complete, null) { }

    public UsbAudioEngine(ControllerProfile profile, IsoCompletion complete, IsoUnlinkAnswer? unlinkAnswer)
    {
        _complete = complete;
        _unlinkAnswer = unlinkAnswer;

        var cfg = profile.UsbConfiguration!;
        int micBytes = 0;
        int micFrame = 0;
        foreach (var iface in cfg.Interfaces)
        {
            if (iface.Function != "audioStreamingIn") continue;
            foreach (var alt in iface.AltSettings)
            {
                var stream = alt.AudioStream;
                if (stream == null) continue;
                // One service interval of capture PCM. Every in-scope rate
                // is a whole number of samples per millisecond (48k, 32k,
                // 16k); a future non-integral rate needs packet-size
                // dithering here, not a bigger divisor.
                micBytes = stream.SampleRateHz / 1000 * stream.Channels * (stream.BitsPerSample / 8);
                micFrame = stream.Channels * (stream.BitsPerSample / 8);
            }
        }
        _micBytesPerInterval = micBytes;                  // DualSense: 48 * 2 * 2 = 192
        _micFrameBytes = Math.Max(1, micFrame);           // DualSense: 2 * 2 = 4
        _micRing = new byte[Math.Max(1, micBytes) * 256]; // ~256 ms of microphone lead

        foreach (var ctl in cfg.AudioControls ?? new List<UsbAudioControlSpec>())
        {
            _knownUnits.Add(ctl.UnitId);
            _controlCur[(ctl.UnitId, 1, 0)] = ctl.MuteCur;
            _controlCur[(ctl.UnitId, 2, 0)] = ctl.VolumeCurRaw;
            _controlRange[(ctl.UnitId, 2, 0)] = (ctl.VolumeMinRaw, ctl.VolumeMaxRaw, ctl.VolumeResRaw);
        }

        try
        {
            _timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero,
                CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (_timer == IntPtr.Zero) // before Windows 10 1803: a coarser timer that still works
                _timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 0, TIMER_ALL_ACCESS);
            _waitPair[0] = _timer;
            _waitPair[1] = _wake.SafeWaitHandle.DangerousGetHandle();

            _thread = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "HMUsbipAudioPump",
                Priority = ThreadPriority.Highest,
            };
            StartThread(_thread);
        }
        catch
        {
            // No pump runs to close the handles it waits on, and the device
            // gets no engine to dispose, so they close here.
            _stop = true;
            _eventWakeClosed = true;
            if (_timer != IntPtr.Zero) CloseHandle(_timer);
            _wake.Dispose();
            _eventWake.Dispose();
            throw;
        }
    }

    // ── Submission (called from the connection reader thread) ────────────

    /// <summary>A URB to fill and submit, with room for
    /// <paramref name="packets"/> packets and <paramref name="outLength"/>
    /// bytes of OUT data.</summary>
    public PendingIso RentUrb(int packets, int outLength)
    {
        PendingIso? urb = null;
        lock (_lock)
            if (_spareUrbs.Count > 0) urb = _spareUrbs.Pop();
        urb ??= new PendingIso();
        if (urb.Packets.Length < packets) urb.Packets = new (uint, uint)[packets];
        if (urb.OutPayload.Length < outLength) urb.OutPayload = new byte[outLength];
        urb.PacketCount = packets;
        urb.OutLength = outLength;
        urb.Import = null;
        urb.Unlinked = false;
        return urb;
    }

    /// <summary>Caller holds <see cref="_lock"/>.</summary>
    private void ReturnUrbLocked(PendingIso urb)
    {
        if (_spareUrbs.Count < MaxSpareUrbs) _spareUrbs.Push(urb);
    }

    /// <summary>Queue an isochronous URB. Due time comes from the per-
    /// direction stream cursor: each packet advances the cursor one
    /// service interval; a cursor in the past re-anchors to now (stream
    /// start or host underrun).</summary>
    public void SubmitIso(PendingIso urb)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            // After Dispose the pump has ended, or is ending and closing its
            // event.
            if (_stop)
            {
                ReturnUrbLocked(urb);
                return;
            }
            ref long cursor = ref urb.IsIn ? ref _inCursor : ref _outCursor;
            if (cursor < now - 50 * TicksPerMs) cursor = now; // (re)anchor
            cursor += urb.PacketCount * TicksPerMs;
            urb.DueTick = cursor;
            _pending.Add(urb);

            byte iface = urb.InterfaceNumber;
            if (urb.AltSetting != 0 && !_explicitAlt.Contains(iface))
            {
                _lastIsoTick[iface] = now;
                if (!_altSetting.TryGetValue(iface, out var cur) || cur == 0)
                {
                    _altSetting[iface] = urb.AltSetting;
                    _inferredOpen.Add(iface);
                    QueueAltLocked(iface, urb.AltSetting);
                }
            }
            // The pump wakes for an URB due before the tick it waits for, or
            // when it is parked. A later one it finds at that tick's pass,
            // which scans the whole list.
            if (urb.DueTick < _armedDue) _wake.Set();
        }
    }

    /// <summary>What a CMD_UNLINK found.</summary>
    internal enum UnlinkResult : byte
    {
        /// <summary>No URB of the import's with that seqnum waits or
        /// completes here.</summary>
        NotFound,
        /// <summary>The URB was waiting for its tick and is gone: the
        /// unlink's answer is -ECONNRESET.</summary>
        Removed,
        /// <summary>The pump has taken the URB to complete. It sends the
        /// URB's RET_SUBMIT, then answers the unlink with status 0.</summary>
        Completing,
    }

    /// <summary>A CMD_UNLINK of <paramref name="seqnum"/> on
    /// <paramref name="import"/>, with seqnum <paramref name="unlinkSeqnum"/>.
    /// A RET_UNLINK with status 0 says the URB's RET_SUBMIT went out before
    /// it (usbip_protocol.rst, USBIP_RET_UNLINK), so the answer to a URB the
    /// pump is completing, a consumer's handler perhaps still running, waits
    /// for the pump: as Linux's stub sends a completed URB's RET_SUBMIT ahead
    /// of every RET_UNLINK (stub_tx.c stub_tx_loop).</summary>
    public UnlinkResult TryUnlink(uint seqnum, uint unlinkSeqnum, UsbipServer.Connection? import)
    {
        lock (_lock)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].Seqnum == seqnum && _pending[i].Import == import)
                {
                    ReturnUrbLocked(_pending[i]);
                    _pending.RemoveAt(i);
                    return UnlinkResult.Removed;
                }
            }
            foreach (var urb in _completing)
            {
                if (urb.Seqnum != seqnum || urb.Import != import || urb.Unlinked) continue;
                urb.Unlinked = true;
                urb.UnlinkSeqnum = unlinkSeqnum;
                return UnlinkResult.Completing;
            }
        }
        return UnlinkResult.NotFound;
    }

    /// <summary>Drop every pending URB (device detach). No replies are
    /// sent; the socket is going away.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var urb in _pending) ReturnUrbLocked(urb);
            _pending.Clear();
        }
    }

    // ── Alternate settings ───────────────────────────────────────────────

    /// <summary>The host's SET_INTERFACE. From the first one on, this
    /// interface's stream state follows SET_INTERFACE alone.</summary>
    public void SetAltSetting(byte interfaceNumber, byte altSetting)
    {
        lock (_lock) _explicitAlt.Add(interfaceNumber);
        ApplyAltSetting(interfaceNumber, altSetting);
    }

    /// <summary>A reset (SET_CONFIGURATION, port reset) parks the stream
    /// and says nothing about whether the transport forwards
    /// SET_INTERFACE.</summary>
    public void ResetAltSetting(byte interfaceNumber) => ApplyAltSetting(interfaceNumber, 0);

    private void ApplyAltSetting(byte interfaceNumber, byte altSetting)
    {
        lock (_lock)
        {
            _altSetting[interfaceNumber] = altSetting;
            _inferredOpen.Remove(interfaceNumber);
            if (altSetting == 0)
            {
                // Parking a stream resets its cursor so the next start
                // re-anchors instead of completing a burst instantly.
                _outCursor = 0;
                _inCursor = 0;
            }
            QueueAltLocked(interfaceNumber, altSetting);
        }
    }

    /// <summary>Queue a stream state change for the event thread. Caller
    /// holds <see cref="_lock"/>.</summary>
    private void QueueAltLocked(byte interfaceNumber, byte altSetting)
        => QueueEventLocked(new AudioEvent(false, interfaceNumber, altSetting, 0, 0));

    /// <summary>Caller holds <see cref="_lock"/>. After Dispose nothing is
    /// raised.</summary>
    private void QueueEventLocked(in AudioEvent e)
    {
        if (_stop || _eventWakeClosed) return;
        if (e.IsControl)
        {
            for (int i = 0; i < _events.Count; i++)
            {
                var queued = _events[i];
                if (queued.IsControl && queued.A == e.A && queued.B == e.B && queued.C == e.C)
                {
                    _events.RemoveAt(i);
                    break;
                }
            }
        }
        try
        {
            _events.Add(e);
        }
        catch (OutOfMemoryException)
        {
            // The queue could not grow. The change is dropped, and the
            // connection's reader gets no exception.
            if (DeviceOrchestrator.DiagEnabled)
                try { DeviceOrchestrator.LogDiag("the USB/IP audio engine dropped a change: out of memory to queue it"); } catch { }
            return;
        }
        if (_eventThread == null && !TryStartEventsLocked()) return;
        _eventWake.Set();
    }

    /// <summary>Starts the event thread. Caller holds <see cref="_lock"/>.
    /// False when the process cannot start a thread, being out of threads or
    /// memory. The connection's reader gets no exception and goes on
    /// answering the host, the queue keeps its changes, and the next change
    /// or Dispose tries again. The failure goes to the diagnostic log under
    /// HIDMAESTRO_DIAG=1.</summary>
    private bool TryStartEventsLocked()
    {
        Thread events;
        try
        {
            events = new Thread(EventLoop)
            {
                IsBackground = true,
                Name = "HMUsbipAudioEvents",
            };
            StartThread(events);
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ThreadStartException)
        {
            if (DeviceOrchestrator.DiagEnabled)
                try
                {
                    DeviceOrchestrator.LogDiag($"the USB/IP audio event thread did not start, {_events.Count} change(s) waiting: " +
                                               $"{ex.GetType().FullName}: {ex.Message}");
                }
                catch { }
            return false;
        }
        _eventThread = events;
        return true;
    }

    /// <summary>The event thread: raises each queued event in order, and
    /// ends at Dispose once the queue is empty.</summary>
    private void EventLoop()
    {
        try
        {
            while (true)
            {
                AudioEvent e;
                bool any;
                lock (_lock)
                {
                    // Each change the host made before Dispose is raised,
                    // then the thread ends.
                    any = _events.Count > 0;
                    e = any ? _events[0] : default;
                    if (any) _events.RemoveAt(0);
                    else if (_stop) return;
                }
                if (!any)
                {
                    _eventWake.WaitOne(250);
                    continue;
                }
                if (e.IsControl)
                {
                    ControlChanged?.Invoke(e.A, e.B, e.C, e.Value);
                }
                else if (!_altRaised.TryGetValue(e.A, out var last) || last != e.B)
                {
                    _altRaised[e.A] = e.B;
                    AltSettingChanged?.Invoke(e.A, e.B);
                }
            }
        }
        finally
        {
            lock (_lock) _eventWakeClosed = true;
            _eventWake.Dispose();
        }
    }

    /// <summary>Close each inferred stream whose traffic stopped. Runs on
    /// the pump thread, which must not wait on a consumer's handler, so
    /// the closes go to the event thread.</summary>
    private void CloseIdleInferredStreams(long now)
    {
        _inferClosing.Clear();
        lock (_lock)
        {
            if (_inferredOpen.Count == 0) return;
            foreach (var iface in _inferredOpen)
            {
                if (!_lastIsoTick.TryGetValue(iface, out var last) || now - last > StopAfterTicks)
                    _inferClosing.Add(iface);
            }
            foreach (var iface in _inferClosing)
            {
                _inferredOpen.Remove(iface);
                _altSetting[iface] = 0;
                QueueAltLocked(iface, 0);
            }
        }
    }

    /// <summary>True on the pump or the event thread, the threads that
    /// raise the engine's events.</summary>
    internal bool RunsOnCurrentThread
    {
        get
        {
            var current = Thread.CurrentThread;
            return current == _thread || current == Volatile.Read(ref _eventThread);
        }
    }

    public byte GetAltSetting(byte interfaceNumber)
    {
        lock (_lock) return _altSetting.TryGetValue(interfaceNumber, out var a) ? a : (byte)0;
    }

    // ── Microphone feed (called from the consumer's thread) ──────────────

    /// <summary>Append interleaved PCM to the microphone ring. Returns the
    /// bytes accepted, always a whole number of frames (the ring holds
    /// ~256 ms; excess is dropped so a runaway producer cannot grow
    /// latency without bound).</summary>
    public int SubmitMicSamples(ReadOnlySpan<byte> pcm)
    {
        lock (_lock)
        {
            // One byte stays reserved so a full ring is distinguishable
            // from an empty one, which makes the free count odd whenever
            // the frame size is even.
            int used = (_micHead - _micTail + _micRing.Length) % _micRing.Length;
            int free = _micRing.Length - 1 - used;
            int n = pcm.Length;
            if (n > free)
            {
                // Dropping the tail of a submit breaks the stream, so the
                // cut has to land the ring on a frame boundary. Anything
                // else leaves a fragment that shifts every later sample.
                // Only the truncating path floors: a submit that fits is
                // copied byte for byte, so a consumer feeding a continuous
                // stream in odd-sized chunks still frames correctly.
                n = free - ((used + free) % _micFrameBytes);
                if (n < 0) n = 0;
            }
            // At most two runs: to the ring's end, then from its start.
            int first = Math.Min(n, _micRing.Length - _micHead);
            pcm[..first].CopyTo(_micRing.AsSpan(_micHead));
            pcm[first..n].CopyTo(_micRing);
            _micHead += n;
            if (_micHead >= _micRing.Length) _micHead -= _micRing.Length;
            if (n < pcm.Length) NoteMicDrop(pcm.Length - n);
            return n;
        }
    }

    /// <summary>Account for a short submit. The caller's return value is
    /// the contractual signal; this is the breadcrumb for the case where
    /// a consumer ignores it, since an overrunning producer is otherwise
    /// invisible from inside the SDK (frames flow, buffers drain, nothing
    /// errors). Called under _lock.</summary>
    private void NoteMicDrop(int bytes)
    {
        _micDroppedBytes += bytes;
        if (_micDropLogged) return;
        _micDropLogged = true;
        DeviceOrchestrator.LogDiag(
            $"UsbAudioEngine: microphone submit truncated, dropped {bytes} bytes " +
            $"(ring {_micRing.Length} B, frame {_micFrameBytes} B). The producer is " +
            "outrunning the 1 ms service interval; check the Submit return value.");
    }

    /// <summary>Total microphone bytes refused because the ring was full.</summary>
    public long MicDroppedBytes
    {
        get { lock (_lock) return _micDroppedBytes; }
    }

    /// <summary>Bytes of microphone PCM buffered and not yet streamed.</summary>
    public int MicBufferedBytes
    {
        get { lock (_lock) return (_micHead - _micTail + _micRing.Length) % _micRing.Length; }
    }

    // ── UAC1 class-request handling (called from the reader thread) ──────

    /// <summary>GET_CUR/MIN/MAX/RES on a feature unit. Writes the raw
    /// little-endian value bytes to <paramref name="dst"/>, which holds two,
    /// and returns their count, or -1 to stall (unknown unit or selector,
    /// matching a real pad's refusal).</summary>
    public int HandleUacGet(byte bRequest, byte unitId, byte selector, byte channel, int wLength, Span<byte> dst)
    {
        if (!_knownUnits.Contains(unitId)) return -1;
        short value;
        lock (_lock)
        {
            switch (bRequest)
            {
                case 0x81: // GET_CUR
                    if (!_controlCur.TryGetValue((unitId, selector, channel), out value)) return -1;
                    break;
                case 0x82: // GET_MIN
                case 0x83: // GET_MAX
                case 0x84: // GET_RES
                    if (!_controlRange.TryGetValue((unitId, selector, channel), out var range)) return -1;
                    value = bRequest == 0x82 ? range.Min : bRequest == 0x83 ? range.Max : range.Res;
                    break;
                default:
                    return -1;
            }
        }
        // Mute is a 1-byte control, volume 2-byte; serve what the host asked.
        dst[0] = (byte)(value & 0xFF);
        if (wLength <= 1) return 1;
        dst[1] = (byte)((value >> 8) & 0xFF);
        return 2;
    }

    /// <summary>SET_CUR on a feature unit. Returns false to stall.</summary>
    public bool HandleUacSet(byte unitId, byte selector, byte channel, ReadOnlySpan<byte> data)
    {
        if (!_knownUnits.Contains(unitId)) return false;
        short value = data.Length >= 2 ? (short)(data[0] | (data[1] << 8))
                    : data.Length == 1 ? data[0] : (short)0;
        lock (_lock)
        {
            if (!_controlCur.ContainsKey((unitId, selector, channel))) return false;
            _controlCur[(unitId, selector, channel)] = value;
            QueueEventLocked(new AudioEvent(true, unitId, selector, channel, value));
        }
        return true;
    }

    // ── The pacing pump ──────────────────────────────────────────────────

    private void PumpLoop()
    {
        try
        {
            Pump();
        }
        finally
        {
            // The pump closes the handles it waits on, so a pump Dispose's
            // bounded join gave up on (a consumer's OutFrames handler still
            // running) never waits on a closed one. Under the lock, _stop
            // orders the close after every Set, Dispose's own included.
            lock (_lock) _stop = true;
            if (_timer != IntPtr.Zero) CloseHandle(_timer);
            _wake.Dispose();
        }
    }

    private void Pump()
    {
        var completedThisTick = new List<PendingIso>(8);
        while (!_stop)
        {
            long nextDue = long.MaxValue;
            completedThisTick.Clear();
            long now = Stopwatch.GetTimestamp();

            if (now >= _nextInferCheck)
            {
                _nextInferCheck = now + InferCheckTicks;
                CloseIdleInferredStreams(now);
            }

            lock (_lock)
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    var p = _pending[i];
                    if (p.DueTick <= now)
                    {
                        p.FrameAtCompletion = (int)((p.DueTick - _epoch) / TicksPerMs);
                        completedThisTick.Add(p);
                        _completing.Add(p);
                        _pending.RemoveAt(i);
                    }
                    else if (p.DueTick < nextDue)
                    {
                        nextDue = p.DueTick;
                    }
                }
                _armedDue = nextDue;
            }

            // Oldest first: _pending is submit-ordered and the reverse walk
            // above collected in reverse.
            for (int i = completedThisTick.Count - 1; i >= 0; i--)
                CompleteIso(completedThisTick[i]);

            if (nextDue == long.MaxValue)
            {
                _wake.WaitOne(250); // idle: nothing pending, park cheaply
                continue;
            }

            long remaining = nextDue - Stopwatch.GetTimestamp();
            if (remaining > 0)
            {
                // Land ~200 µs early on the timer, close the rest spinning.
                long timerTicks = remaining - (long)(200 * TicksPerUs);
                if (timerTicks > 0 && _timer != IntPtr.Zero)
                {
                    long due = -(timerTicks * 10_000_000L / Stopwatch.Frequency); // relative 100 ns units
                    if (SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    {
                        WaitForMultipleObjects(2, _waitPair, false, 250);
                    }
                    else
                    {
                        _wake.WaitOne(1);
                    }
                }
                while (Stopwatch.GetTimestamp() < nextDue && !_stop)
                {
                    if (nextDue - Stopwatch.GetTimestamp() > (long)(500 * TicksPerUs))
                    {
                        Thread.Yield();
                        break; // a new earlier submit may have arrived; re-evaluate
                    }
                    Thread.SpinWait(20);
                }
            }
        }
    }

    private void CompleteIso(PendingIso p)
    {
        if (p.IsIn)
        {
            // Fill each packet from the microphone ring, compacted per the
            // protocol. Baseline one service interval per packet; underrun
            // fills silence so the stream never starves usbaudio.
            int per = Math.Min(_micBytesPerInterval, p.PacketCount > 0 ? (int)p.Packets[0].Length : _micBytesPerInterval);
            if (per <= 0) per = _micBytesPerInterval;
            int length = per * p.PacketCount;
            if (_micOut.Length < length) _micOut = new byte[length];
            var data = _micOut.AsSpan(0, length);
            lock (_lock)
            {
                int buffered = (_micHead - _micTail + _micRing.Length) % _micRing.Length;
                int want = data.Length;
                int take = Math.Min(buffered, want);
                take -= take % _micFrameBytes; // never hand over a partial frame
                int first = Math.Min(take, _micRing.Length - _micTail);
                _micRing.AsSpan(_micTail, first).CopyTo(data);
                _micRing.AsSpan(0, take - first).CopyTo(data[first..]);
                _micTail += take;
                if (_micTail >= _micRing.Length) _micTail -= _micRing.Length;
                // The rest is silence.
                data[take..].Clear();
            }
            _complete(p, data, per);
        }
        else
        {
            if (p.OutLength > 0)
                OutFrames?.Invoke(new ReadOnlyMemory<byte>(p.OutPayload, 0, p.OutLength));
            _complete(p, ReadOnlySpan<byte>.Empty, 0);
        }
        // Sent, and no list holds it: the URB goes back for the next submit,
        // and an unlink that came meanwhile is answered after it.
        bool unlinked;
        uint unlinkSeqnum;
        UsbipServer.Connection? import;
        lock (_lock)
        {
            _completing.Remove(p);
            unlinked = p.Unlinked;
            unlinkSeqnum = p.UnlinkSeqnum;
            import = p.Import;
            ReturnUrbLocked(p);
        }
        if (unlinked) _unlinkAnswer?.Invoke(import, unlinkSeqnum);
    }

    /// <summary>Stops the pump and the event thread. Each closes its own
    /// handles as it ends, and the event thread first raises what is
    /// queued. Changes queued while the event thread could not start get a
    /// last try to start it here, and are dropped if that fails too. An
    /// engine that never raised an event has no event thread, and its wake
    /// event is closed here. Each join gives up after 500 ms,
    /// so a handler still running holds Dispose up that long at most. A
    /// second call returns. Called from a handler on either thread, it does
    /// not wait for that thread.</summary>
    public void Dispose()
    {
        Thread? events;
        lock (_lock)
        {
            if (_stop) return;
            _stop = true;
            _wake.Set();
            // Changes queued while the event thread could not start get one
            // more try here, so they are still raised.
            if (!_eventWakeClosed && _eventThread == null && _events.Count > 0)
                TryStartEventsLocked();
            events = _eventThread;
            if (!_eventWakeClosed)
            {
                if (events != null)
                {
                    _eventWake.Set();
                }
                else
                {
                    // No thread will ever wait on it or raise what is
                    // queued. The queue starts no thread after _stop.
                    if (_events.Count > 0 && DeviceOrchestrator.DiagEnabled)
                        try { DeviceOrchestrator.LogDiag($"the USB/IP audio engine dropped {_events.Count} change(s) at Dispose: its event thread did not start"); } catch { }
                    _events.Clear();
                    _eventWakeClosed = true;
                    _eventWake.Dispose();
                }
            }
        }
        if (Thread.CurrentThread != _thread)
            try { _thread.Join(500); } catch { }
        if (events != null && Thread.CurrentThread != events)
            try { events.Join(500); } catch { }
    }

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateWaitableTimerExW(IntPtr attrs, IntPtr name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr arg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForMultipleObjects(uint count, IntPtr[] handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint ms);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr h);
}
