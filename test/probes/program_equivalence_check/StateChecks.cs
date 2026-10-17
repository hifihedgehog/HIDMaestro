using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Programs;

/// <summary>State programs: a state block's codec fields lowered into a
/// program over HMGamepadState and the six slots. Each lowered block runs
/// four ways on random states, its edge values and its NaN slots: the kernel
/// the build generated (for a catalog block), the interpreter, a naive
/// evaluator written from the design note's arithmetic, and the codec, whose
/// bytes the program replaces and must give exactly.</summary>
internal static unsafe class StateChecks
{
    static void Fail(string text) => Checks.Fail(text);

    public static int Run()
    {
        int before = Checks.Failures;
        Console.WriteLine("== State programs: each lowered state block four ways, the codec among them");
        ButtonNames();
        var data = new Random(ProbeCorpusSeed.Value ^ 0x57A7);
        int catalog = 0;
        foreach (var p in ProfileDatabase.LoadEmbedded().All.Where(p => p.StateBlock != null))
        {
            var m = p.GetOrCompileModel();
            if (m.StateProgram is not { } v) { Console.WriteLine($"{p.Id}: the state block stays with the codec: {m.StateCodecReason}"); continue; }
            catalog++;
            var runner = ProgramBinder.BindState(v);
            if (!runner.Generated) Fail($"{p.Id}: its state program binds the interpreter, not the kernel the build generated");
            Compare(p.Id, v, p.StateBlockSpecForCodec(), runner, data, 200_000);
        }
        if (catalog == 0) Fail("no catalog state block lowers into a state program");
        CatalogCodecReports(data);
        foreach (var (name, json, size) in Variants())
        {
            using var doc = JsonDocument.Parse(json);
            var spec = StateLowering.Lower(doc.RootElement, size, out string reason);
            if (spec == null) { Fail($"{name}: not lowered: {reason}"); continue; }
            var v = ProgramForm.Validate(spec);
            var codec = new ExtendedReportSpec { Size = size, Fields = doc.RootElement.Deserialize(HMJsonContext.Default.ListFieldSpec)! };
            Compare(name, v, codec, ProgramBinder.BindState(v, interpretOnly: true), data, 50_000);
        }
        foreach (var (name, json, size) in Unlowered())
        {
            using var doc = JsonDocument.Parse(json);
            if (StateLowering.Lower(doc.RootElement, size, out string reason) is { } spec)
            {
                try { ProgramForm.Validate(spec); Fail($"{name}: lowered, where the codec must keep it"); }
                catch (FormException e) { Console.WriteLine($"{name}: stays with the codec: {e.Reason}"); }
            }
            else Console.WriteLine($"{name}: stays with the codec: {reason}");
        }
        return Checks.Failures - before;
    }

    /// <summary>What a lowered codec report's counter slots hold, as the codec
    /// keeps them: a rolling counter's value before it advances by its
    /// stride, the accumulator over a divisor, or the sensor ticks over one.
    /// Filled before each frame and advanced after it.</summary>
    internal sealed class Feeds
    {
        readonly StateFeed[] _feeds;
        readonly uint[] _counters;
        readonly uint _nominal;
        public readonly VendorBlobCodec.EncoderState Encoder = new();
        public readonly uint[] Slots = new uint[ProgramForm.MaxCounters];

        public Feeds(StateFeed[] feeds, uint nominalTicks)
        {
            _feeds = feeds;
            _nominal = nominalTicks;
            _counters = feeds.Select(f => f.Initial).ToArray();
        }

        public void Fill(in HMGamepadState st)
        {
            ulong acc = 0;
            bool advanced = false;
            for (int i = 0; i < _feeds.Length; i++)
            {
                var f = _feeds[i];
                switch (f.Kind)
                {
                    case StateFeedKind.Rolling8:
                    case StateFeedKind.Rolling32:
                        Slots[i] = _counters[i];
                        break;
                    case StateFeedKind.AccumulateOver:
                        if (!advanced) { acc = Encoder.AdvanceTimestamp(st.SensorTimestamp, _nominal); advanced = true; }
                        Slots[i] = unchecked((uint)(acc / f.Divisor));
                        break;
                    default:
                        Slots[i] = Encoder.SensorTicks(st.SensorTimestamp) / f.Divisor;
                        break;
                }
            }
        }

        public void Advance()
        {
            for (int i = 0; i < _feeds.Length; i++)
            {
                var f = _feeds[i];
                if (f.Kind == StateFeedKind.Rolling8) _counters[i] = unchecked((byte)(_counters[i] + f.Stride));
                else if (f.Kind == StateFeedKind.Rolling32) _counters[i] = unchecked(_counters[i] + (uint)Math.Max(1, f.Stride));
            }
        }
    }

