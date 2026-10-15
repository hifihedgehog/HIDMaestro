using System;
using System.Collections.Generic;

namespace HIDMaestro.Internal;

/// <summary>Compiled form of an <see cref="ExtendedReportSpec"/> (issue #34).
///
/// The codec used to re-interpret the spec's strings on every frame: a
/// string switch on <c>field.Type</c>, string switches on
/// <c>field.Semantic</c>, <c>Enum.TryParse&lt;HMButton&gt;</c> per button
/// name, and "0-3"-style range parsing, all per field per frame at the
/// consumer's submit cadence. The spec is immutable after profile load, so
/// all of that is compile-once work. <see cref="Compile"/> runs it once and
/// the per-frame loops in <see cref="VendorBlobCodec"/> switch on numeric
/// opcodes over this program instead.
///
/// Byte-for-byte parity with the pre-compiled string-switch implementation
/// is locked by <c>test/probes/vendor_blob_golden_check</c> (70 golden
/// hashes across 10 shipped Sony specs, all three directions). Change
/// behavior here only together with a deliberate golden regeneration.</summary>
internal sealed class VendorBlobProgram
{
    /// <summary>Field type, numeric. One value per wire-format family the
    /// codec understands; <see cref="FieldOp.Unknown"/> preserves the
    /// string implementation's silent-no-op contract for unrecognized
    /// type strings in the codec. Since v1.13.0 a create refuses a profile
    /// that declares one (<see cref="Check"/>).</summary>
    public enum FieldOp : byte
    {
        Unknown = 0,
        U8Axis, U8Trigger, U8Rolling, U8Const,
        I16, U32, TouchpadFinger, Bitfield, Battery, HatOctant,
        ButtonMask, Rgb24, BytesPassthrough, BytesZero, Crc32,
        Stick12Pair,
        // Valve's 16-bit state packets (issue #56). The Steam Deck and both
        // Steam Controllers carry sticks as signed 16-bit, triggers as
        // unsigned 16-bit, and a 32-bit packet counter, none of which the
        // 8-bit ops above can express.
        I16Axis, U16Trigger, U32Rolling,
        // Valve's trackpads. Each pad is a signed 16-bit coordinate
        // pair plus an unsigned pressure, and on the 2015 controller
        // the left pair is shared with the joystick, which is why the
        // pad-or-stick op exists rather than two independent fields.
        I16Pad, I16PadOrStick, U16Pressure,
        // Unsigned 16-bit little-endian. Issue #64: the DualShock 4's
        // sensor timestamp, which counts in 16/3 µs and wraps at 16 bits.
        U16,
        // The DualShock 3 (PadForge discussion 476): a pressure-sensitive
        // button's 0..255 byte, a 10-bit motion sensor in a big-endian
        // 16-bit field, and its battery status byte.
        U8Pressure, U10BE, Ds3Battery,
    }

    /// <summary>Input-direction value source, numeric. Replaces the
    /// per-frame string switches on <c>field.Semantic</c>.</summary>
    public enum SrcOp : byte
    {
        None = 0,
        LeftStickX, LeftStickY, RightStickX, RightStickY,
        LeftTrigger, RightTrigger,
        GyroPitch, GyroYaw, GyroRoll, AccelX, AccelY, AccelZ,
        SensorTimestamp,
        Finger0, Finger1,
        // Whole-stick sources. Unlike the per-axis entries above these name
        // an X/Y pair, because the wire format interleaves the two axes
        // inside shared bytes and neither can be written alone.
        LeftStick, RightStick,
        // Trackpad coordinates and pressures. Finger 0 drives the left
        // pad and finger 1 the right, because a Valve pad is a
        // single-contact surface and the two pads are separate
        // surfaces, unlike Sony's one two-finger pad.
        LeftPadX, LeftPadY, RightPadX, RightPadY,
        LeftPadPressure, RightPadPressure,
        // HMGamepadState.TouchpadPacketCounter, which the DualShock 4
        // carries in each touch report's packet-counter byte (issue #64).
        TouchpadPacketCounter,
        // The pressure-sensitive buttons, HMGamepadState.Pressure*.
        PressureA, PressureB, PressureX, PressureY,
        PressureLeftBumper, PressureRightBumper,
        PressureDpadUp, PressureDpadRight, PressureDpadDown, PressureDpadLeft,
    }

