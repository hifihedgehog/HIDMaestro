// Round 6's SDK write against the HID worker and the XUSB companion, with
// the SDK's stores held in a store buffer (docs/design/signal-gate.md,
// "Checks"). The SDK steps follow SharedMemoryIO.WriteInputFrame at
// f54266e9: the even SeqNo's volatile store; with the capability set, the
// read of InputHandoff and, for a final SeqNo owed there, the
// compare-exchange that puts this frame's in its place; otherwise the read
// of InputWaiters, and with a worker counted the fast branch, which signals
// and, for a frame with a companion event, runs Thread.MemoryBarrier()
// before the companion's count is read; otherwise the provisional exchange,
// the recheck of the count, and the compare-exchange that makes the frame
// final or takes it back. Then the companion's count, and the SetEvent
// calls. Every frame here carries GIP bytes, so each has a companion event.
//
// Memory. On x64 (TSO) a store waits in the store buffer and a later load
// of another variable may run before it is visible. On ARM64 with LRCPC,
// Volatile.Write is stlr and Volatile.Read is ldapr, so a volatile read may
// pass an earlier volatile write and nothing else moves: release keeps the
// SeqNo's store after the payload's, acquire keeps each read before the
// accesses after it. The SDK's only plain-ordered store is the SeqNo's and
// all its loads are volatile reads, so both architectures allow the same
// reordering here, a load passing the buffered SeqNo store. Interlocked
// operations, Thread.MemoryBarrier() and the SetEvent calls run only with
// the buffer empty: .NET's interlocked operations are full fences, and
// SetEvent's state change is a locked operation in the kernel, after the
// SDK's stores. The worker's count and take are interlocked, and its read
// follows a wait, so it has no buffer of its own.
//
// In a final state the HID worker must have read the last frame and the
// companion must have served it. The worker's 500 ms timeout and the
// companion's 8 ms timer are left out, so a frame left in the handoff with
// the worker blocked, or a WAIT_FOR_INPUT left parked with no doorbell
// rung and no kick, shows as a final state with the frame not read or not
// served: one the timeout or the timer would serve late. A run with the
// timer reaches no final state, since the caller sends its next request
// whenever one completes, so the timer stays out of every variant.
using System;
using System.Collections.Generic;
using System.Linq;

internal static class Round6FenceModel
{
    internal sealed record Variant(string Name, string Arch, bool FastFence, bool XchgFenced, bool Timer, bool ExpectInputLost, bool ExpectCompanionLost);

    internal static readonly Variant[] Variants =
    {
        new("round 6 SDK with the fast branch's fence", "x64 (TSO)", true, true, false, false, false),
        new("round 6 SDK with the fast branch's fence", "ARM64 (a volatile read passes an earlier volatile write)", true, true, false, false, false),
        new("the fast branch without its fence", "x64 (TSO)", false, true, false, false, true),
        new("the fast branch without its fence", "ARM64 (a volatile read passes an earlier volatile write)", false, true, false, false, true),
        new("the provisional exchange as a plain store", "x64 (TSO)", true, false, false, true, false),
    };

    // SDK program counters.
    const int S_STORE = 0, S_OWED = 1, S_CAS_OWED = 2, S_COUNT = 3, S_FENCE = 4, S_XCHG = 5, S_RECHECK = 6,
              S_CAS_FINAL = 7, S_CAS_BACK = 8, S_COMP = 9, S_SIGNAL = 10, S_DONE = 11;
    // HID worker program counters: count, take, the decrement after a take
    // that found a final SeqNo, the zero-timeout poll, the wait, the
    // decrement after it, the read.
    const int W_INC = 0, W_TAKE = 1, W_DECP = 2, W_POLL = 3, W_WAIT = 4, W_DEC = 5, W_READ = 6;
    // Companion dispatch and pump, as CompanionModel has them, in
    // companion.c's order: park, then count, then look.
    const int D_IDLE = 0, D_PARK = 1, D_INC = 2, D_LOOK = 3, D_KICK = 4;
    const int P_WAIT = 0, P_TAKE = 1, P_DEC = 2, P_SERVE = 3;

