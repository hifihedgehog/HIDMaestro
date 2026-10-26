// signal-stress [seconds] [--gate-off] [--any-driver]: live and elevated.
// Frames that must all arrive, sent the way a stall would hide: after random
// pauses of 0 to 2 ms, in bursts of 50 to 200 back to back, and after pauses
// of 600 to 1,200 ms, past the driver worker's 500 ms wait. Two passes of
// [seconds] each (120 unless given):
//   a dualshock-4-v2 written with SubmitRawReport, a count in bytes 40 to
//   43, read by a host's ReadFile on its HID interface
//   an xbox-360-wired written with SubmitState, a count in the two sticks'
//   X axes, read through Windows.Gaming.Input, whose reading comes from the
//   XUSB companion's WAIT_FOR_INPUT completions
// Each submit is matched to the first reading that carries its count or a
// later one. A submit not read within 250 ms fails the pass: a wakeup the
// signal gate lost (docs/design/signal-gate.md) waits for the worker's
// 500 ms timeout.
//
// The gate's capability words in Global\HIDMaestroSignal<N> tell the SDK
// it may skip a signal, and the driver hosts count whatever they say. So
// each pass switches the gate itself by writing those words, in blocks of
// 1 s, so a load spike on the machine lands on every mode alike: on and off for the DualShock 4's input event, and for the Xbox 360
// pad also each of its two parts alone (the input event, the companion
// doorbell), in order and then backward, so every mode meets the same
// stretch of the pass. A mode with a gate on fails when it has more frames
// over 4 ms than twice those of the gate off, plus five: a missed signal
// that the worker's timeout or the companion's 8 ms timer recovers shows
// as just such a late frame. --gate-off keeps the gate off for the whole
// pass. The HID driver sets its capability again every 256 frames, so a
// mode with the input event's gate off clears it before every submit, and a
// submit that finds it set again afterward is counted apart from its mode
// and left out of the comparison. Against an SDK that makes no section it
// says so and runs as is.
// --any-driver runs on whatever driver is installed, so a v2.0.3 build of
// this probe can stress the newer driver. Builds against v2.0.3's SDK as it
// is.
// --late-detail, never part of a default run, prints where each DualShock 4
// frame over 4 ms waited: the ReadFile readings that completed between its
// submit and the first reading that carries it, the time since the reading
// before that one, the submits after it in its burst, the later submits
// that came before its reading, and whether the SDK's idle repeat, a write
// of the last frame 4 ms after it (the profile's stream period), was due
// before its reading. A frame whose wakeup was missed waits with the reader
// idle for the next write, the idle repeat when nothing follows. A frame
// queued behind a burst's other reports waits while the reader reads them.
// --input-buffers=<n>, also never in a default run, gives the reader's
// handle n input buffers (HidD_SetNumInputBuffers) in place of HID class's
// default, which caps how many reports can queue ahead of a frame.
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

    static int SignalStress(string[] args)
    {
        double seconds = 120;
        foreach (string a in args)
            if (double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) && s > 0) seconds = s;
        bool gateOff = args.Contains("--gate-off");
        bool anyDriver = args.Contains("--any-driver");
        bool lateDetail = args.Contains("--late-detail");
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
            StressHid(c, seconds, SignalView(c), gateOff, lateDetail, inputBuffers);
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
            else StressWgi(c, pad, seconds, SignalView(c), gateOff);
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

    /// <summary>The submit schedule: a pause of 0 to 2 ms, a burst, or a
    /// pause past the worker's 500 ms wait, until the time is up, in blocks
    /// of 1 s that take the modes in order, then backward, and again. Calls
    /// <paramref name="submit"/> with the count and returns each submit's
    /// count, Stopwatch time and mode. In a mode with the input event's gate
    /// off, the HID driver's capability is cleared before each submit, and
    /// a submit after which it is set again carries the Announced bit.
    /// <paramref name="burstOf"/>, when given, gets each submit's burst size,
    /// 1 for a submit alone, and the submits after it in its burst.</summary>
    static List<(int Count, long Time, int Mode)> StressSchedule(double seconds, Action<int> submit, int[] modes, IntPtr signal,
        List<(int Size, int After)>? burstOf = null)
    {
        var rng = new Random(2101);
        var sent = new List<(int, long, int)>(1 << 20);
        long start = Stopwatch.GetTimestamp();
        long end = start + (long)(seconds * Stopwatch.Frequency);
        long block = Stopwatch.Frequency;
        int n = 0, bursts = 0, gaps = 0, announced = 0, mode = -1;
        void Send(int size, int after)
        {
            n++;
            bool hold = (mode & GateInput) == 0 && signal != IntPtr.Zero;
            if (hold) HoldInputGateOff(signal);
            long t = Stopwatch.GetTimestamp();
            submit(n);
            int m = mode;
            if (hold && InputGateSet(signal)) { m |= Announced; announced++; }
            sent.Add((n, t, m));
            burstOf?.Add((size, after));
        }
        while (Stopwatch.GetTimestamp() < end)
        {
            long b = (Stopwatch.GetTimestamp() - start) / block;
            int lap = (int)(b / modes.Length), at = (int)(b % modes.Length);
            int want = modes[lap % 2 == 0 ? at : modes.Length - 1 - at];
            if (want != mode) { mode = want; SetGate(signal, mode); }
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
        SetGate(signal, 0);
        Console.WriteLine($"    {sent.Count} submits, {bursts} bursts, {gaps} pauses past 500 ms, {announced} with the input capability set again during the submit");
        return sent;
    }

    /// <summary>Each submit's time to the first reading of its count or a
    /// later one, checked against the bound, and the late frames of each
    /// mode against the gate-off mode's.</summary>
    static void StressVerdict(string name, List<(int Count, long Time, int Mode)> sent, List<(int Count, long Time)> seen)
    {
        var late = new SortedDictionary<int, List<double>>();
        int j = 0, missing = 0, over = 0;
        double worst = 0;
        foreach (var (count, t0, mode) in sent)
        {
            while (j < seen.Count && (seen[j].Count < count || seen[j].Time < t0)) j++;
            if (j == seen.Count) { missing++; continue; }
            double ms = (seen[j].Time - t0) * 1000.0 / Stopwatch.Frequency;
            if (!late.TryGetValue(mode, out var list)) late[mode] = list = new List<double>();
            list.Add(ms * 1000);
            worst = Math.Max(worst, ms);
            if (ms > StressBoundMs) over++;
        }
        // The last submit of a pass can still be in flight when it ends.
        int tail = Math.Min(missing, 1);
        Check($"{name}: every submit read within {StressBoundMs} ms",
            over == 0 && missing - tail == 0,
            FormattableString.Invariant($"{sent.Count} submits, {seen.Count} readings, {over} over the bound, {missing} never read, worst {worst:F1} ms"));
        int offLate = late.TryGetValue(0, out var off) ? off.Count(x => x > 4000) : -1;
        foreach (var (mode, list) in late)
        {
            int over2 = list.Count(x => x > 2000), over4 = list.Count(x => x > 4000);
            Report($"{name}, {ModeName(mode)}: submit to the first reading that carries it, microseconds", list, 1,
                FormattableString.Invariant($", {over2} over 2 ms, {over4} over 4 ms"));
            if (mode != 0 && (mode & Announced) == 0 && offLate >= 0)
                Check($"{name}, {ModeName(mode)}: frames over 4 ms at most twice the gate-off mode's plus five",
                    over4 <= 2 * offLate + 5, $"{over4} against {offLate} with the gate off");
        }
    }

    static void StressHid(HMController c, double seconds, IntPtr signal, bool gateOff, bool lateDetail, uint inputBuffers)
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
        var report = new byte[64];
        report[0] = 0x01;
        report[1] = report[2] = report[3] = report[4] = 0x80;
        report[5] = 0x08;
        bool stop = false;
        var reader = new Thread(() =>
        {
            var buf = new byte[dev.InputLength];
            int last = -1;
            while (!Volatile.Read(ref stop))
            {
                if (!ReadFile(dev.Handle, buf, buf.Length, out int read, IntPtr.Zero)) return;
                long t = Stopwatch.GetTimestamp();
                if (read < 44 || buf[0] != 0x01) continue;
                int count = BitConverter.ToInt32(buf, 40);
                if (reads != null) lock (seen) reads.Add((count, t));
                if (count != last) { lock (seen) seen.Add((count, t)); last = count; }
            }
        }) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "stress reader" };
        reader.Start();
        c.SubmitRawReport(report);
        Thread.Sleep(300);
        Console.WriteLine($"  {Ds4}, SubmitRawReport to ReadFile:");
        var modes = signal == IntPtr.Zero || gateOff ? new[] { 0 } : new[] { 0, GateInput };
        var sent = StressSchedule(seconds, n =>
        {
            BitConverter.TryWriteBytes(report.AsSpan(40), n);
            c.SubmitRawReport(report);
        }, modes, signal, burstOf);
        Thread.Sleep((int)StressBoundMs * 2);
        Volatile.Write(ref stop, true);
        CancelIoEx(dev.Handle, IntPtr.Zero);
        reader.Join(2000);
        List<(int, long)> got;
        List<(int, long)>? all = null;
        lock (seen)
        {
            got = seen.ToList();
            all = reads?.ToList();
        }
        StressVerdict($"{Ds4} ReadFile", sent, got);
        if (all != null && burstOf != null) LateDetail($"{Ds4} ReadFile", sent, burstOf, got, all);
    }

    /// <summary>--late-detail (header): each frame over 4 ms, where it
    /// waited, a summary by mode, and each mode's bursts of 50 or more: the
    /// readings that carried one of a burst's counts, and the wait of its
    /// last frame. Times run from the submit's Stopwatch stamp, readings from
    /// ReadFile's return.</summary>
    static void LateDetail(string name, List<(int Count, long Time, int Mode)> sent, List<(int Size, int After)> burstOf,
        List<(int Count, long Time)> seen, List<(int Count, long Time)> reads)
    {
        // The profile's stream period: the SDK writes the last frame again
        // this long after it while no submit comes (HMController.RepeatLastFrame).
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
        static int SubmitAtOrAfter(List<(int Count, long Time, int Mode)> list, long t)
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
        string Short(int mode) => ((mode & ~Announced) == 0 ? "off" : "input") + ((mode & Announced) != 0 ? "*" : "");

        var late = new SortedDictionary<int, List<(int Between, int Size, int After, int Later, bool Idle)>>();
        var rows = new List<string>();
        int j = 0;
        for (int i = 0; i < sent.Count; i++)
        {
            var (count, t0, mode) = sent[i];
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
            bool idle = (tc - sent[Math.Max(i, next - 1)].Time) * tickMs > IdleRepeatMs;
            var (size, after) = burstOf[i];
            int key = mode & ~Announced;
            if (!late.TryGetValue(key, out var list)) late[key] = list = new();
            list.Add((between, size, after, later, idle));
            if (rows.Count < MaxRows)
                rows.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"    {count,7} {Short(mode),-6} {ms,6:F2} {between,7} {F(gap),7} {later,6} {F(toNext),7} {size,5} {after,5} {(idle ? "yes" : "no"),4} {seen[j].Count - count,6}"));
        }
        int total = late.Values.Sum(l => l.Count);
        Console.WriteLine($"  {name}, --late-detail: {total} frames over 4 ms. Columns: the frame's count, its mode (* the input capability set again during the submit),");
        Console.WriteLine("  its wait in ms, the readings between its submit and its reading, ms from the reading before to its reading, the later submits");
        Console.WriteLine($"  before its reading, ms to the next submit, its burst's size (1 alone), the submits after it in the burst, whether the idle repeat");
        Console.WriteLine($"  ({IdleRepeatMs} ms after the last submit) was due before its reading, and how far past its count the reading's count is.");
        Console.WriteLine("      count mode       ms between gap_ms later next_ms burst after idle  ahead");
        foreach (string r in rows) Console.WriteLine(r);
        if (total > rows.Count) Console.WriteLine($"    {total - rows.Count} more not shown");
        foreach (var (mode, list) in late)
        {
            var b = list.Select(x => x.Between).OrderBy(x => x).ToList();
            int inBurst = list.Count(x => x.Size >= 50);
            Console.WriteLine(FormattableString.Invariant(
                $"  {name}, {ModeName(mode)}: {list.Count} frames over 4 ms. Readings between submit and reading: none {b.Count(x => x == 0)}, 1 to 7 {b.Count(x => x is >= 1 and <= 7)}, 8 to 31 {b.Count(x => x is >= 8 and <= 31)}, 32 or more {b.Count(x => x >= 32)}, median {b[b.Count / 2]}. In a burst of 50 or more {inBurst}, its last submit {list.Count(x => x.Size >= 50 && x.After == 0)}, within 8 of its end {list.Count(x => x.Size >= 50 && x.After < 8)}. A later submit before the reading {list.Count(x => x.Later > 0)}. The idle repeat due before the reading {list.Count(x => x.Idle)}."));
        }
        // Each burst of 50 or more: the readings whose count is one of its
        // own, and its last submit's wait, by mode.
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
            var (lastCount, lastTime, _) = sent[i + size - 1];
            while (k2 < seen.Count && (seen[k2].Count < lastCount || seen[k2].Time < lastTime)) k2++;
            double lastMs = k2 < seen.Count ? (seen[k2].Time - lastTime) * tickMs : double.NaN;
            int key = sent[i].Mode & ~Announced;
            if (!bursts.TryGetValue(key, out var list)) bursts[key] = list = new();
            list.Add((e - s, lastMs));
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

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_SetNumInputBuffers(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint count);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetNumInputBuffers(Microsoft.Win32.SafeHandles.SafeFileHandle h, out uint count);

    static void StressWgi(HMController c, Gamepad pad, double seconds, IntPtr signal, bool gateOff)
    {
        // A count in the two sticks' X axes (HMAxis.X and HMAxis.Z on this
        // profile), 200 steps each, apart by more than the 16-bit
        // quantization on the way to Windows.Gaming.Input.
        static float Axis(int digit) => 0.05f + digit * 0.0045f;
        static int Digit(double wgi) => (int)Math.Round(((wgi + 1) / 2 - 0.05) / 0.0045);
        var seen = new List<(int, long)>(1 << 20);
        bool stop = false;
        var state = new HMGamepadState { Axes = new Dictionary<HMAxis, float>() };
        var poller = new Thread(() =>
        {
            int last = -1, unwrapped = 0, prev = 0;
            while (!Volatile.Read(ref stop))
            {
                var r = pad.GetCurrentReading();
                long t = Stopwatch.GetTimestamp();
                int x = Digit(r.LeftThumbstickX), y = Digit(r.RightThumbstickX);
                if (x < 0 || x > 199 || y < 0 || y > 199) continue;
                int code = y * 200 + x;
                if (code == last) continue;
                // Counts go up, 40,000 codes a lap.
                int delta = ((code - prev) % 40000 + 40000) % 40000;
                if (last >= 0) unwrapped += delta; else unwrapped = code;
                prev = code;
                last = code;
                seen.Add((unwrapped, t));
            }
        }) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "stress wgi" };
        state.Axes[HMAxis.X] = Axis(0);
        state.Axes[HMAxis.Z] = Axis(0);
        c.SubmitState(state);
        Thread.Sleep(500);
        poller.Start();
        Console.WriteLine($"  {Xbox360}, SubmitState to Windows.Gaming.Input:");
        var modes = signal == IntPtr.Zero || gateOff ? new[] { 0 } : new[] { 0, GateInput, GateCompanion, GateInput | GateCompanion };
        var sent = StressSchedule(seconds, n =>
        {
            int code = n % 40000;
            state.Axes[HMAxis.X] = Axis(code % 200);
            state.Axes[HMAxis.Z] = Axis(code / 200);
            c.SubmitState(state);
        }, modes, signal);
        Thread.Sleep((int)StressBoundMs * 2);
        Volatile.Write(ref stop, true);
        poller.Join(2000);
        StressVerdict($"{Xbox360} Windows.Gaming.Input", sent, seen);
    }
}
