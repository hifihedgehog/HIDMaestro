// The input gate's delivery rules (docs/design/signal-gate.md, "Which frame
// is new"), checked against v2.0.3's. Memory is sequentially consistent
// here: Program.cs checks the fences. This part checks which frames reach
// HID class.
//
// Each scenario is a sequence of programs on one index and the steps the
// machine may take around them. A program is a 2.1.0 one or a v2.0.x one,
// with an owner pipe the driver can open or without (a v1.11.0 program has
// none). A program makes its pipe first, then holds the input section, its
// event and, for a 2.1.0 program, the signal section from its start: it
// zeroes the input section there. A 2.1.0 program counts its frames from
// above the SeqNo it found, steps over 0 at the wrap, clears the handoff at
// its start, and with the capability set hands each frame over instead of
// signaling it, signaling only when a worker is counted and it takes the
// frame back. A v2.0.x program counts from 0, stores 0 at its wrap and
// signals every frame. A program's end closes its pipe after its last
// SetEvent. The next program starts either once the worker has handled
// that end, or at any time after it ("early", the idle window).
//
// The HID device's worker maps the input section, its event and the
// signal section in Phase 1, once the event exists, and holds them until a
// recycle (the input section and event) or until the device goes (all).
// The XUSB companion can hold the signal section, the input section or
// both from the moment they first exist (the holder scenarios). An object
// nothing holds goes, with what it held. The worker recycles after idle
// timeouts or after a stale wakeup, its device goes and is added again,
// and HID class sends a READ_REPORT at any time none is parked: the read
// takes the cached frame when it is not the last one delivered, as the
// driver's read path does, and parks otherwise. OwnerGone, the recycle and
// the add reset the cache's delivered gate in both drivers.
//
// v2.0.3's worker reads only on a signal and forgets the last SeqNo it sent
// at OwnerGone (which needs a watch), at every recycle and at a device's
// add. This driver's worker does the same and, with the signal section
// mapped, also counts itself and takes the handoff before each wait, reads
// the section when the take finds a frame, and skips a zeroed section.
//
// Each run records how often each frame (program, place in it) reached
// HID class, and the frame the section holds in a final state when it never
// did. The check: in every scenario this driver loses no frame v2.0.3 does
// not lose, sends none twice that v2.0.3 does not send twice, and never
// sends a zeroed section. Scenarios split the steps by the program they
// happen under, so a frame v2.0.3 loses in one order cannot hide a loss in
// another.
//
// Usage: the frames argument of SignalGateModel sets each program's frames
// (the wrap scenarios write two that straddle it).
using System;
using System.Collections.Generic;
using System.Linq;

internal static class DeliveryModel
{
    internal enum Driver { V203, This }

    internal sealed record Prog(bool Is21, bool Pipe)
    {
        public override string ToString() => (Is21 ? "2.1.0" : "v2.0.x") + (Pipe ? "" : " pipe-less");
    }

    // Recycle and ReAdd: the program (1-based) under which the step may
    // happen, 0 for never. HoldSig and HoldIn: the companion holds the signal
    // or the input section once it exists.
    internal sealed record Scenario(string Name, Prog[] Programs)
    {
        public bool Early { get; init; }
        public int Recycle { get; init; }
        public int ReAdd { get; init; }
        public bool HoldSig { get; init; }
        public bool HoldIn { get; init; }
        public int Wrap { get; init; }
        public int? Frames { get; init; }
    }

    // The rules under test, each taken away in turn: a variant must fail.
    internal sealed record Rules(string Name)
    {
        public bool Reclaim { get; init; } = true;        // the SDK signals only after taking the frame back
        public bool TakeBeforeWait { get; init; } = true; // the worker takes the handoff before each wait
        public bool CountFirst { get; init; } = true;     // ... after it counted itself
        public bool ClearAtStart { get; init; } = true;   // a 2.1.0 start clears the handoff
        public bool SkipZero { get; init; } = true;       // a zeroed section is no frame
        public bool SdkSkipsZero { get; init; } = true;   // a 2.1.0 SDK steps over 0 at the wrap
        public bool ForgetAtRecycle { get; init; } = true;// the recycle forgets the last SeqNo, as v2.0.3's
        public bool ClearCapAtGo { get; init; } = true;   // a device that goes clears the capability
        public bool CloseGate { get; init; } = true;      // a recycle closes the gate while the event is open
        public bool TakeAtOwnerGone { get; init; } = true;// OwnerGone takes the handoff before its reset
        public bool DropSent { get; init; } = true;       // ... and drops a frame already sent
        public bool OwedOnlyMapped { get; init; } = true; // ... and signals it only with the input event open
        public bool OwedOnlyHeld { get; init; } = true;   // ... and only while the section still holds it
        public bool DropSentAtTake { get; init; } = true; // a take of the frame last sent wakes no read
        public bool WaitAgainAfterOwner { get; init; } = true; // a taken frame read on the pipe's close waits again before OwnerGone
    }