    // Bitfield flag sources (input direction), numeric. Index-aligned with
    // the compiled FlagKinds array. FlagCable is the DualShock 4's cable
    // bit, set while the pad charges or sits charged on its cable.
    public const byte FlagNone = 0, FlagCharging = 1, FlagFull = 2,
                      FlagMic = 3, FlagHeadphones = 4, FlagCable = 5;

    // Button-mask sentinels, stored above the 32-bit HMButton mask space.
    public const ulong ButtonLtDigital = 1UL << 32;
    public const ulong ButtonRtDigital = 1UL << 33;
    // D-pad directions as discrete buttons. Some pads do not declare a hat
    // at all and instead give each direction its own bit in the button
    // array: Nintendo's Switch 2 Pro report 0x09 is the shipped example.
    // Sourced from HMHat rather than from the button mask, so a consumer
    // still sets the d-pad the one normal way.
    public const ulong ButtonDpadUp    = 1UL << 34;
    public const ulong ButtonDpadDown  = 1UL << 35;
    public const ulong ButtonDpadLeft  = 1UL << 36;
    public const ulong ButtonDpadRight = 1UL << 37;
    // Pad-touch bits, sourced from TouchpadFinger0/1Active rather than
    // from the button mask. Valve's decoders gate the whole trackpad
    // lane on these, and on the 2015 controller the left one also
    // selects whether the shared axes read as pad or as joystick.
    public const ulong ButtonPad0Touch = 1UL << 38;
    public const ulong ButtonPad1Touch = 1UL << 39;

    public readonly struct CompiledField
    {
        public readonly FieldSpec Src;      // original spec (decode strings, scope)
        public readonly FieldOp Op;
        public readonly SrcOp Source;
        public readonly int B;              // byte offset, -1 when absent
        public readonly int RangeLo;        // byte range, -1 when absent
        public readonly int RangeHi;
        public readonly int BitLo;          // bit range (defaulted 0..7)
        public readonly int BitHi;
        public readonly bool HasBits;       // whether the spec declared "bits"
        public readonly int Center;         // uint8-axis center (default 128)
        public readonly int Neutral;        // hat neutral (default 8)
        public readonly byte Initial;
        public readonly int Stride;
        public readonly int CrcDst;         // resolved crc dest start, -1 = end-4
        public readonly string RollKey;     // rolling-counter dict key, precomputed
        public readonly ulong[]? ButtonBits;// per position: HMButton mask | sentinel, 0 = skip
        public readonly byte[]? FlagKinds;  // bitfield: Flag* per position
        public readonly uint Divisor;       // uint16-le sensor timestamp, >= 1
        public readonly int FullValue;      // uint8-battery while full, -1 when absent
        public readonly int Resolution;     // uint10-be wire counts per unit, >= 1
        public readonly int SourceResolution; // uint10-be source counts per unit, >= 1
        public readonly bool Invert;        // uint10-be wire axis opposite the SDK's

        public CompiledField(FieldSpec src, FieldOp op, SrcOp source, int b,
                             int rangeLo, int rangeHi, int bitLo, int bitHi, bool hasBits,
                             int center, int neutral, byte initial, int stride,
                             int crcDst, string rollKey, ulong[]? buttonBits, byte[]? flagKinds,
                             uint divisor, int fullValue,
                             int resolution, int sourceResolution, bool invert)
        {
            Src = src; Op = op; Source = source; B = b;
            RangeLo = rangeLo; RangeHi = rangeHi; BitLo = bitLo; BitHi = bitHi; HasBits = hasBits;
            Center = center; Neutral = neutral; Initial = initial; Stride = stride;
            CrcDst = crcDst; RollKey = rollKey; ButtonBits = buttonBits; FlagKinds = flagKinds;
            Divisor = divisor; FullValue = fullValue;
            Resolution = resolution; SourceResolution = sourceResolution; Invert = invert;
        }
    }

