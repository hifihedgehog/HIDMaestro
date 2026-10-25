// The input gate's delivery rules (docs/design/signal-gate.md, "Which frame
// is new"), checked against v2.0.3's. Memory is sequentially consistent
// here: Program.cs checks the fences. This part checks what the worker
// takes for a new frame.
//
// Each scenario is a sequence of programs on one index and the steps the
// machine may take around them. A program is a 2.1.0 one or a v2.0.x one,
// with an owner pipe the driver can open or without. A 2.1.0 program counts
// its frames from above the SeqNo it found and DriverSeenSeq, steps over 0
// at the wrap, and skips the SetEvent when the capability is set and no
// worker is counted. A v2.0.x program counts from 0, stores 0 at its wrap
// and signals every frame. The check and the SetEvent are two steps, so a
// worker can read the frame between them and the event is left set for a
// frame already read. A program's end closes its pipe after its last
// SetEvent. The next program starts either once the worker has handled
// that end, or at any time after it ("early", the idle window). Around
// them the worker recycles after idle timeouts or after a stale wakeup, its
// device goes and is added again, a 2.1.0 create fails at the owner pipe
// while a program holds the index, and the objects nothing holds go away.
//
// The same scenario runs under two drivers. v2.0.3's worker reads only on
// a signal, never announces the capability, and forgets the last SeqNo at
// the end of a program, at a recycle and at a device's add. This driver's
// worker gates only while its owner watch is connected to the program on
// the index and the signal section exists: it announces the capability
// then, counts itself and looks before each wait, and keeps the last SeqNo
// across a recycle, since the program cannot change unseen. When the watch
// closes (OwnerGone) it takes the capability back, forgets the last SeqNo as
// v2.0.3 does, and looks once for a frame the SDK skipped signaling. A
// device that goes while it gates leaves the next one its last SeqNo while
// the program runs on, and a look. Without the gate it is v2.0.3's worker.
//
// Each run records the frames (program, frame index) that reach HID class
// twice, and the frame the section holds in a final state when it never
// reached HID class. The check: in every scenario this driver loses no frame
// v2.0.3 does not lose, and sends none twice that v2.0.3 does not send
// twice.
//
// Usage: the frames argument of SignalGateModel sets each program's frames
// (the wrap scenarios write two that straddle it).
using System;
using System.Collections.Generic;
using System.Linq;

internal static class DeliveryModel
{
    internal enum Driver { V203, This }

    // A program: a 2.1.0 SDK or a v2.0.x one, and whether the driver can
    // open its owner pipe.
    internal sealed record Prog(bool Is21, bool Pipe)
    {
        public override string ToString() => (Is21 ? "2.1.0" : "v2.0.x") + (Pipe ? "" : " pipe-less");
    }

    internal sealed record Scenario(string Name, Prog[] Programs)
    {
        public bool Early { get; init; }          // the next program starts any time after the last ends
        public int Recycles { get; init; }
        public int Readds { get; init; }
        public bool FailedCreate { get; init; }   // a 2.1.0 create fails at the owner pipe while a program runs
        public int Wrap { get; init; }            // SeqNos modulo this, the first program near the top
        public int? Frames { get; init; }
    }

    // The rules under test, each taken away in turn: a variant must fail.
    internal sealed record Rules(string Name)
    {
        public bool GateOnlyWatched { get; init; } = true;   // announce and look only while the watch is connected
        public bool ResetAtOwnerGone { get; init; } = true;  // OwnerGone forgets the last SeqNo
        public bool LookAfterOwnerGone { get; init; } = true;// OwnerGone looks once after taking the capability back
        public bool LookAgainstGateSeq { get; init; } = true;// the look compares with the last frame read, never reset
        public bool StartAboveSeen { get; init; } = true;    // a 2.1.0 SDK counts from above DriverSeenSeq
        public bool AddFromSeen { get; init; } = true;       // a context gated at add starts from DriverSeenSeq
        public bool SkipZero { get; init; } = true;          // a 2.1.0 SDK steps over 0 at the wrap
        public bool FrameBeforeOwner { get; init; } = true;  // a poll that finds the pipe closed reads a waiting frame first
        public bool CarrySeen { get; init; } = true;         // a device that goes carries SeenSeqNo while its program runs watched
        public bool LookAtAdd { get; init; } = true;         // the first map looks once
    }