    internal static readonly Rules This = new("this driver");

    internal static readonly Rules[] Broken =
    {
        This with { Name = "a SetEvent with no take-back", Reclaim = false },
        This with { Name = "no take before the wait", TakeBeforeWait = false },
        This with { Name = "a take before the count", CountFirst = false },
        This with { Name = "the handoff left at a 2.1.0 start", ClearAtStart = false },
        This with { Name = "a zeroed section read as a frame", SkipZero = false },
        This with { Name = "SeqNo 0 stored at the wrap", SdkSkipsZero = false },
        This with { Name = "the recycle keeps the last SeqNo", ForgetAtRecycle = false },
        This with { Name = "the capability kept as the device goes", ClearCapAtGo = false },
        This with { Name = "no gate closed at a recycle or as the device goes", CloseGate = false },
        This with { Name = "no take at OwnerGone", TakeAtOwnerGone = false },
        This with { Name = "a frame already sent read again after OwnerGone", DropSent = false },
        This with { Name = "a SetEvent after OwnerGone in Phase 1", OwedOnlyMapped = false },
        This with { Name = "a SetEvent after OwnerGone for a frame overwritten", OwedOnlyHeld = false },
        This with { Name = "a take of the frame last sent read", DropSentAtTake = false },
        This with { Name = "OwnerGone straight after a taken frame read on the pipe's close", WaitAgainAfterOwner = false },
    };

    internal static IEnumerable<Scenario> Scenarios()
    {
        var kinds = new[] { new Prog(true, true), new Prog(false, true), new Prog(true, false), new Prog(false, false) };
        var steps = new[] { (0, 0), (1, 0), (2, 0), (0, 1), (0, 2), (1, 2), (2, 1), (1, 1), (2, 2) };
        foreach (var a in kinds)
            foreach (var b in kinds)
                foreach (bool early in new[] { false, true })
                {
                    foreach (var (rc, ra) in steps)
                        yield return new Scenario(Name(a, b, early, rc, ra, ""), new[] { a, b }) { Early = early, Recycle = rc, ReAdd = ra };
                    foreach (var (sig, inp) in new[] { (true, false), (false, true), (true, true) })
                        foreach (var (rc, ra) in new[] { (0, 1), (2, 1), (1, 2) })
                            yield return new Scenario(Name(a, b, early, rc, ra, (sig ? ", companion holds the signal section" : "") + (inp ? ", companion holds the input section" : "")),
                                                      new[] { a, b }) { Early = early, Recycle = rc, ReAdd = ra, HoldSig = sig, HoldIn = inp };
                }
        foreach (var seq in new[] { new[] { true, true, false }, new[] { false, true, false }, new[] { true, false, true } })
            foreach (bool early in new[] { false, true })
                yield return new Scenario(string.Join(" then ", seq.Select(g => g ? "2.1.0" : "v2.0.x")) + (early ? ", early" : ""),
                                          seq.Select(g => new Prog(g, true)).ToArray()) { Early = early, Frames = 2 };
        foreach (var k in kinds)
            yield return new Scenario($"{k} across the wrap", new[] { k }) { Wrap = 16, Frames = 2, Recycle = 1, ReAdd = 1 };
    }

    static string Name(Prog a, Prog b, bool early, int rc, int ra, string hold) =>
        $"{a} then {b}{(early ? ", early" : "")}{(rc > 0 ? $", recycle under program {rc}" : "")}{(ra > 0 ? $", re-add under program {ra}" : "")}{hold}";

