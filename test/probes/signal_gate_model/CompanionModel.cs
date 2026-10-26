// The companion doorbell's gate (docs/design/signal-gate.md, "The companion
// doorbell"), checked the same way as the input event's: every interleaving
// of the SDK's writer, the companion's pump, and a caller that keeps one
// WAIT_FOR_INPUT outstanding and sends the next the moment the last
// completes (Windows.Gaming.Input's XusbDevice), with the SDK's stores held
// in a store buffer. Each request's IOCTL dispatch runs on a thread of its
// own, so the dispatch of one request can still be between its steps when
// the pump has completed it and the next request's dispatch runs. The pump
// waits on the doorbell and on its kick event and serves the parked request
// with the input section's SeqNo at that moment. The 8 ms timer is left out.
//
// In a final state the last frame must have been served. A doorbell on
// every frame with no kick, the protocol before 2.1.0, fails that: the pump
// can take the last ring before the caller has parked its next request,
// which then waits for the timer. The count raised after the park, the
// order companion.c uses (forward the request, then increment), passes: a
// dispatch held between its park and its increment can let the pump take
// the request first, and the count reads -1 for a moment, but the SDK rings
// on any count but 0 and the late dispatch's look kicks the pump for a
// frame newer than the last completion. Counting before the park passes
// too. Taking away the kick or the SDK's fence fails.
// A variant with the 8 ms timer reached no final state, since the caller
// sends its next request whenever one completes, so it checked nothing
// and is gone.
//
// Every completion is also checked against the one before it: a request
// served with the frame the last completion carried got that frame a
// second time. A pump that serves whatever is current on every doorbell
// and kick does that whenever one frame wakes it twice: its doorbell and
// the kick of a request that parked as it was written, or two doorbells
// whose frames the first serve already read. NewerOnly is the pump with one
// reader (companion.c CompanionServeReaders): on a doorbell or a kick it
// looks first, and serves only a frame newer than the reader's last
// completion. ReadersModel checks several readers.
using System;
using System.Collections.Generic;
using System.Linq;

internal static class CompanionModel
{
    internal sealed record Variant(string Name, bool Gated, bool SdkFence, bool CountFirst, bool Kick, bool Timer, bool ExpectLost,
                                   bool NewerOnly = false, bool ExpectTwice = true);

    internal static readonly Variant[] Variants =
    {
        new("companion before 2.1.0: a doorbell on every frame, no kick", false, true, true, false, false, true),
        new("companion, a doorbell on every frame and the kick (an older SDK on this driver)", false, true, true, true, false, false),
        new("companion gate, count before the park, kick, both fences", true, true, true, true, false, false),
        new("companion gate, count after the park, kick (companion.c's order)", true, true, false, true, false, false),
        new("companion gate, count before the park, no kick", true, true, true, false, false, true),
        new("companion gate, count before the park, kick, no fence in the SDK", true, false, true, true, false, true),
        new("companion, a doorbell on every frame, no kick, the pump serving only a newer frame", false, true, true, false, false, true, true, false),
        new("companion, a doorbell on every frame and the kick, the pump serving only a newer frame (an SDK before 2.1.0 on this driver, one reader)", false, true, true, true, false, false, true, false),
        new("companion gate, count after the park, kick, the pump serving only a newer frame (this driver, one reader)", true, true, false, true, false, false, true, false),
    };

    // A dispatch's program counters: its request is outstanding from the
    // start, in the queue from PARK.
    const int D_IDLE = 0, D_INC = 1, D_PARK = 2, D_LOOK = 3, D_KICK = 4;
    // Pump program counters. P_LOOK: NewerOnly's look before the take.
    const int P_WAIT = 0, P_TAKE = 1, P_DEC = 2, P_SERVE = 3, P_LOOK = 4;
    // SDK program counters.
    const int S_STORE = 0, S_FENCE = 1, S_LOAD = 2, S_RING = 3, S_DONE = 4;

