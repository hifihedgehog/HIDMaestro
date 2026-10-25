// The input gate's delivery rules (docs/design/signal-gate.md, "Which frame
// is new"): every interleaving of programs that start one after another on
// an index and the HID driver's worker, with every step that used to forget
// the last SeqNo. Memory is sequentially consistent here: Program.cs checks
// the fences. This part checks what the worker takes for a new frame.
//
// A 2.1.0 program counts itself in SdkStarts before it makes its owner pipe,
// zeroes the input section, counts its frames from above the SeqNo it found
// and DriverSeenSeq, steps over 0 at the wrap, writes each frame, and skips
// the SetEvent when the capability is set and no worker is counted. A
// v2.0.x program counts from 0 and signals every frame. The check and the
// SetEvent are two steps, so a worker can read the frame between them and
// the event is left set for a frame already read. A program's end closes its
// owner pipe after its last SetEvent, which the worker sees only when its
// owner watch is connected to that program. The worker runs
// SharedInputWorkerProc's loop: count itself, look at the section, poll or
// wait, read. Its wait returns the input event before the owner pipe, as
// WaitForMultipleObjects returns the lowest index signaled. It connects its
// owner watch to a running program, recycles after idle timeouts or after a
// stale wakeup, and its device can go and come back (a re-add): a new
// context that maps the signal section again.
//
// Two checks. No frame (program, SeqNo) goes to HID class twice. In a final
// state the frame the section holds has gone to HID class. Each variant
// either passes both or is expected to fail one, and failing it shows the
// check finds that defect.
//
// A v2.0.x program counts from 0, so its first frames can carry the SeqNo of
// the last frame read. v2.0.3 sends them once it has handled the last
// program's end (OwnerGone, the recycle with no owner watch, or a device
// added again), and the variants start a v2.0.x program after that. In the
// other order, a v2.0.x program started while the worker has not yet
// handled the end of a 2.1.0 one, this driver can send that program's first
// frame twice where v2.0.3 does not: the variant that starts it early finds
// it, and docs/design/signal-gate.md states it.
//
// Usage: the frames argument of SignalGateModel sets each program's frames,
// except in the wrap variants, whose two frames straddle the wrap.
using System;
using System.Collections.Generic;
using System.Linq;

internal static class DeliveryModel
{
    internal sealed record Variant(string Name, string? Expect)
    {
        public bool ResetsSeen { get; init; }                 // OwnerGone and the recycle set SeenSeqNo to 0 (0d1bd858)
        public bool UsesBoundary { get; init; } = true;       // the end of a program sends the next equal SeqNo once
        public bool BoundaryChecksStarts { get; init; } = true;// only when no 2.1.0 SDK started since (SdkStarts)
        public bool BoundaryCarried { get; init; } = true;    // a new context takes the boundary from the section
        public bool StartAboveSeen { get; init; } = true;     // a 2.1.0 SDK counts from above DriverSeenSeq and the SeqNo left
        public bool AddFromSeen { get; init; } = true;        // a new context starts from DriverSeenSeq
        public bool FrameBeforeOwner { get; init; } = true;   // a poll for a waiting frame that returns the owner pipe reads the frame first
        public bool WatchAfterFrame { get; init; } = true;    // with no watch, the frame after OwnerGone retries it
        public bool SkipZero { get; init; } = true;           // a 2.1.0 SDK steps over SeqNo 0 at the wrap
        public bool LegacySecond { get; init; }               // the second program is a v2.0.x one
        public bool LegacyEarly { get; init; }                // ... started before the worker handled the end
        public bool NoWatch { get; init; }                    // no owner watch ever connects
        public bool BoundaryOnEveryNoWatchRecycle { get; init; }// ... and a recycle opens a boundary even when the program is the same
        public int Wrap { get; init; }                        // SeqNos counted modulo this, the first program near the top
        public int Programs { get; init; } = 2;
        public int? Frames { get; init; }                     // null: the frames argument
        public int Recycles { get; init; } = 1;
        public int Readds { get; init; } = 1;
    }

