// Case 2: the two packers of 9bd6279. SwitchProPacker.BuildBody writes the
// Switch Pro's 48-byte body the UMDF2 driver streams, Switch2ProPacker's the
// Switch 2 Pro composite's 24-byte body, on the states case 1 uses and on
// direct sweeps of their double inputs. Their output decodes on every input
// they read: DecodeRumbleAmplitude on all 2^17 values of the three bytes it
// reads (all of byte 1, bit 7 of byte 2, all of byte 3), DecodeRumble on each
// 10-bit amplitude field at 0, 1, 512 and 1023 and on a million random
// reports.
//
// The conversion replaces both with a state block and codec fields (design
// note, mechanisms 1 and 2), which must reproduce these bodies and decodes
// byte for byte except the listed arithmetic changes. From v2.0.0 each
// body is its profile's stateBlock through the codec, as HMController
// encodes it, the composite's and switch-pro's, and each rumble decode the
// profile's protocol.decoders.rumble through the kernel the build step
// generated, or the interpreter when HM_ORACLE_INTERPRET is set (Switch2 and
// SwitchPro below).
using System;
using System.Collections.Generic;
using HIDMaestro;
using System.Linq;
using HIDMaestro.Internal;

internal sealed class Case2Packers : IOracleCase
{
    public string Name => "case2-packers";

    const ulong CaseConstant = 2;
    const int RandomRumbleReports = 1_000_000;

    static readonly HMAxis[] SixAxes = { HMAxis.X, HMAxis.Y, HMAxis.Z, HMAxis.Rx, HMAxis.Ry, HMAxis.Rz };

