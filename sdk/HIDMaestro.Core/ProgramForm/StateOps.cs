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
/// step of its 8-bit value (the codec's TriggerOn, v x 255 above 0.5), a
/// direction of HMGamepadState.Hat (a diagonal sets both of its
/// components), or a flag of HMGamepadState.</summary>
internal enum GpFlag : byte
{
    LeftTriggerOn, RightTriggerOn, DpadUp, DpadDown, DpadLeft, DpadRight,
    Finger0Active, Finger1Active, BatteryCharging, BatteryFull, MicMuted, HeadphonesConnected, CableConnected,
}

/// <summary>How a linear value is stored: one byte clamped to 0..255, one
/// byte as its low 8 bits, or two bytes, its low 16 bits little-endian.</summary>
internal enum GpStore : byte { ByteClamped, Byte, Word }

/// <summary>HMButton bits to report bits: pairs of (HMButton bit, report
/// bit). An OR op: each report bit it names is ORed into its byte.</summary>
internal sealed record GpButtonsOp(int[] Moves) : Op;

/// <summary>Flags to report bits, flag i to report bit Bits[i]. An OR op.</summary>
internal sealed record GpFlagsOp(GpFlag[] Flags, int[] Bits) : Op;

/// <summary>WireArith.Linear(src, min, max, invert, rest): a NaN takes rest,
/// the value is clamped to [0, 1], inverted if asked, and min + RoundHalfEven
/// of it times max - min, stored as <see cref="Store"/> says.</summary>
internal sealed record GpLinearOp(GpFloat Src, int Min, int Max, bool Invert, double Rest, GpStore Store, int Dst) : Op;

