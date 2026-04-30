using System;
using System.Globalization;

namespace HIDMaestro.Internal;

/// <summary>
/// Multiplier applied to every wall-clock timeout in the SDK so the same
/// binaries run cleanly on both fast hardware (default scale 1.0) and on
/// ultra-low-end hardware where the existing budgets would trip.
///
/// <para>Set <c>HIDMAESTRO_TIMEOUT_SCALE</c> in the process environment to
/// scale every (a)-class deadlock backstop and (b)-class progress-bounded
/// wait. Pacing ticks (the 8 ms output poll, the 100 ms registry-poll
/// inside Wait* loops) are NOT scaled — they're cadence, not deadlines.</para>
///
/// <para><b>Defaults:</b> <c>1.0</c> (every existing fast-machine deployment
/// behaves identically). Range is clamped to <c>[0.1, 100.0]</c> — values
/// outside that range fall back to <c>1.0</c> with a single
/// <c>OutputDebugString</c> warning. <c>HIDMaestroTimeoutScale</c> is read
/// once at type init and cached; changing the env var mid-process does not
/// affect already-resolved budgets.</para>
///
/// <para><b>Recommendations by hardware tier:</b></para>
/// <list type="bullet">
/// <item><description><c>1.0</c> — modern desktop / laptop (Skylake+, NVMe). Default.</description></item>
/// <item><description><c>2.0</c>–<c>3.0</c> — older desktops, mechanical-disk laptops, hosts under heavy concurrent load.</description></item>
/// <item><description><c>5.0</c>–<c>10.0</c> — Atom-class CPUs, eMMC storage, 4 GB RAM tablets/embedded boxes.</description></item>
/// </list>
///
/// <para>Tradeoff: bumping the scale makes real failures take longer to
/// surface (5 minutes of "stuck" instead of 30 seconds before the throw).
/// That's the right trade — when timeouts fire today, the cause is rarely
/// "the OS is genuinely hosed and we should fail fast"; it's usually
/// "this got slower than expected on this machine." Slow diagnostics are
/// fine; spurious crashes on user hardware aren't.</para>
/// </summary>
internal static class TimeoutScale
{
    /// <summary>Active scale factor, clamped to [0.1, 100.0]. Read once at
    /// type init from <c>HIDMAESTRO_TIMEOUT_SCALE</c>; defaults to 1.0.</summary>
    public static readonly double Factor = ResolveFactor();

    /// <summary>Multiply a wall-clock budget (in milliseconds) by the active
    /// scale factor, saturating at <see cref="int.MaxValue"/>. Used at every
    /// SDK call site that passes a <c>timeoutMs</c> down to a Win32 API.</summary>
    public static int Apply(int ms)
    {
        if (ms <= 0) return ms;          // 0 / negative = caller-meaningful, don't scale
        if (Factor == 1.0) return ms;     // hot path: most users
        double scaled = ms * Factor;
        if (scaled >= int.MaxValue) return int.MaxValue;
        return (int)scaled;
    }

    /// <summary>Multiply a TimeSpan budget by the active scale factor.</summary>
    public static TimeSpan Apply(TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero || Factor == 1.0) return budget;
        double scaledTicks = budget.Ticks * Factor;
        if (scaledTicks >= long.MaxValue) return TimeSpan.MaxValue;
        return TimeSpan.FromTicks((long)scaledTicks);
    }

    private static double ResolveFactor()
    {
        try
        {
            string? raw = Environment.GetEnvironmentVariable("HIDMAESTRO_TIMEOUT_SCALE");
            if (string.IsNullOrWhiteSpace(raw)) return 1.0;

            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[HIDMaestro] HIDMAESTRO_TIMEOUT_SCALE='{raw}' is not a number; using 1.0");
                return 1.0;
            }

            if (v < 0.1 || v > 100.0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[HIDMaestro] HIDMAESTRO_TIMEOUT_SCALE={v} out of range [0.1, 100.0]; using 1.0");
                return 1.0;
            }

            return v;
        }
        catch
        {
            return 1.0;
        }
    }
}
