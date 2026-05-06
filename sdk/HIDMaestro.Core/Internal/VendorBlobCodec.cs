using System;
using System.Collections.Generic;

namespace HIDMaestro.Internal;

/// <summary>v1.3.5 — generic vendor-blob HID report encoder/decoder. Walks an
/// <see cref="ExtendedReportSpec"/> field list to translate between
/// HMGamepadState / parsed-field dictionaries and on-wire bytes. Profile JSON
/// is the source of truth for byte layouts; the codec is profile-agnostic.
///
/// <para>The same codec serves both directions:</para>
/// <list type="bullet">
/// <item><description>Input: HMController.SubmitState → encoded extended-input report bytes</description></item>
/// <item><description>Output: incoming output-report bytes → parsed-field dictionary</description></item>
/// </list>
///
/// <para>CRC32 uses CRC-32/ISO-HDLC (poly 0xEDB88320) via System.IO.Hashing,
/// matching Sony's wire format and dualsense-tester / ds4drv reference impls.</para>
/// </summary>
internal static class VendorBlobCodec
{
    /// <summary>Per-controller mutable state for vendor-blob encoding.
    /// Holds rolling counters that advance with each Encode call so the
    /// emitted reports increment monotonically as a real device does.</summary>
    public sealed class EncoderState
    {
        // Keyed by field semantic name so multiple rolling fields in one
        // report (Sony: framingTag at byte 1, reportCounter at byte 8)
        // each advance independently.
        public Dictionary<string, byte> RollingCounters { get; } = new();
    }

    // ── Input encoder: HMGamepadState → bytes ─────────────────────────────

    /// <summary>Encode an HMGamepadState into the byte buffer per the spec.
    /// Buffer is zeroed first; report ID byte is written at offset 0.</summary>
    public static void EncodeInput(
        ExtendedReportSpec spec,
        in HMGamepadState state,
        byte[] buffer,
        EncoderState encState)
    {
        if (buffer.Length < spec.Size)
            throw new ArgumentException($"Buffer too small: need {spec.Size}, got {buffer.Length}");

        Array.Clear(buffer, 0, spec.Size);
        buffer[0] = spec.ReportIdByte;

        foreach (var field in spec.Fields)
        {
            EncodeField(field, buffer, in state, encState);
        }
    }