    sealed record State(
        int MemSeq, int Parked, int Queue, int Served, int Doorbell, int KickFlag,
        int SdkPc, int SdkFrame, int SdkBuf,   // SdkBuf: a buffered SEQ store, 0 when empty
        bool Outstanding, int D0, int D1, int PumpPc, bool PumpBlocked, string Trace, bool Twice = false)
    {
        public string Key => $"{MemSeq},{Parked},{Queue},{Served},{Doorbell},{KickFlag},{SdkPc},{SdkFrame},{SdkBuf},{Outstanding},{D0},{D1},{PumpPc},{PumpBlocked},{Twice}";
        public int D(int i) => i == 0 ? D0 : D1;
        public State WithD(int i, int pc) => i == 0 ? this with { D0 = pc } : this with { D1 = pc };
    }

    internal sealed class Result { public int States, Finals; public string? Lost, InvariantBroken, Twice; }

    internal static Result Explore(Variant v, int frames)
    {
        var start = new State(0, 0, 0, 0, 0, 0, S_STORE, 0, 0, false, D_IDLE, D_IDLE, P_WAIT, false, "");
        var seen = new HashSet<string> { start.Key };
        var stack = new Stack<State>();
        stack.Push(start);
        var r = new Result();
        while (stack.Count > 0)
        {
            var s = stack.Pop();
            r.States++;
            if (s.Twice && r.Twice == null) r.Twice = $"a frame served twice after {s.Trace}";
            // Counting first, the count is never below the requests in the
            // queue: a ring is never skipped while one waits there.
            if (v.Gated && v.CountFirst && s.Parked < s.Queue && r.InvariantBroken == null)
                r.InvariantBroken = $"count {s.Parked} below the {s.Queue} parked after {s.Trace}";
            var next = Steps(s, v, frames).ToList();
            if (next.Count == 0)
            {
                r.Finals++;
                if (s.Served != frames && r.Lost == null)
                    r.Lost = $"final state with frame {s.Served} served of {frames}: {s.Trace}";
                continue;
            }
            foreach (var n in next)
                if (seen.Add(n.Key)) stack.Push(n);
        }
        return r;
    }

