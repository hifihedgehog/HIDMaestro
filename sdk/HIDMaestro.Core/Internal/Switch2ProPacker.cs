using System;

namespace HIDMaestro.Internal;

/// <summary>
/// Switch 2 Pro Controller state packing and rumble decoding (issue #66),
/// for the composite persona: VID 0x057E PID 0x2069 on the USB/IP backend.
/// Hardcoded the way <see cref="SwitchProPacker"/> is for the first Pro.
/// The device side (<see cref="Usbip.Switch2ProDevice"/>) owns the command
/// protocol, the report clock, the counters and which report goes out.
/// This class owns what the SDK contributes:
///
///   - the state BODY a SubmitState produces: buttons, four stick values
///     and six motion counts, from which the device builds report 0x09 or
///     0x05 each time one goes out,
///   - the motor levels decoded from output report 0x02 for OutputDecoded.
///
/// The body is the field list VIIPER's ns2pro InputState carries
/// (inputstate.go, "buttons:u32 lx:u16 ly:u16 rx:u16 ry:u16" then the six
/// motion values), little endian. Report layouts are SDL's
/// SDL_hidapi_switch2.c HandleSwitchProState and HandleStatePacket, the
/// client this persona has to satisfy.
/// </summary>
internal static class Switch2ProPacker
{
    public const int BodySize = 24;

    /// <summary>Body offsets.</summary>
    public const int ButtonsOffset = 0, SticksOffset = 4, MotionOffset = 12;

    public static bool IsSwitch2Pro(ushort vid, ushort pid)
        => vid == 0x057E && pid == 0x2069;

    // Button bits, in report 0x09's wire order (hid_reports.md "Input
    // Report 0x09" button format, VIIPER const.go ButtonB..ButtonC).
    public const uint BitB = 1u << 0, BitA = 1u << 1, BitY = 1u << 2, BitX = 1u << 3,
                      BitR = 1u << 4, BitZR = 1u << 5, BitPlus = 1u << 6, BitRightStick = 1u << 7,
                      BitDown = 1u << 8, BitRight = 1u << 9, BitLeft = 1u << 10, BitUp = 1u << 11,
                      BitL = 1u << 12, BitZL = 1u << 13, BitMinus = 1u << 14, BitLeftStick = 1u << 15,
                      BitHome = 1u << 16, BitCapture = 1u << 17, BitGR = 1u << 18, BitGL = 1u << 19,
                      BitC = 1u << 20;

    /// <summary>The pad's accelerometer counts per g at its default 8 g
    /// range, as SDL reads them (accel_scale = g * 8 / INT16_MAX).</summary>
    private const float AccelCountsPerG = 32767.0f / 8.0f;

    /// <summary>Gyro counts per degree/second, 16.4337. SDL reads a count
    /// as 34.8 / INT16_MAX radians/second, so this is
    /// (pi / 180) * 32767 / 34.8.</summary>
    private const float GyroCountsPerDps = (float)(Math.PI / 180.0 * 32767.0 / 34.8);

