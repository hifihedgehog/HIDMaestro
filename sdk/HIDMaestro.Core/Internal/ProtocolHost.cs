using System;
using System.Threading;
using HIDMaestro.Internal.Programs;
using HIDMaestro.Internal.Usbip;

namespace HIDMaestro.Internal;

/// <summary>Where a UMDF2 device's protocol host sends its messages: the
/// controller's input section, whose frames the HID driver sends as input
/// reports, one READ_REPORT each, and acknowledges on HID class's next read
/// (issue #67's acknowledgement).</summary>
internal interface IHostChannel
{
    /// <summary>Writes one frame: <paramref name="dataLen"/> bytes of
    /// <paramref name="data"/> on the data lane, a live feature report or
    /// nothing, and <paramref name="extendedLen"/> bytes of
    /// <paramref name="extended"/> on the extended lane, an input report as
    /// the driver sends it, or nothing. Returns the frame's sequence
    /// number.</summary>
    uint Write(byte[] data, int dataLen, byte[] extended, int extendedLen);

    /// <summary>Whether the driver has delivered the frame written with
    /// <paramref name="seq"/>, or a later one, waiting up to
    /// <paramref name="waitMs"/> for it.</summary>
    bool Delivered(uint seq, int waitMs);

    /// <summary>Whether the driver acknowledges delivery at all.</summary>
    bool AcknowledgesDelivery { get; }
}

/// <summary>The protocol of a UMDF2 device, run in the SDK (design note,
/// decision (b)). The output reader hands each output report to the engine
/// before it raises OutputReceived. Replies go to the driver ahead of
/// reports, each held until the driver has delivered the message before it,
/// so no reply is overwritten. The device clock sends the layouts that go
/// out on a period, and SubmitState those that go out on submit, with the
/// live feature report on the data lane. Every send runs under the
/// controller's submit lock, the input section's one writer.</summary>
internal sealed unsafe class ProtocolHost : IDisposable
{
    /// <summary>How long a message waits for the one before it to be
    /// delivered, as a keyboard frame does (HMController.DeliveryTimeoutMs),
    /// and after three misses in a row the host stops waiting until an
    /// acknowledgement shows up again.</summary>
    internal const int DeliveryTimeoutMs = 200;

    public ProtocolEngine Engine { get; }
    public CompiledProtocol Protocol { get; }
    private readonly LayoutRunner? _runner;
    private readonly IHostChannel _channel;
    private readonly object _sendLock;
    private readonly Builder _clockBuilder;
    private readonly Builder _submitBuilder;
    private readonly byte[] _reply;
    private readonly byte[] _feature;
    private readonly byte _featureId;
    private readonly bool _hasFeature;
    private readonly bool _sendsOnSubmit;

    // The last reply written, whose delivery the next message waits for.
    private uint _replySeq;
    private bool _replyPending;
    private int _misses;

    private DeviceClock<HostTick>? _clock;
    private readonly int _index;
    private long _origin;
    private int _started;

    /// <summary>The device clock's tick: one <see cref="Tick"/> of the host
    /// at each point of the grid its start sets.</summary>
    private readonly struct HostTick : IClockTick
    {
        private readonly ProtocolHost _host;
        private readonly long _period;
        public HostTick(ProtocolHost host, long period) { _host = host; _period = period; }
        public bool Running(out long origin) { origin = _host._origin; return true; }
        public long Period => _period;
        public long NotBefore => 0;
        public void Tick(long due) => _host.Tick(DeliveryTimeoutMs);
        public void RunEnded() { }
    }

    private sealed class Builder : ILayoutVisitor<int>
    {
        public ProtocolEngine Engine = null!;
        public byte[] Buffer = Array.Empty<byte>();
        public bool Submit;
        // A submit's block, the live feature report's ID or -1, its buffer
        // and the length Engine.Submit gave it.
        public byte[] Block = Array.Empty<byte>();
        public int FeatureId = -1;
        public byte[] Feature = Array.Empty<byte>();
        public int FeatureLength;
        public int Visit<TSet>(ref TSet set) where TSet : struct, IReportSet =>
            Submit ? Engine.Submit(ref set, Block, Buffer, FeatureId, Feature, out FeatureLength) : Engine.BuildNext(ref set, Buffer);
    }

