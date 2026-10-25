// The input gate's delivery rules (docs/design/signal-gate.md, "Which frame
// is new"), checked against v2.0.3's. Memory is sequentially consistent
// here: Program.cs checks the fences. This part checks which frames reach
// HID class, and which report a keyboard leaves last.
//
// Each scenario is a sequence of programs on one index and the steps the
// machine may take around them. A program is a 2.1.0 one or a v2.0.x one,
// with an owner pipe the driver can open or without (a v1.11.0 program has
// none), and a keyboard leaves its release frame in the acknowledgement
// after its first frame. A program makes its pipe first, then holds the
// input section, its event and, for a 2.1.0 program, the signal section
// from its start: it zeroes the input section and the acknowledgement
// there (v1.11.0 has no acknowledgement). A 2.1.0 program counts its frames
// from above the SeqNo it found, steps over 0 at the wrap, clears the
// handoff at its start, and with the capability set hands each frame over
// instead of signaling it, signaling only when a worker is counted or the
// gate closed and it takes the frame back. A v2.0.x program counts from 0,
// stores 0 at its wrap and signals every frame. A program's end closes its
// pipe after its last SetEvent. A kill ends it inside a frame's
// publication: after the store, after the exchange, after the take-back
// or after the SetEvent. The next program starts either once the worker
// has handled that end, or at any time after it ("early", the idle window).
//
// The HID device's worker maps the input section, its event and the
// signal section in Phase 1, once the event exists, and holds them until a
// recycle (the input section and event) or until the device goes (all).
// The XUSB companion can hold the signal section, the input section or
// both from the moment they first exist (the holder scenarios). An object
// nothing holds goes, with what it held. The worker recycles after idle
// timeouts, after a stale wakeup, or when a foreign stop (another program's
// sweep) sets the stop event. The device goes as ReleaseIndexObjects runs
// it: the queues are purged, TearingDown is set, then the stop event, and
// the worker leaves at its next wait; the release closes the gate and lets
// go of everything, and the device is added again. HID class sends a
// READ_REPORT at any time none is parked while the device is up: a release
// frame queued at OwnerGone goes first, then the cached frame when it is
// not the last one delivered, as the driver's read path does, and the read
// parks otherwise. OwnerGone, the recycle and the add reset the cache's
// delivered gate in both drivers.
//
// v2.0.3's worker reads only on a signal and forgets the last SeqNo it sent
// at OwnerGone (which needs a watch), at every recycle and at a device's
// add. This driver's worker does the same and, with the signal section
// mapped, also counts itself and takes the handoff before each wait, reads
// the section when the take finds a frame, and skips a zeroed section.
//
// Each run records how often each frame (program, place in it) and each
// release frame reached HID class, and for each program whether one of its
// frames reached HID class after the release frames its end queued. The
// check pairs the runs (ExploreAligned): every final state of this driver
// must be matched or bettered by a final state v2.0.3 reaches through the
// same outside events, losing no frame or release frame that run delivers,
// sending twice only what that run sends twice, and sending a program's
// frame after its release frames only where that run does. A zeroed
// section must never go out.
//
// Usage: the frames argument of SignalGateModel sets each program's frames
// (the wrap scenarios write two that straddle it, the three-program ones
// two, and the kill scenarios at least two, so a keyboard has its release
// frame in place).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

internal static class DeliveryModel
{
    internal enum Driver { V203, This }

    // Is21: a 2.1.0 SDK. Pipe: an owner pipe the driver can open. Rel: a
    // keyboard, which writes its release frame into the acknowledgement
    // after its first frame (HMController.NoteReleaseFrame). Every SDK but
    // v1.11.0, the v2.0.x kind without a pipe, makes the acknowledgement and
    // zeroes it at its start.
    internal sealed record Prog(bool Is21, bool Pipe, bool Rel = false)
    {
        public bool MakesAck => Is21 || Pipe;
        public override string ToString() => (Is21 ? "2.1.0" : "v2.0.x") + (Rel ? " keyboard" : "") + (Pipe ? "" : " pipe-less");
    }

    // Recycle, ReAdd, Stop and Kill: the program (1-based) under which the
    // step may happen, 0 for never. Recycle is the idle recycle, ReAdd the
    // device's going and its add, Stop a foreign stop, Kill a kill inside
    // one of the program's frames. HoldSig and HoldIn: the companion holds
    // the signal or the input section once it exists.
    internal sealed record Scenario(string Name, Prog[] Programs)
    {
        public bool Early { get; init; }
        public int Recycle { get; init; }
        public int ReAdd { get; init; }
        public int Stop { get; init; }
        public int Kill { get; init; }
        public bool HoldSig { get; init; }
        public bool HoldIn { get; init; }
        public int Wrap { get; init; }
        public int? Frames { get; init; }
        public int MinFrames { get; init; }
        public int FramesFor(int arg) => Frames ?? Math.Max(arg, MinFrames);
    }

    // The rules under test, each taken away in turn.
    internal sealed record Rules(string Name)
    {
        public bool Reclaim { get; init; } = true;        // the SDK signals only after taking the frame back
        public bool TakeBeforeWait { get; init; } = true; // the worker takes the handoff before each wait
        public bool CountFirst { get; init; } = true;     // ... after it counted itself
        public bool ClearAtStart { get; init; } = true;   // a 2.1.0 start clears the handoff
        public bool SkipZero { get; init; } = true;       // a zeroed section is no frame
        public bool SdkSkipsZero { get; init; } = true;   // a 2.1.0 SDK steps over 0 at the wrap
        public bool ForgetAtRecycle { get; init; } = true;// the recycle forgets the last SeqNo, as v2.0.3's
        public bool ClearCapAtGo { get; init; } = true;   // a device that goes closes the gate
        public bool CloseGate { get; init; } = true;      // a recycle closes the gate while the event is open
        public bool TakeAtOwnerGone { get; init; } = true;// OwnerGone takes the handoff before its reset
        public bool DropSent { get; init; } = true;       // ... and drops a frame already sent
        public bool OwedOnlyMapped { get; init; } = true; // ... and takes only while the worker holds the input event
        public bool OwedOnlyHeld { get; init; } = true;   // ... and signals a frame only while the section still holds it
        public bool DropSentAtTake { get; init; }        // taken away: a take of the frame last sent wakes no read
        public bool WaitAgainAfterOwner { get; init; } = true; // a taken frame read on the pipe's close waits again before OwnerGone
        public bool SignalTakenOnExit { get; init; } = true;   // a frame taken and not read when the worker leaves its loop is signaled
        public bool OwedOnlyNextProgram { get; init; } = true; // OwnerGone signals only the next program's frame, not the ended one's
        public bool RetryAfterOwnerGone { get; init; } = true; // the watch is tried again after the first frame read after OwnerGone
        public bool ZeroForgets { get; init; } = true;     // a zeroed section forgets SeenSeqNo and SharedMemSeqNo and sends nothing
        public bool LateReadNotEqual { get; init; }        // taken away: a late READ_REPORT takes any cached SeqNo but the last delivered
        public bool TakeReadNoNext { get; init; } = true;  // a read a take alone woke sends no frame of a next program that started (B3)
        public bool TakeReadNextSeq { get; init; } = true; // ... and only the frame taken or its program's next SeqNo (NextFrameSeq)
        public bool BackOnlyAlone { get; init; } = true;   // a taken unread frame at a stop or teardown is signaled back only with no next program
        // Candidate changes, off in this driver (Candidates below).
        public bool CloseSignalsSent { get; init; }        // CloseGate signals a handed frame equal to the last one sent too
        public bool TakeReadSendsTaken { get; init; }      // a take's read sends, and a stop signals back, the frame taken when the section still holds it
        // Round 6 as the design summary gives it (no commit yet): a
        // provisional SeqNo goes in the handoff first, the recheck takes it
        // back and signals with a waiter or a closed gate and makes it final
        // otherwise, signaling when a take dropped it meanwhile; a pending
        // final value is replaced by the new frame's final one; a take drops
        // a provisional value and reads a final one, sending only the frame
        // with exactly that SeqNo.
        public bool Round6 { get; init; }
        public bool NoTakeBackOverUntaken { get; init; }   // the SDK skips the take-back when its exchange replaced a frame not taken
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
        This with { Name = "a take at OwnerGone in Phase 1", OwedOnlyMapped = false },
        This with { Name = "a SetEvent after OwnerGone for a frame overwritten", OwedOnlyHeld = false },
        This with { Name = "a take of the frame last sent wakes no read (B2)", DropSentAtTake = true },
        This with { Name = "OwnerGone straight after a taken frame read on the pipe's close", WaitAgainAfterOwner = false },
        This with { Name = "a taken frame dropped as the worker leaves its loop (B1)", SignalTakenOnExit = false },
        This with { Name = "OwnerGone signals the ended program's own frame (FIX-1)", OwedOnlyNextProgram = false },
        This with { Name = "a zeroed section keeps the last SeqNo and the cache (D1)", ZeroForgets = false },
        This with { Name = "a late read takes any SeqNo but the last delivered (D2)", LateReadNotEqual = true },
        This with { Name = "a read a take alone woke sends a next program's frame (B3)", TakeReadNoNext = false },
        This with { Name = "a read a take alone woke sends any SeqNo (NextFrameSeq)", TakeReadNextSeq = false },
        This with { Name = "a taken frame signaled back at a stop with a next program started", BackOnlyAlone = false },
    };