    const int S_CLAIM = 0, S_START = 1, S_WRITE = 2, S_CHECK = 3, S_RECLAIM = 4, S_SIGNAL = 5, S_EXIT = 6, S_NEXT = 7, S_DONE = 8, S_FRAMEDONE = 9;

    // The program steps both drivers share: a step from one of these program
    // counters that moves the program is the same event under either driver.
    // A frame's handoff, take-back and SetEvent are each driver's own, between
    // the frame's store (S_WRITE) and the end of its publication
    // (S_FRAMEDONE).
    static bool SharedPc(int pc) => pc is S_CLAIM or S_START or S_WRITE or S_FRAMEDONE or S_EXIT or S_NEXT;
    // The recycle and the device's going are shared too: the idle time and the PnP removal behind them come from outside the driver.
    // The worker's Phase 1 map is shared as well, so v2.0.3's worker keeps pace
    // with this driver's: neither can map later than the other to miss a frame.
    static int Label(State s, State n) =>
        SharedPc(s.SdkPc) && (n.SdkPc != s.SdkPc || n.P != s.P) ? 1
        : n.RecycleUsed != s.RecycleUsed ? 2
        : n.ReAddUsed != s.ReAddUsed ? 3
        : s.Pc == W_P1 && n.Pc == W_TOP && n.Mapped && !s.Mapped ? 4
        : 0;
    static bool IsShared(State s, State n) => Label(s, n) != 0;
    const int W_P1 = 0, W_TOP = 1, W_LOOK = 2, W_POLL = 3, W_BLOCK = 4, W_READ = 5, W_OWNER = 6, W_GONE = 7, W_COUNT = 8;

    // A frame's identity is its program and its place, 1 to 5, packed into
    // the bits of the delivery counts: two bits a frame, saturating at 2.
    // Frame 0 of a program is the zeroed section.
    static int Slot(int prog, int idx) => (prog - 1) * 6 + idx;

    // InSec, Ev, Sig: the input section, its event and the signal section
    // exist. Seq, Prog, Idx: the frame the input section holds (Prog 0: a
    // zeroed section). Hand: the handoff. Holds: the program holds its
    // objects. Mapped and SigMapped: the worker holds the input section and
    // event, and the signal section. Cache: the frame the read path holds
    // (slot + 1, 0 for none), CacheSeq its SharedMemSeqNo, LastDel the one
    // last delivered, Parked a READ_REPORT parked.
    internal record struct State(
        bool InSec, bool Ev, bool Sig, int Seq, int Prog, int Idx, bool Event, bool Cap, int Waiters, int Hand,
        int P, int SdkPc, int SdkSeq, int Done, bool PipeOpen, bool Holds, bool EndSeen,
        bool Watch, bool OwnerSig, int Pc, bool Counted, bool Blocked, bool AfterOwner, bool Owed, int Seen, bool Mapped, bool SigMapped,
        int Cache, int CacheSeq, int LastDel, bool Parked, bool RecycleUsed, bool ReAddUsed, ulong Delivered);

    internal sealed class Result
    {
        public long States;
        public long Finals;
        public readonly SortedSet<string> Lost = new(), Duplicate = new();
        // Each final state's delivery counts, frame by frame.
        public readonly HashSet<ulong> Outcomes = new();
        public bool ZeroSent;
    }

    // The zeroed-section slots out of a delivery vector.
    static readonly ulong FrameMask = Enumerable.Range(0, 18).Where(x => x % 6 != 0)
                                                .Aggregate(0UL, (m, x) => m | (3UL << (2 * x)));

    // An outcome of this driver no worse than one of v2.0.3's: every frame
    // that one delivers this one delivers, and every frame this one sends
    // twice that one sends twice.
    internal static bool NoWorse(ulong mine, ulong theirs)
    {
        for (int slot = 0; slot < 18; slot++)
        {
            if (slot % 6 == 0) continue;
            int m = Count(mine, slot), t = Count(theirs, slot);
            if (t >= 1 && m == 0) return false;
            if (m == 2 && t < 2) return false;
        }
        return true;
    }

    internal static string Show(ulong v) =>
        string.Join(" ", Enumerable.Range(0, 18).Where(x => x % 6 != 0 && Count(v, x) > 0).Select(x => $"{x / 6 + 1}:{x % 6}x{Count(v, x)}"));