    private static void EncodeField(
        FieldSpec field,
        byte[] buffer,
        in HMGamepadState state,
        EncoderState encState)
    {
        switch (field.Type)
        {
            case "uint8-axis":
            {
                if (field.Byte is not int b) return;
                float v = field.Semantic switch
                {
                    "leftStickX"  => state.LeftStickX,
                    "leftStickY"  => state.LeftStickY,
                    "rightStickX" => state.RightStickX,
                    "rightStickY" => state.RightStickY,
                    _ => 0f,
                };
                int center = field.Center ?? 128;
                int raw = center + (int)Math.Round(Math.Clamp(v, -1f, 1f) * 127);
                buffer[b] = (byte)Math.Clamp(raw, 0, 255);
                break;
            }
            case "uint8-trigger":
            {
                if (field.Byte is not int b) return;
                float v = field.Semantic switch
                {
                    "leftTrigger"  => state.LeftTrigger,
                    "rightTrigger" => state.RightTrigger,
                    _ => 0f,
                };
                int raw = (int)Math.Round(Math.Clamp(v, 0f, 1f) * 255);
                buffer[b] = (byte)Math.Clamp(raw, 0, 255);
                break;
            }
            case "uint8-rolling":
            {
                if (field.Byte is not int b) return;
                string key = field.Semantic ?? $"_b{b}";
                if (!encState.RollingCounters.TryGetValue(key, out var counter))
                {
                    counter = (byte)(field.Initial ?? 0);
                }
                buffer[b] = counter;
                encState.RollingCounters[key] = unchecked((byte)(counter + 1));
                break;
            }
            case "uint8":
            {
                if (field.Byte is not int b) return;
                buffer[b] = (byte)(field.Initial ?? 0);
                break;
            }
            case "hat-octant":
            {
                // Encoded as 4-bit nibble at the declared bit range within the byte.
                // HMHat values 1..8 (N..NW) map to descriptor 0..7.
                // HMHat.None maps to neutralValue (typically 8).
                if (field.Byte is not int b) return;
                int neutral = field.NeutralValue ?? 8;
                int hatNibble = state.Hat == HMHat.None ? neutral : ((int)state.Hat - 1) & 0x0F;
                if (TryParseBitRange(field.Bits, out int bitLo, out int bitHi))
                {
                    int width = bitHi - bitLo + 1;
                    byte mask = (byte)(((1 << width) - 1) << bitLo);
                    buffer[b] = (byte)((buffer[b] & ~mask) | ((hatNibble << bitLo) & mask));
                }
                else
                {
                    buffer[b] = (byte)(hatNibble & 0xFF);
                }
                break;
            }
            case "button-mask":
            {
                if (field.Byte is not int b || field.Buttons is null) return;
                uint mask = (uint)state.Buttons;
                int bitLo = 0, bitHi = 7;
                if (TryParseBitRange(field.Bits, out int lo, out int hi))
                {
                    bitLo = lo; bitHi = hi;
                }
                byte packed = 0;
                for (int i = 0; i < field.Buttons.Count && (bitLo + i) <= bitHi; i++)
                {
                    string name = field.Buttons[i];
                    if (string.IsNullOrEmpty(name) || name == "_") continue; // skip placeholder
                    // Magic names for trigger-engaged digital buttons. DS4/DS5
                    // hardware reports L2/R2 as both analog axis AND digital
                    // button; the descriptor-driven encoder uses triggerButtons
                    // for this. Data-driven path uses these magic names to
                    // engage the bit when the corresponding state trigger > 0.
                    if (name == "LT_DIGITAL")
                    {
                        if (state.LeftTrigger > 0f) packed |= (byte)(1 << (bitLo + i));
                        continue;
                    }
                    if (name == "RT_DIGITAL")
                    {
                        if (state.RightTrigger > 0f) packed |= (byte)(1 << (bitLo + i));
                        continue;
                    }
                    if (Enum.TryParse<HMButton>(name, true, out var btn) && (mask & (uint)btn) != 0)
                        packed |= (byte)(1 << (bitLo + i));
                }
                // OR into existing byte (preserves any already-written bits at other positions)
                byte preserveMask = (byte)~(((1 << (bitHi - bitLo + 1)) - 1) << bitLo);
                buffer[b] = (byte)((buffer[b] & preserveMask) | packed);
                break;
            }
            case "rgb24":
            {
                // Reads RGB from a hypothetical state.LightbarRGB if present;
                // input direction typically doesn't carry RGB so skip silently.
                // Output direction uses EncodeOutput's parsed-fields path.
                break;
            }
            case "crc32-le":
            {
                if (field.Scope == null) return;
                var crc = ComputeCrc32(field.Scope, buffer);
                int dst = ParseDestStart(field, spec: null, fallback: buffer.Length - 4);
                buffer[dst + 0] = (byte)(crc       & 0xFF);
                buffer[dst + 1] = (byte)((crc >> 8 ) & 0xFF);
                buffer[dst + 2] = (byte)((crc >> 16) & 0xFF);
                buffer[dst + 3] = (byte)((crc >> 24) & 0xFF);
                break;
            }
            case "bytes-zero":
                // Default behavior — explicit form for clarity. No-op since
                // the buffer is already zeroed.
                break;
        }
    }

    // ── Output encoder: parsed fields → bytes ─────────────────────────────

    /// <summary>Encode a parsed-field dictionary into the byte buffer per the
    /// spec. Used by HMOutputEncoder for consumers that want to drive a real
    /// device from synthesized state without reimplementing byte layouts.</summary>
    public static byte[] EncodeOutput(
        ExtendedReportSpec spec,
        IReadOnlyDictionary<string, object> fields)
    {
        var buffer = new byte[spec.Size];
        buffer[0] = spec.ReportIdByte;

        foreach (var field in spec.Fields)
        {
            EncodeOutputField(field, buffer, fields);
        }
        return buffer;
    }