    public readonly CompiledField[] Fields;

    /// <summary>Number of fields that produce a decode-dict entry, so
    /// <see cref="VendorBlobCodec.Decode"/> can pre-size its dictionary.</summary>
    public readonly int DecodeFieldCount;

    private VendorBlobProgram(CompiledField[] fields, int decodeFieldCount)
    {
        Fields = fields;
        DecodeFieldCount = decodeFieldCount;
    }

    /// <summary>Get (or build) the compiled program for a spec. The spec is
    /// immutable after profile load; the cache write is a benign race
    /// (idempotent value).</summary>
    public static VendorBlobProgram Get(ExtendedReportSpec spec)
    {
        if (spec.CompiledProgramCache is VendorBlobProgram cached) return cached;
        var built = Compile(spec);
        spec.CompiledProgramCache = built;
        return built;
    }

    private static VendorBlobProgram Compile(ExtendedReportSpec spec)
    {
        var fields = new CompiledField[spec.Fields.Count];
        int decodeCount = 0;
        for (int i = 0; i < spec.Fields.Count; i++)
        {
            var f = spec.Fields[i];
            var op = f.Type switch
            {
                "uint8-axis"        => FieldOp.U8Axis,
                "uint8-trigger"     => FieldOp.U8Trigger,
                "uint8-rolling"     => FieldOp.U8Rolling,
                "uint8"             => FieldOp.U8Const,
                "int16-le"          => FieldOp.I16,
                "int16-axis"        => FieldOp.I16Axis,
                "int16-pad"         => FieldOp.I16Pad,
                "int16-pad-or-stick"=> FieldOp.I16PadOrStick,
                "uint16-pressure"   => FieldOp.U16Pressure,
                "uint16-trigger"    => FieldOp.U16Trigger,
                "uint16-le"         => FieldOp.U16,
                "uint8-pressure"    => FieldOp.U8Pressure,
                "uint10-be"         => FieldOp.U10BE,
                "ds3-battery"       => FieldOp.Ds3Battery,
                "uint32-le"         => FieldOp.U32,
                "uint32-rolling"    => FieldOp.U32Rolling,
                "touchpad-finger"   => FieldOp.TouchpadFinger,
                "bitfield"          => FieldOp.Bitfield,
                "uint8-battery"     => FieldOp.Battery,
                "hat-octant"        => FieldOp.HatOctant,
                "button-mask"       => FieldOp.ButtonMask,
                "rgb24"             => FieldOp.Rgb24,
                "bytes-passthrough" => FieldOp.BytesPassthrough,
                "bytes-zero"        => FieldOp.BytesZero,
                "crc32-le"          => FieldOp.Crc32,
                // Two 12-bit axes packed into three shared bytes. Nintendo's
                // Switch 2 controllers use this for both sticks; the middle
                // byte carries X's high nibble and Y's low nibble, so the
                // pair has to be written as one field rather than two.
                "stick12-pair"      => FieldOp.Stick12Pair,
                _                   => FieldOp.Unknown,
            };

            var source = f.Semantic switch
            {
                "leftStickX"      => SrcOp.LeftStickX,
                "leftStickY"      => SrcOp.LeftStickY,
                "rightStickX"     => SrcOp.RightStickX,
                "rightStickY"     => SrcOp.RightStickY,
                "leftTrigger"     => SrcOp.LeftTrigger,
                "rightTrigger"    => SrcOp.RightTrigger,
                "gyroPitch"       => SrcOp.GyroPitch,
                "gyroYaw"         => SrcOp.GyroYaw,
                "gyroRoll"        => SrcOp.GyroRoll,
                "accelX"          => SrcOp.AccelX,
                "accelY"          => SrcOp.AccelY,
                "accelZ"          => SrcOp.AccelZ,
                "sensorTimestamp" => SrcOp.SensorTimestamp,
                "touchpadFinger1" => SrcOp.Finger1,
                "touchpadFinger0" => SrcOp.Finger0,
                "leftStick"       => SrcOp.LeftStick,
                "rightStick"      => SrcOp.RightStick,
                "leftPadX"        => SrcOp.LeftPadX,
                "leftPadY"        => SrcOp.LeftPadY,
                "rightPadX"       => SrcOp.RightPadX,
                "rightPadY"       => SrcOp.RightPadY,
                "leftPadPressure" => SrcOp.LeftPadPressure,
                "rightPadPressure"=> SrcOp.RightPadPressure,
                "touchpadPacketCounter" => SrcOp.TouchpadPacketCounter,
                "pressureA"           => SrcOp.PressureA,
                "pressureB"           => SrcOp.PressureB,
                "pressureX"           => SrcOp.PressureX,
                "pressureY"           => SrcOp.PressureY,
                "pressureLeftBumper"  => SrcOp.PressureLeftBumper,
                "pressureRightBumper" => SrcOp.PressureRightBumper,
                "pressureDpadUp"      => SrcOp.PressureDpadUp,
                "pressureDpadRight"   => SrcOp.PressureDpadRight,
                "pressureDpadDown"    => SrcOp.PressureDpadDown,
                "pressureDpadLeft"    => SrcOp.PressureDpadLeft,
                _                 => SrcOp.None,
            };
            // touchpad-finger defaults to finger0 for ANY other semantic,
            // matching the string implementation's else-branch.
            if (op == FieldOp.TouchpadFinger && source != SrcOp.Finger1)
                source = SrcOp.Finger0;

            // Deliberate compile-time validation (audit of #34): a negative
            // byte offset is a malformed spec. The string implementation
            // "caught" this by crashing with IndexOutOfRangeException on
            // the first frame; failing at compile (first use, controller
            // creation path) names the field instead.
            if (f.Byte is int declaredByte && declaredByte < 0)
                throw new InvalidOperationException(
                    $"extendedReport field {i} (type '{f.Type}', semantic " +
                    $"'{f.Semantic}') declares negative byte offset {declaredByte}");

            int b = f.Byte ?? -1;
            bool hasBytes = TryParseByteRange(f.Bytes, out int rangeLo, out int rangeHi);
            if (!hasBytes) { rangeLo = -1; rangeHi = -1; }
            bool hasBits = TryParseBitRange(f.Bits, out int bitLo, out int bitHi);
            if (!hasBits) { bitLo = 0; bitHi = 7; }

            // crc dest: the string impl resolves Bytes-range start, else
            // Byte, else buffer.Length - 4 AT RUN TIME. The runtime
            // fallback matters (audit of #34): Decode sees the RECEIVED
            // report's length, which a short host write makes smaller
            // than spec.Size, and the old code validated the CRC at the
            // actual buffer end. -1 = resolve from buffer.Length at use.
            int crcDst = hasBytes ? rangeLo : (f.Byte ?? -1);

            string rollKey = f.Semantic ?? "_b" + (b >= 0 ? b : 0);
            // EncodeOutput's rolling key differs when semantic is null
            // ("_o{b}") but no shipped spec has a semantic-less rolling
            // output field; preserve the input-side key and let the output
            // path derive its own when needed (see VendorBlobCodec).

            ulong[]? buttonBits = null;
            byte[]? flagKinds = null;
            if (f.Buttons != null)
            {
                if (op == FieldOp.ButtonMask)
                {
                    buttonBits = new ulong[f.Buttons.Count];
                    for (int j = 0; j < f.Buttons.Count; j++)
                    {
                        string name = f.Buttons[j];
                        if (string.IsNullOrEmpty(name) || name == "_") continue;
                        if (name == "LT_DIGITAL") { buttonBits[j] = ButtonLtDigital; continue; }
                        if (name == "RT_DIGITAL") { buttonBits[j] = ButtonRtDigital; continue; }
                        if (name == "DPAD_UP")    { buttonBits[j] = ButtonDpadUp;    continue; }
                        if (name == "DPAD_DOWN")  { buttonBits[j] = ButtonDpadDown;  continue; }
                        if (name == "DPAD_LEFT")  { buttonBits[j] = ButtonDpadLeft;  continue; }
                        if (name == "DPAD_RIGHT") { buttonBits[j] = ButtonDpadRight; continue; }
                        if (name == "LEFTPAD_TOUCH")  { buttonBits[j] = ButtonPad0Touch; continue; }
                        if (name == "RIGHTPAD_TOUCH") { buttonBits[j] = ButtonPad1Touch; continue; }
                        if (Enum.TryParse<HMButton>(name, true, out var btn))
                        {
                            buttonBits[j] = (uint)btn;
                            continue;
                        }
                        // Issue #58. A name that resolves to nothing used to
                        // leave buttonBits[j] = 0, which the encoder skips, so
                        // a typo or a control the SDK cannot express became a
                        // button that silently never fires. A profile that
                        // drops a control is worse than one that refuses to
                        // load. Deliberately unused slots are spelled "_" and
                        // are already handled above.
                        throw new InvalidOperationException(
                            $"Button name '{name}' at index {j} of the button mask at byte " +
                            $"{f.Byte} resolves to no HMButton and is not one of the " +
                            "recognized special names (LT_DIGITAL, RT_DIGITAL, DPAD_UP, " +
                            "DPAD_DOWN, DPAD_LEFT, DPAD_RIGHT, LEFTPAD_TOUCH, " +
                            "RIGHTPAD_TOUCH). Use \"_\" for a slot that is " +
                            "deliberately unused.");
                    }
                }
                else if (op == FieldOp.Bitfield)
                {
                    flagKinds = new byte[f.Buttons.Count];
                    for (int j = 0; j < f.Buttons.Count; j++)
                    {
                        flagKinds[j] = f.Buttons[j] switch
                        {
                            "batteryCharging"     => FlagCharging,
                            "batteryFull"         => FlagFull,
                            "micMuted"            => FlagMic,
                            "headphonesConnected" => FlagHeadphones,
                            "cableConnected"      => FlagCable,
                            _                     => FlagNone,
                        };
                    }
                }
            }

            // Issue #64. Both are malformed-spec errors, named at compile
            // like the negative byte offset above rather than producing a
            // wrong wire value per frame: a divisor below 1 would divide by
            // zero or count backwards, and a full value the bit range cannot
            // hold would be silently masked into a different level.
            int divisor = f.Divisor ?? 1;
            if (divisor < 1)
                throw new InvalidOperationException(
                    $"extendedReport field {i} (type '{f.Type}') declares divisor {divisor}, below the minimum of 1");
            int fullValue = f.FullValue ?? -1;
            if (f.FullValue is int fv && (fv < 0 || fv >= (1 << (bitHi - bitLo + 1))))
                throw new InvalidOperationException(
                    $"extendedReport field {i} (type '{f.Type}') declares fullValue {fv}, " +
                    $"which bits {bitLo}-{bitHi} cannot hold");

            // A 10-bit sensor needs both resolutions, or the scale is
            // undefined. Neither has a meaningful default, unlike the 1:1
            // divisor above.
            int resolution = f.Resolution ?? 0, sourceResolution = f.SourceResolution ?? 0;
            if (op == FieldOp.U10BE && (resolution < 1 || sourceResolution < 1))
                throw new InvalidOperationException(
                    $"extendedReport field {i} (type '{f.Type}') needs resolution and sourceResolution of 1 or more");
            if (op == FieldOp.U8Pressure && (source < SrcOp.PressureA || source > SrcOp.PressureDpadLeft))
                throw new InvalidOperationException(
                    $"extendedReport field {i} (type '{f.Type}') names no pressure-sensitive button ('{f.Semantic}')");

            fields[i] = new CompiledField(f, op, source, b, rangeLo, rangeHi,
                bitLo, bitHi, hasBits,
                f.Center ?? (op == FieldOp.U10BE ? 512 : 128), f.NeutralValue ?? 8,
                (byte)(f.Initial ?? 0), f.Stride ?? 1,
                crcDst, rollKey, buttonBits, flagKinds,
                (uint)divisor, fullValue,
                resolution, sourceResolution, f.Invert == true);

            // Mirror of Decode's per-type "produces a dict entry" predicate,
            // used only for dictionary pre-sizing (over-count is harmless).
            bool producesEntry = op switch
            {
                FieldOp.U8Const or FieldOp.U8Rolling or FieldOp.U8Axis
                    or FieldOp.U8Trigger or FieldOp.HatOctant => f.Byte != null && f.Semantic != null,
                FieldOp.ButtonMask => f.Byte != null && f.Buttons != null,
                FieldOp.Rgb24 or FieldOp.BytesPassthrough => hasBytes && f.Semantic != null,
                _ => false,
            };
            if (producesEntry) decodeCount++;
        }
        return new VendorBlobProgram(fields, decodeCount);
    }