    // Candidate changes for the orders this driver loses (the model section
    // of docs/design/signal-gate.md), each run in place of this driver's
    // rules by the check command's rule c0, c1 or c2.
    internal static readonly Rules[] Candidates =
    {
        This with { Name = "CloseGate signals a handed frame equal to the last one sent", CloseSignalsSent = true },
        This with { Name = "no take-back over a frame not taken", NoTakeBackOverUntaken = true },
        This with { Name = "the watch tried again only at a map, as 533ca4a4", RetryAfterOwnerGone = false },
        This with { Name = "all three", CloseSignalsSent = true, NoTakeBackOverUntaken = true, RetryAfterOwnerGone = false },
        This with { Name = "a take's read sends the frame taken with a next program started", TakeReadSendsTaken = true },
        This with { Name = "all four", CloseSignalsSent = true, NoTakeBackOverUntaken = true, RetryAfterOwnerGone = false, TakeReadSendsTaken = true },
        This with { Name = "round 6 as summarized", Round6 = true, TakeReadNoNext = false, TakeReadNextSeq = false, BackOnlyAlone = false },
        This with { Name = "round 6 as summarized, the watch as 533ca4a4, CloseGate signals a sent frame",
                    Round6 = true, TakeReadNoNext = false, TakeReadNextSeq = false, BackOnlyAlone = false, RetryAfterOwnerGone = false, CloseSignalsSent = true },
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
        // A foreign stop at any point, a take before it included (B1's
        // recycle). The device's own teardown after a take is in the re-add
        // scenarios above.
        foreach (var a in kinds)
            foreach (var b in kinds)
                foreach (bool early in new[] { false, true })
                    foreach (int stop in new[] { 1, 2 })
                        yield return new Scenario(Name(a, b, early, 0, 0, $", a foreign stop under program {stop}"), new[] { a, b }) { Early = early, Stop = stop };
        // The first program killed inside one of its frames, a keyboard's
        // release frame in place from its first frame (FIX-1), followed by
        // every kind and by keyboards.
        var killed = new[] { new Prog(true, true, true), new Prog(false, true, true), new Prog(true, true) };
        var after = kinds.Concat(new[] { new Prog(true, true, true), new Prog(false, true, true) }).ToArray();
        foreach (var a in killed)
            foreach (var b in after)
                foreach (bool early in new[] { false, true })
                    yield return new Scenario(Name(a, b, early, 0, 0, ", program 1 killed inside a frame"), new[] { a, b }) { Early = early, Kill = 1, MinFrames = 2 };
    }

    static string Name(Prog a, Prog b, bool early, int rc, int ra, string hold) =>
        $"{a} then {b}{(early ? ", early" : "")}{(rc > 0 ? $", recycle under program {rc}" : "")}{(ra > 0 ? $", re-add under program {ra}" : "")}{hold}";

    const int S_CLAIM = 0, S_START = 1, S_WRITE = 2, S_CHECK = 3, S_RECLAIM = 4, S_SIGNAL = 5, S_EXIT = 6, S_NEXT = 7, S_DONE = 8, S_FRAMEDONE = 9;
    const int W_P1 = 0, W_TOP = 1, W_LOOK = 2, W_POLL = 3, W_BLOCK = 4, W_READ = 5, W_OWNER = 6, W_GONE = 7, W_COUNT = 8,
              W_WOKE = 9, W_OG_TAKE = 10, W_OG_LOOK = 11, W_OG_END = 12, W_OG_ACK = 13, W_OG_SIG = 14, W_EXITED = 15;

    // A frame's identity is its program and its place, 1 to 5, packed into
    // the bits of the delivery counts: two bits a frame, saturating at 2.
    // Frame 0 of a program is the zeroed section. Slots 18 to 20 count the
    // release frame each program left, and bits 42 to 44 say a program's
    // frame went out after the release frames its end queued.
    static int Slot(int prog, int idx) => (prog - 1) * 6 + idx;
    const int RelSlot = 18, LateBit = 42;

    // InSec, Ev, Sig: the input section, its event and the signal section
    // exist. Seq, Prog, Idx: the frame the input section holds (Prog 0: a
    // zeroed section). Hand: the handoff. Holds: the program holds its
    // objects. Mapped and SigMapped: the worker holds the input section and
    // event, and the signal section. Cache: the frame the read path holds
    // (slot + 1, 0 for none), CacheSeq its SharedMemSeqNo, LastDel the one
    // last delivered, Parked a READ_REPORT parked. StopSig: the stop event
    // is set. Tear: 1 once TearingDown is set, 2 once the stop event is set
    // for it too. Ack: the program whose release frame the acknowledgement
    // holds. AckBit: its release bit, which the watch sets as it connects or
    // the worker maps with the watch open, and a program's zeroing clears.
    // AckEnded: the watch-ended marker OwnerGone sets. WatchP: the program
    // whose pipe the watch holds. RelQ and RelFor: the release frame queued
    // (its program) and the program whose end queued it. RelDone: the
    // programs whose end's release frame went out. Handed: OwnerGone's take.
    // RetryNext: OwnerGone's WatchRetryNext. NoRetry: the read under way
    // came from a take answered by the owner pipe, which skips the retry.
    // Replaced serves a candidate change only.
    internal record struct State
    {
        public bool InSec, Ev, Sig, Event, Cap;
        public int Seq, Prog, Idx, Waiters, Hand;
        public int P, SdkPc, SdkSeq, Done;
        public bool PipeOpen, Holds, EndSeen, Watch, OwnerSig;
        public int Pc;
        public bool Counted, Blocked, AfterOwner, Owed, Mapped, SigMapped, Parked;
        public int Seen, Cache, CacheSeq, LastDel;
        public bool RecycleUsed, ReAddUsed, StopUsed, KillUsed, StopSig;
        public int Tear;
        public int Ack, WatchP, RelQ, RelFor, RelDone, Handed;
        public bool AckBit, AckEnded, Replaced, RetryNext, NoRetry, HandFinal;
        public int Took;
        public ulong Delivered;
    }

    // A state packed into three words, for the visited sets.
    internal readonly record struct Key(ulong A, ulong B, ulong C) : IComparable<Key>
    {
        public int CompareTo(Key o) => A != o.A ? A.CompareTo(o.A) : B != o.B ? B.CompareTo(o.B) : C.CompareTo(o.C);
    }

    struct Writer
    {
        public ulong W; int o;
        public void Put(int v, int bits)
        {
            if ((uint)v >= (1u << bits)) throw new InvalidOperationException($"a state field of {bits} bits holds {v}");
            if (o + bits > 64) throw new InvalidOperationException("a state word overflows");
            W |= (ulong)(uint)v << o; o += bits;
        }
        public void Put(bool v) => Put(v ? 1 : 0, 1);
    }

    struct Reader
    {
        readonly ulong w; int o;
        public Reader(ulong w) { this.w = w; o = 0; }
        public int Get(int bits) { int v = (int)((w >> o) & ((1UL << bits) - 1)); o += bits; return v; }
        public bool Bit() => Get(1) != 0;
    }