    public void Record(GoldenCase golden, Options options)
    {
        var scratch = new Dictionary<HMAxis, float>();
        var body = new byte[SwitchPro.BodySize];
        var body2 = new byte[Switch2.BodySize];

        // The bodies on case 1's scripted and random states, each body built
        // as its state comes. The six slot values come from the axes as
        // HMController resolves a profile's simple slots: each clamped to
        // [0, 1], sticks 0.5 and triggers 0 when absent.
        void EachState(Action<HMGamepadState, double[]> use)
        {
            foreach (var s in States.Scripted(SixAxes, scratch)) use(s, Slots(in s));
            var r = new Rng(options.Seed, CaseConstant);
            for (int i = 0; i < States.RandomCount; i++)
            {
                var s = States.Random(r, SixAxes, scratch);
                use(s, Slots(in s));
            }
        }
        golden.Stream("switch-pro/body/states");
        golden.Explainer = new BodyExplainer(switchPro: true);
        EachState((s, v) =>
        {
            golden.SetInput(in s, v);
            SwitchPro.BuildBody(in s, v[0], v[1], v[2], v[3], body);
            golden.Out("body", body);
        });
        golden.Stream("switch2-pro/body/states");
        golden.Explainer = new BodyExplainer(switchPro: false);
        EachState((s, v) =>
        {
            golden.SetInput(in s, v);
            Switch2.BuildBody(in s, v[0], v[1], v[2], v[3], v[4], v[5], body2);
            golden.Out("body", body2);
        });

        // Each slot alone, and all together, over the double values a slot
        // can carry: every float of case 1's lists widened, and doubles
        // outside them.
        var sweep = new List<double>();
        foreach (float f in States.AxisValues) sweep.Add(f);
        foreach (float f in States.BoundaryValues) sweep.Add(f);
        sweep.AddRange(new[] { -0.5, 1.5, 1e-300, -1e-300, double.Epsilon, 0.5 + 1e-16, 0.5 - 1e-16,
                               double.NaN, double.PositiveInfinity, double.NegativeInfinity });
        for (int k = 0; k <= 4095; k += 1) sweep.Add(k / 4095.0);
        var neutral = new HMGamepadState();
        golden.Stream("switch-pro/body/slots");
        golden.Explainer = new BodyExplainer(switchPro: true);
        foreach (double v in sweep)
            for (int slot = 0; slot <= 4; slot++)
            {
                var a = SlotsAt(slot, v, 4);
                golden.SetInput(in neutral, a);
                SwitchPro.BuildBody(in neutral, a[0], a[1], a[2], a[3], body);
                golden.Out(slot == 4 ? "all" : $"slot{slot}", body);
            }
        golden.Stream("switch2-pro/body/slots");
        golden.Explainer = new BodyExplainer(switchPro: false);
        foreach (double v in sweep)
            for (int slot = 0; slot <= 6; slot++)
            {
                var a = SlotsAt(slot, v, 6);
                golden.SetInput(in neutral, a);
                Switch2.BuildBody(in neutral, a[0], a[1], a[2], a[3], a[4], a[5], body2);
                golden.Out(slot == 6 ? "all" : $"slot{slot}", body2);
            }

        var rng = new Rng(options.Seed, CaseConstant + 100);

        // DecodeRumbleAmplitude on every value of what it reads, and on the
        // short blocks it refuses.
        golden.Stream("switch-pro/rumble-amplitude/all");
        var block = new byte[4];
        var outAmp = new byte[5];
        for (int b1 = 0; b1 < 256; b1++)
            foreach (byte b2 in new byte[] { 0x00, 0x80 })
                for (int b3 = 0; b3 < 256; b3++)
                {
                    block[0] = 0x00; block[1] = (byte)b1; block[2] = b2; block[3] = (byte)b3;
                    block.CopyTo(outAmp, 0);
                    outAmp[4] = SwitchPro.DecodeRumbleAmplitude(block);
                    golden.Out("amp", outAmp);
                }
        // Byte 0 and the low seven bits of byte 2 are not read: a sample of
        // them must not move the result.
        for (int i = 0; i < 4096; i++)
        {
            rng.Fill(block);
            block.CopyTo(outAmp, 0);
            outAmp[4] = SwitchPro.DecodeRumbleAmplitude(block);
            golden.Out("amp", outAmp);
        }
        for (int len = 0; len < 4; len++)
            golden.Out($"amp-len{len}", new[] { SwitchPro.DecodeRumbleAmplitude(new byte[] { 0xFF, 0xC8, 0xFF, 0x72 }.AsSpan(0, len)) });

        // DecodeRumble: each of the twelve amplitude fields (two actuators,
        // three frames, two fields) alone at 0, 1, 512 and 1023, then a
        // million random reports, then the short reports it refuses.
        golden.Stream("switch2-pro/rumble/fields");
        var data = new byte[Switch2.RumbleDataSize];
        var outR = new byte[Switch2.RumbleDataSize + 2];
        void Decode(ReadOnlySpan<byte> d, string tag)
        {
            Switch2.DecodeRumble(d, out byte left, out byte right);
            d.Slice(0, Math.Min(d.Length, Switch2.RumbleDataSize)).CopyTo(outR);
            outR[Switch2.RumbleDataSize] = left;
            outR[Switch2.RumbleDataSize + 1] = right;
            golden.Out(tag, outR);
        }
        for (int act = 0; act < 2; act++)
            for (int frame = 0; frame < 3; frame++)
                for (int field = 0; field < 2; field++)
                    foreach (int amp in new[] { 0, 1, 512, 1023 })
                    {
                        Array.Clear(data);
                        int o = act * 16 + 1 + frame * 5;
                        ulong v = (ulong)amp << (field == 0 ? 10 : 30);
                        for (int b = 0; b < 5; b++) data[o + b] = (byte)(v >> (8 * b));
                        Decode(data, $"a{act}f{frame}p{field}");
                    }
        golden.Stream("switch2-pro/rumble/random");
        for (int i = 0; i < RandomRumbleReports; i++)
        {
            rng.Fill(data);
            Decode(data, "rumble");
        }
        golden.Stream("switch2-pro/rumble/short");
        var full = new byte[Switch2.RumbleDataSize];
        Array.Fill(full, (byte)0xFF);
        for (int len = 0; len < Switch2.RumbleDataSize; len++)
        {
            Switch2.DecodeRumble(full.AsSpan(0, len), out byte left, out byte right);
            golden.Out($"len{len}", new[] { left, right });
        }
    }

    /// <summary>The six simple slots of a state with the six axes: left X,
    /// left Y, right X, right Y, left and right trigger.</summary>
    static double[] Slots(in HMGamepadState s)
    {
        var a = s.Axes;
        double Get(HMAxis ax, double def) => a != null && a.TryGetValue(ax, out var v) ? Math.Clamp(v, 0f, 1f) : def;
        return new[] { Get(HMAxis.X, 0.5), Get(HMAxis.Y, 0.5), Get(HMAxis.Rx, 0.5), Get(HMAxis.Ry, 0.5), Get(HMAxis.Z, 0.0), Get(HMAxis.Rz, 0.0) };
    }