    internal static readonly Variant[] Variants =
    {
        new("delivery as 0d1bd858: OwnerGone and the recycle reset SeenSeqNo", "duplicate")
            { ResetsSeen = true, UsesBoundary = false, BoundaryChecksStarts = false, BoundaryCarried = false,
              StartAboveSeen = false, AddFromSeen = false, FrameBeforeOwner = false, WatchAfterFrame = false, SkipZero = false },
        new("delivery, the fix", null),
        new("delivery, the fix, a v2.0.x program second", null) { LegacySecond = true },
        new("delivery, the fix with no owner watch, a v2.0.x program second", null) { LegacySecond = true, NoWatch = true, Readds = 0 },
        new("delivery, the fix across the SeqNo wrap", null) { Programs = 1, Frames = 2, Wrap = 16 },
        new("delivery, the fix with SeqNo 0 stored at the wrap", "lost") { Programs = 1, Frames = 2, Wrap = 16, SkipZero = false, Readds = 0 },
        new("delivery, the fix with a v2.0.x program started before the end was handled", "duplicate")
            { LegacySecond = true, LegacyEarly = true, Recycles = 0, Readds = 0, Frames = 2 },
        new("delivery, the fix with SeenSeqNo reset in place of the boundary", "duplicate")
            { ResetsSeen = true, UsesBoundary = false, BoundaryChecksStarts = false, BoundaryCarried = false, Recycles = 0, Readds = 0 },
        new("delivery, the fix with a boundary that ignores SdkStarts", "duplicate") { BoundaryChecksStarts = false, Recycles = 0, Readds = 0 },
        new("delivery, the fix with no boundary, a v2.0.x program second", "lost")
            { UsesBoundary = false, BoundaryChecksStarts = false, BoundaryCarried = false, LegacySecond = true, Recycles = 0, Readds = 0 },
        new("delivery, the fix with the boundary left out of the section, a v2.0.x program second", "lost")
            { BoundaryCarried = false, LegacySecond = true, Recycles = 0 },
        new("delivery, the fix with a no-watch recycle that opens a boundary whatever it finds", "duplicate")
            { BoundaryOnEveryNoWatchRecycle = true, NoWatch = true, Programs = 1, Readds = 0 },
        new("delivery, the fix without the SDK's start above DriverSeenSeq", "lost") { StartAboveSeen = false, Recycles = 0, Readds = 0 },
        new("delivery, the fix without the add from DriverSeenSeq", "duplicate") { AddFromSeen = false, Programs = 1, Recycles = 0 },
        new("delivery, the fix with OwnerGone before the waiting frame, a v2.0.x program second", "lost")
            { FrameBeforeOwner = false, LegacySecond = true, Recycles = 0, Readds = 0 },
    };

    const int S_START = 0, S_WRITE = 1, S_CHECK = 2, S_SIGNAL = 3, S_EXIT = 4, S_NEXT = 5, S_DONE = 6;
    const int W_TOP = 0, W_LOOK = 1, W_POLL = 2, W_BLOCK = 3, W_READ = 4, W_GONE = 5, W_OWNER = 6;

    // Section words: Seq, Prog (who wrote it, for the checks only: 0 for a
    // zeroed section), Waiters, Cap, DriverSeen, Bound (the boundary in the
    // section: 0, or 1 plus its SdkStarts), SdkStarts, Event (the input
    // event), OwnerSig (the watched program's pipe closed). The program: P,
    // SdkPc, SdkSeq, Done, Running, EndSeen (the worker has handled the last
    // program's end as v2.0.3 would). The worker: Pc, Counted, Blocked, Seen
    // (SeenSeqNo), Boundary, BoundaryStarts, Watch (connected), WatchStarts,
    // OwnerAfterRead, LookRead (a read with no signal behind it).
    sealed record State(
        int Seq, int Prog, int Waiters, bool Cap, int DriverSeen, int Bound, int SdkStarts, bool Event, bool OwnerSig,
        int P, int SdkPc, int SdkSeq, int Done, bool Running, bool EndSeen,
        int Pc, bool Counted, bool Blocked, int Seen, bool Boundary, int BoundaryStarts, bool Watch, int WatchStarts,
        bool OwnerAfterRead, bool LookRead, int RecyclesLeft, int ReaddsLeft,
        string Delivered, string Trace)
    {
        public string Key => $"{Seq},{Prog},{Waiters},{Cap},{DriverSeen},{Bound},{SdkStarts},{Event},{OwnerSig}|" +
                             $"{P},{SdkPc},{SdkSeq},{Done},{Running},{EndSeen}|" +
                             $"{Pc},{Counted},{Blocked},{Seen},{Boundary},{BoundaryStarts},{Watch},{WatchStarts},{OwnerAfterRead},{LookRead},{RecyclesLeft},{ReaddsLeft}|{Delivered}";
    }