    internal static Result Explore(Scenario sc, Driver d, Rules r, int framesArg)
    {
        int frames = sc.Frames ?? framesArg;
        var start = new State(false, false, false, 0, 0, 0, false, false, 0, 0,
                              1, S_CLAIM, 0, 0, false, false, true,
                              false, false, W_P1, false, false, false, false, 0, false, false,
                              0, 0, 0, false, false, false, 0);
        var seen = new HashSet<State> { start };
        var stack = new Stack<State>();
        stack.Push(start);
        var res = new Result();
        var next = new List<State>();
        while (stack.Count > 0)
        {
            var s = stack.Pop();
            res.States++;
            next.Clear();
            Steps(s, sc, d, r, frames, next);
            if (next.Count == 0)
            {
                res.Finals++;
                res.Outcomes.Add(s.Delivered & FrameMask);
                if (s.InSec && s.Prog != 0 && Count(s.Delivered, Slot(s.Prog, s.Idx)) == 0)
                    res.Lost.Add($"{s.Prog}:{s.Idx}");
                continue;
            }
            foreach (var n in next)
            {
                if (n.Delivered != s.Delivered)
                {
                    for (int slot = 0; slot < 64 / 2; slot++)
                        if (Count(n.Delivered, slot) == 2 && Count(s.Delivered, slot) < 2)
                        {
                            if (slot % 6 == 0) res.ZeroSent = true;
                            else res.Duplicate.Add($"{slot / 6 + 1}:{slot % 6}");
                        }
                    for (int p = 1; p <= 3; p++)
                        if (Count(n.Delivered, Slot(p, 0)) > 0) res.ZeroSent = true;
                }
                if (seen.Add(n)) stack.Push(n);
            }
        }
        return res;
    }

    // The shortest order that sends the frame twice (twice true) or ends
    // without it, as the states it passes through: for a failure's report.
    internal static List<State> Witness(Scenario sc, Driver d, Rules r, int framesArg, string frame, bool twice)
    {
        int frames = sc.Frames ?? framesArg;
        var parts = frame.Split(':');
        int slot = frame.StartsWith("=") ? 0 : Slot(int.Parse(parts[0]), int.Parse(parts[1]));
        var start = new State(false, false, false, 0, 0, 0, false, false, 0, 0, 1, S_CLAIM, 0, 0, false, false, true,
                              false, false, W_P1, false, false, false, false, 0, false, false, 0, 0, 0, false, false, false, 0);
        var parent = new Dictionary<State, State> { [start] = start };
        var queue = new Queue<State>();
        queue.Enqueue(start);
        var next = new List<State>();
        while (queue.Count > 0)
        {
            var s = queue.Dequeue();
            next.Clear();
            Steps(s, sc, d, r, frames, next);
            bool hit = frame.StartsWith("=")
                ? next.Count == 0 && (s.Delivered & FrameMask) == ulong.Parse(frame.Substring(1))
                : twice ? Count(s.Delivered, slot) == 2
                        : next.Count == 0 && s.InSec && Slot(s.Prog, s.Idx) == slot && Count(s.Delivered, slot) == 0;
            if (hit)
            {
                var path = new List<State> { s };
                while (!path[^1].Equals(start)) path.Add(parent[path[^1]]);
                path.Reverse();
                return path;
            }
            foreach (var n in next)
                if (parent.TryAdd(n, s)) queue.Enqueue(n);
        }
        return new List<State>();
    }

    static int Count(ulong delivered, int slot) => (int)((delivered >> (2 * slot)) & 3);

    // A set of v2.0.3 states, compared by its members.
    sealed class StateSet : IEquatable<StateSet>
    {
        public readonly HashSet<State> Items;
        readonly int hash;
        public StateSet(HashSet<State> items)
        {
            Items = items;
            int h = items.Count;
            foreach (var x in items) h ^= x.GetHashCode() * 16777619;
            hash = h;
        }
        public bool Equals(StateSet? o) => o != null && o.hash == hash && Items.SetEquals(o.Items);
        public override bool Equals(object? o) => Equals(o as StateSet);
        public override int GetHashCode() => hash;
    }

    internal sealed class AlignedResult
    {
        public long States, Finals, Unaligned;
        public readonly HashSet<ulong> Worse = new();
        public bool ZeroSent;
        public string? Example;
        public List<State>? Path;
    }

