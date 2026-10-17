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

    public StateProgram(ValidProgram v)
    {
        if (!v.Spec.Gamepad) throw new ArgumentException("not a state program", nameof(v));
        Program = v;
        _template = v.Template;
        var groups = new Dictionary<(int, int), uint>();
        var flags = new List<FlagW>();
        var linear = new List<GpLinearOp>();
        var scaled = new List<GpScaledOp>();
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
                default: throw new InvalidOperationException("no state stage for " + op.GetType().Name);
            }
        }
        _groups = new Group[groups.Count];
        int n = 0;
        foreach (var ((dst, shift), mask) in groups) _groups[n++] = new Group(dst, shift, mask);
        _flags = flags.ToArray();
        _linear = linear.ToArray();
        _scaled = scaled.ToArray();
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
    }
}
