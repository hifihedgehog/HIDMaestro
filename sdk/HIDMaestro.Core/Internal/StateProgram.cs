using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HIDMaestro.Internal.Programs;

namespace HIDMaestro.Internal;

/// <summary>The interpreter's form of a state program (design note,
/// mechanism 2): a state block built from HMGamepadState and the six slots,
/// for a block no generated kernel matches. Each op is compiled once into
/// the values it reads, and each write goes through WireArith, as the
/// generated kernel's and the codec's do.</summary>
internal sealed unsafe class StateProgram
{
    private readonly struct Group { public readonly int Dst, Shift; public readonly uint Mask; public Group(int d, int s, uint m) { Dst = d; Shift = s; Mask = m; } }
    private readonly struct FlagW { public readonly GpFlag Flag; public readonly int Dst; public readonly byte Bit; public FlagW(GpFlag f, int d, byte b) { Flag = f; Dst = d; Bit = b; } }

    public ValidProgram Program { get; }
    private readonly byte[] _template;
    private readonly Group[] _groups;
    private readonly FlagW[] _flags;
    private readonly GpLinearOp[] _linear;
    private readonly GpScaledOp[] _scaled;
    // Every other op, in list order, and the CRC, which runs last.
    private readonly Op[] _others;
    private readonly GpCrc32Op? _crc;
    private readonly uint _crcStart;

    public StateProgram(ValidProgram v)
    {
        if (!v.Spec.Gamepad) throw new ArgumentException("not a state program", nameof(v));
        Program = v;
        _template = v.Template;
        var groups = new Dictionary<(int, int), uint>();
        var flags = new List<FlagW>();
        var linear = new List<GpLinearOp>();
        var scaled = new List<GpScaledOp>();
        var others = new List<Op>();
        foreach (var op in v.Unconditional)
        {
            switch (op)
            {
                case GpButtonsOp g:
                    // One masked shift per report byte and distance.
                    for (int i = 0; i < g.Moves.Length; i += 2)
                    {
                        int k = g.Moves[i], r = g.Moves[i + 1];
                        var key = (r >> 3, k - (r & 7));
                        groups[key] = (groups.TryGetValue(key, out var m) ? m : 0) | (1u << (r & 7));
                    }
                    break;
                case GpFlagsOp g:
                    for (int i = 0; i < g.Flags.Length; i++) flags.Add(new FlagW(g.Flags[i], g.Bits[i] >> 3, (byte)(1 << (g.Bits[i] & 7))));
                    break;
                case GpLinearOp g: linear.Add(g); break;
                case GpScaledOp g: scaled.Add(g); break;
                case GpCrc32Op g: _crc = g; _crcStart = Crc32.Update(0xFFFFFFFFu, g.Prefix); break;
                case GpRawOp or GpPressureOp or GpTableOp or GpFingerOp or GpPadOp or GpSelectOp or GpTenBitOp or CounterOp: others.Add(op); break;
                default: throw new InvalidOperationException("no state stage for " + op.GetType().Name);
            }
        }
        _groups = new Group[groups.Count];
        int n = 0;
        foreach (var ((dst, shift), mask) in groups) _groups[n++] = new Group(dst, shift, mask);
        _flags = flags.ToArray();
        _linear = linear.ToArray();
        _scaled = scaled.ToArray();
        _others = others.ToArray();
    }

    static long Raw(GpRaw r, in HMGamepadState st) => r switch
    {
        GpRaw.GyroPitch => st.GyroPitch, GpRaw.GyroYaw => st.GyroYaw, GpRaw.GyroRoll => st.GyroRoll,
        GpRaw.AccelX => st.AccelX, GpRaw.AccelY => st.AccelY, GpRaw.AccelZ => st.AccelZ,
        GpRaw.SensorTimestamp => st.SensorTimestamp,
        _ => st.TouchpadPacketCounter,
    };