    /// <summary>A profile's state in its full range: every field the codec
    /// reads, a stamp that advances from frame to frame.</summary>
    internal static HMGamepadState FullState(Random r, ref uint stamp)
    {
        var st = RandomState(r);
        stamp += (uint)r.Next(1, 40000);
        if (stamp == 0) stamp = 1;
        st.SensorTimestamp = stamp;
        st.GyroPitch = (short)r.Next(short.MinValue, short.MaxValue + 1); st.GyroYaw = (short)r.Next(short.MinValue, short.MaxValue + 1);
        st.GyroRoll = (short)r.Next(short.MinValue, short.MaxValue + 1); st.AccelX = (short)r.Next(short.MinValue, short.MaxValue + 1);
        st.AccelY = (short)r.Next(short.MinValue, short.MaxValue + 1); st.AccelZ = (short)r.Next(short.MinValue, short.MaxValue + 1);
        st.TouchpadFinger0X = (ushort)r.Next(0, r.Next(4) == 0 ? 65536 : 1920); st.TouchpadFinger0Y = (ushort)r.Next(0, r.Next(4) == 0 ? 65536 : 1080);
        st.TouchpadFinger1X = (ushort)r.Next(0, r.Next(4) == 0 ? 65536 : 1920); st.TouchpadFinger1Y = (ushort)r.Next(0, r.Next(4) == 0 ? 65536 : 1080);
        st.TouchpadFinger0Id = (byte)r.Next(256); st.TouchpadFinger1Id = (byte)r.Next(256);
        st.TouchpadPacketCounter = (byte)r.Next(256);
        st.BatteryLevel = (byte)(r.Next(4) == 0 ? r.Next(256) : r.Next(11));
        st.PressureA = Press(r); st.PressureB = Press(r); st.PressureX = Press(r); st.PressureY = Press(r);
        st.PressureLeftBumper = Press(r); st.PressureRightBumper = Press(r);
        st.PressureDpadUp = Press(r); st.PressureDpadRight = Press(r); st.PressureDpadDown = Press(r); st.PressureDpadLeft = Press(r);
        return st;
    }

    static byte Press(Random r) => (byte)(r.Next(3) == 0 ? r.Next(256) : 0);