    internal static readonly Rules This = new("this driver");

    internal static readonly Rules[] Broken =
    {
        This with { Name = "gating with no watch connected", GateOnlyWatched = false },
        This with { Name = "no forgetting at OwnerGone", ResetAtOwnerGone = false },
        This with { Name = "no look after OwnerGone", LookAfterOwnerGone = false },
        This with { Name = "the look against SeenSeqNo", LookAgainstGateSeq = false },
        This with { Name = "no start above DriverSeenSeq", StartAboveSeen = false },
        This with { Name = "no add from DriverSeenSeq", AddFromSeen = false },
        This with { Name = "SeqNo 0 stored at the wrap", SkipZero = false },
        This with { Name = "OwnerGone before the waiting frame", FrameBeforeOwner = false },
        This with { Name = "no SeenSeqNo carried to the next device", CarrySeen = false },
        This with { Name = "no look at add", LookAtAdd = false },
    };

    // Every combination of two programs, early or not, with and without a
    // recycle and a re-add. A failed create, three programs in a row, and the
    // wrap for each SDK.
    internal static IEnumerable<Scenario> Scenarios()
    {
        var kinds = new[] { new Prog(true, true), new Prog(false, true), new Prog(true, false), new Prog(false, false) };
        foreach (var a in kinds)
            foreach (var b in kinds)
                foreach (bool early in new[] { false, true })
                    foreach (var (rc, ra) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
                        yield return new Scenario($"{a} then {b}{(early ? ", early" : "")}{(rc > 0 ? ", recycle" : "")}{(ra > 0 ? ", re-add" : "")}", new[] { a, b })
                            { Early = early, Recycles = rc, Readds = ra };
        foreach (var a in kinds.Where(k => k.Pipe))
            foreach (bool early in new[] { false, true })
                yield return new Scenario($"{a} then v2.0.x, a failed create{(early ? ", early" : "")}", new[] { a, new Prog(false, true) })
                    { Early = early, FailedCreate = true, Recycles = 1 };
        foreach (var seq in new[] { new[] { true, true, false }, new[] { false, true, false }, new[] { true, false, true } })
            foreach (bool early in new[] { false, true })
                yield return new Scenario(string.Join(" then ", seq.Select(g => g ? "2.1.0" : "v2.0.x")) + (early ? ", early" : ""),
                                          seq.Select(g => new Prog(g, true)).ToArray()) { Early = early, Frames = 2 };
        foreach (var k in kinds)
            yield return new Scenario($"{k} across the wrap", new[] { k }) { Wrap = 16, Frames = 2, Recycles = 1, Readds = 1 };
    }

    const int S_START = 0, S_WRITE = 1, S_CHECK = 2, S_SIGNAL = 3, S_EXIT = 4, S_NEXT = 5, S_DONE = 6, S_CLAIM = 7;
    const int W_TOP = 0, W_LOOK = 1, W_POLL = 2, W_BLOCK = 3, W_READ = 4, W_GONE = 5, W_OWNER = 6;

    // Section: Seq, Prog (who wrote it, for the checks only: 0 for a zeroed
    // section), Waiters, Cap, DriverSeen, Event (the input event), OwnerSig
    // (the watched program's pipe closed). Programs: P, SdkPc, SdkSeq, Done,
    // Running, EndSeen (the worker has handled the last program's end),
    // Failed (the failed create has happened). Worker: Pc, Counted, Blocked,
    // Seen (SeenSeqNo), Gate (GateSeq, the last frame read), Watch,
    // OwnerAfterRead, LookRead (a read with no signal behind it).
    sealed record State(
        int Seq, int Prog, int Idx, bool Section, int Carry, int Waiters, bool Cap, int DriverSeen, bool Event, bool OwnerSig,
        int P, int SdkPc, int SdkSeq, int Done, bool Running, bool EndSeen, bool Failed,
        int Pc, bool Counted, bool Blocked, int Seen, int Gate, bool Watch,
        bool OwnerAfterRead, bool LookRead, int RecyclesLeft, int ReaddsLeft,
        string Delivered, string Trace)
    {
        public string Key => $"{Seq},{Prog},{Idx},{Section},{Carry},{Waiters},{Cap},{DriverSeen},{Event},{OwnerSig}|{P},{SdkPc},{SdkSeq},{Done},{Running},{EndSeen},{Failed}|" +
                             $"{Pc},{Counted},{Blocked},{Seen},{Gate},{Watch},{OwnerAfterRead},{LookRead},{RecyclesLeft},{ReaddsLeft}|{Delivered}";
    }

    internal sealed class Result
    {
        public int States, Finals;
        public readonly SortedSet<string> Lost = new(), Duplicate = new();
        public readonly Dictionary<string, string> Traces = new();
    }

    internal static Result Explore(Scenario sc, Driver d, Rules r, int framesArg)
    {
        int frames = sc.Frames ?? framesArg;
        var start = new State(0, 0, 0, false, 0, 0, false, 0, false, false,
                              1, S_CLAIM, 0, 0, false, true, false,
                              W_TOP, false, false, 0, 0, false,
                              false, false, sc.Recycles, sc.Readds, "", "");
        var seen = new HashSet<string> { start.Key };
        var stack = new Stack<State>();
        stack.Push(start);
        var res = new Result();
        while (stack.Count > 0)
        {
            var s = stack.Pop();
            res.States++;
            var next = Steps(s, sc, d, r, frames).ToList();
            if (next.Count == 0)
            {
                res.Finals++;
                if (s.Prog != 0 && !Sent(s.Delivered).Contains($"{s.Prog}:{s.Idx}"))
                {
                    string id = $"{s.Prog}:{s.Idx}";
                    if (res.Lost.Add(id)) res.Traces["lost " + id] = s.Trace;
                }
                continue;
            }
            foreach (var n in next)
            {
                if (n.Delivered != s.Delivered)
                {
                    var all = Sent(n.Delivered);
                    string last = all[^1];
                    if (!last.StartsWith("0:") && all.Count(x => x == last) > 1 && res.Duplicate.Add(last))
                        res.Traces["duplicate " + last] = n.Trace;
                }
                if (seen.Add(n.Key)) stack.Push(n);
            }
        }
        return res;
    }

    static string[] Sent(string delivered) => delivered.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    static IEnumerable<State> Steps(State s, Scenario sc, Driver d, Rules r, int frames)
    {
        bool gating = d == Driver.This;
        var prog = s.P <= sc.Programs.Length ? sc.Programs[s.P - 1] : null;

        // The programs.
        switch (s.SdkPc)
        {
            case S_CLAIM:
                // EnsureInputMapping makes the owner pipe first: a watch can
                // connect from here.
                yield return s with { Running = true, SdkPc = S_START, Trace = s.Trace + $" p{s.P}:pipe" };
                break;
            case S_START:
            {
                // Then the SeqNo left, zeroing, the signal section, the start.
                int startSeq = sc.Wrap > 0 ? sc.Wrap - 4
                             : prog!.Is21 && r.StartAboveSeen ? EvenAbove(s.Seq, gating && s.Section ? s.DriverSeen : 0) : 0;
                // A 2.1.0 SDK makes the signal section after it zeroed the input
                // section. A worker that maps a new one takes its DriverSeenSeq,
                // 0, for the look.
                bool fresh = !s.Section && prog.Is21;
                yield return s with { Seq = 0, Prog = 0, Idx = 0, Section = s.Section || prog.Is21, Gate = fresh && gating ? 0 : s.Gate,
                                      SdkSeq = startSeq, SdkPc = S_WRITE,
                                      Trace = s.Trace + $" p{s.P}:start@{startSeq}" };
                break;
            }
            case S_WRITE:
            {
                int seq = s.SdkSeq + 2;
                if (sc.Wrap > 0) seq %= sc.Wrap;
                if (seq == 0 && prog!.Is21 && r.SkipZero) seq = 2;
                yield return s with { Seq = seq, Prog = s.P, Idx = s.Done + 1, SdkSeq = seq, SdkPc = S_CHECK, Trace = s.Trace + $" p{s.P}:write{seq}" };
                break;
            }
            case S_CHECK:
            {
                bool skip = prog!.Is21 && s.Cap && s.Waiters == 0;
                var n = s with { Trace = s.Trace + (skip ? $" p{s.P}:skip" : $" p{s.P}:check") };
                yield return skip ? AfterFrame(n, frames) : n with { SdkPc = S_SIGNAL };
                break;
            }
            case S_SIGNAL:
            {
                var n = s with { Trace = s.Trace + $" p{s.P}:set" };
                n = s.Pc == W_BLOCK && s.Blocked
                    ? n with { Blocked = false, Pc = W_READ, LookRead = false, Waiters = s.Counted ? s.Waiters - 1 : s.Waiters, Counted = false }
                    : n with { Event = true };
                yield return AfterFrame(n, frames);
                break;
            }
            case S_EXIT:
            {
                // The pipe closes, and the worker sees it through a watch. With no
                // device context either, nothing holds the objects any more.
                var n = s with { OwnerSig = s.Watch, Running = false, EndSeen = false,
                                 SdkPc = s.P < sc.Programs.Length ? S_NEXT : S_DONE, Trace = s.Trace + $" p{s.P}:exit" };
                if (s.Pc == W_GONE) n = Gone(n);
                yield return n;
                break;
            }
            case S_NEXT:
                if (sc.Early || s.EndSeen)
                    yield return s with { P = s.P + 1, Done = 0, SdkPc = S_CLAIM, Trace = s.Trace + $" p{s.P + 1}:begins" };
                break;
        }

        // A 2.1.0 create on the index that fails at the owner pipe while a
        // program holds it: it makes the signal section's words no different.
        if (sc.FailedCreate && !s.Failed && s.Running)
            yield return s with { Failed = true, Trace = s.Trace + " x:failed-create" };

        // The worker.
        bool gateNow = gating && s.Section && (!r.GateOnlyWatched || s.Watch);
        switch (s.Pc)
        {
            case W_TOP:
                yield return gateNow
                    ? s with { Waiters = s.Waiters + 1, Counted = true, Pc = W_LOOK, Trace = s.Trace + " w:inc" }
                    : s with { Pc = W_BLOCK, Trace = s.Trace + " w:wait" };
                if (!s.Watch && s.Running && prog!.Pipe)
                    // TryOpenOwnerWatch, at a map or after a frame.
                    yield return Connect(s, d, r);
                if (s.ReaddsLeft > 0)
                {
                    // ReleaseIndexObjects: SeenSeqNo goes in the section while its
                    // watch is connected to a program still running, else 0.
                    var n = s with { Pc = W_GONE, Cap = false, Watch = false, OwnerSig = false, ReaddsLeft = s.ReaddsLeft - 1,
                                     Carry = s.Watch && s.Section ? (!s.OwnerSig && s.Running && r.CarrySeen ? s.Seen : 0) + 1 : 0,
                                     Trace = s.Trace + " w:device-goes" };
                    if (!s.Running) n = Gone(n);
                    yield return n;
                }
                break;
            case W_GONE:
            {
                // EvtDeviceAdd. The first map takes DriverSeenSeq for the look
                // and the carried SeenSeqNo, connects the owner watch to a
                // running program, and looks once for a frame the SDK skipped
                // signaling before the last context took its capability back.
                bool mapped = gating && s.Section;
                var n = s with { Pc = W_TOP, Seen = mapped && s.Carry != 0 ? s.Carry - 1 : 0, Gate = mapped && r.AddFromSeen ? s.DriverSeen : 0, EndSeen = true,
                                 Trace = s.Trace + " w:added" };
                if (s.Running && prog!.Pipe) n = Connect(n, d, r);
                if (mapped && r.LookAtAdd && s.Carry != 0 && n.Seq != 0 && n.Seq != n.Gate)
                    n = Read(n with { Trace = n.Trace + " w:look-at-add" }, d, look: true, r.LookAgainstGateSeq) with { Pc = W_TOP };
                yield return n;
                break;
            }
            case W_LOOK:
            {
                int against = r.LookAgainstGateSeq ? s.Gate : s.Seen;
                bool pending = s.Seq != 0 && s.Seq != against;
                yield return pending
                    ? s with { Waiters = s.Waiters - 1, Counted = false, Pc = W_POLL, Trace = s.Trace + $" w:look={s.Seq}-pending" }
                    : s with { Pc = W_BLOCK, Trace = s.Trace + $" w:look={s.Seq}" };
                break;
            }
            case W_POLL:
                if (s.Event) yield return s with { Event = false, Pc = W_READ, LookRead = false, Trace = s.Trace + " w:poll-takes-event" };
                else if (s.OwnerSig && r.FrameBeforeOwner)
                    yield return s with { OwnerSig = false, OwnerAfterRead = true, Pc = W_READ, LookRead = true, Trace = s.Trace + " w:poll-owner-frame-first" };
                else if (s.OwnerSig) yield return s with { OwnerSig = false, Pc = W_OWNER, Trace = s.Trace + " w:poll-owner" };
                else yield return s with { Pc = W_READ, LookRead = true, Trace = s.Trace + " w:poll" };
                break;
            case W_BLOCK:
                if (!s.Blocked)
                {
                    var u = s with { Waiters = s.Counted ? s.Waiters - 1 : s.Waiters, Counted = false };
                    if (s.Event) yield return u with { Event = false, Pc = W_READ, LookRead = false, Trace = s.Trace + " w:wait-takes-event" };
                    else if (s.OwnerSig) yield return u with { OwnerSig = false, Pc = W_OWNER, Trace = s.Trace + " w:wait-owner" };
                    else yield return s with { Blocked = true, Trace = s.Trace + " w:blocks" };
                }
                else
                {
                    var u = s with { Blocked = false, Waiters = s.Counted ? s.Waiters - 1 : s.Waiters, Counted = false };
                    if (s.OwnerSig) yield return u with { OwnerSig = false, Pc = W_OWNER, Trace = s.Trace + " w:wakes-owner" };
                    if (s.RecyclesLeft > 0 && !s.OwnerSig)
                        yield return Recycle(u with { Trace = s.Trace + " w:timeout-recycle" }, d);
                }
                break;
            case W_OWNER:
            {
                // OwnerGone: the watch closes. This driver takes the capability
                // back and looks once for a frame the SDK skipped signaling.
                var n = s with { Watch = false, Pc = W_TOP, EndSeen = !s.Running || s.EndSeen, Trace = s.Trace + " w:owner-gone" };
                if (d == Driver.V203 || r.ResetAtOwnerGone) n = n with { Seen = 0 };
                if (gating && s.Section)
                {
                    n = n with { Cap = !r.GateOnlyWatched && s.Cap };
                    if (r.LookAfterOwnerGone && n.Seq != 0 && n.Seq != n.Gate)
                        n = Read(n with { Trace = n.Trace + " w:look-after" }, d, look: true, r.LookAgainstGateSeq) with { Pc = W_TOP };
                }
                yield return n;
                break;
            }
            case W_READ:
            {
                var n = Read(s with { Pc = s.OwnerAfterRead ? W_OWNER : W_TOP, OwnerAfterRead = false }, d, s.LookRead, r.LookAgainstGateSeq);
                bool stale = n.Delivered == s.Delivered;
                // With no watch, the retry after the frame connects one to the
                // program on the index.
                if (gating && !n.Watch && n.Running && prog!.Pipe && n.Pc == W_TOP)
                    n = Connect(n, d, r);
                yield return n;
                // A stale wakeup can end in a recycle (250 of them in a row).
                if (stale && !s.LookRead && s.RecyclesLeft > 0 && n.Pc == W_TOP)
                    yield return Recycle(n with { Trace = n.Trace + " w:stale-recycle" }, d);
                break;
            }
        }
    }

    // ProcessSharedInput. A zeroed section (DataSize and ExtendedReportSize 0)
    // is no frame to this driver. v2.0.3 sends any SeqNo other than SeenSeqNo.
    static State Read(State s, Driver d, bool look, bool againstGate = true)
    {
        var n = s with { Trace = s.Trace + $" w:read={s.Prog}:{s.Idx}@{s.Seq}" };
        if (d == Driver.This && s.Prog == 0) return n;
        bool fresh = look ? s.Seq != (againstGate ? s.Gate : s.Seen) : s.Seq != s.Seen;
        n = n with { Gate = s.Seq, DriverSeen = d == Driver.This && s.Section ? s.Seq : s.DriverSeen };
        if (fresh) n = n with { Seen = s.Seq, Delivered = s.Delivered + $" {s.Prog}:{s.Idx}", Trace = n.Trace + "-sent" };
        return n;
    }

    // The owner watch connects. This driver announces the capability then, and
    // forgets nothing while the watch stays.
    static State Connect(State s, Driver d, Rules r) =>
        s with { Watch = true, Cap = (d == Driver.This && s.Section) || s.Cap, Trace = s.Trace + " w:watch" };

    // A recycle reads nothing and closes the worker's input event and input
    // section. With no program running they are gone. v2.0.3 forgets the last
    // SeqNo. This driver does so only when it does not gate (no watch, or no
    // signal section).
    static State Recycle(State n, Driver d)
    {
        // Phase 1 handles a closed pipe first.
        n = n with { Pc = n.OwnerSig ? W_OWNER : W_TOP, OwnerSig = false, RecyclesLeft = n.RecyclesLeft - 1 };
        if (d == Driver.V203 || !n.Watch || !n.Section) n = n with { Seen = 0 };
        if (!n.Running) n = n with { Seq = 0, Prog = 0, Idx = 0, Event = false, EndSeen = n.EndSeen || n.Pc != W_OWNER, Trace = n.Trace + "-objects-gone" };
        return n;
    }

    static State Gone(State n) =>
        n with { Seq = 0, Prog = 0, Idx = 0, Section = false, Carry = 0, DriverSeen = 0, Event = false, Trace = n.Trace + "-objects-gone" };

    static State AfterFrame(State s, int frames)
    {
        int done = s.Done + 1;
        return s with { Done = done, SdkPc = done == frames ? S_EXIT : S_WRITE };
    }

    // SharedMemoryIO.EvenAbove, as the SDK computes it: 0 is no SeqNo, the
    // later of two as serial numbers, an odd one taken to the even one it
    // becomes, stepping over 0.
    static int EvenAbove(int a, int b)
    {
        uint ua = (uint)a, ub = (uint)b;
        uint m = ua == 0 ? ub : ub == 0 ? ua : (int)(ub - ua) > 0 ? ub : ua;
        if ((m & 1) != 0) { m++; if (m == 0) m = 2; }
        return (int)m;
    }
}
