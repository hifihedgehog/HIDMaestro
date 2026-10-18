// roundtrip: every raw integer a consumer can read from a device of each
// width the catalog uses, and of every width from 1 to 16 bits, signed and
// unsigned, normalized the three ways a consumer normalizes one (double
// division, float division, float times the reciprocal), sent through the
// SDK's own code path for the site and read back. Each must return as the
// raw value it started as.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static class RoundTrip
{
    /// <summary>Raw values, of every conversion, that do not return.</summary>
    public sealed record Result(string Key, long Values, long[] Misses, int FirstMiss);

    public static int Run()
    {
        Inventory.Build();
        var sites = new SortedDictionary<string, Site>(StringComparer.Ordinal);
        foreach (var (k, s) in Inventory.SiteByKey)
            if (!k.Contains("DIGITAL") && !k.Contains("ZL/ZR")) sites[k] = s;
        foreach (var s in Extra()) sites.TryAdd(s.Key, s);

        using var rig = new Rig();
        int fail = 0;
        Console.WriteLine($"roundtrip: {sites.Count} conversions, three normalizations each, through the SDK's code for the site");
        Console.WriteLine("  conversion | raw values | values that do not return: double division, float division, float reciprocal | first");
        foreach (var s in sites.Values)
        {
            var tree = rig.TreeOf(s);
            var r = Check(s, tree);
            bool ok = r.Misses.All(m => m == 0);
            if (!ok) fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {s.Key} | {r.Values} | {string.Join(", ", r.Misses)} | {(r.FirstMiss == int.MinValue ? "-" : r.FirstMiss.ToString())}");
        }
        foreach (var imu in ImuScale.All)
        {
            var (values, misses, first) = rig.ImuRoundTrip(imu);
            bool ok = misses.All(m => m == 0);
            if (!ok) fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] imu {imu.Key} | {values} | {string.Join(", ", misses)} | {(first == int.MinValue ? "-" : first.ToString())}");
        }
        fail += Centered();
        Console.WriteLine(fail == 0 ? "roundtrip: PASS, every raw value returns" : $"roundtrip: FAIL, {fail} conversion(s) lose raw values");
        return fail;
    }

    /// <summary>The centered descriptor axes, through the Luna profiles' own
    /// report builders and their axisCenters: 0..255 around 0x7F and
    /// 0..65535 around 0x7FFF. Each raw value normalized four ways: float
    /// division and the float reciprocal of the whole span and each half by
    /// its own span, through the builder, and double division through the
    /// SDK's WireArith.Centered with the field's own slopes, since a double
    /// reaches the builder only as the float HMGamepadState.Axes holds,
    /// which is float division's value (design note, decision (a) and
    /// mechanism 1).</summary>
    static int Centered()
    {
        int fail = 0;
        Console.WriteLine("roundtrip: centered axes, through the profile's own report builder");
        if (!NewArith.CenteredPresent)
        {
            Console.WriteLine("  [FAIL] this SDK has no WireArith.Centered: no centered axes");
            return 1;
        }
        foreach (var (id, center) in new[] { ("amazon-luna-usb", 0x7F), ("amazon-luna-ble", 0x7FFF) })
        {
            var b = ProfileDatabase.LoadEmbedded().All.First(p => p.Id == id).GetOrBuildReportBuilder();
            var f = b.AxisFields[HMAxis.X];
            int min = f.LogicalMin, max = f.LogicalMax, span = max - min;
            var report = new byte[b.InputReportByteSize];
            var axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 0 };
            int off = f.BitOffset + (b.InputReportId != 0 ? 8 : 0);
            int Tree(float v)
            {
                axes[HMAxis.X] = v;
                b.BuildReportInto(report, axes);
                int raw = 0;
                for (int i = 0; i < f.BitSize; i++)
                    if (((report[(off + i) >> 3] >> ((off + i) & 7)) & 1) != 0) raw |= 1 << i;
                return raw;
            }
            var misses = new List<int>[4];
            for (int k = 0; k < 4; k++) misses[k] = new List<int>();
            bool oneLow = true;
            for (int r = min; r <= max; r++)
            {
                int k = r - min;
                float halves = r <= center
                    ? (float)(0.5 * (r - min) / (center - min))
                    : (float)(0.5 + 0.5 * (r - center) / (max - center));
                float[] n = { 0, k / (float)span, k * (1f / span), halves };
                for (int j = 0; j < 4; j++)
                {
                    int got = j == 0 ? NewArith.Centered((double)k / span, min, center, max) : Tree(n[j]);
                    if (got == r) continue;
                    misses[j].Add(r);
                    if (got != r - 1) oneLow = false;
                }
            }
            // At 16 bits a float normalization lands a hair under the rounding
            // point for values just above the center, whose slope is 65536 where
            // the consumer's is 65535: float division misses the 64 even values
            // 32768 to 32894, the reciprocal the 65 even values 32768 to 32896.
            var even = (int count) => Enumerable.Range(0, count).Select(i => 32768 + 2 * i).ToList();
            bool wide = span > 255;
            bool ok = misses[0].Count == 0 && misses[3].Count == 0 && oneLow
                && misses[1].SequenceEqual(wide ? even(64) : new List<int>())
                && misses[2].SequenceEqual(wide ? even(65) : new List<int>());
            if (!ok) fail++;
            string List(List<int> m) => m.Count == 0 ? "0" : $"{m.Count} ({m[0]}..{m[^1]})";
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {id} X {min}..{max} around {center} | {span + 1} | double {List(misses[0])}, float {List(misses[1])}, reciprocal {List(misses[2])}, per half {List(misses[3])}, every miss one count low: {oneLow}");
            // 0.5 and PadForge's centered 0.5000076 (32768 / 65535) rest on the
            // center. Rounding the wire value itself, the form decision (a)
            // gives an uncentered field, sends the second to the count above
            // at 16 bits.
            float padForge = 32768f / 65535f;
            int atHalf = Tree(0.5f), atPad = Tree(padForge);
            int plain = min + (int)Math.Round((double)padForge * span);
            bool rest = atHalf == center && atPad == center && (!wide || plain != center);
            if (!rest) fail++;
            Console.WriteLine($"  [{(rest ? "PASS" : "FAIL")}] {id} X at 0.5 gives {atHalf} and at {padForge:R} gives {atPad}, center {center}; rounding the wire value itself gives {plain}");
        }
        return fail;
    }

    /// <summary>Every unsigned width from 1 to 16 bits and its two signed
    /// forms, the codec's types, the XUSB slice and the two packers.</summary>
    static IEnumerable<Site> Extra()
    {
        for (int bits = 1; bits <= 16; bits++)
        {
            yield return Sites.Descriptor(0, (1 << bits) - 1);
            if (bits >= 2)
            {
                yield return Sites.Descriptor(-(1 << (bits - 1)), (1 << (bits - 1)) - 1);
                yield return Sites.Descriptor(-((1 << (bits - 1)) - 1), (1 << (bits - 1)) - 1);
            }
        }
        yield return Sites.U8Axis(128, false);
        yield return Sites.U8Axis(128, true);
        yield return Sites.U8Trigger();
        yield return Sites.Stick12();
        yield return Sites.I16Axis(false);
        yield return Sites.I16Axis(true);
        yield return Sites.U16Trigger();
        yield return Sites.GipStickX();
        yield return Sites.GipStickY();
        yield return Sites.GipTrigger();
        yield return Sites.SwitchStick(false);
        yield return Sites.SwitchStick(true);
        yield return Sites.Switch2Stick(false);
        yield return Sites.Switch2Stick(true);
    }

    static Result Check(Site s, Func<float, int> tree)
    {
        var misses = new long[3];
        int first = int.MinValue;
        long values = 0;
        for (long r = s.RawLo; r <= s.RawHi; r++)
        {
            var (k, range) = s.Normal((int)r);
            float n1 = (float)((double)k / range);
            float n2 = k / (float)range;
            float n3 = k * (1f / range);
            values++;
            if (tree(n1) != r) { misses[0]++; if (first == int.MinValue) first = (int)r; }
            if (tree(n2) != r) { misses[1]++; if (first == int.MinValue) first = (int)r; }
            if (tree(n3) != r) { misses[2]++; if (first == int.MinValue) first = (int)r; }
        }
        return new Result(s.Key, values, misses, first);
    }

    /// <summary>The SDK's code paths: a report builder on a one-axis
    /// descriptor, the codec on a one-field report, an offline Xbox 360
    /// controller's XUSB slice, and the two packers.</summary>
    sealed class Rig : IDisposable
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

        readonly IntPtr _view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
        readonly byte[] _section = new byte[SharedMemoryIO.SHARED_INPUT_SIZE];
        HMController? _xbox;
        HMAxis _lx, _ly, _lt;
        const int XboxIndex = 7900;

        public Func<float, int> TreeOf(Site s)
        {
            string k = s.Key;
            if (k.StartsWith("hid-descriptor", StringComparison.Ordinal)) return Descriptor(s.RawLo, s.RawHi);
            if (k.StartsWith("codec uint8-axis", StringComparison.Ordinal))
                return Codec(new FieldSpec { Byte = 1, Type = "uint8-axis", Semantic = "leftStickX", Center = k.Contains("center 128") ? null : int.Parse(k.Split(' ')[3]), Invert = k.EndsWith("invert") ? true : null },
                    (b, _) => b[1], stick: true);
            if (k == "codec uint8-trigger")
                return Codec(new FieldSpec { Byte = 1, Type = "uint8-trigger", Semantic = "leftTrigger" }, (b, _) => b[1], stick: false);
            if (k == "codec stick12-pair")
                return Codec(new FieldSpec { Byte = 1, Type = "stick12-pair", Semantic = "leftStick" }, (b, _) => b[1] | ((b[2] & 0x0F) << 8), stick: true);
            if (k.StartsWith("codec int16-axis", StringComparison.Ordinal))
                return Codec(Spec("int16-axis", k.Contains("Y") ? "leftStickY" : "leftStickX", invert: k.Contains("Y")),
                    (b, _) => (short)(b[1] | (b[2] << 8)), stick: true, y: k.Contains("Y"));
            if (k == "codec uint16-trigger")
                return Codec(Spec("uint16-trigger", "leftTrigger", max: 32767), (b, _) => b[1] | (b[2] << 8), stick: false);
            if (k == "xusb slice stick X") return v => Companion.StickX(Gip(v, 0));
            if (k == "xusb slice stick Y") return v => Companion.StickY(Gip(v, 1));
            if (k == "xusb slice trigger") return v => Companion.Trigger(Gip(v, 2));
            if (k.StartsWith("switch-pro stick", StringComparison.Ordinal))
            {
                bool y = k.EndsWith("Y");
                // The profile's stateBlock through the codec, as
                // HMController encodes it (v2.0.0, where 9bd6279 had
                // SwitchProPacker).
                var body = new byte[SwitchBody.Size];
                var st = new HMGamepadState();
                return v =>
                {
                    SwitchBody.Build(in st, y ? 0.5 : v, y ? v : 0.5, 0.5, 0.5, body);
                    return y ? (body[6] >> 4) | (body[7] << 4) : body[5] | ((body[6] & 0x0F) << 8);
                };
            }
            if (k.StartsWith("switch2 composite stick", StringComparison.Ordinal))
            {
                bool y = k.EndsWith("Y");
                // The composite's stateBlock through the codec, as
                // HMController encodes it (v2.0.0, where 9bd6279 had
                // Switch2ProPacker): sticks at 4 to 11.
                var body = new byte[Switch2Body.Size];
                var st = new HMGamepadState();
                return v =>
                {
                    Switch2Body.Build(in st, y ? 0.5f : v, y ? v : 0.5f, body);
                    int at = 4 + (y ? 2 : 0);
                    return body[at] | (body[at + 1] << 8);
                };
            }
            throw new InvalidOperationException($"No code path for {k}.");
        }

        /// <summary>A one-axis descriptor of X with the given logical range,
        /// in a report field just wide enough, padded to a byte.</summary>
        static Func<float, int> Descriptor(int min, int max)
        {
            int bits = 1;
            if (min < 0) while (min < -(1L << (bits - 1)) || max > (1L << (bits - 1)) - 1) bits++;
            else while (max > (1L << bits) - 1) bits++;
            var d = new List<byte> { 0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0x09, 0x30 };
            Item(d, 0x14, min);
            Item(d, 0x24, max);
            d.AddRange(new byte[] { 0x75, (byte)bits, 0x95, 0x01, 0x81, 0x02 });
            int pad = (8 - bits % 8) % 8;
            if (pad != 0) d.AddRange(new byte[] { 0x75, (byte)pad, 0x95, 0x01, 0x81, 0x03 });
            d.Add(0xC0);
            var b = HidReportBuilder.Parse(d.ToArray());
            if (!b.AxisFields.TryGetValue(HMAxis.X, out var f))
                throw new InvalidOperationException($"The builder found no X axis in the descriptor for {min}..{max}.");
            var report = new byte[b.InputReportByteSize];
            var axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 0 };
            int off = f.BitOffset + (b.InputReportId != 0 ? 8 : 0);
            return v =>
            {
                axes[HMAxis.X] = v;
                b.BuildReportInto(report, axes);
                long raw = 0;
                for (int i = 0; i < f.BitSize; i++)
                    if (((report[(off + i) >> 3] >> ((off + i) & 7)) & 1) != 0) raw |= 1L << i;
                if (f.LogicalMin < 0 && (raw & (1L << (f.BitSize - 1))) != 0) raw -= 1L << f.BitSize;
                return (int)raw;
            };

            // A short item with a signed value in the fewest bytes.
            static void Item(List<byte> d, byte tag, int value)
            {
                if (value >= sbyte.MinValue && value <= sbyte.MaxValue) { d.Add((byte)(tag | 1)); d.Add((byte)value); }
                else if (value >= short.MinValue && value <= short.MaxValue) { d.Add((byte)(tag | 2)); d.Add((byte)value); d.Add((byte)(value >> 8)); }
                else { d.Add((byte)(tag | 3)); d.AddRange(BitConverter.GetBytes(value)); }
            }
        }

        /// <summary>A field at byte 1. The Y axis's negation and the 16-bit
        /// trigger's 32767 are profile data from v2.0.0 (invert, max), set
        /// by name so the probe also builds on a tree from before them,
        /// where the codec held both as code.</summary>
        static FieldSpec Spec(string type, string semantic, bool invert = false, int? max = null)
        {
            var f = new FieldSpec { Byte = 1, Type = type, Semantic = semantic };
            if (invert) typeof(FieldSpec).GetProperty("Invert")!.SetValue(f, true);
            if (max != null) typeof(FieldSpec).GetProperty("Max")?.SetValue(f, max);
            return f;
        }

        /// <summary>The codec on a report with one field at byte 1.</summary>
        static Func<float, int> Codec(FieldSpec field, Func<byte[], int, int> read, bool stick, bool y = false)
        {
            var spec = new ExtendedReportSpec { ReportId = "0x01", Size = 8, Fields = new List<FieldSpec> { field } };
            var buf = new byte[spec.Size];
            var enc = new VendorBlobCodec.EncoderState();
            var st = new HMGamepadState();
            return v =>
            {
                if (stick) VendorBlobCodec.EncodeInput(spec, in st, y ? 0.5f : v, y ? v : 0.5f, 0.5f, 0.5f, 0f, 0f, buf, enc);
                else VendorBlobCodec.EncodeInput(spec, in st, 0.5f, 0.5f, 0.5f, 0.5f, v, 0f, buf, enc);
                return read(buf, 0);
            };
        }

        /// <summary>The XUSB slice's value <paramref name="which"/> (0 left
        /// thumb X, 1 left thumb Y, 2 left trigger) after an offline
        /// xbox-360-wired controller takes a state with that axis at
        /// <paramref name="v"/>.</summary>
        int Gip(float v, int which)
        {
            var c = Xbox();
            var axes = new Dictionary<HMAxis, float> { [_lx] = 0.5f, [_ly] = 0.5f, [_lt] = 0f };
            axes[which == 0 ? _lx : which == 1 ? _ly : _lt] = v;
            c.SubmitState(new HMGamepadState { Axes = axes });
            Marshal.Copy(_view, _section, 0, _section.Length);
            int at = SharedMemoryIO.GIP_DATA_OFFSET + (which == 0 ? 0 : which == 1 ? 2 : 8);
            return _section[at] | (_section[at + 1] << 8);
        }

        /// <summary>The SDK's own controller, built offline on a buffer of
        /// this probe's as device_oracle_check's case 1 builds it: no section,
        /// event or driver is created.</summary>
        HMController Xbox()
        {
            if (_xbox != null) return _xbox;
            var p = new HMProfile(ProfileDatabase.LoadEmbedded().All.First(x => x.Id == "xbox-360-wired"));
            _lx = p.Sticks[0].XAxis;
            _ly = p.Sticks[0].YAxis;
            _lt = HMController.ResolveCanonicalAxis(p.Inner.AxisMap, "lefttrigger", HMAxis.Z);
            Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, _view, SharedMemoryIO.SHARED_INPUT_SIZE);
            Cache("s_inputViews")[XboxIndex] = _view;
            Cache("s_outputViews")[XboxIndex] = IntPtr.Zero;
            var ctor = typeof(HMController).GetConstructor(Instance, new[]
            {
                typeof(HMContext), typeof(int), typeof(HMProfile), typeof(string), typeof(UsbipBackendHandle), typeof(string),
            }) ?? throw new MissingMethodException("HMController's private constructor changed.");
            var context = (HMContext)RuntimeHelpers.GetUninitializedObject(typeof(HMContext));
            _xbox = (HMController)ctor.Invoke(new object?[] { context, XboxIndex, p, null, null, null });
            return _xbox;
        }

        static Dictionary<int, IntPtr> Cache(string field) =>
            (Dictionary<int, IntPtr>)typeof(SharedMemoryIO).GetField(field, Static)!.GetValue(null)!;

        /// <summary>Every int16 count through a packer's motion field: the
        /// count normalized to g or degrees per second three ways, packed,
        /// read back.</summary>
        public (long Values, long[] Misses, int First) ImuRoundTrip(ImuScale s)
        {
            bool pro = s.Key.StartsWith("switch-pro", StringComparison.Ordinal);
            bool gyro = s.Key.Contains("gyro");
            var body = new byte[Math.Max(SwitchBody.Size, Switch2Body.Size)];
            var misses = new long[3];
            int first = int.MinValue;
            int Pack(float value)
            {
                var st = new HMGamepadState();
                // Fields the packers carry with no sign change: the Switch
                // Pro's wire Z from the SDK's Y, the Switch 2 Pro's wire X
                // from the SDK's X.
                if (pro && gyro) st.GyroDpsY = value;
                else if (pro) st.AccelGY = value;
                else if (gyro) st.GyroDpsX = value;
                else st.AccelGX = value;
                int at;
                if (pro) { SwitchBody.Build(in st, 0.5, 0.5, 0.5, 0.5, body); at = 12 + (gyro ? 10 : 4); }
                else { Switch2Body.Build(in st, 0.5f, 0.5f, body); at = 12 + (gyro ? 6 : 0); }
                return (short)(body[at] | (body[at + 1] << 8));
            }
            double d = s.Scale;
            float fd = (float)d;
            for (int r = short.MinValue; r <= short.MaxValue; r++)
            {
                if (Pack((float)(r / d)) != r) { misses[0]++; if (first == int.MinValue) first = r; }
                if (Pack(r / fd) != r) { misses[1]++; if (first == int.MinValue) first = r; }
                if (Pack(r * (1f / fd)) != r) { misses[2]++; if (first == int.MinValue) first = r; }
            }
            return (65536, misses, first);
        }

        public void Dispose()
        {
            if (_xbox != null)
            {
                Cache("s_inputViews").Remove(XboxIndex);
                Cache("s_outputViews").Remove(XboxIndex);
            }
            Marshal.FreeHGlobal(_view);
        }
    }
}