    /// <summary>Every codec report of the catalog, each extendedReport and
    /// each port block, that lowers into a state program: the kernel the
    /// probe's build generated, the interpreter and the naive evaluator
    /// against the codec on 20,000 frames in order, its counters and
    /// timestamps carried from frame to frame.</summary>
    static void CatalogCodecReports(Random data)
    {
        var db = ProfileDatabase.LoadEmbedded();
        var asm = typeof(HMContext).Assembly;
        int lowered = 0, kept = 0;
        var reasons = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(res)!;
            using var doc = JsonDocument.Parse(stream, ProfilePrograms.DocumentOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("id", out var idEl) || idEl.GetString() is not string id) continue;
            var p = db.All.FirstOrDefault(x => x.Id == id);
            if (p?.ExtendedReport == null) continue;
            foreach (var (name, fields, size, rid) in StateLowering.CodecReports(doc.RootElement))
            {
                var spec = StateLowering.Lower(fields, size, rid, allowFeeds: true, out var feeds, out string reason);
                ValidProgram? v = null;
                if (spec != null)
                {
                    try { v = ProgramForm.Validate(spec); }
                    catch (FormException e) { reason = e.Message; }
                }
                if (v == null)
                {
                    kept++;
                    string why = System.Text.RegularExpressions.Regex.Replace(reason, @"fields\[\d+\]", "a field");
                    reasons[why] = reasons.TryGetValue(why, out int n) ? n + 1 : 1;
                    continue;
                }
                lowered++;
                var codec = name == "extendedReport" ? p.ExtendedReport : PortLayout.Of(p.ExtendedReport, id, output: false)!.FieldSpec;
                uint nominal = (uint)Math.Clamp((long)p.ExtendedReport.IdleFrameIntervalMs * 3000, 0, uint.MaxValue);
                if (!ProbeKernels.StateByHash.TryGetValue(v.Hash, out nint fn)) { Fail($"{id} {name}: the probe's build generated no state kernel for it"); continue; }
                Frames($"{id} {name}", v, feeds, nominal, codec, (delegate*<in HMGamepadState, float*, byte*, uint*, void>)fn, data, 20_000);
            }
        }
        Console.WriteLine($"catalog codec reports: {lowered} lowered and held to the codec, {kept} left to the codec: {string.Join("; ", reasons.Select(r => $"{r.Value} x {r.Key}"))}");
        if (lowered == 0) Fail("no catalog codec report lowers into a state program");
    }

    static void Frames(string name, ValidProgram v, StateFeed[] feeds, uint nominal, ExtendedReportSpec codec,
        delegate*<in HMGamepadState, float*, byte*, uint*, void> kernel, Random data, int frames)
    {
        int size = v.Spec.Size;
        var interp = new StateProgram(v);
        var programFeeds = new Feeds(feeds, nominal);
        var enc = new VendorBlobCodec.EncoderState();
        var a = new byte[size]; var b = new byte[size]; var c = new byte[size]; var k = new byte[size];
        float* sl = stackalloc float[6];
        uint* slots = stackalloc uint[ProgramForm.MaxCounters];
        uint stamp = (uint)data.Next(1, int.MaxValue);
        int diff = 0; string? first = null;
        for (int n = 0; n < frames; n++)
        {
            var st = FullState(data, ref stamp);
            for (int i = 0; i < 6; i++) sl[i] = Slot(data);
            programFeeds.Fill(in st);
            for (int i = 0; i < ProgramForm.MaxCounters; i++) slots[i] = programFeeds.Slots[i];
            Array.Fill(a, (byte)0xA5); Array.Fill(b, (byte)0x5A);
            fixed (byte* pa = a, pb = b) { kernel(in st, sl, pa, slots); interp.Run(in st, sl, pb, slots); }
            NaiveState.Run(v.Spec, in st, sl, c, programFeeds.Slots);
            programFeeds.Advance();
            VendorBlobCodec.EncodeInput(codec, in st, sl[0], sl[1], sl[2], sl[3], sl[4], sl[5], k, enc);
            if (!a.AsSpan().SequenceEqual(k) || !b.AsSpan().SequenceEqual(k) || !c.AsSpan().SequenceEqual(k))
            {
                diff++;
                first ??= $"frame {n}: generated {Convert.ToHexString(a)} interpreted {Convert.ToHexString(b)} naive {Convert.ToHexString(c)} codec {Convert.ToHexString(k)}";
            }
        }
        if (diff == 0) Console.WriteLine($"{name}: generated, interpreted, naive and the codec agree on {frames} frames");
        else Fail($"{name}: {diff} of {frames} frames differ, first {first}");
    }

    /// <summary>The lowering's button names are HMButton's, as Enum.TryParse
    /// reads them: every name of the enum, and nothing else.</summary>
    static void ButtonNames()
    {
        var names = Enum.GetNames<HMButton>();
        var wrong = names.Where(n => !StateLowering.ButtonNames.TryGetValue(n, out uint v) || v != (uint)Enum.Parse<HMButton>(n))
            .Concat(StateLowering.ButtonNames.Keys.Where(k => !Enum.TryParse<HMButton>(k, true, out _))).ToList();
        if (wrong.Count == 0) Console.WriteLine($"the lowering's {StateLowering.ButtonNames.Count} button names are HMButton's {names.Length}");
        else Fail($"the lowering's button names differ from HMButton at {string.Join(", ", wrong)}");
    }

    static readonly float[] Specials =
    {
        float.NaN, float.PositiveInfinity, float.NegativeInfinity, -0f, 0f, 1f, 0.5f, -1f, 2f, 1e-45f, 1e30f, -1e30f,
        0.5f + 1e-7f, 0.5f - 1e-7f, 1f / 4095, 2047.5f / 4095, 0.5f / 255, 0.25f, 0.75f,
    };

    static float Slot(Random r)
    {
        int k = r.Next(8);
        if (k == 0) return Specials[r.Next(Specials.Length)];
        // Values on and beside the steps of a 12-bit and an 8-bit field,
        // where ties fall.
        if (k == 1) { int span = r.Next(2) == 0 ? 4095 : 255; return (float)((r.Next(span + 1) + 0.5 * r.Next(2)) / span) + (r.Next(3) - 1) * 1e-7f; }
        return (float)r.NextDouble();
    }

    static float Motion(Random r)
    {
        int k = r.Next(8);
        if (k == 0) return Specials[r.Next(Specials.Length)];
        // Values on and beside a half count of a 32767 / 8 and a 16.43 scale.
        if (k == 1) return (float)((r.Next(-70000, 70000) + 0.5) * 8.0 / 32767.0);
        return (float)((r.NextDouble() * 2 - 1) * (r.Next(2) == 0 ? 8 : 4000));
    }

    static HMGamepadState RandomState(Random r) => new()
    {
        Buttons = (HMButton)(uint)r.NextInt64(0, 1L << 32),
        Hat = (HMHat)(r.Next(4) == 0 ? r.Next(256) : r.Next(9)),
        AccelGX = Motion(r), AccelGY = Motion(r), AccelGZ = Motion(r),
        GyroDpsX = Motion(r), GyroDpsY = Motion(r), GyroDpsZ = Motion(r),
        TouchpadFinger0Active = r.Next(2) == 0, TouchpadFinger1Active = r.Next(2) == 0,
        BatteryCharging = r.Next(2) == 0, BatteryFull = r.Next(2) == 0, MicMuted = r.Next(2) == 0, HeadphonesConnected = r.Next(2) == 0,
    };

    static void Compare(string name, ValidProgram v, ExtendedReportSpec codec, StateRunner runner, Random data, int inputs)
    {
        int size = v.Spec.Size;
        var interp = new StateProgram(v);
        var a = new byte[size]; var b = new byte[size]; var c = new byte[size]; var k = new byte[size];
        var enc = new VendorBlobCodec.EncoderState();
        float* sl = stackalloc float[6];
        int diff = 0; string? first = null;
        for (int n = 0; n < inputs; n++)
        {
            var st = RandomState(data);
            for (int i = 0; i < 6; i++) sl[i] = Slot(data);
            if (n == 0) { st = new HMGamepadState(); sl[0] = sl[1] = sl[2] = sl[3] = 0.5f; sl[4] = sl[5] = 0f; }
            Array.Fill(a, (byte)0xA5); Array.Fill(b, (byte)0x5A);
            fixed (byte* pa = a, pb = b) { runner.Run(in st, sl, pa); interp.Run(in st, sl, pb, null); }
            NaiveState.Run(v.Spec, in st, sl, c);
            VendorBlobCodec.EncodeInput(codec, in st, sl[0], sl[1], sl[2], sl[3], sl[4], sl[5], k, enc);
            if (!a.AsSpan().SequenceEqual(k) || !b.AsSpan().SequenceEqual(k) || !c.AsSpan().SequenceEqual(k))
            {
                diff++;
                first ??= $"slots {string.Join(" ", Enumerable.Range(0, 6).Select(i => sl[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}, "
                          + $"buttons 0x{(uint)st.Buttons:X8}, hat {(byte)st.Hat}: {(runner.Generated ? "generated" : "bound")} {Convert.ToHexString(a)} "
                          + $"interpreted {Convert.ToHexString(b)} naive {Convert.ToHexString(c)} codec {Convert.ToHexString(k)}";
            }
        }
        if (diff == 0) Console.WriteLine($"{name}: {(runner.Generated ? "generated" : "interpreted")}, interpreted, naive and the codec agree on {inputs} states");
        else Fail($"{name}: {diff} of {inputs} states differ, first {first}");
    }

    /// <summary>State blocks that lower: each type and key the lowering
    /// covers, on the codec's own reading of each.</summary>
    static IEnumerable<(string name, string json, int size)> Variants()
    {
        yield return ("uint8 axes, triggers and constants", """
            [ {"byte":0,"type":"uint8","initial":165},
              {"byte":1,"type":"uint8-axis","semantic":"leftStickX"},
              {"byte":2,"type":"uint8-axis","semantic":"leftStickY","invert":true,"center":120},
              {"byte":3,"type":"uint8-axis","semantic":"rightStickX","min":10,"max":200},
              {"byte":4,"type":"uint8-axis","semantic":"rightStickY","min":-40,"max":300},
              {"byte":5,"type":"uint8-trigger","semantic":"leftTrigger"},
              {"byte":6,"type":"uint8-trigger","semantic":"rightTrigger","invert":true,"min":5,"max":250},
              {"byte":7,"type":"uint16-trigger","semantic":"leftTrigger","max":32767},
              {"byte":9,"type":"uint16-trigger","semantic":"rightTrigger","min":100,"max":1023,"invert":true},
              {"byte":11,"type":"int16-axis","semantic":"rightStickY"} ]
            """, 13);
        yield return ("buttons over bit ranges, flags and numbered bits", """
            [ {"byte":0,"type":"button-mask","bits":"0-63","buttons":["A","b","X","y","LeftBumper","RightBumper","Back","Start","LeftStick","RightStick","Guide","Touchpad","Share","RightPaddle","LeftPaddle","Misc1","RightPaddle2","LeftPaddle2","RightTouchpad","LeftStickTouch","RightStickTouch","LeftGripTouch","RightGripTouch","LeftTriggerClick","RightTriggerClick","#31","#30","_","DPAD_UP","DPAD_DOWN","DPAD_LEFT","DPAD_RIGHT","LT_DIGITAL","RT_DIGITAL","LEFTPAD_TOUCH","RIGHTPAD_TOUCH","Cross","None","circle"]},
              {"byte":8,"type":"button-mask","bits":"2-5","buttons":["DPAD_RIGHT","Square","Triangle","#0","Guide","Start"]},
              {"byte":9,"type":"button-mask","buttons":["A","B","X","Y","LeftBumper","RightBumper","Back","Start","Guide","Share"]} ]
            """, 10);
        yield return ("scaled motion with repeats and ratios", """
            [ {"byte":0,"type":"int16-le","semantic":"accelGX","scale":[13371,936]},
              {"byte":2,"type":"int16-le","semantic":"accelGY","scale":4096,"negate":true,"repeat":[3,12]},
              {"byte":4,"type":"int16-le","semantic":"accelGZ","scale":[32767,8],"repeat":[2,12]},
              {"byte":6,"type":"int16-le","semantic":"gyroDpsX","scale":[1,3]},
              {"byte":8,"type":"int16-le","semantic":"gyroDpsY","scale":0.0625,"negate":true},
              {"byte":10,"type":"int16-le","semantic":"gyroDpsZ","scale":[-818.5,7]} ]
            """, 38);
        yield return ("fields past the block's end", """
            [ {"byte":4,"type":"int16-axis","semantic":"leftStickX"},
              {"byte":4,"type":"button-mask","bits":"0-15","buttons":["A","B","X","Y","LeftBumper","RightBumper","Back","Start","LeftStick","RightStick"]},
              {"byte":0,"type":"int16-le","semantic":"accelGX","scale":1000,"repeat":[4,2]},
              {"type":"uint8-axis","semantic":"leftStickX"} ]
            """, 5);
    }

    /// <summary>One state program per op kind, its op last.</summary>
    public static IEnumerable<(string name, ProgramSpec program)> OpSamples()
    {
        ProgramSpec P(Op op) => new("state-sample", 8, 0, Array.Empty<int>(), new[] { op }, Gamepad: true);
        yield return ("gpbuttons", P(new GpButtonsOp(new[] { 0, 3, 31, 12 })));
        yield return ("gpflags", P(new GpFlagsOp(new[] { GpFlag.DpadUp, GpFlag.RightTriggerOn }, new[] { 0, 9 })));
        yield return ("gplinear", P(new GpLinearOp(GpFloat.LeftStickY, -100, 4095, true, 0.5, GpStore.Word, 2)));
        yield return ("gpscaled", P(new GpScaledOp(GpFloat.GyroDpsZ, 16.4, 1, true, 4, 2, 2)));
        yield return ("gpraw", P(new GpRawOp(GpRaw.SensorTimestamp, 0, 4)));
        yield return ("gppressure", P(new GpPressureOp(GpPressure.DpadLeft, 3)));
        yield return ("gptable", P(new GpTableOp(GpByte.Hat, new byte[256], 3, 4, 0, 4, 7, false)));
        yield return ("gpfinger", P(new GpFingerOp(1, 2)));
        yield return ("gppad", P(new GpPadOp(1, true, true, -100, 100, true, 0)));
        yield return ("gpselect", P(new GpSelectOp(GpFlag.Finger1Active, 32767, 0, 6)));
        yield return ("gpten", P(new GpTenBitOp(GpRaw.AccelZ, 113, 8192, 512, true, 6)));
        yield return ("gpcrc", P(new GpCrc32Op(new byte[] { 0xA1 }, 0, 3, 4)));
    }

    /// <summary>State programs built in code, each breaking one rule of the
    /// form, with the path validation must name.</summary>
    public static IEnumerable<(string rule, ProgramSpec program, string path)> Negatives()
    {
        ProgramSpec P(params Op[] ops) => new("state-negative", 8, 0, Array.Empty<int>(), ops, Gamepad: true);
        yield return ("a state program of 257 bytes", new ProgramSpec("state-negative", 257, 0, Array.Empty<int>(), new Op[] { new GpButtonsOp(new[] { 0, 0 }) }, Gamepad: true), "$.size");
        yield return ("a state program that reads a state block", new ProgramSpec("state-negative", 8, 8, Array.Empty<int>(), new Op[] { new GpButtonsOp(new[] { 0, 0 }) }, Gamepad: true), "$.stateSize");
        yield return ("a state program that copies state bytes", P(new CopyOp(0, 0, 1)), "$.ops[0]");
        yield return ("a report program that reads HMGamepadState", new ProgramSpec("negative", 8, 8, new[] { 8 }, new Op[] { new GpLinearOp(GpFloat.LeftStickX, 0, 255, false, 0.5, GpStore.Byte, 0) }), "$.ops[0]");
        yield return ("button moves that are not pairs", P(new GpButtonsOp(new[] { 0 })), "$.ops[0].moves");
        yield return ("HMButton bit 32", P(new GpButtonsOp(new[] { 32, 0 })), "$.ops[0].moves[0]");
        yield return ("a button to a bit past the block", P(new GpButtonsOp(new[] { 0, 64 })), "$.ops[0].moves[1]");
        yield return ("a flag with no bit", P(new GpFlagsOp(new[] { GpFlag.DpadUp }, Array.Empty<int>())), "$.ops[0].bits");
        yield return ("a flag the form does not define", P(new GpFlagsOp(new[] { (GpFlag)99 }, new[] { 0 })), "$.ops[0].flags[0]");
        yield return ("a flag to a bit past the block", P(new GpFlagsOp(new[] { GpFlag.DpadUp }, new[] { 64 })), "$.ops[0].bits[0]");
        yield return ("a linear value of a motion source", P(new GpLinearOp(GpFloat.AccelGX, 0, 255, false, 0.5, GpStore.Byte, 0)), "$.ops[0].src");
        yield return ("a store the form does not define", P(new GpLinearOp(GpFloat.LeftStickX, 0, 255, false, 0.5, (GpStore)9, 0)), "$.ops[0].store");
        yield return ("a rest value of NaN", P(new GpLinearOp(GpFloat.LeftStickX, 0, 255, false, double.NaN, GpStore.Byte, 0)), "$.ops[0].rest");
        yield return ("a scaled value of a slot", P(new GpScaledOp(GpFloat.LeftStickX, 1, 1, false, 0, 1, 0)), "$.ops[0].src");
        yield return ("a scaled value over 0", P(new GpScaledOp(GpFloat.AccelGX, 1, 0, false, 0, 1, 0)), "$.ops[0].scale");
        yield return ("a scaled value written no times", P(new GpScaledOp(GpFloat.AccelGX, 1, 1, false, 0, 0, 0)), "$.ops[0].repeat");
        yield return ("scaled writes past the block", P(new GpScaledOp(GpFloat.AccelGX, 1, 1, false, 4, 3, 2)), "$.ops[0].repeat");
        yield return ("a raw value the form does not define", P(new GpRawOp((GpRaw)99, 0, 2)), "$.ops[0].src");
        yield return ("a raw stamp of two bytes", P(new GpRawOp(GpRaw.SensorTimestamp, 0, 2)), "$.ops[0].bytes");
        yield return ("a pressure the form does not define", P(new GpPressureOp((GpPressure)99, 0)), "$.ops[0].button");
        yield return ("a table of a value the form does not define", P(new GpTableOp((GpByte)9, new byte[256], -1, -1, 0, 0, 7, true)), "$.ops[0].src");
        yield return ("a table of 255 entries", P(new GpTableOp(GpByte.Hat, new byte[255], -1, -1, 0, 0, 7, true)), "$.ops[0].table");
        yield return ("a full value past a byte", P(new GpTableOp(GpByte.BatteryLevel, new byte[256], 256, -1, 0, 0, 7, true)), "$.ops[0].full");
        yield return ("table bits past a byte", P(new GpTableOp(GpByte.Hat, new byte[256], -1, -1, 0, 4, 8, false)), "$.ops[0].bits");
        yield return ("a third finger", P(new GpFingerOp(2, 0)), "$.ops[0].finger");
        yield return ("a third pad", P(new GpPadOp(2, false, false, 0, 0, false, 0)), "$.ops[0].pad");
        yield return ("a selection by a flag the form does not define", P(new GpSelectOp((GpFlag)99, 1, 0, 0)), "$.ops[0].flag");
        yield return ("a ten-bit value of the stamp", P(new GpTenBitOp(GpRaw.SensorTimestamp, 1, 1, 512, false, 0)), "$.ops[0].src");
        yield return ("a ten-bit value of resolution 0", P(new GpTenBitOp(GpRaw.AccelX, 0, 1, 512, false, 0)), "$.ops[0].resolution");
        yield return ("a CRC before another op", P(new GpCrc32Op(Array.Empty<byte>(), 0, 1, 4), new GpRawOp(GpRaw.AccelX, 2, 2)), "$.ops[0]");
        yield return ("a CRC over its own bytes", P(new GpCrc32Op(Array.Empty<byte>(), 0, 5, 4)), "$.ops[0].scope");
    }

    /// <summary>State blocks the lowering leaves to the codec.</summary>
    static IEnumerable<(string name, string json, int size)> Unlowered()
    {
        yield return ("a type the program form does not say", """[ {"byte":0,"type":"stick12-pair","semantic":"leftStick"} ]""", 4);
        yield return ("a rolling counter in a state block", """[ {"byte":0,"type":"uint8-rolling","semantic":"seq"} ]""", 4);
        yield return ("two rolling fields on one counter", """[ {"byte":0,"type":"uint8-rolling","semantic":"seq"}, {"byte":1,"type":"uint8-rolling","semantic":"seq"} ]""", 4);
        yield return ("a field after the CRC", """[ {"bytes":"0-3","type":"crc32-le","scope":{"from":4,"to":5}}, {"byte":4,"type":"uint8","initial":1} ]""", 6);
        yield return ("a resolved d-pad", """[ {"byte":0,"type":"button-mask","dpad":"resolved","buttons":["DPAD_UP"]} ]""", 4);
        yield return ("two fields on one byte", """[ {"byte":0,"type":"uint8","initial":1}, {"byte":0,"type":"button-mask","buttons":["A"]} ]""", 4);
        yield return ("a raw motion value in repeats", """[ {"byte":0,"type":"int16-le","semantic":"gyroPitch","repeat":[2,2]} ]""", 4);
        yield return ("a button name the codec reads as a number", """[ {"byte":0,"type":"button-mask","buttons":["3"]} ]""", 4);
        yield return ("an axis on a trigger's slot", """[ {"byte":0,"type":"uint8-axis","semantic":"leftTrigger"} ]""", 4);
    }
}