/// <summary>WireArith.Scaled: the value, negated if asked, times num / den
/// rounded to nearest with ties to even and clamped to the int16 range, a
/// NaN 0, written as two bytes little-endian <see cref="Count"/> times
/// <see cref="Stride"/> bytes apart.</summary>
internal sealed record GpScaledOp(GpFloat Src, double Num, double Den, bool Negate, int Dst, int Count, int Stride) : Op;

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

    static readonly Dictionary<string, GpFloat> s_floats = new(StringComparer.Ordinal)
    {
        ["leftStickX"] = GpFloat.LeftStickX, ["leftStickY"] = GpFloat.LeftStickY,
        ["rightStickX"] = GpFloat.RightStickX, ["rightStickY"] = GpFloat.RightStickY,
        ["leftTrigger"] = GpFloat.LeftTrigger, ["rightTrigger"] = GpFloat.RightTrigger,
        ["accelGX"] = GpFloat.AccelGX, ["accelGY"] = GpFloat.AccelGY, ["accelGZ"] = GpFloat.AccelGZ,
        ["gyroDpsX"] = GpFloat.GyroDpsX, ["gyroDpsY"] = GpFloat.GyroDpsY, ["gyroDpsZ"] = GpFloat.GyroDpsZ,
    };

    /// <summary>The keys each lowered type reads. A field with any other key
    /// is left to the codec.</summary>
    static readonly Dictionary<string, string[]> s_keys = new(StringComparer.Ordinal)
    {
        ["button-mask"] = new[] { "byte", "type", "buttons", "bits" },
        ["uint8-axis"] = new[] { "byte", "type", "semantic", "min", "max", "invert", "center" },
        ["uint8-trigger"] = new[] { "byte", "type", "semantic", "min", "max", "invert" },
        ["uint16-trigger"] = new[] { "byte", "type", "semantic", "min", "max", "invert" },
        ["int16-axis"] = new[] { "byte", "type", "semantic", "min", "max", "invert" },
        ["int16-le"] = new[] { "byte", "type", "semantic", "scale", "negate", "repeat" },
        ["uint8"] = new[] { "byte", "type", "semantic", "initial" },
    };

    /// <summary>The program of a state block of <paramref name="size"/>
    /// bytes, or null with <paramref name="reason"/>.</summary>
    public static ProgramSpec? Lower(JsonElement fields, int size, out string reason)
    {
        reason = "";
        if (fields.ValueKind != JsonValueKind.Array) { reason = "the fields are not a list"; return null; }
        var ops = new List<Op>();
        var consts = new SortedDictionary<int, byte>();
        // The bits each field writes. The codec fills the block field by
        // field, so a later field over an earlier one's bits wins, which a
        // program, whose writers never share a bit, cannot say: such a block
        // stays with the codec.
        var claimed = new byte[size];
        bool Claim(int at, int bits)
        {
            if (at < 0 || at >= size) return true;
            if ((claimed[at] & bits) != 0) return false;
            claimed[at] |= (byte)bits;
            return true;
        }
        int index = 0;
        foreach (var f in fields.EnumerateArray())
        {
            string where = $"fields[{index++}]";
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
            // A field with no byte writes nothing, as in the codec.
            if (Int(keys, "byte") is not int b) continue;
            string? semantic = Str(keys, "semantic");
            bool invert = Bool(keys, "invert") ?? false;
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
                    var moves = new List<int>();
                    var flags = new List<GpFlag>();
                    var flagBits = new List<int>();
                    for (int j = 0; j < names.Count; j++)
                    {
                        string? name = names[j];
                        if (string.IsNullOrEmpty(name) || name == "_") continue;
                        // A button past the field's bits, or past the block,
                        // is dropped, as the codec drops it.
                        int bit = lo + j;
                        if (bit > hi || b + bit / 8 >= size) continue;
                        int reportBit = b * 8 + bit;
                        if (s_buttonFlags.TryGetValue(name, out var flag)) { flags.Add(flag); flagBits.Add(reportBit); continue; }
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
                    if (b + (store == GpStore.Word ? 1 : 0) >= size) continue;
                    for (int k = 0; k <= (store == GpStore.Word ? 1 : 0); k++)
                        if (!Claim(b + k, 0xFF)) { reason = $"{where} writes bytes another field writes"; return null; }
                    ops.Add(new GpLinearOp(src.Value, min, max, invert, trigger ? 0.0 : 0.5, store, b));
                    break;
                }
                case "int16-le":
                {
                    GpFloat? src = semantic != null && s_floats.TryGetValue(semantic, out var fs) && fs >= GpFloat.AccelGX ? fs : null;
                    if (!keys.TryGetValue("scale", out var scale) || src == null) { reason = $"{where} ('int16-le') is a raw value, which the codec writes"; return null; }
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
                    for (int k = 0, at = b; k < count && at + 1 < size; k++, at += stride) fits++;
                    if (fits == 0) continue;
                    for (int k = 0, at = b; k < fits; k++, at += stride)
                        if (!Claim(at, 0xFF) || !Claim(at + 1, 0xFF)) { reason = $"{where} writes bytes another field writes"; return null; }
                    ops.Add(new GpScaledOp(src.Value, num, den, Bool(keys, "negate") ?? false, b, fits, stride));
                    break;
                }
                case "uint8":
                {
                    // A constant byte. The touchpad's packet counter is read
                    // from the state, which the codec writes.
                    if (semantic == "touchpadPacketCounter") { reason = $"{where} reads the touchpad's packet counter"; return null; }
                    if (b >= size) continue;
                    if (!Claim(b, 0xFF)) { reason = $"{where} writes a byte another field writes"; return null; }
                    consts[b] = (byte)(Int(keys, "initial") ?? 0);
                    break;
                }
            }
        }
        var all = consts.Count == 0 ? ops : ops.Prepend(new ConstOp(0, Template(consts, size)));
        return new ProgramSpec("stateBlock", size, 0, Array.Empty<int>(), all.ToArray(), Gamepad: true);
    }

    static byte[] Template(SortedDictionary<int, byte> consts, int size)
    {
        var t = new byte[size];
        foreach (var (at, v) in consts) t[at] = v;
        return t;
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
