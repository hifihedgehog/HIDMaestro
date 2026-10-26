// signal-stress [seconds] [--gate-off] [--any-driver]: live and elevated.
// Frames that must all arrive, sent the way a stall would hide: after random
// pauses of 0 to 2 ms, in bursts of 50 to 200 back to back, and after pauses
// of 600 to 1,200 ms, past the driver worker's 500 ms wait. Two passes of
// [seconds] each (120 unless given):
//   a dualshock-4-v2 written with SubmitRawReport, a count in report bytes
//   40 to 43, read by a host's ReadFile on its HID interface.
//   SubmitRawReport takes the data without the report ID, which the driver
//   puts in front, so the count goes in at data bytes 39 to 42
//   an xbox-360-wired written with SubmitState, a count in the two sticks'
//   X axes, read through Windows.Gaming.Input, whose reading comes from the
//   XUSB companion's WAIT_FOR_INPUT completions
// Each submit is matched to the first reading that carries its count or a
// later one. A submit not read within 250 ms fails the pass: a wakeup the
// signal gate lost (docs/design/signal-gate.md) waits for the worker's
// 500 ms timeout. A reading whose count is above every count submitted by
// then fails the pass too, so a count read in the wrong place fails rather
// than matching. Until that check the DualShock 4's count went in at data
// bytes 40 to 43, read back as 256 times itself, and every submit matched
// the next new report of any frame. A reader that fails records the error,
// its Stopwatch time and the last count it read. It reads again after an
// abort the probe did not ask for, reopens the controller's interface (or,
// on the Xbox 360 pad, takes the pad that comes back) after anything else,
// and when it cannot go on, a check of its own fails the pass with the error
// and the count. Until then one failed ReadFile ended the DualShock 4's
// reading for the rest of the pass with nothing recorded, and every later
// frame counted as never read.
//
// The gate's capability words in Global\HIDMaestroSignal<N> tell the SDK
// it may skip a signal, and the driver hosts count whatever they say. So
// each pass switches the gate itself by writing those words: on and off for
// the DualShock 4's input event, and for the Xbox 360 pad also each of its
// two parts alone (the input event, the companion doorbell). A missed signal
// that the companion's 8 ms timer or the worker's 500 ms timeout recovers
// is only late, so each mode with a gate on is also held against the gate
// off by its frames over 4 ms.
//
// That comparison must not move with the machine's load. Summed over a pass
// of 1 s blocks, a build running beside the probe gave the Xbox 360 pad 993
// and 1,188 frames over 4 ms with a gate on against 489 with it off
// (SIG-stress-13), where a quiet machine gave 417 and 580 against 888
// (SIG-stress-12). So a pass runs in pairs of blocks of 500 submits, one
// block with the gate off and one with a gate-on mode, the modes taking the
// pairs in turn, and a coin drawn anew each run picks which block of each
// pair comes first. Both blocks of a pair carry the same number of frames,
// and a pair lasts about a second, so the two meet nearly the same load. A
// pair goes against the mode when the mode's block has more frames over
// 4 ms than the gate-off block, and for it when it has fewer. The mode fails
// when the pairs against it are more than a fair coin gives among the pairs
// that differ, by the exact one-sided sign test (the probability of that
// many or more in a binomial with p 1/2) at 1/4,000 or less, and its blocks
// have more than five frames over 4 ms beyond the gate-off blocks'. The
// five is the old rule's: a handful of late frames never fails a mode. Each
// mode's check prints every pair, so a stall shows as the one pair it hit.
//   With a gate that has no effect on when frames are read, which block of a
// pair reads more frames late is the same whichever block ran the gate, and
// the coin alone decided that. Each pair that differs then goes against the
// mode with probability one half, independently of the other pairs and of
// whatever the load did, so such a mode fails at most once in 4,000 runs,
// and a run's four modes together at most once in 1,000. A gate that does
// change when frames are read, by a missed signal or anything else, fails
// once it is worse in enough of the pairs, whatever the load.
//   Frames over 4 ms count bursts and stalls far more than lost wakeups,
// since a frame whose wakeup is lost waits only until the next submit, which
// comes within 2 ms here. The frames a lost wakeup does delay are the ones
// with nothing after them: a submit with no further submit for at least
// 4 ms, a burst's last or a single one before a pause, or the pass's last if
// it was read. On the DualShock 4 every such final submit must be read
// within 2 ms. A lost wakeup leaves its frame for the next write, the SDK's
// idle repeat 4 ms on (the profile's stream period), and a delivered one is
// read within 0.4 ms on both bench machines. This check counts every final
// submit, in every mode, gated or not, so it also sees a lost wakeup the gate
// does not cause, and a stall over a final submit fails it the same way. On
// the Xbox 360 pad the same final submits are printed and not bounded:
// Windows.Gaming.Input reads the frames before the schedule's pauses 5 to
// 30 ms late on v2.0.3's pair as on 2.1.0's (SIG-ab-ds4-1), past the
// companion's 8 ms timer that recovers a lost doorbell, so a bound there
// would measure Windows.Gaming.Input's own pace. The comparison above, which
// the doorbell control fails 40 pairs of 40, is that pad's guard.
//
// --gate-off keeps the gate off for the whole pass. The HID driver sets its
// capability again every 256 frames, so a mode with the input event's gate
// off clears it before every submit, and a submit that finds it set again
// afterward is reported apart from its mode. It counts in its block like any
// other submit. Against an SDK that makes no section it says so and runs as
// is.
// --any-driver runs on whatever driver is installed, so a v2.0.3 build of
// this probe can stress the newer driver. Builds against v2.0.3's SDK as it
// is.
//
// Four more flags are never part of a default run.
// --miss-doorbell=<N> is the comparison's positive control for the doorbell.
// In a mode with the doorbell gate on, one submit in N starts a hold of
// 16 ms, two periods of the companion's timer. Before each submit of the
// hold the probe moves CompanionParked, the count of parked WAIT_FOR_INPUT
// requests that the SDK reads to decide the doorbell, out of the section,
// and it adds the count back once the hold ends. The SDK then skips the
// doorbell a parked request waits for, and the request waits for the
// companion's 8 ms timer: a missed doorbell that is only late. The two
// doorbell modes must fail the comparison, and the input gate's must pass.
// --miss-input=<N> is the same for the input event. In a mode with the
// input gate on, before one submit in N the probe writes a final SeqNo into
// InputHandoff, which the SDK takes for a read the worker still owes, so it
// puts its own frame there in place of a SetEvent. After the submit the
// probe takes the handoff back, so no take reads the frame either, and it
// waits for the next write: the next submit, or the SDK's idle repeat 4 ms
// on when none follows. That is a missed wakeup that is only late. One left
// in the handoff waits for the worker's 500 ms timeout instead, every frame
// after it waits with it, and the 250 ms bound fails it. The DualShock 4's
// input gate must fail the comparison. The Xbox 360 pad's readings come
// from the companion, so its modes must pass.
// --late-detail prints where each frame over 4 ms waited: the readings
// between its submit and the first reading that carries it (every ReadFile
// on the DualShock 4, every change Windows.Gaming.Input showed on the Xbox
// 360 pad), the time since the reading before that one and since the last
// submit before it, the later submits that came before its reading, and its
// place in its burst. On the DualShock 4 it adds whether the SDK's idle
// repeat, a write of the last frame 4 ms after it (the profile's stream
// period), was due before its reading, and the bytes of the first report
// with a count. On the Xbox 360 pad it names what most likely completed the
// WAIT_FOR_INPUT that carried the frame: a doorbell (a submit under 0.5 ms
// before the reading), the kick of the next request (under 0.5 ms after the
// reading before), the companion's 8 ms timer (a wait or a gap of 7.5 to
// 9 ms), or none of those, a stall. A frame whose wakeup was missed waits
// with the reader idle for the next write. A frame queued behind a burst's
// other reports waits while the reader reads them.
// --input-buffers=<n> gives the DualShock 4 reader's handle n input buffers
// (HidD_SetNumInputBuffers) in place of HID class's default, which caps how
// many reports can queue ahead of a frame.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using Windows.Gaming.Input;