/// <summary>The naive evaluator of a state program, from the design note's
/// one arithmetic (mechanism 1): a NaN slot takes the field's rest, the value
/// clamped to [0, 1], inverted if asked, then min + the product rounded to
/// nearest with ties to even. A scaled value is the product rounded the same
/// way and clamped to int16, a NaN 0. A trigger is on past half of its first
/// 8-bit step, and a hat direction holds for its three hat values.</summary>
internal static unsafe class NaiveState
{
    public static void Run(ProgramSpec p, in HMGamepadState st, float* sl, byte[] d, uint[]? counters = null)
    {
        Array.Clear(d);
        foreach (var op in p.Ops) if (op is ConstOp c) c.Bytes.CopyTo(d, c.At);
        foreach (var op in p.Ops)
        {
            switch (op)
            {
                case GpButtonsOp g:
                    for (int i = 0; i < g.Moves.Length; i += 2)
                        if ((((uint)st.Buttons >> g.Moves[i]) & 1) != 0) d[g.Moves[i + 1] / 8] |= (byte)(1 << (g.Moves[i + 1] % 8));
                    break;
                case GpFlagsOp g:
                    for (int i = 0; i < g.Flags.Length; i++)
                        if (Flag(g.Flags[i], in st, sl)) d[g.Bits[i] / 8] |= (byte)(1 << (g.Bits[i] % 8));
                    break;
                case GpCrc32Op g:
                {
                    // Over the bytes as every other op left them.
                    uint crc = Crc(g.Prefix.Concat(d.Skip(g.From).Take(Math.Max(0, g.To - g.From + 1))));
                    for (int k = 0; k < 4; k++) d[g.Dst + k] = (byte)(crc >> (8 * k));
                    break;
                }
                case GpLinearOp g:
                {
                    double v = sl[(int)g.Src];
                    if (double.IsNaN(v)) v = g.Rest;
                    v = Math.Min(Math.Max(v, 0), 1);
                    if (g.Invert) v = 1 - v;
                    long w = g.Min + (long)Math.Round(v * ((long)g.Max - g.Min), MidpointRounding.ToEven);
                    if (g.Store == GpStore.ByteClamped) d[g.Dst] = (byte)Math.Min(Math.Max(w, 0), 255);
                    else if (g.Store == GpStore.Byte) d[g.Dst] = (byte)(w & 0xFF);
                    else { d[g.Dst] = (byte)(w & 0xFF); d[g.Dst + 1] = (byte)((w >> 8) & 0xFF); }
                    break;
                }
                case GpScaledOp g:
                {
                    float f = g.Src switch
                    {
                        GpFloat.AccelGX => st.AccelGX, GpFloat.AccelGY => st.AccelGY, GpFloat.AccelGZ => st.AccelGZ,
                        GpFloat.GyroDpsX => st.GyroDpsX, GpFloat.GyroDpsY => st.GyroDpsY, _ => st.GyroDpsZ,
                    };
                    if (g.Negate) f = -f;
                    long w = 0;
                    if (!float.IsNaN(f))
                    {
                        double x = Math.Round((double)f * g.Num / g.Den, MidpointRounding.ToEven);
                        w = (long)Math.Min(Math.Max(x, -32768), 32767);
                    }
                    for (int k = 0; k < g.Count; k++) { d[g.Dst + k * g.Stride] = (byte)(w & 0xFF); d[g.Dst + k * g.Stride + 1] = (byte)((w >> 8) & 0xFF); }
                    break;
                }
                case GpRawOp g:
                {
                    long v = Raw(g.Src, in st);
                    for (int k = 0; k < g.Bytes; k++) d[g.Dst + k] = (byte)((v >> (8 * k)) & 0xFF);
                    break;
                }
                case GpPressureOp g:
                {
                    (byte pr, bool down) = g.Button switch
                    {
                        GpPressure.A => (st.PressureA, ((uint)st.Buttons & 1) != 0),
                        GpPressure.B => (st.PressureB, ((uint)st.Buttons & 2) != 0),
                        GpPressure.X => (st.PressureX, ((uint)st.Buttons & 4) != 0),
                        GpPressure.Y => (st.PressureY, ((uint)st.Buttons & 8) != 0),
                        GpPressure.LeftBumper => (st.PressureLeftBumper, ((uint)st.Buttons & 16) != 0),
                        GpPressure.RightBumper => (st.PressureRightBumper, ((uint)st.Buttons & 32) != 0),
                        GpPressure.DpadUp => (st.PressureDpadUp, Flag(GpFlag.DpadUp, in st, sl)),
                        GpPressure.DpadRight => (st.PressureDpadRight, Flag(GpFlag.DpadRight, in st, sl)),
                        GpPressure.DpadDown => (st.PressureDpadDown, Flag(GpFlag.DpadDown, in st, sl)),
                        _ => (st.PressureDpadLeft, Flag(GpFlag.DpadLeft, in st, sl)),
                    };
                    d[g.Dst] = pr != 0 ? pr : down ? (byte)255 : (byte)0;
                    break;
                }
                case GpTableOp g:
                {
                    int v;
                    if (g.Full >= 0 && st.BatteryFull) v = g.Full;
                    else if (g.Charging >= 0 && st.BatteryCharging) v = g.Charging;
                    else v = g.Table[g.Src == GpByte.Hat ? (int)(byte)st.Hat : st.BatteryLevel];
                    if (g.Whole) d[g.Dst] = (byte)v;
                    else for (int bit = g.BitLo; bit <= g.BitHi; bit++)
                        if (((v >> (bit - g.BitLo)) & 1) != 0) d[g.Dst] |= (byte)(1 << bit);
                    break;
                }
                case GpFingerOp g:
                {
                    bool active = g.Finger == 1 ? st.TouchpadFinger1Active : st.TouchpadFinger0Active;
                    int x = g.Finger == 1 ? st.TouchpadFinger1X : st.TouchpadFinger0X;
                    int y = g.Finger == 1 ? st.TouchpadFinger1Y : st.TouchpadFinger0Y;
                    int id = g.Finger == 1 ? st.TouchpadFinger1Id : st.TouchpadFinger0Id;
                    d[g.Dst] = (byte)(id % 128 + (active ? 0 : 128));
                    d[g.Dst + 1] = (byte)(x % 256);
                    d[g.Dst + 2] = (byte)(x / 256 % 16 + y % 16 * 16);
                    d[g.Dst + 3] = (byte)(y / 16 % 256);
                    break;
                }
                case GpPadOp g:
                {
                    bool down = g.Pad == 0 ? st.TouchpadFinger0Active : st.TouchpadFinger1Active;
                    long v;
                    if (down)
                    {
                        int raw = g.Pad == 0 ? (g.IsY ? st.TouchpadFinger0Y : st.TouchpadFinger0X) : (g.IsY ? st.TouchpadFinger1Y : st.TouchpadFinger1X);
                        // The codec's float arithmetic, step by step.
                        float unit = Math.Min(Math.Max(raw / (g.IsY ? 1079f : 1919f), 0f), 1f) - 0.5f;
                        if (g.Invert) unit = -unit;
                        v = Math.Min(Math.Max((long)MathF.Round(unit * 2f * 32767f, MidpointRounding.ToEven), -32767), 32767);
                    }
                    else if (g.OrStick)
                    {
                        double x = sl[(g.Pad == 0 ? 0 : 2) + (g.IsY ? 1 : 0)];
                        if (double.IsNaN(x)) x = 0.5;
                        x = Math.Min(Math.Max(x, 0), 1);
                        if (g.Invert) x = 1 - x;
                        v = g.Min + (long)Math.Round(x * ((long)g.Max - g.Min), MidpointRounding.ToEven);
                    }
                    else v = 0;
                    d[g.Dst] = (byte)(v & 0xFF); d[g.Dst + 1] = (byte)((v >> 8) & 0xFF);
                    break;
                }
                case GpSelectOp g:
                {
                    long v = Flag(g.Flag, in st, sl) ? g.WhenSet : g.WhenClear;
                    d[g.Dst] = (byte)(v & 0xFF); d[g.Dst + 1] = (byte)((v >> 8) & 0xFF);
                    break;
                }
                case GpTenBitOp g:
                {
                    long delta = (long)Math.Round((double)Raw(g.Src, in st) * g.Resolution / g.SourceResolution, MidpointRounding.AwayFromZero);
                    long v = Math.Min(Math.Max(g.Invert ? g.Center - delta : g.Center + delta, 0), 1023);
                    d[g.Dst] = (byte)(v / 256); d[g.Dst + 1] = (byte)(v % 256);
                    break;
                }
                case CounterOp k:
                {
                    ulong v = ((ulong)counters![k.Slot] * k.Scale + k.Add) % 4294967296UL;
                    for (int i = 0; i < k.Width; i++) d[k.BigEndian ? k.Dst + k.Width - 1 - i : k.Dst + i] = (byte)(v >> (8 * i));
                    break;
                }
            }
        }
    }

