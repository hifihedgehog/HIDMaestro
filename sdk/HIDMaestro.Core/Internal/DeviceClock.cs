using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using static HIDMaestro.Internal.ClockNative;

namespace HIDMaestro.Internal;

/// <summary>Where a device clock's grid starts.</summary>
internal enum ClockOrigin : byte
{
    /// <summary>The device's attach, or a port reset.</summary>
    Attach,
    /// <summary>The command that let reports flow.</summary>
    InputOpened,
}

/// <summary>What a device clock does about a tick it could not use.</summary>
internal enum MissedTick : byte
{
    /// <summary>The grid holds. A tick that finds no read waiting leaves one
    /// report owed, and the next read is answered as it arrives. The first
    /// tick of a run is one period after its origin.</summary>
    Owe,
    /// <summary>The grid holds. A tick that finds no read waiting sends
    /// nothing, and a read waits for the next tick. The first tick of a run is
    /// the next grid point.</summary>
    NextPoll,
    /// <summary>The period restarts from a late tick, and from the consumer's
    /// own frame: the idle repeat's rule, a report again one period after the
    /// last.</summary>
    Drop,
}

/// <summary>What a device clock drives on each tick. A value type, so the
/// clock is compiled once per tick type and the tick, with any program it
/// runs, inlines into the clock's loop.</summary>
internal interface IClockTick
{
    /// <summary>Whether the clock runs now, and the origin of the current
    /// run. A new origin starts a new run.</summary>
    bool Running(out long origin);

    /// <summary>The period in Stopwatch ticks. A change starts a new
    /// run.</summary>
    long Period { get; }

    /// <summary>The time before which the next tick does not come, 0 for
    /// none: the idle repeat's last consumer frame plus a period.</summary>
    long NotBefore { get; }

    void Tick(long due);

    /// <summary>The clock saw its run end.</summary>
    void RunEnded();
}

/// <summary>The device clock of mechanism 2: one class for every report a
/// device sends on its own time, with a period, an origin, a missed-tick rule
/// and a high-resolution waitable timer. The GameCube adapter's host-poll
/// grid, the Switch 2 Pro's 4 ms reports and the idle repeat all run on it.
/// The period is a whole number of Stopwatch ticks, so a period of 1.25 ms
/// runs as 1.25 ms. A clock held up past several ticks skips them rather than
/// sending a burst. A plain wait rounds up to the 15.6 ms system tick, so the
/// wait is a high-resolution waitable timer (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
/// Windows 10 1803 and later), with the plain wait as the fallback.</summary>
internal interface IDeviceClock : IDisposable
{
    void Start();
    void Wake();
    void Stop(int joinMs);
    /// <summary>The clock's thread, for code that checks it runs.</summary>
    Thread Thread { get; }

    /// <summary>One tick now, on the calling thread, outside the clock's
    /// grid: device_oracle_check's replay drives a device's ticks itself, in
    /// a fixed order, and leaves the clock unstarted. Returns false when the
    /// clock would not run.</summary>
    bool TickNow();
}

internal sealed class DeviceClock<TTick> : IDeviceClock where TTick : struct, IClockTick
{
    private TTick _tick;
    private readonly MissedTick _missed;
    private readonly AutoResetEvent _wake = new(false);
    private readonly IntPtr _timer;
    private readonly uint _waitCapMs;
    private readonly Thread _thread;
    private readonly object _lifetime = new();
    private volatile bool _stop;
    private bool _closed;

