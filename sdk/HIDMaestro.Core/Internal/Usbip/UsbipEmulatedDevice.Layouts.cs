using System;
using System.Runtime.CompilerServices;
using System.Threading;
using HIDMaestro.Internal.Programs;

namespace HIDMaestro.Internal.Usbip;

// A device whose profile declares inputReports: it builds each report from
// its state block when its own clock ticks, in the layout its registers
// select, and answers commands on a bulk pair. SubmitState only replaces the
// state block. The protocol's data says when reports flow, how often, and
// what each command answers (design note, mechanisms 2 and 3).
internal sealed partial class UsbipEmulatedDevice
{
    private LayoutRunner? _layoutRunner;
    private IDeviceClock? _layoutClock;
    /// <summary>The interrupt IN endpoint the protocol names, where the
    /// device's own reports go: its reads park and complete in this queue,
    /// whichever interface holds it.</summary>
    private byte _layoutInEndpoint;
    private OwedReport? _owedReport;
    private SubmitReport? _submitReport;

    // A tick that found no read parked leaves one report owed, and the next
    // read is answered as it arrives: the device had its report ready and the
    // host polled late. Guarded by _hidLock.
    private bool _reportOwed;

    // The report the clock builds and sends, under _streamSendLock, which is
    // held from building a report to sending it by the clock and by a late
    // read alike, so two reports never reach the wire out of order.
    private byte[] _layoutReport = Array.Empty<byte>();
    private readonly object _streamSendLock = new();

    /// <summary>The clock's tick for a device that builds its reports,
    /// generic over the layouts' set, so a generated program inlines into the
    /// loop that sends its reports.</summary>
    internal struct LayoutTick<TSet> : IClockTick where TSet : struct, IReportSet
    {
        private readonly UsbipEmulatedDevice _dev;
        private readonly ProtocolEngine _engine;
        private readonly long _period;
        private TSet _set;
        private long _run;

        public LayoutTick(UsbipEmulatedDevice dev, ProtocolEngine engine, TSet set, long period)
        {
            _dev = dev; _engine = engine; _set = set; _period = period; _run = 0;
        }

        public bool Running(out long origin)
        {
            bool on = _engine.InputOpenSince(out origin);
            _run = origin;
            return on && !_dev._stop;
        }

        public long Period => _period;
        public long NotBefore => 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Tick(long due) => _dev.ClockReport(ref _set, _run);

        public void RunEnded()
        {
            lock (_dev._hidLock) _dev._reportOwed = false;
        }
    }

    /// <summary>Builds the clock for the bound set, over the set's own type.</summary>
    private sealed class LayoutClockFactory : ILayoutVisitor<IDeviceClock>
    {
        private readonly UsbipEmulatedDevice _dev;
        public LayoutClockFactory(UsbipEmulatedDevice dev) { _dev = dev; }

        public IDeviceClock Visit<TSet>(ref TSet set) where TSet : struct, IReportSet
        {
            var engine = _dev._engine!;
            long period = Math.Max(1, (long)Math.Round(engine.Protocol.ClockPeriodMs * System.Diagnostics.Stopwatch.Frequency / 1000.0));
            return new DeviceClock<LayoutTick<TSet>>(new LayoutTick<TSet>(_dev, engine, set, period),
                engine.Protocol.MissedTick, $"HMUsbipReports_{_dev._index}");
        }
    }

    /// <summary>The device clock of a device that builds its reports, made in
    /// the constructor before any thread starts.</summary>
    private void CreateLayoutClock(LayoutRunner runner)
    {
        int largest = 1;
        foreach (var l in _engine!.Protocol.Model.Layouts) largest = Math.Max(largest, l.Size);
        _layoutReport = GC.AllocateArray<byte>(largest, pinned: true);
        _layoutRunner = runner;
        _layoutInEndpoint = _engine.Protocol.InputEndpoint;
        _owedReport = new OwedReport(this);
        _submitReport = new SubmitReport(this);
        _layoutClock = runner.Visit(new LayoutClockFactory(this));
    }

    private void WakeLayoutClock() => _layoutClock?.Wake();