    internal sealed class Result { public int States, Finals, Frames; public string? Duplicate, Lost; }

    internal static Result Explore(Variant v, int framesArg)
    {
        int frames = v.Frames ?? framesArg;
        var start = new State(0, 0, 0, true, 0, 0, 0, false, false,
                              1, S_START, 0, 0, false, true,
                              W_TOP, false, false, 0, false, 0, false, 0,
                              false, false, v.Recycles, v.Readds, "", "");
        var seen = new HashSet<string> { start.Key };
        var stack = new Stack<State>();
        stack.Push(start);
        var r = new Result { Frames = frames };
        while (stack.Count > 0)
        {
            var s = stack.Pop();
            r.States++;
            var next = Steps(s, v, frames).ToList();
            if (next.Count == 0)
            {
                r.Finals++;
                if (s.Prog != 0 && !Sent(s.Delivered).Contains($"{s.Prog}:{s.Seq}") && r.Lost == null)
                    r.Lost = $"final state, frame {s.Prog}:{s.Seq} in the section never sent: {s.Trace}";
                continue;
            }
            foreach (var n in next)
            {
                if (r.Duplicate == null && n.Delivered != s.Delivered)
                {
                    var all = Sent(n.Delivered);
                    if (all.Length != all.Distinct().Count())
                        r.Duplicate = $"frame {all[^1]} sent twice: {n.Trace}";
                }
                if (seen.Add(n.Key)) stack.Push(n);
            }
        }
        return r;
    }

    static string[] Sent(string delivered) => delivered.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    static bool Legacy(Variant v, int p) => v.LegacySecond && p == 2;