    private static void EncodeOutputField(
        FieldSpec field,
        byte[] buffer,
        IReadOnlyDictionary<string, object> fields)
    {
        // For output, the source of every value is the parsed-fields dict
        // keyed by semantic name. Unmapped fields stay zero.
        switch (field.Type)
        {
            case "uint8":
            case "uint8-rolling":
            {
                if (field.Byte is not int b) return;
                if (field.Semantic != null && fields.TryGetValue(field.Semantic, out var val))
                {
                    buffer[b] = ToByte(val);
                }
                else if (field.Initial.HasValue)
                {
                    // Constant byte the spec wants written even when the
                    // consumer's parsed-fields dict doesn't carry the
                    // semantic. Sony BT output's byte-2 framing-flag
                    // (0x10) is the canonical case: real firmware drops
                    // the effect packet if byte 2 isn't 0x10. Mirroring
                    // the input-side EncodeField behavior so consumers
                    // don't have to remember firmware-mandated constants.
                    buffer[b] = (byte)field.Initial.Value;
                }
                break;
            }
            case "uint8-axis":
            {
                if (field.Byte is not int b) return;
                if (field.Semantic == null) return;
                int center = field.Center ?? 128;
                if (fields.TryGetValue(field.Semantic, out var val))
                {
                    if (val is float f) buffer[b] = (byte)Math.Clamp(center + (int)Math.Round(f * 127), 0, 255);
                    else if (val is double d) buffer[b] = (byte)Math.Clamp(center + (int)Math.Round(d * 127), 0, 255);
                    else buffer[b] = ToByte(val);
                }
                else
                {
                    buffer[b] = (byte)center;
                }
                break;
            }
            case "uint8-trigger":
            {
                if (field.Byte is not int b) return;
                if (field.Semantic == null) return;
                if (fields.TryGetValue(field.Semantic, out var val))
                {
                    if (val is float f) buffer[b] = (byte)Math.Clamp((int)Math.Round(f * 255), 0, 255);
                    else if (val is double d) buffer[b] = (byte)Math.Clamp((int)Math.Round(d * 255), 0, 255);
                    else buffer[b] = ToByte(val);
                }
                break;
            }
            case "rgb24":
            {
                if (!TryParseByteRange(field.Bytes, out int rangeLo, out int rangeHi)) return;
                if (field.Semantic == null) return;
                if (fields.TryGetValue(field.Semantic, out var val))
                {
                    if (val is byte[] arr && arr.Length >= 3)
                    {
                        buffer[rangeLo + 0] = arr[0];
                        buffer[rangeLo + 1] = arr[1];
                        buffer[rangeLo + 2] = arr[2];
                    }
                    else if (val is uint packed)
                    {
                        buffer[rangeLo + 0] = (byte)((packed >> 16) & 0xFF);
                        buffer[rangeLo + 1] = (byte)((packed >>  8) & 0xFF);
                        buffer[rangeLo + 2] = (byte)( packed        & 0xFF);
                    }
                }
                break;
            }
            case "bytes-passthrough":
            {
                if (!TryParseByteRange(field.Bytes, out int rangeLo, out int rangeHi)) return;
                if (field.Semantic == null) return;
                if (fields.TryGetValue(field.Semantic, out var val) && val is byte[] arr)
                {
                    int n = Math.Min(arr.Length, rangeHi - rangeLo + 1);
                    Buffer.BlockCopy(arr, 0, buffer, rangeLo, n);
                }
                break;
            }
            case "bytes-zero":
                break;
            case "crc32-le":
            {
                if (field.Scope == null) return;
                var crc = ComputeCrc32(field.Scope, buffer);
                int dst = ParseDestStart(field, spec: null, fallback: buffer.Length - 4);
                buffer[dst + 0] = (byte)(crc       & 0xFF);
                buffer[dst + 1] = (byte)((crc >> 8 ) & 0xFF);
                buffer[dst + 2] = (byte)((crc >> 16) & 0xFF);
                buffer[dst + 3] = (byte)((crc >> 24) & 0xFF);
                break;
            }
        }
    }

    // ── Decoder: bytes → parsed fields ────────────────────────────────────