    static (byte Pressure, bool Down) Pressure(GpPressure p, in HMGamepadState st, float* sl) => p switch
    {
        GpPressure.A => (st.PressureA, (st.Buttons & HMButton.A) != 0),
        GpPressure.B => (st.PressureB, (st.Buttons & HMButton.B) != 0),
        GpPressure.X => (st.PressureX, (st.Buttons & HMButton.X) != 0),
        GpPressure.Y => (st.PressureY, (st.Buttons & HMButton.Y) != 0),
        GpPressure.LeftBumper => (st.PressureLeftBumper, (st.Buttons & HMButton.LeftBumper) != 0),
        GpPressure.RightBumper => (st.PressureRightBumper, (st.Buttons & HMButton.RightBumper) != 0),
        GpPressure.DpadUp => (st.PressureDpadUp, Holds(GpFlag.DpadUp, in st, sl)),
        GpPressure.DpadRight => (st.PressureDpadRight, Holds(GpFlag.DpadRight, in st, sl)),
        GpPressure.DpadDown => (st.PressureDpadDown, Holds(GpFlag.DpadDown, in st, sl)),
        _ => (st.PressureDpadLeft, Holds(GpFlag.DpadLeft, in st, sl)),
    };

    void Other(Op op, in HMGamepadState st, float* sl, byte* d, uint* c)
    {
        switch (op)
        {
            case GpRawOp g:
            {
                ulong v = (ulong)Raw(g.Src, in st);
                for (int i = 0; i < g.Bytes; i++) d[g.Dst + i] = (byte)(v >> (8 * i));
                break;
            }
            case GpPressureOp g:
            {
                var (p, down) = Pressure(g.Button, in st, sl);
                d[g.Dst] = p != 0 ? p : down ? (byte)255 : (byte)0;
                break;
            }
            case GpTableOp g:
            {
                int v = g.Full >= 0 && st.BatteryFull ? g.Full
                      : g.Charging >= 0 && st.BatteryCharging ? g.Charging
                      : g.Table[g.Src == GpByte.Hat ? (byte)st.Hat : st.BatteryLevel];
                if (g.Whole) d[g.Dst] = (byte)v;
                else d[g.Dst] |= (byte)((v << g.BitLo) & (((1 << (g.BitHi - g.BitLo + 1)) - 1) << g.BitLo));
                break;
            }
            case GpFingerOp g:
            {
                bool active = g.Finger == 1 ? st.TouchpadFinger1Active : st.TouchpadFinger0Active;
                int x = g.Finger == 1 ? st.TouchpadFinger1X : st.TouchpadFinger0X;
                int y = g.Finger == 1 ? st.TouchpadFinger1Y : st.TouchpadFinger0Y;
                int id = g.Finger == 1 ? st.TouchpadFinger1Id : st.TouchpadFinger0Id;
                d[g.Dst] = (byte)((id & 0x7F) | (active ? 0x00 : 0x80));
                d[g.Dst + 1] = (byte)(x & 0xFF);
                d[g.Dst + 2] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4));
                d[g.Dst + 3] = (byte)((y >> 4) & 0xFF);
                break;
            }
            case GpPadOp g:
            {
                bool down = g.Pad == 0 ? st.TouchpadFinger0Active : st.TouchpadFinger1Active;
                short v;
                if (down)
                {
                    int raw = g.Pad == 0 ? (g.IsY ? st.TouchpadFinger0Y : st.TouchpadFinger0X) : (g.IsY ? st.TouchpadFinger1Y : st.TouchpadFinger1X);
                    float span = g.IsY ? 1079f : 1919f;
                    float unit = Math.Clamp(raw / span, 0f, 1f) - 0.5f;
                    if (g.Invert) unit = -unit;
                    v = (short)Math.Clamp((int)Math.Round(unit * 2f * 32767f), -32767, 32767);
                }
                else if (g.OrStick) v = (short)WireArith.Linear(sl[(g.Pad == 0 ? 0 : 2) + (g.IsY ? 1 : 0)], g.Min, g.Max, g.Invert, 0.5);
                else v = 0;
                d[g.Dst] = (byte)v; d[g.Dst + 1] = (byte)(v >> 8);
                break;
            }
            case GpSelectOp g:
            {
                int v = Holds(g.Flag, in st, sl) ? g.WhenSet : g.WhenClear;
                d[g.Dst] = (byte)v; d[g.Dst + 1] = (byte)(v >> 8);
                break;
            }
            case GpTenBitOp g:
            {
                int delta = (int)Math.Round((double)Raw(g.Src, in st) * g.Resolution / g.SourceResolution, MidpointRounding.AwayFromZero);
                int v = Math.Clamp(g.Invert ? g.Center - delta : g.Center + delta, 0, 1023);
                d[g.Dst] = (byte)(v >> 8); d[g.Dst + 1] = (byte)v;
                break;
            }
            case CounterOp k:
            {
                uint v = unchecked(c[k.Slot] * k.Scale + k.Add);
                for (int i = 0; i < k.Width; i++) d[k.Dst + i] = (byte)(v >> (8 * (k.BigEndian ? k.Width - 1 - i : i)));
                break;
            }
        }
    }

    /// <summary>A direction holds for its hat values, N 1 to NW 8, and for
    /// none past 8, as the codec reads HMGamepadState.Hat.</summary>
    static bool Holds(GpFlag f, in HMGamepadState st, float* sl) => f switch
    {
        GpFlag.LeftTriggerOn => sl[4] * 255.0 > 0.5,
        GpFlag.RightTriggerOn => sl[5] * 255.0 > 0.5,
        GpFlag.DpadUp => st.Hat is HMHat.North or HMHat.NorthEast or HMHat.NorthWest,
        GpFlag.DpadDown => st.Hat is HMHat.South or HMHat.SouthEast or HMHat.SouthWest,
        GpFlag.DpadLeft => st.Hat is HMHat.West or HMHat.NorthWest or HMHat.SouthWest,
        GpFlag.DpadRight => st.Hat is HMHat.East or HMHat.NorthEast or HMHat.SouthEast,
        GpFlag.Finger0Active => st.TouchpadFinger0Active,
        GpFlag.Finger1Active => st.TouchpadFinger1Active,
        GpFlag.BatteryCharging => st.BatteryCharging,
        GpFlag.BatteryFull => st.BatteryFull,
        GpFlag.MicMuted => st.MicMuted,
        GpFlag.HeadphonesConnected => st.HeadphonesConnected,
        GpFlag.CableConnected => st.BatteryCharging || st.BatteryFull,
        _ => false,
    };

    static float Motion(GpFloat src, in HMGamepadState st) => src switch
    {
        GpFloat.AccelGX => st.AccelGX, GpFloat.AccelGY => st.AccelGY, GpFloat.AccelGZ => st.AccelGZ,
        GpFloat.GyroDpsX => st.GyroDpsX, GpFloat.GyroDpsY => st.GyroDpsY, GpFloat.GyroDpsZ => st.GyroDpsZ,
        _ => 0f,
    };

    /// <summary>Builds the block into <paramref name="d"/>, the six slots in
    /// <paramref name="sl"/> in the order of <see cref="GpFloat"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Run(in HMGamepadState st, float* sl, byte* d, uint* c)
    {
        fixed (byte* t = _template) Unsafe.CopyBlockUnaligned(d, t, (uint)_template.Length);
        uint b = (uint)st.Buttons;
        foreach (var g in _groups)
            d[g.Dst] |= (byte)((g.Shift >= 0 ? b >> g.Shift : b << -g.Shift) & g.Mask);
        foreach (var f in _flags)
            if (Holds(f.Flag, in st, sl)) d[f.Dst] |= f.Bit;
        foreach (var l in _linear)
        {
            int v = WireArith.Linear(sl[(int)l.Src], l.Min, l.Max, l.Invert, l.Rest);
            switch (l.Store)
            {
                case GpStore.ByteClamped: d[l.Dst] = (byte)Math.Clamp(v, 0, 255); break;
                case GpStore.Byte: d[l.Dst] = (byte)v; break;
                default: d[l.Dst] = (byte)v; d[l.Dst + 1] = (byte)(v >> 8); break;
            }
        }
        foreach (var s in _scaled)
        {
            float g = Motion(s.Src, in st);
            short v = WireArith.Scaled(s.Negate ? -g : g, s.Num, s.Den);
            for (int k = 0, at = s.Dst; k < s.Count; k++, at += s.Stride) { d[at] = (byte)v; d[at + 1] = (byte)(v >> 8); }
        }
        foreach (var op in _others) Other(op, in st, sl, d, c);
        if (_crc is { } crcOp)
        {
            uint crc = Crc32.Update(_crcStart, d + crcOp.From, Math.Max(0, crcOp.To - crcOp.From + 1)) ^ 0xFFFFFFFFu;
            d[crcOp.Dst] = (byte)crc; d[crcOp.Dst + 1] = (byte)(crc >> 8); d[crcOp.Dst + 2] = (byte)(crc >> 16); d[crcOp.Dst + 3] = (byte)(crc >> 24);
        }
    }
}
