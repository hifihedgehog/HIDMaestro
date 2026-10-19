// clock [seconds] [periodMs] [frameMs]: offline, no elevation. The idle
// repeat's device clock (DeviceClock with MissedTick.Drop and the repeat's
// NotBefore, one period after the consumer's last frame) on a tick that
// only counts, while a consumer thread sends a frame on a 1 ms grid of
// a high-resolution waitable timer as the SDK's submit paths do, then at
// rest. Prints, for each window, the clock loop's passes per second, its
// ticks per second, the frames per second, the shortest time from a frame
// to a tick, and this process's CPU cycles per second:
//   clock,<window>,<passes/s>,<ticks/s>,<frames/s>,<min frame-to-tick ms>,<Mcycles/s>
// While frames come faster than the period, a clock that wakes only for a
// gap in them makes no pass and no tick. At rest it makes two passes a tick
// and one wake. Built against a tree whose clock has no FrameSent, the
// consumer only moves the timestamp, as that tree's submit paths did.
#if !HM_BASE
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro.Internal;

internal static class ClockCost
{
    static long s_last, s_period, s_passes, s_ticks, s_minGap = long.MaxValue;

    struct CountTick : IClockTick
    {
        public bool Running(out long origin) { origin = 0; Interlocked.Increment(ref s_passes); return true; }
        public long Period => s_period;
        public long NotBefore { get { long l = Volatile.Read(ref s_last); return l == 0 ? 0 : l + s_period; } }
        public void Tick(long due)
        {
            Interlocked.Increment(ref s_ticks);
            long l = Volatile.Read(ref s_last);
            if (l != 0) { long gap = Stopwatch.GetTimestamp() - l; if (gap < Volatile.Read(ref s_minGap)) Volatile.Write(ref s_minGap, gap); }
        }
        public void RunEnded() { }
    }

    public static void Run(double seconds, double periodMs, double frameMs)
    {
        s_period = (long)(periodMs * Stopwatch.Frequency / 1000);
        var clock = new DeviceClock<CountTick>(new CountTick(), MissedTick.Drop, "clock-cost", everyChangeWakes: true);
        // FrameSent where the tree has it.
        var frameSent = typeof(DeviceClock<CountTick>).GetMethod("FrameSent", BindingFlags.Public | BindingFlags.Instance)
            ?.CreateDelegate<Action<long>>(clock);
        clock.Start();
        bool stop = false;
        long frames = 0;
        var consumer = new Thread(() =>
        {
            IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 2, 0x1F0003);
            long step = (long)(frameMs * Stopwatch.Frequency / 1000);
            long next = Stopwatch.GetTimestamp();
            while (!Volatile.Read(ref stop))
            {
                next += step;
                long rel = -Math.Max(1, (next - Stopwatch.GetTimestamp()) * 10_000_000L / Stopwatch.Frequency);
                SetWaitableTimer(timer, ref rel, 0, IntPtr.Zero, IntPtr.Zero, false);
                WaitForSingleObject(timer, 1000);
                long at = Stopwatch.GetTimestamp();
                Volatile.Write(ref s_last, at);
                frameSent?.Invoke(at);
                Interlocked.Increment(ref frames);
            }
            CloseHandle(timer);
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        consumer.Start();
        Thread.Sleep(500);
        Window("frames", seconds, () => Interlocked.Read(ref frames));
        Volatile.Write(ref stop, true);
        consumer.Join();
        Thread.Sleep(Math.Max(100, (int)(periodMs * 4)));
        Window("rest", seconds, () => Interlocked.Read(ref frames));
        clock.Stop(1000);
        Console.WriteLine($"clock: FrameSent {(frameSent != null ? "called" : "absent")}");
    }

    static void Window(string name, double seconds, Func<long> frames)
    {
        Volatile.Write(ref s_minGap, long.MaxValue);
        long p0 = Interlocked.Read(ref s_passes), k0 = Interlocked.Read(ref s_ticks), f0 = frames();
        QueryProcessCycleTime(GetCurrentProcess(), out ulong c0);
        long t0 = Stopwatch.GetTimestamp();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        long t1 = Stopwatch.GetTimestamp();
        QueryProcessCycleTime(GetCurrentProcess(), out ulong c1);
        long p1 = Interlocked.Read(ref s_passes), k1 = Interlocked.Read(ref s_ticks), f1 = frames();
        double wall = (t1 - t0) / (double)Stopwatch.Frequency;
        long gap = Volatile.Read(ref s_minGap);
        Console.WriteLine(FormattableString.Invariant(
            $"clock,{name},{(p1 - p0) / wall:F1},{(k1 - k0) / wall:F1},{(f1 - f0) / wall:F1},{(gap == long.MaxValue ? -1 : gap * 1000.0 / Stopwatch.Frequency):F3},{(c1 - c0) / wall / 1e6:F3}"));
    }

    [DllImport("kernel32.dll")] static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, IntPtr name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr completion, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);
}
#endif