    /// <summary>Decode a byte buffer into a parsed-field dictionary per the
    /// spec. Used by HMController.OnOutputReceived to surface incoming output
    /// reports as named values to consumers via the OutputDecoded event.</summary>
    public static (Dictionary<string, object> fields, bool crcValid) Decode(
        ExtendedReportSpec spec,
        ReadOnlySpan<byte> buffer)
    {
        var result = new Dictionary<string, object>();
        bool crcValid = true;

        foreach (var field in spec.Fields)
        {
            switch (field.Type)
            {
                case "uint8":
                case "uint8-rolling":
                {
                    if (field.Byte is not int b || field.Semantic == null) continue;
                    if ((uint)b >= (uint)buffer.Length) continue;
                    result[field.Semantic] = buffer[b];
                    break;
                }
                case "uint8-axis":
                {
                    if (field.Byte is not int b || field.Semantic == null) continue;
                    if ((uint)b >= (uint)buffer.Length) continue;
                    int center = field.Center ?? 128;
                    result[field.Semantic] = (float)((buffer[b] - center) / 127.0);
                    break;
                }
                case "uint8-trigger":
                {
                    if (field.Byte is not int b || field.Semantic == null) continue;
                    if ((uint)b >= (uint)buffer.Length) continue;
                    result[field.Semantic] = (float)(buffer[b] / 255.0);
                    break;
                }
                case "hat-octant":
                {
                    if (field.Byte is not int b || field.Semantic == null) continue;
                    if ((uint)b >= (uint)buffer.Length) continue;
                    int raw;
                    if (TryParseBitRange(field.Bits, out int lo, out int hi))
                    {
                        int width = hi - lo + 1;
                        int mask = (1 << width) - 1;
                        raw = (buffer[b] >> lo) & mask;
                    }
                    else
                    {
                        raw = buffer[b];
                    }
                    int neutral = field.NeutralValue ?? 8;
                    result[field.Semantic] = raw == neutral ? (byte)0 : (byte)((raw + 1) & 0xFF);
                    break;
                }
                case "button-mask":
                {
                    if (field.Byte is not int b || field.Buttons == null) continue;
                    if ((uint)b >= (uint)buffer.Length) continue;
                    int bitLo = 0;
                    if (TryParseBitRange(field.Bits, out int lo, out _)) bitLo = lo;
                    var pressed = new List<string>();
                    for (int i = 0; i < field.Buttons.Count; i++)
                    {
                        if (((buffer[b] >> (bitLo + i)) & 1) != 0
                            && !string.IsNullOrEmpty(field.Buttons[i])
                            && field.Buttons[i] != "_")
                        {
                            pressed.Add(field.Buttons[i]);
                        }
                    }
                    string semantic = field.Semantic ?? $"buttons_b{b}";
                    result[semantic] = pressed;
                    break;
                }
                case "rgb24":
                {
                    if (!TryParseByteRange(field.Bytes, out int rangeLo, out int _) || field.Semantic == null) continue;
                    if (rangeLo + 2 >= buffer.Length) continue;
                    result[field.Semantic] = new byte[] { buffer[rangeLo], buffer[rangeLo + 1], buffer[rangeLo + 2] };
                    break;
                }
                case "bytes-passthrough":
                {
                    if (!TryParseByteRange(field.Bytes, out int rangeLo, out int rangeHi) || field.Semantic == null) continue;
                    if (rangeHi >= buffer.Length) continue;
                    int n = rangeHi - rangeLo + 1;
                    var slice = new byte[n];
                    buffer.Slice(rangeLo, n).CopyTo(slice);
                    result[field.Semantic] = slice;
                    break;
                }
                case "crc32-le":
                {
                    if (field.Scope == null) continue;
                    int dst = ParseDestStart(field, spec: null, fallback: buffer.Length - 4);
                    if (dst + 3 >= buffer.Length) continue;
                    uint observed = (uint)buffer[dst]
                                  | ((uint)buffer[dst + 1] << 8)
                                  | ((uint)buffer[dst + 2] << 16)
                                  | ((uint)buffer[dst + 3] << 24);
                    var bufArr = buffer.ToArray();
                    uint expected = ComputeCrc32(field.Scope, bufArr);
                    crcValid = observed == expected;
                    break;
                }
            }
        }
        return (result, crcValid);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool TryParseBitRange(string? bits, out int lo, out int hi)
    {
        lo = 0; hi = 7;
        if (string.IsNullOrEmpty(bits)) return false;
        var parts = bits.Split('-');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], out lo) & int.TryParse(parts[1], out hi);
    }

    private static bool TryParseByteRange(string? bytes, out int lo, out int hi)
    {
        lo = 0; hi = 0;
        if (string.IsNullOrEmpty(bytes)) return false;
        var parts = bytes.Split('-');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], out lo) & int.TryParse(parts[1], out hi);
    }

    private static int ParseDestStart(FieldSpec field, ExtendedReportSpec? spec, int fallback)
    {
        if (TryParseByteRange(field.Bytes, out int lo, out int _)) return lo;
        if (field.Byte is int b) return b;
        return fallback;
    }

    // CRC-32/ISO-HDLC, polynomial 0xEDB88320 (matches Sony BT, dualsense-tester,
    // ds4drv, hidapi, OpenRGB SonyDualSenseController, PadForge Ds5RawHidWriter).
    // Inline table to avoid pulling System.IO.Hashing — single-file deployment
    // works without an extra NuGet dep.
    private static readonly uint[] s_crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            t[i] = c;
        }
        return t;
    }

    private static uint ComputeCrc32(CrcScope scope, byte[] buffer)
    {
        uint crc = 0xFFFFFFFFu;
        if (scope.Prefix != null)
        {
            for (int i = 0; i < scope.Prefix.Count; i++)
                crc = s_crc32Table[(crc ^ scope.Prefix[i]) & 0xFF] ^ (crc >> 8);
        }
        int from = scope.From;
        int to = Math.Min(scope.To, buffer.Length - 1);
        for (int i = from; i <= to; i++)
            crc = s_crc32Table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static byte ToByte(object val)
    {
        return val switch
        {
            byte b => b,
            sbyte sb => (byte)sb,
            short s => (byte)s,
            ushort us => (byte)us,
            int i => (byte)i,
            uint u => (byte)u,
            long l => (byte)l,
            ulong ul => (byte)ul,
            _ => 0,
        };
    }
}
