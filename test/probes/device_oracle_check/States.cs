// The input states cases 1 and 2 run every subject through: a scripted list
// that walks each input to its edges, then seeded random states. The same
// seed gives the same list.
using System;
using System.Collections.Generic;
using HIDMaestro;

internal static class States
{
    /// <summary>Random states after the scripted list.</summary>
    public const int RandomCount = 100_000;

    /// <summary>Each state is submitted this many times in a row, so a
    /// rolling counter shows in the outputs.</summary>
    public const int Repeats = 3;

    public static float Down(float v) => MathF.BitDecrement(v);
    public static float Up(float v) => MathF.BitIncrement(v);

    /// <summary>A value and its two float neighbors.</summary>
    static IEnumerable<float> AndNeighbors(float v)
    {
        yield return Down(v);
        yield return v;
        yield return Up(v);
    }

    /// <summary>Axis values: the quarters and their neighbors, and the
    /// values outside [0, 1] the clamp sees.</summary>
    public static readonly float[] AxisValues = Build(() =>
    {
        var l = new List<float>();
        foreach (float q in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f }) l.AddRange(AndNeighbors(q));
        l.AddRange(new[] { -0.0f, float.Epsilon, -float.Epsilon, -1f, 2f, float.NaN,
                           float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, float.MinValue });
        return l.ToArray();
    });

    /// <summary>The spans of every normalized-to-wire conversion the design
    /// note's arithmetic table lists (descriptor ranges, the codec's 8-, 12-
    /// and 16-bit scales, the XUSB slice's 1023 and 65535, the Switch Pro
    /// sticks' 0x600), so the floats where a truncation or a rounding
    /// changes its result are inputs.</summary>
    static readonly int[] Spans = { 1, 2, 3, 11, 127, 254, 255, 511, 767, 1023, 1535, 1536, 1792, 2047, 3071, 3600, 4094, 4095, 16383, 32766, 32767, 65534, 65535 };

    /// <summary>For each span R: k / R and (k + 0.5) / R as floats with their
    /// neighbors, for k at the ends and the middle of the span. Then the two
    /// floats the design note names: 0x39000801 and 0x3B008081 (LT_DIGITAL's
    /// edge).</summary>
    public static readonly float[] BoundaryValues = Build(() =>
    {
        var set = new SortedSet<uint>();
        void Add(double v)
        {
            foreach (float f in AndNeighbors((float)v))
                if (f >= 0f && f <= 1f) set.Add(BitConverter.SingleToUInt32Bits(f));
        }
        foreach (int r in Spans)
        {
            var ks = new SortedSet<int> { 0, 1, 2, r / 2 - 1, r / 2, r / 2 + 1, r - 2, r - 1, r };
            foreach (int k in ks)
            {
                if (k < 0 || k > r) continue;
                Add((double)k / r);
                Add((k + 0.5) / r);
            }
        }
        set.Add(0x39000801);
        set.Add(0x3B008081);
        var list = new List<float>();
        foreach (uint bits in set) list.Add(BitConverter.UInt32BitsToSingle(bits));
        return list.ToArray();
    });

    /// <summary>Hat angles: the sector edges of 4-, 8- and 16-position hats
    /// ((k + 0.5) x 360 / R, the rounding HidReportBuilder applies), the
    /// 22.5-degree edges the Switch packers test, each with its neighbors,
    /// and the wrap and special values.</summary>
    public static readonly float[] HatDegreeValues = Build(() =>
    {
        var set = new SortedSet<uint>();
        void Add(float v) { foreach (float f in AndNeighbors(v)) set.Add(BitConverter.SingleToUInt32Bits(f)); }
        foreach (int r in new[] { 4, 8, 16 })
            for (int k = -1; k <= r; k++)
                Add((float)((k + 0.5) * 360.0 / r));
        for (int k = -1; k <= 17; k++) Add(k * 22.5f);
        Add(720f); Add(-720f); Add(1e9f); Add(-1e9f);
        var list = new List<float>();
        foreach (uint bits in set) list.Add(BitConverter.UInt32BitsToSingle(bits));
        list.AddRange(new[] { -0.0f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, float.MinValue });
        return list.ToArray();
    });

    /// <summary>Hundredths of a degree at the truncation edges of 4-, 8- and
    /// 16-position hats (k x 36000 / R) and one either side.</summary>
    public static readonly int[] HatHundredthValues = Build(() =>
    {
        var set = new SortedSet<int> { int.MinValue, int.MaxValue, -1, 0, 1, 35999, 36000, 36001, 72000, -36000 };
        foreach (int r in new[] { 4, 8, 16 })
            for (int k = -1; k <= r + 1; k++)
            {
                int e = (int)((long)k * 36000 / r);
                set.Add(e - 1); set.Add(e); set.Add(e + 1);
            }
        return new List<int>(set).ToArray();
    });

    /// <summary>Counts per unit of every float IMU scale the SDK packs with
    /// at 9bd6279: the Switch Pro's 4096 per g and 13371/936 per
    /// degree/second (SwitchProPacker.cs:93-99), the Switch 2 Pro's 32767/8
    /// per g and pi/180 x 32767/34.8 per degree/second
    /// (Switch2ProPacker.cs:45, 50). Written out here, so the inputs stay
    /// the same when those classes change or go.</summary>
    static float[] ImuScales() => new[]
    {
        4096.0f,
        13371.0f / 936.0f,
        32767.0f / 8.0f,
        (float)(Math.PI / 180.0 * 32767.0 / 34.8),
    };

    /// <summary>Float IMU values: zero and its neighbors, the clamp edges of
    /// each scale (the values that round to 32767, 32768, -32768 and -32769
    /// counts) with their neighbors, and the special values.</summary>
    public static readonly float[] ImuValues = Build(() =>
    {
        var set = new SortedSet<uint>();
        void Add(float v) { foreach (float f in AndNeighbors(v)) set.Add(BitConverter.SingleToUInt32Bits(f)); }
        Add(0f);
        foreach (float s in ImuScales())
            foreach (double counts in new[] { 32767.0, 32767.5, 32768.0, -32768.0, -32768.5, -32769.0 })
                Add((float)(counts / s));
        var list = new List<float>();
        foreach (uint bits in set) list.Add(BitConverter.UInt32BitsToSingle(bits));
        list.AddRange(new[] { -0.0f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, float.MinValue });
        return list.ToArray();
    });

    /// <summary>The three ways a consumer turns a raw value k of an 8-bit or
    /// 16-bit axis into the SDK's [0, 1]: double division rounded to float,
    /// float division, and float times the reciprocal. Every k of 0 to 255,
    /// and k from 32700 to 32900 around the 16-bit center, where the design
    /// note's centered form (round 3 follow-up) says float division and the
    /// reciprocal return 64 and 65 even values one count low.</summary>
    public static readonly float[] CenteredValues = Build(() =>
    {
        var set = new SortedSet<uint>();
        void Add(int k, int r)
        {
            set.Add(BitConverter.SingleToUInt32Bits((float)((double)k / r)));
            set.Add(BitConverter.SingleToUInt32Bits((float)k / r));
            set.Add(BitConverter.SingleToUInt32Bits(k * (1f / r)));
        }
        for (int k = 0; k <= 255; k++) Add(k, 255);
        for (int k = 32700; k <= 32900; k++) Add(k, 65535);
        var list = new List<float>();
        foreach (uint bits in set) list.Add(BitConverter.UInt32BitsToSingle(bits));
        return list.ToArray();
    });

    static T Build<T>(Func<T> f) => f();

    /// <summary>The scripted states for a subject whose analog inputs are
    /// <paramref name="axes"/>. Each is yielded once. The dictionary in a
    /// state is <paramref name="scratch"/>, refilled for every state: use
    /// a state before asking for the next.</summary>
    public static IEnumerable<HMGamepadState> Scripted(IReadOnlyList<HMAxis> axes, Dictionary<HMAxis, float> scratch)
    {
        // Neutral: no axes at all, then every axis at each value together.
        yield return new HMGamepadState();
        foreach (float v in AxisValues)
        {
            scratch.Clear();
            foreach (var a in axes) scratch[a] = v;
            yield return new HMGamepadState { Axes = scratch };
        }
        // Each axis alone at each value.
        foreach (var a in axes)
            foreach (float v in AxisValues)
            {
                scratch.Clear();
                scratch[a] = v;
                yield return new HMGamepadState { Axes = scratch };
            }
        // PadForge's centered stick, (0 + 32768f) / 65535f
        // (HMaestroVirtualController.cs:848-851 in PadForge 95b9e6aa), on
        // every axis, and the triggers written with every other axis left
        // unwritten, so a default value by role or by sign shows
        // (design review FB14, N19).
        scratch.Clear();
        foreach (var a in axes) scratch[a] = 32768f / 65535f;
        yield return new HMGamepadState { Axes = scratch };
        scratch.Clear();
        scratch[HMAxis.Z] = 0f;
        scratch[HMAxis.Rz] = 0f;
        yield return new HMGamepadState { Axes = scratch };
        scratch.Clear();
        scratch[HMAxis.Z] = 1f;
        scratch[HMAxis.Rz] = 0.5f;
        yield return new HMGamepadState { Axes = scratch };
        // Every axis at each consumer normalization of the raw values
        // around a center.
        foreach (float v in CenteredValues)
        {
            scratch.Clear();
            foreach (var a in axes) scratch[a] = v;
            yield return new HMGamepadState { Axes = scratch };
        }
        // Every axis at each boundary float.
        foreach (float v in BoundaryValues)
        {
            scratch.Clear();
            foreach (var a in axes) scratch[a] = v;
            yield return new HMGamepadState { Axes = scratch };
        }
        // Each button alone, all, none.
        for (int bit = 0; bit < 32; bit++) yield return new HMGamepadState { Buttons = (HMButton)(1u << bit) };
        yield return new HMGamepadState { Buttons = (HMButton)uint.MaxValue };
        // Each hat value, the eight directions and values past them.
        for (int h = 0; h <= 15; h++) yield return new HMGamepadState { Hat = (HMHat)h };
        yield return new HMGamepadState { Hat = (HMHat)255 };
        foreach (float d in HatDegreeValues) yield return new HMGamepadState { HatDegrees = d };
        foreach (int c in HatHundredthValues) yield return new HMGamepadState { HatHundredths = c };
        for (int r = 0; r <= 17; r++) yield return new HMGamepadState { HatRaw = (ushort)r };
        yield return new HMGamepadState { HatRaw = 0xFF };
        yield return new HMGamepadState { HatRaw = 0xFFFF };
        // The priority chain: every source set, then each lower pair.
        yield return new HMGamepadState { Hat = HMHat.South, HatRaw = 2, HatHundredths = 27000, HatDegrees = 45f };
        yield return new HMGamepadState { Hat = HMHat.South, HatRaw = 2, HatHundredths = 27000 };
        yield return new HMGamepadState { Hat = HMHat.South, HatRaw = 2 };
        // Each IMU field alone.
        foreach (float v in ImuValues)
        {
            yield return new HMGamepadState { AccelGX = v };
            yield return new HMGamepadState { AccelGY = v };
            yield return new HMGamepadState { AccelGZ = v };
            yield return new HMGamepadState { GyroDpsX = v };
            yield return new HMGamepadState { GyroDpsY = v };
            yield return new HMGamepadState { GyroDpsZ = v };
        }
        foreach (short v in new short[] { 0, 1, -1, short.MaxValue, short.MinValue, 0x1234 })
        {
            yield return new HMGamepadState { GyroPitch = v };
            yield return new HMGamepadState { GyroYaw = v };
            yield return new HMGamepadState { GyroRoll = v };
            yield return new HMGamepadState { AccelX = v };
            yield return new HMGamepadState { AccelY = v };
            yield return new HMGamepadState { AccelZ = v };
        }
        // The sensor timestamp, across its 32-bit wrap.
        foreach (uint t in new uint[] { 0, 1, 2, 3, 15, 16, 17, 4000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFF0, 0xFFFFFFFF, 0, 5 })
            yield return new HMGamepadState { SensorTimestamp = t };
        // The touchpad at its corners, and the packet counter.
        foreach (ushort x in new ushort[] { 0, 1, 1919, 1920, 4095, ushort.MaxValue })
        {
            yield return new HMGamepadState { TouchpadFinger0Active = true, TouchpadFinger0X = x, TouchpadFinger0Y = x, TouchpadFinger0Id = (byte)x };
            yield return new HMGamepadState { TouchpadFinger1Active = true, TouchpadFinger1X = x, TouchpadFinger1Y = (ushort)(x ^ 0x5A5), TouchpadFinger1Id = (byte)(x >> 3) };
        }
        yield return new HMGamepadState { TouchpadFinger0X = 100, TouchpadFinger0Y = 200 };
        foreach (byte c in new byte[] { 0, 1, 127, 128, 255 }) yield return new HMGamepadState { TouchpadPacketCounter = c };
        // Each pressure alone, with and without its button or hat.
        foreach (byte p in new byte[] { 0, 1, 254, 255 })
        {
            yield return new HMGamepadState { PressureA = p };
            yield return new HMGamepadState { PressureA = p, Buttons = HMButton.A };
            yield return new HMGamepadState { PressureB = p, Buttons = HMButton.B };
            yield return new HMGamepadState { PressureX = p, Buttons = HMButton.X };
            yield return new HMGamepadState { PressureY = p, Buttons = HMButton.Y };
            yield return new HMGamepadState { PressureLeftBumper = p, Buttons = HMButton.LeftBumper };
            yield return new HMGamepadState { PressureRightBumper = p, Buttons = HMButton.RightBumper };
            yield return new HMGamepadState { PressureDpadUp = p, Hat = HMHat.North };
            yield return new HMGamepadState { PressureDpadRight = p, Hat = HMHat.East };
            yield return new HMGamepadState { PressureDpadDown = p, Hat = HMHat.South };
            yield return new HMGamepadState { PressureDpadLeft = p, Hat = HMHat.West };
            yield return new HMGamepadState { PressureDpadUp = p };
        }
        // Battery and the status flags.
        for (int level = 0; level <= 16; level++) yield return new HMGamepadState { BatteryLevel = (byte)level };
        yield return new HMGamepadState { BatteryLevel = 255 };
        yield return new HMGamepadState { BatteryCharging = true, BatteryLevel = 5 };
        yield return new HMGamepadState { BatteryFull = true, BatteryLevel = 5 };
        yield return new HMGamepadState { BatteryFull = true, BatteryCharging = true };
        yield return new HMGamepadState { MicMuted = true };
        yield return new HMGamepadState { HeadphonesConnected = true };
    }

    /// <summary>One random state. Each field is drawn, so a field the
    /// subject ignores still moves the generator the same way whatever the
    /// subject reads.</summary>
    public static HMGamepadState Random(Rng r, IReadOnlyList<HMAxis> axes, Dictionary<HMAxis, float> scratch)
    {
        var s = new HMGamepadState();
        scratch.Clear();
        foreach (var a in axes)
        {
            int pick = r.Next(100);
            float v = r.Float01();
            if (pick < 15) continue;                                       // absent
            if (pick < 25) v = AxisValues[r.Next(AxisValues.Length)];
            else if (pick < 40) v = BoundaryValues[r.Next(BoundaryValues.Length)];
            scratch[a] = v;
        }
        s.Axes = r.Next(20) == 0 ? null : scratch;

        int b = r.Next(10);
        s.Buttons = (HMButton)(b == 0 ? 0u : b == 1 ? 1u << r.Next(32) : r.NextU32());
        s.Hat = (HMHat)(r.Next(20) == 0 ? r.Next(256) : r.Next(9));
        int hs = r.Next(16);
        if (hs == 0) s.HatDegrees = r.Bool() ? HatDegreeValues[r.Next(HatDegreeValues.Length)] : (r.Float01() * 1440f - 720f);
        if (hs == 1) s.HatHundredths = r.Bool() ? HatHundredthValues[r.Next(HatHundredthValues.Length)] : r.Next(144000) - 72000;
        if (hs == 2) s.HatRaw = (ushort)(r.Bool() ? r.Next(18) : r.Next(65536));

        s.TouchpadFinger0Active = r.Bool();
        s.TouchpadFinger0X = (ushort)r.Next(65536);
        s.TouchpadFinger0Y = (ushort)r.Next(65536);
        s.TouchpadFinger0Id = r.Byte();
        s.TouchpadFinger1Active = r.Bool();
        s.TouchpadFinger1X = (ushort)r.Next(65536);
        s.TouchpadFinger1Y = (ushort)r.Next(65536);
        s.TouchpadFinger1Id = r.Byte();
        s.TouchpadPacketCounter = r.Byte();

        s.GyroPitch = (short)r.Next(65536);
        s.GyroYaw = (short)r.Next(65536);
        s.GyroRoll = (short)r.Next(65536);
        s.AccelX = (short)r.Next(65536);
        s.AccelY = (short)r.Next(65536);
        s.AccelZ = (short)r.Next(65536);
        s.SensorTimestamp = r.NextU32();
        s.AccelGX = Imu(r, 16f);
        s.AccelGY = Imu(r, 16f);
        s.AccelGZ = Imu(r, 16f);
        s.GyroDpsX = Imu(r, 2500f);
        s.GyroDpsY = Imu(r, 2500f);
        s.GyroDpsZ = Imu(r, 2500f);

        s.PressureA = r.Byte();
        s.PressureB = r.Byte();
        s.PressureX = r.Byte();
        s.PressureY = r.Byte();
        s.PressureLeftBumper = r.Byte();
        s.PressureRightBumper = r.Byte();
        s.PressureDpadUp = r.Byte();
        s.PressureDpadRight = r.Byte();
        s.PressureDpadDown = r.Byte();
        s.PressureDpadLeft = r.Byte();

        s.BatteryLevel = (byte)(r.Next(8) == 0 ? r.Byte() : r.Next(11));
        s.BatteryCharging = r.Bool();
        s.BatteryFull = r.Next(4) == 0;
        s.MicMuted = r.Bool();
        s.HeadphonesConnected = r.Bool();
        return s;
    }

    static float Imu(Rng r, float range)
    {
        int pick = r.Next(10);
        float u = r.Float01();
        if (pick == 0) return ImuValues[r.Next(ImuValues.Length)];
        return (u * 2f - 1f) * range;
    }
}