    // The order-aligned check. Each state of this driver's is paired with the
    // set of v2.0.3 states reachable through the same program steps (SharedPc),
    // each driver taking any steps of its own in between. At a final state of
    // this driver's, some final state in that set must deliver every frame
    // this one does not lose and send twice every frame this one sends twice.
    internal static AlignedResult ExploreAligned(Scenario sc, Rules r, int framesArg, bool trace = false)
    {
        int frames = sc.Frames ?? framesArg;
        var res = new AlignedResult();
        var buf = new List<State>();
        var interned = new Dictionary<StateSet, StateSet>();
        var closures = new Dictionary<StateSet, StateSet>();

        StateSet Intern(HashSet<State> h)
        {
            var k = new StateSet(h);
            if (interned.TryGetValue(k, out var e)) return e;
            interned[k] = k;
            return k;
        }
        // v2.0.3's own steps from a set, to a fixed point.
        StateSet Close(StateSet seed)
        {
            if (closures.TryGetValue(seed, out var c)) return c;
            var all = new HashSet<State>(seed.Items);
            var work = new Stack<State>(seed.Items);
            var nx = new List<State>();
            while (work.Count > 0)
            {
                var v = work.Pop();
                nx.Clear();
                Steps(v, sc, Driver.V203, This, frames, nx);
                foreach (var n in nx)
                    if (!IsShared(v, n) && all.Add(n)) work.Push(n);
            }
            c = Intern(all);
            closures[seed] = c;
            return c;
        }

        var t0 = Start();
        var v0 = Close(Intern(new HashSet<State> { Start() }));
        var seen = new HashSet<(State, StateSet)> { (t0, v0) };
        var parent = trace ? new Dictionary<(State, StateSet), (State, StateSet)>() : null;
        var stack = new Stack<(State, StateSet)>();
        stack.Push((t0, v0));
        var vn = new List<State>();
        while (stack.Count > 0)
        {
            var (t, V) = stack.Pop();
            res.States++;
            buf.Clear();
            Steps(t, sc, Driver.This, r, frames, buf);
            if (buf.Count == 0)
            {
                res.Finals++;
                ulong mine = t.Delivered & FrameMask;
                for (int pr = 1; pr <= 3; pr++) if (Count(t.Delivered, Slot(pr, 0)) > 0) res.ZeroSent = true;
                bool matched = false;
                foreach (var v in V.Items)
                {
                    vn.Clear();
                    Steps(v, sc, Driver.V203, This, frames, vn);
                    if (vn.Count == 0 && NoWorse(mine, v.Delivered & FrameMask)) { matched = true; break; }
                }
                if (!matched && res.Worse.Add(mine) && res.Example == null)
                {
                    res.Example = Show(mine);
                    if (parent != null)
                    {
                        res.Path = new List<State>();
                        var k = (t, V);
                        while (true) { res.Path.Add(k.Item1); if (!parent.TryGetValue(k, out var pk)) break; k = pk; }
                        res.Path.Reverse();
                    }
                }
                continue;
            }
            foreach (var n in buf)
            {
                StateSet V2 = V;
                if (IsShared(t, n))
                {
                    var h = new HashSet<State>();
                    foreach (var v in V.Items)
                    {
                        vn.Clear();
                        Steps(v, sc, Driver.V203, This, frames, vn);
                        foreach (var m in vn)
                            if (Label(v, m) == Label(t, n) && m.SdkPc == n.SdkPc && m.P == n.P && m.Done == n.Done && m.RecycleUsed == n.RecycleUsed && m.ReAddUsed == n.ReAddUsed) h.Add(m);
                    }
                    if (h.Count == 0) { res.Unaligned++; continue; }
                    V2 = Close(Intern(h));
                }
                if (seen.Add((n, V2))) { parent?.Add((n, V2), (t, V)); stack.Push((n, V2)); }
            }
        }
        return res;
    }

    static State Start() => new State(false, false, false, 0, 0, 0, false, false, 0, 0, 1, S_CLAIM, 0, 0, false, false, true,
                                      false, false, W_P1, false, false, false, false, 0, false, false, 0, 0, 0, false, false, false, 0);

    static ulong Deliver(ulong delivered, int slot) =>
        Count(delivered, slot) == 2 ? delivered : delivered + (1UL << (2 * slot));