    /// <param name="everyChangeWakes">Every change to what the tick reads
    /// calls <see cref="Wake"/>, so the clock waits for its next tick in one
    /// wait. Otherwise a wait lasts at most 100 ms, which bounds how late the
    /// clock sees a change nothing woke it for. At a 250 ms period the cap
    /// is three wakes a period, each about 50,000 cycles.</param>
    public DeviceClock(TTick tick, MissedTick missed, string threadName, bool everyChangeWakes = false)
    {
        _tick = tick;
        _missed = missed;
        _waitCapMs = everyChangeWakes ? INFINITE : 100;
        _timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = threadName,
            // The same priority as the USB/IP input pump that carries a
            // device's frames to the wire.
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public void Start() => _thread.Start();

    public Thread Thread => _thread;

    public bool TickNow()
    {
        if (!_tick.Running(out _)) return false;
        _tick.Tick(Stopwatch.GetTimestamp());
        return true;
    }

    /// <summary>Something the tick reads changed: look again now.</summary>
    public void Wake()
    {
        lock (_lifetime)
            if (!_closed) _wake.Set();
    }

    /// <summary>Ends the clock and waits up to <paramref name="joinMs"/> for
    /// its thread, which closes its own handles as it ends.</summary>
    public void Stop(int joinMs)
    {
        _stop = true;
        Wake();
        if (_thread.ThreadState == System.Threading.ThreadState.Unstarted) Close();
        else if (Thread.CurrentThread != _thread)
            try { _thread.Join(joinMs); } catch (ThreadStateException) { }
    }

    public void Dispose() => Stop(Timeout.Infinite);

    /// <summary>The first tick of a run.</summary>
    internal static long FirstDue(MissedTick missed, long origin, long now, long period) => missed switch
    {
        MissedTick.Owe => origin + period,
        MissedTick.NextPoll => origin + ((now - origin) / period + 1) * period,
        _ => origin,
    };

    /// <summary>The tick after <paramref name="due"/>, at <paramref name="now"/>.</summary>
    internal static long NextDue(MissedTick missed, long due, long now, long period)
    {
        due += period;
        if (due > now) return due;
        return missed == MissedTick.Drop ? now + period : due + ((now - due) / period + 1) * period;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Loop()
    {
        var waits = new[] { _timer, _wake.SafeWaitHandle.DangerousGetHandle() };
        long freq = Stopwatch.Frequency;
        bool running = false;
        long origin = 0, period = 0, due = 0;
        try
        {
            while (!_stop)
            {
                try
                {
                    if (!_tick.Running(out long o))
                    {
                        if (running)
                        {
                            running = false;
                            _tick.RunEnded();
                        }
                        _wake.WaitOne(500);
                        continue;
                    }
                    long p = Math.Max(1, _tick.Period);
                    long now = Stopwatch.GetTimestamp();
                    if (!running || o != origin || p != period)
                    {
                        // A new run. It may follow a stop this loop never
                        // saw, so its origin tells the runs apart.
                        running = true;
                        origin = o;
                        period = p;
                        due = FirstDue(_missed, origin, now, period);
                    }
                    long notBefore = _tick.NotBefore;
                    if (notBefore > due) due = notBefore;
                    if (now >= due)
                    {
                        _tick.Tick(due);
                        due = NextDue(_missed, due, now, period);
                        continue;
                    }

                    long wait = due - now;
                    if (_timer != IntPtr.Zero)
                    {
                        long rel = -Math.Max(1, wait * 10_000_000L / freq); // relative, 100 ns units
                        if (SetWaitableTimer(_timer, ref rel, 0, IntPtr.Zero, IntPtr.Zero, false)
                            && WaitForMultipleObjects(2, waits, false, _waitCapMs) != WAIT_FAILED)
                            continue;
                    }
                    // No high-resolution timer (Windows 10 before 1803) or a
                    // failed wait: the plain wait, coarser but still running.
                    _wake.WaitOne((int)Math.Max(1, wait * 1000 / freq));
                }
                catch
                {
                    // The input pump's containment contract: a transient
                    // failure must not end the clock.
                }
            }
        }
        finally
        {
            Close();
        }
    }

    private void Close()
    {
        lock (_lifetime)
        {
            if (_closed) return;
            _closed = true;
        }
        if (_timer != IntPtr.Zero) CloseHandle(_timer);
        _wake.Dispose();
    }
}

/// <summary>The waitable timer calls the device clock makes.</summary>
internal static partial class ClockNative
{
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    public const uint TIMER_ALL_ACCESS = 0x1F0003;
    public const uint WAIT_FAILED = 0xFFFFFFFF;
    public const uint INFINITE = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr CreateWaitableTimerExW(IntPtr attrs, IntPtr name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForMultipleObjects(uint count, IntPtr[] handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint ms);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);
}