    internal static Key Pack(in State s)
    {
        var a = new Writer();
        a.Put(s.InSec); a.Put(s.Ev); a.Put(s.Sig); a.Put(s.Event); a.Put(s.Cap);
        a.Put(s.Seq, 8); a.Put(s.Prog, 2); a.Put(s.Idx, 3); a.Put(s.Waiters + 4, 4); a.Put(s.Hand, 8);
        a.Put(s.P, 3); a.Put(s.SdkPc, 4); a.Put(s.SdkSeq, 8); a.Put(s.Done, 3);
        a.Put(s.PipeOpen); a.Put(s.Holds); a.Put(s.EndSeen); a.Put(s.Watch); a.Put(s.OwnerSig);
        a.Put(s.Pc, 4);
        a.Put(s.Counted); a.Put(s.Blocked); a.Put(s.AfterOwner); a.Put(s.Owed); a.Put(s.Mapped); a.Put(s.SigMapped); a.Put(s.Parked);
        var b = new Writer();
        b.Put(s.Seen, 8); b.Put(s.Cache, 5); b.Put(s.CacheSeq, 8); b.Put(s.LastDel, 8);
        b.Put(s.RecycleUsed); b.Put(s.ReAddUsed); b.Put(s.StopUsed); b.Put(s.KillUsed); b.Put(s.StopSig);
        b.Put(s.Tear, 2);
        b.Put(s.Ack, 2); b.Put(s.WatchP, 2); b.Put(s.RelQ, 2); b.Put(s.RelFor, 2); b.Put(s.RelDone, 3); b.Put(s.Handed, 8);
        b.Put(s.AckBit); b.Put(s.AckEnded); b.Put(s.Replaced); b.Put(s.RetryNext); b.Put(s.NoRetry); b.Put(s.HandFinal && s.Hand != 0);
        // The delivery counts take 45 bits of the third word, Took the 8 above.
        if ((uint)s.Took >= 256) throw new InvalidOperationException("Took out of range");
        return new Key(a.W, b.W, s.Delivered | (ulong)(uint)s.Took << 45);
    }

    internal static State Unpack(Key k)
    {
        var a = new Reader(k.A);
        var s = new State();
        s.InSec = a.Bit(); s.Ev = a.Bit(); s.Sig = a.Bit(); s.Event = a.Bit(); s.Cap = a.Bit();
        s.Seq = a.Get(8); s.Prog = a.Get(2); s.Idx = a.Get(3); s.Waiters = a.Get(4) - 4; s.Hand = a.Get(8);
        s.P = a.Get(3); s.SdkPc = a.Get(4); s.SdkSeq = a.Get(8); s.Done = a.Get(3);
        s.PipeOpen = a.Bit(); s.Holds = a.Bit(); s.EndSeen = a.Bit(); s.Watch = a.Bit(); s.OwnerSig = a.Bit();
        s.Pc = a.Get(4);
        s.Counted = a.Bit(); s.Blocked = a.Bit(); s.AfterOwner = a.Bit(); s.Owed = a.Bit(); s.Mapped = a.Bit(); s.SigMapped = a.Bit(); s.Parked = a.Bit();
        var b = new Reader(k.B);
        s.Seen = b.Get(8); s.Cache = b.Get(5); s.CacheSeq = b.Get(8); s.LastDel = b.Get(8);
        s.RecycleUsed = b.Bit(); s.ReAddUsed = b.Bit(); s.StopUsed = b.Bit(); s.KillUsed = b.Bit(); s.StopSig = b.Bit();
        s.Tear = b.Get(2);
        s.Ack = b.Get(2); s.WatchP = b.Get(2); s.RelQ = b.Get(2); s.RelFor = b.Get(2); s.RelDone = b.Get(3); s.Handed = b.Get(8);
        s.AckBit = b.Bit(); s.AckEnded = b.Bit(); s.Replaced = b.Bit(); s.RetryNext = b.Bit(); s.NoRetry = b.Bit(); s.HandFinal = b.Bit();
        s.Delivered = k.C & ((1UL << 45) - 1);
        s.Took = (int)(k.C >> 45);
        return s;
    }

    static State Start() => new State { P = 1, SdkPc = S_CLAIM, EndSeen = true, Pc = W_P1 };

    // The outside events both drivers share. A program's own steps from
    // these program counters, a kill, the idle recycle (the idle time
    // behind it), the device's going (PnP) and its add, a foreign stop, the
    // worker's Phase 1 map (its 200 ms poll), and HID class sending a
    // READ_REPORT come from outside the driver, so v2.0.3 takes each of them
    // where this driver does. A frame's handoff, take-back and SetEvent are
    // each driver's own, between the frame's store (S_WRITE) and the end of
    // its publication (S_FRAMEDONE), so that window is one place for the
    // pairing. Every read of the section this driver makes, a zeroed one
    // and a stale one included, pairs with a read of v2.0.3's at the same
    // step. v2.0.3 also reads on wakes of its own (a SetEvent this driver
    // never makes, for a frame a take already found), so its reads are its
    // own steps as well.
    static bool SharedPc(int pc) => pc is S_CLAIM or S_START or S_WRITE or S_FRAMEDONE or S_EXIT or S_NEXT;
    const int L_PROG = 1, L_RECYCLE = 2, L_GOES = 3, L_MAP = 4, L_STOP = 5, L_ADD = 6, L_KILL = 7, L_HID = 8, L_READ = 9, L_READ0 = 10;

    static int Label(in State s, in State n)
    {
        if (n.KillUsed != s.KillUsed) return L_KILL;
        if (SharedPc(s.SdkPc) && (n.SdkPc != s.SdkPc || n.P != s.P)) return L_PROG;
        if (n.RecycleUsed != s.RecycleUsed) return L_RECYCLE;
        if (n.ReAddUsed != s.ReAddUsed) return L_GOES;
        if (s.Pc == W_P1 && n.Pc == W_TOP && n.Mapped && !s.Mapped) return L_MAP;
        if (n.StopUsed != s.StopUsed) return L_STOP;
        if (s.Pc == W_GONE && n.Pc != W_GONE) return L_ADD;
        if (NeedsRead(s) && s.Pc == n.Pc && s.SdkPc == n.SdkPc && s.Tear == n.Tear && (n.Parked || n.Delivered != s.Delivered)) return L_HID;
        if (s.Pc == W_READ && n.Pc != W_READ && s.Tear == 0)
            return n.Seen == s.Seen && n.Cache == s.Cache && n.CacheSeq == s.CacheSeq && n.LastDel == s.LastDel && n.Delivered == s.Delivered
                   && n.Parked == s.Parked ? L_READ0 : L_READ;
        return 0;
    }


    // HID class has no READ_REPORT at the driver and the device is up: the
    // next one is HID class's to send.
    static bool NeedsRead(in State s) => !s.Parked && s.Pc != W_GONE && s.Tear == 0;

    // What a paired v2.0.3 step must agree on besides its label: the
    // program and the outside events used so far, and after a program's own
    // step or a kill, where the program is. A frame's handoff, take-back and
    // SetEvent are each driver's own, so between two shared program steps
    // the two programs may stand at different counters.
    static int MatchKey(int label, in State n) =>
        n.P | (n.RecycleUsed ? 1 << 3 : 0) | (n.ReAddUsed ? 1 << 4 : 0) | (n.StopUsed ? 1 << 5 : 0) | (n.KillUsed ? 1 << 6 : 0)
        | (label is L_PROG or L_KILL ? (n.SdkPc << 7 | n.Done << 11 | 1 << 14) : 0);

    // Where in a frame's publication a kill lands: before the SetEvent (1)
    // or after the frame woke the worker (2). This driver's kill between
    // its exchange and its recheck with the frame still in the handoff, or
    // between its take-back and its SetEvent, is a kill before v2.0.3's
    // SetEvent: the program died before the step that would wake a waiting
    // worker (the same-order bar for kill windows, review SIG5c). With the
    // frame already taken, its wake has happened, as after a SetEvent.
    static int KillPhase(in State s) => s.SdkPc switch
    {
        S_CHECK or S_SIGNAL => 1,
        S_RECLAIM => s.Hand == s.SdkSeq ? 1 : 2,
        S_FRAMEDONE => 2,
        _ => 0,
    };

    internal sealed class Result
    {
        public long States;
        public long Finals;
        public readonly HashSet<ulong> Outcomes = new();
        public bool ZeroSent;
    }

    // The delivery counts and the late bits, without the zeroed sections.
    static readonly ulong OutMask = Enumerable.Range(0, 21).Where(x => x >= RelSlot || x % 6 != 0)
                                              .Aggregate(0UL, (m, x) => m | (3UL << (2 * x))) | (7UL << LateBit);

    // An outcome of this driver no worse than one of v2.0.3's: every frame
    // and release frame that one delivers this one delivers, every one this
    // one sends twice that one sends twice, and a program's frame goes out
    // after its release frames only where that one sends it so.
    internal static bool NoWorse(ulong mine, ulong theirs)
    {
        for (int slot = 0; slot < 21; slot++)
        {
            if (slot < RelSlot && slot % 6 == 0) continue;
            int m = Count(mine, slot), t = Count(theirs, slot);
            if (t >= 1 && m == 0) return false;
            if (m == 2 && t < 2) return false;
        }
        ulong late = (mine & ~theirs) >> LateBit & 7;
        return late == 0;
    }

    // Every frame and release frame this outcome sends, that one sends.
    internal static bool NoExtra(ulong mine, ulong theirs)
    {
        for (int slot = 0; slot < 21; slot++)
        {
            if (slot < RelSlot && slot % 6 == 0) continue;
            if (Count(mine, slot) >= 1 && Count(theirs, slot) == 0) return false;
        }
        return true;
    }