    /// <summary>v1.13.0: refuse a field the codec would skip, cut short or
    /// read past the end of its report, rather than let it compile into a
    /// field that is silently never written or that throws on the first
    /// report. Runs where a create compiles the device model, before
    /// anything machine-wide. <paramref name="size"/> is the report's bytes,
    /// or a port block's. <paramref name="output"/> is true for a report the
    /// host writes, whose semantics are names rather than SDK sources.
    /// <paramref name="portBlock"/> is true for a port block, and an output
    /// port block's single-byte fields <see cref="PortLayout.ReadOutput"/>
    /// reads through their bits. Throws <see cref="InvalidOperationException"/>
    /// naming <paramref name="where"/>, the field and the limit.</summary>
    internal static void Check(ExtendedReportSpec spec, int size, string where, bool output, bool portBlock)
    {
        if (spec.Fields == null)
            throw new InvalidOperationException($"{where}.fields is null. Leave it out, or give an empty list, for no fields.");
        for (int i = 0; i < spec.Fields.Count; i++)
            if (spec.Fields[i] == null)
                throw new InvalidOperationException($"{where}.fields[{i}] is null.");
        VendorBlobProgram prog;
        // Compile's own refusals name the field but not the profile.
        try { prog = Get(spec); }
        catch (InvalidOperationException e) { throw new InvalidOperationException($"{where}: {e.Message}", e); }
        for (int i = 0; i < prog.Fields.Length; i++)
        {
            ref readonly var f = ref prog.Fields[i];
            var src = f.Src;
            // Messages are built only on the throw path, so a profile that
            // passes costs no strings.
            if (f.Op == FieldOp.Unknown)
                throw new InvalidOperationException(
                    $"{where}.fields[{i}].type '{src.Type}' is not a field type: {string.Join(", ", s_typeNames)}.");
            if (src.Bytes != null && (!TryParseByteRange(src.Bytes, out int bytesLo, out int bytesHi) || bytesHi < bytesLo))
                throw new InvalidOperationException($"{where}.fields[{i}].bytes \"{src.Bytes}\" is not a range such as \"3-8\".");
            if (src.Bits != null)
            {
                if (!TryParseBitRange(src.Bits, out _, out _) || f.BitLo < 0 || f.BitHi < f.BitLo)
                    throw new InvalidOperationException($"{where}.fields[{i}].bits \"{src.Bits}\" is not a range such as \"0-3\".");
                bool honored = f.Op is FieldOp.Bitfield or FieldOp.Battery or FieldOp.HatOctant or FieldOp.ButtonMask
                    || (output && portBlock && f.Op is FieldOp.U8Const or FieldOp.U8Axis or FieldOp.U8Trigger);
                if (!honored)
                    throw new InvalidOperationException($"{where}.fields[{i}] ('{src.Type}').bits: this type reads whole bytes and ignores bits.");
                int maxBit = f.Op == FieldOp.ButtonMask ? 63 : 7;
                if (f.BitHi > maxBit)
                    throw new InvalidOperationException(
                        $"{where}.fields[{i}] ('{src.Type}').bits \"{src.Bits}\" reaches bit {f.BitHi}. This type holds bits 0 to {maxBit}.");
            }

            var (lo, hi) = Extent(in f, size);
            if (lo < 0)
                throw new InvalidOperationException(
                    UsesRange(f.Op)
                        ? $"{where}.fields[{i}] ('{src.Type}') needs bytes, a range such as \"3-8\"."
                        : $"{where}.fields[{i}] ('{src.Type}') needs byte, the offset it is written at.");
            if (f.Op == FieldOp.Rgb24 && f.RangeHi - f.RangeLo != 2)
                throw new InvalidOperationException($"{where}.fields[{i}] ('{src.Type}').bytes \"{src.Bytes}\" must name three bytes, red, green and blue.");
            if (f.Op == FieldOp.Crc32 && f.RangeLo >= 0 && f.RangeHi - f.RangeLo != 3)
                throw new InvalidOperationException($"{where}.fields[{i}] ('{src.Type}').bytes \"{src.Bytes}\" must name the four bytes the CRC is written to.");
            if (hi >= size)
                throw new InvalidOperationException(
                    $"{where}.fields[{i}] ('{src.Type}') at bytes {lo}-{hi} runs past the {size}-byte {(portBlock ? "port block" : "report")}.");

            if (f.Op == FieldOp.Crc32)
            {
                if (src.Scope == null)
                    throw new InvalidOperationException($"{where}.fields[{i}] ('{src.Type}') needs scope, the bytes the CRC covers.");
                // The codec stops the scope at the buffer's end, so a scope
                // that runs past the report covers the report's tail. One that
                // starts past it covers no report byte at all.
                if (src.Scope.From < 0 || src.Scope.To < src.Scope.From || src.Scope.From >= size)
                    throw new InvalidOperationException(
                        $"{where}.fields[{i}] ('{src.Type}').scope {src.Scope.From}-{src.Scope.To} does not start inside the {size}-byte report.");
            }
            if (f.Op == FieldOp.ButtonMask || f.Op == FieldOp.Bitfield)
            {
                if (src.Buttons == null || src.Buttons.Count == 0)
                    throw new InvalidOperationException($"{where}.fields[{i}] ('{src.Type}') needs buttons, one name per bit.");
                int width = f.BitHi - f.BitLo + 1;
                if (src.Buttons.Count > width)
                    throw new InvalidOperationException(
                        $"{where}.fields[{i}] ('{src.Type}') names {src.Buttons.Count} buttons for bits {f.BitLo}-{f.BitHi}, which hold {width}.");
                if (f.Op == FieldOp.Bitfield)
                {
                    for (int j = 0; j < src.Buttons.Count; j++)
                    {
                        string name = src.Buttons[j];
                        if (f.FlagKinds![j] == FlagNone && !string.IsNullOrEmpty(name) && name != "_")
                            throw new InvalidOperationException(
                                $"{where}.fields[{i}] ('{src.Type}').buttons[{j}] '{name}' is not a flag: batteryCharging, batteryFull, " +
                                "micMuted, headphonesConnected, cableConnected. Use \"_\" for a bit that is deliberately unused.");
                    }
                }
            }

            // An input semantic that names nothing the SDK has is a typo the
            // encoder would turn into its fallback. A missing semantic, or one
            // that names another type's value, keeps the type's defined
            // fallback: the axis center, a zero, the left stick, finger 0.
            if (!output && !string.IsNullOrEmpty(src.Semantic) && f.Source == SrcOp.None && Sources(f.Op) is string[] allowed)
                throw new InvalidOperationException(
                    $"{where}.fields[{i}] ('{src.Type}').semantic '{src.Semantic}' names no value the SDK has. " +
                    $"This type reads {string.Join(", ", allowed)}.");
        }
    }