internal static partial class Program
{
    const double StressBoundMs = 250;
    // The paired comparison (header): submits in a block, the frames over
    // 4 ms past the gate-off blocks' that a mode needs before it can fail,
    // and the sign test's level for each mode.
    const int StressBlock = 500;
    const int StressFloor = 5;
    const double StressLevel = 1.0 / 4000;
    // The positive control's hold, two periods of the companion's 8 ms
    // timer (companion.c, WDF_TIMER_CONFIG_INIT_PERIODIC).
    const double MissHoldMs = 16;
    // A final submit (header): no further submit for this long. On the
    // DualShock 4 each must be read within FinalBoundMs.
    const double FinalGapMs = 4;
    const double FinalBoundMs = 2;

    static int SignalStress(string[] args)
    {
        double seconds = 120;
        foreach (string a in args)
            if (double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) && s > 0) seconds = s;
        bool gateOff = args.Contains("--gate-off");
        bool anyDriver = args.Contains("--any-driver");
        bool lateDetail = args.Contains("--late-detail");
        int missEvery = 0, missInputEvery = 0;
        foreach (var (flag, set) in new (string, Action<int>)[] { ("--miss-doorbell=", v => missEvery = v), ("--miss-input=", v => missInputEvery = v) })
        {
            string? miss = args.FirstOrDefault(a => a.StartsWith(flag, StringComparison.Ordinal));
            if (miss == null) continue;
            if (!int.TryParse(miss[flag.Length..], out int every) || every < 1)
            {
                Console.WriteLine($"signal-stress: {miss} needs a whole number of submits, 1 or more");
                return 1;
            }
            set(every);
        }
        uint inputBuffers = 0;
        string? buffers = args.FirstOrDefault(a => a.StartsWith("--input-buffers=", StringComparison.Ordinal));
        if (buffers != null && (!uint.TryParse(buffers["--input-buffers=".Length..], out inputBuffers) || inputBuffers < 2 || inputBuffers > 512))
        {
            Console.WriteLine($"signal-stress: {buffers} needs a count from 2 to 512, HID class's bounds");
            return 1;
        }
        if (!Elevated("signal-stress")) return 3;
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        using (var c = ctx.CreateController(ctx.GetProfile(Ds4)!, s_key))
        {
            if (!anyDriver && !OnEmbeddedDriver(c, Ds4)) return 5;
            StressHid(c, seconds, SignalView(c), gateOff, lateDetail, inputBuffers, missInputEvery);
        }

