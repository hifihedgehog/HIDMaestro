using System;
using System.Runtime.CompilerServices;


namespace HIDMaestro.Internal;

/// <summary>The one arithmetic by which the SDK turns a normalized value
/// into a wire value: the descriptor path, the report codec, the XUSB slice
/// and the Switch packers all call it.
///
/// <para>A value v in [0, 1], 0 at the left and the top as the SDK runs its
/// axes, goes on the wire as <c>min + RoundHalfEven(v' x (max - min))</c>
/// computed in double, where v' is v clamped to [0, 1], or 1 minus that for
/// a field whose wire grows the other way. A float carries 24 significant
/// bits, so the product is exact in double for any span below 2^29 and the
/// only rounding is the last one. Every raw value a consumer reads from a
/// device and normalizes back, by double division, float division or a
/// float reciprocal, then returns as that value. Truncation, which the
/// descriptor path used, sends a float that lands a hair under its raw
/// value one count low.</para>
///
/// <para>A NaN input has no place in [0, 1]. It writes the field's rest
/// value, the value the field takes when the program leaves it unwritten:
/// 0.5 on a stick or a centered axis, 0 on a trigger, before any inversion.
/// A NaN in <see cref="HMGamepadState.Axes"/> takes the same rest where the
/// SDK reads it (<see cref="Normalized"/>), so one stick never has two
/// answers.</para></summary>
internal static class WireArith
{
    /// <summary>The wire value of <paramref name="v"/> on a field running
    /// from <paramref name="min"/> at 0 to <paramref name="max"/> at 1, or
    /// the other way when <paramref name="invert"/>. A NaN takes
    /// <paramref name="rest"/>, the field's unwritten value: 0.5 for a stick
    /// or a centered axis, 0 for a trigger.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Linear(double v, int min, int max, bool invert, double rest)
    {
        if (double.IsNaN(v)) v = rest;
        // Min and Max rather than Clamp: the same value for an input that
        // is not NaN, in two instructions and no branch.
        double c = Math.Max(0.0, Math.Min(v, 1.0));
        if (invert) c = 1.0 - c;
        // The span is below 2^32, so the product is under 2^51 and the
        // bias rounds it. The sum wraps back into the field's range.
        return unchecked(min + (int)BitConverter.DoubleToInt64Bits(c * ((long)max - min) + RoundingBias));
    }

    /// <summary><see cref="Linear"/> from 0 for an input the caller has
    /// already clamped to [0, 1], never NaN: <c>RoundHalfEven(c x span)</c>.
    /// The XUSB slice's six conversions take it on values ResolveSimpleSlots
    /// gives, and the descriptor path on values GetAxisValue gives, each a
    /// NaN already replaced by its rest (<see cref="Normalized"/>), so a
    /// frame pays no second clamp and no NaN test. A span of 2^31 or more
    /// returns the low 32 bits, which the descriptor path's min + Unit wraps
    /// back into the field's range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Unit(double c, double span) =>
        (int)BitConverter.DoubleToInt64Bits(c * span + RoundingBias);

    /// <summary>The wire value of <paramref name="v"/> on a field whose
    /// hosts read <paramref name="center"/> as the stick at rest, with the
    /// field's two slopes from <see cref="Slopes"/>. With t = v' - 0.5 the
    /// value is <c>center + RoundHalfEven(t x sL + max(t, 0) x (sH - sL))</c>,
    /// rounded once as a distance from the center, so 0.5 gives the center
    /// and so does a tie beside it. A NaN rests at the center.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Centered(double v, int min, int center, int max, double slopeLow, double slopeStep, bool invert)
    {
        if (double.IsNaN(v)) return center;
        double c = Math.Max(0.0, Math.Min(v, 1.0));
        if (invert) c = 1.0 - c;
        double t = c - 0.5;
        return center + (int)Math.Round(t * slopeLow + Math.Max(t, 0.0) * slopeStep);
    }

    /// <summary><see cref="Centered"/> for an input the caller has already
    /// clamped to [0, 1] and that is not NaN, uninverted.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CenteredUnit(double c, int center, double slopeLow, double slopeStep)
    {
        double t = c - 0.5;
        return center + (int)BitConverter.DoubleToInt64Bits(t * slopeLow + Math.Max(t, 0.0) * slopeStep + RoundingBias);
    }

    /// <summary>1.5 x 2^52. Added to a double y with |y| below 2^51, the
    /// sum lies where doubles are one apart, so the addition itself rounds
    /// y to the nearest integer, ties to even, by IEEE 754's default
    /// rounding, and the low 32 bits of the sum are that integer in two's
    /// complement. Math.Round and a conversion give the same integer, at
    /// the cost of the saturating conversion.</summary>
    private const double RoundingBias = 6755399441055744.0;

    /// <summary>The two slopes of a centered field, computed once when its
    /// profile compiles: sL = 2 (c - min) below the center c, and the step
    /// sH - sL with sH = 2 (max - c) above it.</summary>
    public static (double SlopeLow, double SlopeStep) Slopes(int min, int center, int max)
    {
        double low = 2.0 * ((long)center - min);
        double high = 2.0 * ((long)max - center);
        return (low, high - low);
    }

    /// <summary>A calibrated value, g or degrees per second, as signed
    /// 16-bit counts: <c>RoundHalfEven(v x numerator / denominator)</c> in
    /// double, clamped to the int16 range. A ratio such as 13371/936 is one
    /// exact product and one correctly rounded division.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short Scaled(float v, double numerator, double denominator)
    {
        if (float.IsNaN(v)) return 0;
        double x = Math.Round((double)v * numerator / denominator);
        return (short)Math.Clamp(x, short.MinValue, short.MaxValue);
    }

    /// <summary>A value a program wrote into HMGamepadState.Axes, clamped to
    /// [0, 1], or <paramref name="rest"/>, the value of the axis left
    /// unwritten, for NaN.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Normalized(float v, double rest) => float.IsNaN(v) ? rest : Math.Clamp(v, 0f, 1f);
}
