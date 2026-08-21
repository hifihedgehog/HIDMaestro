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
/// is locked by <c>test/probes/vendor_blob_golden_check</c> (63 golden
/// hashes across all 9 shipped Sony specs, all three directions). Change
/// behavior here only together with a deliberate golden regeneration.</summary>
internal sealed class VendorBlobProgram
{
    /// <summary>Field type, numeric. One value per wire-format family the
    /// codec understands; <see cref="FieldOp.Unknown"/> preserves the
    /// string implementation's silent-no-op contract for unrecognized
    /// type strings.</summary>
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
    }

    // Bitfield flag sources (input direction), numeric. Index-aligned with
    // the compiled FlagKinds array.
    public const byte FlagNone = 0, FlagCharging = 1, FlagFull = 2,
                      FlagMic = 3, FlagHeadphones = 4;

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

        public CompiledField(FieldSpec src, FieldOp op, SrcOp source, int b,
                             int rangeLo, int rangeHi, int bitLo, int bitHi, bool hasBits,
                             int center, int neutral, byte initial, int stride,
                             int crcDst, string rollKey, ulong[]? buttonBits, byte[]? flagKinds)
        {
            Src = src; Op = op; Source = source; B = b;
            RangeLo = rangeLo; RangeHi = rangeHi; BitLo = bitLo; BitHi = bitHi; HasBits = hasBits;
            Center = center; Neutral = neutral; Initial = initial; Stride = stride;
            CrcDst = crcDst; RollKey = rollKey; ButtonBits = buttonBits; FlagKinds = flagKinds;
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
                            buttonBits[j] = (uint)btn;
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
                            _                     => FlagNone,
                        };
                    }
                }
            }

            fields[i] = new CompiledField(f, op, source, b, rangeLo, rangeHi,
                bitLo, bitHi, hasBits,
                f.Center ?? 128, f.NeutralValue ?? 8,
                (byte)(f.Initial ?? 0), f.Stride ?? 1,
                crcDst, rollKey, buttonBits, flagKinds);

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