    static IEnumerable<State> Steps(State s, Variant v, int frames)
    {
        // The programs.
        switch (s.SdkPc)
        {
            case S_START:
            {
                // EnsureInputMapping: SdkStarts before the pipe, then the
                // SeqNo left, zeroing, the start.
                bool legacy = Legacy(v, s.P);
                int startSeq = v.Wrap > 0 && s.P == 1 ? v.Wrap - 4
                             : !legacy && v.StartAboveSeen ? EvenAbove(s.Seq, s.DriverSeen) : 0;
                int starts = legacy ? s.SdkStarts : s.SdkStarts + 1;
                yield return s with { Seq = 0, Prog = 0, SdkStarts = starts, SdkSeq = startSeq, SdkPc = S_WRITE, Running = true,
                                      Trace = s.Trace + $" p{s.P}:start@{startSeq}" };
                break;
            }
            case S_WRITE:
            {
                int seq = s.SdkSeq + 2;
                if (v.Wrap > 0) seq %= v.Wrap;
                if (seq == 0 && v.SkipZero && !Legacy(v, s.P)) seq = 2;
                yield return s with { Seq = seq, Prog = s.P, SdkSeq = seq, SdkPc = S_CHECK, Trace = s.Trace + $" p{s.P}:write{seq}" };
                break;
            }
            case S_CHECK:
            {
                bool skip = !Legacy(v, s.P) && s.Cap && s.Waiters == 0;
                var n = s with { Trace = s.Trace + (skip ? $" p{s.P}:skip" : $" p{s.P}:check") };
                yield return skip ? AfterFrame(n, frames) : n with { SdkPc = S_SIGNAL };
                break;
            }
            case S_SIGNAL:
            {
                var n = s with { Trace = s.Trace + $" p{s.P}:set" };
                n = s.Pc == W_BLOCK && s.Blocked ? n with { Blocked = false, Pc = W_READ, LookRead = false, Waiters = s.Waiters - 1, Counted = false }
                                                 : n with { Event = true };
                yield return AfterFrame(n, frames);
                break;
            }
            case S_EXIT:
            {
                // The pipe closes. The worker sees it only through a watch.
                // With no device context either, nothing holds the index's
                // objects any more and they are gone.
                var n = s with { OwnerSig = s.Watch, Running = false, EndSeen = false,
                                 SdkPc = s.P < v.Programs ? S_NEXT : S_DONE, Trace = s.Trace + $" p{s.P}:exit" };
                if (s.Pc == W_GONE)
                    n = n with { Seq = 0, Prog = 0, DriverSeen = 0, Bound = 0, SdkStarts = 0, Event = false, Trace = n.Trace + "-objects-gone" };
                yield return n;
                break;
            }
            case S_NEXT:
                if (!Legacy(v, s.P + 1) || s.EndSeen || v.LegacyEarly)
                    yield return s with { P = s.P + 1, Done = 0, SdkPc = S_START, Trace = s.Trace + $" p{s.P + 1}:begins" };
                break;
        }

        // The worker.
        switch (s.Pc)
        {
            case W_TOP:
                yield return s with { Waiters = s.Waiters + 1, Counted = true, Pc = W_LOOK, Trace = s.Trace + " w:inc" };
                if (!s.Watch && s.Running && !v.NoWatch)
                    // TryOpenOwnerWatch, at a map or every 256 frames.
                    yield return s with { Watch = true, WatchStarts = s.SdkStarts, Trace = s.Trace + " w:watch" };
                if (s.ReaddsLeft > 0)
                {
                    // The device goes. With no program running nothing holds
                    // the index's objects after its context lets go, so the
                    // input event, the sections and their words are gone, and
                    // the next program makes new ones.
                    var n = s with { Pc = W_GONE, Cap = false, Watch = false, OwnerSig = false, ReaddsLeft = s.ReaddsLeft - 1,
                                     Trace = s.Trace + " w:device-goes" };
                    if (!s.Running)
                        n = n with { Seq = 0, Prog = 0, DriverSeen = 0, Bound = 0, SdkStarts = 0, Event = false, Trace = n.Trace + "-objects-gone" };
                    yield return n;
                }
                break;
            case W_GONE:
            {
                // EvtDeviceAdd zeroes the context, which v2.0.3 took for the
                // end of any program before. The first map of the signal
                // section takes DriverSeenSeq and the boundary and sets the
                // capability, and connects the owner watch to a running
                // program.
                int from = v.AddFromSeen ? s.DriverSeen : 0;
                bool bound = v.BoundaryCarried && s.Bound != 0;
                yield return s with { Pc = W_TOP, Seen = from, Boundary = bound, BoundaryStarts = bound ? s.Bound - 1 : 0, Cap = true,
                                      Watch = s.Running && !v.NoWatch, WatchStarts = s.SdkStarts, EndSeen = true,
                                      Trace = s.Trace + $" w:added@{from}" + (bound ? "-boundary" : "") };
                break;
            }
            case W_LOOK:
            {
                bool pending = s.Seq != 0 && s.Seq != s.Seen;
                yield return pending
                    ? s with { Waiters = s.Waiters - 1, Counted = false, Pc = W_POLL, Trace = s.Trace + $" w:look={s.Seq}-pending" }
                    : s with { Pc = W_BLOCK, Trace = s.Trace + $" w:look={s.Seq}" };
                break;
            }
            case W_POLL:
                if (s.Event) yield return s with { Event = false, Pc = W_READ, LookRead = false, Trace = s.Trace + " w:poll-takes-event" };
                else if (s.OwnerSig && v.FrameBeforeOwner)
                    yield return s with { OwnerSig = false, OwnerAfterRead = true, Pc = W_READ, LookRead = true, Trace = s.Trace + " w:poll-owner-frame-first" };
                else if (s.OwnerSig) yield return s with { OwnerSig = false, Pc = W_OWNER, Trace = s.Trace + " w:poll-owner" };
                else yield return s with { Pc = W_READ, LookRead = true, Trace = s.Trace + " w:poll" };
                break;
            case W_BLOCK:
                if (!s.Blocked)
                {
                    if (s.Event) yield return s with { Event = false, Waiters = s.Waiters - 1, Counted = false, Pc = W_READ, LookRead = false, Trace = s.Trace + " w:wait-takes-event" };
                    else if (s.OwnerSig) yield return s with { OwnerSig = false, Waiters = s.Waiters - 1, Counted = false, Pc = W_OWNER, Trace = s.Trace + " w:wait-owner" };
                    else yield return s with { Blocked = true, Trace = s.Trace + " w:blocks" };
                }
                else
                {
                    if (s.OwnerSig) yield return s with { OwnerSig = false, Blocked = false, Waiters = s.Waiters - 1, Counted = false, Pc = W_OWNER, Trace = s.Trace + " w:wakes-owner" };
                    if (s.RecyclesLeft > 0)
                        yield return Recycle(s with { Blocked = false, Waiters = s.Waiters - 1, Counted = false, Trace = s.Trace + " w:timeout-recycle" }, v);
                }
                break;
            case W_OWNER:
            {
                // OwnerGone: the watch closes.
                var n = s with { Watch = false, Pc = W_TOP, EndSeen = !s.Running || s.EndSeen, Trace = s.Trace + " w:owner-gone" };
                if (v.ResetsSeen) n = n with { Seen = 0 };
                else if (!v.BoundaryChecksStarts || s.SdkStarts == s.WatchStarts) n = SetBoundary(n, v);
                yield return n;
                break;
            }
            case W_READ:
            {
                // ProcessSharedInput. A 0 the look found is a zeroed section, and
                // a signaled 0 goes on as v2.0.3 took it.
                var n = s with { Pc = s.OwnerAfterRead ? W_OWNER : W_TOP, OwnerAfterRead = false, Trace = s.Trace + $" w:read={s.Prog}:{s.Seq}" };
                if (s.Seq == 0 && s.LookRead) { yield return n; break; }
                bool boundarySends = s.Boundary && (!v.BoundaryChecksStarts || s.SdkStarts == s.BoundaryStarts);
                bool fresh = s.Seq != s.Seen || boundarySends;
                n = n with { DriverSeen = s.Seq, Seen = s.Seq, Boundary = false, Bound = s.Boundary ? 0 : s.Bound };
                if (fresh) n = n with { Delivered = s.Delivered + $" {s.Prog}:{s.Seq}", Trace = n.Trace + "-sent" };
                // With no watch, the retry after the frame connects one to
                // the program whose frame it is, which made its pipe first.
                if (!n.Watch && n.Running && s.Prog == s.P && v.WatchAfterFrame && !v.NoWatch)
                    n = n with { Watch = true, WatchStarts = n.SdkStarts, Trace = n.Trace + " w:watch" };
                yield return n;
                // A stale wakeup can end in a recycle (250 of them in a row).
                if (!fresh && s.RecyclesLeft > 0 && n.Pc == W_TOP)
                    yield return Recycle(n with { Trace = n.Trace + " w:stale-recycle" }, v);
                break;
            }
        }
    }

