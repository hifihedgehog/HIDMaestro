// State programs: a device's state block built from HMGamepadState and the
// six slot values SubmitState resolves (design note, mechanism 2, the build
// step). A profile writes its state block in the codec's vocabulary, and
// StateLowering turns that list into these ops, so the block is generated
// and bound once per controller like a report program. Each op computes what
// the codec's field writes, with the one arithmetic (WireArith): rounding to
// nearest with ties to even, and a NaN slot taking the field's rest value.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace HIDMaestro.Internal.Programs;

/// <summary>A float a state program reads: one of the six slots SubmitState
/// resolves, in [0, 1] (NaN for an axis left unwritten), or a calibrated
/// motion value of HMGamepadState.</summary>
internal enum GpFloat : byte
{
    LeftStickX, LeftStickY, RightStickX, RightStickY, LeftTrigger, RightTrigger,
    AccelGX, AccelGY, AccelGZ, GyroDpsX, GyroDpsY, GyroDpsZ,
}

/// <summary>A one-bit value a state program reads: a trigger past the first
/// step of its 8-bit value (the codec's TriggerOn, v x 255 above 0.5, for a
/// button mask without a triggerThreshold, see GpTriggerBitOp), a
/// direction of HMGamepadState.Hat (a diagonal sets both of its
/// components), a flag of HMGamepadState, or a direction resolved from
/// HatDegrees, HatHundredths, HatRaw or Hat, the first set, as the codec
/// resolves a field with "dpad": "resolved" (VendorBlobCodec.ResolveDpad).</summary>
internal enum GpFlag : byte
{
    LeftTriggerOn, RightTriggerOn, DpadUp, DpadDown, DpadLeft, DpadRight,
    Finger0Active, Finger1Active, BatteryCharging, BatteryFull, MicMuted, HeadphonesConnected, CableConnected,
    DpadUpResolved, DpadDownResolved, DpadLeftResolved, DpadRightResolved,
}

/// <summary>An integer of HMGamepadState written as it is: the raw motion
/// values (int16), the consumer's sensor stamp (uint32) and the touchpad's
/// packet counter (byte).</summary>
internal enum GpRaw : byte { GyroPitch, GyroYaw, GyroRoll, AccelX, AccelY, AccelZ, SensorTimestamp, TouchpadPacketCounter }

/// <summary>A byte of HMGamepadState a table maps: the battery level or the
/// hat.</summary>
internal enum GpByte : byte { BatteryLevel, Hat }

/// <summary>A pressure-sensitive button: its pressure byte, or 255 while it
/// is pressed and its pressure is 0.</summary>
internal enum GpPressure : byte { A, B, X, Y, LeftBumper, RightBumper, DpadUp, DpadRight, DpadDown, DpadLeft }

/// <summary>How a linear value is stored: one byte clamped to 0..255, one
/// byte as its low 8 bits, or two bytes, its low 16 bits little-endian.</summary>
internal enum GpStore : byte { ByteClamped, Byte, Word }

/// <summary>HMButton bits to report bits: pairs of (HMButton bit, report
/// bit). An OR op: each report bit it names is ORed into its byte.</summary>
internal sealed record GpButtonsOp(int[] Moves) : Op;

/// <summary>Flags to report bits, flag i to report bit Bits[i]. An OR op.</summary>
internal sealed record GpFlagsOp(GpFlag[] Flags, int[] Bits) : Op;

/// <summary>A trigger's digital bit at a threshold (the codec's
/// triggerThreshold): report bit <see cref="Bit"/> is set while
/// WireArith.Linear(src, 0, max, false, 0), the trigger on a scale of
/// <see cref="Max"/> counts, is at least <see cref="At"/>. An OR op.</summary>
internal sealed record GpTriggerBitOp(GpFloat Src, int Max, int At, int Bit) : Op;

/// <summary>WireArith.Linear(src, min, max, invert, rest): a NaN takes rest,
/// the value is clamped to [0, 1], inverted if asked, and min + RoundHalfEven
/// of it times max - min, stored as <see cref="Store"/> says.</summary>
internal sealed record GpLinearOp(GpFloat Src, int Min, int Max, bool Invert, double Rest, GpStore Store, int Dst) : Op;

/// <summary>Two 12-bit axes in three bytes, as the codec's stick12-pair
/// writes them: x and y each WireArith.Linear(slot, min, max, invert, 0.5),
/// y inverted if asked, x's low 8 bits at Dst, x's high 4 and y's low 4 at
/// Dst + 1, y's high 8 at Dst + 2.</summary>
internal sealed record GpStick12Op(GpFloat SrcX, GpFloat SrcY, int Min, int Max, bool InvertY, int Dst) : Op;

/// <summary>WireArith.Scaled: the value, negated if asked, times num / den
/// rounded to nearest with ties to even and clamped to the int16 range, a
/// NaN 0, written as two bytes little-endian <see cref="Count"/> times
/// <see cref="Stride"/> bytes apart.</summary>
internal sealed record GpScaledOp(GpFloat Src, double Num, double Den, bool Negate, int Dst, int Count, int Stride) : Op;

/// <summary>An integer of HMGamepadState, its low <see cref="Bytes"/> bytes
/// little-endian.</summary>
internal sealed record GpRawOp(GpRaw Src, int Dst, int Bytes) : Op;

/// <summary>A pressure-sensitive button's byte.</summary>
internal sealed record GpPressureOp(GpPressure Button, int Dst) : Op;

/// <summary>A byte of HMGamepadState through a 256-entry table, or
/// <see cref="Full"/> while the battery is full and <see cref="Charging"/>
/// while it charges, each when 0 or more. Written as the whole byte, or
/// shifted to bit <see cref="BitLo"/> and masked to bits BitLo to BitHi as
/// an OR op.</summary>
internal sealed record GpTableOp(GpByte Src, byte[] Table, int Full, int Charging, int Dst, int BitLo, int BitHi, bool Whole) : Op;

