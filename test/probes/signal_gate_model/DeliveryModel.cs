// The input gate's delivery rules (docs/design/signal-gate.md, "Which frame
// is new"): every interleaving of programs that start one after another on
// an index and the HID driver's worker, with every step that used to forget
// the last SeqNo. Memory is sequentially consistent here: Program.cs checks
// the fences. This part checks what the worker takes for a new frame.
//
// A 2.1.0 program counts itself in SdkStarts, zeroes the input section,
// counts its frames from above the SeqNo it found and DriverSeenSeq, writes
// each frame, and skips the SetEvent when the capability is set and no
// worker is counted. A v2.0.x program counts from 0 and signals every
// frame. The check and the SetEvent are two steps, so a worker can read the
// frame between them and the event is left set for a frame already read. A
// program's end closes its owner pipe after its last SetEvent, which the
// worker sees only when its owner watch is connected to that program. The
// worker runs SharedInputWorkerProc's loop: count itself, look at the
// section, poll or wait, read. Its wait returns the input event before the
// owner pipe, as WaitForMultipleObjects returns the lowest index signaled.
// It connects its owner watch to a running program, can time out into a
// recycle, and its device can go and come back (a re-add): a new context
// that maps the signal section again.
//
// Two checks. No frame (program, SeqNo) goes to HID class twice. In a final
// state the frame the section holds has gone to HID class. Each variant
// either passes both or is expected to fail one, and failing it shows the
// check finds that defect.
//
// A v2.0.x program's first frames can carry the SeqNo of the last frame
// read. v2.0.3 sends those only once it has handled the last program's end
// (OwnerGone, or a recycle with no owner watch), so the model starts a
// v2.0.x program after that.
using System;
using System.Collections.Generic;
using System.Linq;

internal static class DeliveryModel
{
    internal sealed record Variant(
        string Name,
        bool ResetsSeen,          // OwnerGone and the recycle set SeenSeqNo to 0 (0d1bd858)
        bool UsesBoundary,        // the end of a program sends the next equal SeqNo once
        bool BoundaryChecksStarts,// only when no 2.1.0 SDK started since (SdkStarts)
        bool BoundaryCarried,     // a new context takes the boundary from the section
        bool StartAboveSeen,      // a 2.1.0 SDK counts from above DriverSeenSeq and the SeqNo left
        bool AddFromSeen,         // a new context starts from DriverSeenSeq
        bool FrameBeforeOwner,    // a poll for a waiting frame that returns the owner pipe reads the frame first
        bool LegacySecond,        // the second program is a v2.0.x one
        bool WatchAfterFrame,     // with no watch, the frame after OwnerGone retries it
        int Programs, int Frames, int Recycles, int Readds,
        string? Expect);          // null, "duplicate" or "lost"

    internal static readonly Variant[] Variants =
    {
        new("delivery as 0d1bd858: OwnerGone and the recycle reset SeenSeqNo", true, false, false, false, false, false, false, false, false, 2, 2, 1, 1, "duplicate"),
        new("delivery, the fix", false, true, true, true, true, true, true, false, true, 2, 2, 1, 1, null),
        new("delivery, the fix, a v2.0.x program second", false, true, true, true, true, true, true, true, true, 2, 2, 1, 1, null),
        new("delivery, the fix with SeenSeqNo reset in place of the boundary", true, false, false, false, true, true, true, false, true, 2, 2, 0, 0, "duplicate"),
        new("delivery, the fix with a boundary that ignores SdkStarts", false, true, false, true, true, true, true, false, true, 2, 2, 0, 0, "duplicate"),
        new("delivery, the fix with no boundary, a v2.0.x program second", false, false, false, false, true, true, true, true, true, 2, 2, 0, 0, "lost"),
        new("delivery, the fix with the boundary left out of the section, a v2.0.x program second", false, true, true, false, true, true, true, true, true, 2, 2, 0, 1, "lost"),
        new("delivery, the fix without the SDK's start above DriverSeenSeq", false, true, true, true, false, true, true, false, true, 2, 2, 0, 0, "lost"),
        new("delivery, the fix without the add from DriverSeenSeq", false, true, true, true, true, false, true, false, true, 1, 2, 0, 1, "duplicate"),
        new("delivery, the fix with OwnerGone before the waiting frame, a v2.0.x program second", false, true, true, true, true, true, false, true, true, 2, 2, 0, 0, "lost"),
    };

    const int S_START = 0, S_WRITE = 1, S_CHECK = 2, S_SIGNAL = 3, S_EXIT = 4, S_NEXT = 5, S_DONE = 6;
    const int W_TOP = 0, W_LOOK = 1, W_POLL = 2, W_BLOCK = 3, W_READ = 4, W_GONE = 5, W_OWNER = 6;

