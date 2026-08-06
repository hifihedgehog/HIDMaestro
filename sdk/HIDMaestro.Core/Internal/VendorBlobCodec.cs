using System;
using System.Collections.Generic;

namespace HIDMaestro.Internal;

/// <summary>v1.3.5: generic vendor-blob HID report encoder/decoder. Walks an
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
/// <para>Since issue #34 the per-frame loops run over a
/// <see cref="VendorBlobProgram"/>: the spec's strings (type, semantic,
/// button names, bit/byte ranges) are compiled to numeric opcodes once per
/// spec, and each frame is a switch over enums with no string parsing.
/// Byte parity with the pre-compiled implementation is locked by
/// <c>test/probes/vendor_blob_golden_check</c>.</para>
///
/// <para>CRC32 uses CRC-32/ISO-HDLC (poly 0xEDB88320), matching Sony's wire
/// format and dualsense-tester / ds4drv reference impls.</para>
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
    /// Buffer is zeroed first; report ID byte is written at offset 0.
    ///
    /// <para>v1.3.9: caller passes the pre-resolved 6 simple-slot values
    /// (left stick X/Y, right stick X/Y, LT, RT) in <c>[0..1]</c> range.
    /// HMController.SubmitState resolves these from
    /// <see cref="HMGamepadState.Axes"/> via the profile's
    /// <see cref="HMProfile.Sticks"/> / <see cref="HMProfile.Triggers"/>.
    /// All other state fields (touchpad, IMU, battery, hat, buttons) are
    /// read directly from <paramref name="state"/>.</para></summary>
    public static void EncodeInput(
        ExtendedReportSpec spec,
        in HMGamepadState state,
        float leftStickX, float leftStickY,
        float rightStickX, float rightStickY,
        float leftTrigger, float rightTrigger,
        byte[] buffer,
        EncoderState encState)
    {
        if (buffer.Length < spec.Size)
            throw new ArgumentException($"Buffer too small: need {spec.Size}, got {buffer.Length}");

        Array.Clear(buffer, 0, spec.Size);
        buffer[0] = spec.ReportIdByte;

        var prog = VendorBlobProgram.Get(spec);
        var fields = prog.Fields;
        for (int fi = 0; fi < fields.Length; fi++)
        {
            ref readonly var f = ref fields[fi];
            switch (f.Op)
            {
                case VendorBlobProgram.FieldOp.U8Axis:
                {
                    if (f.B < 0) break;
                    // v1.3.9: sticks are [0..1] uniformly (0.5 = center).
                    // The "center" override stays for vendor blobs that
                    // place the on-wire center somewhere other than 128.
                    float v = f.Source switch
                    {
                        VendorBlobProgram.SrcOp.LeftStickX  => leftStickX,
                        VendorBlobProgram.SrcOp.LeftStickY  => leftStickY,
                        VendorBlobProgram.SrcOp.RightStickX => rightStickX,
                        VendorBlobProgram.SrcOp.RightStickY => rightStickY,
                        _ => 0.5f,
                    };
                    int raw = (int)Math.Round(Math.Clamp(v, 0f, 1f) * 255);
                    if (f.Center != 128)
                        raw = f.Center + (int)Math.Round((Math.Clamp(v, 0f, 1f) - 0.5f) * 254);
                    buffer[f.B] = (byte)Math.Clamp(raw, 0, 255);
                    break;
                }
                case VendorBlobProgram.FieldOp.U8Trigger:
                {
                    if (f.B < 0) break;
                    float v = f.Source switch
                    {
                        VendorBlobProgram.SrcOp.LeftTrigger  => leftTrigger,
                        VendorBlobProgram.SrcOp.RightTrigger => rightTrigger,
                        _ => 0f,
                    };
                    buffer[f.B] = (byte)Math.Clamp((int)Math.Round(Math.Clamp(v, 0f, 1f) * 255), 0, 255);
                    break;
                }
                case VendorBlobProgram.FieldOp.U8Rolling:
                {
                    if (f.B < 0) break;
                    if (!encState.RollingCounters.TryGetValue(f.RollKey, out var counter))
                        counter = f.Initial;
                    buffer[f.B] = counter;
                    encState.RollingCounters[f.RollKey] = unchecked((byte)(counter + f.Stride));
                    break;
                }
                case VendorBlobProgram.FieldOp.U8Const:
                {
                    if (f.B < 0) break;
                    buffer[f.B] = f.Initial;
                    break;
                }
                case VendorBlobProgram.FieldOp.I16:
                {
                    if (f.B < 0 || f.B + 1 >= buffer.Length) break;
                    short v = f.Source switch
                    {
                        VendorBlobProgram.SrcOp.GyroPitch => state.GyroPitch,
                        VendorBlobProgram.SrcOp.GyroYaw   => state.GyroYaw,
                        VendorBlobProgram.SrcOp.GyroRoll  => state.GyroRoll,
                        VendorBlobProgram.SrcOp.AccelX    => state.AccelX,
                        VendorBlobProgram.SrcOp.AccelY    => state.AccelY,
                        VendorBlobProgram.SrcOp.AccelZ    => state.AccelZ,
                        _ => (short)0,
                    };
                    buffer[f.B]     = (byte)(v & 0xFF);
                    buffer[f.B + 1] = (byte)((v >> 8) & 0xFF);
                    break;
                }
                case VendorBlobProgram.FieldOp.U32:
                {
                    if (f.B < 0 || f.B + 3 >= buffer.Length) break;
                    uint v = f.Source == VendorBlobProgram.SrcOp.SensorTimestamp
                        ? state.SensorTimestamp : 0u;
                    buffer[f.B]     = (byte)(v        & 0xFF);
                    buffer[f.B + 1] = (byte)((v >>  8) & 0xFF);
                    buffer[f.B + 2] = (byte)((v >> 16) & 0xFF);
                    buffer[f.B + 3] = (byte)((v >> 24) & 0xFF);
                    break;
                }
                case VendorBlobProgram.FieldOp.TouchpadFinger:
                {
                    // Sony two-finger packet: 4 bytes per finger.
                    //   byte 0: bit 7 = lifted (1 = not touching), bits 0-6 = tracking ID
                    //   byte 1: X low 8 bits
                    //   byte 2: bits 0-3 = X high 4 bits, bits 4-7 = Y low 4 bits
                    //   byte 3: Y high 8 bits
                    if (f.B < 0 || f.B + 3 >= buffer.Length) break;
                    bool active; ushort x, y; byte id;
                    if (f.Source == VendorBlobProgram.SrcOp.Finger1)
                    {
                        active = state.TouchpadFinger1Active;
                        x = state.TouchpadFinger1X; y = state.TouchpadFinger1Y;
                        id = state.TouchpadFinger1Id;
                    }
                    else
                    {
                        active = state.TouchpadFinger0Active;
                        x = state.TouchpadFinger0X; y = state.TouchpadFinger0Y;
                        id = state.TouchpadFinger0Id;
                    }
                    buffer[f.B]     = (byte)((id & 0x7F) | (active ? 0x00 : 0x80));
                    buffer[f.B + 1] = (byte)(x & 0xFF);
                    buffer[f.B + 2] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4));
                    buffer[f.B + 3] = (byte)((y >> 4) & 0xFF);
                    break;
                }
                case VendorBlobProgram.FieldOp.Bitfield:
                {
                    if (f.B < 0 || (uint)f.B >= (uint)buffer.Length || f.FlagKinds == null) break;
                    byte packed = 0;
                    for (int i = 0; i < f.FlagKinds.Length && (f.BitLo + i) <= f.BitHi; i++)
                    {
                        bool bit = f.FlagKinds[i] switch
                        {
                            VendorBlobProgram.FlagCharging   => state.BatteryCharging,
                            VendorBlobProgram.FlagFull       => state.BatteryFull,
                            VendorBlobProgram.FlagMic        => state.MicMuted,
                            VendorBlobProgram.FlagHeadphones => state.HeadphonesConnected,
                            _ => false,
                        };
                        if (bit) packed |= (byte)(1 << (f.BitLo + i));
                    }
                    byte preserveMask = (byte)~(((1 << (f.BitHi - f.BitLo + 1)) - 1) << f.BitLo);
                    buffer[f.B] = (byte)((buffer[f.B] & preserveMask) | packed);
                    break;
                }
                case VendorBlobProgram.FieldOp.Battery:
                {
                    // BatteryLevel byte, optionally sub-bit-ranged (Sony packs
                    // capacity in the low nibble, charging+full in the high).
                    if (f.B < 0 || (uint)f.B >= (uint)buffer.Length) break;
                    int width = f.BitHi - f.BitLo + 1;
                    byte mask = (byte)(((1 << width) - 1) << f.BitLo);
                    byte v = (byte)(state.BatteryLevel & ((1 << width) - 1));
                    buffer[f.B] = (byte)((buffer[f.B] & ~mask) | ((v << f.BitLo) & mask));
                    break;
                }
                case VendorBlobProgram.FieldOp.HatOctant:
                {
                    // HMHat values 1..8 (N..NW) map to descriptor 0..7;
                    // HMHat.None maps to the neutral value (typically 8).
                    if (f.B < 0) break;
                    int hatNibble = state.Hat == HMHat.None ? f.Neutral : ((int)state.Hat - 1) & 0x0F;
                    if (f.HasBits)
                    {
                        int width = f.BitHi - f.BitLo + 1;
                        byte mask = (byte)(((1 << width) - 1) << f.BitLo);
                        buffer[f.B] = (byte)((buffer[f.B] & ~mask) | ((hatNibble << f.BitLo) & mask));
                    }
                    else
                    {
                        buffer[f.B] = (byte)(hatNibble & 0xFF);
                    }
                    break;
                }
                case VendorBlobProgram.FieldOp.ButtonMask:
                {
                    if (f.B < 0 || f.ButtonBits == null) break;
                    uint mask = (uint)state.Buttons;
                    byte packed = 0;
                    for (int i = 0; i < f.ButtonBits.Length && (f.BitLo + i) <= f.BitHi; i++)
                    {
                        ulong bits = f.ButtonBits[i];
                        if (bits == 0) continue;
                        // Magic trigger-engaged digital buttons: DS4/DS5
                        // report L2/R2 as both analog axis AND digital button.
                        bool on = bits switch
                        {
                            VendorBlobProgram.ButtonLtDigital => leftTrigger > 0f,
                            VendorBlobProgram.ButtonRtDigital => rightTrigger > 0f,
                            _ => (mask & (uint)bits) != 0,
                        };
                        if (on) packed |= (byte)(1 << (f.BitLo + i));
                    }
                    // OR into existing byte (preserves bits at other positions)
                    byte preserveMask = (byte)~(((1 << (f.BitHi - f.BitLo + 1)) - 1) << f.BitLo);
                    buffer[f.B] = (byte)((buffer[f.B] & preserveMask) | packed);
                    break;
                }
                case VendorBlobProgram.FieldOp.Crc32:
                {
                    if (f.Src.Scope == null) break;
                    var crc = ComputeCrc32(f.Src.Scope, buffer);
                    // -1 sentinel: no declared dest; the old string impl
                    // resolved buffer.Length - 4 at run time. Preserved.
                    int dst = f.CrcDst >= 0 ? f.CrcDst : buffer.Length - 4;
                    buffer[dst + 0] = (byte)(crc       & 0xFF);
                    buffer[dst + 1] = (byte)((crc >> 8 ) & 0xFF);
                    buffer[dst + 2] = (byte)((crc >> 16) & 0xFF);
                    buffer[dst + 3] = (byte)((crc >> 24) & 0xFF);
                    break;
                }
                // Rgb24: input direction carries no RGB source; skip (the
                // output direction consumes it via the parsed-fields path).
                // BytesZero / BytesPassthrough / Unknown: buffer already
                // zeroed; unknown types stay a silent no-op by contract.
                default:
                    break;
            }
        }
    }

    // ── Output encoder: parsed fields → bytes ─────────────────────────────

    /// <summary>Encode a parsed-field dictionary into the byte buffer per the
    /// spec. Used by HMOutputEncoder for consumers that want to drive a real
    /// device from synthesized state without reimplementing byte layouts.
    ///
    /// <para><paramref name="encState"/> may be null for stateless callers
    /// (the historical signature). When supplied, <c>uint8-rolling</c> fields
    /// without a matching dict entry advance an internal rolling counter so
    /// the spec can own the framingTag pattern (Sony BT effect output's
    /// <c>btTag</c> stride-16 cycle is the canonical case).</para></summary>
    public static byte[] EncodeOutput(
        ExtendedReportSpec spec,
        IReadOnlyDictionary<string, object> fields,
        EncoderState? encState = null)
    {
        var buffer = new byte[spec.Size];
        buffer[0] = spec.ReportIdByte;

        var prog = VendorBlobProgram.Get(spec);
        var compiled = prog.Fields;
        for (int fi = 0; fi < compiled.Length; fi++)
        {
            ref readonly var f = ref compiled[fi];
            var field = f.Src;
            // The source of every value is the parsed-fields dict keyed by
            // semantic name. Unmapped fields stay zero.
            switch (f.Op)
            {
                case VendorBlobProgram.FieldOp.U8Const:
                {
                    if (f.B < 0) break;
                    if (field.Semantic != null && fields.TryGetValue(field.Semantic, out var val))
                        buffer[f.B] = ToByte(val);
                    else if (field.Initial.HasValue)
                        // Constant byte the spec wants written even when the
                        // consumer's dict doesn't carry the semantic. Sony BT
                        // output's byte-2 framing-flag (0x10) is the canonical
                        // case: real firmware drops the packet without it.
                        buffer[f.B] = f.Initial;
                    break;
                }
                case VendorBlobProgram.FieldOp.U8Rolling:
                {
                    if (f.B < 0) break;
                    // An explicit consumer value overrides the auto-advance
                    // (diagnostic fixtures).
                    if (field.Semantic != null && fields.TryGetValue(field.Semantic, out var val))
                    {
                        buffer[f.B] = ToByte(val);
                        break;
                    }
                    // Auto-advance by stride. Required for Sony BT btTag.
                    // Real firmware silently drops the packet otherwise.
                    if (encState != null)
                    {
                        string key = field.Semantic ?? $"_o{f.B}";
                        if (!encState.RollingCounters.TryGetValue(key, out var counter))
                            counter = f.Initial;
                        buffer[f.B] = counter;
                        encState.RollingCounters[key] = unchecked((byte)(counter + f.Stride));
                    }
                    else if (field.Initial.HasValue)
                    {
                        buffer[f.B] = f.Initial;
                    }
                    break;
                }
                case VendorBlobProgram.FieldOp.U8Axis:
                {
                    if (f.B < 0 || field.Semantic == null) break;
                    if (fields.TryGetValue(field.Semantic, out var val))
                    {
                        if (val is float fv) buffer[f.B] = (byte)Math.Clamp(f.Center + (int)Math.Round(fv * 127), 0, 255);
                        else if (val is double d) buffer[f.B] = (byte)Math.Clamp(f.Center + (int)Math.Round(d * 127), 0, 255);
                        else buffer[f.B] = ToByte(val);
                    }
                    else
                    {
                        buffer[f.B] = (byte)f.Center;
                    }
                    break;
                }
                case VendorBlobProgram.FieldOp.U8Trigger:
                {
                    if (f.B < 0 || field.Semantic == null) break;
                    if (fields.TryGetValue(field.Semantic, out var val))
                    {
                        if (val is float fv) buffer[f.B] = (byte)Math.Clamp((int)Math.Round(fv * 255), 0, 255);
                        else if (val is double d) buffer[f.B] = (byte)Math.Clamp((int)Math.Round(d * 255), 0, 255);
                        else buffer[f.B] = ToByte(val);
                    }
                    break;
                }
                case VendorBlobProgram.FieldOp.Rgb24:
                {
                    if (f.RangeLo < 0 || field.Semantic == null) break;
                    if (fields.TryGetValue(field.Semantic, out var val))
                    {
                        if (val is byte[] arr && arr.Length >= 3)
                        {
                            buffer[f.RangeLo + 0] = arr[0];
                            buffer[f.RangeLo + 1] = arr[1];
                            buffer[f.RangeLo + 2] = arr[2];
                        }
                        else if (val is uint packed)
                        {
                            buffer[f.RangeLo + 0] = (byte)((packed >> 16) & 0xFF);
                            buffer[f.RangeLo + 1] = (byte)((packed >>  8) & 0xFF);
                            buffer[f.RangeLo + 2] = (byte)( packed        & 0xFF);
                        }
                    }
                    break;
                }
                case VendorBlobProgram.FieldOp.BytesPassthrough:
                {
                    if (f.RangeLo < 0 || field.Semantic == null) break;
                    if (fields.TryGetValue(field.Semantic, out var val) && val is byte[] arr)
                    {
                        int n = Math.Min(arr.Length, f.RangeHi - f.RangeLo + 1);
                        Buffer.BlockCopy(arr, 0, buffer, f.RangeLo, n);
                    }
                    break;
                }
                case VendorBlobProgram.FieldOp.Crc32:
                {
                    if (field.Scope == null) break;
                    var crc = ComputeCrc32(field.Scope, buffer);
                    // -1 sentinel: resolve from the live buffer (see EncodeInput).
                    int dst = f.CrcDst >= 0 ? f.CrcDst : buffer.Length - 4;
                    buffer[dst + 0] = (byte)(crc       & 0xFF);
                    buffer[dst + 1] = (byte)((crc >> 8 ) & 0xFF);
                    buffer[dst + 2] = (byte)((crc >> 16) & 0xFF);
                    buffer[dst + 3] = (byte)((crc >> 24) & 0xFF);
                    break;
                }
                // BytesZero and every unknown/input-only type: leave the
                // field's bytes at their zeroed default.
                default:
                    break;
            }
        }
        return buffer;
    }

    // ── Decoder: bytes → parsed fields ────────────────────────────────────

    /// <summary>Decode a byte buffer into a parsed-field dictionary per the
    /// spec. Used by HMController.OnOutputReceived to surface incoming output
    /// reports as named values to consumers via the OutputDecoded event.</summary>
    public static (Dictionary<string, object> fields, bool crcValid) Decode(
        ExtendedReportSpec spec,
        ReadOnlySpan<byte> buffer)
    {
        var prog = VendorBlobProgram.Get(spec);
        // Pre-sized (issue #34): the compiled program knows how many fields
        // produce entries, so the dictionary never rehashes mid-decode.
        var result = new Dictionary<string, object>(prog.DecodeFieldCount);
        bool crcValid = true;

        var compiled = prog.Fields;
        for (int fi = 0; fi < compiled.Length; fi++)
        {
            ref readonly var f = ref compiled[fi];
            var field = f.Src;
            switch (f.Op)
            {
                case VendorBlobProgram.FieldOp.U8Const:
                case VendorBlobProgram.FieldOp.U8Rolling:
                {
                    if (f.B < 0 || field.Semantic == null) continue;
                    if ((uint)f.B >= (uint)buffer.Length) continue;
                    result[field.Semantic] = buffer[f.B];
                    break;
                }
                case VendorBlobProgram.FieldOp.U8Axis:
                {
                    if (f.B < 0 || field.Semantic == null) continue;
                    if ((uint)f.B >= (uint)buffer.Length) continue;
                    result[field.Semantic] = (float)((buffer[f.B] - f.Center) / 127.0);
                    break;
                }
                case VendorBlobProgram.FieldOp.U8Trigger:
                {
                    if (f.B < 0 || field.Semantic == null) continue;
                    if ((uint)f.B >= (uint)buffer.Length) continue;
                    result[field.Semantic] = (float)(buffer[f.B] / 255.0);
                    break;
                }
                case VendorBlobProgram.FieldOp.HatOctant:
                {
                    if (f.B < 0 || field.Semantic == null) continue;
                    if ((uint)f.B >= (uint)buffer.Length) continue;
                    int raw;
                    if (f.HasBits)
                    {
                        int width = f.BitHi - f.BitLo + 1;
                        int mask = (1 << width) - 1;
                        raw = (buffer[f.B] >> f.BitLo) & mask;
                    }
                    else
                    {
                        raw = buffer[f.B];
                    }
                    result[field.Semantic] = raw == f.Neutral ? (byte)0 : (byte)((raw + 1) & 0xFF);
                    break;
                }
                case VendorBlobProgram.FieldOp.ButtonMask:
                {
                    if (f.B < 0 || field.Buttons == null) continue;
                    if ((uint)f.B >= (uint)buffer.Length) continue;
                    var pressed = new List<string>();
                    for (int i = 0; i < field.Buttons.Count; i++)
                    {
                        if (((buffer[f.B] >> (f.BitLo + i)) & 1) != 0
                            && !string.IsNullOrEmpty(field.Buttons[i])
                            && field.Buttons[i] != "_")
                        {
                            pressed.Add(field.Buttons[i]);
                        }
                    }
                    string semantic = field.Semantic ?? $"buttons_b{f.B}";
                    result[semantic] = pressed;
                    break;
                }
                case VendorBlobProgram.FieldOp.Rgb24:
                {
                    if (f.RangeLo < 0 || field.Semantic == null) continue;
                    if (f.RangeLo + 2 >= buffer.Length) continue;
                    result[field.Semantic] = new byte[]
                    {
                        buffer[f.RangeLo], buffer[f.RangeLo + 1], buffer[f.RangeLo + 2],
                    };
                    break;
                }
                case VendorBlobProgram.FieldOp.BytesPassthrough:
                {
                    if (f.RangeLo < 0 || field.Semantic == null) continue;
                    if (f.RangeHi >= buffer.Length) continue;
                    int n = f.RangeHi - f.RangeLo + 1;
                    var slice = new byte[n];
                    buffer.Slice(f.RangeLo, n).CopyTo(slice);
                    result[field.Semantic] = slice;
                    break;
                }
                case VendorBlobProgram.FieldOp.Crc32:
                {
                    if (field.Scope == null) continue;
                    // -1 sentinel: the RECEIVED report's length rules here.
                    // A short host write must still get its CRC validated
                    // at the actual buffer end, exactly like the old impl.
                    int dst = f.CrcDst >= 0 ? f.CrcDst : buffer.Length - 4;
                    if (dst < 0 || dst + 3 >= buffer.Length) continue;
                    uint observed = (uint)buffer[dst]
                                  | ((uint)buffer[dst + 1] << 8)
                                  | ((uint)buffer[dst + 2] << 16)
                                  | ((uint)buffer[dst + 3] << 24);
                    // Span-based CRC (issue #34): the string implementation
                    // did buffer.ToArray() per decode purely to feed the
                    // byte[] CRC helper.
                    uint expected = ComputeCrc32(field.Scope, buffer);
                    crcValid = observed == expected;
                    break;
                }
                // Unknown/unhandled decode types: omit the field from the
                // result dict rather than throwing.
                default:
                    break;
            }
        }
        return (result, crcValid);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    // CRC-32/ISO-HDLC, polynomial 0xEDB88320 (matches Sony BT, dualsense-tester,
    // ds4drv, hidapi, OpenRGB SonyDualSenseController, PadForge Ds5RawHidWriter).
    // Inline table to avoid pulling System.IO.Hashing, so single-file
    // deployment works without an extra NuGet dep.
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

    private static uint ComputeCrc32(CrcScope scope, ReadOnlySpan<byte> buffer)
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