    /// <summary>One tick of the device clock: answer the oldest parked read
    /// with a report built now, or leave one owed. <paramref name="run"/>
    /// names the run the tick belongs to, by the time of the command that
    /// started it. A tick of a run that has ended, or that a later start has
    /// replaced, sends nothing, and so does a tick whose layout goes out on
    /// submit or on change with its bytes unchanged: the read stays parked
    /// and nothing is owed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void ClockReport<TSet>(ref TSet set, long run) where TSet : struct, IReportSet
    {
        var engine = _engine!;
        lock (_streamSendLock)
        {
            uint seq;
            int length;
            lock (_hidLock)
            {
                if (!engine.InputOpenSince(out long since) || since != run) return;
                var pending = PendingFor(_layoutInEndpoint);
                if (pending.Count == 0)
                {
                    if (engine.TickWouldSend(ref set)) _reportOwed = true;
                    return;
                }
                length = engine.BuildNext(ref set, _layoutReport);
                if (length == 0) return;
                seq = pending.Dequeue();
                CountCompletion();
            }
            SendRetSubmitSpan(seq, 0, length, _layoutReport.AsSpan(0, length));
        }
    }

    private void CountCompletion()
    {
        if (DiagPath != null)
            _completionsPerEp[_layoutInEndpoint] =
                _completionsPerEp.TryGetValue(_layoutInEndpoint, out var cc) ? cc + 1 : 1;
    }

    /// <summary>An interrupt IN read on the endpoint the device reports on.
    /// It parks until the clock's next tick, unless a tick already passed with
    /// no read to answer. While reports do not flow it parks and stays
    /// parked: the device answers every poll with NAK.</summary>
    private void SubmitLayoutRead(uint seqnum)
    {
        var engine = _engine!;
        lock (_streamSendLock)
        {
            int length;
            lock (_hidLock)
            {
                if (DiagPath != null)
                    _readsPerEp[_layoutInEndpoint] =
                        _readsPerEp.TryGetValue(_layoutInEndpoint, out var rc) ? rc + 1 : 1;
                if (!_reportOwed || !engine.InputOpenSince(out _))
                {
                    PendingFor(_layoutInEndpoint).Enqueue(seqnum);
                    return;
                }
                _reportOwed = false;
                length = BuildOwedReport();
                if (length == 0)
                {
                    // The report changed back to the one last sent, or the
                    // layout turned to one that goes out on submit.
                    PendingFor(_layoutInEndpoint).Enqueue(seqnum);
                    return;
                }
                CountCompletion();
            }
            SendRetSubmitSpan(seqnum, 0, length, _layoutReport.AsSpan(0, length));
        }
    }

    /// <summary>A SubmitState on a device whose selected layout goes out on
    /// submit: the oldest parked read gets the report built now, or one is
    /// owed. Called by the input pump once the new state block is in.</summary>
    private void SubmitLayoutReport()
    {
        var engine = _engine!;
        if (!engine.SendsOnSubmit) return;
        lock (_streamSendLock)
        {
            uint seq;
            int length;
            lock (_hidLock)
            {
                if (!engine.InputOpenSince(out _)) return;
                var pending = PendingFor(_layoutInEndpoint);
                if (pending.Count == 0)
                {
                    _reportOwed = true;
                    return;
                }
                length = _layoutRunner!.Visit(_submitReport!);
                if (length == 0) return;
                seq = pending.Dequeue();
                CountCompletion();
            }
            SendRetSubmitSpan(seq, 0, length, _layoutReport.AsSpan(0, length));
        }
    }

    private sealed class SubmitReport : ILayoutVisitor<int>
    {
        private readonly UsbipEmulatedDevice _dev;
        public SubmitReport(UsbipEmulatedDevice dev) { _dev = dev; }
        public int Visit<TSet>(ref TSet set) where TSet : struct, IReportSet => _dev._engine!.BuildOnSubmit(ref set, _dev._layoutReport);
    }

    /// <summary>A report for a read that arrived after the tick or the submit
    /// that owed it, built through the runner's set. Caller holds
    /// _streamSendLock and _hidLock.</summary>
    private int BuildOwedReport() => _engine!.SendsOnSubmit
        ? _layoutRunner!.Visit(_submitReport!)
        : _layoutRunner!.Visit(_owedReport!);

    private sealed class OwedReport : ILayoutVisitor<int>
    {
        private readonly UsbipEmulatedDevice _dev;
        public OwedReport(UsbipEmulatedDevice dev) { _dev = dev; }
        public int Visit<TSet>(ref TSet set) where TSet : struct, IReportSet => _dev._engine!.BuildNext(ref set, _dev._layoutReport);
    }

