using System;

namespace HIDMaestro.Internal;

/// <summary>
/// Switch Pro Controller wire-format packing and rumble decoding
/// (issue #33). Hardcoded and keyed on VID 0x057E PID 0x2009 per the
/// spec's recommendation: the state machine is protocol, not layout, so
/// no JSON DSL. The driver-side responder (driver.c, SwitchHandle*) owns
/// the request-reply state machine; this class owns the byte layouts the
/// SDK touches:
///
///   - the input report 0x30 BODY the consumer's SubmitState produces
///     (buttons 3B + 12-bit packed sticks 6B + IMU 3x12B), which the
///     driver's 60 Hz streamer serves with its own timer/battery overlay,
///   - the coarse HD-rumble amplitude decode for OutputDecoded.
///
/// Byte layout grounded in SDL_hidapi_switch.c (the client under test):
/// SwitchControllerStatePacket_t + SwitchControllerIMUState_t[3], button
/// bits per HandleFullControllerState, 12-bit little-nibble stick packing
/// per its axis extraction, wire Y up-positive (SDL sends LEFTY as
/// ~axis). Rumble encoding per dekuNukem rumble_data_table.md as
/// implemented by SDL's EncodeRumbleHighAmplitude / EncodeRumbleLowAmplitude.
/// </summary>
internal static class SwitchProPacker
{
    public const int BodySize = 48;

    public static bool IsSwitchPro(ushort vid, ushort pid)
        => vid == 0x057E && pid == 0x2009;

    // Button indices per profiles/nintendo/switch-pro.json layout:
    // 0=B 1=A 2=Y 3=X 4=L 5=R 6=ZL 7=ZR 8=Minus 9=Plus 10=LStick
    // 11=RStick 12=Home 13=Capture.
    //
    // Wire bits per SDL HandleFullControllerState:
    //   byte0 (right): bit0=Y bit1=X bit2=B bit3=A bit6=R bit7=ZR
    //   byte1 (shared): bit0=Minus bit1=Plus bit2=RStick bit3=LStick
    //                   bit4=Home bit5=Capture
    //   byte2 (left):  bit0=Down bit1=Up bit2=Right bit3=Left
    //                  bit6=L bit7=ZL
    private static byte ButtonsByte0(uint m) => (byte)(
        (Bit(m, 2) << 0) | (Bit(m, 3) << 1) | (Bit(m, 0) << 2) | (Bit(m, 1) << 3)
        | (Bit(m, 5) << 6) | (Bit(m, 7) << 7));

    private static byte ButtonsByte1(uint m) => (byte)(
        (Bit(m, 8) << 0) | (Bit(m, 9) << 1) | (Bit(m, 11) << 2) | (Bit(m, 10) << 3)
        | (Bit(m, 12) << 4) | (Bit(m, 13) << 5));

    private static byte ButtonsByte2(uint m, bool up, bool down, bool left, bool right) => (byte)(
        (down ? 0x01u : 0u) | (up ? 0x02u : 0u) | (right ? 0x04u : 0u) | (left ? 0x08u : 0u)
        | (Bit(m, 4) << 6) | (Bit(m, 6) << 7));

    private static uint Bit(uint mask, int index) => (mask >> index) & 1u;

    /// <summary>Build the 48-byte report-0x30 body.
    /// dst[0]=counter (driver overlays), [1]=battery (driver overlays),
    /// [2..4]=buttons, [5..7]=left stick, [8..10]=right stick,
    /// [11]=vibrator (driver overlays), [12..47]=IMU 3 frames x 12 bytes.
    /// Stick floats are the SubmitState-resolved 0..1 values with the
    /// SDK's HID convention (y=0 is up); the wire wants up-positive, so
    /// Y inverts here. Center 0x800 / range 0x600 matches the fabricated
    /// factory calibration the driver serves from SPI 0x603D.</summary>
    public static void BuildBody(in HMGamepadState state,
                                 double lx, double ly, double rx, double ry,
                                 byte[] dst)
    {
        Array.Clear(dst, 0, BodySize);

        uint m = (uint)state.Buttons;
        ResolveDpad(in state, out bool up, out bool down, out bool left, out bool right);
        dst[2] = ButtonsByte0(m);
        dst[3] = ButtonsByte1(m);
        dst[4] = ButtonsByte2(m, up, down, left, right);

        PackStick(dst, 5, StickRaw(lx, invert: false), StickRaw(ly, invert: true));
        PackStick(dst, 8, StickRaw(rx, invert: false), StickRaw(ry, invert: true));

        // IMU: three 5 ms frames per report. One SubmitState carries one
        // sample; emit it three times (SDL keys sensor delivery on frame
        // content, and its per-frame timestamps advance regardless).
        short ax = ImuRaw(state.AccelGX, 4096.0f);
        short ay = ImuRaw(state.AccelGY, 4096.0f);
        short az = ImuRaw(state.AccelGZ, 4096.0f);
        // Gyro raw = deg/s x 13371/936: the exact inverse of SDL's
        // LoadIMUCalibration scale with the coefficients the driver's
        // fabricated SPI image serves (0x343B, zero origin).
        const float gyroScale = 13371.0f / 936.0f;
        short gx = ImuRaw(state.GyroDpsX, gyroScale);
        short gy = ImuRaw(state.GyroDpsY, gyroScale);
        short gz = ImuRaw(state.GyroDpsZ, gyroScale);
        for (int frame = 0; frame < 3; frame++)
        {
            int o = 12 + frame * 12;
            WriteInt16(dst, o + 0, ax);
            WriteInt16(dst, o + 2, ay);
            WriteInt16(dst, o + 4, az);
            WriteInt16(dst, o + 6, gx);
            WriteInt16(dst, o + 8, gy);
            WriteInt16(dst, o + 10, gz);
        }
    }