/// <summary>The Switch 2 Pro composite's state block, its profile's
/// stateBlock encoded by the codec from the six slot values as floats, as
/// HMController encodes it.</summary>
/// <summary>The Switch Pro profile's state block through the codec, as
/// HMController encodes it (v2.0.0, where 9bd6279 had SwitchProPacker): the
/// sticks 12-bit at 5 to 10, the motion from 12.</summary>
internal static class SwitchBody
{
    static readonly ExtendedReportSpec s_spec = ProfileDatabase.LoadEmbedded().All
        .First(p => p.Id == "switch-pro").StateBlockSpecForCodec();
    static readonly VendorBlobCodec.EncoderState s_enc = new();

    public static int Size => s_spec.Size;

    /// <summary>The body from the four stick slots, as HMController hands
    /// them to the codec (floats).</summary>
    public static void Build(in HMGamepadState state, double lx, double ly, double rx, double ry, byte[] dst) =>
        VendorBlobCodec.EncodeInput(s_spec, in state, (float)lx, (float)ly, (float)rx, (float)ry, 0f, 0f, dst, s_enc);
}

internal static class Switch2Body
{
    static readonly ExtendedReportSpec s_spec = ProfileDatabase.LoadEmbedded().All
        .First(p => p.Id == "switch2-pro-controller-composite").StateBlockSpecForCodec();
    static readonly VendorBlobCodec.EncoderState s_enc = new();

    public static int Size => s_spec.Size;

    public static void Build(in HMGamepadState state, float lx, float ly, byte[] dst) =>
        VendorBlobCodec.EncodeInput(s_spec, in state, lx, ly, 0.5f, 0.5f, 0f, 0f, dst, s_enc);

    /// <summary>The body from all six slots, as HMController hands them to
    /// the codec (floats).</summary>
    public static void Build(in HMGamepadState state, double lx, double ly, double rx, double ry, double lt, double rt, byte[] dst) =>
        VendorBlobCodec.EncodeInput(s_spec, in state, (float)lx, (float)ly, (float)rx, (float)ry, (float)lt, (float)rt, dst, s_enc);
}