    /// <summary>Build the 24-byte body. Buttons follow the names the
    /// switch2-pro-controller profile's report 0x09 field list uses, so the
    /// two profiles answer the same HMGamepadState alike: HMButton.B is the
    /// pad's B, Start its Plus, Back its Minus, Guide its Home, Share its
    /// Capture, RightPaddle and LeftPaddle its GR and GL, Misc1 its C. ZL
    /// and ZR are set while their trigger is above zero and the D-pad comes
    /// from <see cref="HMGamepadState.Hat"/>, the codec's LT_DIGITAL,
    /// RT_DIGITAL and DPAD_* rules.
    ///
    /// <para>Sticks are the SubmitState-resolved 0..1 values, 0 at the left
    /// and at the top. The wire runs 0 at the left to 4095 at the right and
    /// 4095 at the top: SDL inverts both Y axes of this pad's reports
    /// (HandleSwitchProState passes invert for LEFTY and RIGHTY).</para>
    ///
    /// <para>Motion is the calibrated channel, SDL's sensor frame in g and
    /// degrees/second. SDL reads accelerometer X, Y, Z from wire fields 0,
    /// 2 and minus 1, and gyro X, Y, Z from 0, 2 and minus 1 as well, so
    /// the inverse written here returns the submitted vector.</para></summary>
    public static void BuildBody(in HMGamepadState state,
                                 double lx, double ly, double rx, double ry,
                                 double leftTrigger, double rightTrigger,
                                 byte[] dst)
    {
        uint m = (uint)state.Buttons;
        uint b = 0;
        if ((m & (uint)HMButton.B) != 0) b |= BitB;
        if ((m & (uint)HMButton.A) != 0) b |= BitA;
        if ((m & (uint)HMButton.Y) != 0) b |= BitY;
        if ((m & (uint)HMButton.X) != 0) b |= BitX;
        if ((m & (uint)HMButton.RightBumper) != 0) b |= BitR;
        if (TriggerByte(rightTrigger) > 0) b |= BitZR;
        if ((m & (uint)HMButton.Start) != 0) b |= BitPlus;
        if ((m & (uint)HMButton.RightStick) != 0) b |= BitRightStick;
        var h = state.Hat;
        if (h is HMHat.South or HMHat.SouthEast or HMHat.SouthWest) b |= BitDown;
        if (h is HMHat.East or HMHat.NorthEast or HMHat.SouthEast) b |= BitRight;
        if (h is HMHat.West or HMHat.NorthWest or HMHat.SouthWest) b |= BitLeft;
        if (h is HMHat.North or HMHat.NorthEast or HMHat.NorthWest) b |= BitUp;
        if ((m & (uint)HMButton.LeftBumper) != 0) b |= BitL;
        if (TriggerByte(leftTrigger) > 0) b |= BitZL;
        if ((m & (uint)HMButton.Back) != 0) b |= BitMinus;
        if ((m & (uint)HMButton.LeftStick) != 0) b |= BitLeftStick;
        if ((m & (uint)HMButton.Guide) != 0) b |= BitHome;
        if ((m & (uint)HMButton.Share) != 0) b |= BitCapture;
        if ((m & (uint)HMButton.RightPaddle) != 0) b |= BitGR;
        if ((m & (uint)HMButton.LeftPaddle) != 0) b |= BitGL;
        if ((m & (uint)HMButton.Misc1) != 0) b |= BitC;

        dst[0] = (byte)b;
        dst[1] = (byte)(b >> 8);
        dst[2] = (byte)(b >> 16);
        dst[3] = (byte)(b >> 24);

        WriteUInt16(dst, SticksOffset + 0, StickRaw(lx, invert: false));
        WriteUInt16(dst, SticksOffset + 2, StickRaw(ly, invert: true));
        WriteUInt16(dst, SticksOffset + 4, StickRaw(rx, invert: false));
        WriteUInt16(dst, SticksOffset + 6, StickRaw(ry, invert: true));

        WriteInt16(dst, MotionOffset + 0, ImuRaw(state.AccelGX, AccelCountsPerG));
        WriteInt16(dst, MotionOffset + 2, ImuRaw(-state.AccelGZ, AccelCountsPerG));
        WriteInt16(dst, MotionOffset + 4, ImuRaw(state.AccelGY, AccelCountsPerG));
        WriteInt16(dst, MotionOffset + 6, ImuRaw(state.GyroDpsX, GyroCountsPerDps));
        WriteInt16(dst, MotionOffset + 8, ImuRaw(-state.GyroDpsZ, GyroCountsPerDps));
        WriteInt16(dst, MotionOffset + 10, ImuRaw(state.GyroDpsY, GyroCountsPerDps));
    }

