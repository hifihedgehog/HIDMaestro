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
// from above the SeqNo it found, steps over 0 at the wrap, leaves the
// handoff as it found it at its start (c5b207a3, SharedMemoryIO.cs 532;
// the clear 10ddf75e made is the rule ClearAtStart, off in this driver),
// and with the capability set hands each frame over instead of signaling
// it, provisional and then final, signaling only when a worker is counted
// or the gate closed and it takes the frame back. A v2.0.x program counts from 0,
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
// release frame reached HID class, up to three times, for each program
// whether one of its frames reached HID class after the release frames
// its end queued, whether a frame went out after a later one of its
// program, and whether a frame or release frame went out after one of a
// later program. The check pairs the runs (ExploreAligned): both workers
// take their wait events together, and every final state of this driver
// must be matched or bettered by a final state v2.0.3 reaches through the
// same outside events and wait events, losing no frame or release frame
// that run delivers, sending a frame more often than once only where that
// run sends it as often, and sending out of order only where that run
// does. Better is an extra frame of a program that is no keyboard, and
// nothing after a step v2.0.3 could not take passes. A zeroed section
// must never go out.
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
        public bool ClearAtStart { get; init; } = true;   // a 2.1.0 start clears the handoff (10ddf75e; off in this driver, which leaves it as found)
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
        public bool SignalTakenAtTear { get; init; } = true;   // ... at teardown too (driver.c 2084, 2117), not only at a foreign stop (2122)
        public bool BackOnlyOnStop { get; init; }          // candidate: at teardown only when the poll returned the stop event
        public bool BackOnlyUndelivered { get; init; }     // candidate: and only while the section holds a SeqNo other than the last one delivered
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
        // Round 6 (10ddf75e). The SDK hands a frame over provisional (the
        // SeqNo with its low bit set), rechecks, and makes it final or takes
        // it back and signals, signaling also when a take dropped it; a
        // final SeqNo still pending is replaced by the next frame's final one
        // with no recheck (SharedMemoryIO.WriteInputFrame). A take drops a
        // provisional SeqNo and reads on a final one (TakeHandoff).
        public bool Round6 { get; init; }
        public bool Round6TakeProvisional { get; init; }   // taken away: a take reads on a provisional SeqNo too
        public bool Round6NoReplace { get; init; }         // taken away: a pending final is not replaced, the next frame goes provisional
        public bool Round6ExactTake { get; init; }         // the summary's reading: a take's read sends only the SeqNo taken
        // The fast branch (a candidate): after the pending-final branch the
        // SDK reads InputWaiters, and with it nonzero signals and leaves
        // InputHandoff alone. With it zero, the provisional path as before.
        public bool FastSignal { get; init; }
        public bool FastSkipsSignal { get; init; }         // taken away: the fast branch does not signal
        // T1: a take the owner pipe answered at teardown signals the frame
        // back before its read.
        public bool TearCheckedRead { get; init; }
        // Taken away (R6-1, 10ddf75e): a take's read the teardown overtook
        // signals the frame back when no READ_REPORT completed with it.
        public bool TearPostRead { get; init; }
        public bool TearCheckDelivered { get; init; }      // candidate: that check asks whether the frame read was delivered by any read
        public bool NoTakeBackOverUntaken { get; init; }   // the SDK skips the take-back when its exchange replaced a frame not taken
    }

    // This driver as of c5b207a3, rule by rule (driver/driver.c and
    // sdk/HIDMaestro.Core/Internal/SharedMemoryIO.cs at that commit):
    //   Round6: the SDK reads InputHandoff and replaces a final SeqNo there
    //     with its frame's final one by one compare-exchange, with no
    //     SetEvent (SharedMemoryIO.cs 1186). Otherwise it exchanges the
    //     provisional SeqNo in (1191), rechecks the capability and the
    //     count, and makes it final (1196) or takes it back (1200, Reclaim),
    //     and it signals unless it left the frame final. A take drops a
    //     provisional SeqNo and reads on a final one (driver.c 918,
    //     HandoffFinal 901).
    //   CountFirst, TakeBeforeWait: the increment (driver.c 2050), then the
    //     take (2061), then the wait.
    //   ClearAtStart off: the SDK leaves InputHandoff as found at its start
    //     (SharedMemoryIO.cs 532).
    //   SdkSkipsZero: NextEvenSeq steps over 0 (SharedMemoryIO.cs 693).
    //   SkipZero, ZeroForgets: a zeroed section sets SeenSeqNo to 0 and
    //     caches the zero report, and completes no read (driver.c 1739,
    //     1742, 1844).
    //   LateReadNotEqual off: a late READ_REPORT takes the cache only above
    //     the last SeqNo delivered (driver.c 2869).
    //   DropSentAtTake off: a take's read sends any SeqNo but the last one
    //     sent, as a signal's read does (driver.c 1727).
    //   TearCheckedRead: at teardown the owner path signals a taken frame
    //     back before its read (driver.c 2084), and TearPostRead off: no
    //     check follows the read (2089, 2144).
    //   WaitAgainAfterOwner: after that read the worker waits again
    //     (driver.c 2091).
    //   SignalTakenOnExit, BackOnlyAlone off: a frame taken and not read is
    //     signaled back at teardown or the stop event, whatever program runs
    //     (driver.c 2117, 2123).
    //   TakeAtOwnerGone off: OwnerGone leaves the handoff alone (driver.c
    //     1241), and forgets SeenSeqNo (1267). RetryAfterOwnerGone off: the
    //     watch is tried again at a map or every 256th frame (driver.c 966,
    //     2163). TakeReadNoNext and TakeReadNextSeq off: the read takes
    //     whatever the section holds (driver.c 2089, 2144).
    //   CloseGate, CloseSignalsSent: CloseGate clears the capability, takes
    //     the handoff, and signals any final SeqNo, the one last sent too,
    //     and drops a provisional one (driver.c 934, 942). It runs at a
    //     recycle (2236) and as the device goes (ClearCapAtGo, 2294).
    //   ForgetAtRecycle: the recycle forgets SeenSeqNo (driver.c 2264).
    internal static readonly Rules This = new Rules("this driver") with
    {
        Round6 = true, TearCheckedRead = true, CloseSignalsSent = true, TakeAtOwnerGone = false, RetryAfterOwnerGone = false,
        TakeReadNoNext = false, TakeReadNextSeq = false, BackOnlyAlone = false, ClearAtStart = false,
    };

    internal static readonly Rules[] Broken =
    {
        This with { Name = "a SetEvent with no take-back", Reclaim = false },
        This with { Name = "no take before the wait", TakeBeforeWait = false },
        This with { Name = "a take before the count", CountFirst = false },
        This with { Name = "the handoff cleared at a 2.1.0 start (10ddf75e)", ClearAtStart = true },
        This with { Name = "a zeroed section read as a frame", SkipZero = false },
        This with { Name = "SeqNo 0 stored at the wrap", SdkSkipsZero = false },
        This with { Name = "the recycle keeps the last SeqNo", ForgetAtRecycle = false },
        This with { Name = "the capability kept as the device goes", ClearCapAtGo = false },
        This with { Name = "no gate closed at a recycle or as the device goes", CloseGate = false },
        This with { Name = "OwnerGone straight after a taken frame read on the pipe's close", WaitAgainAfterOwner = false },
        This with { Name = "a taken frame dropped as the worker leaves its loop (B1)", SignalTakenOnExit = false },
        This with { Name = "a zeroed section keeps the last SeqNo and the cache (D1)", ZeroForgets = false },
        This with { Name = "a late read takes any SeqNo but the last delivered (D2)", LateReadNotEqual = true },
        This with { Name = "a take of the frame last sent wakes no read (B2)", DropSentAtTake = true },
        This with { Name = "a take reads on a provisional SeqNo (round 6)", Round6TakeProvisional = true },
        This with { Name = "a pending final SeqNo not replaced by the next frame's (round 6)", Round6NoReplace = true },
        This with { Name = "CloseGate drops a final frame equal to the last one sent (F-A)", CloseSignalsSent = false },
        This with { Name = "a take's read at teardown not signaled back (T1)", TearCheckedRead = false },
        This with { Name = "the watch tried again after OwnerGone (W1)", RetryAfterOwnerGone = true },
        This with { Name = "OwnerGone takes the handoff as b17050c1 did", TakeAtOwnerGone = true },
        This with { Name = "a take's read at teardown signaled back after it (R6-1, 10ddf75e)", TearPostRead = true },
        This with { Name = "a taken frame dropped as the worker leaves at teardown (B1, teardown half)", SignalTakenAtTear = false },
        This with { Name = "the fast branch skips its SetEvent (run on c2)", FastSkipsSignal = true },
        This with { Name = "a take's read sends no frame of a next program that started (B4, b17050c1's NextProgramStarted)", TakeReadNoNext = true },
        This with { Name = "a take's read sends only the frame taken or its program's next SeqNo (b17050c1's NextFrameSeq)", TakeReadNoNext = true, TakeReadNextSeq = true },
        This with { Name = "a taken frame signaled back at a stop only with no next program (b17050c1)", BackOnlyAlone = true },
    };

    // Candidate changes (the model section of docs/design/signal-gate.md),
    // each run in place of this driver's rules by the check command's rule
    // c0 to c6.
    internal static readonly Rules[] Candidates =
    {
        This with { Name = "round 6 with the summary's exact-SeqNo take", Round6ExactTake = true },
        This with { Name = "round 6 with the post-read teardown check asking whether the frame went out", TearPostRead = true, TearCheckDelivered = true },
        This with { Name = "c5b207a3 with the fast branch", FastSignal = true },
        This with { Name = "c5b207a3 signaling a taken frame back at teardown only on the stop event", BackOnlyOnStop = true },
        This with { Name = "c3 with the fast branch", BackOnlyOnStop = true, FastSignal = true },
        This with { Name = "c3 signaling back only a section not yet delivered", BackOnlyOnStop = true, BackOnlyUndelivered = true },
        This with { Name = "c5 with the fast branch", BackOnlyOnStop = true, BackOnlyUndelivered = true, FastSignal = true },
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

    const int S_CLAIM = 0, S_START = 1, S_WRITE = 2, S_CHECK = 3, S_RECLAIM = 4, S_SIGNAL = 5, S_EXIT = 6, S_NEXT = 7, S_DONE = 8, S_FRAMEDONE = 9,
              S_FAST = 10, S_XCHG = 11;
    const int W_P1 = 0, W_TOP = 1, W_LOOK = 2, W_POLL = 3, W_BLOCK = 4, W_READ = 5, W_OWNER = 6, W_GONE = 7, W_COUNT = 8,
              W_WOKE = 9, W_OG_TAKE = 10, W_OG_LOOK = 11, W_OG_END = 12, W_OG_ACK = 13, W_OG_SIG = 14, W_EXITED = 15, W_RCHK = 16;

    // A frame's identity is its program and its place, 1 to 5, packed into
    // the bits of the delivery counts: two bits a frame, saturating at 3,
    // three or more. Frame 0 of a program is the zeroed section. Slots 18
    // to 20 count the release frame each program left, and bits 42 to 44
    // say a program's frame went out after the release frames its end
    // queued. OrderBit: a program's frame went out after a later frame of
    // its program. CrossBit: a program's frame or release frame went out
    // after a frame or release frame of a later program.
    static int Slot(int prog, int idx) => (prog - 1) * 6 + idx;
    const int RelSlot = 18, LateBit = 42, OrderBit = 45, CrossBit = 46, TookShift = 56;

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
        public bool AckBit, AckEnded, Replaced, RetryNext, NoRetry, HandFinal, Served;
        public int Took;
        public ulong Delivered;
        // v2.0.3 only: its wait returned the input event at this driver's
        // take, and the poll after that take has not come yet.
        public bool Early;
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
        a.Put(s.Pc, 5);
        a.Put(s.Counted); a.Put(s.Blocked); a.Put(s.AfterOwner); a.Put(s.Owed); a.Put(s.Mapped); a.Put(s.SigMapped);
        var b = new Writer();
        b.Put(s.Seen, 8); b.Put(s.Cache, 5); b.Put(s.CacheSeq, 8); b.Put(s.LastDel, 8);
        b.Put(s.RecycleUsed); b.Put(s.ReAddUsed); b.Put(s.StopUsed); b.Put(s.KillUsed); b.Put(s.StopSig);
        b.Put(s.Tear, 2);
        b.Put(s.Ack, 2); b.Put(s.WatchP, 2); b.Put(s.RelQ, 2); b.Put(s.RelFor, 2); b.Put(s.RelDone, 3); b.Put(s.Handed, 8);
        b.Put(s.AckBit); b.Put(s.AckEnded); b.Put(s.Replaced); b.Put(s.RetryNext); b.Put(s.NoRetry); b.Put(s.HandFinal && s.Hand != 0);
        b.Put(s.Parked); b.Put(s.Served); b.Put(s.Early);
        // The delivery counts and the late, order and cross bits take 47 bits
        // of the third word, Took the top 8.
        if ((uint)s.Took >= 256) throw new InvalidOperationException("Took out of range");
        return new Key(a.W, b.W, s.Delivered | (ulong)(uint)s.Took << TookShift);
    }

    internal static State Unpack(Key k)
    {
        var a = new Reader(k.A);
        var s = new State();
        s.InSec = a.Bit(); s.Ev = a.Bit(); s.Sig = a.Bit(); s.Event = a.Bit(); s.Cap = a.Bit();
        s.Seq = a.Get(8); s.Prog = a.Get(2); s.Idx = a.Get(3); s.Waiters = a.Get(4) - 4; s.Hand = a.Get(8);
        s.P = a.Get(3); s.SdkPc = a.Get(4); s.SdkSeq = a.Get(8); s.Done = a.Get(3);
        s.PipeOpen = a.Bit(); s.Holds = a.Bit(); s.EndSeen = a.Bit(); s.Watch = a.Bit(); s.OwnerSig = a.Bit();
        s.Pc = a.Get(5);
        s.Counted = a.Bit(); s.Blocked = a.Bit(); s.AfterOwner = a.Bit(); s.Owed = a.Bit(); s.Mapped = a.Bit(); s.SigMapped = a.Bit();
        var b = new Reader(k.B);
        s.Seen = b.Get(8); s.Cache = b.Get(5); s.CacheSeq = b.Get(8); s.LastDel = b.Get(8);
        s.RecycleUsed = b.Bit(); s.ReAddUsed = b.Bit(); s.StopUsed = b.Bit(); s.KillUsed = b.Bit(); s.StopSig = b.Bit();
        s.Tear = b.Get(2);
        s.Ack = b.Get(2); s.WatchP = b.Get(2); s.RelQ = b.Get(2); s.RelFor = b.Get(2); s.RelDone = b.Get(3); s.Handed = b.Get(8);
        s.AckBit = b.Bit(); s.AckEnded = b.Bit(); s.Replaced = b.Bit(); s.RetryNext = b.Bit(); s.NoRetry = b.Bit(); s.HandFinal = b.Bit();
        s.Parked = b.Bit(); s.Served = b.Bit(); s.Early = b.Bit();
        s.Delivered = k.C & ((1UL << TookShift) - 1);
        s.Took = (int)(k.C >> TookShift);
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
    const int L_PROG = 1, L_RECYCLE = 2, L_GOES = 3, L_MAP = 4, L_STOP = 5, L_ADD = 6, L_KILL = 7, L_HID = 8, L_READ = 9, L_READ0 = 10,
              L_WAIT = 11, L_TEAR = 12;
    // What a worker's wait did (WaitKind): it blocked, or it returned the
    // stop event, the input event, the owner pipe, or a timeout.
    const int K_BLOCK = 1, K_STOP = 2, K_INPUT = 3, K_OWNER = 4, K_TIMEOUT = 5, K_TAKE = 6, K_POLL = 7;

    static int Label(in State s, in State n)
    {
        if (n.KillUsed != s.KillUsed) return L_KILL;
        if (SharedPc(s.SdkPc) && (n.SdkPc != s.SdkPc || n.P != s.P)) return L_PROG;
        if (n.RecycleUsed != s.RecycleUsed) return L_RECYCLE;
        if (n.ReAddUsed != s.ReAddUsed) return L_GOES;
        if (s.Pc == W_P1 && n.Pc == W_TOP && n.Mapped && !s.Mapped) return L_MAP;
        if (n.StopUsed != s.StopUsed) return L_STOP;
        if (s.Pc == W_GONE && n.Pc != W_GONE) return L_ADD;
        if (s.Tear == 1 && n.Tear == 2) return L_TEAR;
        if (WaitKind(s, n) != 0) return L_WAIT;
        if (NeedsRead(s) && s.Pc == n.Pc && s.SdkPc == n.SdkPc && s.Tear == n.Tear && (n.Parked || n.Delivered != s.Delivered)) return L_HID;
        if (s.Pc == W_READ && n.Pc != W_READ && s.Tear == 0)
            return n.Delivered == s.Delivered && n.Parked == s.Parked ? L_READ0 : L_READ;
        return 0;
    }


    // The worker's wait events, shared by both drivers (review SIG6, M6-1):
    // each driver's worker blocks in its wait, and returns from it, at the
    // same step as the other's, and the wait returns the same object. The
    // object is the first one set in the wait's order: the stop event, the
    // input event, the owner pipe. A blocked wait returns the stop event,
    // the owner pipe or a timeout, or the input event once a SetEvent ended
    // it (W_WOKE). This driver's wait after a take is two steps, the take
    // that finds a final SeqNo (K_TAKE) and the zero-timeout poll right
    // after it (K_POLL, or K_STOP when it returns the stop event), where
    // v2.0.3's is one. v2.0.3's wait returns the input event at either:
    // at the take (Early until the poll) or at the poll. A poll that
    // returns the stop event pairs with v2.0.3's wait returning it, after
    // its read when it returned at the take. The 500 ms timeout is a
    // time-driven step both workers take together. 0: the step is no wait
    // event.
    static int WaitKind(in State s, in State n)
    {
        if (n.SdkPc != s.SdkPc || n.P != s.P) return 0;
        switch (s.Pc)
        {
            case W_LOOK:
                return n.Pc == W_POLL && !s.StopSig ? K_TAKE : 0;
            case W_POLL:
                return n.Pc == s.Pc ? 0 : s.StopSig ? K_STOP : K_POLL;
            case W_WOKE:
                return n.Pc == s.Pc ? 0 : K_INPUT;
            case W_BLOCK when !s.Blocked:
                if (n.Pc == W_BLOCK) return n.Blocked ? K_BLOCK : 0;
                return s.StopSig ? K_STOP : s.Event ? K_INPUT : s.OwnerSig ? K_OWNER : 0;
            case W_BLOCK:
                if (n.Pc == W_BLOCK) return 0;
                if (n.Pc == W_EXITED) return s.StopSig ? K_STOP : s.OwnerSig ? K_OWNER : K_TIMEOUT;
                return n.Pc == W_P1 ? K_STOP : n.Pc is W_OG_TAKE or W_OG_LOOK ? K_OWNER : n.Pc == W_TOP ? K_TIMEOUT : 0;
            case W_P1:
                // Phase 1's 200 ms wait: the owner pipe, a timeout at
                // teardown, or a foreign stop it absorbs. The map is L_MAP.
                if (n.Pc is W_OG_TAKE or W_OG_LOOK) return K_OWNER;
                if (n.Pc == W_EXITED) return K_TIMEOUT;
                if (n.Pc == W_P1 && s.StopSig && !n.StopSig) return K_STOP;
                return 0;
            default:
                return 0;
        }
    }

    // The match of a step: MatchKey, and for a wait event what it did.
    static int Match(int label, in State s, in State n) =>
        MatchKey(label, n) | (label == L_WAIT ? WaitKind(s, n) << 15 : 0);

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
    // Under round 6 a take drops a provisional SeqNo unread, so a kill
    // before the recheck is always a kill before the wake.
    static int KillPhase(in State s, bool round6 = false) => s.SdkPc switch
    {
        S_CHECK or S_FAST or S_XCHG or S_SIGNAL => 1,
        S_RECLAIM => round6 || s.Hand == s.SdkSeq ? 1 : 2,
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
                                              .Aggregate(0UL, (m, x) => m | (3UL << (2 * x))) | (7UL << LateBit) | (1UL << OrderBit) | (1UL << CrossBit);

    // An outcome of this driver no worse than one of v2.0.3's: every frame
    // and release frame that one delivers this one delivers, every one this
    // one sends two or three times or more that one sends as often, and a
    // program's frame goes out after its release frames, after a later
    // frame of its program, and a program's frame or release frame after a
    // later program's, only where that one sends it so.
    internal static bool NoWorse(ulong mine, ulong theirs)
    {
        for (int slot = 0; slot < 21; slot++)
        {
            if (slot < RelSlot && slot % 6 == 0) continue;
            int m = Count(mine, slot), t = Count(theirs, slot);
            if (t >= 1 && m == 0) return false;
            if (m >= 2 && t < m) return false;
        }
        ulong late = (mine & ~theirs) >> LateBit & 7;
        ulong order = (mine & ~theirs) >> OrderBit & 1;
        ulong cross = (mine & ~theirs) >> CrossBit & 1;
        return late == 0 && order == 0 && cross == 0;
    }

    // 2: an outcome of theirs matches mine with no frame mine sends that it
    // does not, 1: one matches only with such an extra frame, of a program
    // that is no keyboard, 0: none.
    static int Classify(ulong mine, IEnumerable<ulong> theirs, int keyboards)
    {
        int cls = 0;
        foreach (var t in theirs)
        {
            if (!NoWorse(mine, t)) continue;
            int extra = ExtraPrograms(mine, t);
            if (extra == 0) return 2;
            if ((extra & keyboards) == 0) cls = 1;
        }
        return cls;
    }

    // The outcome checks on hand-built outcomes: the order across programs,
    // counts past two, and an extra keyboard frame.
    internal static List<(string Name, bool Ok)> SelfTest()
    {
        var t = new List<(string, bool)>();
        var none = new State();
        // Program 1's frame, program 2's frame, then program 1's release
        // frame: out of order across programs.
        var a = DeliverFrame(none, Slot(1, 1));
        a = DeliverFrame(a, Slot(2, 1));
        var cross = DeliverRelease(a with { RelQ = 1, RelFor = 1 });
        var inOrder = DeliverFrame(DeliverRelease(DeliverFrame(none, Slot(1, 1)) with { RelQ = 1, RelFor = 1 }), Slot(2, 1));
        t.Add(("a release frame of program 1 after program 2's frame sets the cross bit", (cross.Delivered >> CrossBit & 1) == 1));
        t.Add(("that outcome fails against one in order", !NoWorse(cross.Delivered & OutMask, inOrder.Delivered & OutMask)));
        t.Add(("the in-order outcome passes against itself", NoWorse(inOrder.Delivered & OutMask, inOrder.Delivered & OutMask)));
        var late = DeliverFrame(DeliverFrame(none, Slot(2, 1)), Slot(1, 1));
        t.Add(("program 1's frame after program 2's sets the cross bit", (late.Delivered >> CrossBit & 1) == 1));
        // Counts: three sends against two.
        var two = DeliverFrame(DeliverFrame(none, Slot(1, 1)), Slot(1, 1));
        var three = DeliverFrame(two, Slot(1, 1));
        var four = DeliverFrame(three, Slot(1, 1));
        t.Add(("a frame sent three times counts 3", Count(three.Delivered, Slot(1, 1)) == 3));
        t.Add(("a fourth send stays at 3, three or more", Count(four.Delivered, Slot(1, 1)) == 3));
        t.Add(("three sends fail against two", !NoWorse(three.Delivered & OutMask, two.Delivered & OutMask)));
        t.Add(("two sends pass against three", NoWorse(two.Delivered & OutMask, three.Delivered & OutMask)));
        // An extra frame: better for a program that is no keyboard, a
        // failure for a keyboard.
        var one = DeliverFrame(none, Slot(1, 1));
        var extra = DeliverFrame(one, Slot(1, 2));
        ulong[] theirs = { one.Delivered & OutMask };
        t.Add(("an extra frame of a program that is no keyboard is better", Classify(extra.Delivered & OutMask, theirs, 0) == 1));
        t.Add(("an extra keyboard frame fails", Classify(extra.Delivered & OutMask, theirs, 1) == 0));
        t.Add(("an equal outcome is equal", Classify(one.Delivered & OutMask, theirs, 1) == 2));
        return t;
    }

    // The programs (a bit each) with a frame this outcome sends and that one
    // does not.
    static int ExtraPrograms(ulong mine, ulong theirs)
    {
        int m = 0;
        for (int slot = 0; slot < RelSlot; slot++)
            if (slot % 6 != 0 && Count(mine, slot) >= 1 && Count(theirs, slot) == 0) m |= 1 << (slot / 6);
        return m;
    }

    // The programs (a bit each) that leave a release frame: keyboards.
    static int KeyboardPrograms(Scenario sc)
    {
        int m = 0;
        for (int i = 0; i < sc.Programs.Length; i++) if (sc.Programs[i].Rel) m |= 1 << i;
        return m;
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
        if ((v >> OrderBit & 1) != 0) parts.Add("out-of-order");
        if ((v >> CrossBit & 1) != 0) parts.Add("before-a-later-program");
        return string.Join(" ", parts);
    }

    static int Count(ulong delivered, int slot) => (int)((delivered >> (2 * slot)) & 3);

    static ulong Deliver(ulong delivered, int slot) =>
        Count(delivered, slot) == 3 ? delivered : delivered + (1UL << (2 * slot));

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
            // v2.0.3's timeout back to its wait changes nothing.
            if (next.All(n => Label(s, n) == L_WAIT && WaitKind(s, n) == K_TIMEOUT && d == Driver.V203))
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

    internal static bool SetStats;
    internal static bool CutDiverged;

    internal sealed class AlignedResult
    {
        public long States, Finals, Unaligned, Sets, SetItems, V203States, Subsumed;
        public bool Canceled, OwnFails, TooLarge;
        // With CutDiverged: a shared step v2.0.3 could not take, where the
        // exploration stopped, since every final state after it fails.
        public bool CutFail;
        public readonly HashSet<ulong> Worse = new();
        // Outcomes matched only by v2.0.3 runs that deliver fewer frames:
        // an extra frame, neither a loss nor a duplicate.
        public readonly HashSet<ulong> Extra = new();
        // Outcomes equal to a paired v2.0.3 run's.
        public readonly HashSet<ulong> Equal = new();
        public string? ExtraExample;
        public List<State>? ExtraPath;
        public bool ZeroSent;
        public string? Example;
        public List<State>? Path;
        // With the witness: the paired v2.0.3 set at each of its states, as
        // one line a state of the set (--sets).
        public List<List<string>>? PathSets;
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
    // Not a wait: this driver's worker has left its loop (W_EXITED, W_GONE),
    // so the wait events of v2.0.3's worker are its own until the device is
    // added again. Passed beside the waits.
    const int M_FREE = 4;

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
        readonly SetStore store = new();
        readonly List<int> items = new();
        readonly Dictionary<(int, int), int> closed = new();
        readonly Dictionary<(int, int, int, int, int, int), int> follow = new();
        readonly Dictionary<int, ulong[]> finals = new();
        readonly Dictionary<int, int> waits = new();
        readonly List<State> buf = new();
        readonly HashSet<int> scratch = new();
        readonly Stack<int> work = new();
        public long SetItems => store.Members;

        public V203Side(Scenario sc, int frames) { this.sc = sc; this.frames = frames; }
        public int SetCount => store.Sets;
        public int StateCount => keyOf.Count;

        readonly record struct Succ(int M, int Label, int Match, int Phase);
        // Waits: the time-driven steps the state waits for (M_READ, M_MAP).
        // Pending: a wake waits to be read (the input event set, or a wait
        // the input event ended).
        // Final: no step but v2.0.3's timeout back to its wait, which
        // changes nothing.
        // Waiting: the worker is at its wait, blocked in it or about to be,
        // with no input event set.
        // AtWait: the worker is at its wait, blocked or not.
        // Exited: the worker has left its loop.
        sealed record Info(Succ[] S, int Waits, bool Pending, bool Final, bool Waiting, bool Blocked, bool AtWait, bool Exited);

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
                s[i] = new Succ(Id(Pack(states[i])), l, Match(l, v, states[i]), KillPhase(v));
            }
            a = new Info(s, w, v.Event || v.Pc is W_WOKE or W_READ, s.All(y => y.Label == L_WAIT && y.Match >> 15 == K_TIMEOUT),
                         v.Pc == W_BLOCK && !v.Event, v.Pc == W_BLOCK && v.Blocked, v.Pc == W_BLOCK, v.Pc is W_EXITED or W_GONE);
            info[id] = a;
            return a;
        }

        int Intern(HashSet<int> h) => store.Intern(h);

        // The steps a v2.0.3 state takes as its own while this driver's side
        // waits for thisWaits: what it alone waits for, or else its worker's,
        // its program's private ones and its reads, and its wait events once
        // this driver's worker has left its loop.
        static bool Own(Info x, Succ y, int thisWaits)
        {
            int alone = x.Waits & ~thisWaits;
            if (alone != 0)
                return ((alone & M_READ) != 0 && y.Label == L_HID) || ((alone & M_MAP) != 0 && y.Label == L_MAP);
            return y.Label is 0 or L_READ || (y.Label == L_WAIT && (thisWaits & M_FREE) != 0);
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
            foreach (var x in store.Fill(v, items)) scratch.Add(x);
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

        // The states of set V that wait for every step in bits, closed under
        // the given waits: the runs that, like this driver's side, still
        // wait when that side lets a step it waits for wait longer.
        readonly Dictionary<(int, int, int), int> restricted = new();
        public int Restrict(int v, int bits, int thisWaits)
        {
            if (restricted.TryGetValue((v, bits, thisWaits), out int r)) return r;
            scratch.Clear();
            foreach (var x in store.Fill(v, items)) if ((Of(x).Waits & bits) == bits) scratch.Add(x);
            r = scratch.Count == 0 ? -1 : CloseScratch(thisWaits);
            restricted[(v, bits, thisWaits)] = r;
            return r;
        }

        // The time-driven steps some state of set V waits for.
        public int Waits(int v)
        {
            if (waits.TryGetValue(v, out int w)) return w;
            w = 0;
            foreach (var x in store.Fill(v, items)) w |= Of(x).Waits;
            waits[v] = w;
            return w;
        }

        // The states one state reaches by its own steps, wait events left
        // out.
        List<int> OwnClosure(int start, int thisWaits)
        {
            var seen = new HashSet<int> { start };
            var stack = new Stack<int>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var x = Of(stack.Pop());
                foreach (var y in x.S)
                    if (y.Label != L_WAIT && Own(x, y, thisWaits & (M_READ | M_MAP)) && seen.Add(y.M)) stack.Push(y.M);
            }
            return seen.ToList();
        }

        // State id with its Early flag set or cleared.
        int WithEarly(int id, bool early)
        {
            var u = Unpack(keyOf[id]);
            if (u.Early == early) return id;
            u.Early = early;
            return Id(Pack(u));
        }

        // Each state of set V in one line, for --sets.
        public List<string> Brief(int v) =>
            store.Fill(v, new List<int>()).Select(x => Unpack(keyOf[x])).Select(x =>
                $"{PcName(x.Pc)}{(x.Blocked ? "(blocked)" : "")} sdk {SdkName(x.SdkPc)} p{x.P} ev {(x.Event ? 1 : 0)} stop {(x.StopSig ? 1 : 0)} owner {(x.OwnerSig ? 1 : 0)} " +
                $"tear {x.Tear} seq {x.Seq} seen {x.Seen} parked {(x.Parked ? 1 : 0)}{(x.Early ? " early" : "")} [{Show(x.Delivered & OutMask)}]").ToList();

        // Measures of the set store and the caches over it.
        public string Stats() =>
            $"sets {store.Sets} members {store.Members} nodes {store.Nodes} store-bytes {store.Bytes} " +
            $"states {keyOf.Count} closed {closed.Count} follow {follow.Count} restricted {restricted.Count} waits {waits.Count} finals {finals.Count}";

        // Set a is a subset of set b.
        public bool Subset(int a, int b) => store.Subset(a, b);

        // The set v2.0.3 reaches from set V through the shared event this
        // driver took, or -1 when no state of V can take it. Every state of
        // V that can take the event takes it. A state that cannot take a
        // READ_REPORT arrival, the Phase 1 map or the idle recycle lets the
        // time pass and goes on (the recycle's one chance under its program
        // spent): it was not waiting for it, or its worker was busy. A state
        // that alone waits for a step takes that step first, so the state
        // after it stands for it here. A read of this driver's pairs with a
        // read of every v2.0.3 run that has a wake to read on. A run with
        // none goes on without reading when this driver's read completed no
        // READ_REPORT (it can change no outcome by itself), once the program
        // was killed, or while the run's worker waits with nothing set: this
        // driver then read on a wake v2.0.3 never had (a frame the dying
        // program left in the handoff, or a frame signaled back at a stop or
        // teardown). A wait of this driver's that returns on such a wake,
        // and the block after its read, leave that run's worker in its wait.
        // Every other wait event pairs with the same one of the run's.
        public int Follow(int v, int label, int match, int phase, int waitsBefore, int waitsAfter)
        {
            var key = (v, label, match, phase, waitsBefore, waitsAfter);
            if (follow.TryGetValue(key, out int r)) return r;
            scratch.Clear();
            int want = label == L_READ0 ? L_READ : label;
            int kind = label == L_WAIT ? match >> 15 : 0;
            foreach (var xi in store.Fill(v, items))
            {
                var x = Of(xi);
                if ((x.Waits & ~waitsBefore) != 0) continue;
                if (kind == K_TAKE)
                {
                    // v2.0.3's wait returns the input event here, or at the
                    // poll that follows.
                    scratch.Add(xi);
                    foreach (var y in x.S)
                        if (y.Label == L_WAIT && y.Match >> 15 == K_INPUT && (y.Match & 0x7FFF) == (match & 0x7FFF)) scratch.Add(WithEarly(y.M, true));
                    continue;
                }
                if (kind == K_POLL)
                {
                    if (Unpack(keyOf[xi]).Early) { scratch.Add(WithEarly(xi, false)); continue; }
                    bool any = false;
                    foreach (var y in x.S)
                        if (y.Label == L_WAIT && y.Match >> 15 == K_INPUT && (y.Match & 0x7FFF) == (match & 0x7FFF)) { scratch.Add(y.M); any = true; }
                    if (!any && (x.Waiting || x.Exited))
                    {
                        // A wake this driver has and the run does not.
                        bool blocks = false;
                        if (x.Waiting && !x.Blocked)
                            foreach (var y in x.S)
                                if (y.Label == L_WAIT && y.Match >> 15 == K_BLOCK) { scratch.Add(y.M); blocks = true; }
                        if (!blocks) scratch.Add(xi);
                    }
                    continue;
                }
                bool took = false;
                bool early = kind == K_STOP && Unpack(keyOf[xi]).Early;
                foreach (var y in x.S)
                    if (y.Label == want && y.Match == match && (label != L_KILL || (y.Phase & phase) != 0))
                    {
                        scratch.Add(early ? WithEarly(y.M, false) : y.M);
                        took = true;
                    }
                if (!took && label is L_HID or L_MAP) scratch.Add(xi);
                // A wake this driver has and the run does not (a frame
                // signaled back at teardown that v2.0.3's wait consumed as
                // its worker left): the run's worker waits on, or blocks,
                // through this driver's return on it, and through the block
                // that follows that read.
                if (!took && label == L_WAIT && match >> 15 == K_INPUT && x.Waiting)
                {
                    bool blocks = false;
                    if (!x.Blocked)
                        foreach (var y in x.S)
                            if (y.Label == L_WAIT && y.Match >> 15 == K_BLOCK) { scratch.Add(y.M); blocks = true; }
                    if (!blocks) scratch.Add(xi);
                }
                if (!took && label == L_WAIT && match >> 15 == K_BLOCK && x.Waiting && x.Blocked) scratch.Add(xi);
                // The run's worker left its loop first: this driver's wait
                // events have no partner left.
                if (!took && label == L_WAIT && x.Exited) scratch.Add(xi);
                // A wake the run has and this driver does not (a SetEvent
                // that came after its worker read the frame, which this
                // driver's take had already answered): where this driver's
                // wait blocks or returns the owner pipe or a timeout, the
                // run's returns that wake first, the run reads on it, and its
                // next wait does what this driver's did.
                if (!took && label == L_WAIT && kind is K_BLOCK or K_OWNER or K_TIMEOUT && x.AtWait && !x.Waiting && !x.Blocked)
                    foreach (var y in x.S)
                        if (y.Label == L_WAIT && y.Match >> 15 == K_INPUT && (y.Match & 0x7FFF) == (match & 0x7FFF))
                            foreach (var z in OwnClosure(y.M, waitsBefore))
                            {
                                bool next = false;
                                foreach (var w in Of(z).S)
                                    if (w.Label == L_WAIT && w.Match == match) { scratch.Add(w.M); next = true; }
                                if (!next && kind == K_TIMEOUT)
                                    foreach (var w in Of(z).S)
                                        if (w.Label == L_WAIT && w.Match >> 15 == K_BLOCK)
                                            foreach (var u in Of(w.M).S) if (u.Label == L_WAIT && u.Match >> 15 == K_TIMEOUT) { scratch.Add(u.M); next = true; }
                                if (!next && kind == K_BLOCK && Of(z).AtWait) scratch.Add(z);
                            }
                if (!took && label == L_WAIT && match >> 15 == K_TIMEOUT && x.Waiting && !x.Blocked)
                    foreach (var y in x.S)
                        if (y.Label == L_WAIT && y.Match >> 15 == K_BLOCK)
                            foreach (var z in Of(y.M).S) if (z.Label == L_WAIT && z.Match >> 15 == K_TIMEOUT) scratch.Add(z.M);
                if (!took && !x.Pending && (label == L_READ0 || (label == L_READ && ((match & 1 << 6) != 0 || x.Waiting)))) scratch.Add(xi);
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
            var members = store.Fill(v, new List<int>());
            var finalsOut = members.Where(x => Of(x).Final).Select(x => Show(keyOf[x].C & OutMask)).Distinct().ToList();
            lines.Add($"paired v2.0.3 set: {members.Count} states, {finalsOut.Count} final outcome(s): " +
                      (finalsOut.Count == 0 ? "none" : string.Join(" | ", finalsOut.Select(o => "[" + o + "]"))));
            foreach (var xi in members.Where(x => !Of(x).Final && Of(x).S.All(y => y.Label is not (0 or L_READ))).Take(4))
            {
                var x = Unpack(keyOf[xi]);
                lines.Add($"  waits: worker {PcName(x.Pc)}, sdk {SdkName(x.SdkPc)}, parked {x.Parked}, event {x.Event}, seen {x.Seen}, seq {x.Seq}, " +
                          $"delivered [{Show(x.Delivered & OutMask)}], its steps: " +
                          string.Join(", ", Of(xi).S.Select(y => y.Label switch
                          {
                              L_PROG => "program", L_RECYCLE => "recycle", L_GOES => "goes", L_MAP => "map", L_STOP => "stop", L_ADD => "add",
                              L_KILL => "kill", L_HID => "READ_REPORT", L_READ => "read", L_WAIT => "wait", L_TEAR => "teardown's stop", _ => "own",
                          })));
            }
            return lines;
        }

        // 2: some final state of V matches with no frame this one sends that
        // it does not, 1: only with such an extra frame, 0: none matches. An
        // extra frame of a keyboard, a press and release v2.0.3 never shows,
        // is no better outcome (keyboards: a bit per program).
        public int MatchClass(int v, ulong mine, int keyboards) => Matches(v, mine) ? Classify(mine, finals[v], keyboards) : 0;

        public bool Matches(int v, ulong mine)
        {
            if (!finals.TryGetValue(v, out var outs))
            {
                var o = new HashSet<ulong>();
                foreach (var x in store.Fill(v, items))
                    if (Of(x).Final) o.Add(keyOf[x].C & OutMask);
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
    // any state of the set is counted (Unaligned): the runs of the set go on
    // without it, and the final state passes only with a better outcome
    // than theirs, an extra frame and nothing worse.
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

        int TWaits(in State t) => (NeedsRead(t) ? M_READ : 0) | (MapReady(t, sc, Driver.This, r, frames, scratch) ? M_MAP : 0)
                                  | (t.Pc is W_EXITED or W_GONE ? M_FREE : 0);

        // A set number at or above 0 is paired; -2 - n is set n after a step
        // its runs could not follow.
        static int SetOf(int v) => v >= 0 ? v : -2 - v;
        static int Diverged(int v) => v >= 0 ? -2 - v : v;

        // The explored pairs: by this driver's state, the waits of the set
        // and divergence, the sets explored with it that no other holds.
        var covers = new Dictionary<(Key, int, bool), List<int>>();
        bool Visit(Key k, int v, int tw)
        {
            int set = SetOf(v);
            var key = (k, v203.Waits(set) & tw, v < 0);
            if (!covers.TryGetValue(key, out var lst)) { covers[key] = new List<int> { set }; return true; }
            // A diverged pair passes only with a better outcome, which a
            // larger set can turn into an equal one, so those are kept
            // exactly.
            if (v < 0) { if (lst.Contains(set)) return false; lst.Add(set); return true; }
            foreach (var v1 in lst) if (v203.Subset(v1, set)) { res.Subsumed++; return false; }
            lst.RemoveAll(v1 => v203.Subset(set, v1));
            lst.Add(set);
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
            int Vset = SetOf(V);
            bool div = V < 0;
            int alone = tw & ~v203.Waits(Vset) & (M_READ | M_MAP);
            if (alone != 0)
                buf.RemoveAll(n => Label(t, n) is var l && !(((alone & M_READ) != 0 && l == L_HID) || ((alone & M_MAP) != 0 && l == L_MAP)));
            if (buf.Count == 0)
            {
                res.Finals++;
                ulong mine = t.Delivered & OutMask;
                for (int pr = 1; pr <= 3; pr++) if (Count(t.Delivered, Slot(pr, 0)) > 0) res.ZeroSent = true;
                // 2: equal to a paired run's, 1: better (an extra frame, nothing
                // worse), 0: no paired run's. After a step the runs could not
                // follow, nothing passes.
                // This driver's worker takes no wait event again, so the
                // runs' workers finish theirs on their own: a wake only they
                // have is read.
                int cls = v203.MatchClass(v203.Close(Vset, tw | M_FREE), mine, KeyboardPrograms(sc));
                if (div) cls = 0;
                bool matched = cls != 0;
                if (cls == 2) res.Equal.Add(mine);
                if (cls == 1 && res.Extra.Add(mine) && res.ExtraExample == null)
                {
                    res.ExtraExample = Show(mine);
                    if (parent != null) res.ExtraPath = PathTo(parent, (tk, V));
                }
                if (!matched && res.Worse.Add(mine) && parent != null) res.Paths[mine] = PathTo(parent, (tk, V));
                if (!matched && res.Example == null)
                {
                    res.Example = Show(mine) + (div ? " (after a step v2.0.3 could not take)" : "");
                    if (parent != null) res.Path = PathTo(parent, (tk, V));
                    if (parent != null && DumpSets) res.PathSets = PathSetsTo(parent, (tk, V), v203);
                    if (parent != null) res.PairedSide = v203.Describe(v203.Close(Vset, tw | M_FREE));
                }
                continue;
            }
            foreach (var n in buf)
            {
                int label = Label(t, n);
                int nw = TWaits(n);
                // HID class sends its next READ_REPORT as soon as one
                // completes (IOCTL_HID_READ_REPORT, "ping-pong fashion"),
                // through the reflector to the driver host, so it may arrive
                // late on both sides alike. When this driver's side lets a
                // READ_REPORT or the Phase 1 map it waits for wait longer,
                // only the v2.0.3 runs that wait for it too stay paired: a
                // run that has its read takes no part in the delay.
                int delayed = tw & (M_READ | M_MAP) & ~((label == L_HID ? M_READ : 0) | (label == L_MAP ? M_MAP : 0));
                int Vs = delayed == 0 ? Vset : v203.Restrict(Vset, delayed, tw);
                if (Vs == None) continue;
                int V2 = Vs;
                if (label != 0)
                    V2 = v203.Follow(Vs, label, Match(label, t, n), label == L_KILL ? KillPhase(t, r.Round6 && !r.Round6TakeProvisional) : 0, tw, nw);
                else if (nw != tw)
                    V2 = v203.Close(Vs, nw);
                if (V2 == None)
                {
                    res.Unaligned++;
                    if (parent != null && res.UnalignedPath == null)
                    {
                        res.UnalignedPath = PathTo(parent, (tk, V));
                        res.UnalignedPath.Add(n);
                    }
                    // Every final state after it fails. Cut there, the
                    // scenario fails and its later outcomes go uncounted.
                    if (CutDiverged) { res.CutFail = true; div = V < 0; continue; }
                    // The runs go on without that step.
                    V2 = nw != tw ? v203.Close(Vs, nw) : Vs;
                    div = true;
                }
                if (div) V2 = Diverged(V2);
                div = V < 0;
                var nk = Pack(n);
                if (Visit(nk, V2, nw)) { parent?.TryAdd((nk, V2), (tk, V)); stack.Push((nk, V2)); }
            }
        }
        if (SetStats) Console.Error.WriteLine($"{sc.Name}: " + v203.Stats() + $" covers {covers.Count} listed {covers.Values.Sum(l => (long)l.Count)} explored {res.States}");
        res.Sets = v203.SetCount;
        res.SetItems = v203.SetItems;
        res.V203States = v203.StateCount;
        return res;
    }

    // The review's order x14-take-at-teardown-rev-late-g: the worker takes
    // a frame X, frame G is handed over final after the take's decrement,
    // TearingDown is set, and the poll after the take returns with no stop
    // event, so the worker leaves with X dropped and G final in the
    // handoff, which CloseGate signals. FindOrder searches this driver's
    // runs alone for one that reaches that point and ends with G sent;
    // ReplayOrder pairs that one run as ExploreAligned pairs every run and
    // gives its class and the paired set's final outcomes.
    internal static List<State>? FindOrder(Scenario sc, Rules r, int framesArg)
    {
        int frames = sc.FramesFor(framesArg);
        var start = Start();
        var parent = new Dictionary<(Key, int), (Key, int)>();
        var seen = new HashSet<(Key, int)> { (Pack(start), 0) };
        var queue = new Queue<(State, int)>();
        queue.Enqueue((start, 0));
        var next = new List<State>();
        while (queue.Count > 0)
        {
            var (t, g) = queue.Dequeue();
            next.Clear();
            Steps(t, sc, Driver.This, r, frames, next);
            if (next.Count == 0)
            {
                if (g >= 100 && Count(t.Delivered, g - 100) >= 1)
                {
                    var path = new List<State>();
                    var k = (Pack(t), g);
                    while (true) { path.Add(Unpack(k.Item1)); if (!parent.TryGetValue(k, out var pk)) break; k = pk; }
                    path.Reverse();
                    return path;
                }
                continue;
            }
            foreach (var n in next)
            {
                // The order, step by step: 1, the worker takes X before
                // teardown, with the section still holding X; 2, then G is
                // stored and handed over final; 3, then
                // TearingDown is set; 100 + G's slot, then the poll after
                // the take returns with no stop event, G final in the
                // handoff and not yet delivered.
                int g2 = g;
                if (g == 0 && t.Pc == W_LOOK && n.Pc == W_POLL && t.Tear == 0 && t.Seq == t.Hand) g2 = 1;
                else if (g == 1 && n.Pc == W_POLL && n.Tear == 0 && n.Hand != 0 && n.HandFinal && !(t.Hand == n.Hand && t.HandFinal)) g2 = 2;
                else if (g == 2 && t.Tear == 0 && n.Tear == 1 && n.Pc == W_POLL) g2 = 3;
                else if (g == 3 && t.Pc == W_POLL && n.Pc == W_EXITED && t.Tear == 1 && !t.StopSig && t.Hand != 0 && t.HandFinal
                         && t.Seq == t.Hand && t.Prog != 0 && Count(t.Delivered, Slot(t.Prog, t.Idx)) == 0)
                    g2 = Slot(t.Prog, t.Idx) + 100;
                else if (g is 1 or 2 or 3 && n.Pc != W_POLL) g2 = 0;
                var key = (Pack(n), g2);
                if (seen.Add(key)) { parent[key] = (Pack(t), g); queue.Enqueue((n, g2)); }
            }
        }
        return null;
    }

    internal static List<string> ReplayOrder(Scenario sc, Rules r, int framesArg, List<State> path)
    {
        int frames = sc.FramesFor(framesArg);
        var v203 = new V203Side(sc, frames);
        var scratch = new List<State>();
        var lines = new List<string>();
        int TWaits(in State t) => (NeedsRead(t) ? M_READ : 0) | (MapReady(t, sc, Driver.This, r, frames, scratch) ? M_MAP : 0)
                                  | (t.Pc is W_EXITED or W_GONE ? M_FREE : 0);
        int V = v203.Initial(TWaits(path[0]));
        bool div = false;
        for (int i = 1; i < path.Count; i++)
        {
            var t = path[i - 1]; var n = path[i];
            int tw = TWaits(t), nw = TWaits(n);
            int label = Label(t, n);
            string tag = $"{i,3}. {Describe(t, n)}";
            int alone = tw & ~v203.Waits(V) & (M_READ | M_MAP);
            if (alone != 0 && !(((alone & M_READ) != 0 && label == L_HID) || ((alone & M_MAP) != 0 && label == L_MAP)))
                tag += "   (a step this driver's side does not take while it alone waits)";
            int delayed = tw & (M_READ | M_MAP) & ~((label == L_HID ? M_READ : 0) | (label == L_MAP ? M_MAP : 0));
            int Vs = delayed == 0 ? V : v203.Restrict(V, delayed, tw);
            if (Vs == -1) { lines.Add(tag); lines.Add("    no v2.0.3 run waits as long: the pairing drops this run"); return lines; }
            int V2 = label != 0 ? v203.Follow(Vs, label, Match(label, t, n), label == L_KILL ? KillPhase(t, r.Round6 && !r.Round6TakeProvisional) : 0, tw, nw)
                                : nw != tw ? v203.Close(Vs, nw) : Vs;
            if (V2 == -1) { div = true; V2 = nw != tw ? v203.Close(Vs, nw) : Vs; tag += "   (v2.0.3 cannot follow)"; }
            V = V2;
            lines.Add(tag);
            if (DumpSets) foreach (var l in v203.Brief(V)) lines.Add("       | " + l);
        }
        var last = path[^1];
        ulong mine = last.Delivered & OutMask;
        int Vfin = v203.Close(V, TWaits(last) | M_FREE);
        int cls = div ? 0 : v203.MatchClass(Vfin, mine, KeyboardPrograms(sc));
        lines.Add($"this driver delivers [{Show(mine)}]: {(cls == 2 ? "equal" : cls == 1 ? "better" : "fails")}");
        lines.AddRange(v203.Describe(Vfin));
        return lines;
    }

    internal static bool DumpSets;

    static List<List<string>> PathSetsTo(Dictionary<(Key, int), (Key, int)> parent, (Key, int) k, V203Side v203)
    {
        var sets = new List<List<string>>();
        while (true)
        {
            int v = k.Item2 >= 0 ? k.Item2 : -2 - k.Item2;
            sets.Add(v203.Brief(v));
            if (!parent.TryGetValue(k, out var pk)) break;
            k = pk;
        }
        sets.Reverse();
        return sets;
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
        return (l == 0 ? "" : l == L_READ ? "[read, paired] " : l == L_READ0 ? "[read of nothing new, paired] " : l == L_HID ? "[READ_REPORT arrives] "
                : l == L_WAIT ? $"[wait {KindName(WaitKind(a, b))}, paired] " : l == L_TEAR ? "[stop event at teardown, shared] " : "[shared] ") + sb;
    }

    static string KindName(int k) => k switch
    {
        K_BLOCK => "blocks", K_STOP => "returns the stop event", K_INPUT => "returns the input event", K_OWNER => "returns the owner pipe",
        K_TIMEOUT => "times out", K_TAKE => "takes a final SeqNo", K_POLL => "polls after the take", _ => k.ToString(),
    };

    static string PcName(int pc) => pc switch
    {
        W_P1 => "P1", W_TOP => "TOP", W_LOOK => "TAKE", W_POLL => "POLL", W_BLOCK => "WAIT", W_READ => "READ", W_OWNER => "OWNER",
        W_GONE => "GONE", W_COUNT => "COUNT", W_WOKE => "WOKE", W_OG_TAKE => "OG-TAKE", W_OG_LOOK => "OG-LOOK", W_OG_END => "OG-END",
        W_OG_ACK => "OG-ACK", W_OG_SIG => "OG-SIGNAL", W_EXITED => "EXITED", W_RCHK => "READ-CHECK", _ => pc.ToString(),
    };

    static string SdkName(int pc) => pc switch
    {
        S_CLAIM => "claim", S_START => "start", S_WRITE => "store", S_CHECK => "check", S_RECLAIM => "take-back", S_SIGNAL => "SetEvent",
        S_EXIT => "exit", S_NEXT => "next", S_DONE => "done", S_FRAMEDONE => "frame-done", S_FAST => "count-read", S_XCHG => "exchange",
        _ => pc.ToString(),
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
                if (r.Round6 && !r.Round6NoReplace && prog.Is21 && s.Cap && s.Hand != 0 && s.HandFinal)
                    // A pending final value becomes the new frame's, in one
                    // compare-exchange: its wake carries this frame.
                    next.Add(AfterFrame(s with { Hand = s.SdkSeq, HandFinal = true }));
                else if (r.Round6 && r.FastSignal && prog.Is21 && s.Cap)
                    next.Add(s with { SdkPc = S_FAST });
                else
                    next.Add(prog.Is21 && s.Cap ? s with { Hand = s.SdkSeq, HandFinal = false, Replaced = r.NoTakeBackOverUntaken && s.Hand != 0, SdkPc = S_RECLAIM }
                                                : s with { SdkPc = S_SIGNAL });
                break;
            case S_FAST:
                // The fast branch: with a worker counted the SDK signals and
                // leaves the handoff as it is. With none, the provisional
                // path follows.
                if (s.Waiters != 0) next.Add(r.FastSkipsSignal ? AfterFrame(s) : s with { SdkPc = S_SIGNAL });
                else next.Add(s with { SdkPc = S_XCHG });
                break;
            case S_XCHG:
                next.Add(s with { Hand = s.SdkSeq, HandFinal = false, SdkPc = S_RECLAIM });
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
                    else next.Add(s with { Hand = mine && r.Reclaim ? 0 : s.Hand, HandFinal = mine && r.Reclaim ? false : s.HandFinal, SdkPc = S_SIGNAL });
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
        if (sc.Kill == s.P && !s.KillUsed && s.SdkPc is S_CHECK or S_FAST or S_XCHG or S_RECLAIM or S_SIGNAL or S_FRAMEDONE)
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
                bool took = r.TakeBeforeWait && s.Hand != 0 && (s.Hand != s.Seen || !r.DropSentAtTake) && (!r.Round6 || s.HandFinal || r.Round6TakeProvisional);
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
                var back = r.SignalTakenOnExit && alone && (!r.BackOnlyUndelivered || s.Seq != s.LastDel) ? s with { Event = true, Took = 0 } : s with { Took = 0 };
                var backTear = r.SignalTakenAtTear ? back : s with { Took = 0 };
                // A candidate: at teardown the frame goes back only when the
                // poll returned the stop event, as v2.0.3's wait left its
                // signal only then. Before the stop event its wait returned
                // the input event, and the worker left with it consumed.
                var tearNoStop = r.BackOnlyOnStop ? s with { Took = 0 } : backTear;
                if (s.StopSig) next.Add(s.Tear != 0 ? backTear with { Pc = W_EXITED } : StopReturned(back, sc, d, r, prog));
                else if (s.Event)
                    next.Add(s.Tear != 0 ? (r.SignalTakenOnExit && r.SignalTakenAtTear && !r.BackOnlyOnStop ? s : s with { Event = false }) with { Took = 0, Pc = W_EXITED }
                                         : s with { Event = false, Took = r.TearCheckedRead ? s.Took : 0, Pc = W_READ, AfterOwner = false });
                else if (s.OwnerSig && !r.WaitAgainAfterOwner) next.Add(s with { OwnerSig = false, Pc = W_READ, AfterOwner = true, NoRetry = true });
                else if (s.OwnerSig && r.TearCheckedRead && s.Tear != 0) next.Add(tearNoStop with { Pc = W_EXITED });
                else if (s.OwnerSig) next.Add(s with { Pc = W_READ, AfterOwner = false, NoRetry = true });
                else next.Add(s.Tear != 0 ? tearNoStop with { Pc = W_EXITED } : s with { Pc = W_READ, AfterOwner = false });
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
                        // v2.0.3's worker times out and waits again. It
                        // changes nothing, and is taken only beside this
                        // driver's timeout (L_WAIT).
                        else if (d == Driver.V203) next.Add(u with { Pc = W_TOP });
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
                if (d == Driver.This && r.TearCheckedRead && r.TearPostRead && s.Took != 0)
                {
                    // ProcessSharedInput reports whether a READ_REPORT
                    // completed. The teardown check follows the read.
                    next.Add(n with { Served = n.Delivered != s.Delivered, Pc = W_RCHK, AfterOwner = s.AfterOwner });
                    break;
                }
                if (n.Pc == W_TOP && !s.NoRetry && s.RetryNext)
                    n = (CanConnect(n, prog) ? Connect(n, prog, d) : n) with { RetryNext = false };
                next.Add(n);
                // 250 stale wakeups in a row end in a recycle (staleWakeups,
                // driver.c). No run here reaches 251 wakeups: each is a
                // SetEvent, a take or a signal back, and the frames bound
                // them. So that recycle never comes, and only the idle one
                // (eight timeouts in a row) is modeled.
                _ = fresh;
                break;
            }
            case W_OWNER:
                next.Add(OwnerGone(s, d, r));
                break;
            case W_RCHK:
                // A take's read the teardown overtook signals the frame back
                // when no READ_REPORT completed with it: the queues are
                // purged by then.
                bool sent = s.Served || (r.TearCheckDelivered && s.Seen != 0 && s.LastDel == s.Seen);
                if (s.Tear != 0) next.Add(s with { Event = s.Event || !sent, Served = false, AfterOwner = false, Pc = W_EXITED });
                else next.Add(s with { Served = false, AfterOwner = false, Pc = s.AfterOwner ? W_OWNER : W_TOP });
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
        if (d == Driver.This && taken != 0 && s.Prog != 0 && r.Round6 && r.Round6ExactTake && s.Seq != taken) return s;
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
        // A program's frame after a later one of the same program, or after
        // a later program's.
        if (slot % 6 != 0)
        {
            for (int j = slot % 6 + 1; j <= 5; j++)
                if (Count(s.Delivered, Slot(p, j)) > 0) delivered |= 1UL << OrderBit;
            if (LaterSent(s.Delivered, p)) delivered |= 1UL << CrossBit;
        }
        return s with { Delivered = delivered };
    }

    static State DeliverRelease(in State s)
    {
        ulong delivered = Deliver(s.Delivered, RelSlot + s.RelQ - 1);
        if (LaterSent(s.Delivered, s.RelQ)) delivered |= 1UL << CrossBit;
        return s with { Delivered = delivered, RelDone = s.RelFor > 0 ? s.RelDone | 1 << (s.RelFor - 1) : s.RelDone, RelQ = 0, RelFor = 0 };
    }

    // A frame or release frame of a program after p went out.
    static bool LaterSent(ulong delivered, int p)
    {
        for (int q = p + 1; q <= 3; q++)
        {
            if (Count(delivered, RelSlot + q - 1) > 0) return true;
            for (int j = 1; j <= 5; j++) if (Count(delivered, Slot(q, j)) > 0) return true;
        }
        return false;
    }

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