    public ProtocolHost(ControllerProfile profile, CompiledProtocol protocol, int index, IHostChannel channel, object sendLock)
    {
        Protocol = protocol;
        _runner = ProgramBinder.BindLayouts(protocol.Model);
        Engine = new ProtocolEngine(protocol, profile.SerialString, index, _runner);
        // A device that builds its reports starts from a pad at rest.
        if (protocol.BuildsReports || protocol.Model.LiveFeatures.Length > 0)
            Engine.SetStateBlock(profile.StateBlockAtRest());
        _channel = channel;
        _sendLock = sendLock;
        _index = index;
        int largest = 1;
        foreach (var l in protocol.Model.Layouts) largest = Math.Max(largest, l.Size);
        _clockBuilder = new Builder { Engine = Engine, Buffer = new byte[largest] };
        _submitBuilder = new Builder { Engine = Engine, Buffer = new byte[largest], Submit = true, Block = new byte[protocol.Model.StateSize] };
        _reply = new byte[Math.Max(1, protocol.MaxReplyLength)];
        var model = protocol.Model;
        _hasFeature = model.LiveFeatures.Length > 0;
        _sendsOnSubmit = Array.Exists(model.Layouts, l => l.Send == SendRule.Submit);
        if (_hasFeature)
        {
            _featureId = (byte)model.LiveFeatureIds[0];
            _feature = new byte[model.LiveFeatures[0].Spec.Size];
            _submitBuilder.FeatureId = _featureId;
            _submitBuilder.Feature = _feature;
        }
        else _feature = Array.Empty<byte>();
    }