    /// <summary>Rest values with one slot at <paramref name="v"/>, or the
    /// first <paramref name="count"/> slots, every one the packer takes, at
    /// it when <paramref name="slot"/> is <paramref name="count"/>.</summary>
    static double[] SlotsAt(int slot, double v, int count)
    {
        var a = new[] { 0.5, 0.5, 0.5, 0.5, 0.0, 0.0 };
        if (slot == count) for (int k = 0; k < count; k++) a[k] = v;
        else a[slot] = v;
        return a;
    }

    /// <summary>What replaced SwitchProPacker: switch-pro's stateBlock,
    /// encoded by the codec from the four stick slots HMController passes it
    /// (as floats), and its rumble decoder on a block placed as the left
    /// side of report 0x10. A block shorter than the decoder reads is not
    /// decoded, which the recording's packer reported as level 0, and so
    /// does this.</summary>
    internal static unsafe class SwitchPro
    {
        public const int BodySize = 48;
        static readonly ControllerProfile s_profile =
            ProfileDatabase.LoadEmbedded().All.First(p => p.Id == "switch-pro");
        static readonly ExtendedReportSpec s_spec = s_profile.StateBlockSpecForCodec();
        static readonly VendorBlobCodec.EncoderState s_enc = new();
        static readonly DecoderRunner s_decoder = ProgramBinder.BindDecoders(s_profile.GetOrCompileModel(),
            Environment.GetEnvironmentVariable("HM_ORACLE_INTERPRET") == "1")[0];
        static readonly byte[] s_in = GC.AllocateArray<byte>(10 + 4, pinned: true);
        static readonly byte[] s_out = GC.AllocateArray<byte>(8, pinned: true);

        public static void BuildBody(in HMGamepadState state, double lx, double ly, double rx, double ry, byte[] dst) =>
            VendorBlobCodec.EncodeInput(s_spec, in state, (float)lx, (float)ly, (float)rx, (float)ry, 0f, 0f, dst, s_enc);

        public static byte DecodeRumbleAmplitude(ReadOnlySpan<byte> block)
        {
            if (block.Length < 4) return 0;
            Array.Clear(s_in);
            s_in[0] = 0x10;
            block[..4].CopyTo(s_in.AsSpan(2));
            fixed (byte* sp = s_in)
            fixed (byte* dp = s_out)
                s_decoder.Run(sp, dp);
            return s_out[Array.IndexOf(s_decoder.Form.Semantics, "leftMotor")];
        }
    }

    /// <summary>What replaced Switch2ProPacker: the composite profile's
    /// stateBlock, encoded by the codec from the same six slot values
    /// HMController passes it (as floats), and its rumble decoder. A report
    /// shorter than the decoder reads is not decoded, which the recording's
    /// packer reported as two zero levels, and so does this.</summary>
    static unsafe class Switch2
    {
        public const int BodySize = 24;
        public const int RumbleDataSize = 32;
        static readonly ControllerProfile s_profile =
            ProfileDatabase.LoadEmbedded().All.First(p => p.Id == "switch2-pro-controller-composite");
        static readonly ExtendedReportSpec s_spec = s_profile.StateBlockSpecForCodec();
        static readonly VendorBlobCodec.EncoderState s_enc = new();
        static readonly DecoderRunner s_decoder = ProgramBinder.BindDecoders(s_profile.GetOrCompileModel(),
            Environment.GetEnvironmentVariable("HM_ORACLE_INTERPRET") == "1")[0];
        static readonly byte[] s_in = GC.AllocateArray<byte>(64 + 4, pinned: true);
        static readonly byte[] s_out = GC.AllocateArray<byte>(8, pinned: true);

        public static void BuildBody(in HMGamepadState state, double lx, double ly, double rx, double ry,
                                     double lt, double rt, byte[] dst) =>
            VendorBlobCodec.EncodeInput(s_spec, in state, (float)lx, (float)ly, (float)rx, (float)ry, (float)lt, (float)rt, dst, s_enc);

        /// <summary><paramref name="data"/> is report 0x02 after its ID.</summary>
        public static void DecodeRumble(ReadOnlySpan<byte> data, out byte left, out byte right)
        {
            left = right = 0;
            if (data.Length + 1 < s_decoder.Form.MinLength) return;
            s_in[0] = 0x02;
            data[..RumbleDataSize].CopyTo(s_in.AsSpan(1));
            fixed (byte* sp = s_in)
            fixed (byte* dp = s_out)
                s_decoder.Run(sp, dp);
            left = s_out[0];
            right = s_out[1];
        }
    }
}