/// <summary>A touchpad finger as Sony's four bytes: bit 7 of the first set
/// while lifted and its tracking ID in bits 0 to 6, then X in 12 bits and Y
/// in 12 bits.</summary>
internal sealed record GpFingerOp(int Finger, int Dst) : Op;

/// <summary>A Valve trackpad coordinate (VendorBlobCodec's int16-pad): with
/// the pad's finger down, its Sony-range coordinate as full-scale signed,
/// inverted if asked. With it up, 0, or with <see cref="OrStick"/> the
/// stick's value through WireArith.Linear.</summary>
internal sealed record GpPadOp(int Pad, bool IsY, bool OrStick, int Min, int Max, bool Invert, int Dst) : Op;

/// <summary>A Valve pad's pressure (the codec's uint16-pressure): finger
/// <see cref="Finger"/>'s pressure p, or with p not above 0 (0, negative or
/// NaN) 1 while the finger is down and 0 while it is up, through
/// WireArith.Linear(v, min, max, false, 0), its low 16 bits
/// little-endian.</summary>
internal sealed record GpPadPressureOp(int Finger, int Min, int Max, int Dst) : Op;

/// <summary>The CRC-32 (IEEE, reflected) of the prefix and the block's bytes
/// From to To as the other ops wrote them, little-endian at Dst. It runs
/// last.</summary>
internal sealed record GpCrc32Op(byte[] Prefix, int From, int To, int Dst) : Op;

/// <summary>What a counter slot of a lowered program holds, which the code
/// that runs it fills before each run: a rolling counter's value before it
/// advances, or the codec's timestamp over a divisor.</summary>
internal enum StateFeedKind : byte { Rolling8, Rolling32, AccumulateOver, TicksOver }

internal readonly record struct StateFeed(StateFeedKind Kind, uint Initial, int Stride, uint Divisor);

/// <summary>The CRC-32 the codec's crc32-le field computes: CRC-32/ISO-HDLC,
/// reflected, polynomial 0xEDB88320, the CRC a Sony Bluetooth report
/// carries.
///
/// <para>Sixteen bytes a step, by slicing: table k gives the register's
/// contribution of a byte that k more bytes follow, so the sixteen lookups
/// of a step are independent of each other, where a byte at a time chains
/// each lookup on the one before. DS4Windows computes the same CRC this way
/// for every Bluetooth DS4 and DualSense report (DS4Library/Crc32.cs,
/// InitializeTable and CalculateBasicHash). The register after each step is
/// the one the byte-at-a-time loop reaches, and the bytes left over take
/// that loop.</para></summary>
internal static unsafe class Crc32
{
    // Sixteen tables of 256, table k at k * 256.
    static readonly uint[] s_table = Build();