    static void Steps(State s, Scenario sc, Driver d, Rules r, int frames, List<State> next)
    {
        bool gating = d == Driver.This && s.SigMapped;
        var prog = s.P <= sc.Programs.Length ? sc.Programs[s.P - 1] : null;
        bool deviceUp = s.Pc != W_GONE;

        // The programs.
        switch (s.SdkPc)
        {
            case S_CLAIM:
                next.Add(Collect(s with { PipeOpen = prog!.Pipe, SdkPc = S_START }, sc, prog));
                break;
            case S_START:
            {
                // The input section and its event, zeroed, the signal section,
                // the start.
                int left = s.InSec ? s.Seq : 0;
                var n = s with { Holds = true, InSec = true, Seq = 0, Prog = 0, Idx = 0, Ev = true, Event = s.Ev && s.Event, SdkPc = S_WRITE };
                if (prog!.Is21)
                {
                    if (!s.Sig) n = n with { Sig = true, Cap = false, Waiters = 0, Hand = 0 };
                    if (r.ClearAtStart) n = n with { Hand = 0 };
                }
                int startSeq = sc.Wrap > 0 ? sc.Wrap - 4 : prog.Is21 ? EvenAbove(left) : 0;
                next.Add(Collect(n with { SdkSeq = startSeq }, sc, prog));
                break;
            }
            case S_WRITE:
            {
                int seq = s.SdkSeq + 2;
                if (sc.Wrap > 0) seq %= sc.Wrap;
                if (seq == 0 && prog!.Is21 && r.SdkSkipsZero) seq = 2;
                next.Add(s with { Seq = seq, Prog = s.P, Idx = s.Done + 1, SdkSeq = seq, SdkPc = S_CHECK });
                break;
            }
            case S_CHECK:
                // A 2.1.0 SDK with the capability hands the frame over.
                next.Add(prog!.Is21 && s.Cap ? s with { Hand = s.SdkSeq, SdkPc = S_RECLAIM } : s with { SdkPc = S_SIGNAL });
                break;
            case S_RECLAIM:
                // The capability again, then the count.
                if (s.Cap && s.Waiters == 0) next.Add(AfterFrame(s, frames));
                else if (!r.Reclaim) next.Add(s with { SdkPc = S_SIGNAL });
                else if (s.Hand == s.SdkSeq) next.Add(s with { Hand = 0, SdkPc = S_SIGNAL });
                else next.Add(AfterFrame(s, frames));
                break;
            case S_SIGNAL:
            {
                var n = s.Pc == W_BLOCK && s.Blocked
                    ? s with { Blocked = false, Pc = W_READ, AfterOwner = false, Waiters = s.Counted ? s.Waiters - 1 : s.Waiters, Counted = false }
                    : s with { Event = true };
                next.Add(AfterFrame(n, frames));
                break;
            }
            case S_EXIT:
                next.Add(Collect(s with { OwnerSig = s.Watch, PipeOpen = false, Holds = false, EndSeen = false,
                                          SdkPc = s.P < sc.Programs.Length ? S_NEXT : S_DONE }, sc, prog));
                break;
            case S_FRAMEDONE:
                next.Add(s with { SdkPc = s.Done == frames ? S_EXIT : S_WRITE });
                break;
            case S_NEXT:
                if (sc.Early || s.EndSeen)
                    next.Add(s with { P = s.P + 1, Done = 0, SdkPc = S_CLAIM });
                break;
        }

        // HID class: a READ_REPORT when none is parked.
        if (deviceUp && !s.Parked)
        {
            if (s.Cache != 0 && s.CacheSeq != s.LastDel)
                next.Add(s with { Delivered = Deliver(s.Delivered, s.Cache - 1), LastDel = s.CacheSeq });
            else
                next.Add(s with { Parked = true });
        }

        // The worker.
        bool recycleNow = !s.RecycleUsed && sc.Recycle == s.P;
        bool reAddNow = !s.ReAddUsed && sc.ReAdd == s.P;
        switch (s.Pc)
        {
            case W_P1:
                if (s.OwnerSig)
                    next.Add(Owner(s with { OwnerSig = false }, d, r) with { Pc = W_P1 });
                else if (s.Ev)
                {
                    var n = s with { Mapped = true, SigMapped = s.SigMapped || s.Sig, Pc = W_TOP };
                    if (d == Driver.This && n.SigMapped) n = n with { Cap = true };
                    next.Add(Connect(n, prog));
                }
                if (reAddNow) next.Add(Goes(s, sc, d, r, prog));
                break;
            case W_TOP:
                if (s.Owed)
                {
                    // The read OwnerGone owes for a frame it took.
                    next.Add(Read(s with { Owed = false }, d, r, out _));
                    break;
                }
                if (gating)
                    next.Add(r.CountFirst ? s with { Waiters = s.Waiters + 1, Counted = true, Pc = W_LOOK } : s with { Pc = W_LOOK });
                else
                    next.Add(s with { Pc = W_BLOCK });
                if (!s.Watch && s.PipeOpen && prog!.Pipe) next.Add(Connect(s, prog));
                if (reAddNow) next.Add(Goes(s, sc, d, r, prog));
                break;
            case W_LOOK:
            {
                bool took = r.TakeBeforeWait && s.Hand != 0 && (s.Hand != s.Seen || !r.DropSentAtTake);
                var n = took ? s with { Hand = 0, Waiters = s.Counted ? s.Waiters - 1 : s.Waiters, Counted = false, Pc = W_POLL }
                             : s with { Hand = r.TakeBeforeWait ? 0 : s.Hand, Pc = W_BLOCK };
                // The broken order counts itself only after a take that found
                // nothing, as a step of its own.
                if (!r.CountFirst && !took) n = n with { Pc = W_COUNT };
                next.Add(n);
                break;
            }
            case W_COUNT:
                next.Add(s with { Waiters = s.Waiters + 1, Counted = true, Pc = W_BLOCK });
                break;
            case W_POLL:
                // The wait with a zero timeout: the input event comes first,
                // then the owner pipe, and a timeout stands for the take. On
                // the owner pipe the worker reads, then waits again: the
                // pipe's event stays set, and an input event set meanwhile
                // comes first.
                if (s.Event) next.Add(s with { Event = false, Pc = W_READ, AfterOwner = false });
                else if (s.OwnerSig && !r.WaitAgainAfterOwner) next.Add(s with { OwnerSig = false, Pc = W_READ, AfterOwner = true });
                else next.Add(s with { Pc = W_READ, AfterOwner = false });
                break;
            case W_BLOCK:
                if (!s.Blocked)
                {
                    var u = s with { Waiters = s.Counted ? s.Waiters - 1 : s.Waiters, Counted = false };
                    if (s.Event) next.Add(u with { Event = false, Pc = W_READ, AfterOwner = false });
                    else if (s.OwnerSig) next.Add(Owner(u with { OwnerSig = false }, d, r));
                    else next.Add(s with { Blocked = true });
                }
                else
                {
                    var u = s with { Blocked = false, Waiters = s.Counted ? s.Waiters - 1 : s.Waiters, Counted = false };
                    if (s.OwnerSig) next.Add(Owner(u with { OwnerSig = false }, d, r));
                    else if (recycleNow) next.Add(Recycle(u, sc, d, r, prog));
                }
                break;
            case W_READ:
            {
                var n = Read(s with { Pc = s.AfterOwner ? W_OWNER : W_TOP, AfterOwner = false }, d, r, out bool fresh);
                if (n.Pc == W_TOP && !n.Watch && n.PipeOpen && prog!.Pipe) n = Connect(n, prog);
                next.Add(n);
                // 250 stale wakeups in a row end in a recycle.
                if (!fresh && recycleNow && n.Pc == W_TOP) next.Add(Recycle(n, sc, d, r, prog));
                break;
            }
            case W_OWNER:
                next.Add(Owner(s, d, r));
                break;
            case W_GONE:
                // EvtDeviceAdd: a context with nothing read, nothing cached.
                next.Add(s with { Pc = W_P1, Seen = 0, Cache = 0, CacheSeq = 0, LastDel = 0, Parked = false });
                break;
        }
    }