        var before = new HashSet<Gamepad>(Gamepad.Gamepads);
        using (var c = ctx.CreateController(ctx.GetProfile(Xbox360)!, s_key))
        {
            if (!anyDriver && !OnEmbeddedDriver(c, Xbox360)) return 5;
            Gamepad? pad = null;
            var wait = Stopwatch.StartNew();
            while (pad == null && wait.ElapsedMilliseconds < 20000)
            {
                pad = Gamepad.Gamepads.FirstOrDefault(g => !before.Contains(g));
                if (pad == null) Thread.Sleep(100);
            }
            if (pad == null) Check($"{Xbox360}: a Windows.Gaming.Input gamepad the create added", false, "none in 20 s");
            else StressWgi(c, pad, before, seconds, SignalView(c), gateOff, missEvery, missInputEvery, lateDetail);
        }
        return s_failures == 0 ? 0 : 1;
    }

    const uint FILE_MAP_WRITE = 0x0002;

    /// <summary>The index's signal section mapped for writing, or zero when
    /// the SDK made none (a v2.0.3 build). Left mapped for the process's
    /// life.</summary>
    static IntPtr SignalView(HMController c)
    {
        int index = (int)typeof(HMController).GetProperty("Index", Any)!.GetValue(c)!;
        IntPtr map = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, false, $@"Global\HIDMaestroSignal{index}");
        if (map == IntPtr.Zero)
        {
            Console.WriteLine($"  index {index}: no signal section (error {Marshal.GetLastWin32Error()}), so every frame signals");
            return IntPtr.Zero;
        }
        IntPtr view = MapViewOfFile(map, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, (UIntPtr)256);
        if (view == IntPtr.Zero) CloseHandle(map);
        return view;
    }

    // The modes a pass switches through: bit 0 the input event's gate, bit 1
    // the companion doorbell's. Bit 2 marks a submit in a mode with the
    // input event's gate off that found the HID driver's capability set
    // again once it returned: the driver's AnnounceGate, every 256 frames,
    // may have run between the clear and the SDK's read of the word.
    const int GateInput = 1, GateCompanion = 2, Announced = 4;

    static string ModeName(int mode) => (mode & ~Announced) switch
    {
        0 => "gate off",
        GateInput => "input gate on",
        GateCompanion => "doorbell gate on",
        _ => "both gates on",
    } + ((mode & Announced) != 0 ? ", input capability set again during the submit" : "");

    /// <summary>Writes the capability words for the mode: DriverCaps at 4,
    /// CompanionCaps at 8.</summary>
    static unsafe void SetGate(IntPtr view, int mode)
    {
        if (view == IntPtr.Zero) return;
        Interlocked.Exchange(ref *(int*)((byte*)view + 4), (mode & GateInput) != 0 ? 1 : 0);
        Interlocked.Exchange(ref *(int*)((byte*)view + 8), (mode & GateCompanion) != 0 ? 1 : 0);
    }

    /// <summary>Clears the HID driver's capability, DriverCaps at 4, which
    /// the driver sets again every 256 frames (driver.c AnnounceGate), so
    /// a mode with the input event's gate off keeps it off.</summary>
    static unsafe void HoldInputGateOff(IntPtr view)
    {
        if (view != IntPtr.Zero) Interlocked.Exchange(ref *(int*)((byte*)view + 4), 0);
    }

    /// <summary>The HID driver's capability is set.</summary>
    static unsafe bool InputGateSet(IntPtr view) =>
        view != IntPtr.Zero && (Volatile.Read(ref *(int*)((byte*)view + 4)) & GateInput) != 0;

    /// <summary>The positive control (header): CompanionParked, at 128,
    /// taken out of the section, so the SDK finds no request parked and
    /// skips the doorbell. Returns what was there, for
    /// <see cref="PutParked"/>. The companion counts on from zero
    /// meanwhile, so the true count stays the word plus what the probe
    /// holds.</summary>
    static unsafe int TakeParked(IntPtr view) =>
        Interlocked.Exchange(ref *(int*)((byte*)view + 128), 0);

    /// <summary>Adds back the count <see cref="TakeParked"/> took.</summary>
    static unsafe void PutParked(IntPtr view, int count) =>
        Interlocked.Add(ref *(int*)((byte*)view + 128), count);

    /// <summary>The input event's positive control (header): a final SeqNo,
    /// 2, written into InputHandoff at 68, which the SDK takes for a read
    /// the worker still owes and replaces with its own frame's, with no
    /// SetEvent.</summary>
    static unsafe void OweHandoff(IntPtr view) =>
        Interlocked.Exchange(ref *(int*)((byte*)view + 68), 2);

    /// <summary>Takes InputHandoff back after the submit, so no take of the
    /// worker reads the frame the SDK left there.</summary>
    static unsafe void TakeHandoffBack(IntPtr view) =>
        Interlocked.Exchange(ref *(int*)((byte*)view + 68), 0);

    /// <summary>The submit schedule: a pause of 0 to 2 ms, a burst, or a
    /// pause past the worker's 500 ms wait, until the time is up. The
    /// submits run in pairs of blocks of <see cref="StressBlock"/>, one
    /// block with the gate off and one with the next of the gate-on modes
    /// in <paramref name="modes"/>, in the order a coin picks. Calls
    /// <paramref name="submit"/> with the count and returns each submit's
    /// count, Stopwatch time, mode and block. In a mode with the input
    /// event's gate off, the HID driver's capability is cleared before each
    /// submit, and a submit after which it is set again carries the
    /// Announced bit. With <paramref name="missEvery"/> or
    /// <paramref name="missInputEvery"/> above zero, the positive controls
    /// hold the doorbell's count at zero or take a frame's wakeup (header).
    /// <paramref name="burstOf"/>, when given, gets each submit's burst size,
    /// 1 for a submit alone, and the submits after it in its burst.</summary>
    static List<(int Count, long Time, int Mode, int Block)> StressSchedule(double seconds, Action<int> submit, int[] modes, IntPtr signal,
        int missEvery, int missInputEvery, List<(int Size, int After)>? burstOf = null)
    {
        var rng = new Random(2101);
        // The pair order, apart from the schedule's draws, from a seed of
        // its own each run, which the pass prints.
        int seed = Random.Shared.Next();
        var coin = new Random(seed);
        int[] gated = modes.Where(m => m != 0).ToArray();
        var sent = new List<(int, long, int, int)>(1 << 20);
        long start = Stopwatch.GetTimestamp();
        long end = start + (long)(seconds * Stopwatch.Frequency);
        long missTicks = (long)(MissHoldMs * Stopwatch.Frequency / 1000);
        int n = 0, bursts = 0, gaps = 0, announced = 0, mode = -1, block = -1, pairMode = 0;
        bool gateFirst = false;
        int doorbellSubmits = 0, misses = 0, missed = 0, parkedOut = 0, inputSubmits = 0, inputMisses = 0;
        long missUntil = long.MinValue;
        void Send(int size, int after)
        {
            n++;
            int b = (n - 1) / StressBlock;
            if (b != block)
            {
                block = b;
                int want = 0;
                if (gated.Length > 0)
                {
                    if (b % 2 == 0)
                    {
                        pairMode = gated[b / 2 % gated.Length];
                        gateFirst = coin.Next(2) == 0;
                    }
                    if ((b % 2 == 0) == gateFirst) want = pairMode;
                }
                if (want != mode) { mode = want; SetGate(signal, mode); }
            }
            if (missEvery > 0 && signal != IntPtr.Zero)
            {
                long now = Stopwatch.GetTimestamp();
                bool doorbell = (mode & GateCompanion) != 0;
                if (doorbell && ++doorbellSubmits % missEvery == 0)
                {
                    if (now >= missUntil) misses++;
                    missUntil = now + missTicks;
                }
                if (doorbell && now < missUntil) { parkedOut += TakeParked(signal); missed++; }
                else if (parkedOut != 0) { PutParked(signal, parkedOut); parkedOut = 0; }
            }
            bool hold = (mode & GateInput) == 0 && signal != IntPtr.Zero;
            if (hold) HoldInputGateOff(signal);
            bool owe = missInputEvery > 0 && !hold && signal != IntPtr.Zero && ++inputSubmits % missInputEvery == 0;
            if (owe) { OweHandoff(signal); inputMisses++; }
            long t = Stopwatch.GetTimestamp();
            submit(n);
            if (owe) TakeHandoffBack(signal);
            int m = mode;
            if (hold && InputGateSet(signal)) { m |= Announced; announced++; }
            sent.Add((n, t, m, block));
            burstOf?.Add((size, after));
        }
        try
        {
            while (Stopwatch.GetTimestamp() < end)
            {
                int roll = rng.Next(1000);
                if (roll < 3)
                {
                    Thread.Sleep(rng.Next(600, 1201));
                    gaps++;
                    continue;
                }
                if (roll < 23)
                {
                    int k = rng.Next(50, 201);
                    for (int i = 0; i < k; i++) Send(k, k - 1 - i);
                    bursts++;
                    continue;
                }
                Spin(rng.Next(0, 2001));
                Send(1, 0);
            }
        }
        finally
        {
            // A count held out of the section would leave the SDK skipping
            // the doorbell for this index until its devices go.
            if (parkedOut != 0) PutParked(signal, parkedOut);
            SetGate(signal, 0);
        }
        Console.WriteLine($"    {sent.Count} submits, {bursts} bursts, {gaps} pauses past 500 ms, {announced} with the input capability set again during the submit");
        if (gated.Length > 0)
            Console.WriteLine($"    {n / StressBlock / 2} pairs of blocks of {StressBlock} submits, gate off in one block and on in the other, the order a coin's (seed {seed})");
        if (missEvery > 0 && gated.Any(g => (g & GateCompanion) != 0))
            Console.WriteLine($"    positive control: {misses} holds of {MissHoldMs} ms, one per {missEvery} submits with the doorbell gate on, {missed} submits with the companion's parked count held at zero");
        if (missInputEvery > 0 && gated.Any(g => (g & GateInput) != 0))
            Console.WriteLine($"    positive control: {inputMisses} submits, one per {missInputEvery} with the input gate on, with their wakeup taken");
        return sent;
    }

    /// <summary>Each submit's time to the first reading of its count or a
    /// later one, checked against the bound, and each gate-on mode's frames
    /// over 4 ms against the gate-off block of its pair (header).</summary>
    static void StressVerdict(string name, List<(int Count, long Time, int Mode, int Block)> sent, List<(int Count, long Time)> seen, bool boundFinals)
    {
        var late = new SortedDictionary<int, List<double>>();
        // Each block's mode without the Announced bit, its submits and its
        // frames over 4 ms, a frame never read among them.
        var blocks = new List<(int Mode, int Submits, int Over4)>();
        // Each submit's wait in ms, NaN for one never read.
        var wait = new double[sent.Count];
        int j = 0, missing = 0, over = 0, at = -1;
        double worst = 0;
        foreach (var (count, t0, mode, block) in sent)
        {
            at++;
            wait[at] = double.NaN;
            while (blocks.Count <= block) blocks.Add((mode & ~Announced, 0, 0));
            var bk = blocks[block];
            bk.Submits++;
            while (j < seen.Count && (seen[j].Count < count || seen[j].Time < t0)) j++;
            if (j == seen.Count) { missing++; bk.Over4++; blocks[block] = bk; continue; }
            double ms = (seen[j].Time - t0) * 1000.0 / Stopwatch.Frequency;
            wait[at] = ms;
            if (!late.TryGetValue(mode, out var list)) late[mode] = list = new List<double>();
            list.Add(ms * 1000);
            worst = Math.Max(worst, ms);
            if (ms > StressBoundMs) over++;
            if (ms > 4) bk.Over4++;
            blocks[block] = bk;
        }
        // The last submit of a pass can still be in flight when it ends.
        int tail = Math.Min(missing, 1);
        Check($"{name}: every submit read within {StressBoundMs} ms",
            over == 0 && missing - tail == 0,
            FormattableString.Invariant($"{sent.Count} submits, {seen.Count} readings, {over} over the bound, {missing} never read, worst {worst:F1} ms"));
        foreach (var (mode, list) in late)
        {
            int over2 = list.Count(x => x > 2000), over4 = list.Count(x => x > 4000);
            Report($"{name}, {ModeName(mode)}: submit to the first reading that carries it, microseconds", list, 1,
                FormattableString.Invariant($", {over2} over 2 ms, {over4} over 4 ms"));
        }
        FinalVerdict(name, sent, wait, boundFinals);
        var pairs = BlockPairs(blocks);
        foreach (int mode in blocks.Select(x => x.Mode).Where(m => m != 0).Distinct().OrderBy(m => m))
        {
            var list = pairs.TryGetValue(mode, out var got) ? got : new List<(int On, int Off)>();
            var v = PairVerdict(list);
            Check($"{name}, {ModeName(mode)}: frames over 4 ms against the gate-off block of each pair, worse in no more pairs than a sign test allows at 1/4,000, or by {StressFloor} frames or fewer",
                !v.Fails,
                FormattableString.Invariant($"worse in {v.Against} of {list.Count} pairs, better in {v.For}, even in {list.Count - v.Against - v.For}, one-sided p {v.P:G3}; {list.Sum(x => x.On)} frames over 4 ms in its blocks, {list.Sum(x => x.Off)} in the gate-off blocks"));
            // Every pair, so a stall shows as the one pair it landed in.
            Console.WriteLine("    its pairs in order, frames over 4 ms in its block/in the gate-off block, . where even: " +
                (list.Count == 0 ? "none" : string.Join(" ", list.Select(x => x.On == x.Off ? "." : $"{x.On}/{x.Off}"))));
        }
    }

    static string ShortMode(int mode) =>
        ((mode & ~Announced) switch { 0 => "off", GateInput => "input", GateCompanion => "door", _ => "both" })
        + ((mode & Announced) != 0 ? "*" : "");

    /// <summary>The final submits (header): each submit with no further
    /// submit for <see cref="FinalGapMs"/>, and the pass's last one when it
    /// was read. Indexes into <paramref name="sent"/>, whose waits in ms are
    /// <paramref name="wait"/>, NaN for one never read.</summary>
    internal static List<int> FinalSubmits(List<(int Count, long Time, int Mode, int Block)> sent, double[] wait)
    {
        long gap = (long)(FinalGapMs * Stopwatch.Frequency / 1000);
        var finals = new List<int>();
        for (int i = 0; i < sent.Count; i++)
        {
            bool last = i + 1 == sent.Count;
            if (last ? !double.IsNaN(wait[i]) : sent[i + 1].Time - sent[i].Time >= gap) finals.Add(i);
        }
        return finals;
    }

    /// <summary>The final submits' waits (header). With
    /// <paramref name="bound"/>, a check that each was read within
    /// <see cref="FinalBoundMs"/>, a final submit never read counting as
    /// over it. Without, the same figures on a line of their own. Returns the
    /// final submits over the bound.</summary>
    internal static int FinalVerdict(string name, List<(int Count, long Time, int Mode, int Block)> sent, double[] wait, bool bound)
    {
        var finals = FinalSubmits(sent, wait);
        bool Over(int i) => double.IsNaN(wait[i]) || wait[i] > FinalBoundMs;
        var overs = finals.Where(Over).ToList();
        var read = finals.Where(i => !double.IsNaN(wait[i])).ToList();
        string F(double v) => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        string byMode = string.Join(", ", finals.GroupBy(i => sent[i].Mode & ~Announced).OrderBy(g => g.Key)
            .Select(g => $"{ShortMode(g.Key)} {g.Count()} ({g.Count(Over)} over)"));
        string listed = string.Join("; ", overs.Take(12).Select(i =>
            $"count {sent[i].Count} {ShortMode(sent[i].Mode)} {(double.IsNaN(wait[i]) ? "never read" : F(wait[i]) + " ms")}"));
        string detail = $"{finals.Count} final submits, {overs.Count} over {F(FinalBoundMs)} ms, worst {(read.Count == 0 ? "-" : F(read.Max(i => wait[i])))} ms; by mode {byMode}"
            + (overs.Count > 0 ? "; over: " + listed + (overs.Count > 12 ? $"; {overs.Count - 12} more" : "") : "");
        if (bound)
            Check($"{name}: every submit with no further submit for {F(FinalGapMs)} ms read within {F(FinalBoundMs)} ms", overs.Count == 0, detail);
        else
            Console.WriteLine($"  {name}: submits with no further submit for {F(FinalGapMs)} ms, not bounded on this pad (header): {detail}");
        return overs.Count;
    }

    /// <summary>A pass's pairs, by gate-on mode, as frames over 4 ms in the
    /// mode's block and in the gate-off block: blocks 0 and 1, 2 and 3, and
    /// so on, as <see cref="StressSchedule"/> runs them. A pair needs both
    /// blocks full and the gate off in one of them, so the last block of a
    /// pass, cut short, leaves its pair out.</summary>
    static SortedDictionary<int, List<(int On, int Off)>> BlockPairs(List<(int Mode, int Submits, int Over4)> blocks)
    {
        var pairs = new SortedDictionary<int, List<(int On, int Off)>>();
        for (int b = 0; b + 1 < blocks.Count; b += 2)
        {
            var (x, y) = (blocks[b], blocks[b + 1]);
            if (x.Submits != StressBlock || y.Submits != StressBlock || (x.Mode == 0) == (y.Mode == 0)) continue;
            var (gate, off) = x.Mode != 0 ? (x, y) : (y, x);
            if (!pairs.TryGetValue(gate.Mode, out var list)) pairs[gate.Mode] = list = new List<(int, int)>();
            list.Add((gate.Over4, off.Over4));
        }
        return pairs;
    }

    /// <summary>The paired comparison of a gate-on mode (header): the pairs
    /// worse with the gate on, the pairs better, the one-sided sign test's p
    /// for the worse ones among the pairs that differ, the mode's frames over
    /// 4 ms beyond the gate-off blocks', and whether the mode fails.</summary>
    internal static (int Against, int For, double P, int Excess, bool Fails) PairVerdict(IEnumerable<(int On, int Off)> pairs)
    {
        int against = 0, forIt = 0, excess = 0;
        foreach (var (on, off) in pairs)
        {
            if (on > off) against++;
            else if (off > on) forIt++;
            excess += on - off;
        }
        double p = SignTail(against, against + forIt);
        return (against, forIt, p, excess, p <= StressLevel && excess > StressFloor);
    }

    /// <summary>The probability of <paramref name="k"/> or more successes in
    /// <paramref name="n"/> trials of a fair coin, summed term by term from
    /// logarithms, exact to rounding for the few hundred pairs a pass
    /// has.</summary>
    internal static double SignTail(int k, int n)
    {
        if (k <= 0) return 1;
        if (k > n) return 0;
        double logN = LogFactorial(n), half = n * Math.Log(0.5), sum = 0;
        for (int i = k; i <= n; i++)
            sum += Math.Exp(logN - LogFactorial(i) - LogFactorial(n - i) + half);
        return Math.Min(1, sum);
    }

    static double LogFactorial(int m)
    {
        double s = 0;
        for (int i = 2; i <= m; i++) s += Math.Log(i);
        return s;
    }

    /// <summary>Offline checks of the paired comparison for the selftest:
    /// the sign test's tail against exact fractions, the fewest pairs that
    /// fail a mode, the pairing of blocks, the rule on pairs placed on each
    /// side of it, and the rule on simulated modes, with no effect and with
    /// one, under a load that drifts from block to block, jumps tenfold at
    /// random and rises across every pair.</summary>
    internal static IEnumerable<(string Name, bool Ok, string Detail)> StressSelfTest()
    {
        static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-15 + 1e-9 * Math.Abs(b);
        yield return ("signal-stress: the sign test's tail matches 11/1024, 1/4096 and 13/4096",
            Near(SignTail(9, 10), 11.0 / 1024) && Near(SignTail(12, 12), 1.0 / 4096) && Near(SignTail(11, 12), 13.0 / 4096)
            && SignTail(0, 0) == 1 && SignTail(5, 4) == 0,
            FormattableString.Invariant($"{SignTail(9, 10):R} {SignTail(12, 12):R} {SignTail(11, 12):R}"));

        // Exact values (binomial sums in integers): the fewest pairs against
        // a mode that fail it at 1/4,000, out of those that differ. None of
        // 11 can: 11 of 11 has the chance 1/2048.
        var fewest = new (int Counted, int Against)[] { (11, 0), (12, 12), (20, 18), (40, 32), (120, 80) };
        int Fewest(int counted)
        {
            for (int k = 1; k <= counted; k++)
                if (SignTail(k, counted) <= StressLevel) return k;
            return 0;
        }
        yield return ("signal-stress: the fewest pairs against a mode that fail it: none of 11, 12 of 12, 18 of 20, 32 of 40, 80 of 120",
            fewest.All(f => Fewest(f.Counted) == f.Against),
            string.Join(" ", fewest.Select(f => $"{f.Counted}:{Fewest(f.Counted)}")));

        // Blocks as a pass leaves them: gate off first or second in a pair,
        // and a last pair cut short, or with both blocks off, left out.
        var blocks = new List<(int Mode, int Submits, int Over4)>
        {
            (0, StressBlock, 3), (GateInput, StressBlock, 30), (GateCompanion, StressBlock, 40), (0, StressBlock, 4),
            (0, StressBlock, 5), (0, StressBlock, 6), (GateInput | GateCompanion, StressBlock, 50), (0, StressBlock, 7),
            (0, StressBlock, 8), (GateInput, StressBlock - 1, 9),
        };
        var grouped = BlockPairs(blocks);
        string Shown(int m) => grouped.TryGetValue(m, out var l) ? string.Join(" ", l.Select(x => $"{x.On}/{x.Off}")) : "none";
        yield return ("signal-stress: blocks pair as the schedule runs them, the gate's block on either side, a pair cut short or all off left out",
            grouped.Count == 3 && Shown(GateInput) == "30/3" && Shown(GateCompanion) == "40/4" && Shown(GateInput | GateCompanion) == "50/7",
            $"input {Shown(GateInput)}, doorbell {Shown(GateCompanion)}, both {Shown(GateInput | GateCompanion)}");

        // Final submits: gaps of 1, 0.5, 8.5, 3.9 and 16.1 ms after submits
        // 1 to 5, and submit 6 last and never read. Submits 3 and 5 are
        // final; 4 is not (3.9 ms), and 6 is not, never read at the end.
        static long T(double ms) => (long)(ms * Stopwatch.Frequency / 1000);
        var fs = new List<(int Count, long Time, int Mode, int Block)>
        {
            (1, T(0), 0, 0), (2, T(1), 0, 0), (3, T(1.5), GateInput, 0), (4, T(10), 0, 0), (5, T(13.9), 0, 0), (6, T(30), 0, 0),
        };
        var fw = new[] { 0.05, 0.05, 4.2, 0.1, 0.06, double.NaN };
        var finals = FinalSubmits(fs, fw);
        yield return ("signal-stress: a final submit has no further submit for 4 ms, the last one counting only when read",
            string.Join(" ", finals.Select(i => fs[i].Count)) == "3 5" && finals.Count(i => fw[i] > FinalBoundMs) == 1,
            "final: " + string.Join(" ", finals.Select(i => fs[i].Count)));

        static List<(int On, int Off)> Same(int pairs, int on, int off) => Enumerable.Repeat((on, off), pairs).ToList();
        static bool Fails(List<(int On, int Off)> p) => PairVerdict(p).Fails;
        var split = Same(20, 60, 10).Concat(Same(20, 10, 60)).ToList();
        var offset = Same(20, 1, 0).Concat(Same(1, 0, 16)).ToList();
        yield return ("signal-stress: 40 pairs at 60 against 10 fail, 12 at 11 against 10 fail and 11 pass, 40 even pairs and 20 each way pass",
            Fails(Same(40, 60, 10)) && Fails(Same(12, 11, 10)) && !Fails(Same(11, 11, 10)) && !Fails(Same(40, 3, 3)) && !Fails(split),
            FormattableString.Invariant($"p {PairVerdict(Same(40, 60, 10)).P:G3}, {PairVerdict(Same(12, 11, 10)).P:G3}, {PairVerdict(Same(11, 11, 10)).P:G3}, {PairVerdict(split).P:G3}"));
        yield return ("signal-stress: 20 pairs at 1 against 0 fail, and pass once one pair at 0 against 16 leaves the mode 4 frames over, the floor",
            Fails(Same(20, 1, 0)) && !Fails(offset) && PairVerdict(offset).P <= StressLevel && PairVerdict(offset).Excess == 4,
            FormattableString.Invariant($"p {PairVerdict(Same(20, 1, 0)).P:G3} and {PairVerdict(offset).P:G3}, frames over {PairVerdict(Same(20, 1, 0)).Excess} and {PairVerdict(offset).Excess}"));

        // Simulated modes of 40 pairs, a pass's share of each Xbox 360 mode.
        // A block's frames over 4 ms scatter as far as their mean (an
        // exponential draw), and one block in 20 has ten times as many. The
        // load's mean drifts by up to 65% from pair to pair, and the second
        // block of every pair has half again the first one's.
        const int trials = 20000;
        var r = new Random(4000);
        static int Late(Random r, double mean)
        {
            int c = (int)(mean * -Math.Log(1 - r.NextDouble()));
            return r.NextDouble() < 0.05 ? c * 10 : c;
        }
        int coined = 0, fixedOrder = 0, caught = 0, caughtFew = 0;
        var a = new List<(int On, int Off)>(40);
        var f2 = new List<(int On, int Off)>(40);
        var e = new List<(int On, int Off)>(40);
        var e2 = new List<(int On, int Off)>(40);
        for (int s = 0; s < trials; s++)
        {
            a.Clear(); f2.Clear(); e.Clear(); e2.Clear();
            double load = 10;
            for (int p = 0; p < 40; p++)
            {
                double first = load, second = load * 1.5;
                bool gateFirst = r.Next(2) == 0;
                double gate = gateFirst ? first : second, off = gateFirst ? second : first;
                a.Add((Late(r, gate), Late(r, off)));
                f2.Add((Late(r, second), Late(r, first)));
                e.Add((Late(r, gate * 8), Late(r, off)));
                // A twentieth of the load: half a late frame per block.
                e2.Add((Late(r, gate * 8 / 20), Late(r, off / 20)));
                load = Math.Clamp(load * Math.Exp(r.NextDouble() - 0.5), 2, 200);
            }
            if (Fails(a)) coined++;
            if (Fails(f2)) fixedOrder++;
            if (Fails(e)) caught++;
            if (Fails(e2)) caughtFew++;
        }
        // At the level 5 of 20,000 would fail. A bound of three times that
        // fails for a test at the level about once in 14,500 seeds.
        yield return ("signal-stress: with no effect and the coin, at most 15 of 20,000 simulated modes fail, where the level gives 5",
            coined <= 15,
            $"{coined} of {trials} failed, {fixedOrder} with the gate always in the second block instead");
        yield return ("signal-stress: a mode with eight times the frames over 4 ms fails 9 simulated modes in 10 or more, at the load above and at a twentieth of it",
            caught * 10 >= trials * 9 && caughtFew * 10 >= trials * 9,
            $"{caught} and {caughtFew} of {trials} failed");
    }

    static void StressHid(HMController c, double seconds, IntPtr signal, bool gateOff, bool lateDetail, uint inputBuffers, int missInputEvery)
    {
        using var dev = OpenStateInterface(c, out string why);
        if (dev == null) { Check($"{Ds4}: a HID interface with input reports", false, why); return; }
        if (lateDetail || inputBuffers > 0)
        {
            bool had = HidD_GetNumInputBuffers(dev.Handle, out uint before);
            if (inputBuffers > 0 && !HidD_SetNumInputBuffers(dev.Handle, inputBuffers))
                Check($"{Ds4}: the reader's handle takes {inputBuffers} input buffers", false, $"HidD_SetNumInputBuffers error {Marshal.GetLastWin32Error()}");
            bool has = HidD_GetNumInputBuffers(dev.Handle, out uint now);
            Console.WriteLine($"  {Ds4}: the reader's handle has {(has ? now.ToString() : "an unknown number of")} input buffers" +
                (inputBuffers > 0 ? $", {(had ? before.ToString() : "an unknown number")} before --input-buffers" : ""));
        }
        var seen = new List<(int, long)>(1 << 20);
        // --late-detail: every reading, a count read again included.
        var reads = lateDetail ? new List<(int, long)>(1 << 20) : null;
        var burstOf = lateDetail ? new List<(int Size, int After)>(1 << 20) : null;
        // SubmitRawReport takes a report's data without its ID, and the
        // driver puts report ID 0x01 in front of it (HMController
        // SubmitRawReportCore, driver.c ProcessSharedInput), so data byte i
        // goes out as report byte i + 1. The count the reader takes from
        // report bytes 40 to 43 goes in at data bytes 39 to 42. Sticks
        // centered, hat neutral.
        var data = new byte[63];
        data[0] = data[1] = data[2] = data[3] = 0x80;
        data[4] = 0x08;
        // The highest count submitted so far, set before its submit, so a
        // reading can only carry a count at or below it. A reading above it
        // is a misread, and fails the pass.
        int highest = 0, misread = 0, misreadValue = 0;
        byte[]? firstReport = null;
        bool stop = false;
        // A failed ReadFile is recorded with its error, its Stopwatch time and
        // the last count read before it. An abort the probe did not ask for
        // reads again. Any other error reopens the controller's interface,
        // which a device that went and came back offers again. A reader that
        // cannot go on records why and stops, and the pass fails on it, so a
        // dead reader never passes as late frames.
        var failures = new ReaderFailures();
        HidDevice current = dev;
        var reopened = new List<HidDevice>();
        var reader = new Thread(() =>
        {
            var buf = new byte[dev.InputLength];
            int last = -1, inARow = 0;
            while (!Volatile.Read(ref stop))
            {
                if (!ReadFile(Volatile.Read(ref current).Handle, buf, buf.Length, out int read, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    long at = Stopwatch.GetTimestamp();
                    // The probe's own CancelIoEx, as the pass ends.
                    if (Volatile.Read(ref stop)) break;
                    failures.Add(error, at, last);
                    if (++inARow > 20) { failures.Stop(error, at, last, "20 failed reads in a row"); return; }
                    if (error == ERROR_OPERATION_ABORTED) continue;
                    HidDevice? again;
                    string lost;
                    try { again = OpenStateInterface(c, out lost); }
                    catch (Exception ex) { again = null; lost = ex.GetType().Name + ": " + ex.Message; }
                    if (again == null) { failures.Stop(error, at, last, "no interface to reopen: " + lost); return; }
                    if (inputBuffers > 0) HidD_SetNumInputBuffers(again.Handle, inputBuffers);
                    lock (reopened) reopened.Add(again);
                    Volatile.Write(ref current, again);
                    continue;
                }
                inARow = 0;
                long t = Stopwatch.GetTimestamp();
                if (read < 44 || buf[0] != 0x01) continue;
                int count = BitConverter.ToInt32(buf, 40);
                if (count < 0 || count > Volatile.Read(ref highest))
                {
                    if (misread++ == 0) misreadValue = count;
                    continue;
                }
                if (firstReport == null && count > 0) firstReport = buf.ToArray();
                if (reads != null) lock (seen) reads.Add((count, t));
                if (count != last) { lock (seen) seen.Add((count, t)); last = count; }
            }
        }) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "stress reader" };
        reader.Start();
        c.SubmitRawReport(data);
        Thread.Sleep(300);
        Console.WriteLine($"  {Ds4}, SubmitRawReport to ReadFile:");
        var modes = signal == IntPtr.Zero || gateOff ? new[] { 0 } : new[] { 0, GateInput };
        var sent = StressSchedule(seconds, n =>
        {
            BitConverter.TryWriteBytes(data.AsSpan(39), n);
            Volatile.Write(ref highest, n);
            c.SubmitRawReport(data);
        }, modes, signal, 0, missInputEvery, burstOf);
        Thread.Sleep((int)StressBoundMs * 2);
        Volatile.Write(ref stop, true);
        CancelIoEx(Volatile.Read(ref current).Handle, IntPtr.Zero);
        if (!reader.Join(2000)) { CancelIoEx(Volatile.Read(ref current).Handle, IntPtr.Zero); reader.Join(2000); }
        lock (reopened) foreach (var h in reopened) h.Dispose();
        List<(int, long)> got;
        List<(int, long)>? all = null;
        lock (seen)
        {
            got = seen.ToList();
            all = reads?.ToList();
        }
        long passStart = sent.Count > 0 ? sent[0].Time : 0;
        failures.Report($"{Ds4} ReadFile", passStart, reopened.Count, "reopens");
        Check($"{Ds4} ReadFile: every reading carries a count the probe submitted, at report bytes 40 to 43",
            misread == 0, misread == 0 ? $"{got.Count} counts read" : $"{misread} readings carried none, the first {misreadValue}");
        StressVerdict($"{Ds4} ReadFile", sent, got, boundFinals: true);
        if (all != null && burstOf != null)
        {
            if (firstReport != null)
                Console.WriteLine($"  {Ds4} ReadFile, --late-detail: the first report with a count, bytes 0 to 7 {Convert.ToHexString(firstReport, 0, 8)}, " +
                    $"bytes 36 to 47 {Convert.ToHexString(firstReport, 36, 12)}, count at bytes 40 to 43 {BitConverter.ToInt32(firstReport, 40)}");
            LateDetail($"{Ds4} ReadFile", sent, burstOf, got, all, wgi: false);
        }
    }

    /// <summary>--late-detail (header): each frame over 4 ms, where it
    /// waited, a summary by mode, and each mode's bursts of 50 or more: the
    /// readings that carried one of a burst's counts, and the wait of its
    /// last frame. Times run from the submit's Stopwatch stamp, readings from
    /// ReadFile's return, or on the Xbox 360 pad (<paramref name="wgi"/>)
    /// from the poll that saw Windows.Gaming.Input's reading change, where
    /// <paramref name="reads"/> holds only the changes. There the last
    /// column says what most likely completed the WAIT_FOR_INPUT that
    /// carried the frame (header).</summary>
    static void LateDetail(string name, List<(int Count, long Time, int Mode, int Block)> sent, List<(int Size, int After)> burstOf,
        List<(int Count, long Time)> seen, List<(int Count, long Time)> reads, bool wgi)
    {
        // The DualShock 4 profile's stream period: the SDK writes the last
        // frame again this long after it while no submit comes
        // (HMController.RepeatLastFrame). The Xbox 360 profile has none.
        const double IdleRepeatMs = 4;
        const int MaxRows = 600;
        double tickMs = 1000.0 / Stopwatch.Frequency;
        // The first index whose time is past t, or at or past it, in a list
        // in time order.
        static int After(List<(int Count, long Time)> list, long t, bool orAt)
        {
            int lo = 0, hi = list.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (list[mid].Time > t || (orAt && list[mid].Time == t)) hi = mid; else lo = mid + 1;
            }
            return lo;
        }
        static int SubmitAtOrAfter(List<(int Count, long Time, int Mode, int Block)> list, long t)
        {
            int lo = 0, hi = list.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (list[mid].Time >= t) hi = mid; else lo = mid + 1;
            }
            return lo;
        }
        string F(double v) => double.IsNaN(v) ? "-" : v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        string Short(int mode) => ((mode & ~Announced) switch { 0 => "off", GateInput => "input", GateCompanion => "door", _ => "both" })
            + ((mode & Announced) != 0 ? "*" : "");
        // What completed the request that carried a late Xbox 360 frame, as
        // well as the probe can tell. A submit just before the reading: its
        // doorbell. The reading just after the one before it: the kick of the
        // next WAIT_FOR_INPUT, which comes back at once with a newer frame. A
        // wait or a gap of 7.5 to 9 ms: the companion's 8 ms timer, which is
        // what a missed doorbell waits for. Anything else: a stall.
        static string Cause(double since, double gap, double wait) =>
            since < 0.5 ? "doorbell" : gap < 0.5 ? "kick" : (gap >= 7.5 && gap <= 9) || (wait >= 7.5 && wait <= 9) ? "timer" : "other";

        var late = new SortedDictionary<int, List<(int Between, int Size, int After, int Later, string Why)>>();
        var rows = new List<string>();
        int j = 0;
        for (int i = 0; i < sent.Count; i++)
        {
            var (count, t0, mode, _) = sent[i];
            while (j < seen.Count && (seen[j].Count < count || seen[j].Time < t0)) j++;
            if (j == seen.Count) break;
            long tc = seen[j].Time;
            double ms = (tc - t0) * tickMs;
            if (ms <= 4) continue;
            int from = After(reads, t0, false), at = After(reads, tc, true);
            int between = Math.Max(0, at - from);
            // The reading before the carrying one, and how long before it.
            double gap = at > 0 ? (tc - reads[at - 1].Time) * tickMs : double.NaN;
            int next = SubmitAtOrAfter(sent, tc);
            int later = Math.Max(0, next - (i + 1));
            double toNext = i + 1 < sent.Count ? (sent[i + 1].Time - t0) * tickMs : double.NaN;
            // From the last submit before the reading to the reading.
            double since = (tc - sent[Math.Max(i, next - 1)].Time) * tickMs;
            string why = wgi ? Cause(since, gap, ms) : since > IdleRepeatMs ? "idle" : "-";
            var (size, after) = burstOf[i];
            int key = mode & ~Announced;
            if (!late.TryGetValue(key, out var list)) late[key] = list = new();
            list.Add((between, size, after, later, why));
            if (rows.Count < MaxRows)
                rows.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"    {count,7} {Short(mode),-6} {ms,6:F2} {between,7} {F(gap),7} {F(since),8} {later,6} {F(toNext),7} {size,5} {after,5} {why,-8} {seen[j].Count - count,6}"));
        }
        int total = late.Values.Sum(l => l.Count);
        Console.WriteLine($"  {name}, --late-detail: {total} frames over 4 ms. Columns: the frame's count, its mode (* the input capability set again during the submit),");
        Console.WriteLine("  its wait in ms, the readings between its submit and its reading, ms from the reading before to its reading, ms from the last submit");
        Console.WriteLine("  before its reading to its reading, the later submits before its reading, ms to the next submit, its burst's size (1 alone), the");
        Console.WriteLine(wgi
            ? "  submits after it in the burst, what most likely completed its WAIT_FOR_INPUT (doorbell: a submit under 0.5 ms before; kick: under 0.5 ms"
            : $"  submits after it in the burst, idle where the SDK's idle repeat ({IdleRepeatMs} ms after the last submit) was due before its reading,");
        Console.WriteLine(wgi
            ? "  after the reading before; timer: a wait or a gap of 7.5 to 9 ms; other: none of those, a stall), and how far past its count the reading's is."
            : "  and how far past its count the reading's count is.");
        Console.WriteLine("      count mode       ms between  gap_ms since_ms later next_ms burst after why       ahead");
        foreach (string row in rows) Console.WriteLine(row);
        if (total > rows.Count) Console.WriteLine($"    {total - rows.Count} more not shown");
        foreach (var (mode, list) in late)
        {
            var b = list.Select(x => x.Between).OrderBy(x => x).ToList();
            int inBurst = list.Count(x => x.Size >= 50);
            string why = string.Join(", ", list.GroupBy(x => x.Why).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"));
            Console.WriteLine(FormattableString.Invariant(
                $"  {name}, {ModeName(mode)}: {list.Count} frames over 4 ms. Readings between submit and reading: none {b.Count(x => x == 0)}, 1 to 7 {b.Count(x => x is >= 1 and <= 7)}, 8 to 31 {b.Count(x => x is >= 8 and <= 31)}, 32 or more {b.Count(x => x >= 32)}, median {b[b.Count / 2]}. In a burst of 50 or more {inBurst}, its last submit {list.Count(x => x.Size >= 50 && x.After == 0)}, within 8 of its end {list.Count(x => x.Size >= 50 && x.After < 8)}. A later submit before the reading {list.Count(x => x.Later > 0)}. Last column: {why}."));
        }
        // Each burst of 50 or more inside one block: the readings whose count
        // is one of its own, and its last submit's wait, by mode.
        var bursts = new SortedDictionary<int, List<(int Readings, double LastMs)>>();
        int s = 0, k2 = 0;
        for (int i = 0; i < sent.Count;)
        {
            var (size, after) = burstOf[i];
            if (size < 50 || after != size - 1 || i + size > sent.Count) { i++; continue; }
            int a = sent[i].Count, z = sent[i + size - 1].Count;
            while (s < seen.Count && seen[s].Count < a) s++;
            int e = s;
            while (e < seen.Count && seen[e].Count <= z) e++;
            var (lastCount, lastTime, _, lastBlock) = sent[i + size - 1];
            while (k2 < seen.Count && (seen[k2].Count < lastCount || seen[k2].Time < lastTime)) k2++;
            double lastMs = k2 < seen.Count ? (seen[k2].Time - lastTime) * tickMs : double.NaN;
            if (sent[i].Block == lastBlock)
            {
                int key = sent[i].Mode & ~Announced;
                if (!bursts.TryGetValue(key, out var list)) bursts[key] = list = new();
                list.Add((e - s, lastMs));
            }
            i += size;
        }
        foreach (var (mode, list) in bursts)
        {
            var r = list.Select(x => x.Readings).OrderBy(x => x).ToList();
            var w = list.Select(x => x.LastMs).Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
            double P(List<double> v, double q) => v.Count == 0 ? double.NaN : v[Math.Clamp((int)Math.Ceiling(q * v.Count) - 1, 0, v.Count - 1)];
            Console.WriteLine(FormattableString.Invariant(
                $"  {name}, {ModeName(mode)}: {list.Count} bursts of 50 or more. Readings that carried one of a burst's counts: median {r[r.Count / 2]}, mean {r.Average():F1}, max {r[^1]}. The last submit's wait: median {F(P(w, 0.5))} ms, p95 {F(P(w, 0.95))} ms, max {F(w.Count > 0 ? w[^1] : double.NaN)} ms, {w.Count(x => x > 4)} over 4 ms."));
        }
    }

    static void StressWgi(HMController c, Gamepad pad, HashSet<Gamepad> before, double seconds, IntPtr signal, bool gateOff, int missEvery, int missInputEvery, bool lateDetail)
    {
        // A count in the two sticks' X axes, 200 steps each, apart by more
        // than the 16-bit quantization on the way to Windows.Gaming.Input.
        // The axes come from the profile's layout: on xbox-360-wired the
        // right stick's X is Rx, and Z is the combined trigger, where the
        // count's high digit never reached RightThumbstickX.
        static float Axis(int digit) => 0.05f + digit * 0.0045f;
        static int Digit(double wgi) => (int)Math.Round(((wgi + 1) / 2 - 0.05) / 0.0045);
        var sticks = c.Profile.Sticks;
        if (sticks.Count < 2) { Check($"{Xbox360}: a profile with two sticks", false, $"{sticks.Count} sticks"); return; }
        HMAxis leftX = sticks[0].XAxis, rightX = sticks[1].XAxis;
        var seen = new List<(int, long)>(1 << 20);
        var burstOf = lateDetail ? new List<(int Size, int After)>(1 << 20) : null;
        bool stop = false;
        // As on the DualShock 4: a count above the highest submitted is a
        // misread, and fails the pass.
        int highest = 0, misread = 0, misreadValue = 0;
        string? firstReading = null;
        var state = new HMGamepadState { Axes = new Dictionary<HMAxis, float>() };
        // As on the DualShock 4, the poller's failures are recorded: a
        // reading that throws, with its HRESULT, and the pad leaving
        // Windows.Gaming.Input's list. It reads again after a throw, and after
        // a removal it takes the pad that comes back within 5 s. A poller that
        // cannot go on records why and stops, and the pass fails on it.
        var failures = new ReaderFailures();
        int rebound = 0;
        Gamepad current = pad;
        var poller = new Thread(() =>
        {
            int last = -1, unwrapped = 0, prev = 0, lastBad = -1, inARow = 0;
            long nextLook = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
            while (!Volatile.Read(ref stop))
            {
                GamepadReading r;
                try { r = current.GetCurrentReading(); }
                catch (Exception ex)
                {
                    long at = Stopwatch.GetTimestamp();
                    failures.Add(ex.HResult, at, unwrapped, ex.GetType().Name);
                    if (++inARow > 20) { failures.Stop(ex.HResult, at, unwrapped, "20 failed readings in a row"); return; }
                    Thread.Sleep(1);
                    continue;
                }
                inARow = 0;
                long t = Stopwatch.GetTimestamp();
                bool listed = true;
                if (t >= nextLook)
                {
                    nextLook = t + Stopwatch.Frequency / 10;
                    try { listed = Gamepad.Gamepads.Contains(current); }
                    catch (Exception ex) { failures.Add(ex.HResult, t, unwrapped, "Gamepad.Gamepads threw " + ex.GetType().Name); }
                    if (!listed)
                    {
                        failures.Add(0, t, unwrapped, "the pad left Gamepad.Gamepads");
                        Gamepad? back = null;
                        var wait = Stopwatch.StartNew();
                        while (back == null && !Volatile.Read(ref stop) && wait.ElapsedMilliseconds < 5000)
                        {
                            try { back = Gamepad.Gamepads.FirstOrDefault(g => !before.Contains(g) && !ReferenceEquals(g, current)); }
                            catch (Exception) { back = null; }
                            if (back == null) Thread.Sleep(50);
                        }
                        if (back == null) { failures.Stop(0, t, unwrapped, "the pad left Gamepad.Gamepads and none came back in 5 s"); return; }
                        current = back;
                        rebound++;
                        continue;
                    }
                }
                int x = Digit(r.LeftThumbstickX), y = Digit(r.RightThumbstickX);
                if (x < 0 || x > 199 || y < 0 || y > 199) continue;
                int code = y * 200 + x;
                // The first reading counts once it carries a count the probe
                // has submitted. Before that the pad shows its own state.
                if (code == last || (last < 0 && code > Volatile.Read(ref highest))) continue;
                // Counts go up, 40,000 codes a lap.
                int delta = ((code - prev) % 40000 + 40000) % 40000;
                int count = last >= 0 ? unwrapped + delta : code;
                if (count > Volatile.Read(ref highest))
                {
                    if (code != lastBad && misread++ == 0) misreadValue = count;
                    lastBad = code;
                    continue;
                }
                unwrapped = count;
                prev = code;
                last = code;
                seen.Add((unwrapped, t));
                if (firstReading == null && count > 0)
                    firstReading = FormattableString.Invariant(
                        $"LeftThumbstickX {r.LeftThumbstickX:F5}, RightThumbstickX {r.RightThumbstickX:F5}, digits {x} and {y}, count {count}");
            }
        }) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "stress wgi" };
        state.Axes[leftX] = Axis(0);
        state.Axes[rightX] = Axis(0);
        c.SubmitState(state);
        Thread.Sleep(500);
        poller.Start();
        Console.WriteLine($"  {Xbox360}, SubmitState to Windows.Gaming.Input, the count in {leftX} and {rightX}:");
        var modes = signal == IntPtr.Zero || gateOff ? new[] { 0 } : new[] { 0, GateInput, GateCompanion, GateInput | GateCompanion };
        var sent = StressSchedule(seconds, n =>
        {
            int code = n % 40000;
            state.Axes[leftX] = Axis(code % 200);
            state.Axes[rightX] = Axis(code / 200);
            Volatile.Write(ref highest, n);
            c.SubmitState(state);
        }, modes, signal, missEvery, missInputEvery, burstOf);
        Thread.Sleep((int)StressBoundMs * 2);
        Volatile.Write(ref stop, true);
        poller.Join(2000);
        failures.Report($"{Xbox360} Windows.Gaming.Input", sent.Count > 0 ? sent[0].Time : 0, rebound, "rebinds to a pad that came back");
        Check($"{Xbox360} Windows.Gaming.Input: every reading carries a count the probe submitted, in the sticks' X axes",
            misread == 0, misread == 0 ? $"{seen.Count} counts read" : $"{misread} readings carried none, the first {misreadValue}");
        Console.WriteLine($"  {Xbox360} Windows.Gaming.Input: the first reading with a count, {firstReading ?? "none"}");
        StressVerdict($"{Xbox360} Windows.Gaming.Input", sent, seen, boundFinals: false);
        if (burstOf != null) LateDetail($"{Xbox360} Windows.Gaming.Input", sent, burstOf, seen, seen, wgi: true);
    }

    const int ERROR_OPERATION_ABORTED = 995;

    static string ErrorName(int e) => e switch
    {
        0 => "no error code",
        2 => "ERROR_FILE_NOT_FOUND",
        6 => "ERROR_INVALID_HANDLE",
        21 => "ERROR_NOT_READY",
        22 => "ERROR_BAD_COMMAND",
        31 => "ERROR_GEN_FAILURE",
        995 => "ERROR_OPERATION_ABORTED",
        1167 => "ERROR_DEVICE_NOT_CONNECTED",
        1784 => "ERROR_INVALID_USER_BUFFER",
        < 0 => FormattableString.Invariant($"HRESULT 0x{e:X8}"),
        _ => new System.ComponentModel.Win32Exception(e).Message,
    };

    /// <summary>A signal-stress reader's failures: each one's error, its
    /// Stopwatch time and the last count read before it, and why the reader
    /// stopped, if it did. The report is a check that fails on a reader that
    /// stopped before the pass ended, naming the error and the count.</summary>
    sealed class ReaderFailures
    {
        readonly object _lock = new();
        readonly List<(int Error, long Time, int LastCount, string What)> _first = new();
        int _count;
        (int Error, long Time, int LastCount, string Why)? _stopped;

        public void Add(int error, long time, int lastCount, string what = "")
        {
            lock (_lock)
            {
                _count++;
                if (_first.Count < 16) _first.Add((error, time, lastCount, what));
            }
        }

        public void Stop(int error, long time, int lastCount, string why)
        {
            lock (_lock) _stopped = (error, time, lastCount, why);
        }

        public void Report(string name, long passStart, int recoveries, string recovered)
        {
            double Into(long t) => passStart == 0 ? double.NaN : (t - passStart) / (double)Stopwatch.Frequency;
            lock (_lock)
            {
                Check($"{name}: the reader read until the pass ended", _stopped == null,
                    _stopped is { } s
                        ? FormattableString.Invariant($"it stopped after count {s.LastCount}: error {s.Error} ({ErrorName(s.Error)}) at QPC {s.Time}, {Into(s.Time):F3} s into the pass, {s.Why}")
                        : FormattableString.Invariant($"{_count} failures, {recoveries} {recovered}"));
                if (_count > 0)
                    Console.WriteLine($"  {name}: {_count} failures, {recoveries} {recovered}: " + string.Join("; ", _first.Select(f =>
                        FormattableString.Invariant($"{(f.What.Length > 0 ? f.What + ", " : "")}error {f.Error} ({ErrorName(f.Error)}) at QPC {f.Time}, {Into(f.Time):F3} s in, after count {f.LastCount}"))));
            }
        }
    }

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_SetNumInputBuffers(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint count);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetNumInputBuffers(Microsoft.Win32.SafeHandles.SafeFileHandle h, out uint count);
}
