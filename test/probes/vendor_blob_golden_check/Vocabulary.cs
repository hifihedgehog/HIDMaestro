// The codec vocabulary of the data-driven design (design note, mechanism 1),
// held against the code it is to replace and the profiles that use it:
//
//   - the removed ds3-battery type is refused at create, naming its
//     replacement and the field's path
//   - a key whose absence v1 read as one behavior is required: max on
//     uint16-trigger and uint16-pressure, invert on an int16 Y, each
//     refusal naming the field's path and what to add for v1's bytes
//   - an output field refuses min, max and dpad, and invert on any type
//     but uint8-axis, which the decoder would ignore
//   - every catalog profile's device model compiles under the vocabulary's
//     checks
//   - uint8-level-map gives the bytes ds3-battery gave, full ahead of
//     charging, and dualshock-3-full writes a USB pad's 0xEE and 0xEF
//   - field lists in the vocabulary reproduce SwitchProPacker.BuildBody's
//     48 bytes on random states, and the Switch 2 Pro composite's stateBlock
//     is the field list that reproduced Switch2ProPacker.BuildBody's 24
//     bytes until v2.0.0 deleted the packer: "#n" buttons, the resolved
//     d-pad, min and max, invert, and scaled, negated and repeated motion
//   - an int16-le field's center offsets its value on the wire, 32768
//     giving offset binary (the PS Move's motion words, psmove.c
//     psmove_decode_16bit at 97d97e9), on a raw field and on a scaled,
//     repeated one, both out of the encode loop, a word past 0 or 65535
//     clamped as uint10-be clamps, without a center the bytes stay two's
//     complement, and a center outside 0 to 65535, or on an output field,
//     is refused
//   - packed-uint output fields reproduce SwitchProPacker's rumble decode
//     on every input it reads, and the Switch 2 Pro composite's
//     protocol.decoders.rumble on a million random reports, boxed as byte
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Vocabulary
{
    static int s_fail;

    static void Check(string what, bool ok, string detail = "")
    {
        if (!ok) s_fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? ": " + detail : "")}");
    }

    public static int Run()
    {
        Console.WriteLine("vocabulary:");
        var db = ProfileDatabase.LoadEmbedded().All.ToDictionary(p => p.Id, StringComparer.Ordinal);
        void Run(string name, Action check)
        {
            try { check(); }
            catch (Exception e) { Check(name, false, $"{e.GetType().Name}: {e.Message}"); }
        }
        Run("the removed spelling", () => RemovedSpelling(db["dualshock-3-full"]));
        Run("the required keys", () => RequiredKeys(db["steam-controller-2"]));
        Run("the output fields", OutputFields);
        Run("the catalog", () => Catalog(db.Values));
        Run("the level map", () => LevelMap(db["dualshock-3-full"]));
        Run("the Switch Pro body", SwitchProBody);
        Run("the Switch 2 Pro body", Switch2ProBody);
        Run("the Switch Pro rumble", SwitchProRumble);
        Run("the Switch 2 Pro rumble", Switch2ProRumble);
        Run("the Triton timestamp", () => TritonTimestamp(db["steam-controller-2"]));
        Run("the int16 center", Int16Center);
        Run("the Triton timestamp while its IMU is off", () => TritonImuHold(db["steam-controller-2"]));
        Console.WriteLine($"=== vocabulary: {(s_fail == 0 ? "PASS" : $"{s_fail} FAIL")} ===");
        return s_fail;
    }

    /// <summary>A profile that still writes ds3-battery is refused where a
    /// create compiles its device model, with the replacement and the
    /// path.</summary>
    static void RemovedSpelling(ControllerProfile ds3)
    {
        // Edited as JSON, so the check also builds on a tree whose field
        // type has no levels.
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(ds3, HMJsonContext.Default.ControllerProfile))!;
        var fields = node["extendedReport"]!["fields"]!.AsArray();
        int at = fields.Select((x, k) => (x, k)).First(t => (string?)t.x!["semantic"] == "batteryLevel").k;
        var field = fields[at]!.AsObject();
        field["type"] = "ds3-battery";
        foreach (var key in new[] { "levels", "chargingValue", "fullValue" }) field.Remove(key);
        var p = JsonSerializer.Deserialize(node.ToJsonString(), HMJsonContext.Default.ControllerProfile)!;
        string? refused = null;
        try { p.ValidateDeviceModel(); }
        catch (InvalidOperationException e) { refused = e.Message; }
        Check("a profile with the removed ds3-battery type is refused, naming uint8-level-map and the field's path",
            refused != null && refused.Contains("uint8-level-map", StringComparison.Ordinal)
                && refused.Contains($"extendedReport", StringComparison.Ordinal) && refused.Contains($"fields[{at}].type", StringComparison.Ordinal),
            refused ?? "accepted");
    }

    /// <summary>steam-controller-2 with one key at a time taken out of a
    /// field that needs it: each is refused where a create compiles the
    /// profile, naming the field's path and the value v1 implied.</summary>
    static void RequiredKeys(ControllerProfile triton)
    {
        foreach (var (semantic, key, implied) in new[]
        {
            ("leftTrigger", "max", "32767"), ("rightPadPressure", "max", "32767"),
            ("leftStickY", "invert", "true"), ("rightPadY", "invert", "true"),
        })
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(triton, HMJsonContext.Default.ControllerProfile))!;
            var fields = node["extendedReport"]!["fields"]!.AsArray();
            int at = fields.Select((x, k) => (x, k)).First(t => (string?)t.x!["semantic"] == semantic).k;
            fields[at]!.AsObject().Remove(key);
            var p = JsonSerializer.Deserialize(node.ToJsonString(), HMJsonContext.Default.ControllerProfile)!;
            string? refused = null;
            try { p.ValidateDeviceModel(); }
            catch (InvalidOperationException e) { refused = e.Message; }
            Check($"steam-controller-2's {semantic} without {key} is refused, naming fields[{at}] and \"{key}\": {implied}",
                refused != null && refused.Contains($"extendedReport.fields[{at}]", StringComparison.Ordinal)
                    && refused.Contains($"\"{key}\": {implied}", StringComparison.Ordinal),
                refused ?? "accepted");
        }
    }

    /// <summary>Output fields: a parameter the decoder ignores is refused,
    /// and uint8-axis's invert, which it reads, is accepted.</summary>
    static void OutputFields()
    {
        static string? Refusal(string field)
        {
            var spec = Spec(8, field);
            try { VendorBlobProgram.Check(spec, spec.Size, "output", output: true, portBlock: false); return null; }
            catch (InvalidOperationException e) { return e.Message; }
        }
        foreach (var (field, what) in new[]
        {
            ("{\"byte\":1,\"type\":\"uint8-trigger\",\"semantic\":\"leftTrigger\",\"max\":200}", "max"),
            ("{\"byte\":1,\"type\":\"uint8-trigger\",\"semantic\":\"leftTrigger\",\"min\":10}", "min"),
            ("{\"byte\":1,\"type\":\"uint8-trigger\",\"semantic\":\"leftTrigger\",\"invert\":true}", "invert"),
            ("{\"byte\":1,\"type\":\"hat-octant\",\"semantic\":\"hat\",\"dpad\":\"resolved\"}", "dpad"),
        })
        {
            string? r = Refusal(field);
            Check($"an output field with {what} that its decoder ignores is refused, naming it", r != null && r.Contains($".{what}", StringComparison.Ordinal), r ?? "accepted");
        }
        string? axis = Refusal("{\"byte\":1,\"type\":\"uint8-axis\",\"semantic\":\"leftStickX\",\"invert\":true}");
        Check("an output uint8-axis with invert, which the decoder reads, is accepted", axis == null, axis ?? "");
    }

    static void Catalog(IEnumerable<ControllerProfile> profiles)
    {
        var refused = new List<string>();
        int n = 0;
        foreach (var p in profiles)
        {
            n++;
            try { p.ValidateDeviceModel(); }
            catch (InvalidOperationException e) { refused.Add($"{p.Id}: {e.Message}"); }
        }
        Check($"all {n} catalog profiles compile their device model", refused.Count == 0, string.Join(" | ", refused.Take(3)));
    }

    /// <summary>The DualShock 3 battery byte. uint8-level-map with the table
    /// ds3-battery held gives that type's bytes, 0xEE charging and 0xEF
    /// full, full first. The catalog's dualshock-3-full maps every level to
    /// 0xEE, what a pad on USB sends while it charges (hid-sony.c:973-987 at
    /// d24e8ac), and keeps 0xEE and 0xEF.</summary>
    static void LevelMap(ControllerProfile ds3)
    {
        byte[] Bytes(ControllerProfile p, out byte charging, out byte full, out byte both)
        {
            var spec = p.ExtendedReport!;
            int b = spec.Fields.First(f => f.Semantic == "batteryLevel").Byte!.Value;
            var buf = new byte[spec.Size];
            byte Encode(byte level, bool c, bool f)
            {
                var s = new HMGamepadState { BatteryLevel = level, BatteryCharging = c, BatteryFull = f };
                VendorBlobCodec.EncodeInput(spec, in s, 0.5f, 0.5f, 0.5f, 0.5f, 0, 0, buf, new VendorBlobCodec.EncoderState());
                return buf[b];
            }
            charging = Encode(5, true, false);
            full = Encode(5, false, true);
            both = Encode(5, true, true);
            return Enumerable.Range(0, 12).Select(l => Encode((byte)l, false, false)).ToArray();
        }
        byte[] table = { 1, 2, 2, 2, 3, 3, 4, 4, 4, 5, 5 };
        // Edited as JSON: dualshock-3-full with the ds3-battery table.
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(ds3, HMJsonContext.Default.ControllerProfile))!;
        var field = node["extendedReport"]!["fields"]!.AsArray().First(x => (string?)x!["semantic"] == "batteryLevel")!;
        field["levels"] = System.Text.Json.Nodes.JsonNode.Parse("[" + string.Join(",", table) + "]");
        var old = JsonSerializer.Deserialize(node.ToJsonString(), HMJsonContext.Default.ControllerProfile)!;
        var got = Bytes(old, out byte c, out byte f, out byte cf);
        Check("uint8-level-map with ds3-battery's table gives its bytes for levels 0 to 11, level 11 as 10",
            got.Take(11).SequenceEqual(table) && got[11] == table[10], Convert.ToHexString(got));
        Check("charging gives 0xEE, full gives 0xEF, and both give 0xEF, full first as ds3-battery put it",
            c == 0xEE && f == 0xEF && cf == 0xEF, $"{c:X2} {f:X2} {cf:X2}");
        var usb = Bytes(ds3, out c, out f, out cf);
        Check("dualshock-3-full writes 0xEE at every level, 0xEE charging and 0xEF full, as a pad on USB does",
            usb.All(x => x == 0xEE) && c == 0xEE && f == 0xEF && cf == 0xEF, $"{Convert.ToHexString(usb)} {c:X2} {f:X2} {cf:X2}");
    }

    /// <summary>steam-controller-2's IMU register and the hold it drives
    /// (design note, mechanisms 1 and 3): SDL's SET_SETTINGS_VALUES on
    /// feature report 1 (SDL_hidapi_steam_triton.c:652-668 at 804cf65), the
    /// settings as 3-byte records (controller_structs.h:36-40), setting 48
    /// imu_mode. The register is the device's, from its protocol, as a create
    /// builds it, and the codec reads it as HMController hands it over. A real
    /// Triton's stamp stays frozen while its IMU is off (sc2-research
    /// HID_REPORT_FORMAT.md:83, :105 at b46d517).</summary>
    static void TritonImuHold(ControllerProfile triton)
    {
        var compiled = HIDMaestro.Internal.Usbip.CompiledProtocol.Compile(triton, new HIDMaestro.Internal.Usbip.UsbDescriptorSet(triton, 0));
        // Without a protocol the register checks fail, and the stamp checks
        // below still run, on a codec that holds nothing.
        var engine = compiled == null ? null : new HIDMaestro.Internal.Usbip.ProtocolEngine(compiled);
        int imu = compiled?.Registers.IndexOf("imuMode") ?? -1;
        void Set(string hex) { var b = new byte[64]; Convert.FromHexString(hex).CopyTo(b, 0); engine?.HandlePacket(b); }
        uint Reg() => engine != null && imu >= 0 ? engine.GetRegister(imu) : 0xFFFFFFFF;
        Check("the IMU is off until a host turns it on", Reg() == 0, imu < 0 ? "no imuMode register" : $"0x{Reg():X}");
        Set("018703301800");
        uint on = Reg();
        Set("018703300000");
        uint off = Reg();
        Check("SDL's 01 87 03 30 18 00 sets imuMode to 0x18 and 01 87 03 30 00 00 clears it", on == 0x18 && off == 0, $"0x{on:X} then 0x{off:X}");
        Set("018709" + "090000" + "301800" + "070100");
        uint second = Reg();
        Set("018703" + "090000");
        uint other = Reg();
        Check("a write of three settings with imu_mode second sets it, and one without imu_mode leaves it", second == 0x18 && other == 0x18, $"0x{second:X} then 0x{other:X}");
        Set("018806301800");
        Check("another message leaves it", Reg() == 0x18);

        var spec = triton.ExtendedReport!;
        var enc = new VendorBlobCodec.EncoderState();
        long now = 1_000_000;
        typeof(VendorBlobCodec.EncoderState).GetField("TimestampClock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(enc, (Func<long>)(() => now));
        typeof(VendorBlobCodec.EncoderState).GetField("TimestampFrequency", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(enc, 3_000_000L);
        var hold = VendorBlobProgram.Get(spec).HoldWhile;
        if (hold != null && compiled != null && engine != null)
        {
            enc.Hold = HIDMaestro.Internal.ConditionParser.Parse(hold, compiled.Registers, "holdWhile");
            enc.HoldRegisters = engine.RegisterWords;
        }
        var buf = new byte[spec.Size];
        uint Frame(long advanceTicks)
        {
            now += advanceTicks;
            var st = new HMGamepadState();
            VendorBlobCodec.EncodeInput(spec, in st, 0.5f, 0.5f, 0.5f, 0.5f, 0, 0, buf, enc);
            return BitConverter.ToUInt32(buf, 30);
        }
        Set("018703300000");
        var offRun = new[] { Frame(0), Frame(3000), Frame(3000) };
        Check("while the IMU is off the timestamp holds", offRun.All(x => x == offRun[0]), string.Join(" ", offRun));
        Set("018703301800");
        var onRun = new[] { Frame(3000), Frame(3000) };
        Check("the IMU on, it resumes from the held value and adds only the time since the last frame",
            onRun[0] == offRun[0] + 1000 && onRun[1] == onRun[0] + 1000, $"{offRun[^1]} then {string.Join(" ", onRun)}");
        Set("018703300000");
        uint held = Frame(3000);
        uint stillHeld = Frame(30_000);
        Check("off again, it holds there", held == onRun[1] && stillHeld == held, $"{held} {stillHeld}");
        engine?.Reset();
        Check("an attach or port reset turns the IMU off", Reg() == 0);
    }

    /// <summary>steam-controller-2's report 0x42 bytes 30 to 33, the IMU
    /// timestamp in microseconds, through the accumulator with a clock the
    /// check drives (design note, mechanism 1). The clock is set by name,
    /// so the check also builds on a tree without it, where it fails.</summary>
    static void TritonTimestamp(ControllerProfile triton)
    {
        var spec = triton.ExtendedReport!;
        var enc = new VendorBlobCodec.EncoderState();
        long now = 1_000_000;
        var clock = typeof(VendorBlobCodec.EncoderState).GetField("TimestampClock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        // 3,000,000 clock ticks a second: one tick is one 1/3 microsecond.
        clock?.SetValue(enc, (Func<long>)(() => now));
        typeof(VendorBlobCodec.EncoderState).GetField("TimestampFrequency", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(enc, 3_000_000L);
        var buf = new byte[spec.Size];
        uint Frame(uint stamp, long advanceTicks)
        {
            now += advanceTicks;
            var s = new HMGamepadState { SensorTimestamp = stamp };
            VendorBlobCodec.EncodeInput(spec, in s, 0.5f, 0.5f, 0.5f, 0.5f, 0, 0, buf, enc);
            return BitConverter.ToUInt32(buf, 30);
        }
        // The controller's clock, 1 ms a frame, from 0.
        var clockRun = new[] { Frame(0, 0), Frame(0, 3000), Frame(0, 3000) };
        Check("before any consumer stamp the timestamp counts the controller's clock in microseconds", clockRun.SequenceEqual(new uint[] { 0, 1000, 2000 }), string.Join(" ", clockRun));
        // The first nonzero stamp: the clock's 2 ms since the last frame,
        // not a jump to the stamp's own value.
        uint sw = Frame(3_000_000, 6000);
        Check("the frame that switches to the consumer's stamps adds the clock's time since the last frame and no jump", sw == 4000, sw.ToString());
        uint same = Frame(3_000_000, 3000);
        Check("an idle repeat of the last state, the same stamp again, adds nothing", same == 4000, same.ToString());
        uint step = Frame(3_000_000 + 12_000, 0);
        Check("a later stamp adds its advance", step == 8000, step.ToString());
        uint back = Frame(3_000_000, 3000);
        Check("a stamp that runs backward adds one nominal interval, the 4 ms idleFrameIntervalMs", back == 12_000, back.ToString());
        uint zero = Frame(0, 3000);
        Check("a stamp of 0 after the switch adds the clock's time", zero == 13_000, zero.ToString());
        uint after = Frame(3_000_000 + 6000, 0);
        Check("and the last stamp moves on with it, so the next stamp adds only its own advance", after == 14_000, after.ToString());
        Frame(0xFFFF_FF00u, 0);
        uint before = BitConverter.ToUInt32(buf, 30);
        uint wrap = Frame(0x0000_0100u, 0);
        Check("a stamp through the 32-bit wrap adds its true advance, 512 ticks", wrap - before == 512 / 3, $"{before} then {wrap}");
    }

    static FieldSpec Field(string json) => JsonSerializer.Deserialize(json, HMJsonContext.Default.FieldSpec)
        ?? throw new InvalidOperationException(json);

    static ExtendedReportSpec Spec(int size, params string[] fields) =>
        new() { ReportId = "0x00", Size = size, Fields = fields.Select(Field).ToList() };

    /// <summary>A random state that reaches every input the packers read:
    /// buttons, each of the four d-pad inputs, the six slots at edge and
    /// random values, and calibrated motion inside and past the int16
    /// range.</summary>
    static (HMGamepadState State, float[] Slots) RandomState(Random rng)
    {
        float Axis() => rng.Next(8) switch
        {
            0 => 0f, 1 => 1f, 2 => 0.5f, 3 => BitConverter.UInt32BitsToSingle((uint)rng.Next(0x3F000000, 0x3F800001)),
            4 => -0.25f, 5 => 1.5f, _ => (float)rng.NextDouble(),
        };
        float Motion() => rng.Next(4) switch { 0 => 0f, 1 => (float)(rng.NextDouble() * 20 - 10), 2 => (float)(rng.NextDouble() * 4000 - 2000), _ => (float)(rng.NextDouble() * 4 - 2) };
        var s = new HMGamepadState
        {
            Buttons = (HMButton)(uint)rng.Next(),
            Hat = (HMHat)rng.Next(0, 9),
            AccelGX = Motion(), AccelGY = Motion(), AccelGZ = Motion(),
            GyroDpsX = Motion(), GyroDpsY = Motion(), GyroDpsZ = Motion(),
        };
        switch (rng.Next(4))
        {
            case 0: s.HatDegrees = (float)(rng.NextDouble() * 720 - 360); break;
            case 1: s.HatHundredths = rng.Next(-36000, 72000); break;
            case 2: s.HatRaw = (ushort)rng.Next(0, 12); break;
        }
        var slots = new float[6];
        for (int i = 0; i < 6; i++) slots[i] = Math.Clamp(Axis(), 0f, 1f);
        return (s, slots);
    }

    /// <summary>An int16-le center: the wire value of a zero reading. With
    /// 32768 the word is offset binary, unsigned with zero at 0x8000, as the
    /// PS Move ZCM1 sends its accelerometer and gyro (psmove.c
    /// psmove_decode_16bit at 97d97e9 subtracts 0x8000).</summary>
    static void Int16Center()
    {
        var spec = Spec(9,
            """{"byte":1,"type":"int16-le","semantic":"accelX","center":32768}""",
            """{"byte":3,"type":"int16-le","semantic":"accelY"}""",
            """{"byte":5,"type":"int16-le","semantic":"accelGZ","scale":1000,"center":32768,"repeat":[2,2]}""");
        VendorBlobProgram.Check(spec, spec.Size, "int16 center", output: false, portBlock: false);
        var buf = new byte[spec.Size];
        var enc = new VendorBlobCodec.EncoderState();
        var bad = new List<string>();
        foreach (short v in new short[] { -32768, -1000, -1, 0, 1, 1000, 32767 })
        {
            var s = new HMGamepadState { AccelX = v, AccelY = v, AccelGZ = v / 1000f };
            Array.Clear(buf);
            VendorBlobCodec.EncodeInput(spec, in s, 0.5f, 0.5f, 0.5f, 0.5f, 0, 0, buf, enc);
            int offset = buf[1] | buf[2] << 8, plain = buf[3] | buf[4] << 8;
            int g1 = buf[5] | buf[6] << 8, g2 = buf[7] | buf[8] << 8;
            if (offset != (ushort)(v + 32768)) bad.Add($"accelX {v}: 0x{offset:X4}");
            if (plain != (ushort)v) bad.Add($"accelY {v}: 0x{plain:X4}");
            if (g1 != (ushort)(v + 32768) || g2 != g1) bad.Add($"accelGZ {v}: 0x{g1:X4} 0x{g2:X4}");
        }
        Check("center 32768 writes offset binary on a raw field and on a scaled, repeated one, and no center stays two's complement",
              bad.Count == 0, string.Join(", ", bad));

        // A center other than 32768 can carry a short past the word: it
        // clamps, where a wrap would turn a full swing into a small one.
        var off = Spec(5,
            """{"byte":1,"type":"int16-le","semantic":"accelX","center":40000}""",
            """{"byte":3,"type":"int16-le","semantic":"accelY","center":1000}""");
        VendorBlobProgram.Check(off, off.Size, "int16 center", output: false, portBlock: false);
        var clamps = new List<string>();
        foreach (short v in new short[] { -32768, -1001, -1000, 0, 25535, 25536, 32767 })
        {
            var s = new HMGamepadState { AccelX = v, AccelY = v };
            var b = new byte[off.Size];
            VendorBlobCodec.EncodeInput(off, in s, 0.5f, 0.5f, 0.5f, 0.5f, 0, 0, b, enc);
            int hi = b[1] | b[2] << 8, lo = b[3] | b[4] << 8;
            if (hi != Math.Clamp(v + 40000, 0, 65535)) clamps.Add($"center 40000, {v}: 0x{hi:X4}");
            if (lo != Math.Clamp(v + 1000, 0, 65535)) clamps.Add($"center 1000, {v}: 0x{lo:X4}");
        }
        Check("a centered word past 0 or 65535 clamps there", clamps.Count == 0, string.Join(", ", clamps));

        string? outRefused = null;
        try
        {
            var outSpec = Spec(3, """{"byte":1,"type":"int16-le","semantic":"x","center":32768}""");
            VendorBlobProgram.Check(outSpec, outSpec.Size, "int16 center", output: true, portBlock: false);
        }
        catch (InvalidOperationException e) { outRefused = e.Message; }
        Check("an output int16-le field with a center, which its decode ignores, is refused naming it",
              outRefused != null && outRefused.Contains("fields[0]", StringComparison.Ordinal) && outRefused.Contains("center", StringComparison.Ordinal),
              outRefused ?? "accepted");
        string? refused = null;
        try
        {
            var wide = Spec(3, """{"byte":1,"type":"int16-le","semantic":"accelX","center":65536}""");
            VendorBlobProgram.Check(wide, wide.Size, "int16 center", output: false, portBlock: false);
        }
        catch (InvalidOperationException e) { refused = e.Message; }
        Check("an int16-le center outside 0 to 65535 is refused, naming the field",
              refused != null && refused.Contains("fields[0].center", StringComparison.Ordinal), refused ?? "accepted");
    }

    /// <summary>The Switch Pro's 48-byte body as field lists: its buttons by
    /// HMButton bit, the d-pad resolved, 12-bit sticks at 0x800 +/- 0x600
    /// with Y up, and three frames of motion.</summary>
    static void SwitchProBody()
    {
        var spec = Spec(48,
            """{"byte":2,"type":"button-mask","buttons":["#2","#3","#0","#1","_","_","#5","#7"]}""",
            """{"byte":3,"type":"button-mask","buttons":["#8","#9","#11","#10","#12","#13"]}""",
            """{"byte":4,"type":"button-mask","dpad":"resolved","buttons":["DPAD_DOWN","DPAD_UP","DPAD_RIGHT","DPAD_LEFT","_","_","#4","#6"]}""",
            """{"byte":5,"type":"stick12-pair","semantic":"leftStick","min":512,"max":3584,"invert":true}""",
            """{"byte":8,"type":"stick12-pair","semantic":"rightStick","min":512,"max":3584,"invert":true}""",
            """{"byte":12,"type":"int16-le","semantic":"accelGZ","scale":4096,"negate":true,"repeat":[3,12]}""",
            """{"byte":14,"type":"int16-le","semantic":"accelGX","scale":4096,"negate":true,"repeat":[3,12]}""",
            """{"byte":16,"type":"int16-le","semantic":"accelGY","scale":4096,"repeat":[3,12]}""",
            """{"byte":18,"type":"int16-le","semantic":"gyroDpsZ","scale":[13371,936],"negate":true,"repeat":[3,12]}""",
            """{"byte":20,"type":"int16-le","semantic":"gyroDpsX","scale":[13371,936],"negate":true,"repeat":[3,12]}""",
            """{"byte":22,"type":"int16-le","semantic":"gyroDpsY","scale":[13371,936],"repeat":[3,12]}""");
        VendorBlobProgram.Check(spec, spec.Size, "switch-pro body", output: false, portBlock: false);
        // The profile's own stateBlock, against the list this check held
        // SwitchProPacker to until v2.0.0 moved the body into the profile.
        var own = ProfileDatabase.LoadEmbedded().All.First(p => p.Id == "switch-pro").StateBlockSpecForCodec();
        var enc2 = new VendorBlobCodec.EncoderState();
        var rng = new Random(20261016);
        var packed = new byte[48];
        var coded = new byte[spec.Size];
        var enc = new VendorBlobCodec.EncoderState();
        int differ = 0;
        string first = "";
        const int N = 200_000;
        for (int i = 0; i < N; i++)
        {
            var (s, v) = RandomState(rng);
            VendorBlobCodec.EncodeInput(own, in s, v[0], v[1], v[2], v[3], v[4], v[5], packed, enc2);
            VendorBlobCodec.EncodeInput(spec, in s, v[0], v[1], v[2], v[3], v[4], v[5], coded, enc);
            if (!packed.AsSpan().SequenceEqual(coded))
            {
                if (differ++ == 0) first = $"{Convert.ToHexString(packed)} against {Convert.ToHexString(coded)}";
            }
        }
        Check($"switch-pro's stateBlock gives the 48 bytes of the list SwitchProPacker.BuildBody was held to, on {N:N0} random states", differ == 0, differ == 0 ? "" : $"{differ} differ, first {first}");
    }

    /// <summary>The Switch 2 Pro composite's 24-byte body as field lists.</summary>
    static void Switch2ProBody()
    {
        string gyro = (Math.PI / 180.0 * 32767.0 / 34.8).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var spec = Spec(24,
            """{"byte":0,"type":"button-mask","buttons":["B","A","Y","X","RightBumper","RT_DIGITAL","Start","RightStick"]}""",
            """{"byte":1,"type":"button-mask","buttons":["DPAD_DOWN","DPAD_RIGHT","DPAD_LEFT","DPAD_UP","LeftBumper","LT_DIGITAL","Back","LeftStick"]}""",
            """{"byte":2,"type":"button-mask","buttons":["Guide","Share","RightPaddle","LeftPaddle","Misc1"]}""",
            """{"byte":4,"type":"int16-axis","semantic":"leftStickX","min":0,"max":4095}""",
            """{"byte":6,"type":"int16-axis","semantic":"leftStickY","min":0,"max":4095,"invert":true}""",
            """{"byte":8,"type":"int16-axis","semantic":"rightStickX","min":0,"max":4095}""",
            """{"byte":10,"type":"int16-axis","semantic":"rightStickY","min":0,"max":4095,"invert":true}""",
            """{"byte":12,"type":"int16-le","semantic":"accelGX","scale":[32767,8]}""",
            """{"byte":14,"type":"int16-le","semantic":"accelGZ","scale":[32767,8],"negate":true}""",
            """{"byte":16,"type":"int16-le","semantic":"accelGY","scale":[32767,8]}""",
            "{\"byte\":18,\"type\":\"int16-le\",\"semantic\":\"gyroDpsX\",\"scale\":" + gyro + "}",
            "{\"byte\":20,\"type\":\"int16-le\",\"semantic\":\"gyroDpsZ\",\"scale\":" + gyro + ",\"negate\":true}",
            "{\"byte\":22,\"type\":\"int16-le\",\"semantic\":\"gyroDpsY\",\"scale\":" + gyro + "}");
        VendorBlobProgram.Check(spec, spec.Size, "switch2 body", output: false, portBlock: false);
        // The profile's own stateBlock, against the list this check held
        // Switch2ProPacker to until v2.0.0 moved the body into the profile.
        var profile = ProfileDatabase.LoadEmbedded().All.First(p => p.Id == "switch2-pro-controller-composite");
        var own = profile.StateBlockSpecForCodec();
        var rng = new Random(20261017);
        var packed = new byte[24];
        var enc2 = new VendorBlobCodec.EncoderState();
        var coded = new byte[spec.Size];
        var enc = new VendorBlobCodec.EncoderState();
        int differ = 0;
        string first = "";
        const int N = 200_000;
        for (int i = 0; i < N; i++)
        {
            var (s, v) = RandomState(rng);
            VendorBlobCodec.EncodeInput(own, in s, v[0], v[1], v[2], v[3], v[4], v[5], packed, enc2);
            VendorBlobCodec.EncodeInput(spec, in s, v[0], v[1], v[2], v[3], v[4], v[5], coded, enc);
            if (!packed.AsSpan().SequenceEqual(coded))
            {
                if (differ++ == 0) first = $"{Convert.ToHexString(packed)} against {Convert.ToHexString(coded)}";
            }
        }
        Check($"the Switch 2 composite's stateBlock gives the field list's 24 bytes on {N:N0} random states", differ == 0, differ == 0 ? "" : $"{differ} differ, first {first}");
    }

    /// <summary>Output report 0x01 or 0x10 with its ID at byte 0: the left
    /// block at 2 to 5 and the right at 6 to 9, each the louder of the high
    /// band (bits 1 to 7 of its second byte, times 255 / 200) and the low
    /// band (its fourth byte with bit 7 of its third below it, 0x40 to 0x72,
    /// times 255 / 101), both truncated.</summary>
    static string SwitchRumbleSide(string name, int block) =>
        "{\"type\":\"packed-uint\",\"semantic\":\"" + name + "\",\"parts\":[" +
        "{\"pieces\":[{\"at\":" + ((block + 1) * 8 + 1) + ",\"count\":7}],\"scale\":[510,200],\"round\":\"down\"}," +
        "{\"pieces\":[{\"at\":" + ((block + 2) * 8 + 7) + ",\"count\":1},{\"at\":" + ((block + 3) * 8) + ",\"count\":8}]," +
        "\"offset\":-128,\"range\":[0,101],\"scale\":[255,101],\"round\":\"down\"}]}";

    static unsafe void SwitchProRumble()
    {
        // The profile's own decoder, against the field list this check held
        // SwitchProPacker.DecodeRumbleAmplitude to until v2.0.0 moved the
        // decode into the profile.
        var decoder = ProgramBinder.BindDecoders(ProfileDatabase.LoadEmbedded().All.First(p => p.Id == "switch-pro").GetOrCompileModel())[0];
        int li = Array.IndexOf(decoder.Form.Semantics, "leftMotor"), ri = Array.IndexOf(decoder.Form.Semantics, "rightMotor");
        var input = GC.AllocateArray<byte>(10 + 4, pinned: true);
        var output = GC.AllocateArray<byte>(8, pinned: true);
        var spec = new ExtendedReportSpec { ReportId = "0x10", Size = 10, Fields = new() { Field(SwitchRumbleSide("leftMotor", 2)), Field(SwitchRumbleSide("rightMotor", 6)) } };
        VendorBlobProgram.Check(spec, spec.Size, "switch-pro rumble", output: true, portBlock: false);
        var report = new byte[10];
        report[0] = 0x10;
        int differ = 0, n = 0;
        bool boxed = true;
        string first = "";
        // Every value of what the hand-written decode reads, on the left
        // block, with the right block holding the same bytes shifted.
        for (int b1 = 0; b1 < 256; b1++)
            foreach (byte b2 in new byte[] { 0x00, 0x80, 0x7F, 0xFF })
                for (int b3 = 0; b3 < 256; b3++)
                {
                    report[2] = 0x5A; report[3] = (byte)b1; report[4] = b2; report[5] = (byte)b3;
                    report[6] = (byte)b3; report[7] = (byte)(255 - b1); report[8] = (byte)(b2 ^ 0x80); report[9] = (byte)b1;
                    var (fields, _) = VendorBlobCodec.Decode(spec, report);
                    report.CopyTo(input, 0);
                    fixed (byte* sp = input) fixed (byte* dp = output) decoder.Run(sp, dp);
                    byte l = output[li], r = output[ri];
                    n++;
                    boxed &= fields["leftMotor"] is byte && fields["rightMotor"] is byte;
                    if ((byte)fields["leftMotor"] != l || (byte)fields["rightMotor"] != r)
                        if (differ++ == 0) first = $"{Convert.ToHexString(report)}: {l} {r} against {fields["leftMotor"]} {fields["rightMotor"]}";
                }
        Check($"packed-uint gives switch-pro's protocol.decoders.rumble on both sides of {n:N0} reports, boxed as byte",
            differ == 0 && boxed, differ == 0 ? (boxed ? "" : "not byte") : $"{differ} differ, first {first}");
    }

    /// <summary>Output report 0x02 with its ID at byte 0: two 16-byte
    /// blocks, each a sequence byte and three 5-byte frames, the motor the
    /// largest amplitude of the six frames (bits 10 to 19 for the right,
    /// 30 to 39 for the left) times 64 x 255 / 29000, rounded.</summary>
    static string Switch2Motor(string name, int bit) =>
        "{\"type\":\"packed-uint\",\"semantic\":\"" + name + "\",\"parts\":[" +
        "{\"pieces\":[{\"at\":" + (2 * 8 + bit) + ",\"count\":10}],\"frames\":[3,5],\"scale\":[16320,29000]}," +
        "{\"pieces\":[{\"at\":" + (18 * 8 + bit) + ",\"count\":10}],\"frames\":[3,5],\"scale\":[16320,29000]}]}";

    static void Switch2ProRumble()
    {
        var spec = new ExtendedReportSpec { ReportId = "0x02", Size = 64, Fields = new() { Field(Switch2Motor("leftMotor", 30)), Field(Switch2Motor("rightMotor", 10)) } };
        VendorBlobProgram.Check(spec, spec.Size, "switch2 rumble", output: true, portBlock: false);
        var rng = new Random(20261018);
        var report = new byte[64];
        report[0] = 0x02;
        int differ = 0, n = 0;
        bool boxed = true;
        string first = "";
        void One()
        {
            var (fields, _) = VendorBlobCodec.Decode(spec, report);
            Switch2Decode(report, out byte l, out byte r);
            n++;
            boxed &= fields["leftMotor"] is byte && fields["rightMotor"] is byte;
            if ((byte)fields["leftMotor"] != l || (byte)fields["rightMotor"] != r)
                if (differ++ == 0) first = $"{Convert.ToHexString(report)}: {l} {r} against {fields["leftMotor"]} {fields["rightMotor"]}";
        }
        // Each amplitude field alone at 0, 1, 512 and 1023, then random
        // reports.
        for (int act = 0; act < 2; act++)
            for (int frame = 0; frame < 3; frame++)
                for (int field = 0; field < 2; field++)
                    foreach (int amp in new[] { 0, 1, 512, 1023 })
                    {
                        Array.Clear(report, 1, 63);
                        int o = 1 + act * 16 + 1 + frame * 5;
                        ulong v = (ulong)amp << (field == 0 ? 10 : 30);
                        for (int b = 0; b < 5; b++) report[o + b] = (byte)(v >> (8 * b));
                        One();
                    }
        for (int i = 0; i < 1_000_000; i++)
        {
            rng.NextBytes(report.AsSpan(1));
            One();
        }
        Check($"packed-uint gives the Switch 2 composite's rumble decoder on {n:N0} reports, boxed as byte",
            differ == 0 && boxed, differ == 0 ? (boxed ? "" : "not byte") : $"{differ} differ, first {first}");
    }

    static readonly HIDMaestro.Internal.DecoderRunner s_switch2Rumble = HIDMaestro.Internal.ProgramBinder.BindDecoders(
        ProfileDatabase.LoadEmbedded().All.First(p => p.Id == "switch2-pro-controller-composite").GetOrCompileModel())[0];

    /// <summary>The composite's protocol.decoders.rumble on report 0x02, ID
    /// first, through its generated kernel.</summary>
    static unsafe void Switch2Decode(byte[] report, out byte left, out byte right)
    {
        var input = new byte[report.Length + 4];
        report.CopyTo(input, 0);
        var output = new byte[2];
        fixed (byte* sp = input)
        fixed (byte* dp = output)
            s_switch2Rumble.Run(sp, dp);
        left = output[0];
        right = output[1];
    }
}