    internal sealed record State(
        int MemSeq, int Waiters, int Hand, int Event,
        int BufLo, int BufHi,                    // buffered SeqNo stores BufLo..BufHi, none when BufLo > BufHi
        int HandBuf,                             // the broken SDK's buffered handoff store after them, 0 for none
        int SdkPc, int Frame, int Owed, bool SigEvent, bool SigComp,
        int WPc, bool WBlocked, int Seen,
        int Parked, int Queue, int Served, int Doorbell, int KickFlag, bool Outstanding, int D0, int D1, int PumpPc, bool PumpBlocked,
        string Trace)
    {
        public bool BufEmpty => BufLo > BufHi && HandBuf == 0;
        public string Key =>
            $"{MemSeq},{Waiters},{Hand},{Event},{BufLo},{BufHi},{HandBuf},{SdkPc},{Frame},{Owed},{SigEvent},{SigComp},{WPc},{WBlocked},{Seen}," +
            $"{Parked},{Queue},{Served},{Doorbell},{KickFlag},{Outstanding},{D0},{D1},{PumpPc},{PumpBlocked}";
        public int D(int i) => i == 0 ? D0 : D1;
        public State WithD(int i, int pc) => i == 0 ? this with { D0 = pc } : this with { D1 = pc };
    }

    internal sealed class Result
    {
        public int States, Finals;
        public string? InputLost, CompanionLost;
        // The fast branch's count read taken while the SeqNo's store still
        // waited in the buffer, and the branch it chose then.
        public int EarlyCountNonzero, EarlyCountZero;
    }

    internal static Result Explore(Variant v, int frames)
    {
        var start = new State(0, 0, 0, 0, 1, 0, 0, S_STORE, 0, 0, false, false, W_INC, false, 0,
                              0, 0, 0, 0, 0, false, D_IDLE, D_IDLE, P_WAIT, false, "");
        var seen = new HashSet<string> { start.Key };
        var stack = new Stack<State>();
        stack.Push(start);
        var r = new Result();
        while (stack.Count > 0)
        {
            var s = stack.Pop();
            r.States++;
            var next = Steps(s, v, frames, r).ToList();
            if (next.Count == 0)
            {
                r.Finals++;
                if (s.Seen != frames && r.InputLost == null)
                    r.InputLost = $"final state with frame {s.Seen} read of {frames}: {s.Trace}";
                if (s.Served != frames && r.CompanionLost == null)
                    r.CompanionLost = $"final state with frame {s.Served} served of {frames}: {s.Trace}";
                continue;
            }
            foreach (var n in next)
                if (seen.Add(n.Key)) stack.Push(n);
        }
        return r;
    }

    static int Final(int k) => 2 * k;
    static int Provisional(int k) => 2 * k + 1;