    // ProcessSharedInput. v2.0.3 sends a zeroed section as a frame.
    static State Read(State s, Driver d, Rules r, out bool fresh)
    {
        fresh = false;
        if (s.Prog == 0 && d == Driver.This && r.SkipZero) return s;
        int slot = s.Prog == 0 ? Slot(s.P, 0) : Slot(s.Prog, s.Idx);
        fresh = s.Seq != s.Seen;
        if (!fresh) return s;
        // The frame goes in the cache with its SeqNo, and a parked read
        // completes with it at once.
        var n = s with { Seen = s.Seq, Cache = slot + 1, CacheSeq = s.Seq };
        return n.Parked
            ? n with { Delivered = Deliver(n.Delivered, slot), LastDel = s.Seq, Parked = false }
            : n;
    }

    // OwnerGone: the watch closes, the last SeqNo and the delivered gate go.
    // This driver first takes the handoff: a frame already sent, or one the
    // section no longer holds, is dropped, and any other sets the input
    // event after the reset.
    static State Owner(State s, Driver d, Rules r)
    {
        bool owed = false;
        if (d == Driver.This && s.SigMapped && r.TakeAtOwnerGone)
        {
            owed = s.Hand != 0 && (s.Hand != s.Seen || !r.DropSent) && (s.Mapped || !r.OwedOnlyMapped)
                   && (s.Seq == s.Hand || !r.OwedOnlyHeld);
            s = s with { Hand = 0 };
        }
        return s with { Watch = false, Seen = 0, CacheSeq = 0, LastDel = 0, EndSeen = true, Event = s.Event || owed, Pc = s.Mapped ? W_TOP : W_P1 };
    }