    /// <summary>A command on the bulk OUT endpoint, then the replies it left
    /// for parked bulk IN reads. Under the report lock, so a report built
    /// before this command is on the wire before the command's own answer:
    /// a host that selects another report then never sees the old one behind
    /// the reply.</summary>
    private void HandleBulkCommand(uint seqnum, ReadOnlySpan<byte> command)
    {
        var engine = _engine!;
        ProtocolEngine.CommandResult result;
        lock (_streamSendLock)
        {
            result = engine.HandleCommand(command, System.Diagnostics.Stopwatch.GetTimestamp());
            // A start owes nothing from before it. Its first report is the
            // clock's, one period after this command.
            if ((result.Effect & ProtocolEngine.Effect.InputOpened) != 0)
                lock (_hidLock) _reportOwed = false;
        }
        PublishCommand(result, command);
        SendRetSubmit(seqnum, 0, command.Length, null);
        CompleteBulkIn();
        // What the command did, as on the protocol's other command paths: a
        // halt fails the parked reads, and a change wakes the clocks. The
        // host's poll clock is among them: a reply this command left for the
        // input endpoint, or reports it let flow, wait for its next tick.
        ProtocolApply(result.Effect);
    }

    /// <summary>A reply a command on another path left for the bulk IN
    /// endpoint goes to a bulk read already parked for it, after the
    /// command's own answer, as HandleBulkCommand sends its reply. Only the
    /// connection's reader thread calls this.</summary>
    private void CompleteBulkReplies(ProtocolEngine.Effect effect)
    {
        if ((effect & ProtocolEngine.Effect.Reply) != 0 && _engine!.Protocol.Channel == ReplyChannel.BulkIn)
            CompleteBulkIn();
    }

    /// <summary>Hand waiting replies to parked bulk IN reads, oldest of each
    /// first. A read gets at most its buffer length of one reply and the rest
    /// stays at the head of the queue, so an 80-byte reply goes out as 64
    /// bytes and then 16 to a host that reads in 64s. Only the connection's
    /// reader thread calls this.</summary>
    private void CompleteBulkIn()
    {
        var engine = _engine!;
        byte endpoint = engine.Protocol.ReplyEndpoint;
        Span<byte> chunk = stackalloc byte[Math.Min(4096, Math.Max(64, engine.Protocol.MaxReplyLength))];
        while (true)
        {
            uint seq;
            int n;
            lock (_hidLock)
            {
                var pending = PendingFor(endpoint);
                if (pending.Count == 0 || !engine.HasReply) return;
                seq = pending.Dequeue();
                _parkedBulkLength.Remove(seq, out int capacity);
                n = engine.TakeReply(Math.Min(capacity, chunk.Length), chunk);
                if (n < 0) n = 0;
            }
            if (DeviceOrchestrator.DiagEnabled)
                DeviceOrchestrator.LogDiag($"usbip bulk in {BusId}: read {seq} gets {n} bytes {Convert.ToHexString(chunk[..n])}");
            SendRetSubmitSpan(seq, 0, n, chunk[..n]);
        }
    }

    /// <summary>A bulk IN read on the reply endpoint. Parked first, then
    /// served in arrival order. A read that finds no reply stays parked until
    /// a command produces one or the host unlinks it.</summary>
    private void SubmitBulkRead(uint seqnum, int length)
    {
        lock (_hidLock)
        {
            PendingFor(_engine!.Protocol.ReplyEndpoint).Enqueue(seqnum);
            _parkedBulkLength[seqnum] = Math.Max(0, length);
        }
        if (DeviceOrchestrator.DiagEnabled)
            DeviceOrchestrator.LogDiag($"usbip bulk in {BusId}: read {seqnum} for {length} bytes");
        CompleteBulkIn();
    }

    /// <summary>A device that builds its reports, as it is at attach and
    /// after a port reset: no read owed and no bulk read parked. The engine's
    /// own reset puts its registers back.</summary>
    private void ResetLayoutState()
    {
        if (_engine is not { Protocol.BuildsReports: true } && _engine?.Protocol.Channel != ReplyChannel.BulkIn) return;
        lock (_hidLock)
        {
            _parkedBulkLength.Clear();
            _reportOwed = false;
        }
    }
}
