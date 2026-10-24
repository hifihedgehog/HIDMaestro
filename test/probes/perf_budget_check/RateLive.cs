// rate [runs] [seconds] [profile ...]: sustained throughput, live and
// elevated. For each profile (dualshock-4-v2 and xbox-360-wired unless
// profiles are named), one controller created through HMContext as a
// program creates it and one reader, a child process of this probe reading
// the controller's HID input reports as a game does. Each run has two
// windows of [seconds] (3 unless given): SubmitState once a millisecond,
// each frame on its own point of a 1 ms grid waited for on a
// high-resolution waitable timer, and SubmitState back to back on one
// thread. Over each window it reads the frames submitted, the reports the
// reader read, and the CPU cycles of this process, of the reader and of
// every WUDFHost process, which host the UMDF2 drivers. The summary lines
// give p50 and p95 over the runs: reports read per second, and kilocycles
// per report read (driver hosts, reader) or per frame submitted (this
// process). WUDFHost processes are only read, never stopped. Their sum
// includes every UMDF2 device on the machine, so the builds compared run
// on one machine with the same devices attached.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32.SafeHandles;

internal static partial class Program
{
    static int Rate(int runs, string[] args)
    {
        double seconds = 3;
        var ids = new List<string>();
        foreach (string a in args)
        {
            if (double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) && s > 0) seconds = s;
            else ids.Add(a);
        }
        if (ids.Count == 0) ids.AddRange(new[] { Ds4, Xbox360 });
        if (!Elevated("rate")) return 3;
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        foreach (string id in ids)
        {
            var profile = ctx.GetProfile(id);
            if (profile == null) { Check($"{id}: a catalog profile", false, "no such id"); continue; }
            using var c = ctx.CreateController(profile, s_key);
            if (!OnEmbeddedDriver(c, id)) return 5;
            string? path = OwnInputInterface(c);
            if (path == null) { Check($"{id}: a HID input interface under its devnode", false, "none in 5 s"); continue; }
            RateProfile(c, id, path, runs, seconds);
        }
        return 0;
    }

    sealed class RateWindow
    {
        public readonly List<double> ReadsPerSecond = new(), SubmitsPerSecond = new(),
            HostKcPerRead = new(), ReaderKcPerRead = new(), SelfKcPerSubmit = new();
    }

    static void RateProfile(HMController c, string id, string path, int runs, double seconds)
    {
        using var reader = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            ArgumentList = { "rate-reader", path },
        })!;
        try
        {
            var a = new HMGamepadState { Buttons = HMButton.A };
            var b = new HMGamepadState { Buttons = HMButton.B };
            c.SubmitState(a);
            // Past the reader's open and the tiered JIT's recompiles of the
            // submit path, so the windows read the steady state.
            Thread.Sleep(1000);
            var hosts = Process.GetProcessesByName("WUDFHost");
            var paced = new RateWindow();
            var free = new RateWindow();
            using var timer = HighResolutionTimer();
            for (int run = 0; run < runs; run++)
            {
                RateRun(c, a, b, reader, hosts, seconds, timer, paced);
                RateRun(c, a, b, reader, hosts, seconds, null, free);
            }
            foreach (var h in hosts) h.Dispose();
            ReportWindow($"{id}, SubmitState every millisecond", paced, runs);
            ReportWindow($"{id}, SubmitState back to back", free, runs);
        }
        finally
        {
            try { reader.StandardInput.Close(); } catch (System.IO.IOException) { }
            // The reader ends when its input closes. One that does not is this
            // probe's own child, stopped here.
            if (!reader.WaitForExit(5000)) reader.Kill();
        }
    }

    static void ReportWindow(string name, RateWindow w, int runs)
    {
        Report($"{name}: frames submitted per second", w.SubmitsPerSecond, runs, "");
        Report($"{name}: reports read per second", w.ReadsPerSecond, runs, "");
        Report($"{name}: driver hosts' kilocycles per report read", w.HostKcPerRead, runs, "");
        Report($"{name}: the reader's kilocycles per report read", w.ReaderKcPerRead, runs, "");
        Report($"{name}: this process's kilocycles per frame submitted", w.SelfKcPerSubmit, runs, "");
    }

    static void RateRun(HMController c, in HMGamepadState a, in HMGamepadState b, Process reader, Process[] hosts,
        double seconds, SafeWaitHandle? timer, RateWindow into)
    {
        using var self = Process.GetCurrentProcess();
        long reads0 = ReaderCount(reader);
        ulong self0 = Cycles(self), reader0 = Cycles(reader), hosts0 = hosts.Aggregate(0UL, (s, p) => s + Cycles(p));
        long t0 = Stopwatch.GetTimestamp();
        long end = t0 + (long)(seconds * Stopwatch.Frequency);
        long period = Stopwatch.Frequency / 1000, next = t0;
        long submits = 0;
        bool flip = false;
        while (Stopwatch.GetTimestamp() < end)
        {
            // The grid point, not the last wake plus a period, so a late
            // wake does not lower the rate.
            if (timer != null) WaitUntil(timer, next += period);
            flip = !flip;
            c.SubmitState(flip ? b : a);
            submits++;
        }
        long t1 = Stopwatch.GetTimestamp();
        ulong self1 = Cycles(self), reader1 = Cycles(reader), hosts1 = hosts.Aggregate(0UL, (s, p) => s + Cycles(p));
        long reads1 = ReaderCount(reader);
        double wall = (t1 - t0) / (double)Stopwatch.Frequency;
        long reads = reads1 - reads0;
        into.SubmitsPerSecond.Add(submits / wall);
        into.ReadsPerSecond.Add(reads / wall);
        if (reads > 0)
        {
            into.HostKcPerRead.Add((hosts1 - hosts0) / 1000.0 / reads);
            into.ReaderKcPerRead.Add((reader1 - reader0) / 1000.0 / reads);
        }
        if (submits > 0) into.SelfKcPerSubmit.Add((self1 - self0) / 1000.0 / submits);
    }

    /// <summary>The reader's count of reports read so far: one line out for
    /// each line in.</summary>
    static long ReaderCount(Process reader)
    {
        reader.StandardInput.WriteLine("count");
        reader.StandardInput.Flush();
        return long.TryParse(reader.StandardOutput.ReadLine(), out long n) ? n : -1;
    }

    /// <summary>The reader: input reports read as fast as the device gives
    /// them on one thread, on a handle opened as hidapi opens it, and the
    /// count so far printed for each line on stdin. Ends when stdin closes,
    /// with a read still pending.</summary>
    public static int RateReader(string path)
    {
        var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) { Console.WriteLine("-1"); return 1; }
        long count = 0;
        var t = new Thread(() =>
        {
            var buffer = new byte[4096];
            while (ReadFile(h, buffer, buffer.Length, out _, IntPtr.Zero)) Interlocked.Increment(ref count);
        }) { IsBackground = true };
        t.Start();
        while (Console.ReadLine() is not null)
        {
            Console.WriteLine(Interlocked.Read(ref count));
            Console.Out.Flush();
        }
        return 0;
    }

    static SafeWaitHandle HighResolutionTimer()
    {
        var h = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (h.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return h;
    }

    /// <summary>Wait for the Stopwatch timestamp <paramref name="target"/>
    /// on the timer, armed one shot for the time left (relative, in 100 ns
    /// units). A target already past returns at once.</summary>
    static void WaitUntil(SafeWaitHandle timer, long target)
    {
        long left = target - Stopwatch.GetTimestamp();
        if (left <= 0) return;
        long due = -Math.Max(1, left * 10_000_000 / Stopwatch.Frequency);
        if (!SetWaitableTimerEx(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        WaitForSingleObject(timer, 100);
    }

    /// <summary>A process's CPU cycles. A process this run cannot read
    /// fails the run, rather than read as 0.</summary>
    static ulong Cycles(Process p)
    {
        if (QueryProcessCycleTime(p.Handle, out ulong cycles)) return cycles;
        throw new InvalidOperationException($"rate cannot read the cycles of {p.ProcessName} ({p.Id}): error {Marshal.GetLastWin32Error()}");
    }

    const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long dueTime, int periodMs, IntPtr completion, IntPtr arg, IntPtr wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);
}