    /// <summary>Sends the state at rest and starts the device clock, when a
    /// layout goes out on it. Its origin is this start: on UMDF2 the
    /// device's attach is when the create has read the driver's half of
    /// the version section.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        // A device that sends on submit or answers a live feature report
        // reports its state block, at rest until a submit, once it starts:
        // a read or a GET before the first SubmitState finds that state,
        // where the driver would have nothing to give.
        if (Protocol.Model.LiveFeatures.Length > 0 || Array.Exists(Protocol.Model.Layouts, l => l.Send == SendRule.Submit))
            OnState(Engine.CopyStateBlock());
        if (!NeedsClock()) return;
        _origin = System.Diagnostics.Stopwatch.GetTimestamp();
        long period = Math.Max(1, (long)Math.Round(Protocol.ClockPeriodMs * System.Diagnostics.Stopwatch.Frequency / 1000.0));
        // The device clock of mechanism 2, above normal priority as the
        // idle repeat and the USB/IP input pump run (decision (b)). Its
        // ticks hold the grid from the start (decision (e)): a tick that
        // cannot send, a reply still undelivered, sends nothing, and the
        // next comes at the next grid point.
        _clock = new DeviceClock<HostTick>(new HostTick(this, period), MissedTick.NextPoll, $"HMProtocolClock_{_index}");
        _clock.Start();
    }

    private bool NeedsClock()
    {
        foreach (var l in Protocol.Model.Layouts)
            if (l.Send != SendRule.Submit) return true;
        return false;
    }

    /// <summary>One tick of the device clock: the replies that wait go out
    /// first, then the layout the registers select when it goes out on the
    /// clock or has changed.</summary>
    public void Tick(int waitMs)
    {
        lock (_sendLock)
        {
            PumpLocked(waitMs);
            if (_runner == null) return;
            int n = _runner.Visit(_clockBuilder);
            if (n <= 0) return;
            // A report never overwrites a reply the driver has not
            // delivered. One that cannot wait is not sent.
            if (!AwaitReplyLocked(waitMs)) return;
            _channel.Write(_feature, 0, _clockBuilder.Buffer, n);
        }
    }

    /// <summary>An output report the driver handed on, report ID first: the
    /// rule it matches runs, and a reply goes out. What OutputReceived
    /// raises in its place: nothing when the rule or the protocol consumes
    /// it, the report the rule emits, or the report as written.</summary>
    /// <returns>The length of the report to raise, in <paramref name="report"/>,
    /// or 0 for none.</returns>
    public int OnOutput(Span<byte> report, int length, int waitMs)
    {
        var command = report[..length];
        var result = Engine.HandleCommand(command, System.Diagnostics.Stopwatch.GetTimestamp());
        if ((result.Effect & ProtocolEngine.Effect.Reply) != 0) Pump(waitMs);
        if (Protocol.ConsumesCommands || result.Consume) return 0;
        if (result.Emit < 0) return length;
        Span<byte> emitted = stackalloc byte[ProgramForm.MaxReport];
        int n = Engine.BuildEmit(result.Emit, command, emitted);
        n = Math.Min(n, report.Length);
        emitted[..n].CopyTo(report);
        return n;
    }

    /// <summary>Sends the replies that wait, each once the message before it
    /// is delivered or <paramref name="waitMs"/> has passed.</summary>
    public void Pump(int waitMs)
    {
        lock (_sendLock) PumpLocked(waitMs);
    }

    private void PumpLocked(int waitMs)
    {
        while (Engine.ReplyWaiting)
        {
            if (!AwaitReplyLocked(waitMs)) return;
            int n = Engine.TakeReply(_reply.Length, _reply);
            if (n <= 0) return;
            _replySeq = _channel.Write(_feature, 0, _reply, n);
            _replyPending = _channel.AcknowledgesDelivery;
        }
    }

    /// <summary>True once the last reply is delivered, or when the caller
    /// may send past it. A wait that times out counts a miss, and the next
    /// message goes out: Windows is not reading the device. After three
    /// misses in a row the host stops waiting, as the keyboard path does,
    /// until an acknowledgement shows up again. A caller that does not wait
    /// (<paramref name="waitMs"/> 0) gets false while the reply is out.</summary>
    private bool AwaitReplyLocked(int waitMs)
    {
        if (!_replyPending) return true;
        if (_channel.Delivered(_replySeq, _misses >= 3 ? 0 : waitMs))
        {
            _misses = 0;
            _replyPending = false;
            return true;
        }
        if (waitMs == 0) return false;
        _misses++;
        _replyPending = false;
        return true;
    }

    /// <summary>A new state block from SubmitState or a raw submit. The
    /// layout the registers select goes out now when it goes out on submit,
    /// with the live feature report on the data lane, in one frame.</summary>
    public void OnState(ReadOnlySpan<byte> block)
    {
        // A device whose reports all go out on its clock sends nothing on a
        // submit: the block is all it takes.
        if (!_sendsOnSubmit && !_hasFeature)
        {
            Engine.SetStateBlock(block);
            return;
        }
        lock (_sendLock)
        {
            int n, f;
            if (_runner != null && block.Length == _submitBuilder.Block.Length)
            {
                // The block, the submit layout and the live feature report
                // under the engine's one lock.
                block.CopyTo(_submitBuilder.Block);
                n = _runner.Visit(_submitBuilder);
                f = _submitBuilder.FeatureLength;
            }
            else
            {
                Engine.SetStateBlock(block);
                n = 0;
                f = _hasFeature ? Engine.BuildLiveFeature(_featureId, _feature) : 0;
            }
            if (n <= 0 && f <= 0) return;
            if (!AwaitReplyLocked(DeliveryTimeoutMs)) return;
            _channel.Write(_feature, Math.Max(0, f), _submitBuilder.Buffer, Math.Max(0, n));
        }
    }

    public void Dispose() => _clock?.Stop(2000);

    /// <summary>The clock's thread, or null before a start that needed one.</summary>
    internal Thread? ClockThread => _clock?.Thread;
}