    // The type strings Compile reads, for the message that refuses another.
    private static readonly string[] s_typeNames =
    {
        "uint8", "uint8-axis", "uint8-trigger", "uint8-rolling", "uint8-pressure", "uint8-battery",
        "int16-le", "int16-axis", "int16-pad", "int16-pad-or-stick", "uint16-le", "uint16-trigger",
        "uint16-pressure", "uint10-be", "ds3-battery", "uint32-le", "uint32-rolling", "touchpad-finger",
        "bitfield", "hat-octant", "button-mask", "rgb24", "bytes-passthrough", "bytes-zero", "crc32-le",
        "stick12-pair",
    };

    private static bool UsesRange(FieldOp op)
        => op is FieldOp.Rgb24 or FieldOp.BytesPassthrough or FieldOp.BytesZero;

    /// <summary>The bytes a field writes in a report of
    /// <paramref name="size"/> bytes, as <see cref="VendorBlobCodec"/>
    /// writes them, or (-1, -1) when the field declares no place.</summary>
    internal static (int Lo, int Hi) Extent(in CompiledField f, int size)
    {
        switch (f.Op)
        {
            case FieldOp.Rgb24:
            case FieldOp.BytesPassthrough:
            case FieldOp.BytesZero:
                return f.RangeLo < 0 ? (-1, -1) : (f.RangeLo, f.RangeHi);
            case FieldOp.Crc32:
            {
                // Without a declared place the codec writes the last four
                // bytes of the report.
                int dst = f.CrcDst >= 0 ? f.CrcDst : size - 4;
                return dst < 0 ? (-1, -1) : (dst, dst + 3);
            }
        }
        if (f.B < 0) return (-1, -1);
        int width = f.Op switch
        {
            FieldOp.I16 or FieldOp.I16Axis or FieldOp.I16Pad or FieldOp.I16PadOrStick
                or FieldOp.U16 or FieldOp.U16Trigger or FieldOp.U16Pressure or FieldOp.U10BE => 2,
            FieldOp.Stick12Pair => 3,
            FieldOp.U32 or FieldOp.U32Rolling or FieldOp.TouchpadFinger => 4,
            FieldOp.ButtonMask => f.BitHi / 8 + 1,
            _ => 1,
        };
        return (f.B, f.B + width - 1);
    }