    internal static string Show(ulong v)
    {
        var parts = new List<string>();
        for (int x = 0; x < 18; x++) if (x % 6 != 0 && Count(v, x) > 0) parts.Add($"{x / 6 + 1}:{x % 6}x{Count(v, x)}");
        for (int q = 1; q <= 3; q++) if (Count(v, RelSlot + q - 1) > 0) parts.Add($"release{q}x{Count(v, RelSlot + q - 1)}");
        for (int p = 1; p <= 3; p++) if ((v >> (LateBit + p - 1) & 1) != 0) parts.Add($"{p}:after-its-release");
        return string.Join(" ", parts);
    }

    static int Count(ulong delivered, int slot) => (int)((delivered >> (2 * slot)) & 3);

    static ulong Deliver(ulong delivered, int slot) =>
        Count(delivered, slot) == 2 ? delivered : delivered + (1UL << (2 * slot));

    // Every outcome one driver reaches in a scenario, unpaired: for the
    // outcomes listing.
    internal static Result Explore(Scenario sc, Driver d, Rules r, int framesArg)
    {
        int frames = sc.FramesFor(framesArg);
        var start = Start();
        var seen = new HashSet<Key> { Pack(start) };
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
                res.Outcomes.Add(s.Delivered & OutMask);
                for (int p = 1; p <= 3; p++) if (Count(s.Delivered, Slot(p, 0)) > 0) res.ZeroSent = true;
                continue;
            }
            foreach (var n in next)
                if (seen.Add(Pack(n))) stack.Push(n);
        }
        return res;
    }

    internal sealed class AlignedResult
    {
        public long States, Finals, Unaligned, Sets, SetItems, V203States, Subsumed;
        public bool Canceled, OwnFails, TooLarge;
        public readonly HashSet<ulong> Worse = new();
        // Outcomes matched only by v2.0.3 runs that deliver fewer frames:
        // an extra frame, neither a loss nor a duplicate.
        public readonly HashSet<ulong> Extra = new();
        public string? ExtraExample;
        public List<State>? ExtraPath;
        public bool ZeroSent;
        public string? Example;
        public List<State>? Path;
        public readonly Dictionary<ulong, List<State>> Paths = new();
        public List<State>? UnalignedPath;
        public List<string>? PairedSide;
    }

    // The two time-driven steps a worker waits for, by side: HID class's
    // next READ_REPORT (bit 1) and the Phase 1 map (bit 2). Each is shared
    // while both sides wait for it, so HID class and the 200 ms poll are as
    // slow on both. When only one side waits, that side takes it before any
    // other step of its own.
    const int M_READ = 1, M_MAP = 2;

    static bool MapReady(in State s, Scenario sc, Driver d, Rules r, int frames, List<State> scratch)
    {
        if (s.Pc != W_P1 || s.OwnerSig || !s.Ev) return false;
        scratch.Clear();
        Steps(s, sc, d, r, frames, scratch);
        foreach (var n in scratch) if (Label(s, n) == L_MAP) return true;
        return false;
    }

    // v2.0.3's side of the pairing for one scenario: its states, numbered,
    // their steps, the sets of them the pairing reaches (sorted numbers),
    // the closure of a set under v2.0.3's own steps, the step from a set
    // through a shared event, and each set's final outcomes. A closure takes
    // the waits of this driver's side (M_READ, M_MAP): a v2.0.3 state that
    // waits for a step this driver's side does not wait for takes that step
    // and nothing else, and one that waits for a step this driver's side
    // also waits for takes it only with this driver.
    sealed class V203Side
    {
        readonly Scenario sc;
        readonly int frames;
        readonly Dictionary<Key, int> idOf = new();
        readonly List<Key> keyOf = new();
        readonly List<Info?> info = new();
        readonly List<int[]> sets = new();
        readonly List<ulong> sigs = new();
        readonly Dictionary<SetKey, int> ids = new();
        readonly Dictionary<(int, int), int> closed = new();
        readonly Dictionary<(int, int, int, int, int, int), int> follow = new();
        readonly Dictionary<int, ulong[]> finals = new();
        readonly Dictionary<int, int> waits = new();
        readonly List<State> buf = new();
        readonly HashSet<int> scratch = new();
        readonly Stack<int> work = new();
        public long SetItems;

        public V203Side(Scenario sc, int frames) { this.sc = sc; this.frames = frames; }
        public int SetCount => sets.Count;
        public int StateCount => keyOf.Count;

        readonly record struct Succ(int M, int Label, int Match, int Phase);
        // Waits: the time-driven steps the state waits for (M_READ, M_MAP).
        // Pending: a wake waits to be read (the input event set, or a wait
        // the input event ended).
        sealed record Info(Succ[] S, int Waits, bool Pending);

        readonly struct SetKey : IEquatable<SetKey>
        {
            public readonly int[] Items;
            readonly int hash;
            public SetKey(int[] items)
            {
                Items = items;
                var h = new HashCode();
                foreach (var x in items) h.Add(x);
                hash = h.ToHashCode();
            }
            public bool Equals(SetKey o) => hash == o.hash && Items.AsSpan().SequenceEqual(o.Items);
            public override bool Equals(object? o) => o is SetKey k && Equals(k);
            public override int GetHashCode() => hash;
        }

        int Id(Key k)
        {
            if (idOf.TryGetValue(k, out int id)) return id;
            id = keyOf.Count;
            idOf[k] = id;
            keyOf.Add(k);
            info.Add(null);
            return id;
        }

        Info Of(int id)
        {
            var a = info[id];
            if (a != null) return a;
            var v = Unpack(keyOf[id]);
            buf.Clear();
            Steps(v, sc, Driver.V203, This, frames, buf);
            var states = buf.ToArray();
            var s = new Succ[states.Length];
            int w = NeedsRead(v) ? M_READ : 0;
            for (int i = 0; i < states.Length; i++)
            {
                int l = Label(v, states[i]);
                if (l == L_READ0) l = L_READ;
                if (l == L_MAP) w |= M_MAP;
                s[i] = new Succ(Id(Pack(states[i])), l, MatchKey(l, states[i]), KillPhase(v));
            }
            a = new Info(s, w, v.Event || v.Pc is W_WOKE or W_READ);
            info[id] = a;
            return a;
        }

        int Intern(HashSet<int> h)
        {
            var arr = h.ToArray();
            Array.Sort(arr);
            var k = new SetKey(arr);
            if (ids.TryGetValue(k, out int id)) return id;
            id = sets.Count;
            sets.Add(arr);
            ulong sig = 0;
            foreach (var x in arr) sig |= 1UL << (int)((uint)(x * 0x9E3779B1) >> 26);
            sigs.Add(sig);
            SetItems += arr.Length;
            ids[k] = id;
            return id;
        }

        // The steps a v2.0.3 state takes as its own while this driver's side
        // waits for thisWaits: what it alone waits for, or else its worker's,
        // its program's private ones and its reads.
        static bool Own(Info x, Succ y, int thisWaits)
        {
            int alone = x.Waits & ~thisWaits;
            if (alone != 0)
                return ((alone & M_READ) != 0 && y.Label == L_HID) || ((alone & M_MAP) != 0 && y.Label == L_MAP);
            return y.Label is 0 or L_READ;
        }

        int CloseScratch(int thisWaits)
        {
            work.Clear();
            foreach (var x in scratch) work.Push(x);
            while (work.Count > 0)
            {
                var x = Of(work.Pop());
                foreach (var y in x.S)
                    if (Own(x, y, thisWaits) && scratch.Add(y.M)) work.Push(y.M);
            }
            int c = Intern(scratch);
            closed[(c, thisWaits)] = c;
            return c;
        }

        public int Close(int v, int thisWaits)
        {
            if (closed.TryGetValue((v, thisWaits), out int c)) return c;
            scratch.Clear();
            foreach (var x in sets[v]) scratch.Add(x);
            c = CloseScratch(thisWaits);
            closed[(v, thisWaits)] = c;
            return c;
        }

        public int Initial(int thisWaits)
        {
            scratch.Clear();
            scratch.Add(Id(Pack(Start())));
            return CloseScratch(thisWaits);
        }

        // The time-driven steps some state of set V waits for.
        public int Waits(int v)
        {
            if (waits.TryGetValue(v, out int w)) return w;
            w = 0;
            foreach (var x in sets[v]) w |= Of(x).Waits;
            waits[v] = w;
            return w;
        }

        // Set a is a subset of set b.
        public bool Subset(int a, int b)
        {
            if (a == b) return true;
            var x = sets[a]; var y = sets[b];
            if (x.Length > y.Length || (sigs[a] & ~sigs[b]) != 0) return false;
            int j = 0;
            foreach (var e in x)
            {
                while (j < y.Length && y[j] < e) j++;
                if (j == y.Length || y[j] != e) return false;
                j++;
            }
            return true;
        }

        // The set v2.0.3 reaches from set V through the shared event this
        // driver took, or -1 when no state of V can take it. Every state of
        // V that can take the event takes it. A state that cannot take a
        // READ_REPORT arrival, the Phase 1 map or the idle recycle lets the
        // time pass and goes on (the recycle's one chance under its program
        // spent): it was not waiting for it, or its worker was busy. A state
        // that alone waits for a step takes that step first, so the state
        // after it stands for it here. A read of this driver's pairs with a
        // read of every v2.0.3 run that has a wake to read on. A run with
        // none goes on without reading when this driver's read found nothing
        // new, or once the program was killed: this driver then read on a
        // wake v2.0.3 never had (a frame the dying program left in the
        // handoff, where v2.0.3's program died before its SetEvent).
        public int Follow(int v, int label, int match, int phase, int waitsBefore, int waitsAfter)
        {
            var key = (v, label, match, phase, waitsBefore, waitsAfter);
            if (follow.TryGetValue(key, out int r)) return r;
            scratch.Clear();
            int want = label == L_READ0 ? L_READ : label;
            foreach (var xi in sets[v])
            {
                var x = Of(xi);
                if ((x.Waits & ~waitsBefore) != 0) continue;
                bool took = false;
                foreach (var y in x.S)
                    if (y.Label == want && y.Match == match && (label != L_KILL || (y.Phase & phase) != 0))
                    {
                        scratch.Add(y.M);
                        took = true;
                    }
                if (!took && label is L_HID or L_MAP) scratch.Add(xi);
                if (!took && !x.Pending && (label == L_READ0 || (label == L_READ && (match & 1 << 6) != 0))) scratch.Add(xi);
                if (!took && label == L_RECYCLE)
                {
                    var u = Unpack(keyOf[xi]);
                    u.RecycleUsed = true;
                    scratch.Add(Id(Pack(u)));
                }
            }
            r = scratch.Count == 0 ? -1 : CloseScratch(waitsAfter);
            follow[key] = r;
            return r;
        }

        // The paired v2.0.3 set at a failing final state: its size, the
        // outcomes of its final states, and a few states that are not final
        // with the steps they can take.
        public List<string> Describe(int v)
        {
            var lines = new List<string>();
            var finalsOut = sets[v].Where(x => Of(x).S.Length == 0).Select(x => Show(keyOf[x].C & OutMask)).Distinct().ToList();
            lines.Add($"paired v2.0.3 set: {sets[v].Length} states, {finalsOut.Count} final outcome(s): " +
                      (finalsOut.Count == 0 ? "none" : string.Join(" | ", finalsOut.Select(o => "[" + o + "]"))));
            foreach (var xi in sets[v].Where(x => Of(x).S.Length > 0 && Of(x).S.All(y => y.Label is not (0 or L_READ))).Take(4))
            {
                var x = Unpack(keyOf[xi]);
                lines.Add($"  waits: worker {PcName(x.Pc)}, sdk {SdkName(x.SdkPc)}, parked {x.Parked}, event {x.Event}, seen {x.Seen}, seq {x.Seq}, " +
                          $"delivered [{Show(x.Delivered & OutMask)}], its steps: " +
                          string.Join(", ", Of(xi).S.Select(y => y.Label switch
                          {
                              L_PROG => "program", L_RECYCLE => "recycle", L_GOES => "goes", L_MAP => "map", L_STOP => "stop", L_ADD => "add",
                              L_KILL => "kill", L_HID => "READ_REPORT", L_READ => "read", _ => "own",
                          })));
            }
            return lines;
        }

        // 2: some final state of V matches with no frame this one sends that
        // it does not, 1: only with such an extra frame, 0: none matches.
        public int MatchClass(int v, ulong mine)
        {
            if (!Matches(v, mine)) return 0;
            foreach (var t in finals[v])
                if (NoWorse(mine, t) && NoExtra(mine, t)) return 2;
            return 1;
        }

        public bool Matches(int v, ulong mine)
        {
            if (!finals.TryGetValue(v, out var outs))
            {
                var o = new HashSet<ulong>();
                foreach (var x in sets[v])
                    if (Of(x).S.Length == 0) o.Add(keyOf[x].C & OutMask);
                outs = o.ToArray();
                finals[v] = outs;
            }
            foreach (var t in outs)
                if (NoWorse(mine, t)) return true;
            return false;
        }
    }

    // The order-aligned check. Each state of this driver's is paired with the
    // set of v2.0.3 states reachable through the same outside events, the
    // same time-driven steps and a read at each of this driver's reads
    // (Label), each driver taking any steps of its own in between. At a
    // final state of this driver's, some final state in that set must
    // deliver every frame this one does not lose, send twice every frame
    // this one sends twice, and send a program's frame after its release
    // frames wherever this one does. A shared step v2.0.3 cannot follow from
    // any state of the set is counted (Unaligned): this driver's run
    // continues, paired with no v2.0.3 run, so its final state fails.
    //
    // Pairs are pruned by subsumption. Every step of the pairing is
    // monotone in the v2.0.3 set: a larger set follows to a larger set and
    // matches whatever a smaller one matches. This driver's steps depend on
    // the set only through the time-driven steps some state of it waits for.
    // So a pair (t, V2) whose set holds the set V1 of a pair (t, V1) already
    // explored, with the same waits, fails only where (t, V1) fails, and it
    // is not explored. A pair paired with no v2.0.3 run covers every pair of
    // its state. The pruning keeps every failure the full exploration finds,
    // and reports each failing scenario with one witness.
    internal static AlignedResult ExploreAligned(Scenario sc, Rules r, int framesArg, bool trace = false, Func<bool>? stop = null, long maxStates = long.MaxValue)
    {
        int frames = sc.FramesFor(framesArg);
        var res = new AlignedResult();
        var v203 = new V203Side(sc, frames);
        var buf = new List<State>();
        var scratch = new List<State>();
        const int None = -1;

        int TWaits(in State t) => (NeedsRead(t) ? M_READ : 0) | (MapReady(t, sc, Driver.This, r, frames, scratch) ? M_MAP : 0);

        // The explored pairs: by this driver's state and the waits of the
        // set, the sets explored with it that no other of them holds.
        var covers = new Dictionary<(Key, int), List<int>>();
        var unpaired = new HashSet<Key>();
        bool Visit(Key k, int v, int tw)
        {
            if (unpaired.Contains(k)) { res.Subsumed++; return false; }
            if (v == None) { unpaired.Add(k); return true; }
            var key = (k, v203.Waits(v) & tw);
            if (!covers.TryGetValue(key, out var lst)) { covers[key] = new List<int> { v }; return true; }
            foreach (var v1 in lst) if (v203.Subset(v1, v)) { res.Subsumed++; return false; }
            lst.RemoveAll(v1 => v203.Subset(v, v1));
            lst.Add(v);
            return true;
        }

        var start = Start();
        var t0 = Pack(start);
        int v0 = v203.Initial(TWaits(start));
        Visit(t0, v0, TWaits(start));
        var parent = trace ? new Dictionary<(Key, int), (Key, int)>() : null;
        var stack = new Stack<(Key, int)>();
        stack.Push((t0, v0));
        while (stack.Count > 0)
        {
            if ((res.States & 0xFFFF) == 0 && stop != null && stop()) { res.Canceled = true; break; }
            if (res.States >= maxStates) { res.TooLarge = true; break; }
            var (tk, V) = stack.Pop();
            var t = Unpack(tk);
            res.States++;
            buf.Clear();
            Steps(t, sc, Driver.This, r, frames, buf);
            int tw = TWaits(t);
            // What this driver's side alone waits for, it takes first.
            int alone = V == None ? 0 : tw & ~v203.Waits(V);
            if (alone != 0)
                buf.RemoveAll(n => Label(t, n) is var l && !(((alone & M_READ) != 0 && l == L_HID) || ((alone & M_MAP) != 0 && l == L_MAP)));
            if (buf.Count == 0)
            {
                res.Finals++;
                ulong mine = t.Delivered & OutMask;
                for (int pr = 1; pr <= 3; pr++) if (Count(t.Delivered, Slot(pr, 0)) > 0) res.ZeroSent = true;
                int cls = V == None ? 0 : v203.MatchClass(V, mine);
                bool matched = cls != 0;
                if (cls == 1 && res.Extra.Add(mine) && res.ExtraExample == null)
                {
                    res.ExtraExample = Show(mine);
                    if (parent != null) res.ExtraPath = PathTo(parent, (tk, V));
                }
                if (!matched && res.Worse.Add(mine) && parent != null) res.Paths[mine] = PathTo(parent, (tk, V));
                if (!matched && res.Example == null)
                {
                    res.Example = Show(mine) + (V == None ? " (after a step v2.0.3 could not take)" : "");
                    if (parent != null) res.Path = PathTo(parent, (tk, V));
                    if (parent != null && V != None) res.PairedSide = v203.Describe(V);
                }
                continue;
            }
            foreach (var n in buf)
            {
                int V2 = V;
                int label = Label(t, n);
                int nw = TWaits(n);
                if (V != None)
                {
                    if (label != 0)
                        V2 = v203.Follow(V, label, MatchKey(label, n), label == L_KILL ? KillPhase(t) : 0, tw, nw);
                    else if (nw != tw)
                        V2 = v203.Close(V, nw);
                    if (V2 == None)
                    {
                        res.Unaligned++;
                        if (parent != null && res.UnalignedPath == null)
                        {
                            res.UnalignedPath = PathTo(parent, (tk, V));
                            res.UnalignedPath.Add(n);
                        }
                    }
                }
                var nk = Pack(n);
                if (Visit(nk, V2, nw)) { parent?.TryAdd((nk, V2), (tk, V)); stack.Push((nk, V2)); }
            }
        }
        res.Sets = v203.SetCount;
        res.SetItems = v203.SetItems;
        res.V203States = v203.StateCount;
        return res;
    }

    static List<State> PathTo(Dictionary<(Key, int), (Key, int)> parent, (Key, int) k)
    {
        var path = new List<State>();
        while (true) { path.Add(Unpack(k.Item1)); if (!parent.TryGetValue(k, out var pk)) break; k = pk; }
        path.Reverse();
        return path;
    }

    // One step of a witness, as the fields it changed.
    internal static string Describe(in State a, in State b)
    {
        var sb = new StringBuilder();
        object oa = a, ob = b;
        foreach (var f in typeof(State).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object? x = f.GetValue(oa), y = f.GetValue(ob);
            if (Equals(x, y)) continue;
            if (sb.Length > 0) sb.Append(", ");
            string Fmt(object? v) => f.Name == "Pc" ? PcName((int)v!) : f.Name == "SdkPc" ? SdkName((int)v!) : f.Name == "Delivered" ? "[" + Show((ulong)v!) + "]" : v?.ToString() ?? "";
            sb.Append(f.Name).Append(' ').Append(Fmt(x)).Append("->").Append(Fmt(y));
        }
        int l = Label(a, b);
        return (l == 0 ? "" : l == L_READ ? "[read, paired] " : l == L_READ0 ? "[read of nothing new, paired] " : l == L_HID ? "[READ_REPORT arrives] " : "[shared] ") + sb;
    }

    static string PcName(int pc) => pc switch
    {
        W_P1 => "P1", W_TOP => "TOP", W_LOOK => "TAKE", W_POLL => "POLL", W_BLOCK => "WAIT", W_READ => "READ", W_OWNER => "OWNER",
        W_GONE => "GONE", W_COUNT => "COUNT", W_WOKE => "WOKE", W_OG_TAKE => "OG-TAKE", W_OG_LOOK => "OG-LOOK", W_OG_END => "OG-END",
        W_OG_ACK => "OG-ACK", W_OG_SIG => "OG-SIGNAL", W_EXITED => "EXITED", _ => pc.ToString(),
    };

    static string SdkName(int pc) => pc switch
    {
        S_CLAIM => "claim", S_START => "start", S_WRITE => "store", S_CHECK => "check", S_RECLAIM => "take-back", S_SIGNAL => "SetEvent",
        S_EXIT => "exit", S_NEXT => "next", S_DONE => "done", S_FRAMEDONE => "frame-done", _ => pc.ToString(),
    };

    static void Steps(in State s, Scenario sc, Driver d, Rules r, int frames, List<State> next)
    {
        bool gating = d == Driver.This && s.SigMapped;
        var prog = sc.Programs[s.P - 1];
        bool deviceUp = s.Pc != W_GONE && s.Tear == 0;

        // The programs.
        switch (s.SdkPc)
        {
            case S_CLAIM:
                next.Add(Collect(s with { PipeOpen = prog.Pipe, SdkPc = S_START }, sc, prog));
                break;
            case S_START:
            {
                // The input section and its event, zeroed, the
                // acknowledgement, zeroed, the signal section, the start.
                int left = s.InSec ? s.Seq : 0;
                var n = s with { Holds = true, InSec = true, Seq = 0, Prog = 0, Idx = 0, Ev = true, Event = s.Ev && s.Event, SdkPc = S_WRITE };
                if (prog.MakesAck) n = n with { Ack = 0, AckBit = false, AckEnded = false };
                if (prog.Is21)
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
                if (seq == 0 && prog.Is21 && r.SdkSkipsZero) seq = 2;
                next.Add(s with { Seq = seq, Prog = s.P, Idx = s.Done + 1, SdkSeq = seq, SdkPc = S_CHECK });
                break;
            }
            case S_CHECK:
                // A 2.1.0 SDK with the capability hands the frame over.
                if (r.Round6 && prog.Is21 && s.Cap && s.Hand != 0 && s.HandFinal)
                    // A pending final value becomes the new frame's, in one
                    // compare-exchange: its wake carries this frame.
                    next.Add(AfterFrame(s with { Hand = s.SdkSeq, HandFinal = true }));
                else
                    next.Add(prog.Is21 && s.Cap ? s with { Hand = s.SdkSeq, HandFinal = false, Replaced = r.NoTakeBackOverUntaken && s.Hand != 0, SdkPc = S_RECLAIM }
                                                : s with { SdkPc = S_SIGNAL });
                break;
            case S_RECLAIM:
                if (r.Round6)
                {
                    // The recheck: provisional to final with no waiter and
                    // the gate open, taken back and signaled otherwise. A
                    // take that dropped the provisional value leaves the
                    // signal to this step.
                    bool mine = s.Hand == s.SdkSeq && !s.HandFinal;
                    if (s.Cap && s.Waiters == 0 && mine) next.Add(AfterFrame(s with { HandFinal = true }));
                    else next.Add(s with { Hand = mine ? 0 : s.Hand, HandFinal = mine ? false : s.HandFinal, SdkPc = S_SIGNAL });
                    break;
                }
                // The capability again, then the count.
                if (s.Cap && s.Waiters == 0) next.Add(AfterFrame(s) with { Replaced = false });
                else if (!r.Reclaim || s.Replaced) next.Add(s with { Replaced = false, SdkPc = S_SIGNAL });
                else if (s.Hand == s.SdkSeq) next.Add(s with { Hand = 0, SdkPc = S_SIGNAL });
                else next.Add(AfterFrame(s));
                break;
            case S_SIGNAL:
            {
                // An auto-reset event: a blocked wait returns with it, or it
                // stays set. A wait the stop event or the owner pipe already
                // ended is not waiting any more. The woken worker's count
                // drops as its wait returns.
                var n = s.Pc == W_BLOCK && s.Blocked && !s.StopSig && !s.OwnerSig
                    ? s with { Blocked = false, Pc = W_WOKE, Waiters = s.Counted ? s.Waiters - 1 : s.Waiters, Counted = false }
                    : s with { Event = true };
                next.Add(AfterFrame(n));
                break;
            }
            case S_EXIT:
                next.Add(Collect(Ended(s, sc), sc, prog));
                break;
            case S_FRAMEDONE:
            {
                // A keyboard's release frame goes into the acknowledgement
                // after its first frame (HMController.WriteKbmFrame).
                var n = s with { SdkPc = s.Done == frames ? S_EXIT : S_WRITE };
                if (prog.Rel && s.Done == 1) n = n with { Ack = s.P };
                next.Add(n);
                break;
            }
            case S_NEXT:
                if (sc.Early || s.EndSeen)
                    next.Add(s with { P = s.P + 1, Done = 0, SdkPc = S_CLAIM });
                break;
        }

        // A kill inside a frame's publication: its threads go, then its
        // handles, the pipe among them.
        if (sc.Kill == s.P && !s.KillUsed && s.SdkPc is S_CHECK or S_RECLAIM or S_SIGNAL or S_FRAMEDONE)
            next.Add(Collect(Ended(s, sc) with { Done = s.Idx, KillUsed = true }, sc, prog));

        // A foreign stop: another program's sweep sets the index's stop event.
        if (sc.Stop == s.P && !s.StopUsed && deviceUp)
            next.Add(s with { StopSig = true, StopUsed = true });

        // The device goes: the queues are purged, then ReleaseIndexObjects
        // sets TearingDown, then the stop event.
        if (sc.ReAdd == s.P && !s.ReAddUsed)
            next.Add(s with { Tear = 1, ReAddUsed = true, Parked = false });
        if (s.Tear == 1)
            next.Add(s with { Tear = 2, StopSig = true });

        // HID class: a READ_REPORT when none is at the driver. A release frame
        // goes first, then the cached frame when it is newer than the last
        // delivered: by inequality in this driver (driver.c, IOCTL_HID_READ_
        // REPORT), by greater-than in v2.0.3 (533ca4a4 driver.c:2605).
        if (deviceUp && !s.Parked)
        {
            bool newer = d == Driver.This && r.LateReadNotEqual ? s.CacheSeq != s.LastDel : s.CacheSeq > s.LastDel;
            if (s.RelQ != 0) next.Add(DeliverRelease(s));
            else if (s.Cache != 0 && newer) next.Add(DeliverFrame(s, s.Cache - 1) with { LastDel = s.CacheSeq });
            else next.Add(s with { Parked = true });
        }

        // The worker.
        bool recycleNow = !s.RecycleUsed && sc.Recycle == s.P;
        switch (s.Pc)
        {
            case W_P1:
                if (s.OwnerSig)
                    next.Add(OwnerGone(s with { OwnerSig = false }, d, r));
                else if (s.Ev)
                {
                    // The map: the input section, the acknowledgement (its
                    // release bit set again while the watch is open), the
                    // signal section and its capability.
                    var n = s with { Mapped = true, SigMapped = s.SigMapped || s.Sig, AckBit = d == Driver.This && s.Watch, Pc = W_TOP };
                    if (d == Driver.This && n.SigMapped) n = n with { Cap = true };
                    next.Add(Connect(n, prog, d));
                }
                // The 200 ms boot wait returns: at teardown the worker ends,
                // and a foreign stop is absorbed.
                if (s.Tear != 0) next.Add(s with { Pc = W_EXITED });
                else if (s.StopSig) next.Add(s with { StopSig = false });
                break;
            case W_TOP:
                if (gating)
                    next.Add(r.CountFirst ? s with { Waiters = s.Waiters + 1, Counted = true, Took = 0, Pc = W_LOOK } : s with { Took = 0, Pc = W_LOOK });
                else
                    next.Add(s with { Took = 0, Pc = W_BLOCK });
                break;
            case W_LOOK:
            {
                bool took = r.TakeBeforeWait && s.Hand != 0 && (s.Hand != s.Seen || !r.DropSentAtTake) && (!r.Round6 || s.HandFinal);
                var n = took ? s with { Hand = 0, Took = s.Hand, Waiters = s.Counted ? s.Waiters - 1 : s.Waiters, Counted = false, Pc = W_POLL }
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
            {
                // The wait with a zero timeout after a take: the stop event
                // first, then the input event, then the owner pipe, and a
                // timeout stands for the input event. On the owner pipe the
                // worker reads, then waits again: the pipe's event stays
                // set, and an input event set meanwhile comes first. On the
                // stop event, and at teardown, the taken frame goes back to
                // the input event as a SetEvent before the worker leaves its
                // loop.
                // A taken frame left unread goes back to the input event
                // only while no next program has started (b17050c1), and
                // always when the poll consumed the input event.
                bool alone = !r.BackOnlyAlone || !NextStarted(s) || (r.TakeReadSendsTaken && s.Seq == s.Took);
                var back = r.SignalTakenOnExit && alone ? s with { Event = true, Took = 0 } : s with { Took = 0 };
                if (s.StopSig) next.Add(StopReturned(back, sc, d, r, prog));
                else if (s.Event)
                    next.Add(s.Tear != 0 ? (r.SignalTakenOnExit ? s : s with { Event = false }) with { Took = 0, Pc = W_EXITED }
                                         : s with { Event = false, Took = 0, Pc = W_READ, AfterOwner = false });
                else if (s.OwnerSig && !r.WaitAgainAfterOwner) next.Add(s with { OwnerSig = false, Pc = W_READ, AfterOwner = true, NoRetry = true });
                else if (s.OwnerSig) next.Add(s with { Pc = W_READ, AfterOwner = false, NoRetry = true });
                else next.Add(s.Tear != 0 ? back with { Pc = W_EXITED } : s with { Pc = W_READ, AfterOwner = false });
                break;
            }
            case W_BLOCK:
                if (!s.Blocked)
                {
                    // The wait finds what is already set, the stop event
                    // first, or blocks.
                    var u = s with { Waiters = s.Counted ? s.Waiters - 1 : s.Waiters, Counted = false };
                    if (s.StopSig) next.Add(StopReturned(u, sc, d, r, prog));
                    else if (s.Event) next.Add(u with { Event = false, Pc = s.Tear != 0 ? W_EXITED : W_READ, AfterOwner = false });
                    else if (s.OwnerSig) next.Add(s.Tear != 0 ? u with { Pc = W_EXITED } : OwnerGone(u with { OwnerSig = false }, d, r));
                    else next.Add(s with { Blocked = true });
                }
                else
                {
                    // A blocked wait returns with whichever event was set
                    // first, or times out.
                    var u = s with { Blocked = false, Waiters = s.Counted ? s.Waiters - 1 : s.Waiters, Counted = false };
                    if (s.StopSig) next.Add(StopReturned(u, sc, d, r, prog));
                    if (s.OwnerSig) next.Add(s.Tear != 0 ? u with { Pc = W_EXITED } : OwnerGone(u with { OwnerSig = false }, d, r));
                    if (!s.StopSig && !s.OwnerSig)
                    {
                        if (s.Tear != 0) next.Add(u with { Pc = W_EXITED });
                        else if (recycleNow) next.Add(Recycle(u, sc, d, r, prog, true));
                        // A timeout takes the handoff again, which matters
                        // only when a frame waits there under the count: the
                        // program died between its exchange and its
                        // take-back.
                        else if (gating && s.Hand != 0 && s.Hand != s.Seen) next.Add(u with { Pc = W_TOP });
                    }
                }
                break;
            case W_WOKE:
                // A SetEvent ended the wait: TearingDown is checked first.
                next.Add(s with { Pc = s.Tear != 0 ? W_EXITED : W_READ, AfterOwner = false });
                break;
            case W_READ:
            {
                // A read the input event (or a take standing for it)
                // answered tries the watch again after OwnerGone
                // (WatchRetryNext). Every 256th frame does too in both
                // drivers, which a model run never reaches.
                var n = Read(s with { Pc = s.AfterOwner ? W_OWNER : W_TOP, AfterOwner = false, NoRetry = false, Took = 0 }, d, r, out bool fresh, s.Took, NextStarted(s), sc.Wrap);
                if (n.Pc == W_TOP && !s.NoRetry && s.RetryNext)
                    n = (CanConnect(n, prog) ? Connect(n, prog, d) : n) with { RetryNext = false };
                next.Add(n);
                // 250 stale wakeups in a row end in a recycle.
                if (!fresh && recycleNow && n.Pc == W_TOP) next.Add(Recycle(n, sc, d, r, prog, true));
                break;
            }
            case W_OWNER:
                next.Add(OwnerGone(s, d, r));
                break;
            case W_OG_TAKE:
                // OwnerGone's take, while the worker holds the input event.
                // Round 6: a provisional value is dropped.
                next.Add(s with { Handed = r.Round6 && !s.HandFinal ? 0 : s.Hand, Hand = 0, Pc = W_OG_LOOK });
                break;
            case W_OG_LOOK:
            {
                // A frame taken is owed when it was not sent, the section
                // still holds it, and a program has started since the watch
                // connected (the acknowledgement's release bit is clear): a
                // frame of the program that ended is dropped. Then the watch
                // closes and the last SeqNo goes.
                bool owed = s.Handed != 0 && (s.Handed != s.Seen || !r.DropSent) && (s.Mapped || !r.OwedOnlyMapped) && s.Ev
                            && (!s.AckBit || !r.OwedOnlyNextProgram) && (s.Seq == s.Handed || !r.OwedOnlyHeld);
                next.Add(s with { Owed = owed, Handed = 0, Watch = false, Seen = 0, RetryNext = d == Driver.This && r.RetryAfterOwnerGone, Pc = W_OG_END });
                break;
            }
            case W_OG_END:
                // The watch-ended marker, then RecordOwnerGone.
                next.Add(s with { AckEnded = true, Pc = W_OG_ACK });
                break;
            case W_OG_ACK:
            {
                // The release frames the acknowledgement holds are queued
                // and the delivered gate is reset, under InputLock, then a
                // parked read takes a release frame.
                var n = s with { RelQ = s.Ack, RelFor = s.Ack != 0 ? s.WatchP : 0, CacheSeq = 0, LastDel = 0, Pc = W_OG_SIG };
                if (n.RelQ != 0 && n.Parked) n = DeliverRelease(n) with { Parked = false };
                next.Add(n);
                break;
            }
            case W_OG_SIG:
                next.Add(s with { Event = s.Event || s.Owed, Owed = false, EndSeen = true, WatchP = 0, Pc = s.Mapped ? W_TOP : W_P1 });
                break;
            case W_EXITED:
                // ReleaseIndexObjects after the join: the stop event is set by
                // then.
                if (s.Tear == 2) next.Add(Release(s, sc, d, r, prog));
                break;
            case W_GONE:
                // EvtDeviceAdd: a context with nothing read, nothing cached,
                // and the stop event reset at its start.
                next.Add(s with { Pc = W_P1, Seen = 0, Cache = 0, CacheSeq = 0, LastDel = 0, Parked = false, StopSig = false, Tear = 0, RetryNext = false });
                break;
        }
    }

    // A program's end, by exit or kill: the owner pipe closes after the last
    // SetEvent, and with it what the program held.
    static State Ended(in State s, Scenario sc) =>
        s with { OwnerSig = s.OwnerSig || (s.Watch && s.WatchP == s.P), PipeOpen = false, Holds = false, EndSeen = false,
                 SdkPc = s.P < sc.Programs.Length ? S_NEXT : S_DONE };

    // ProcessSharedInput. v2.0.3 sends a zeroed section as a frame.
    // NextProgramStarted: the watch is open and the acknowledgement's release
    // bit is clear, so a next program zeroed it.
    static bool NextStarted(in State s) => s.Watch && !s.AckBit;

    // NextFrameSeq: the SeqNo a 2.1.0 program writes after this one, 0
    // stepped over at the wrap (the wrap scenarios wrap at 16).
    static int NextFrameSeq(int seq, int wrap)
    {
        int n = seq + 2;
        if (wrap > 0) n %= wrap;
        return n == 0 ? 2 : n;
    }

    static State Read(State s, Driver d, Rules r, out bool fresh, int taken = 0, bool nextStarted = false, int wrap = 0)
    {
        fresh = false;
        if (s.Seq == s.Seen) return s;
        if (s.Prog == 0 && d == Driver.This && r.SkipZero)
        {
            // A zeroed section is SeqNo 0: no news when SeenSeqNo is 0.
            // Otherwise SeenSeqNo becomes 0 and the cache takes the all-zero
            // report at SeqNo 0, as v2.0.3's read of it did, and no
            // READ_REPORT completes with it (161d29db). Taken away (D1), the
            // read returns before either changes (60bf16b0).
            if (!r.ZeroForgets || s.Seen == 0) return s;
            fresh = true;
            return s with { Seen = 0, Cache = Slot(s.P, 0) + 1, CacheSeq = 0 };
        }
        // A read a take alone woke sends only the frame taken or its
        // program's next one, and nothing once a next program started
        // (b17050c1).
        if (d == Driver.This && taken != 0 && s.Prog != 0 && r.Round6 && s.Seq != taken) return s;
        if (d == Driver.This && taken != 0 && s.Prog != 0
            && ((r.TakeReadNoNext && nextStarted && (s.Seq != taken || !r.TakeReadSendsTaken))
                || (r.TakeReadNextSeq && s.Seq != taken && s.Seq != NextFrameSeq(taken, wrap))))
            return s;
        int slot = s.Prog == 0 ? Slot(s.P, 0) : Slot(s.Prog, s.Idx);
        fresh = s.Seq != s.Seen;
        if (!fresh) return s;
        // The frame goes in the cache with its SeqNo, and a parked read
        // completes with it at once.
        var n = s with { Seen = s.Seq, Cache = slot + 1, CacheSeq = s.Seq };
        return n.Parked ? DeliverFrame(n, slot) with { LastDel = s.Seq, Parked = false } : n;
    }

    static State DeliverFrame(in State s, int slot)
    {
        ulong delivered = Deliver(s.Delivered, slot);
        int p = slot / 6 + 1;
        if (slot % 6 != 0 && (s.RelDone >> (p - 1) & 1) != 0) delivered |= 1UL << (LateBit + p - 1);
        return s with { Delivered = delivered };
    }

    static State DeliverRelease(in State s) =>
        s with { Delivered = Deliver(s.Delivered, RelSlot + s.RelQ - 1), RelDone = s.RelFor > 0 ? s.RelDone | 1 << (s.RelFor - 1) : s.RelDone, RelQ = 0, RelFor = 0 };

    // OwnerGone starts: this driver takes the handoff first, while it holds
    // the input event.
    static State OwnerGone(in State s, Driver d, Rules r) =>
        d == Driver.This && s.SigMapped && r.TakeAtOwnerGone && (s.Mapped || !r.OwedOnlyMapped)
            ? s with { Pc = W_OG_TAKE } : s with { Pc = W_OG_LOOK };

    // The wait returned the stop event: at teardown the worker ends;
    // otherwise the event is reset and the worker recycles.
    static State StopReturned(in State s, Scenario sc, Driver d, Rules r, Prog prog) =>
        s.Tear != 0 ? s with { Pc = W_EXITED } : Recycle(s with { StopSig = false }, sc, d, r, prog, false);

    // TryOpenOwnerWatch, at a map or after a frame: only beside an
    // acknowledgement whose ended marker is clear, and the release bit is
    // set as it connects. Only this driver's OwnerGone reads that bit.
    static bool CanConnect(in State s, Prog prog) => !s.Watch && s.PipeOpen && prog.Pipe && !s.AckEnded;

    static State Connect(in State s, Prog prog, Driver d) =>
        CanConnect(s, prog) ? s with { Watch = true, WatchP = s.P, AckBit = d == Driver.This } : s;

    // CloseGate: the capability goes, and a frame handed over and not sent
    // becomes a SetEvent on the input event, while that exists.
    static State CloseGate(in State s, Driver d, Rules r)
    {
        if (d != Driver.This || !s.SigMapped || !r.CloseGate) return s;
        var n = s with { Cap = false, Hand = 0 };
        if (s.Hand != 0 && (!r.Round6 || s.HandFinal) && (s.Hand != s.Seen || r.CloseSignalsSent) && s.Ev) n = n with { Event = true };
        return n;
    }

    // A recycle closes the input section and its event and goes to Phase 1.
    // The idle and stale recycles are outside events; a foreign stop's is
    // the worker's answer to one.
    static State Recycle(State n, Scenario sc, Driver d, Rules r, Prog prog, bool idle)
    {
        n = CloseGate(n, d, r);
        n = n with { Mapped = false, Pc = W_P1, CacheSeq = 0, LastDel = 0, Took = 0, EndSeen = n.EndSeen || !n.Holds };
        if (idle) n = n with { RecycleUsed = true };
        if (d == Driver.V203 || r.ForgetAtRecycle) n = n with { Seen = 0 };
        return Collect(n, sc, prog);
    }

    // ReleaseIndexObjects after the worker ended: the gate closes, and
    // everything the context held goes with it.
    static State Release(State s, Scenario sc, Driver d, Rules r, Prog prog)
    {
        if (r.ClearCapAtGo) s = CloseGate(s, d, r);
        var n = s with { Pc = W_GONE, Mapped = false, SigMapped = false, Watch = false, OwnerSig = false, WatchP = 0, Counted = false, Blocked = false,
                         Owed = false, Cache = 0, CacheSeq = 0, LastDel = 0, Parked = false, RelQ = 0, RelFor = 0, Took = 0, EndSeen = s.EndSeen || !s.Holds };
        return Collect(n, sc, prog);
    }

    // Objects nothing holds go, with what they held. The companion holds a
    // section once it exists.
    static State Collect(State s, Scenario sc, Prog prog)
    {
        bool progSig = s.Holds && prog.Is21;
        if (s.InSec && !(s.Holds || s.Mapped || sc.HoldIn)) s = s with { InSec = false, Seq = 0, Prog = 0, Idx = 0 };
        if (s.Ev && !(s.Holds || s.Mapped)) s = s with { Ev = false, Event = false };
        if (s.Sig && !(progSig || s.SigMapped || sc.HoldSig)) s = s with { Sig = false, Cap = false, Waiters = 0, Hand = 0 };
        return s;
    }

    static State AfterFrame(in State s) => s with { Done = s.Done + 1, SdkPc = S_FRAMEDONE };

    // SharedMemoryIO.EvenAbove(left, 0): the SeqNo left, taken to the even
    // one an odd one becomes, stepping over 0.
    static int EvenAbove(int left)
    {
        uint m = (uint)left;
        if ((m & 1) != 0) { m++; if (m == 0) m = 2; }
        return (int)m;
    }
}
