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
    /// type strings in the codec. Since v2.0.0 a create refuses a profile
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
        // button's 0..255 byte and a 10-bit motion sensor in a big-endian
        // 16-bit field.
        U8Pressure, U10BE,
        // v2.0.0: a byte chosen from a table by the battery level, the
        // charging flag and the full flag, and an output field decoded from
        // bit groups.
        U8LevelMap, PackedUint,
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
        // v2.0.0: the calibrated motion channel, g and degrees per second
        // in SDL's sensor frame (HMGamepadState.AccelGX to GyroDpsZ), which
        // an int16-le field with a scale writes as counts.
        AccelGX, AccelGY, AccelGZ, GyroDpsX, GyroDpsY, GyroDpsZ,
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

    /// <summary>One part of a packed-uint field, compiled: its bit groups,
    /// its frames, and the map from its value to 0..255.</summary>
    public readonly struct PackedPart
    {
        public readonly int[] PieceAt;      // bit offset of each group, lowest first
        public readonly int[] PieceCount;   // width of each group
        public readonly int Frames;         // frames read, >= 1
        public readonly int StrideBits;     // between frames
        public readonly long Offset;
        public readonly long Lo, Hi;        // inclusive range, after the offset
        public readonly double Numerator, Denominator;
        public readonly bool Down;          // truncate, else nearest ties to even

        public PackedPart(int[] pieceAt, int[] pieceCount, int frames, int strideBits, long offset,
                          long lo, long hi, double numerator, double denominator, bool down)
        {
            PieceAt = pieceAt; PieceCount = pieceCount; Frames = frames; StrideBits = strideBits;
            Offset = offset; Lo = lo; Hi = hi; Numerator = numerator; Denominator = denominator; Down = down;
        }

        /// <summary>The bits the part reads, from its first to its last, in
        /// a report: the report must reach the last.</summary>
        /// <summary>The last bit the part reads, in long: offsets near
        /// int.MaxValue must not wrap back inside the report.</summary>
        public long LastBit
        {
            get
            {
                long last = 0;
                for (int i = 0; i < PieceAt.Length; i++) last = Math.Max(last, (long)PieceAt[i] + PieceCount[i] - 1);
                return last + (long)(Frames - 1) * StrideBits;
            }
        }
    }

    /// <summary>The parameters only a few fields set: a calibrated field's
    /// scale and repeat, a level map's charging value, a packed-uint's
    /// parts.</summary>
    public sealed class FieldExtras
    {
        public double ScaleNumerator;
        public double ScaleDenominator = 1;
        public bool Negate;
        public int RepeatCount = 1;
        public int RepeatStride;
        public int ChargingValue = -1;
        public PackedPart[]? Parts;
        /// <summary>An int16-le field with a center: its words are unsigned,
        /// clamped to 0 to 65535 as uint10-be clamps to its range.</summary>
        public bool OffsetBinary;
    }

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
        public readonly int Center;         // uint8-axis center (default 128), uint10-be zero (512), int16-le offset (0)
        public readonly int Neutral;        // hat neutral (default 8)
        public readonly byte Initial;
        public readonly int Stride;
        public readonly int CrcDst;         // resolved crc dest start, -1 = end-4
        public readonly string RollKey;     // rolling-counter key, for the output direction
        public readonly ulong[]? ButtonBits;// per position: HMButton mask | sentinel, 0 = skip
        public readonly byte[]? FlagKinds;  // bitfield: Flag* per position
        public readonly uint Divisor;       // uint16-le sensor timestamp, >= 1
        public readonly int FullValue;      // uint8-battery and uint8-level-map while full, -1 when absent
        public readonly int Resolution;     // uint10-be wire counts per unit, >= 1
        public readonly int SourceResolution; // uint10-be source counts per unit, >= 1
        public readonly bool Invert;        // wire axis opposite the SDK's

        // v2.0.0: the vocabulary's parameters, resolved to their defaults.
        // The ones a frame reads on common fields are stored here. The rest,
        // which only a few fields set, sit in Extras, so the field array an
        // encode walks stays as short as it can.
        /// <summary>Wire value at the SDK's 0 and at 1, or the released and
        /// pressed values of a pad pressure.</summary>
        public int Min { get; init; }
        public int Max { get; init; }
        /// <summary>The d-pad from HatDegrees, HatHundredths, HatRaw or Hat,
        /// the first one set.</summary>
        public bool DpadResolved { get; init; }
        /// <summary>A sensor timestamp written from the encoder state's
        /// accumulator (<c>"clock": "accumulate"</c>).</summary>
        public bool Accumulate { get; init; }
        /// <summary>The rolling field's counter, an index into the encoder
        /// state's counters, -1 for any other field.</summary>
        public int CounterSlot { get; init; }
        /// <summary>The scale, repeat, level table and packed parts of the
        /// few fields that set them, null for every other field.</summary>
        public FieldExtras? Extras { get; init; }
        /// <summary>A button-mask's buttons that are one HMButton bit each,
        /// grouped by the distance from their HMButton bit to their wire bit:
        /// each group is one masked shift of the button mask. Null for any
        /// other field.</summary>
        public ButtonGroup[]? ButtonGroups { get; init; }
        /// <summary>A button-mask's other positions, each read on its own:
        /// the trigger, d-pad and pad-touch bits and any position that names
        /// more than one HMButton bit. Null for any other field.</summary>
        public int[]? ButtonSingles { get; init; }

        /// <summary>A calibrated int16-le field's counts per unit, as a
        /// ratio. Numerator 0 for any other field.</summary>
        public double ScaleNumerator => Extras?.ScaleNumerator ?? 0;
        public double ScaleDenominator => Extras?.ScaleDenominator ?? 1;
        public bool Negate => Extras?.Negate ?? false;
        /// <summary>Times the field is written, and bytes between.</summary>
        public int RepeatCount => Extras?.RepeatCount ?? 1;
        public int RepeatStride => Extras?.RepeatStride ?? 0;
        /// <summary>A uint8-level-map's table, stored here, since the frame
        /// reads it on every report.</summary>
        public byte[]? Levels { get; init; }
        public int ChargingValue => Extras?.ChargingValue ?? -1;
        public PackedPart[]? Parts => Extras?.Parts;

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
            Min = 0; Max = 0; DpadResolved = false; Accumulate = false; CounterSlot = -1; Extras = null;
        }
    }

    /// <summary>Buttons whose wire bit is their HMButton bit moved by
    /// <see cref="Shift"/>: <c>(mask &amp; Mask)</c> shifted left by Shift,
    /// or right by -Shift.</summary>
    public readonly record struct ButtonGroup(int Shift, ulong Mask);

    /// <summary>The groups and singles of a button-mask field's positions,
    /// in the range its bits declare.</summary>
    internal static (ButtonGroup[] Groups, int[] Singles) GroupButtons(ulong[] bits, int bitLo, int bitHi)
    {
        var groups = new List<ButtonGroup>();
        var singles = new List<int>();
        for (int i = 0; i < bits.Length && bitLo + i <= bitHi; i++)
        {
            ulong b = bits[i];
            if (b == 0) continue;
            if (b >= 1UL << 32 || (b & (b - 1)) != 0) { singles.Add(i); continue; }
            int shift = bitLo + i - System.Numerics.BitOperations.TrailingZeroCount(b);
            int at = groups.FindIndex(g => g.Shift == shift);
            if (at < 0) groups.Add(new ButtonGroup(shift, b));
            else groups[at] = groups[at] with { Mask = groups[at].Mask | b };
        }
        return (groups.ToArray(), singles.ToArray());
    }

    public readonly CompiledField[] Fields;

    /// <summary>Number of fields that produce a decode-dict entry, so
    /// <see cref="VendorBlobCodec.Decode"/> can pre-size its dictionary.</summary>
    public readonly int DecodeFieldCount;

    /// <summary>The rolling counters an encode advances: one per distinct
    /// semantic and width, as the string-keyed counters were. Each starts
    /// at the initial value of the first field that uses it.</summary>
    public readonly uint[] CounterInitials;

    /// <summary>True when a field reads the resolved d-pad, so an encode
    /// resolves it once.</summary>
    public readonly bool ResolvesDpad;

    /// <summary>True when a field accumulates the sensor timestamp, so an
    /// encode advances the accumulator once.</summary>
    public readonly bool Accumulates;

    /// <summary>The accumulator's nominal interval in 1/3 microsecond
    /// ticks, the report's idleFrameIntervalMs, added for a consumer stamp
    /// that restarted or ran backward.</summary>
    public readonly uint NominalTicks;

    /// <summary>The accumulating field's holdWhile condition, as written, or
    /// null. The controller compiles it against the protocol's registers.</summary>
    public readonly string? HoldWhile;

    private VendorBlobProgram(CompiledField[] fields, int decodeFieldCount, uint[] counterInitials, bool resolvesDpad,
                              bool accumulates, uint nominalTicks, string? holdWhile = null)
    {
        HoldWhile = holdWhile;
        Fields = fields;
        DecodeFieldCount = decodeFieldCount;
        CounterInitials = counterInitials;
        ResolvesDpad = resolvesDpad;
        Accumulates = accumulates;
        NominalTicks = nominalTicks;
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
        var counterSlots = new Dictionary<(string Key, bool Wide), int>();
        var counterInitials = new List<uint>();
        bool resolvesDpad = false, accumulates = false;
        string? holdWhile = null;
        for (int i = 0; i < spec.Fields.Count; i++)
        {
            var f = spec.Fields[i];
            // v2.0.0 drops the one battery type written for one device. Its
            // table is profile data now.
            if (f.Type == "ds3-battery")
                throw new InvalidOperationException(
                    $"fields[{i}].type 'ds3-battery' is not a field type. Write it as type 'uint8-level-map' with " +
                    "levels [1, 2, 2, 2, 3, 3, 4, 4, 4, 5, 5], chargingValue 238 and fullValue 239.");
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
                "uint8-level-map"   => FieldOp.U8LevelMap,
                "packed-uint"       => FieldOp.PackedUint,
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
                "accelGX"             => SrcOp.AccelGX,
                "accelGY"             => SrcOp.AccelGY,
                "accelGZ"             => SrcOp.AccelGZ,
                "gyroDpsX"            => SrcOp.GyroDpsX,
                "gyroDpsY"            => SrcOp.GyroDpsY,
                "gyroDpsZ"            => SrcOp.GyroDpsZ,
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

            // One counter per distinct key and width, as the encoder state's
            // two string-keyed dictionaries kept them until v2.0.0: two
            // rolling fields that share a semantic share a counter.
            int counterSlot = -1;
            if (op is FieldOp.U8Rolling or FieldOp.U32Rolling)
            {
                var key = (rollKey, op == FieldOp.U32Rolling);
                if (!counterSlots.TryGetValue(key, out counterSlot))
                {
                    counterSlot = counterInitials.Count;
                    counterSlots[key] = counterSlot;
                    // Both widths start from the field's initial byte, as
                    // the string-keyed counters started from the compiled
                    // field's Initial.
                    counterInitials.Add((byte)(f.Initial ?? 0));
                }
            }

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
                        // v2.0.0: "#n" is HMButton bit n, for a profile whose
                        // buttons are layout positions rather than names.
                        if (name.Length > 1 && name[0] == '#')
                        {
                            if (int.TryParse(name.AsSpan(1), System.Globalization.NumberStyles.None,
                                             System.Globalization.CultureInfo.InvariantCulture, out int bit) && bit < 32)
                            {
                                buttonBits[j] = 1UL << bit;
                                continue;
                            }
                            throw new InvalidOperationException(
                                $"Button '{name}' at index {j} of the button mask at byte {f.Byte} is not '#' and an HMButton bit from 0 to 31.");
                        }
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
                            "RIGHTPAD_TOUCH) or '#' and a bit number. Use \"_\" for a slot that is " +
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
            if (op == FieldOp.Battery && f.FullValue is int fv && (fv < 0 || fv >= (1 << (bitHi - bitLo + 1))))
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

            var (defMin, defMax) = DefaultRange(op);
            bool dpad = f.Dpad == "resolved";
            resolvesDpad |= dpad;
            bool accumulate = f.Clock == "accumulate";
            accumulates |= accumulate;
            if (accumulate && f.HoldWhile != null) holdWhile = f.HoldWhile;
            var (buttonGroups, buttonSingles) = buttonBits != null
                ? GroupButtons(buttonBits, bitLo, bitHi)
                : (null, null);
            fields[i] = new CompiledField(f, op, source, b, rangeLo, rangeHi,
                bitLo, bitHi, hasBits,
                f.Center ?? (op == FieldOp.U10BE ? 512 : op == FieldOp.I16 ? 0 : 128), f.NeutralValue ?? 8,
                (byte)(f.Initial ?? 0), f.Stride ?? 1,
                crcDst, rollKey, buttonBits, flagKinds,
                (uint)divisor, fullValue,
                resolution, sourceResolution, f.Invert == true)
            {
                Min = f.Min ?? defMin,
                Max = f.Max ?? defMax,
                DpadResolved = dpad,
                ButtonGroups = buttonGroups,
                ButtonSingles = buttonSingles,
                CounterSlot = counterSlot,
                Accumulate = accumulate,
                Levels = f.Levels?.ConvertAll(x => (byte)x).ToArray(),
                // An int16-le field with a center writes offset binary out of
                // line too, so the encode loop stays as it was for the rest.
                Extras = f.Scale == null && f.Negate == null && f.Repeat == null && f.Levels == null
                         && f.ChargingValue == null && op != FieldOp.PackedUint
                         && !(op == FieldOp.I16 && f.Center != null) ? null : new FieldExtras
                {
                    ScaleNumerator = f.Scale?.Numerator ?? 0,
                    ScaleDenominator = f.Scale?.Denominator ?? 1,
                    Negate = f.Negate == true,
                    RepeatCount = f.Repeat is { Count: 2 } r ? r[0] : 1,
                    RepeatStride = f.Repeat is { Count: 2 } s ? s[1] : 0,
                    ChargingValue = f.ChargingValue ?? -1,
                    OffsetBinary = op == FieldOp.I16 && f.Center != null,
                    Parts = op == FieldOp.PackedUint ? CompileParts(f, i) : null,
                },
            };

            // Mirror of Decode's per-type "produces a dict entry" predicate,
            // used only for dictionary pre-sizing (over-count is harmless).
            bool producesEntry = op switch
            {
                FieldOp.U8Const or FieldOp.U8Rolling or FieldOp.U8Axis
                    or FieldOp.U8Trigger or FieldOp.HatOctant => f.Byte != null && f.Semantic != null,
                FieldOp.ButtonMask => f.Byte != null && f.Buttons != null,
                FieldOp.Rgb24 or FieldOp.BytesPassthrough => hasBytes && f.Semantic != null,
                FieldOp.PackedUint => f.Semantic != null,
                _ => false,
            };
            if (producesEntry) decodeCount++;
        }
        return new VendorBlobProgram(fields, decodeCount, counterInitials.ToArray(), resolvesDpad,
            accumulates, (uint)Math.Clamp((long)spec.IdleFrameIntervalMs * 3000, 0, uint.MaxValue), holdWhile);
    }

    /// <summary>The wire values at the SDK's 0 and 1 a type takes when its
    /// field gives no <c>min</c> and <c>max</c>: the full range of its
    /// width, symmetric for the signed axes.</summary>
    private static (int Min, int Max) DefaultRange(FieldOp op) => op switch
    {
        FieldOp.U8Axis or FieldOp.U8Trigger => (0, 255),
        FieldOp.Stick12Pair => (0, 4095),
        FieldOp.I16Axis or FieldOp.I16PadOrStick => (-32767, 32767),
        FieldOp.U16Trigger or FieldOp.U16Pressure => (0, 65535),
        _ => (0, 0),
    };

    private static PackedPart[] CompileParts(FieldSpec f, int i)
    {
        var specs = f.Parts ?? throw new InvalidOperationException($"fields[{i}] ('packed-uint') needs parts.");
        var parts = new PackedPart[specs.Count];
        for (int p = 0; p < specs.Count; p++)
        {
            var s = specs[p] ?? throw new InvalidOperationException($"fields[{i}].parts[{p}] is null.");
            var at = new int[s.Pieces?.Count ?? 0];
            var count = new int[at.Length];
            for (int k = 0; k < at.Length; k++)
            {
                var piece = s.Pieces![k] ?? throw new InvalidOperationException($"fields[{i}].parts[{p}].pieces[{k}] is null.");
                at[k] = piece.At;
                count[k] = piece.Count;
            }
            parts[p] = new PackedPart(at, count,
                s.Frames is { Count: 2 } fr ? fr[0] : 1, s.Frames is { Count: 2 } fs ? (int)Math.Clamp(fs[1] * 8L, int.MinValue, int.MaxValue) : 0,
                s.Offset ?? 0,
                s.Range is { Count: 2 } rl ? rl[0] : long.MinValue, s.Range is { Count: 2 } rh ? rh[1] : long.MaxValue,
                s.Scale?.Numerator ?? 1, s.Scale?.Denominator ?? 1,
                s.Round == "down");
        }
        return parts;
    }

    /// <summary>v2.0.0: refuse a field the codec would skip, cut short or
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
            CheckVocabulary(in f, i, size, where, output);
            // The field starts from one byte (CompiledField.Initial), a
            // rolling counter of either width too, so a value past it was
            // cut to its low byte.
            if (src.Initial is < 0 or > 255)
                throw new InvalidOperationException(
                    $"{where}.fields[{i}].initial {src.Initial} is not a byte. A field, a rolling counter's included, starts from 0 to 255.");
            if (src.HoldWhile != null && src.Clock != "accumulate")
                throw new InvalidOperationException($"{where}.fields[{i}].holdWhile goes with \"clock\": \"accumulate\", the stamp it holds.");
            if (src.Clock != null)
            {
                if (src.Clock != "accumulate")
                    throw new InvalidOperationException($"{where}.fields[{i}].clock \"{src.Clock}\": the one value is \"accumulate\".");
                if (output || f.Op is not (FieldOp.U16 or FieldOp.U32) || f.Source != SrcOp.SensorTimestamp)
                    throw new InvalidOperationException($"{where}.fields[{i}].clock: only an input uint16-le or uint32-le sensorTimestamp accumulates.");
                if (spec.IdleFrameIntervalMs < 1)
                    throw new InvalidOperationException(
                        $"{where}.fields[{i}].clock \"accumulate\" needs the report's idleFrameIntervalMs, the interval it adds for a consumer stamp that runs backward.");
            }

            if (f.Op == FieldOp.PackedUint) continue;
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
            if (!output && !string.IsNullOrEmpty(src.Semantic) && f.Source == SrcOp.None && Sources(f.Op, f.ScaleNumerator != 0) is string[] allowed)
                throw new InvalidOperationException(
                    $"{where}.fields[{i}] ('{src.Type}').semantic '{src.Semantic}' names no value the SDK has. " +
                    $"This type reads {string.Join(", ", allowed)}.");
        }
    }

    /// <summary>v2.0.0: the vocabulary's parameters, each refused on a type
    /// that ignores it and outside the values it takes.</summary>
    private static void CheckVocabulary(in CompiledField f, int i, int size, string where, bool output)
    {
        var src = f.Src;
        string at = $"{where}.fields[{i}]";

        // An output field decodes its byte over the type's full range, and
        // only uint8-axis decodes inverted (VendorBlobCodec.Decode), so the
        // parameters it would ignore are refused.
        if (output)
        {
            if (src.Min != null || src.Max != null)
                throw new InvalidOperationException($"{at} ('{src.Type}').min and .max: an output field decodes its type's full range and ignores them.");
            if (src.Dpad != null)
                throw new InvalidOperationException($"{at} ('{src.Type}').dpad: an output field reads no d-pad.");
            if (src.Invert == true && f.Op != FieldOp.U8Axis)
                throw new InvalidOperationException($"{at} ('{src.Type}').invert: of the output types only uint8-axis decodes inverted.");
        }

        // What v1 did without a key, a profile now states, so no profile
        // written for v1 changes its bytes unseen: a uint16-trigger or
        // uint16-pressure without max went to 32767, and every int16 Y,
        // stick or pad, was negated.
        if (!output && (f.Op is FieldOp.U16Trigger or FieldOp.U16Pressure) && src.Max == null)
            throw new InvalidOperationException(
                $"{at} ('{src.Type}').max is required. v1 wrote a full pull or press as 32767: add \"max\": 32767 for that, or \"max\": 65535 for the full width.");
        bool ySource = f.Source is SrcOp.LeftStickY or SrcOp.RightStickY or SrcOp.LeftPadY or SrcOp.RightPadY;
        if (!output && (f.Op is FieldOp.I16Axis or FieldOp.I16Pad or FieldOp.I16PadOrStick) && ySource && src.Invert == null)
            throw new InvalidOperationException(
                $"{at} ('{src.Type}', {src.Semantic}).invert is required. v1 negated every int16 Y: add \"invert\": true for that, or \"invert\": false for a wire that grows downward.");

        bool linear = f.Op is FieldOp.U8Axis or FieldOp.U8Trigger or FieldOp.Stick12Pair or FieldOp.I16Axis
            or FieldOp.I16PadOrStick or FieldOp.U16Trigger or FieldOp.U16Pressure;
        if ((src.Min != null || src.Max != null) && !linear)
            throw new InvalidOperationException($"{at} ('{src.Type}').min and .max: this type carries no normalized value and ignores them.");
        if (linear)
        {
            if (f.Min == f.Max)
                throw new InvalidOperationException($"{at}.min {f.Min} equals max. A field needs two ends.");
            if (f.Op == FieldOp.U8Axis && f.Center != 128 && (src.Min != null || src.Max != null))
                throw new InvalidOperationException($"{at}.center {f.Center} sets the range center - 127 to center + 127. Give center or min and max, not both.");
            var (lo, hi) = f.Op switch
            {
                FieldOp.U8Axis or FieldOp.U8Trigger => (0, 255),
                FieldOp.Stick12Pair => (0, 4095),
                FieldOp.U16Trigger or FieldOp.U16Pressure => (0, 65535),
                _ => (-32768, 32767),
            };
            if (f.Min < lo || f.Min > hi || f.Max < lo || f.Max > hi)
                throw new InvalidOperationException($"{at}.min {f.Min} and .max {f.Max}: this type's wire holds {lo} to {hi}.");
        }
        bool inverts = f.Op is FieldOp.U8Axis or FieldOp.U8Trigger or FieldOp.Stick12Pair or FieldOp.I16Axis
            or FieldOp.I16Pad or FieldOp.I16PadOrStick or FieldOp.U16Trigger or FieldOp.U10BE;
        if (src.Invert == true && !inverts)
            throw new InvalidOperationException($"{at} ('{src.Type}').invert: this type carries no axis and ignores it.");

        bool calibrated = f.Source is >= SrcOp.AccelGX and <= SrcOp.GyroDpsZ;
        if (src.Scale != null)
        {
            if (f.Op != FieldOp.I16 || !calibrated)
                throw new InvalidOperationException($"{at}.scale: only an int16-le field of a calibrated source, accelGX to gyroDpsZ, takes a scale.");
            if (!(f.ScaleNumerator != 0) || !double.IsFinite(f.ScaleNumerator) || !(f.ScaleDenominator > 0) || !double.IsFinite(f.ScaleDenominator))
                throw new InvalidOperationException($"{at}.scale: the counts per unit are a finite nonzero number, over a positive denominator.");
        }
        else if (calibrated && !output)
            throw new InvalidOperationException($"{at}.semantic '{src.Semantic}' is a calibrated value: give scale, its counts per unit.");
        // int16-le's center is the wire value of a zero reading: a sensor
        // that sends offset binary, unsigned with zero at 0x8000, sets 32768.
        if (f.Op == FieldOp.I16 && src.Center != null && (src.Center < 0 || src.Center > 65535))
            throw new InvalidOperationException($"{at}.center {src.Center}: an int16-le field's center is a 16-bit wire value, 0 to 65535.");
        if (output && f.Op == FieldOp.I16 && src.Center != null)
            throw new InvalidOperationException($"{at} ('{src.Type}').center: an output int16-le field reads two's complement and ignores a center.");
        if ((src.Negate != null || src.Repeat != null) && src.Scale == null)
            throw new InvalidOperationException($"{at}.negate and .repeat go with scale.");
        if (src.Repeat != null)
        {
            if (src.Repeat.Count != 2 || f.RepeatCount < 1 || f.RepeatStride < 2)
                throw new InvalidOperationException($"{at}.repeat is [count, stride]: one or more frames, at least 2 bytes apart.");
            long last = f.B + (long)(f.RepeatCount - 1) * f.RepeatStride + 1;
            if (f.B >= 0 && last >= size)
                throw new InvalidOperationException($"{at}.repeat's last frame ends at byte {last}, past the {size}-byte report.");
        }

        if (src.Dpad != null)
        {
            if (src.Dpad != "resolved")
                throw new InvalidOperationException($"{at}.dpad \"{src.Dpad}\": the one value is \"resolved\".");
            bool dpadBits = f.ButtonBits != null && Array.Exists(f.ButtonBits,
                x => x is ButtonDpadUp or ButtonDpadDown or ButtonDpadLeft or ButtonDpadRight);
            if (!(f.Op == FieldOp.HatOctant || (f.Op == FieldOp.ButtonMask && dpadBits)))
                throw new InvalidOperationException($"{at}.dpad: only a hat-octant or a button-mask with DPAD_* names reads the d-pad.");
        }

        if (f.Op == FieldOp.U8LevelMap)
        {
            if (src.Levels == null || src.Levels.Count != 11)
                throw new InvalidOperationException($"{at}.levels: eleven bytes, one for each battery level from 0 to 10.");
            for (int k = 0; k < 11; k++)
                if (src.Levels[k] is < 0 or > 255)
                    throw new InvalidOperationException($"{at}.levels[{k}] {src.Levels[k]} is not a byte.");
            if (src.ChargingValue is < 0 or > 255) throw new InvalidOperationException($"{at}.chargingValue {src.ChargingValue} is not a byte.");
            if (src.FullValue is < 0 or > 255) throw new InvalidOperationException($"{at}.fullValue {src.FullValue} is not a byte.");
        }
        else
        {
            if (src.Levels != null || src.ChargingValue != null)
                throw new InvalidOperationException($"{at} ('{src.Type}').levels and .chargingValue go with uint8-level-map.");
            if (src.FullValue != null && f.Op != FieldOp.Battery)
                throw new InvalidOperationException($"{at} ('{src.Type}').fullValue goes with uint8-battery and uint8-level-map.");
        }

        if (f.Op == FieldOp.PackedUint)
        {
            if (!output)
                throw new InvalidOperationException($"{at}: packed-uint decodes an output report. An input report cannot carry one.");
            if (string.IsNullOrEmpty(src.Semantic))
                throw new InvalidOperationException($"{at} ('packed-uint') needs a semantic, the name its value is reported under.");
            if (f.Parts!.Length == 0)
                throw new InvalidOperationException($"{at}.parts: at least one part.");
            for (int p = 0; p < f.Parts.Length; p++)
            {
                ref readonly var part = ref f.Parts[p];
                var partSpec = src.Parts![p];
                long bits = 0;
                if (part.PieceAt.Length == 0)
                    throw new InvalidOperationException($"{at}.parts[{p}].pieces: at least one bit group.");
                for (int k = 0; k < part.PieceAt.Length; k++)
                {
                    if (part.PieceAt[k] < 0 || part.PieceCount[k] < 1)
                        throw new InvalidOperationException($"{at}.parts[{p}].pieces[{k}]: a bit offset of 0 or more and a count of 1 or more.");
                    bits += part.PieceCount[k];
                }
                if (bits > 32)
                    throw new InvalidOperationException($"{at}.parts[{p}].pieces hold {bits} bits. A part reads at most 32.");
                if (partSpec.Frames != null && (partSpec.Frames.Count != 2 || part.Frames < 1 || part.StrideBits < 8))
                    throw new InvalidOperationException($"{at}.parts[{p}].frames is [count, stride]: one or more frames, a stride of 1 byte or more.");
                if (partSpec.Range != null && (partSpec.Range.Count != 2 || part.Lo > part.Hi))
                    throw new InvalidOperationException($"{at}.parts[{p}].range is [low, high], low at most high.");
                if (!(part.Numerator != 0) || !double.IsFinite(part.Numerator) || !(part.Denominator > 0) || !double.IsFinite(part.Denominator))
                    throw new InvalidOperationException($"{at}.parts[{p}].scale: a finite nonzero number, over a positive denominator.");
                if (partSpec.Round is not (null or "down" or "nearest"))
                    throw new InvalidOperationException($"{at}.parts[{p}].round \"{partSpec.Round}\": \"down\" or \"nearest\".");
                if (part.LastBit >= size * 8L)
                    throw new InvalidOperationException($"{at}.parts[{p}] reads bit {part.LastBit}, past the {size}-byte report.");
            }
        }
        else if (src.Parts != null)
        {
            throw new InvalidOperationException($"{at} ('{src.Type}').parts go with packed-uint.");
        }
    }

    // The type strings Compile reads, for the message that refuses another.
    private static readonly string[] s_typeNames =
    {
        "uint8", "uint8-axis", "uint8-trigger", "uint8-rolling", "uint8-pressure", "uint8-battery", "uint8-level-map",
        "int16-le", "int16-axis", "int16-pad", "int16-pad-or-stick", "uint16-le", "uint16-trigger",
        "uint16-pressure", "uint10-be", "uint32-le", "uint32-rolling", "touchpad-finger",
        "bitfield", "hat-octant", "button-mask", "rgb24", "bytes-passthrough", "bytes-zero", "crc32-le",
        "stick12-pair", "packed-uint",
    };

    private static bool UsesRange(FieldOp op)
        => op is FieldOp.Rgb24 or FieldOp.BytesPassthrough or FieldOp.BytesZero;

    /// <summary>The bytes a field writes in a report of
    /// <paramref name="size"/> bytes, as <see cref="VendorBlobCodec"/>
    /// writes them, or (-1, -1) when the field declares no place. A
    /// packed-uint field writes none.</summary>
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
                return dst < 0 ? (-1, -1) : (dst, (int)Math.Min(int.MaxValue, dst + 3L));
            }
            case FieldOp.PackedUint:
                return (-1, -1);
        }
        if (f.B < 0) return (-1, -1);
        long width = f.Op switch
        {
            FieldOp.I16 or FieldOp.I16Axis or FieldOp.I16Pad or FieldOp.I16PadOrStick
                or FieldOp.U16 or FieldOp.U16Trigger or FieldOp.U16Pressure or FieldOp.U10BE => 2,
            FieldOp.Stick12Pair => 3,
            FieldOp.U32 or FieldOp.U32Rolling or FieldOp.TouchpadFinger => 4,
            FieldOp.ButtonMask => f.BitHi / 8 + 1,
            _ => 1,
        };
        // A repeated motion field's extent runs to its last frame. In long,
        // held to int.MaxValue: an offset near it must not wrap back inside
        // the report and pass the check.
        if (f.RepeatCount > 1) width += (long)(f.RepeatCount - 1) * f.RepeatStride;
        return (f.B, (int)Math.Min(int.MaxValue, f.B + width - 1));
    }

    /// <summary>The semantics an input field of type <paramref name="op"/>
    /// takes its value from, as the encoder's source switch reads them, or
    /// null for a type whose semantic is only a name. Touchpad-finger is not
    /// listed: Compile maps every semantic but touchpadFinger1 to finger
    /// 0.</summary>
    private static string[]? Sources(FieldOp op, bool scaled) => op switch
    {
        FieldOp.U8Axis or FieldOp.I16Axis => s_stickAxes,
        FieldOp.U8Trigger or FieldOp.U16Trigger => s_triggers,
        FieldOp.I16 => scaled ? s_calibrated : s_motion,
        FieldOp.U10BE => s_motion,
        FieldOp.U16 or FieldOp.U32 => s_timestamp,
        FieldOp.Stick12Pair => s_sticks,
        FieldOp.I16Pad or FieldOp.I16PadOrStick => s_pads,
        FieldOp.U16Pressure => s_padPressures,
        _ => null,
    };

    private static readonly string[] s_stickAxes = { "leftStickX", "leftStickY", "rightStickX", "rightStickY" };
    private static readonly string[] s_triggers = { "leftTrigger", "rightTrigger" };
    private static readonly string[] s_motion = { "gyroPitch", "gyroYaw", "gyroRoll", "accelX", "accelY", "accelZ" };
    private static readonly string[] s_calibrated = { "accelGX", "accelGY", "accelGZ", "gyroDpsX", "gyroDpsY", "gyroDpsZ" };
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