    /// <summary>The semantics an input field of type <paramref name="op"/>
    /// takes its value from, as the encoder's source switch reads them, or
    /// null for a type whose semantic is only a name. Touchpad-finger is not
    /// listed: Compile maps every semantic but touchpadFinger1 to finger
    /// 0.</summary>
    private static string[]? Sources(FieldOp op) => op switch
    {
        FieldOp.U8Axis or FieldOp.I16Axis => s_stickAxes,
        FieldOp.U8Trigger or FieldOp.U16Trigger => s_triggers,
        FieldOp.I16 or FieldOp.U10BE => s_motion,
        FieldOp.U16 or FieldOp.U32 => s_timestamp,
        FieldOp.Stick12Pair => s_sticks,
        FieldOp.I16Pad or FieldOp.I16PadOrStick => s_pads,
        FieldOp.U16Pressure => s_padPressures,
        _ => null,
    };

    private static readonly string[] s_stickAxes = { "leftStickX", "leftStickY", "rightStickX", "rightStickY" };
    private static readonly string[] s_triggers = { "leftTrigger", "rightTrigger" };
    private static readonly string[] s_motion = { "gyroPitch", "gyroYaw", "gyroRoll", "accelX", "accelY", "accelZ" };
    private static readonly string[] s_timestamp = { "sensorTimestamp" };
    private static readonly string[] s_sticks = { "leftStick", "rightStick" };
    private static readonly string[] s_pads = { "leftPadX", "leftPadY", "rightPadX", "rightPadY" };
    private static readonly string[] s_padPressures = { "leftPadPressure", "rightPadPressure" };

    internal static bool TryParseBitRange(string? bits, out int lo, out int hi)
    {
        lo = 0; hi = 7;
        if (string.IsNullOrEmpty(bits)) return false;
        var parts = bits.Split('-');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], out lo) & int.TryParse(parts[1], out hi);
    }

    internal static bool TryParseByteRange(string? bytes, out int lo, out int hi)
    {
        lo = 0; hi = 0;
        if (string.IsNullOrEmpty(bytes)) return false;
        var parts = bytes.Split('-');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], out lo) & int.TryParse(parts[1], out hi);
    }
}
