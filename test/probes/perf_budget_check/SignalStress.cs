// signal-stress [seconds] [--gate-off] [--any-driver] [--passes=<name,...>]:
// live and elevated. Frames that must all arrive, sent the way a stall would
// hide: after random pauses of 0 to 2 ms, in bursts of 50 to 200 back to
// back, and after pauses of 600 to 1,200 ms, past the driver worker's 500 ms
// wait. Two passes of [seconds] each (120 unless given), unless --passes=
// names others:
//   a dualshock-4-v2 written with SubmitRawReport, a count in report bytes
//   40 to 43, read by a host's ReadFile on its HID interface.
//   SubmitRawReport takes the data without the report ID, which the driver
//   puts in front, so the count goes in at data bytes 39 to 42
//   an xbox-360-wired written with SubmitState, a count in the two sticks'
//   X axes, read through Windows.Gaming.Input, whose reading comes from the
//   XUSB companion's WAIT_FOR_INPUT completions
// Each submit is matched to the first reading that carries its count or a
// later one. A submit not read within 250 ms fails the pass: a frame whose
// wakeup the signal gate lost (docs/design/signal-gate.md) waits for the
// worker's 500 ms timeout when the SDK left it in the handoff, and for the
// next signal, an idle repeat or the program's next submit, when it did
// not (driver.c, the worker's wait). A reading whose count is above every
// count submitted by then fails the pass too, so a count read in the wrong
// place fails rather than matching. Until that check the DualShock 4's count went in at data
// bytes 40 to 43, read back as 256 times itself, and every submit matched
// the next new report of any frame. A reading whose count is below the one
// read before it, a frame that came after a newer one, fails the pass as
// well: the Xbox 360 pad's HID collection read count 99,670 and then
// 99,669, 0.02 ms apart, each a whole frame (SIG-stress-rel3e-celeron).
// Matching took no notice of it. A reader that fails records the error,
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
// many or more in a binomial with p 1/2) at 1/4,000 or less. That takes at
// least 12 pairs against it (11 of 11 has the chance 1/2,048), so a handful
// of late frames never fails a mode. Until 2026-10-26 a mode also needed
// more than five frames over 4 ms beyond the gate-off blocks', summed over
// its pairs, and one stall in a gate-off block canceled that: the input
// control on dualsense, worse in 17 pairs and better in 1 (p 7.25E-05),
// passed on 19 frames against 140 (SIG-stress-rel3d-celeron). Each mode's
// check prints every pair, so a stall shows as the one pair it hit.
//   With a gate that has no effect on when frames are read, which block of a
// pair reads more frames late is the same whichever block ran the gate, and
// the coin alone decided that. Each pair that differs then goes against the
// mode with probability one half, independently of the other pairs and of
// whatever the load did, so such a mode fails a comparison at most once in
// 4,000 runs. The battery's two passes make five comparisons, frames over
// 4 ms in the DualShock 4's input mode and the Xbox 360 pad's three, and the
// submits alone below in the DualShock 4's input mode, together at most once
// in 800. A gate that does change when frames are read, by a missed signal
// or anything else, fails once it is worse in enough of the pairs, whatever
// the load.
//   Frames over 4 ms count bursts and stalls far more than lost wakeups,
// since a frame whose wakeup is lost waits only until the next write. That
// write is the next submit, within 2 ms here, or the SDK's idle repeat one
// stream period after the frame (4 ms on the DualShock 4 and the DualSense,
// 1.25 ms on the DualSense over Bluetooth). No time bound tells a lost
// wakeup from the machine's own delays: on the Celeron, final submits with
// the gate off waited up to 4.32 ms and still went out on their own, before
// anything else was written, and a bound of 2 ms on final submits failed a
// plain run (SIG-stress-rel3c-celeron, SIG-stress-rel3d-celeron). So the
// checks look for what a lost wakeup leaves behind instead.
//   A submit alone, with no further submit for 1 ms, goes out on its own
// in about 0.1 ms. A lost wakeup leaves its frame for the next write: the
// next submit overwrites it, and the first reading at or past its count
// carries a later count, or the idle repeat sends it a stream period after
// the submit. So a submit alone counts as not read on its own when it was
// never read, first read at a later count, or read a stream period or more
// after its submit. Each gate-on mode's submits alone not read on their own
// are held against the gate-off block of each pair by the same sign test at
// the same level. Load that delays the worker past the next submit leaves
// the same mark in both blocks of a pair, and the coin decides which one the
// gate ran in. The input control fails it in every pair on the DualShock 4
// and the F310 (SIG-stress-rel3g-celeron).
//   Each submit before a pause past the worker's 500 ms wait is printed
// with whether it was read first at its own count, gated or not. A frame
// read first at a later count never went out on its own and waited out the
// pause for the next submit: a lost wakeup on a path with no idle repeat,
// an idle repeat that never came, or a frame held past the pause. That line
// is no check, since load leaves the same mark: on the development laptop
// beside the host-priority lane's bursts at AboveNormal, frames waited up to
// 732 ms, past the schedule's shortest pause of 600 ms
// (SIG-stress-rel3g-laptop-burst-abovenormal). A lost wakeup on a path with
// no idle repeat still fails a check: its frame waits 600 ms or more, past
// the 250 ms bound, and a gated one fails the comparison of submits alone
// as well. A lost wakeup the gate does not cause shows in both blocks of
// every pair, and on a path with an idle repeat it costs one stream period,
// which the machine's own delays also reach. No check here can see that
// case.
//   The final submits, with no further submit for 4 ms, are printed by mode
// with their waits. On the Xbox 360 pad's Windows.Gaming.Input pass the
// submits alone are printed and not compared: Windows.Gaming.Input reads the
// frames before the schedule's pauses 5 to 30 ms late on v2.0.3's pair as
// on 2.1.0's (SIG-ab-ds4-1), past the companion's 8 ms timer that recovers a
// lost doorbell. The comparison of frames over 4 ms, which the doorbell
// control fails 40 pairs of 40, is that pad's guard.
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
// --passes=<name,...> runs the passes named, in this order: ds4 and x360-wgi
// (the two above), x360-xinput, x360-hid, dualsense, dualsense-bt, generic
// and usbip (SignalStressPaths.cs, whose header gives each one's path), or
// all of them with --passes=all.
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
// input gate must fail the comparison. The Xbox 360 pad's Windows.Gaming.Input
// and XInput readings come from the companion, so their modes must pass. On
// a pass with no idle repeat (x360-hid, generic) a submit before a pause
// whose wakeup is taken waits for the next submit, past the pause, and fails
// the 250 ms bound and the check on submits before a pause.
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
    // The paired comparison (header): submits in a block and the sign test's
    // level for each mode.
    const int StressBlock = 500;
    const double StressLevel = 1.0 / 4000;
    // The positive control's hold, two periods of the companion's 8 ms
    // timer (companion.c, WDF_TIMER_CONFIG_INIT_PERIODIC).
    const double MissHoldMs = 16;
    // A final submit (header): no further submit for this long. A submit
    // before a pause: no further submit for the worker's 500 ms wait, which
    // the schedule's pauses of 600 to 1,200 ms pass.
    const double FinalGapMs = 4;
    const double PauseGapMs = 500;
    // A submit alone (header): no further submit for this long, ten times a
    // delivery's 0.1 ms, so its frame normally goes out on its own.
    const double AloneGapMs = 1;

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
        // --passes=<name,...> or all (SignalStressPaths.cs). Without it, the
        // battery's two.
        var passes = new List<string> { "ds4", "x360-wgi" };
        string? named = args.FirstOrDefault(a => a.StartsWith("--passes=", StringComparison.Ordinal));
        if (named != null)
        {
            var list = named["--passes=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // all is every pass but the keyboard's and the mouse's.
            passes = list.SelectMany(p => p == "all" ? StressPasses.Select(s => s.Name).Where(n => !StressKbmPasses.Contains(n)) : new[] { p })
                .Distinct().ToList();
            string? unknown = passes.FirstOrDefault(p => !StressPasses.Any(s => s.Name == p));
            if (unknown != null || passes.Count == 0)
            {
                Console.WriteLine($"signal-stress: {named} names {unknown ?? "no pass"}. The passes are {string.Join(", ", StressPasses.Select(p => p.Name))}, or all");
                return 1;
            }
            // The keys and the pointer land in whatever window has focus on
            // this machine's console, so the caller names the machine they may
            // type on, and a command copied to another machine refuses.
            string? kbm = args.FirstOrDefault(x => x.StartsWith("--kbm=", StringComparison.Ordinal))?["--kbm=".Length..];
            if (passes.Any(StressKbmPasses.Contains) && !string.Equals(kbm, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"signal-stress: the keyboard and mouse passes type and move the pointer on this machine's console. They run only with --kbm=<this machine's name>, and never on the development laptop");
                return 1;
            }
        }
        if (!Elevated("signal-stress")) return 3;
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        foreach (var (pass, profile) in StressPasses.Where(p => passes.Contains(p.Name)))
        {
            var before = new HashSet<Gamepad>(Gamepad.Gamepads);
            var slotsBefore = pass == "x360-xinput" ? XInputConnected() : new HashSet<int>();
            var p = ctx.GetProfile(profile);
            if (p == null) { Check($"{pass}: profile {profile} in the catalog", false, "not found"); continue; }
            using var c = ctx.CreateController(p, s_key);
            if (!anyDriver && !OnEmbeddedDriver(c, profile)) return 5;
            switch (pass)
            {
                case "ds4":
                    StressHid(c, seconds, SignalView(c), gateOff, lateDetail, inputBuffers, missInputEvery);
                    break;
                case "x360-wgi":
                    Gamepad? pad = null;
                    var wait = Stopwatch.StartNew();
                    while (pad == null && wait.ElapsedMilliseconds < 20000)
                    {
                        pad = Gamepad.Gamepads.FirstOrDefault(g => !before.Contains(g));
                        if (pad == null) Thread.Sleep(100);
                    }
                    if (pad == null) Check($"{Xbox360}: a Windows.Gaming.Input gamepad the create added", false, "none in 20 s");
                    else StressWgi(c, pad, before, seconds, SignalView(c), gateOff, missEvery, missInputEvery, lateDetail);
                    break;
                case "x360-xinput":
                    StressXInput(c, slotsBefore, seconds, SignalView(c), gateOff, lateDetail, missEvery, missInputEvery);
                    break;
                case "keyboard":
                case "mouse":
                    StressKbm(c, profile, seconds, SignalView(c), gateOff, lateDetail, missInputEvery, keyboard: pass == "keyboard");
                    break;
                default:
                    // A USB/IP controller has no HIDMaestro driver and no
                    // signal section: its pass has one mode and no pairs.
                    IntPtr signal = c.Profile.RequiresUsbipBackend ? IntPtr.Zero : SignalView(c);
                    StressHidSticks(c, pass == "x360-hid" ? $"{Xbox360} HID" : profile, seconds, signal, gateOff, lateDetail, inputBuffers,
                        missEvery, missInputEvery, companion: pass == "x360-hid");
                    break;
            }
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
    /// later one, checked against the bound, the submits a lost wakeup marks
    /// (header, <see cref="FinalVerdict"/>), and each gate-on mode's frames
    /// over 4 ms against the gate-off block of its pair.
    /// <paramref name="repeatMs"/> is the SDK's idle repeat on the pass's
    /// controller, 0 for none. With <paramref name="compareFinals"/> each
    /// gate-on mode's submits alone are compared as its frames are.</summary>
    static void StressVerdict(string name, List<(int Count, long Time, int Mode, int Block)> sent, List<(int Count, long Time)> seen,
        double repeatMs, bool compareFinals)
    {
        var late = new SortedDictionary<int, List<double>>();
        // Each block's mode without the Announced bit, its submits and its
        // frames over 4 ms, a frame never read among them.
        var blocks = new List<(int Mode, int Submits, int Over4)>();
        // Each submit's wait in ms, NaN for one never read, and how far past
        // its count the reading it waited for was, -1 for one never read.
        var wait = new double[sent.Count];
        var ahead = new int[sent.Count];
        int j = 0, missing = 0, over = 0, at = -1;
        double worst = 0;
        foreach (var (count, t0, mode, block) in sent)
        {
            at++;
            wait[at] = double.NaN;
            ahead[at] = -1;
            while (blocks.Count <= block) blocks.Add((mode & ~Announced, 0, 0));
            var bk = blocks[block];
            bk.Submits++;
            while (j < seen.Count && (seen[j].Count < count || seen[j].Time < t0)) j++;
            if (j == seen.Count) { missing++; bk.Over4++; blocks[block] = bk; continue; }
            double ms = (seen[j].Time - t0) * 1000.0 / Stopwatch.Frequency;
            wait[at] = ms;
            ahead[at] = seen[j].Count - count;
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
        FinalVerdict(name, sent, wait, ahead, blocks, repeatMs, compareFinals);
        var pairs = BlockPairs(blocks);
        foreach (int mode in blocks.Select(x => x.Mode).Where(m => m != 0).Distinct().OrderBy(m => m))
        {
            var list = pairs.TryGetValue(mode, out var got) ? got : new List<(int On, int Off)>();
            var v = PairVerdict(list);
            Check($"{name}, {ModeName(mode)}: frames over 4 ms against the gate-off block of each pair, worse in no more pairs than a sign test allows at 1/4,000",
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

    /// <summary>Each submit with no further submit for
    /// <paramref name="gapMs"/>, and the pass's last one when it was read
    /// (header): the submits alone (<see cref="AloneGapMs"/>), the final ones
    /// (<see cref="FinalGapMs"/>) and those before a pause
    /// (<see cref="PauseGapMs"/>). Indexes into <paramref name="sent"/>,
    /// whose waits in ms are <paramref name="wait"/>, NaN for one never
    /// read.</summary>
    internal static List<int> SubmitsBefore(List<(int Count, long Time, int Mode, int Block)> sent, double[] wait, double gapMs)
    {
        long gap = (long)(gapMs * Stopwatch.Frequency / 1000);
        var list = new List<int>();
        for (int i = 0; i < sent.Count; i++)
        {
            bool last = i + 1 == sent.Count;
            if (last ? !double.IsNaN(wait[i]) : sent[i + 1].Time - sent[i].Time >= gap) list.Add(i);
        }
        return list;
    }

    /// <summary>A submit not read on its own before its idle repeat was due
    /// (header): never read, first read at a later count, or read
    /// <paramref name="repeatMs"/> or more after its submit, 0 for a
    /// controller with no idle repeat.</summary>
    internal static bool NotOnItsOwn(double wait, int ahead, double repeatMs) =>
        double.IsNaN(wait) || ahead != 0 || (repeatMs > 0 && wait >= repeatMs);

    /// <summary>The submits a lost wakeup marks (header): the submits before
    /// a pause and whether each was first read at its own count, each mode's
    /// final submits and submits alone, each on a line, and with
    /// <paramref name="compare"/> each gate-on mode's submits alone not read
    /// on their own against the gate-off block of each pair.
    /// <paramref name="ahead"/> is how far past its count each submit's first
    /// reading was, -1 for one never read. Returns the submits before a pause
    /// not first read at their own count.</summary>
    internal static int FinalVerdict(string name, List<(int Count, long Time, int Mode, int Block)> sent, double[] wait, int[] ahead,
        List<(int Mode, int Submits, int Over4)> blocks, double repeatMs, bool compare)
    {
        string F(double v) => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        string Late(int i) => ahead[i] < 0 ? "never read" : ahead[i] > 0
            ? $"first read at count {sent[i].Count + ahead[i]}, {F(wait[i])} ms on" : $"read {F(wait[i])} ms on";

        var pauses = SubmitsBefore(sent, wait, PauseGapMs);
        var missed = pauses.Where(i => ahead[i] != 0).ToList();
        // Printed, not checked (header): load that holds a frame past the
        // pause leaves the same mark as a lost wakeup.
        var readOwn = pauses.Where(i => ahead[i] == 0).ToList();
        Console.WriteLine($"  {name}: {pauses.Count} submits before a pause past the worker's 500 ms wait, {missed.Count} not first read at their own count" +
            (readOwn.Count == 0 ? "" : $", the slowest of the others read {F(readOwn.Max(i => wait[i]))} ms on") +
            (missed.Count == 0 ? "" : ": " + string.Join("; ", missed.Take(12).Select(i => $"count {sent[i].Count} {ShortMode(sent[i].Mode)} {Late(i)}"))
                + (missed.Count > 12 ? $"; {missed.Count - 12} more" : "")));

        var finals = SubmitsBefore(sent, wait, FinalGapMs);
        bool Off(int i) => NotOnItsOwn(wait[i], ahead[i], repeatMs);
        string what = repeatMs > 0 ? FormattableString.Invariant($"not read on their own before the idle repeat {repeatMs} ms on") : "not read on their own";
        // Each mode's waits of its final submits read, median, 90th
        // percentile (nearest rank) and most.
        string Spread(IEnumerable<int> g)
        {
            var w = g.Where(i => !double.IsNaN(wait[i])).Select(i => wait[i]).OrderBy(x => x).ToList();
            if (w.Count == 0) return "none read";
            double median = w.Count % 2 == 1 ? w[w.Count / 2] : (w[w.Count / 2 - 1] + w[w.Count / 2]) / 2;
            double p90 = w[(int)Math.Ceiling(0.9 * w.Count) - 1];
            return $"median {F(median)}, 90th percentile {F(p90)}, most {F(w[^1])} ms";
        }
        string byMode = string.Join(", ", finals.GroupBy(i => sent[i].Mode & ~Announced).OrderBy(g => g.Key)
            .Select(g => $"{ShortMode(g.Key)} {g.Count()} ({g.Count(Off)} {what}, {Spread(g)})"));
        var offs = finals.Where(Off).ToList();
        Console.WriteLine($"  {name}: {finals.Count} submits with no further submit for {F(FinalGapMs)} ms; by mode {byMode}" +
            (offs.Count == 0 ? "" : "; " + string.Join("; ", offs.Take(12).Select(i => $"count {sent[i].Count} {ShortMode(sent[i].Mode)} {Late(i)}"))
                + (offs.Count > 12 ? $"; {offs.Count - 12} more" : "")));
        // The submits alone, the final ones among them, and those not read
        // on their own.
        var alone = SubmitsBefore(sent, wait, AloneGapMs);
        var aloneOff = alone.Where(Off).ToList();
        Console.WriteLine($"  {name}: {alone.Count} submits with no further submit for {F(AloneGapMs)} ms; by mode " +
            string.Join(", ", alone.GroupBy(i => sent[i].Mode & ~Announced).OrderBy(g => g.Key).Select(g => $"{ShortMode(g.Key)} {g.Count()} ({g.Count(Off)} {what})")));
        if (compare)
        {
            // The same pairs as the frames over 4 ms, counting the submits
            // alone not read on their own in each block.
            var lateIn = new int[blocks.Count];
            foreach (int i in aloneOff) lateIn[sent[i].Block]++;
            var pairs = BlockPairs(blocks.Select((b, k) => (b.Mode, b.Submits, lateIn[k])).ToList());
            foreach (int mode in blocks.Select(x => x.Mode).Where(m => m != 0).Distinct().OrderBy(m => m))
            {
                var list = pairs.TryGetValue(mode, out var got) ? got : new List<(int On, int Off)>();
                var v = PairVerdict(list);
                Check($"{name}, {ModeName(mode)}: submits with no further submit for {F(AloneGapMs)} ms {what}, against the gate-off block of each pair, worse in no more pairs than a sign test allows at 1/4,000",
                    !v.Fails,
                    FormattableString.Invariant($"worse in {v.Against} of {list.Count} pairs, better in {v.For}, even in {list.Count - v.Against - v.For}, one-sided p {v.P:G3}; {list.Sum(x => x.On)} such submits in its blocks, {list.Sum(x => x.Off)} in the gate-off blocks"));
                Console.WriteLine("    its pairs in order, such submits in its block/in the gate-off block, . where even: " +
                    (list.Count == 0 ? "none" : string.Join(" ", list.Select(x => x.On == x.Off ? "." : $"{x.On}/{x.Off}"))));
            }
        }
        return missed.Count;
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
    /// for the worse ones among the pairs that differ, and whether the mode
    /// fails.</summary>
    internal static (int Against, int For, double P, bool Fails) PairVerdict(IEnumerable<(int On, int Off)> pairs)
    {
        int against = 0, forIt = 0;
        foreach (var (on, off) in pairs)
        {
            if (on > off) against++;
            else if (off > on) forIt++;
        }
        double p = SignTail(against, against + forIt);
        return (against, forIt, p, p <= StressLevel);
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
        // Submit 3 is read at its own count 4.2 ms on, submit 5 first at
        // count 6: with an idle repeat 4 ms on neither was read on its own
        // before the repeat, and with none, only 3 was read on its own.
        static long T(double ms) => (long)(ms * Stopwatch.Frequency / 1000);
        var fs = new List<(int Count, long Time, int Mode, int Block)>
        {
            (1, T(0), 0, 0), (2, T(1), 0, 0), (3, T(1.5), GateInput, 0), (4, T(10), 0, 0), (5, T(13.9), 0, 0), (6, T(30), 0, 0),
        };
        var fw = new[] { 0.05, 0.05, 4.2, 0.1, 0.06, double.NaN };
        var fa = new[] { 0, 0, 0, 0, 1, -1 };
        var finals = SubmitsBefore(fs, fw, FinalGapMs);
        string Own(double repeat) => string.Join(" ", finals.Select(i => NotOnItsOwn(fw[i], fa[i], repeat) ? "no" : "yes"));
        yield return ("signal-stress: a final submit has no further submit for 4 ms, the last one counting only when read, and is on its own when read at its count before any idle repeat",
            string.Join(" ", finals.Select(i => fs[i].Count)) == "3 5" && Own(4) == "no no" && Own(0) == "yes no",
            $"final: {string.Join(" ", finals.Select(i => fs[i].Count))}, on its own with a 4 ms repeat: {Own(4)}, with none: {Own(0)}");

        // Submits before a pause: 699 ms after submit 2, 798 ms after submit
        // 4, and submit 6 last, read or not.
        var ps = new List<(int Count, long Time, int Mode, int Block)>
        {
            (1, T(0), 0, 0), (2, T(1), 0, 0), (3, T(700), 0, 0), (4, T(702), 0, 0), (5, T(1500), 0, 0), (6, T(1501), 0, 0),
        };
        var pw = Enumerable.Repeat(0.1, 6).ToArray();
        string Pauses(double[] w) => string.Join(" ", SubmitsBefore(ps, w, PauseGapMs).Select(i => ps[i].Count));
        string read6 = Pauses(pw);
        pw[5] = double.NaN;
        string unread6 = Pauses(pw);
        yield return ("signal-stress: a submit before a pause has no further submit for 500 ms, the last one counting only when read",
            read6 == "2 4 6" && unread6 == "2 4", $"{read6}, and {unread6} with the last never read");

        static List<(int On, int Off)> Same(int pairs, int on, int off) => Enumerable.Repeat((on, off), pairs).ToList();
        static bool Fails(List<(int On, int Off)> p) => PairVerdict(p).Fails;
        var split = Same(20, 60, 10).Concat(Same(20, 10, 60)).ToList();
        // A stall of 16 frames in one gate-off block, beside 20 pairs one
        // frame worse with the gate on.
        var stall = Same(20, 1, 0).Concat(Same(1, 0, 16)).ToList();
        yield return ("signal-stress: 40 pairs at 60 against 10 fail, 12 at 11 against 10 fail and 11 pass, 40 even pairs and 20 each way pass",
            Fails(Same(40, 60, 10)) && Fails(Same(12, 11, 10)) && !Fails(Same(11, 11, 10)) && !Fails(Same(40, 3, 3)) && !Fails(split),
            FormattableString.Invariant($"p {PairVerdict(Same(40, 60, 10)).P:G3}, {PairVerdict(Same(12, 11, 10)).P:G3}, {PairVerdict(Same(11, 11, 10)).P:G3}, {PairVerdict(split).P:G3}"));
        yield return ("signal-stress: 20 pairs at 1 against 0 fail, and still fail beside a stall of 16 frames in one more pair's gate-off block",
            Fails(Same(20, 1, 0)) && Fails(stall),
            FormattableString.Invariant($"p {PairVerdict(Same(20, 1, 0)).P:G3} and {PairVerdict(stall).P:G3}"));

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

        // Submits alone compared the same way, in simulated modes of 122
        // pairs, a DualShock 4 pass's. A block's submits alone are a Poisson
        // draw of mean 60. Load alone leaves each one off its own with a
        // chance that drifts from pair to pair between 0.1% and 5%, half
        // again in the second block of every pair. The input control takes
        // every second gated wakeup, a chance of 0.5 in a gated block. A
        // fault 25 times rarer adds 0.02.
        static int Poisson(Random r, double mean)
        {
            double floor = Math.Exp(-mean), p = 1;
            int k = 0;
            do { k++; p *= r.NextDouble(); } while (p > floor);
            return k - 1;
        }
        var rf = new Random(4001);
        int aloneNull = 0, aloneControl = 0, aloneRare = 0;
        var an = new List<(int On, int Off)>(122);
        var ac = new List<(int On, int Off)>(122);
        var ar = new List<(int On, int Off)>(122);
        for (int s = 0; s < trials; s++)
        {
            an.Clear(); ac.Clear(); ar.Clear();
            double chance = 0.005;
            for (int p = 0; p < 122; p++)
            {
                int n1 = Poisson(rf, 60), n2 = Poisson(rf, 60);
                double c1 = chance, c2 = chance * 1.5;
                bool gateFirst = rf.Next(2) == 0;
                var (ng, cg, no, co) = gateFirst ? (n1, c1, n2, c2) : (n2, c2, n1, c1);
                an.Add((Poisson(rf, ng * cg), Poisson(rf, no * co)));
                ac.Add((Poisson(rf, ng * (1 - (1 - cg) * 0.5)), Poisson(rf, no * co)));
                ar.Add((Poisson(rf, ng * (1 - (1 - cg) * 0.98)), Poisson(rf, no * co)));
                chance = Math.Clamp(chance * Math.Exp(rf.NextDouble() - 0.5), 0.001, 0.05);
            }
            if (Fails(an)) aloneNull++;
            if (Fails(ac)) aloneControl++;
            if (Fails(ar)) aloneRare++;
        }
        yield return ("signal-stress: submits alone compared by pairs: with no effect at most 15 of 20,000 simulated modes fail, and the input control's fault fails 99 in 100 or more",
            aloneNull <= 15 && aloneControl * 100 >= trials * 99,
            $"{aloneNull} and {aloneControl} of {trials} failed, {aloneRare} with a fault 25 times rarer");
        foreach (var check in StressDecodeSelfTest()) yield return check;
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
        // A reading whose count is below the last one read is a frame that
        // came after a newer one, and fails the pass. The first 8, with the
        // count before them and both readings' times.
        int older = 0;
        var olderAt = new List<(long Time, int Count, int Last, long LastTime)>();
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
            long lastAt = 0;
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
                if (count < last)
                {
                    if (older++ < 8) lock (seen) olderAt.Add((t, count, last, lastAt));
                    continue;
                }
                if (firstReport == null && count > 0) firstReport = buf.ToArray();
                if (reads != null) lock (seen) reads.Add((count, t));
                if (count != last) { lock (seen) seen.Add((count, t)); last = count; }
                lastAt = t;
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
        List<(long Time, int Count, int Last, long LastTime)> early;
        lock (seen) early = olderAt.ToList();
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        Check($"{Ds4} ReadFile: every reading carries a count the probe submitted, at report bytes 40 to 43, none older than the reading before it",
            misread == 0 && older == 0,
            $"{got.Count} counts read" + (misread > 0 ? $", {misread} readings carried none, the first {misreadValue}" : "")
            + (older > 0 ? FormattableString.Invariant($", {older} older than the reading before, the first count {early[0].Count} after {early[0].Last}, {Ms(early[0].Time - early[0].LastTime):F2} ms later") : ""));
        foreach (var o in early)
        {
            var at = SubmitAt(sent, o.Time);
            Console.WriteLine(FormattableString.Invariant(
                $"  {Ds4} ReadFile, older frame at {Ms(o.Time - passStart):F2} ms into the pass, {ShortMode(at.Mode)} block {at.Block}: count {o.Count} after {o.Last}, {Ms(o.Time - o.LastTime):F2} ms later"));
        }
        StressVerdict($"{Ds4} ReadFile", sent, got, repeatMs: IdleRepeatMs(c), compareFinals: true);
        if (all != null && burstOf != null)
        {
            if (firstReport != null)
                Console.WriteLine($"  {Ds4} ReadFile, --late-detail: the first report with a count, bytes 0 to 7 {Convert.ToHexString(firstReport, 0, 8)}, " +
                    $"bytes 36 to 47 {Convert.ToHexString(firstReport, 36, 12)}, count at bytes 40 to 43 {BitConverter.ToInt32(firstReport, 40)}");
            LateDetail($"{Ds4} ReadFile", sent, burstOf, got, all, wgi: false, streamMs: IdleRepeatMs(c));
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
    /// carried the frame (header). <paramref name="streamMs"/> is the
    /// profile's stream period, 0 for none: the SDK writes the last frame
    /// again this long after it while no submit comes
    /// (HMController.RepeatLastFrame).</summary>
    static void LateDetail(string name, List<(int Count, long Time, int Mode, int Block)> sent, List<(int Size, int After)> burstOf,
        List<(int Count, long Time)> seen, List<(int Count, long Time)> reads, bool wgi, double streamMs)
    {
        double repeatMs = streamMs > 0 ? streamMs : double.PositiveInfinity;
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
            string why = wgi ? Cause(since, gap, ms) : since > repeatMs ? "idle" : "-";
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
            : streamMs > 0 ? $"  submits after it in the burst, idle where the SDK's idle repeat ({streamMs} ms after the last submit) was due before its reading," : "  submits after it in the burst, - for every row (no stream, so no idle repeat),");
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
        // misread, and a frame older than the one read before it came after
        // it. Either fails the pass (CountTracker, 40,000 codes a lap).
        int highest = 0;
        var tracker = new CountTracker(40000);
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
            int inARow = 0;
            long nextLook = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
            while (!Volatile.Read(ref stop))
            {
                GamepadReading r;
                try { r = current.GetCurrentReading(); }
                catch (Exception ex)
                {
                    long at = Stopwatch.GetTimestamp();
                    failures.Add(ex.HResult, at, tracker.Last, ex.GetType().Name);
                    if (++inARow > 20) { failures.Stop(ex.HResult, at, tracker.Last, "20 failed readings in a row"); return; }
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
                    catch (Exception ex) { failures.Add(ex.HResult, t, tracker.Last, "Gamepad.Gamepads threw " + ex.GetType().Name); }
                    if (!listed)
                    {
                        failures.Add(0, t, tracker.Last, "the pad left Gamepad.Gamepads");
                        Gamepad? back = null;
                        var wait = Stopwatch.StartNew();
                        while (back == null && !Volatile.Read(ref stop) && wait.ElapsedMilliseconds < 5000)
                        {
                            try { back = Gamepad.Gamepads.FirstOrDefault(g => !before.Contains(g) && !ReferenceEquals(g, current)); }
                            catch (Exception) { back = null; }
                            if (back == null) Thread.Sleep(50);
                        }
                        if (back == null) { failures.Stop(0, t, tracker.Last, "the pad left Gamepad.Gamepads and none came back in 5 s"); return; }
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
                if (!tracker.Accept(code, Volatile.Read(ref highest), out int count)) continue;
                seen.Add((count, t));
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
        Check($"{Xbox360} Windows.Gaming.Input: every reading carries a count the probe submitted, in the sticks' X axes, none older than the reading before it",
            tracker.Misread == 0 && tracker.Older == 0,
            $"{seen.Count} counts read" +
            (tracker.Older > 0 ? $", {tracker.Older} older than the reading before, the first count {tracker.OlderValue} after {tracker.OlderAfter}" : "") +
            (tracker.Misread > 0 ? $", {tracker.Misread} readings carried none, the first {tracker.MisreadValue}" : ""));
        Console.WriteLine($"  {Xbox360} Windows.Gaming.Input: the first reading with a count, {firstReading ?? "none"}");
        StressVerdict($"{Xbox360} Windows.Gaming.Input", sent, seen, repeatMs: 0, compareFinals: false);
        if (burstOf != null) LateDetail($"{Xbox360} Windows.Gaming.Input", sent, burstOf, seen, seen, wgi: true, streamMs: 0);
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