    private static void ResolveDpad(in HMGamepadState state,
                                    out bool up, out bool down, out bool left, out bool right)
    {
        // Same priority chain BuildReportInto documents:
        // HatDegrees > HatHundredths > HatRaw > Hat.
        double? deg = null;
        if (state.HatDegrees.HasValue) deg = state.HatDegrees.Value;
        else if (state.HatHundredths.HasValue) deg = state.HatHundredths.Value / 100.0;
        else if (state.HatRaw.HasValue)
        {
            // 8-way raw: 0..7 = N..NW clockwise, >=8 = neutral.
            if (state.HatRaw.Value < 8) deg = state.HatRaw.Value * 45.0;
        }
        else if (state.Hat != HMHat.None) deg = ((int)state.Hat - 1) * 45.0;

        up = down = left = right = false;
        if (!deg.HasValue) return;
        double d = ((deg.Value % 360.0) + 360.0) % 360.0;
        up = d > 292.5 || d < 67.5;
        right = d > 22.5 && d < 157.5;
        down = d > 112.5 && d < 247.5;
        left = d > 202.5 && d < 337.5;
    }

    private static ushort StickRaw(double v, bool invert)
    {
        double centered = (Math.Clamp(v, 0.0, 1.0) * 2.0) - 1.0;  // -1..+1
        if (invert) centered = -centered;
        int raw = 0x800 + (int)(centered * 0x600);
        return (ushort)Math.Clamp(raw, 0, 0xFFF);
    }

    /// <summary>12-bit little-nibble pair packing, the inverse of SDL's
    /// extraction (x = b0 | ((b1 &amp; 0xF) &lt;&lt; 8); y = (b1 &gt;&gt; 4) | (b2 &lt;&lt; 4)).</summary>
    private static void PackStick(byte[] dst, int offset, ushort x, ushort y)
    {
        dst[offset + 0] = (byte)(x & 0xFF);
        dst[offset + 1] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4));
        dst[offset + 2] = (byte)(y >> 4);
    }

    private static short ImuRaw(float value, float scale)
        => (short)Math.Clamp((int)MathF.Round(value * scale), short.MinValue, short.MaxValue);

    private static void WriteInt16(byte[] dst, int offset, short v)
    {
        dst[offset] = (byte)(v & 0xFF);
        dst[offset + 1] = (byte)((v >> 8) & 0xFF);
    }

    /// <summary>Coarse per-side amplitude from one 4-byte HD rumble block
    /// ([hf, hfAmp|hfHi, lf|lfAmpHi, lfAmpLo], SDL EncodeRumble layout).
    /// Returns 0..255. Takes the louder of the high-band and low-band
    /// amplitudes: HF amp byte spans 0x00..0xC8 linearly over SDL's
    /// encoding table; the LF 9-bit value spans index 0..100 as
    /// (lo - 0x40) * 2 + msb over the same table. Neutral (00 01 40 40)
    /// and zeroed blocks decode to 0. Full HD fidelity is out of scope
    /// for v1 (spec point 4); amplitude extraction is the contract.</summary>
    public static byte DecodeRumbleAmplitude(ReadOnlySpan<byte> block)
    {
        if (block.Length < 4) return 0;

        // High band: amplitude byte with the frequency MSB masked off.
        int hf = block[1] & 0xFE;
        int hfNorm = Math.Clamp(hf * 255 / 0xC8, 0, 255);

        // Low band: 9-bit amplitude (MSB rides bit7 of the frequency
        // byte). Encoded values run 0x0040..0x8072; index = (lo-0x40)*2
        // + msb, 0..101 -> normalize over the table span.
        int msb = (block[2] & 0x80) != 0 ? 1 : 0;
        int lo = block[3];
        int lfNorm = 0;
        if (lo >= 0x40 && lo <= 0x72)
        {
            int index = (lo - 0x40) * 2 + msb;
            lfNorm = Math.Clamp(index * 255 / 101, 0, 255);
        }

        return (byte)Math.Max(hfNorm, lfNorm);
    }
}