    // Section words: Seq, Prog (who wrote it, for the checks only), Waiters,
    // Cap, DriverSeen, Bound (the boundary in the section: 0, or 1 plus its
    // SdkStarts), SdkStarts, Event (the input event), OwnerSig (the watched
    // program's pipe closed). The program: P, SdkPc, SdkSeq, Done, Running,
    // EndSeen (the worker has handled the last program's end). The worker:
    // Pc, Counted, Blocked, Seen (SeenSeqNo), Boundary, BoundaryStarts,
    // Watch (connected), WatchStarts, OwnerAfterRead.
    sealed record State(
        int Seq, int Prog, int Waiters, bool Cap, int DriverSeen, int Bound, int SdkStarts, bool Event, bool OwnerSig,
        int P, int SdkPc, int SdkSeq, int Done, bool Running, bool EndSeen,
        int Pc, bool Counted, bool Blocked, int Seen, bool Boundary, int BoundaryStarts, bool Watch, int WatchStarts,
        bool OwnerAfterRead, int RecyclesLeft, int ReaddsLeft,
        string Delivered, string Trace)
    {
        public string Key => $"{Seq},{Prog},{Waiters},{Cap},{DriverSeen},{Bound},{SdkStarts},{Event},{OwnerSig}|" +
                             $"{P},{SdkPc},{SdkSeq},{Done},{Running},{EndSeen}|" +
                             $"{Pc},{Counted},{Blocked},{Seen},{Boundary},{BoundaryStarts},{Watch},{WatchStarts},{OwnerAfterRead},{RecyclesLeft},{ReaddsLeft}|{Delivered}";
    }

    internal sealed class Result { public int States, Finals; public string? Duplicate, Lost; }