    // A recycle reads nothing and closes the worker's input event and input
    // section. With a program running they stay, its map connects the owner
    // watch to it, and nothing else changes. With none they are gone, and the
    // worker waits in Phase 1 for the next program to make them: with no
    // watch connected the program may have changed unseen, and a boundary
    // opens. The signal section stays mapped.
    static State Recycle(State n, Variant v)
    {
        n = n with { Pc = W_TOP, RecyclesLeft = n.RecyclesLeft - 1 };
        if (v.ResetsSeen) return n with { Seen = 0, EndSeen = n.EndSeen || !n.Running };
        if (n.Running)
        {
            if (!n.Watch && !v.NoWatch) return n with { Watch = true, WatchStarts = n.SdkStarts, Trace = n.Trace + " w:watch" };
            if (!n.Watch && v.BoundaryOnEveryNoWatchRecycle) return SetBoundary(n, v);
            return n;
        }
        n = n with { Seq = 0, Prog = 0, Event = false, Trace = n.Trace + "-objects-gone" };
        if (!n.Watch) return SetBoundary(n, v) with { EndSeen = true };
        return n;
    }

    static State SetBoundary(State s, Variant v) =>
        v.UsesBoundary ? s with { Boundary = true, BoundaryStarts = s.SdkStarts, Bound = s.SdkStarts + 1 } : s;

    static State AfterFrame(State s, int frames)
    {
        int done = s.Done + 1;
        return s with { Done = done, SdkPc = done == frames ? S_EXIT : S_WRITE };
    }

    static int EvenAbove(int a, int b) => (Math.Max(a, b) + 1) & ~1;
}