    static long Raw(GpRaw r, in HMGamepadState st) => r switch
    {
        GpRaw.GyroPitch => st.GyroPitch, GpRaw.GyroYaw => st.GyroYaw, GpRaw.GyroRoll => st.GyroRoll,
        GpRaw.AccelX => st.AccelX, GpRaw.AccelY => st.AccelY, GpRaw.AccelZ => st.AccelZ,
        GpRaw.SensorTimestamp => st.SensorTimestamp,
        _ => st.TouchpadPacketCounter,
    };

    /// <summary>The CRC-32 as the polynomial division it is, a bit at a time,
    /// with no table.</summary>
    static uint Crc(IEnumerable<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    static bool Flag(GpFlag f, in HMGamepadState st, float* sl)
    {
        int h = (byte)st.Hat;
        return f switch
        {
            GpFlag.LeftTriggerOn => sl[4] * 255.0 > 0.5,
            GpFlag.RightTriggerOn => sl[5] * 255.0 > 0.5,
            GpFlag.DpadUp => h is 1 or 2 or 8,
            GpFlag.DpadDown => h is 4 or 5 or 6,
            GpFlag.DpadLeft => h is 6 or 7 or 8,
            GpFlag.DpadRight => h is 2 or 3 or 4,
            GpFlag.Finger0Active => st.TouchpadFinger0Active,
            GpFlag.Finger1Active => st.TouchpadFinger1Active,
            GpFlag.BatteryCharging => st.BatteryCharging,
            GpFlag.BatteryFull => st.BatteryFull,
            GpFlag.MicMuted => st.MicMuted,
            GpFlag.HeadphonesConnected => st.HeadphonesConnected,
            _ => st.BatteryCharging || st.BatteryFull,
        };
    }
}