    /// <summary>The body of a pad at rest: no buttons, both sticks at
    /// 0x800, no motion. What the device reports before the first
    /// SubmitState.</summary>
    public static byte[] NeutralBody()
    {
        var body = new byte[BodySize];
        var neutral = new HMGamepadState();
        BuildBody(in neutral, 0.5, 0.5, 0.5, 0.5, 0.0, 0.0, body);
        return body;
    }

    /// <summary>The 8-bit trigger value VendorBlobCodec's LT_DIGITAL and
    /// RT_DIGITAL test for being nonzero.</summary>
    private static int TriggerByte(double v) => (int)Math.Round(Math.Clamp(v, 0.0, 1.0) * 255);

    /// <summary>0..1 onto the full 12-bit range, the scale stick12-pair
    /// uses. 0.5 lands on 0x800 in either direction, the center the
    /// persona's flash calibration states.</summary>
    private static ushort StickRaw(double v, bool invert)
    {
        double c = Math.Clamp(v, 0.0, 1.0);
        if (invert) c = 1.0 - c;
        return (ushort)Math.Clamp((int)Math.Round(c * 4095.0), 0, 4095);
    }

    private static short ImuRaw(float value, float scale)
        => (short)Math.Clamp((int)MathF.Round(value * scale), short.MinValue, short.MaxValue);

    private static void WriteUInt16(byte[] dst, int offset, ushort v)
    {
        dst[offset] = (byte)(v & 0xFF);
        dst[offset + 1] = (byte)(v >> 8);
    }

    private static void WriteInt16(byte[] dst, int offset, short v)
    {
        dst[offset] = (byte)(v & 0xFF);
        dst[offset + 1] = (byte)((v >> 8) & 0xFF);
    }

    /// <summary>Bytes of output report 0x02 the decode needs after its
    /// report ID: one 16-byte block per actuator.</summary>
    public const int RumbleDataSize = 32;

    /// <summary>The largest amplitude SDL writes: it scales each motor
    /// level to 29000 of 65535 before packing (RUMBLE_MAX).</summary>
    private const double RumbleMax = 29000.0;

    /// <summary>Decode output report 0x02 into two motor levels, 0..255.
    /// <paramref name="data"/> is the report after its ID: the left
    /// actuator's block in bytes 0..15 and the right's in 16..31
    /// (hid_reports.md "Output Report 0x02"). A block is a sequence byte
    /// and three 5-byte frames. A frame is a 40-bit little-endian value:
    /// frequency in bits 0-8, a flag in bit 9, amplitude in bits 10-19, and
    /// the same three fields again from bit 20 (switch2-controllers
    /// controller.py VibrationData.get_bytes).
    ///
    /// <para>SDL's EncodeHDRumble puts its high-frequency amplitude in the
    /// first amplitude field and its low-frequency amplitude in the second,
    /// each cut to its top 10 bits. So the right motor is the largest first
    /// field over the six frames and the left motor the largest second
    /// field, scaled back from 29000.</para></summary>
    public static void DecodeRumble(ReadOnlySpan<byte> data, out byte leftMotor, out byte rightMotor)
    {
        leftMotor = 0;
        rightMotor = 0;
        if (data.Length < RumbleDataSize) return;

        int first = 0, second = 0;
        for (int block = 0; block < 2; block++)
        {
            for (int frame = 0; frame < 3; frame++)
            {
                int o = block * 16 + 1 + frame * 5;
                ulong v = data[o] | ((ulong)data[o + 1] << 8) | ((ulong)data[o + 2] << 16)
                        | ((ulong)data[o + 3] << 24) | ((ulong)data[o + 4] << 32);
                first = Math.Max(first, (int)((v >> 10) & 0x3FF));
                second = Math.Max(second, (int)((v >> 30) & 0x3FF));
            }
        }
        rightMotor = MotorLevel(first);
        leftMotor = MotorLevel(second);
    }

    private static byte MotorLevel(int amplitude)
        => (byte)Math.Min(255, (int)Math.Round(amplitude * 64.0 * 255.0 / RumbleMax));
}