    static IEnumerable<State> Steps(State s, Variant v, int frames, Result r)
    {
        // The buffer drains oldest first, at any step.
        if (s.BufLo <= s.BufHi)
            yield return s with { MemSeq = s.BufLo, BufLo = s.BufLo + 1, Trace = s.Trace + $" sdk:drain{s.BufLo}" };
        else if (s.HandBuf != 0)
            yield return s with { Hand = s.HandBuf, HandBuf = 0, Trace = s.Trace + $" sdk:drain-handoff={s.HandBuf}" };

        int k = s.Frame + 1;
        switch (s.SdkPc)
        {
            case S_STORE:
                // The even SeqNo's Volatile.Write.
                yield return s with { BufLo = s.BufLo > s.BufHi ? k : s.BufLo, BufHi = k, SdkPc = S_OWED, SigEvent = false, SigComp = false, Trace = s.Trace + $" sdk:store{k}" };
                break;
            case S_OWED:
            {
                // Volatile.Read(InputHandoff): a final SeqNo is owed.
                int owed = s.HandBuf != 0 ? s.HandBuf : s.Hand;
                bool isOwed = owed != 0 && (owed & 1) == 0;
                yield return s with { Owed = owed, SdkPc = isOwed ? S_CAS_OWED : S_COUNT, Trace = s.Trace + $" sdk:read-handoff={owed}" };
                break;
            }
            case S_CAS_OWED:
                if (s.BufEmpty)
                {
                    if (s.Hand == s.Owed)
                        yield return s with { Hand = Final(k), SigEvent = false, SdkPc = S_COMP, Trace = s.Trace + $" sdk:replace-owed->{Final(k)}" };
                    else
                        yield return s with { SdkPc = S_COUNT, Trace = s.Trace + " sdk:replace-failed" };
                }
                break;
            case S_COUNT:
            {
                // Volatile.Read(InputWaiters), with no fence of its own: it
                // may run before the SeqNo's store is visible.
                bool early = !s.BufEmpty;
                if (s.Waiters != 0)
                {
                    if (early) r.EarlyCountNonzero++;
                    yield return s with { SigEvent = true, SdkPc = v.FastFence ? S_FENCE : S_COMP, Trace = s.Trace + $" sdk:count={s.Waiters}(fast)" };
                }
                else
                {
                    if (early) r.EarlyCountZero++;
                    yield return s with { SdkPc = S_XCHG, Trace = s.Trace + " sdk:count=0" };
                }
                break;
            }
            case S_FENCE:
                // Thread.MemoryBarrier() before the companion's count.
                if (s.BufEmpty) yield return s with { SdkPc = S_COMP, Trace = s.Trace + " sdk:fence" };
                break;
            case S_XCHG:
                if (v.XchgFenced)
                {
                    if (s.BufEmpty) yield return s with { Hand = Provisional(k), SdkPc = S_RECHECK, Trace = s.Trace + $" sdk:xchg-provisional={Provisional(k)}" };
                }
                else
                    // The broken SDK: the provisional SeqNo as a plain store,
                    // left in the buffer, which the recheck's load may pass.
                    yield return s with { HandBuf = Provisional(k), SdkPc = S_RECHECK, Trace = s.Trace + $" sdk:store-provisional={Provisional(k)}" };
                break;
            case S_RECHECK:
            {
                // The capability (always set here), then the count.
                bool kept = s.Waiters == 0;
                yield return s with { SdkPc = kept ? S_CAS_FINAL : S_CAS_BACK, Trace = s.Trace + $" sdk:recheck-count={s.Waiters}" };
                break;
            }
            case S_CAS_FINAL:
                if (s.BufEmpty)
                {
                    if (s.Hand == Provisional(k))
                        yield return s with { Hand = Final(k), SigEvent = false, SdkPc = S_COMP, Trace = s.Trace + $" sdk:final={Final(k)}" };
                    else
                        yield return s with { SigEvent = true, SdkPc = S_COMP, Trace = s.Trace + " sdk:final-failed" };
                }
                break;
            case S_CAS_BACK:
                if (s.BufEmpty)
                {
                    var n = s.Hand == Provisional(k) ? s with { Hand = 0, Trace = s.Trace + " sdk:take-back" } : s with { Trace = s.Trace + " sdk:take-back-failed" };
                    yield return n with { SigEvent = true, SdkPc = S_COMP };
                }
                break;
            case S_COMP:
                // Volatile.Read(CompanionParked): ring only with one counted.
                yield return s with { SigComp = s.Parked != 0, SdkPc = S_SIGNAL, Trace = s.Trace + $" sdk:companion-count={s.Parked}" };
                break;
            case S_SIGNAL:
                if (s.BufEmpty)
                {
                    var n = s with { Trace = s.Trace + $" sdk:signal(event={(s.SigEvent ? 1 : 0)},doorbell={(s.SigComp ? 1 : 0)})" };
                    if (s.SigEvent)
                        n = s.WPc == W_WAIT && s.WBlocked ? n with { WBlocked = false, WPc = W_DEC } : n with { Event = 1 };
                    if (s.SigComp)
                        n = n.PumpBlocked ? n with { PumpBlocked = false, PumpPc = P_TAKE } : n with { Doorbell = 1 };
                    yield return n with { Frame = k, SdkPc = k == frames ? S_DONE : S_STORE };
                }
                break;
        }

        // The HID worker. Its accesses are interlocked or follow a wait.
        if (!s.WBlocked)
        {
            switch (s.WPc)
            {
                case W_INC:
                    yield return s with { Waiters = s.Waiters + 1, WPc = W_TAKE, Trace = s.Trace + " w:inc" };
                    break;
                case W_TAKE:
                {
                    int t = s.Hand;
                    bool final = t != 0 && (t & 1) == 0;
                    yield return s with { Hand = 0, WPc = final ? W_DECP : W_WAIT, Trace = s.Trace + $" w:take={t}" };
                    break;
                }
                case W_DECP:
                    yield return s with { Waiters = s.Waiters - 1, WPc = W_POLL, Trace = s.Trace + " w:dec" };
                    break;
                case W_POLL:
                    yield return s with { Event = 0, WPc = W_READ, Trace = s.Trace + (s.Event == 1 ? " w:poll-takes-event" : " w:poll") };
                    break;
                case W_WAIT:
                    if (s.Event == 1) yield return s with { Event = 0, WPc = W_DEC, Trace = s.Trace + " w:wait-takes" };
                    else yield return s with { WBlocked = true, Trace = s.Trace + " w:blocks" };
                    break;
                case W_DEC:
                    yield return s with { Waiters = s.Waiters - 1, WPc = W_READ, Trace = s.Trace + " w:dec" };
                    break;
                case W_READ:
                    yield return s with { Seen = s.MemSeq, WPc = W_INC, Trace = s.Trace + $" w:read={s.MemSeq}" };
                    break;
            }
        }

        // The caller sends its next WAIT_FOR_INPUT once none is outstanding.
        if (!s.Outstanding)
            for (int i = 0; i < 2; i++)
                if (s.D(i) == D_IDLE)
                {
                    yield return s.WithD(i, D_PARK) with { Outstanding = true, Trace = s.Trace + $" wgi:send(d{i})" };
                    break;
                }
        for (int i = 0; i < 2; i++)
        {
            switch (s.D(i))
            {
                case D_PARK:
                    yield return s.WithD(i, D_INC) with { Queue = 1, Trace = s.Trace + $" d{i}:park" };
                    break;
                case D_INC:
                    yield return s.WithD(i, D_LOOK) with { Parked = s.Parked + 1, Trace = s.Trace + $" d{i}:inc" };
                    break;
                case D_LOOK:
                {
                    bool newer = s.MemSeq != s.Served;
                    yield return s.WithD(i, newer ? D_KICK : D_IDLE) with { Trace = s.Trace + $" d{i}:look-seq={s.MemSeq}" };
                    break;
                }
                case D_KICK:
                {
                    var n = s.WithD(i, D_IDLE) with { Trace = s.Trace + $" d{i}:kick" };
                    yield return s.PumpBlocked ? n with { PumpBlocked = false, PumpPc = P_TAKE } : n with { KickFlag = 1 };
                    break;
                }
            }
        }
        if (!s.PumpBlocked)
        {
            switch (s.PumpPc)
            {
                case P_WAIT:
                    if (s.Doorbell == 1) yield return s with { Doorbell = 0, PumpPc = P_TAKE, Trace = s.Trace + " pump:doorbell" };
                    if (s.KickFlag == 1) yield return s with { KickFlag = 0, PumpPc = P_TAKE, Trace = s.Trace + " pump:kick" };
                    if (s.Doorbell == 0 && s.KickFlag == 0) yield return s with { PumpBlocked = true, Trace = s.Trace + " pump:blocks" };
                    break;
                case P_TAKE:
                    if (s.Queue == 1) yield return s with { Queue = 0, PumpPc = P_DEC, Trace = s.Trace + " pump:take" };
                    else yield return s with { PumpPc = P_WAIT, Trace = s.Trace + " pump:empty" };
                    break;
                case P_DEC:
                    yield return s with { Parked = s.Parked - 1, PumpPc = P_SERVE, Trace = s.Trace + " pump:dec" };
                    break;
                case P_SERVE:
                    yield return s with { Served = s.MemSeq, Outstanding = false, PumpPc = P_WAIT, Trace = s.Trace + $" pump:serve={s.MemSeq}" };
                    break;
            }
        }
        if (v.Timer && s.Queue == 1)
            yield return s with { Queue = 0, Parked = s.Parked - 1, Served = s.MemSeq, Outstanding = false, Trace = s.Trace + $" timer:serve={s.MemSeq}" };
    }
}