    static uint[] Build()
    {
        var t = new uint[16 * 256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        for (int k = 1; k < 16; k++)
            for (int i = 0; i < 256; i++)
            {
                uint prev = t[(k - 1) * 256 + i];
                t[k * 256 + i] = (prev >> 8) ^ t[prev & 0xFF];
            }
        return t;
    }

    /// <summary>The register after <paramref name="bytes"/>, from
    /// <paramref name="crc"/>, without the final inversion.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> bytes)
    {
        fixed (byte* p = bytes) return Update(crc, p, bytes.Length);
    }

    // Compiled fully optimized from its first call, as the frame writer is:
    // tier-0 code calls Le32 four times a step instead of inlining it.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static uint Update(uint crc, byte* p, int len)
    {
        fixed (uint* t = s_table)
        {
            for (; len >= 16; len -= 16, p += 16)
            {
                uint one = Le32(p) ^ crc, two = Le32(p + 4), three = Le32(p + 8), four = Le32(p + 12);
                crc = t[15 * 256 + (one & 0xFF)] ^ t[14 * 256 + ((one >> 8) & 0xFF)]
                    ^ t[13 * 256 + ((one >> 16) & 0xFF)] ^ t[12 * 256 + (one >> 24)]
                    ^ t[11 * 256 + (two & 0xFF)] ^ t[10 * 256 + ((two >> 8) & 0xFF)]
                    ^ t[9 * 256 + ((two >> 16) & 0xFF)] ^ t[8 * 256 + (two >> 24)]
                    ^ t[7 * 256 + (three & 0xFF)] ^ t[6 * 256 + ((three >> 8) & 0xFF)]
                    ^ t[5 * 256 + ((three >> 16) & 0xFF)] ^ t[4 * 256 + (three >> 24)]
                    ^ t[3 * 256 + (four & 0xFF)] ^ t[2 * 256 + ((four >> 8) & 0xFF)]
                    ^ t[256 + ((four >> 16) & 0xFF)] ^ t[four >> 24];
            }
            for (; len > 0; len--, p++) crc = t[(crc ^ *p) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }

    /// <summary>Four bytes as a little-endian word, the order the reflected
    /// register takes them in.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    static uint Le32(byte* p)
    {
        uint v = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<uint>(p);
        return BitConverter.IsLittleEndian ? v : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    }
}

/// <summary>A state block's codec field list lowered into a state program,
/// or the reason it cannot be. The lowering covers the field types whose
/// writes a state program expresses, each read as VendorBlobProgram reads
/// it: its keys, their defaults and the button names. A block it cannot
/// lower runs through the codec, which program_equivalence_check holds the
/// lowered programs to.</summary>
internal static class StateLowering
{
    /// <summary>HMButton's names and bits, as Enum.TryParse reads them,
    /// ignoring case. program_equivalence_check holds this to HMButton.</summary>
    public static readonly IReadOnlyDictionary<string, uint> ButtonNames = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
    {
        ["None"] = 0, ["A"] = 1u << 0, ["B"] = 1u << 1, ["X"] = 1u << 2, ["Y"] = 1u << 3,
        ["LeftBumper"] = 1u << 4, ["RightBumper"] = 1u << 5, ["Back"] = 1u << 6, ["Start"] = 1u << 7,
        ["LeftStick"] = 1u << 8, ["RightStick"] = 1u << 9, ["Guide"] = 1u << 10, ["Touchpad"] = 1u << 11,
        ["Share"] = 1u << 12, ["RightPaddle"] = 1u << 13, ["LeftPaddle"] = 1u << 14, ["Misc1"] = 1u << 15,
        ["RightPaddle2"] = 1u << 16, ["LeftPaddle2"] = 1u << 17, ["RightTouchpad"] = 1u << 18,
        ["LeftStickTouch"] = 1u << 19, ["RightStickTouch"] = 1u << 20, ["LeftGripTouch"] = 1u << 21, ["RightGripTouch"] = 1u << 22,
        ["LeftTriggerClick"] = 1u << 23, ["RightTriggerClick"] = 1u << 24, ["LeftTouchpad"] = 1u << 11,
        ["Cross"] = 1u << 0, ["Circle"] = 1u << 1, ["Square"] = 1u << 2, ["Triangle"] = 1u << 3,
    };

    static readonly Dictionary<string, GpFlag> s_buttonFlags = new(StringComparer.Ordinal)
    {
        ["LT_DIGITAL"] = GpFlag.LeftTriggerOn, ["RT_DIGITAL"] = GpFlag.RightTriggerOn,
        ["DPAD_UP"] = GpFlag.DpadUp, ["DPAD_DOWN"] = GpFlag.DpadDown, ["DPAD_LEFT"] = GpFlag.DpadLeft, ["DPAD_RIGHT"] = GpFlag.DpadRight,
        ["LEFTPAD_TOUCH"] = GpFlag.Finger0Active, ["RIGHTPAD_TOUCH"] = GpFlag.Finger1Active,
    };

    /// <summary>A d-pad direction of a field with "dpad": "resolved".</summary>
    static GpFlag Resolved(GpFlag f) => f switch
    {
        GpFlag.DpadUp => GpFlag.DpadUpResolved, GpFlag.DpadDown => GpFlag.DpadDownResolved,
        GpFlag.DpadLeft => GpFlag.DpadLeftResolved, GpFlag.DpadRight => GpFlag.DpadRightResolved,
        _ => f,
    };

    static readonly Dictionary<string, GpFlag> s_bitfieldFlags = new(StringComparer.Ordinal)
    {
        ["batteryCharging"] = GpFlag.BatteryCharging, ["batteryFull"] = GpFlag.BatteryFull, ["micMuted"] = GpFlag.MicMuted,
        ["headphonesConnected"] = GpFlag.HeadphonesConnected, ["cableConnected"] = GpFlag.CableConnected,
    };

    static readonly Dictionary<string, GpFloat> s_floats = new(StringComparer.Ordinal)
    {
        ["leftStickX"] = GpFloat.LeftStickX, ["leftStickY"] = GpFloat.LeftStickY,
        ["rightStickX"] = GpFloat.RightStickX, ["rightStickY"] = GpFloat.RightStickY,
        ["leftTrigger"] = GpFloat.LeftTrigger, ["rightTrigger"] = GpFloat.RightTrigger,
        ["accelGX"] = GpFloat.AccelGX, ["accelGY"] = GpFloat.AccelGY, ["accelGZ"] = GpFloat.AccelGZ,
        ["gyroDpsX"] = GpFloat.GyroDpsX, ["gyroDpsY"] = GpFloat.GyroDpsY, ["gyroDpsZ"] = GpFloat.GyroDpsZ,
    };

    static readonly Dictionary<string, GpRaw> s_raw = new(StringComparer.Ordinal)
    {
        ["gyroPitch"] = GpRaw.GyroPitch, ["gyroYaw"] = GpRaw.GyroYaw, ["gyroRoll"] = GpRaw.GyroRoll,
        ["accelX"] = GpRaw.AccelX, ["accelY"] = GpRaw.AccelY, ["accelZ"] = GpRaw.AccelZ,
    };

    static readonly Dictionary<string, GpPressure> s_pressure = new(StringComparer.Ordinal)
    {
        ["pressureA"] = GpPressure.A, ["pressureB"] = GpPressure.B, ["pressureX"] = GpPressure.X, ["pressureY"] = GpPressure.Y,
        ["pressureLeftBumper"] = GpPressure.LeftBumper, ["pressureRightBumper"] = GpPressure.RightBumper,
        ["pressureDpadUp"] = GpPressure.DpadUp, ["pressureDpadRight"] = GpPressure.DpadRight,
        ["pressureDpadDown"] = GpPressure.DpadDown, ["pressureDpadLeft"] = GpPressure.DpadLeft,
    };

    /// <summary>The keys each lowered type reads. A field with any other key
    /// is left to the codec.</summary>
    static readonly Dictionary<string, string[]> s_keys = new(StringComparer.Ordinal)
    {
        ["button-mask"] = new[] { "byte", "type", "semantic", "buttons", "bits", "dpad", "triggerThreshold" },
        ["stick12-pair"] = new[] { "byte", "type", "semantic", "min", "max", "invert" },
        ["uint8-axis"] = new[] { "byte", "type", "semantic", "min", "max", "invert", "center" },
        ["uint8-trigger"] = new[] { "byte", "type", "semantic", "min", "max", "invert" },
        ["uint16-trigger"] = new[] { "byte", "type", "semantic", "min", "max", "invert" },
        ["int16-axis"] = new[] { "byte", "type", "semantic", "min", "max", "invert" },
        ["int16-le"] = new[] { "byte", "type", "semantic", "scale", "negate", "repeat" },
        ["uint8"] = new[] { "byte", "type", "semantic", "initial" },
        ["uint8-pressure"] = new[] { "byte", "type", "semantic" },
        ["uint10-be"] = new[] { "byte", "type", "semantic", "center", "resolution", "sourceResolution", "invert" },
        ["uint8-level-map"] = new[] { "byte", "type", "semantic", "levels", "chargingValue", "fullValue" },
        ["uint8-battery"] = new[] { "byte", "type", "semantic", "bits", "fullValue" },
        ["hat-octant"] = new[] { "byte", "type", "semantic", "bits", "neutralValue" },
        ["bitfield"] = new[] { "byte", "type", "semantic", "bits", "buttons" },
        ["touchpad-finger"] = new[] { "byte", "type", "semantic" },
        ["int16-pad"] = new[] { "byte", "type", "semantic", "invert" },
        ["int16-pad-or-stick"] = new[] { "byte", "type", "semantic", "min", "max", "invert" },
        ["uint16-pressure"] = new[] { "byte", "type", "semantic", "min", "max" },
        ["uint8-rolling"] = new[] { "byte", "type", "semantic", "initial", "stride" },
        ["uint32-rolling"] = new[] { "byte", "type", "semantic", "initial", "stride" },
        ["uint16-le"] = new[] { "byte", "type", "semantic", "divisor", "clock", "holdWhile" },
        ["uint32-le"] = new[] { "byte", "type", "semantic", "divisor", "clock", "holdWhile" },
        ["crc32-le"] = new[] { "byte", "bytes", "type", "scope" },
    };

    /// <summary>The program of a state block of <paramref name="size"/>
    /// bytes, or null with <paramref name="reason"/>. A block whose fields
    /// keep state between frames (a rolling counter, a timestamp) is left to
    /// the codec.</summary>
    public static ProgramSpec? Lower(JsonElement fields, int size, out string reason) =>
        Lower(fields, size, 0, allowFeeds: false, out _, out reason);

    /// <summary>The program of a codec report of <paramref name="size"/>
    /// bytes whose byte 0 the codec sets to <paramref name="reportId"/>
    /// before its fields. With <paramref name="allowFeeds"/>, a field that
    /// keeps state between frames reads a counter slot, and
    /// <paramref name="feeds"/> says what the code that runs the program puts
    /// in each.</summary>
    public static ProgramSpec? Lower(JsonElement fields, int size, int reportId, bool allowFeeds, out StateFeed[] feeds, out string reason)
    {
        // The lowering reads fields the codec's check has not seen yet: a
        // value no JSON read gives as the lowering asks leaves the block to
        // the codec, whose check refuses it with its path.
        try { return LowerFields(fields, size, reportId, allowFeeds, out feeds, out reason); }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException)
        {
            feeds = Array.Empty<StateFeed>();
            reason = $"a field holds a value the lowering cannot read ({e.Message})";
            return null;
        }
    }

    private static ProgramSpec? LowerFields(JsonElement fields, int size, int reportId, bool allowFeeds, out StateFeed[] feeds, out string reason)
    {
        reason = "";
        feeds = Array.Empty<StateFeed>();
        if (fields.ValueKind != JsonValueKind.Array) { reason = "the fields are not a list"; return null; }
        if (size < 1) { reason = "the block has no bytes"; return null; }
        var ops = new List<Op>();
        var feedList = new List<StateFeed>();
        var rollingKeys = new HashSet<string>(StringComparer.Ordinal);
        // The codec clears the block, sets byte 0 to the report ID, then
        // writes its fields in order.
        var template = new byte[size];
        template[0] = (byte)reportId;
        var consts = new SortedDictionary<int, byte>();
        // The bits each field writes. A later field over an earlier one's
        // bits wins in the codec, which a program, whose writers never share
        // a bit, cannot say: such a block stays with the codec. A field's
        // bits clear the report ID's under them.
        var claimed = new byte[size];
        bool Claim(int at, int bits)
        {
            if (at < 0 || at >= size) return true;
            if ((claimed[at] & bits) != 0) return false;
            claimed[at] |= (byte)bits;
            template[at] &= (byte)~bits;
            return true;
        }
        bool ClaimBytes(int at, int n)
        {
            for (int k = 0; k < n; k++) if (!Claim(at + k, 0xFF)) return false;
            return true;
        }
        bool crcSeen = false;
        int index = 0;
        foreach (var f in fields.EnumerateArray())
        {
            string where = $"fields[{index++}]";
            if (crcSeen) { reason = $"{where} follows the CRC, which the codec computes before it"; return null; }
            if (f.ValueKind != JsonValueKind.Object) { reason = $"{where} is not an object"; return null; }
            var keys = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in f.EnumerateObject())
            {
                if (keys.ContainsKey(p.Name)) { reason = $"{where} writes a key twice"; return null; }
                keys[p.Name] = p.Value;
            }
            string type = Str(keys, "type") ?? "";
            if (!s_keys.TryGetValue(type, out var allowed)) { reason = $"{where} is a {type}, which the codec writes"; return null; }
            foreach (var k in keys.Keys)
                if (!allowed.Contains(k, StringComparer.OrdinalIgnoreCase)) { reason = $"{where} ('{type}') sets {k}, which the codec reads"; return null; }
            string? semantic = Str(keys, "semantic");
            bool invert = Bool(keys, "invert") ?? false;
            if (type == "crc32-le")
            {
                if (!keys.TryGetValue("scope", out var scope) || scope.ValueKind != JsonValueKind.Object) continue;
                int dst = Str(keys, "bytes") is string bytes ? (TryBitRange(bytes, out int lo, out _) ? lo : -1) : Int(keys, "byte") ?? size - 4;
                var sk = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var x in scope.EnumerateObject())
                {
                    if (sk.ContainsKey(x.Name)) { reason = $"{where}.scope writes a key twice"; return null; }
                    sk[x.Name] = x.Value;
                }
                var prefix = sk.TryGetValue("prefix", out var pr) && pr.ValueKind == JsonValueKind.Array ? pr.EnumerateArray().Select(x => (byte)x.GetInt32()).ToArray() : Array.Empty<byte>();
                int from = sk.TryGetValue("from", out var fr) && fr.ValueKind == JsonValueKind.Number ? fr.GetInt32() : 0;
                int to = Math.Min(sk.TryGetValue("to", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0, size - 1);
                if (dst < 0 || (long)dst + 4 > size || from < 0 || (from <= to && dst <= to && from <= dst + 3)) { reason = $"{where} covers its own bytes or passes the block"; return null; }
                if (!ClaimBytes(dst, 4)) { reason = $"{where} writes bytes another field writes"; return null; }
                ops.Add(new GpCrc32Op(prefix, from, to, dst));
                crcSeen = true;
                continue;
            }
            // A field with no byte writes nothing, as in the codec.
            if (Int(keys, "byte") is not int b) continue;
            if (b < 0) { reason = $"{where}.byte"; return null; }
            // Past the block: the codec's check refuses it. Every offset
            // below is under the block's size, so no sum of one wraps.
            if (b >= size) { reason = $"{where}.byte {b} is past the {size}-byte block"; return null; }
            switch (type)
            {
                case "button-mask":
                {
                    var names = keys.TryGetValue("buttons", out var bl) && bl.ValueKind == JsonValueKind.Array
                        ? bl.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToList()
                        : null;
                    if (names == null) continue;
                    int lo = 0, hi = 7;
                    if (Str(keys, "bits") is string bits && (!TryBitRange(bits, out lo, out hi) || lo < 0 || hi < lo || hi > 63)) { reason = $"{where}.bits"; return null; }
                    // The codec writes every bit of the field's range.
                    for (int bit = lo; bit <= hi; bit++)
                        if (!Claim(b + bit / 8, 1 << (bit & 7))) { reason = $"{where} writes bits another field writes"; return null; }
                    // "dpad" reads "resolved" or nothing, as the codec's
                    // check refuses any other value.
                    bool resolved = Str(keys, "dpad") is string dp
                        ? (dp == "resolved" ? true : throw new InvalidOperationException($"dpad '{dp}'"))
                        : false;
                    // "triggerThreshold" moves the trigger bits along the
                    // travel. A value the codec's check refuses leaves the
                    // block to the codec, which refuses it with its path.
                    int tMax = 0, tAt = 0;
                    if (keys.TryGetValue("triggerThreshold", out var tt))
                    {
                        if (tt.ValueKind != JsonValueKind.Object) { reason = $"{where}.triggerThreshold"; return null; }
                        var tk = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                        foreach (var x in tt.EnumerateObject())
                        {
                            if (tk.ContainsKey(x.Name)) { reason = $"{where}.triggerThreshold writes a key twice"; return null; }
                            tk[x.Name] = x.Value;
                        }
                        if (tk.Keys.Any(k => !k.Equals("max", StringComparison.OrdinalIgnoreCase) && !k.Equals("at", StringComparison.OrdinalIgnoreCase))
                            || Int(tk, "max") is not int m || Int(tk, "at") is not int a || m < 1 || m > 65535 || a < 1 || a > m)
                        { reason = $"{where}.triggerThreshold"; return null; }
                        tMax = m; tAt = a;
                    }
                    var moves = new List<int>();
                    var flags = new List<GpFlag>();
                    var flagBits = new List<int>();
                    var triggerBits = new List<GpTriggerBitOp>();
                    for (int j = 0; j < names.Count; j++)
                    {
                        string? name = names[j];
                        if (string.IsNullOrEmpty(name) || name == "_") continue;
                        // A button past the field's bits, or past the block,
                        // is dropped, as the codec drops it.
                        int bit = lo + j;
                        if (bit > hi || b + bit / 8 >= size) continue;
                        int reportBit = b * 8 + bit;
                        if (tMax > 0 && name is "LT_DIGITAL" or "RT_DIGITAL")
                        {
                            triggerBits.Add(new GpTriggerBitOp(name == "LT_DIGITAL" ? GpFloat.LeftTrigger : GpFloat.RightTrigger, tMax, tAt, reportBit));
                            continue;
                        }
                        if (s_buttonFlags.TryGetValue(name, out var flag)) { flags.Add(resolved ? Resolved(flag) : flag); flagBits.Add(reportBit); continue; }
                        if (name.Length > 1 && name[0] == '#')
                        {
                            if (!int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n >= 32) { reason = $"{where}: '{name}'"; return null; }
                            moves.Add(n); moves.Add(reportBit);
                            continue;
                        }
                        if (!ButtonNames.TryGetValue(name.Trim(), out uint mask)) { reason = $"{where}: '{name}' is a button name the codec reads"; return null; }
                        if (mask == 0) continue;
                        moves.Add(System.Numerics.BitOperations.TrailingZeroCount(mask)); moves.Add(reportBit);
                    }
                    if (moves.Count > 0) ops.Add(new GpButtonsOp(moves.ToArray()));
                    if (flags.Count > 0) ops.Add(new GpFlagsOp(flags.ToArray(), flagBits.ToArray()));
                    ops.AddRange(triggerBits);
                    break;
                }
                case "stick12-pair":
                {
                    // The codec's left stick for any semantic but rightStick.
                    bool right = semantic == "rightStick";
                    if (b + 2 >= size) continue;
                    if (!ClaimBytes(b, 3)) { reason = $"{where} writes bytes another field writes"; return null; }
                    ops.Add(new GpStick12Op(right ? GpFloat.RightStickX : GpFloat.LeftStickX, right ? GpFloat.RightStickY : GpFloat.LeftStickY,
                        Int(keys, "min") ?? 0, Int(keys, "max") ?? 4095, invert, b));
                    break;
                }
                case "uint8-axis":
                case "uint8-trigger":
                case "uint16-trigger":
                case "int16-axis":
                {
                    bool trigger = type is "uint8-trigger" or "uint16-trigger";
                    // A semantic the type does not read writes the codec's
                    // default: 0.5 on an axis, 0 on a trigger.
                    GpFloat? src = semantic != null && s_floats.TryGetValue(semantic, out var fs)
                        && (trigger ? fs is GpFloat.LeftTrigger or GpFloat.RightTrigger : fs <= GpFloat.RightStickY) ? fs : null;
                    if (src == null) { reason = $"{where} ('{type}') reads no slot"; return null; }
                    var (defMin, defMax) = type switch
                    {
                        "uint8-axis" or "uint8-trigger" => (0, 255),
                        "int16-axis" => (-32767, 32767),
                        _ => (0, 65535),
                    };
                    int min = Int(keys, "min") ?? defMin, max = Int(keys, "max") ?? defMax;
                    if (type == "uint8-axis" && (Int(keys, "center") ?? 128) is int center && center != 128) { min = center - 127; max = center + 127; }
                    var store = type switch { "uint8-axis" => GpStore.ByteClamped, "uint8-trigger" => GpStore.Byte, _ => GpStore.Word };
                    int width = store == GpStore.Word ? 2 : 1;
                    if (b + width > size) continue;
                    if (!ClaimBytes(b, width)) { reason = $"{where} writes bytes another field writes"; return null; }
                    ops.Add(new GpLinearOp(src.Value, min, max, invert, trigger ? 0.0 : 0.5, store, b));
                    break;
                }
                case "int16-le":
                {
                    if (!keys.ContainsKey("scale") && !keys.ContainsKey("negate") && !keys.ContainsKey("repeat"))
                    {
                        // A raw value, or 0 for a semantic it does not read.
                        if (b + 2 > size) continue;
                        if (!ClaimBytes(b, 2)) { reason = $"{where} writes bytes another field writes"; return null; }
                        if (semantic != null && s_raw.TryGetValue(semantic, out var raw)) ops.Add(new GpRawOp(raw, b, 2));
                        break;
                    }
                    GpFloat? src = semantic != null && s_floats.TryGetValue(semantic, out var fs) && fs >= GpFloat.AccelGX ? fs : null;
                    if (!keys.TryGetValue("scale", out var scale) || src == null) { reason = $"{where} ('int16-le') writes a raw value in repeats, which the codec writes"; return null; }
                    double num, den = 1;
                    if (scale.ValueKind == JsonValueKind.Number) num = scale.GetDouble();
                    else if (scale.ValueKind == JsonValueKind.Array && scale.GetArrayLength() == 2) { num = scale[0].GetDouble(); den = scale[1].GetDouble(); }
                    else { reason = $"{where}.scale"; return null; }
                    // A numerator of 0 is the codec's raw path.
                    if (num == 0) { reason = $"{where} ('int16-le') has a scale of 0, the codec's raw value"; return null; }
                    int count = 1, stride = 0;
                    if (keys.TryGetValue("repeat", out var rep))
                    {
                        if (rep.ValueKind != JsonValueKind.Array || rep.GetArrayLength() != 2) { reason = $"{where}.repeat"; return null; }
                        count = rep[0].GetInt32(); stride = rep[1].GetInt32();
                        if (count > 1 && stride < 2) { reason = $"{where}.repeat"; return null; }
                    }
                    // The codec writes each repeat that fits the block.
                    int fits = 0;
                    // In long: a stride near int.MaxValue would wrap the
                    // offset of the frame after it back inside the block.
                    long next = b;
                    for (int k = 0; k < count && next + 1 < size; k++, next += stride) fits++;
                    if (fits == 0) continue;
                    for (int k = 0; k < fits; k++)
                        if (!ClaimBytes(b + k * stride, 2)) { reason = $"{where} writes bytes another field writes"; return null; }
                    ops.Add(new GpScaledOp(src.Value, num, den, Bool(keys, "negate") ?? false, b, fits, stride));
                    break;
                }
                case "uint8":
                {
                    if (b >= size) continue;
                    if (!ClaimBytes(b, 1)) { reason = $"{where} writes a byte another field writes"; return null; }
                    // The touchpad's packet counter is read from the state.
                    if (semantic == "touchpadPacketCounter") ops.Add(new GpRawOp(GpRaw.TouchpadPacketCounter, b, 1));
                    else consts[b] = (byte)(Int(keys, "initial") ?? 0);
                    break;
                }
                case "uint8-pressure":
                {
                    if (semantic == null || !s_pressure.TryGetValue(semantic, out var button)) { reason = $"{where} names no pressure-sensitive button"; return null; }
                    if (b >= size) continue;
                    if (!ClaimBytes(b, 1)) { reason = $"{where} writes a byte another field writes"; return null; }
                    ops.Add(new GpPressureOp(button, b));
                    break;
                }
                case "uint10-be":
                {
                    int res = Int(keys, "resolution") ?? 0, srcRes = Int(keys, "sourceResolution") ?? 0;
                    if (res < 1 || srcRes < 1) { reason = $"{where} needs both resolutions"; return null; }
                    if (b + 2 > size) continue;
                    if (!ClaimBytes(b, 2)) { reason = $"{where} writes bytes another field writes"; return null; }
                    GpRaw? raw = semantic != null && s_raw.TryGetValue(semantic, out var r) ? r : null;
                    if (raw == null) { reason = $"{where} ('uint10-be') reads no motion value"; return null; }
                    ops.Add(new GpTenBitOp(raw.Value, res, srcRes, Int(keys, "center") ?? 512, invert, b));
                    break;
                }
                case "uint8-level-map":
                {
                    if (!keys.TryGetValue("levels", out var lv) || lv.ValueKind != JsonValueKind.Array || lv.GetArrayLength() < 11) { reason = $"{where} needs eleven levels"; return null; }
                    if (b >= size) continue;
                    if (!ClaimBytes(b, 1)) { reason = $"{where} writes a byte another field writes"; return null; }
                    var levels = lv.EnumerateArray().Select(x => (byte)x.GetInt32()).ToArray();
                    var table = new byte[256];
                    for (int l = 0; l < 256; l++) table[l] = levels[Math.Min(l, 10)];
                    ops.Add(new GpTableOp(GpByte.BatteryLevel, table, Int(keys, "fullValue") ?? -1, Int(keys, "chargingValue") ?? -1, b, 0, 7, Whole: true));
                    break;
                }
                case "uint8-battery":
                {
                    int lo = 0, hi = 7;
                    if (Str(keys, "bits") is string bits && (!TryBitRange(bits, out lo, out hi) || lo < 0 || hi < lo || hi > 7)) { reason = $"{where}.bits"; return null; }
                    if (b >= size) continue;
                    for (int bit = lo; bit <= hi; bit++) if (!Claim(b, 1 << bit)) { reason = $"{where} writes bits another field writes"; return null; }
                    int wmask = (1 << (hi - lo + 1)) - 1;
                    var table = new byte[256];
                    for (int l = 0; l < 256; l++) table[l] = (byte)(l & wmask);
                    int full = Int(keys, "fullValue") ?? -1;
                    ops.Add(new GpTableOp(GpByte.BatteryLevel, table, full >= 0 ? full & wmask : -1, -1, b, lo, hi, Whole: false));
                    break;
                }
                case "hat-octant":
                {
                    bool hasBits = Str(keys, "bits") != null;
                    int lo = 0, hi = 7;
                    if (hasBits && (!TryBitRange(Str(keys, "bits")!, out lo, out hi) || lo < 0 || hi < lo || hi > 7)) { reason = $"{where}.bits"; return null; }
                    if (b >= size) continue;
                    for (int bit = lo; bit <= hi; bit++) if (!Claim(b, 1 << bit)) { reason = $"{where} writes bits another field writes"; return null; }
                    int neutral = Int(keys, "neutralValue") ?? 8;
                    var table = new byte[256];
                    for (int h = 0; h < 256; h++) table[h] = (byte)(h == 0 ? neutral : (h - 1) & 0x0F);
                    ops.Add(new GpTableOp(GpByte.Hat, table, -1, -1, b, lo, hi, Whole: !hasBits));
                    break;
                }
                case "bitfield":
                {
                    var names = keys.TryGetValue("buttons", out var bl) && bl.ValueKind == JsonValueKind.Array
                        ? bl.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToList()
                        : null;
                    if (names == null) continue;
                    int lo = 0, hi = 7;
                    if (Str(keys, "bits") is string bits && (!TryBitRange(bits, out lo, out hi) || lo < 0 || hi < lo || hi > 7)) { reason = $"{where}.bits"; return null; }
                    if (b >= size) continue;
                    for (int bit = lo; bit <= hi; bit++) if (!Claim(b, 1 << bit)) { reason = $"{where} writes bits another field writes"; return null; }
                    var flags = new List<GpFlag>();
                    var flagBits = new List<int>();
                    for (int j = 0; j < names.Count && lo + j <= hi; j++)
                        if (names[j] != null && s_bitfieldFlags.TryGetValue(names[j]!, out var flag)) { flags.Add(flag); flagBits.Add(b * 8 + lo + j); }
                    if (flags.Count > 0) ops.Add(new GpFlagsOp(flags.ToArray(), flagBits.ToArray()));
                    break;
                }
                case "touchpad-finger":
                {
                    if (b + 4 > size) continue;
                    if (!ClaimBytes(b, 4)) { reason = $"{where} writes bytes another field writes"; return null; }
                    ops.Add(new GpFingerOp(semantic == "touchpadFinger1" ? 1 : 0, b));
                    break;
                }
                case "int16-pad":
                case "int16-pad-or-stick":
                {
                    int pad, isY;
                    switch (semantic)
                    {
                        case "leftPadX": pad = 0; isY = 0; break;
                        case "leftPadY": pad = 0; isY = 1; break;
                        case "rightPadX": pad = 1; isY = 0; break;
                        case "rightPadY": pad = 1; isY = 1; break;
                        default: reason = $"{where} ('{type}') names no pad coordinate"; return null;
                    }
                    if (b + 2 > size) continue;
                    if (!ClaimBytes(b, 2)) { reason = $"{where} writes bytes another field writes"; return null; }
                    bool orStick = type == "int16-pad-or-stick";
                    ops.Add(new GpPadOp(pad, isY == 1, orStick, orStick ? Int(keys, "min") ?? -32767 : 0, orStick ? Int(keys, "max") ?? 32767 : 0, invert, b));
                    break;
                }
                case "uint16-pressure":
                {
                    if (b + 2 > size) continue;
                    if (!ClaimBytes(b, 2)) { reason = $"{where} writes bytes another field writes"; return null; }
                    // The codec reads finger 1 for rightPadPressure and
                    // finger 0 for any other semantic.
                    ops.Add(new GpPadPressureOp(semantic == "rightPadPressure" ? 1 : 0, Int(keys, "min") ?? 0, Int(keys, "max") ?? 65535, b));
                    break;
                }
                case "uint8-rolling":
                case "uint32-rolling":
                {
                    bool wide = type == "uint32-rolling";
                    int width = wide ? 4 : 1;
                    if (!allowFeeds) { reason = $"{where} is a rolling counter, which keeps state between frames"; return null; }
                    // Two fields that share a key share the codec's counter,
                    // which a counter slot read once a frame cannot say.
                    if (!rollingKeys.Add((semantic ?? "_b" + b) + (wide ? "/32" : "/8"))) { reason = $"{where} shares a counter with another field"; return null; }
                    if (b + width > size) continue;
                    if (!ClaimBytes(b, width)) { reason = $"{where} writes bytes another field writes"; return null; }
                    ops.Add(new CounterOp(feedList.Count, b, width, 1, 0));
                    feedList.Add(new StateFeed(wide ? StateFeedKind.Rolling32 : StateFeedKind.Rolling8, (byte)(Int(keys, "initial") ?? 0), Int(keys, "stride") ?? 1, 0));
                    break;
                }
                case "uint16-le":
                case "uint32-le":
                {
                    int width = type == "uint16-le" ? 2 : 4;
                    int divisor = Int(keys, "divisor") ?? 1;
                    if (divisor < 1) { reason = $"{where}.divisor"; return null; }
                    bool accumulate = Str(keys, "clock") == "accumulate";
                    if (b + width > size) continue;
                    if (!ClaimBytes(b, width)) { reason = $"{where} writes bytes another field writes"; return null; }
                    // A semantic other than the stamp writes 0.
                    if (semantic != "sensorTimestamp") break;
                    if (width == 4 && !accumulate) { ops.Add(new GpRawOp(GpRaw.SensorTimestamp, b, 4)); break; }
                    if (!allowFeeds) { reason = $"{where} is a timestamp, which keeps state between frames"; return null; }
                    ops.Add(new CounterOp(feedList.Count, b, width, 1, 0));
                    feedList.Add(new StateFeed(accumulate ? StateFeedKind.AccumulateOver : StateFeedKind.TicksOver, 0, 0, (uint)divisor));
                    break;
                }
            }
        }
        foreach (var (at, v) in consts) template[at] = v;
        var all = template.Any(x => x != 0) ? ops.Prepend(new ConstOp(0, template)) : ops;
        feeds = feedList.ToArray();
        return new ProgramSpec("stateBlock", size, 0, Array.Empty<int>(), all.ToArray(), Gamepad: true);
    }

    /// <summary>The codec reports a profile file holds besides its state
    /// block: its extendedReport, from byte 0 with its report ID, or, for a
    /// report with ports, one port's block, as PortLayout encodes it (no
    /// report ID, the port stride's bytes). Keys are read in any case, as the
    /// SDK reads a profile.</summary>
    public static IEnumerable<(string Name, JsonElement Fields, int Size, int ReportId)> CodecReports(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || Get(root, "extendedReport") is not { ValueKind: JsonValueKind.Object } ext) yield break;
        if (Get(ext, "ports") is { ValueKind: JsonValueKind.Object } ports)
        {
            if (Get(ports, "fields") is { ValueKind: JsonValueKind.Array } pf && Get(ports, "stride") is { ValueKind: JsonValueKind.Number } stride && stride.TryGetInt32(out int st))
                yield return ("extendedReport.ports", pf, st, 0);
            yield break;
        }
        if (Get(ext, "fields") is not { ValueKind: JsonValueKind.Array } fields || fields.GetArrayLength() == 0) yield break;
        if (Get(ext, "size") is not { ValueKind: JsonValueKind.Number } sz || !sz.TryGetInt32(out int size)) yield break;
        int id = 0;
        if (Get(ext, "reportId") is { ValueKind: JsonValueKind.String } rid && rid.GetString() is { Length: > 0 } text)
        {
            var hex = text.AsSpan();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            if (!byte.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte b)) yield break;
            id = b;
        }
        yield return ("extendedReport", fields, size, id);
    }

    static JsonElement? Get(JsonElement o, string name)
    {
        foreach (var p in o.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    static string? Str(Dictionary<string, JsonElement> k, string name) =>
        k.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static int? Int(Dictionary<string, JsonElement> k, string name) =>
        k.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;

    static bool? Bool(Dictionary<string, JsonElement> k, string name) =>
        k.TryGetValue(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    /// <summary>"lo-hi" or a single bit, as VendorBlobProgram reads it.</summary>
    static bool TryBitRange(string text, out int lo, out int hi)
    {
        lo = hi = 0;
        string s = text.Trim();
        int dash = s.IndexOf('-');
        if (dash < 0) { if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out lo)) return false; hi = lo; return true; }
        return int.TryParse(s.AsSpan(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture, out lo)
            && int.TryParse(s.AsSpan(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out hi);
    }
}

/// <summary>A DualShock 3 motion value (the codec's uint10-be): the raw value
/// rescaled from its resolution to the wire's, rounded half away from zero,
/// around the center (subtracted when inverted), clamped to 0..1023 and
/// written most significant byte first.</summary>
internal sealed record GpTenBitOp(GpRaw Src, int Resolution, int SourceResolution, int Center, bool Invert, int Dst) : Op;