    // TryOpenOwnerWatch, at a map or after a frame.
    static State Connect(State s, Prog? prog) =>
        s.Watch || !s.PipeOpen || prog == null || !prog.Pipe ? s : s with { Watch = true };

    // CloseGate: the capability goes, and a frame handed over and not sent
    // becomes a SetEvent on the input event, while that exists.
    static State CloseGate(State s, Driver d, Rules r)
    {
        if (d != Driver.This || !s.SigMapped || !r.CloseGate) return s;
        var n = s with { Cap = false, Hand = 0 };
        if (s.Hand != 0 && s.Hand != s.Seen && s.Ev) n = n with { Event = true };
        return n;
    }

    // A recycle closes the input section and its event and goes to Phase 1.
    static State Recycle(State n, Scenario sc, Driver d, Rules r, Prog? prog)
    {
        n = CloseGate(n, d, r);
        n = n with { Mapped = false, Pc = W_P1, RecycleUsed = true, CacheSeq = 0, LastDel = 0, EndSeen = n.EndSeen || !n.Holds };
        if (d == Driver.V203 || r.ForgetAtRecycle) n = n with { Seen = 0 };
        return Collect(n, sc, prog);
    }

    // The device goes: everything the worker held goes with it.
    static State Goes(State s, Scenario sc, Driver d, Rules r, Prog? prog)
    {
        if (r.ClearCapAtGo) s = CloseGate(s, d, r);
        var n = s with { Pc = W_GONE, Mapped = false, SigMapped = false, Watch = false, OwnerSig = false, Counted = false, Owed = false,
                         Cache = 0, CacheSeq = 0, LastDel = 0, Parked = false, ReAddUsed = true, EndSeen = s.EndSeen || !s.Holds };
        return Collect(n, sc, prog);
    }

    // Objects nothing holds go, with what they held. The companion holds a
    // section once it exists.
    static State Collect(State s, Scenario sc, Prog? prog)
    {
        bool progSig = s.Holds && prog != null && prog.Is21;
        if (s.InSec && !(s.Holds || s.Mapped || sc.HoldIn)) s = s with { InSec = false, Seq = 0, Prog = 0, Idx = 0 };
        if (s.Ev && !(s.Holds || s.Mapped)) s = s with { Ev = false, Event = false };
        if (s.Sig && !(progSig || s.SigMapped || sc.HoldSig)) s = s with { Sig = false, Cap = false, Waiters = 0, Hand = 0 };
        return s;
    }

    static State AfterFrame(State s, int frames)
    {
        return s with { Done = s.Done + 1, SdkPc = S_FRAMEDONE };
    }

    // SharedMemoryIO.EvenAbove(left, 0): the SeqNo left, taken to the even
    // one an odd one becomes, stepping over 0.
    static int EvenAbove(int left)
    {
        uint m = (uint)left;
        if ((m & 1) != 0) { m++; if (m == 0) m = 2; }
        return (int)m;
    }
}
