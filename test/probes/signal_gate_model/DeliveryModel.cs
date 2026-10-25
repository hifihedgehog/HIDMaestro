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
        public bool DropSentAtTake { get; init; } = true; // a take of the frame last sent wakes no read
        public bool WaitAgainAfterOwner { get; init; } = true; // a taken frame read on the pipe's close waits again before OwnerGone
        public bool SignalTakenOnExit { get; init; } = true;   // a frame taken and not read when the worker leaves its loop is signaled
        public bool OwedOnlyNextProgram { get; init; } = true; // OwnerGone signals only the next program's frame, not the ended one's
        public bool RetryAfterOwnerGone { get; init; } = true; // the watch is tried again after the first frame read after OwnerGone
        public bool LateReadNotEqual { get; init; } = true;   // a late READ_REPORT takes any cached SeqNo but the last delivered
        // Candidate changes, off in this driver (Candidates below).
        public bool ZeroForgets { get; init; }             // a zeroed section forgets SeenSeqNo and SharedMemSeqNo and sends nothing
        public bool CloseSignalsSent { get; init; }        // CloseGate signals a handed frame equal to the last one sent too
        public bool StaleTakeOnExit { get; init; }         // a take of the frame last sent counts for the SetEvent as the worker leaves its loop
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
        This with { Name = "a take of the frame last sent read", DropSentAtTake = false },
        This with { Name = "OwnerGone straight after a taken frame read on the pipe's close", WaitAgainAfterOwner = false },
        This with { Name = "a taken frame dropped as the worker leaves its loop (B1)", SignalTakenOnExit = false },
        This with { Name = "OwnerGone signals the ended program's own frame (FIX-1)", OwedOnlyNextProgram = false },
    };

    // Candidate changes for the orders this driver loses (the model section
    // of docs/design/signal-gate.md), each run in place of this driver's
    // rules by the check command's rule c0, c1 or c2.
    internal static readonly Rules[] Candidates =
    {
        This with { Name = "CloseGate and a stale take signal the frame last sent", CloseSignalsSent = true, StaleTakeOnExit = true },
        This with { Name = "no take-back over a frame not taken", NoTakeBackOverUntaken = true },
        This with { Name = "CloseGate, a stale take and no take-back over a frame not taken", CloseSignalsSent = true, StaleTakeOnExit = true, NoTakeBackOverUntaken = true },
        This with { Name = "a zeroed section forgets the last SeqNo, and a late read takes only a greater SeqNo", ZeroForgets = true, LateReadNotEqual = false },
        This with { Name = "all of them", CloseSignalsSent = true, StaleTakeOnExit = true, NoTakeBackOverUntaken = true, ZeroForgets = true, LateReadNotEqual = false },
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
    // StaleTook and Replaced serve the candidate changes only.
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
        public bool AckBit, AckEnded, StaleTook, Replaced, RetryNext, NoRetry;
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
        b.Put(s.AckBit); b.Put(s.AckEnded); b.Put(s.StaleTook); b.Put(s.Replaced); b.Put(s.RetryNext); b.Put(s.NoRetry);
        return new Key(a.W, b.W, s.Delivered);
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
        s.AckBit = b.Bit(); s.AckEnded = b.Bit(); s.StaleTook = b.Bit(); s.Replaced = b.Bit(); s.RetryNext = b.Bit(); s.NoRetry = b.Bit();
        s.Delivered = k.C;
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
    const int L_PROG = 1, L_RECYCLE = 2, L_GOES = 3, L_MAP = 4, L_STOP = 5, L_ADD = 6, L_KILL = 7, L_HID = 8, L_READ = 9;

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
        if (s.Pc == W_READ) return L_READ;
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

    // Where in a frame's publication a kill lands. Before the SetEvent (1)
    // or after it (2). This driver's kill between the exchange and the
    // take-back pairs with either: the frame is in the handoff, as a
    // program dying at v2.0.3's SetEvent left it signaled or not.
    static int KillPhase(int sdkPc) => sdkPc switch { S_CHECK or S_SIGNAL => 1, S_FRAMEDONE => 2, S_RECLAIM => 3, _ => 0 };

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
        public long States, Finals, Unaligned, Sets, V203States, Excused;
        public bool Canceled, OwnFails;
        public readonly HashSet<ulong> Worse = new();
        public bool ZeroSent;
        public string? Example;
        public List<State>? Path;
        public List<State>? UnalignedPath;
    }

    // v2.0.3's side of the pairing for one scenario: its steps from each
    // state, the sets of its states the pairing reaches, each set's closure
    // under v2.0.3's own steps, the step from a set through a shared event,
    // and each set's final outcomes.
    //
    // HID class: a READ_REPORT arrival is shared while both sides wait for
    // one, so HID class is as slow on both. When only one side completed a
    // read, that side's HID class sends its next one before any other step
    // of that side. The closure takes a mode for this: whether this
    // driver's side waits for a READ_REPORT. Without it, a v2.0.3 state that
    // waits for one takes only its arrival. With it, the arrival waits for
    // this driver's.
    sealed class V203Side
    {
        readonly Scenario sc;
        readonly int frames;
        readonly Dictionary<Key, Info> info = new();
        readonly List<Key[]> sets = new();
        readonly Dictionary<SetKey, int> ids = new();
        readonly Dictionary<(int, bool), int> closed = new();
        readonly Dictionary<(int, int, int, int, bool, bool), (int, bool)> follow = new();
        readonly Dictionary<int, ulong[]> finals = new();
        readonly Dictionary<int, bool> anyNeeds = new();
        readonly List<State> buf = new();
        public long Excused;

        public V203Side(Scenario sc, int frames) { this.sc = sc; this.frames = frames; }
        public int SetCount => sets.Count;
        public int StateCount => info.Count;

        readonly record struct Succ(Key M, int Label, int Match, int Phase);
        sealed record Info(Succ[] S, bool Needs);

        readonly struct SetKey : IEquatable<SetKey>
        {
            public readonly Key[] Items;
            readonly int hash;
            public SetKey(Key[] items)
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

        Info Of(Key k)
        {
            if (info.TryGetValue(k, out var a)) return a;
            var v = Unpack(k);
            buf.Clear();
            Steps(v, sc, Driver.V203, This, frames, buf);
            var s = new Succ[buf.Count];
            for (int i = 0; i < buf.Count; i++)
            {
                int l = Label(v, buf[i]);
                s[i] = new Succ(Pack(buf[i]), l, MatchKey(l, buf[i]), KillPhase(v.SdkPc));
            }
            a = new Info(s, NeedsRead(v));
            info[k] = a;
            return a;
        }

        int Intern(HashSet<Key> h)
        {
            var arr = h.ToArray();
            Array.Sort(arr);
            var k = new SetKey(arr);
            if (ids.TryGetValue(k, out int id)) return id;
            id = sets.Count;
            sets.Add(arr);
            ids[k] = id;
            return id;
        }

        // v2.0.3's own steps from a set, to a fixed point: its worker's, its
        // program's private ones, and its reads. thisNeeds: this driver's
        // side waits for a READ_REPORT.
        public int Close(int seed, bool thisNeeds)
        {
            if (closed.TryGetValue((seed, thisNeeds), out int c)) return c;
            var all = new HashSet<Key>(sets[seed]);
            var work = new Stack<Key>(sets[seed]);
            while (work.Count > 0)
            {
                var x = Of(work.Pop());
                bool prompt = !thisNeeds && x.Needs;
                foreach (var y in x.S)
                    if ((prompt ? y.Label == L_HID : y.Label is 0 or L_READ) && all.Add(y.M)) work.Push(y.M);
            }
            c = Intern(all);
            closed[(seed, thisNeeds)] = c;
            closed[(c, thisNeeds)] = c;
            return c;
        }

        public int Initial(bool thisNeeds) => Close(Intern(new HashSet<Key> { Pack(Start()) }), thisNeeds);

        // Some state of set V waits for a READ_REPORT.
        public bool AnyNeeds(int v)
        {
            if (anyNeeds.TryGetValue(v, out bool b)) return b;
            b = sets[v].Any(x => Of(x).Needs);
            anyNeeds[v] = b;
            return b;
        }

        // The set v2.0.3 reaches from set V through the shared event this
        // driver took, or -1 when no state of V can take it.
        public int Follow(int v, int label, int match, int phase, bool needsBefore, bool needsAfter)
        {
            var key = (v, label, match, phase, needsBefore, needsAfter);
            if (follow.TryGetValue(key, out var hit)) { if (hit.Item2) Excused++; return hit.Item1; }
            bool excused = false;
            var h = new HashSet<Key>();
            foreach (var xk in sets[v])
            {
                var x = Of(xk);
                // A state whose HID class owes a READ_REPORT sends it first.
                if (!needsBefore && x.Needs) continue;
                if (label == L_HID && !x.Needs) { h.Add(xk); continue; }
                foreach (var y in x.S)
                    if (y.Label == label && y.Match == match && (label != L_KILL || (y.Phase & phase) != 0))
                        h.Add(y.M);
            }
            // The idle or stale recycle and the Phase 1 map come from time
            // passing, and happen only to a worker in the state for them:
            // idle in its wait, stale after a read, or in Phase 1 with the
            // event there. Every v2.0.3 run that can be in that state takes
            // the step with this driver, so v2.0.3 is as prompt. When no
            // v2.0.3 run can, its worker is busy in all of them: the time
            // passes and the runs go on, the recycle's one chance under its
            // program spent (counted in Excused).
            if (h.Count == 0 && label is L_RECYCLE or L_MAP)
            {
                Excused++;
                excused = true;
                foreach (var xk in sets[v])
                {
                    if (!needsBefore && Of(xk).Needs) continue;
                    var u = Unpack(xk);
                    if (label == L_RECYCLE) u.RecycleUsed = true;
                    h.Add(Pack(u));
                }
            }
            int r = h.Count == 0 ? -1 : Close(Intern(h), needsAfter);
            follow[key] = (r, excused);
            return r;
        }

        public bool Matches(int v, ulong mine)
        {
            if (!finals.TryGetValue(v, out var outs))
            {
                var o = new HashSet<ulong>();
                foreach (var x in sets[v])
                    if (Of(x).S.Length == 0) o.Add(x.C & OutMask);
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
    // same READ_REPORT arrivals and a read at each of this driver's reads
    // (Label), each driver taking any steps of its own in between. At a
    // final state of this driver's, some final state in that set must
    // deliver every frame this one does not lose, send twice every frame
    // this one sends twice, and send a program's frame after its release
    // frames wherever this one does. A shared step v2.0.3 cannot follow from
    // any state of the set is counted (Unaligned): this driver's run
    // continues, paired with no v2.0.3 run, so its final state fails.
    internal static AlignedResult ExploreAligned(Scenario sc, Rules r, int framesArg, bool trace = false, Func<bool>? stop = null)
    {
        int frames = sc.FramesFor(framesArg);
        var res = new AlignedResult();
        var v203 = new V203Side(sc, frames);
        var buf = new List<State>();
        const int None = -1;

        var t0 = Pack(Start());
        int v0 = v203.Initial(NeedsRead(Start()));
        var seen = new HashSet<(Key, int)> { (t0, v0) };
        var parent = trace ? new Dictionary<(Key, int), (Key, int)>() : null;
        var stack = new Stack<(Key, int)>();
        stack.Push((t0, v0));
        while (stack.Count > 0)
        {
            if ((res.States & 0xFFFF) == 0 && stop != null && stop()) { res.Canceled = true; break; }
            var (tk, V) = stack.Pop();
            var t = Unpack(tk);
            res.States++;
            buf.Clear();
            Steps(t, sc, Driver.This, r, frames, buf);
            bool tNeeds = NeedsRead(t);
            // This driver's HID class alone owes a READ_REPORT: it sends it
            // first.
            if (tNeeds && V != None && !v203.AnyNeeds(V))
                buf.RemoveAll(n => Label(t, n) != L_HID);
            if (buf.Count == 0)
            {
                res.Finals++;
                ulong mine = t.Delivered & OutMask;
                for (int pr = 1; pr <= 3; pr++) if (Count(t.Delivered, Slot(pr, 0)) > 0) res.ZeroSent = true;
                bool matched = V != None && v203.Matches(V, mine);
                if (!matched && res.Worse.Add(mine) && res.Example == null)
                {
                    res.Example = Show(mine) + (V == None ? " (after a step v2.0.3 could not take)" : "");
                    if (parent != null) res.Path = PathTo(parent, (tk, V));
                }
                continue;
            }
            foreach (var n in buf)
            {
                int V2 = V;
                int label = Label(t, n);
                bool nNeeds = NeedsRead(n);
                if (V != None)
                {
                    if (label != 0)
                        V2 = v203.Follow(V, label, MatchKey(label, n), label == L_KILL ? KillPhase(t.SdkPc) : 0, tNeeds, nNeeds);
                    else if (nNeeds != tNeeds)
                        V2 = v203.Close(V, nNeeds);
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
                var nk = (Pack(n), V2);
                if (seen.Add(nk)) { parent?.Add(nk, (tk, V)); stack.Push(nk); }
            }
        }
        res.Sets = v203.SetCount;
        res.V203States = v203.StateCount;
        res.Excused = v203.Excused;
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
        return (l == 0 ? "" : l == L_READ ? "[read, paired] " : l == L_HID ? "[READ_REPORT arrives] " : "[shared] ") + sb;
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
                next.Add(prog.Is21 && s.Cap ? s with { Hand = s.SdkSeq, Replaced = r.NoTakeBackOverUntaken && s.Hand != 0, SdkPc = S_RECLAIM }
                                            : s with { SdkPc = S_SIGNAL });
                break;
            case S_RECLAIM:
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
                    next.Add(r.CountFirst ? s with { Waiters = s.Waiters + 1, Counted = true, StaleTook = false, Pc = W_LOOK } : s with { StaleTook = false, Pc = W_LOOK });
                else
                    next.Add(s with { StaleTook = false, Pc = W_BLOCK });
                break;
            case W_LOOK:
            {
                bool took = r.TakeBeforeWait && s.Hand != 0 && (s.Hand != s.Seen || !r.DropSentAtTake);
                var n = took ? s with { Hand = 0, Waiters = s.Counted ? s.Waiters - 1 : s.Waiters, Counted = false, Pc = W_POLL }
                             : s with { Hand = r.TakeBeforeWait ? 0 : s.Hand, StaleTook = r.StaleTakeOnExit && r.TakeBeforeWait && s.Hand != 0, Pc = W_BLOCK };
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
                var back = r.SignalTakenOnExit ? s with { Event = true } : s;
                if (s.StopSig) next.Add(StopReturned(back, sc, d, r, prog));
                else if (s.Event)
                    next.Add(s.Tear != 0 ? (r.SignalTakenOnExit ? s : s with { Event = false }) with { Pc = W_EXITED }
                                         : s with { Event = false, Pc = W_READ, AfterOwner = false });
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
                    var back = s.StaleTook ? u with { Event = true, StaleTook = false } : u;
                    if (s.StopSig) next.Add(StopReturned(back, sc, d, r, prog));
                    else if (s.Event) next.Add(s.Tear != 0 ? back with { Event = s.StaleTook, Pc = W_EXITED } : u with { Event = false, Pc = W_READ, AfterOwner = false });
                    else if (s.OwnerSig) next.Add(s.Tear != 0 ? back with { Pc = W_EXITED } : OwnerGone(u with { OwnerSig = false }, d, r));
                    else next.Add(s with { Blocked = true });
                }
                else
                {
                    // A blocked wait returns with whichever event was set
                    // first, or times out.
                    var u = s with { Blocked = false, Waiters = s.Counted ? s.Waiters - 1 : s.Waiters, Counted = false };
                    var back = s.StaleTook ? u with { Event = true, StaleTook = false } : u;
                    if (s.StopSig) next.Add(StopReturned(back, sc, d, r, prog));
                    if (s.OwnerSig) next.Add(s.Tear != 0 ? back with { Pc = W_EXITED } : OwnerGone(u with { OwnerSig = false }, d, r));
                    if (!s.StopSig && !s.OwnerSig)
                    {
                        if (s.Tear != 0) next.Add(back with { Pc = W_EXITED });
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
                var n = Read(s with { Pc = s.AfterOwner ? W_OWNER : W_TOP, AfterOwner = false, NoRetry = false }, d, r, out bool fresh);
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
                next.Add(s with { Handed = s.Hand, Hand = 0, Pc = W_OG_LOOK });
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
    static State Read(State s, Driver d, Rules r, out bool fresh)
    {
        fresh = false;
        if (s.Prog == 0 && d == Driver.This && r.SkipZero)
        {
            // The lane returns before SeenSeqNo and the cache change. The
            // candidate forgets both, as v2.0.3's read of it did.
            if (!r.ZeroForgets) return s;
            fresh = s.Seen != 0;
            return s with { Seen = 0, CacheSeq = 0 };
        }
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
        if (s.Hand != 0 && (s.Hand != s.Seen || r.CloseSignalsSent) && s.Ev) n = n with { Event = true };
        return n;
    }

    // A recycle closes the input section and its event and goes to Phase 1.
    // The idle and stale recycles are outside events; a foreign stop's is
    // the worker's answer to one.
    static State Recycle(State n, Scenario sc, Driver d, Rules r, Prog prog, bool idle)
    {
        n = CloseGate(n, d, r);
        n = n with { Mapped = false, Pc = W_P1, CacheSeq = 0, LastDel = 0, EndSeen = n.EndSeen || !n.Holds };
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
                         Owed = false, Cache = 0, CacheSeq = 0, LastDel = 0, Parked = false, RelQ = 0, RelFor = 0, EndSeen = s.EndSeen || !s.Holds };
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
