using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace HIDMaestro.Internal;

/// <summary>The failures of one of the SDK's long-running loops: a device
/// clock, a pump, a reader or the USB/IP server's accept loop. The loop
/// catches a pass that fails and runs the next one, and this says how long
/// it waits first: 1 ms after one failure, doubling with each failure in a
/// row up to the loop's cap. A pass that fails at once again and again, a
/// throw or a wait on a handle that fails, so never spins. A pass that does
/// not fail ends the run, and the next failure waits 1 ms again.
///
/// <para>The diagnostic log (<see cref="DeviceOrchestrator.LogDiag"/>)
/// gets a line under the loop's thread name for the first failure and for
/// the second, fourth, eighth and every later power of two, then one line
/// once a pass begins a second or more after the last failure. Failures
/// less than a second apart count as one episode, so a pass that fails on
/// some frames and not others writes no more lines than one that fails on
/// all of them.</para>
///
/// <para>A loop keeps one in a local, or in a field that one thread at a
/// time uses. None of it is thread-safe. A pass that fails nothing costs
/// the test of one field in <see cref="NextPass"/>, with no lock, no wait
/// and no allocation.</para></summary>
internal struct LoopFailures
{
    /// <summary>The longest wait between two passes that fail, for a loop
    /// whose wait its stop ends at once.</summary>
    internal const int MaxWaitMs = 100;

    /// <summary>How long a loop runs without a failure before the log says
    /// the episode ended.</summary>
    internal const int QuietMs = 1000;

    // Failures in the episode, 0 outside one, and failures in a row. Both
    // stop at int.MaxValue.
    private int _count;
    private int _inRow;
    // The last pass to begin failed.
    private bool _lastFailed;
    // Environment.TickCount64 at the episode's first and latest failure.
    private long _first;
    private long _last;

    /// <summary>True from a failure until the log says the episode
    /// ended.</summary>
    public readonly bool Failing => _count != 0;

    /// <summary>Called as each pass begins.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void NextPass()
    {
        if (_count != 0) AfterFailure();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AfterFailure()
    {
        if (_lastFailed)
        {
            _lastFailed = false;
            return;
        }
        _inRow = 0;
        long now = Environment.TickCount64;
        if (now - _last < QuietMs) return;
        Log($"{_count} failure(s) over {_last - _first} ms, then none for a second");
        _count = 0;
    }

    /// <summary>A pass threw <paramref name="e"/>. Returns the wait before
    /// the next pass in milliseconds, at most <paramref name="capMs"/>.</summary>
    public int Threw(Exception e, int capMs = MaxWaitMs) => Failed(e, 0, null, capMs);

    /// <summary>A wait failed at once with Win32 error
    /// <paramref name="error"/>, named in the log as <paramref name="wait"/>
    /// or "a wait". Returns the wait before the next pass in milliseconds,
    /// at most <paramref name="capMs"/>.</summary>
    public int WaitFailed(int error, int capMs = MaxWaitMs, string? wait = null) => Failed(null, error, wait, capMs);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int Failed(Exception? e, int error, string? wait, int capMs)
    {
        long now = Environment.TickCount64;
        if (_count == 0) _first = now;
        _last = now;
        _lastFailed = true;
        if (_count < int.MaxValue) _count++;
        if (_inRow < int.MaxValue) _inRow++;
        if ((_count & (_count - 1)) == 0)
            Log(e, error, wait, _count);
        return _inRow > 30 ? capMs : (int)Math.Min(capMs, 1L << (_inRow - 1));
    }

    /// <summary>The loop ends on <paramref name="e"/>, which it does not
    /// retry: one line under the thread's name.</summary>
    public static void Ended(Exception e)
    {
        try { Log($"ended by {e.GetType().FullName}: {e.Message}"); }
        catch { }
    }

    private static void Log(Exception? e, int error, string? wait, int n)
    {
        // An exception's Message is its own code, and may throw.
        try
        {
            Log(e != null ? $"a pass threw {e.GetType().FullName}: {e.Message} (failure {n})"
                          : $"{wait ?? "a wait"} failed with Win32 error {error} (failure {n})");
        }
        catch { }
    }

    private static void Log(string what)
    {
        try { DeviceOrchestrator.LogDiag($"{Thread.CurrentThread.Name ?? $"thread {Environment.CurrentManagedThreadId}"}: {what}"); }
        catch { }
    }
}