    internal static Result Explore(Variant v)
    {
        var start = new State(0, 0, 0, true, 0, 0, 0, false, false,
                              1, S_START, 0, 0, false, true,
                              W_TOP, false, false, 0, false, 0, false, 0,
                              false, v.Recycles, v.Readds, "", "");
        var seen = new HashSet<string> { start.Key };
        var stack = new Stack<State>();
        stack.Push(start);
        var r = new Result();
        while (stack.Count > 0)
        {
            var s = stack.Pop();
            r.States++;
            var next = Steps(s, v).ToList();
            if (next.Count == 0)
            {
                r.Finals++;
                if (s.Seq != 0 && !Sent(s.Delivered).Contains($"{s.Prog}:{s.Seq}") && r.Lost == null)
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

    static IEnumerable<State> Steps(State s, Variant v)
    {
        // The programs.
        switch (s.SdkPc)
        {
            case S_START:
            {
                bool legacy = Legacy(v, s.P);
                int startSeq = !legacy && v.StartAboveSeen ? EvenAbove(s.Seq, s.DriverSeen) : 0;
                int starts = legacy ? s.SdkStarts : s.SdkStarts + 1;
                yield return s with { Seq = 0, Prog = 0, SdkStarts = starts, SdkSeq = startSeq, SdkPc = S_WRITE, Running = true,
                                      Trace = s.Trace + $" p{s.P}:start@{startSeq}" };
                break;
            }
            case S_WRITE:
            {
                int seq = s.SdkSeq + 2;
                yield return s with { Seq = seq, Prog = s.P, SdkSeq = seq, SdkPc = S_CHECK, Trace = s.Trace + $" p{s.P}:write{seq}" };
                break;
            }
            case S_CHECK:
            {
                bool skip = !Legacy(v, s.P) && s.Cap && s.Waiters == 0;
                var n = s with { Trace = s.Trace + (skip ? $" p{s.P}:skip" : $" p{s.P}:check") };
                yield return skip ? AfterFrame(n, v) : n with { SdkPc = S_SIGNAL };
                break;
            }
            case S_SIGNAL:
            {
                var n = s with { Trace = s.Trace + $" p{s.P}:set" };
                n = s.Pc == W_BLOCK && s.Blocked ? n with { Blocked = false, Pc = W_READ, Waiters = s.Waiters - 1, Counted = false }
                                                 : n with { Event = true };
                yield return AfterFrame(n, v);
                break;
            }
            case S_EXIT:
                // The pipe closes. The worker sees it only through a watch.
                yield return s with { OwnerSig = s.Watch, Running = false, EndSeen = false,
                                      SdkPc = s.P < v.Programs ? S_NEXT : S_DONE, Trace = s.Trace + $" p{s.P}:exit" };
                break;
            case S_NEXT:
                if (!Legacy(v, s.P + 1) || s.EndSeen)
                    yield return s with { P = s.P + 1, Done = 0, SdkPc = S_START, Trace = s.Trace + $" p{s.P + 1}:begins" };
                break;
        }

        // The worker.
        switch (s.Pc)
        {
            case W_TOP:
                yield return s with { Waiters = s.Waiters + 1, Counted = true, Pc = W_LOOK, Trace = s.Trace + " w:inc" };
                if (!s.Watch && s.Running)
                    // TryOpenOwnerWatch, at a map or every 256 frames.
                    yield return s with { Watch = true, WatchStarts = s.SdkStarts, Trace = s.Trace + " w:watch" };
                if (s.ReaddsLeft > 0)
                    yield return s with { Pc = W_GONE, Cap = false, Watch = false, OwnerSig = false, ReaddsLeft = s.ReaddsLeft - 1,
                                          Trace = s.Trace + " w:device-goes" };
                break;
            case W_GONE:
            {
                // EvtDeviceAdd zeroes the context. The first map of the
                // signal section takes DriverSeenSeq and the boundary and sets
                // the capability.
                int from = v.AddFromSeen ? s.DriverSeen : 0;
                bool bound = v.BoundaryCarried && s.Bound != 0;
                // The same map connects the owner watch to a running program.
                yield return s with { Pc = W_TOP, Seen = from, Boundary = bound, BoundaryStarts = bound ? s.Bound - 1 : 0, Cap = true,
                                      Watch = s.Running, WatchStarts = s.SdkStarts,
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
                if (s.Event) yield return s with { Event = false, Pc = W_READ, Trace = s.Trace + " w:poll-takes-event" };
                else if (s.OwnerSig && v.FrameBeforeOwner)
                    yield return s with { OwnerSig = false, OwnerAfterRead = true, Pc = W_READ, Trace = s.Trace + " w:poll-owner-frame-first" };
                else if (s.OwnerSig) yield return s with { OwnerSig = false, Pc = W_OWNER, Trace = s.Trace + " w:poll-owner" };
                else yield return s with { Pc = W_READ, Trace = s.Trace + " w:poll" };
                break;
            case W_BLOCK:
                if (!s.Blocked)
                {
                    if (s.Event) yield return s with { Event = false, Waiters = s.Waiters - 1, Counted = false, Pc = W_READ, Trace = s.Trace + " w:wait-takes-event" };
                    else if (s.OwnerSig) yield return s with { OwnerSig = false, Waiters = s.Waiters - 1, Counted = false, Pc = W_OWNER, Trace = s.Trace + " w:wait-owner" };
                    else yield return s with { Blocked = true, Trace = s.Trace + " w:blocks" };
                }
                else
                {
                    if (s.OwnerSig) yield return s with { OwnerSig = false, Blocked = false, Waiters = s.Waiters - 1, Counted = false, Pc = W_OWNER, Trace = s.Trace + " w:wakes-owner" };
                    if (s.RecyclesLeft > 0)
                    {
                        // The idle timeouts end in a recycle, which reads
                        // nothing. Its map connects the owner watch to a
                        // running program. The driver forgets the last frame
                        // only when that finds no pipe, which with a 2.0
                        // or later SDK the model never meets.
                        var n = s with { Blocked = false, Waiters = s.Waiters - 1, Counted = false, Pc = W_TOP,
                                         RecyclesLeft = s.RecyclesLeft - 1, Trace = s.Trace + " w:timeout-recycle" };
                        if (v.ResetsSeen) n = n with { Seen = 0, EndSeen = n.EndSeen || !n.Running };
                        else if (!s.Watch && s.Running) n = n with { Watch = true, WatchStarts = s.SdkStarts, Trace = n.Trace + " w:watch" };
                        yield return n;
                    }
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
                // ProcessSharedInput.
                var n = s with { Pc = s.OwnerAfterRead ? W_OWNER : W_TOP, OwnerAfterRead = false, Trace = s.Trace + $" w:read={s.Prog}:{s.Seq}" };
                if (s.Seq == 0) { yield return n; break; }
                bool boundarySends = s.Boundary && (!v.BoundaryChecksStarts || s.SdkStarts == s.BoundaryStarts);
                bool fresh = s.Seq != s.Seen || boundarySends;
                n = n with { DriverSeen = s.Seq, Seen = s.Seq, Boundary = false, Bound = 0 };
                if (fresh) n = n with { Delivered = s.Delivered + $" {s.Prog}:{s.Seq}", Trace = n.Trace + "-sent" };
                // With no watch, the retry after the frame connects one to
                // the program whose frame it is, which made its pipe first.
                if (!n.Watch && n.Running && s.Prog == s.P && v.WatchAfterFrame)
                    n = n with { Watch = true, WatchStarts = n.SdkStarts, Trace = n.Trace + " w:watch" };
                yield return n;
                break;
            }
        }
    }

    static State SetBoundary(State s, Variant v) =>
        v.UsesBoundary ? s with { Boundary = true, BoundaryStarts = s.SdkStarts, Bound = s.SdkStarts + 1 } : s;

    static State AfterFrame(State s, Variant v)
    {
        int done = s.Done + 1;
        return s with { Done = done, SdkPc = done == v.Frames ? S_EXIT : S_WRITE };
    }

    static int EvenAbove(int a, int b) => (Math.Max(a, b) + 1) & ~1;
}