    static IEnumerable<State> Steps(State s, Variant v, int frames)
    {
        if (s.SdkBuf != 0)
            yield return s with { MemSeq = s.SdkBuf, SdkBuf = 0, Trace = s.Trace + " sdk:drain" };

        // The SDK: frame k is a store of SEQ = k, then the gate.
        switch (s.SdkPc)
        {
            case S_STORE:
                yield return s with
                {
                    SdkBuf = s.SdkFrame + 1,
                    SdkPc = !v.Gated ? S_RING : v.SdkFence ? S_FENCE : S_LOAD,
                    Trace = s.Trace + $" sdk:store{s.SdkFrame + 1}",
                };
                break;
            case S_FENCE:
                if (s.SdkBuf == 0) yield return s with { SdkPc = S_LOAD, Trace = s.Trace + " sdk:fence" };
                break;
            case S_LOAD:
                if (s.Parked != 0) yield return s with { SdkPc = S_RING, Trace = s.Trace + $" sdk:load-parked={s.Parked}" };
                else yield return Next(s with { Trace = s.Trace + " sdk:load-parked=0" }, frames);
                break;
            case S_RING:
                if (s.SdkBuf == 0)
                {
                    var n = s with { Trace = s.Trace + " sdk:ring" };
                    n = s.PumpBlocked ? n with { PumpBlocked = false, PumpPc = v.NewerOnly ? P_LOOK : P_TAKE } : n with { Doorbell = 1 };
                    yield return Next(n, frames);
                }
                break;
        }

        // The caller sends its next request once none is outstanding, on a
        // dispatch thread of its own.
        if (!s.Outstanding)
            for (int i = 0; i < 2; i++)
                if (s.D(i) == D_IDLE)
                {
                    int first = v.Gated && v.CountFirst ? D_INC : D_PARK;
                    yield return s.WithD(i, first) with { Outstanding = true, Trace = s.Trace + $" wgi:send(d{i})" };
                    break;
                }

        // The dispatch threads.
        for (int i = 0; i < 2; i++)
        {
            switch (s.D(i))
            {
                case D_INC:
                {
                    int after = v.CountFirst ? D_PARK : D_LOOK;
                    yield return s.WithD(i, after) with { Parked = s.Parked + 1, Trace = s.Trace + $" d{i}:inc" };
                    break;
                }
                case D_PARK:
                {
                    int after = !v.Gated ? (v.Kick ? D_LOOK : D_IDLE) : v.CountFirst ? D_LOOK : D_INC;
                    yield return s.WithD(i, after) with { Queue = 1, Trace = s.Trace + $" d{i}:park" };
                    break;
                }
                case D_LOOK:
                {
                    bool newer = s.MemSeq != s.Served;
                    yield return s.WithD(i, v.Kick && newer ? D_KICK : D_IDLE) with { Trace = s.Trace + $" d{i}:look-seq={s.MemSeq}" };
                    break;
                }
                case D_KICK:
                {
                    var n = s.WithD(i, D_IDLE) with { Trace = s.Trace + $" d{i}:kick" };
                    yield return s.PumpBlocked ? n with { PumpBlocked = false, PumpPc = v.NewerOnly ? P_LOOK : P_TAKE } : n with { KickFlag = 1 };
                    break;
                }
            }
        }

        // The pump.
        if (!s.PumpBlocked)
        {
            int woken = v.NewerOnly ? P_LOOK : P_TAKE;
            switch (s.PumpPc)
            {
                case P_WAIT:
                    if (s.Doorbell == 1) yield return s with { Doorbell = 0, PumpPc = woken, Trace = s.Trace + " pump:doorbell" };
                    if (s.KickFlag == 1) yield return s with { KickFlag = 0, PumpPc = woken, Trace = s.Trace + " pump:kick" };
                    if (s.Doorbell == 0 && s.KickFlag == 0) yield return s with { PumpBlocked = true, Trace = s.Trace + " pump:blocks" };
                    break;
                case P_LOOK:
                    // FrameNewerThanServed, before the take.
                    yield return s with { PumpPc = s.MemSeq != s.Served ? P_TAKE : P_WAIT, Trace = s.Trace + $" pump:look-seq={s.MemSeq}" };
                    break;
                case P_TAKE:
                    if (s.Queue == 1)
                        yield return s with { Queue = 0, PumpPc = v.Gated ? P_DEC : P_SERVE, Trace = s.Trace + " pump:take" };
                    else
                        yield return s with { PumpPc = P_WAIT, Trace = s.Trace + " pump:empty" };
                    break;
                case P_DEC:
                    yield return s with { Parked = s.Parked - 1, PumpPc = P_SERVE, Trace = s.Trace + " pump:dec" };
                    break;
                case P_SERVE:
                    // The completion carries the section's SeqNo now, and the
                    // caller may send its next request.
                    yield return s with { Served = s.MemSeq, Outstanding = false, PumpPc = P_WAIT, Trace = s.Trace + $" pump:serve={s.MemSeq}",
                                          Twice = s.Twice || s.MemSeq == s.Served };
                    break;
            }
        }

        // The 8 ms timer takes a parked request whatever the pump does, and
        // serves it as the pump would.
        if (v.Timer && s.Queue == 1)
            yield return s with
            {
                Queue = 0, Parked = v.Gated ? s.Parked - 1 : s.Parked, Served = s.MemSeq, Outstanding = false,
                Trace = s.Trace + $" timer:serve={s.MemSeq}",
            };
    }

    static State Next(State n, int frames)
    {
        int k = n.SdkFrame + 1;
        return n with { SdkFrame = k, SdkPc = k == frames ? S_DONE : S_STORE };
    }
}
