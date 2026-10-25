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
        if (!Elevated("signal-stress")) return 3;
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        using (var c = ctx.CreateController(ctx.GetProfile(Ds4)!, s_key))
        {
            if (!anyDriver && !OnEmbeddedDriver(c, Ds4)) return 5;
            StressHid(c, seconds, SignalView(c), gateOff);
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
    /// a submit after which it is set again carries the Announced bit.</summary>
    static List<(int Count, long Time, int Mode)> StressSchedule(double seconds, Action<int> submit, int[] modes, IntPtr signal)
    {
        var rng = new Random(2101);
        var sent = new List<(int, long, int)>(1 << 20);
        long start = Stopwatch.GetTimestamp();
        long end = start + (long)(seconds * Stopwatch.Frequency);
        long block = Stopwatch.Frequency;
        int n = 0, bursts = 0, gaps = 0, announced = 0, mode = -1;
        void Send()
        {
            n++;
            bool hold = (mode & GateInput) == 0 && signal != IntPtr.Zero;
            if (hold) HoldInputGateOff(signal);
            long t = Stopwatch.GetTimestamp();
            submit(n);
            int m = mode;
            if (hold && InputGateSet(signal)) { m |= Announced; announced++; }
            sent.Add((n, t, m));
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
                for (int i = 0; i < k; i++) Send();
                bursts++;
                continue;
            }
            Spin(rng.Next(0, 2001));
            Send();
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

    static void StressHid(HMController c, double seconds, IntPtr signal, bool gateOff)
    {
        using var dev = OpenStateInterface(c, out string why);
        if (dev == null) { Check($"{Ds4}: a HID interface with input reports", false, why); return; }
        var seen = new List<(int, long)>(1 << 20);
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
        }, modes, signal);
        Thread.Sleep((int)StressBoundMs * 2);
        Volatile.Write(ref stop, true);
        CancelIoEx(dev.Handle, IntPtr.Zero);
        reader.Join(2000);
        List<(int, long)> got;
        lock (seen) got = seen.ToList();
        StressVerdict($"{Ds4} ReadFile", sent, got);
    }

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
