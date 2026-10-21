using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
        // EncodeOutput's rolling counters, keyed by field semantic name so
        // multiple rolling fields in one report each advance independently.
        public Dictionary<string, byte> RollingCounters { get; } = new();

        // EncodeInput's rolling counters, one slot per counter the program
        // compiled (VendorBlobProgram.CounterInitials), so a frame looks up
        // none by name. Bound to the first program this state encodes, and
        // bound again, from each counter's initial value, if it is handed
        // another.
        private VendorBlobProgram? _counterProgram;
        private uint[] _counters = Array.Empty<uint>();

        internal uint[] CountersFor(VendorBlobProgram prog)
        {
            if (!ReferenceEquals(_counterProgram, prog))
            {
                _counters = (uint[])prog.CounterInitials.Clone();
                _counterProgram = prog;
            }
            return _counters;
        }

        private long _clockOrigin;

        /// <summary>Issue #64. The sensor time for this report in 1/3 µs
        /// ticks: the consumer's own <see cref="HMGamepadState.SensorTimestamp"/>
        /// when it sets one, otherwise the time since this controller's
        /// first report. A real pad stamps every report, and SDL, Linux and
        /// DS4Windows all take the motion interval from the difference
        /// between stamps, so a stamp that never moves reads as no time
        /// passing.</summary>
        public uint SensorTicks(uint consumerTicks)
        {
            if (consumerTicks != 0) return consumerTicks;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_clockOrigin == 0) _clockOrigin = now;
            // 3 ticks per microsecond, wrapping at 32 bits like the
            // consumer's value does.
            double us = (now - _clockOrigin) * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
            return unchecked((uint)(ulong)(us * 3.0));
        }

        // v2.0.0: the accumulator of a "clock": "accumulate" timestamp
        // field, one per controller, in the 1/3 microsecond ticks of
        // HMGamepadState.SensorTimestamp (design note, mechanism 1).
        private bool _accStarted, _accConsumer;
        private long _accOrigin, _accPrevTicks;
        private uint _accPrevStamp;
        private ulong _acc;

        /// <summary>The clock the accumulator reads before a consumer stamp
        /// arrives, a Stopwatch-style timestamp, and its ticks per second.
        /// Null reads the Stopwatch. A test sets its own.</summary>
        internal Func<long>? TimestampClock;

        /// <summary>The accumulating field's holdWhile, compiled against the
        /// protocol's registers, and the registers it reads: the device's
        /// own, or the declared initial values for a device whose protocol
        /// no host has reached. Null for none.</summary>
        internal Programs.Condition? Hold;
        internal uint[]? HoldRegisters;
        internal long TimestampFrequency = System.Diagnostics.Stopwatch.Frequency;

        /// <summary>Advances the accumulator by one frame whose consumer
        /// stamp is <paramref name="stamp"/> and returns it. The source is
        /// chosen once: the clock until the first nonzero stamp, then the
        /// consumer's stamps for the rest of the controller's life. The frame
        /// that switches adds the clock's time since the previous frame. A
        /// later frame adds the stamp's advance modulo 2^32, and an advance of
        /// 2^31 or more, a consumer clock that restarted or ran backward,
        /// adds <paramref name="nominalTicks"/> instead. A stamp of 0 after
        /// the switch is no stamp this frame: it adds the clock's time, and
        /// the last stamp moves on by that much. A repeat of the last state
        /// carries the same stamp and adds nothing.
        /// <para>While <paramref name="hold"/>, the frame adds nothing, and
        /// the stamps and the clock still move on, so the field resumes from
        /// its held value.</para></summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal ulong AdvanceTimestamp(uint stamp, uint nominalTicks, bool hold = false)
        {
            long now = TimestampClock?.Invoke() ?? System.Diagnostics.Stopwatch.GetTimestamp();
            if (!_accStarted)
            {
                _accStarted = true;
                _accOrigin = now;
            }
            // The clock's ticks since the origin, computed from the origin on
            // every frame so no remainder is lost, in 1/3 microseconds.
            long elapsed = Math.Max(0, now - _accOrigin);
            long f = TimestampFrequency;
            long ticks = elapsed / f * 3_000_000 + elapsed % f * 3_000_000 / f;
            ulong clock = (ulong)Math.Max(0, ticks - _accPrevTicks);
            _accPrevTicks = Math.Max(ticks, _accPrevTicks);
            if (!_accConsumer)
            {
                if (!hold) _acc += clock;
                if (stamp != 0)
                {
                    _accConsumer = true;
                    _accPrevStamp = stamp;
                }
            }
            else if (stamp == 0)
            {
                if (!hold) _acc += clock;
                _accPrevStamp = unchecked(_accPrevStamp + (uint)clock);
            }
            else
            {
                uint d = unchecked(stamp - _accPrevStamp);
                if (!hold) _acc += d >= 0x8000_0000u ? nominalTicks : d;
                _accPrevStamp = stamp;
            }
            return _acc;
        }
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

        var prog = VendorBlobProgram.Get(spec);
        // The cleared report with its ID and its constant bytes in place,
        // compiled once, and the fields left to write. The ID is read when
        // the program compiles, so a frame parses no text.
        var fields = prog.InputFields;
        if (prog.InputTemplate is { } template) template.AsSpan().CopyTo(buffer);
        else
        {
            Array.Clear(buffer, 0, spec.Size);
            buffer[0] = spec.ReportIdByte;
        }
        var counters = prog.CounterInitials.Length > 0 ? encState.CountersFor(prog) : null;
        // One advance of the timestamp accumulator per frame, for a program
        // with a "clock": "accumulate" field.
        ulong acc = prog.Accumulates
            ? encState.AdvanceTimestamp(state.SensorTimestamp, prog.NominalTicks,
                encState.Hold != null && encState.HoldRegisters != null && encState.Hold.Holds(encState.HoldRegisters))
            : 0;
        // The d-pad from HatDegrees, HatHundredths, HatRaw or Hat, once per
        // frame for a program that reads it.
        bool dUp = false, dDown = false, dLeft = false, dRight = false;
        if (prog.ResolvesDpad) ResolveDpad(in state, out dUp, out dDown, out dLeft, out dRight);
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
                    // "invert" for a wire axis that grows the other way
                    // from the SDK's, which runs 0 at the left and the top,
                    // such as a stick Y that grows upward.
                    int raw = f.Center == 128
                        ? WireArith.Linear(v, f.Min, f.Max, f.Invert, rest: 0.5)
                        : WireArith.Linear(v, f.Center - 127, f.Center + 127, f.Invert, rest: 0.5);
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
                    buffer[f.B] = (byte)WireArith.Linear(v, f.Min, f.Max, f.Invert, rest: 0);
                    break;
                }
                case VendorBlobProgram.FieldOp.Stick12Pair:
                {
                    // Two 12-bit axes sharing three bytes, exactly as
                    // VIIPER's ns2pro packStick12 lays them out:
                    //   out[0] = x low 8
                    //   out[1] = x high 4 in the low nibble,
                    //            y low 4 in the high nibble
                    //   out[2] = y high 8
                    // The middle byte belongs to both axes, which is why
                    // this is one field and not two: writing either axis
                    // alone would clobber half of the other.
                    if (f.B < 0 || f.B + 2 >= buffer.Length) break;
                    float vx, vy;
                    if (f.Source == VendorBlobProgram.SrcOp.RightStick) { vx = rightStickX; vy = rightStickY; }
                    else { vx = leftStickX; vy = leftStickY; }
                    // 0..1 maps onto the full 12-bit range. VIIPER centers a
                    // resting stick at 0x0800 (StickCenter), which is what
                    // 0.5f produces here after rounding.
                    // invert turns the Y half, the axis whose direction a
                    // stick can read the other way from the SDK's.
                    int x = WireArith.Linear(vx, f.Min, f.Max, invert: false, rest: 0.5);
                    int y = WireArith.Linear(vy, f.Min, f.Max, f.Invert, rest: 0.5);
                    buffer[f.B]     = (byte)(x & 0xFF);
                    buffer[f.B + 1] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4));
                    buffer[f.B + 2] = (byte)((y >> 4) & 0xFF);
                    break;
                }
                case VendorBlobProgram.FieldOp.U8Rolling:
                {
                    if (f.B < 0) break;
                    byte counter = (byte)counters![f.CounterSlot];
                    buffer[f.B] = counter;
                    counters[f.CounterSlot] = unchecked((byte)(counter + f.Stride));
                    break;
                }
                case VendorBlobProgram.FieldOp.U8Const:
                {
                    if (f.B < 0) break;
                    buffer[f.B] = f.Source == VendorBlobProgram.SrcOp.TouchpadPacketCounter
                        ? state.TouchpadPacketCounter : f.Initial;
                    break;
                }
                case VendorBlobProgram.FieldOp.U16:
                {
                    // Issue #64: the DualShock 4 sensor timestamp, little
                    // endian in 16/3 µs ticks (SDL_hidapi_ps4.c "Sensor
                    // timestamp is in 5.33us units", hid-playstation.c
                    // dualshock4_input_report_common.sensor_timestamp).
                    // Divisor 16 turns the SDK's 1/3 µs ticks into those, and
                    // because 2^32 / 16 is a multiple of 2^16 the 16-bit
                    // value still wraps cleanly when the 32-bit source does.
                    if (f.B < 0 || f.B + 1 >= buffer.Length) break;
                    uint u16 = f.Source != VendorBlobProgram.SrcOp.SensorTimestamp ? 0u
                        : f.Accumulate ? (uint)(acc / f.Divisor)
                        : encState.SensorTicks(state.SensorTimestamp) / f.Divisor;
                    buffer[f.B]     = (byte)(u16 & 0xFF);
                    buffer[f.B + 1] = (byte)((u16 >> 8) & 0xFF);
                    break;
                }
                case VendorBlobProgram.FieldOp.U8Pressure:
                {
                    if (f.B < 0 || (uint)f.B >= (uint)buffer.Length) break;
                    buffer[f.B] = Pressure(f.Source, in state);
                    break;
                }
                case VendorBlobProgram.FieldOp.U10BE:
                {
                    // A DualShock 3 motion sensor: 10 bits around a center,
                    // most significant byte first (hid-sony.c sixaxis_raw_event
                    // "MSByte first", SDL_hidapi_ps3.c ScaleAccel's
                    // SDL_Swap16BE). The SDK's fields are rescaled from their
                    // own resolution to the wire's and clamped to 0..1023.
                    if (f.B < 0 || f.B + 1 >= buffer.Length) break;
                    int src = f.Source switch
                    {
                        VendorBlobProgram.SrcOp.AccelX    => state.AccelX,
                        VendorBlobProgram.SrcOp.AccelY    => state.AccelY,
                        VendorBlobProgram.SrcOp.AccelZ    => state.AccelZ,
                        VendorBlobProgram.SrcOp.GyroPitch => state.GyroPitch,
                        VendorBlobProgram.SrcOp.GyroYaw   => state.GyroYaw,
                        VendorBlobProgram.SrcOp.GyroRoll  => state.GyroRoll,
                        _ => 0,
                    };
                    int delta = (int)Math.Round((double)src * f.Resolution / f.SourceResolution,
                                                MidpointRounding.AwayFromZero);
                    int v10 = Math.Clamp(f.Invert ? f.Center - delta : f.Center + delta, 0, 1023);
                    buffer[f.B]     = (byte)((v10 >> 8) & 0xFF);
                    buffer[f.B + 1] = (byte)(v10 & 0xFF);
                    break;
                }
                case VendorBlobProgram.FieldOp.U8LevelMap:
                {
                    // A battery byte from the profile's table: fullValue while
                    // full, ahead of chargingValue while charging, otherwise
                    // the level's entry. The DualShock 3's table puts 0xEF
                    // ahead of 0xEE (dualshock-3-full.json).
                    if (f.B < 0 || (uint)f.B >= (uint)buffer.Length) break;
                    buffer[f.B] = state.BatteryFull && f.FullValue >= 0 ? (byte)f.FullValue
                                : state.BatteryCharging && f.ChargingValue >= 0 ? (byte)f.ChargingValue
                                : f.Levels![Math.Min((int)state.BatteryLevel, 10)];
                    break;
                }
                case VendorBlobProgram.FieldOp.I16:
                {
                    if (f.B < 0 || f.B + 1 >= buffer.Length) break;
                    // A calibrated, repeated or offset field: its own method,
                    // inlined here, so a field without extras pays one null
                    // check and the few profiles that set them no call.
                    if (f.Extras != null) { WriteCalibratedI16(in f, in state, buffer); break; }
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
                case VendorBlobProgram.FieldOp.I16Axis:
                {
                    // Signed 16-bit stick axis. Sticks arrive as [0..1] with
                    // 0.5 centered (v1.3.9); Valve's packets carry them
                    // full-scale signed, which SDL reads straight through.
                    if (f.B < 0 || f.B + 1 >= buffer.Length) break;
                    float sv = f.Source switch
                    {
                        VendorBlobProgram.SrcOp.LeftStickX  => leftStickX,
                        VendorBlobProgram.SrcOp.LeftStickY  => leftStickY,
                        VendorBlobProgram.SrcOp.RightStickX => rightStickX,
                        VendorBlobProgram.SrcOp.RightStickY => rightStickY,
                        _ => 0.5f,
                    };
                    // A wire axis that grows upward, such as Valve's stick Y,
                    // which SDL negates on the way in, sets invert.
                    short sraw = (short)WireArith.Linear(sv, f.Min, f.Max, f.Invert, rest: 0.5);
                    buffer[f.B]     = (byte)(sraw & 0xFF);
                    buffer[f.B + 1] = (byte)((sraw >> 8) & 0xFF);
                    break;
                }
                case VendorBlobProgram.FieldOp.I16Pad:
                case VendorBlobProgram.FieldOp.I16PadOrStick:
                {
                    // Valve trackpad coordinate. Fingers arrive in Sony's
                    // native range (0..1919 by 0..1079, HMGamepadState), and
                    // Valve's pads are full-scale signed with positive Y up:
                    // SDL_hidapi_steamdeck.c reads them back as
                    // sLeftPadX / 65536 + 0.5 and -sLeftPadY / 65536 + 0.5,
                    // so X maps straight across and Y, which sets invert,
                    // turns.
                    //
                    // I16PadOrStick is the 2015 controller's shared pair. Its
                    // firmware packs EITHER the pad or the joystick into the
                    // same two axes and flags which with the finger-down bit,
                    // and SDL_hidapi_steam.c:UpdateSteamControllerState reads
                    // it exactly that way. Falling back to the stick when the
                    // finger is up is the hardware's own behavior, not a
                    // substitution for missing data.
                    if (f.B < 0 || f.B + 1 >= buffer.Length) break;
                    bool isLeft = f.Source is VendorBlobProgram.SrcOp.LeftPadX
                                            or VendorBlobProgram.SrcOp.LeftPadY;
                    bool isY = f.Source is VendorBlobProgram.SrcOp.LeftPadY
                                         or VendorBlobProgram.SrcOp.RightPadY;
                    bool down = isLeft ? state.TouchpadFinger0Active
                                       : state.TouchpadFinger1Active;
                    short praw;
                    if (down)
                    {
                        int raw = isLeft
                            ? (isY ? state.TouchpadFinger0Y : state.TouchpadFinger0X)
                            : (isY ? state.TouchpadFinger1Y : state.TouchpadFinger1X);
                        float span = isY ? 1079f : 1919f;
                        float unit = Math.Clamp(raw / span, 0f, 1f) - 0.5f;
                        if (f.Invert) unit = -unit;
                        praw = (short)Math.Clamp(
                            (int)Math.Round(unit * 2f * 32767f), -32767, 32767);
                    }
                    else if (f.Op == VendorBlobProgram.FieldOp.I16PadOrStick)
                    {
                        float sv = isLeft ? (isY ? leftStickY : leftStickX)
                                          : (isY ? rightStickY : rightStickX);
                        praw = (short)WireArith.Linear(sv, f.Min, f.Max, f.Invert, rest: 0.5);
                    }
                    else
                    {
                        praw = 0;
                    }
                    buffer[f.B]     = (byte)(praw & 0xFF);
                    buffer[f.B + 1] = (byte)((praw >> 8) & 0xFF);
                    break;
                }
                case VendorBlobProgram.FieldOp.U16Pressure:
                {
                    // Pad pressure, finger 0 on the left pad and finger 1
                    // on the right, by the one arithmetic from min at 0 to
                    // max at 1. A pressure of 0 on a contact is full pressure,
                    // so a consumer that knows only touch sends max, the
                    // pressure-sensitive buttons' rule. p > 0 is false for 0,
                    // a negative pressure and NaN, each of which reads as 0.
                    // Valve's profiles set max 32767, full scale for SDL,
                    // which divides by 32768.0f.
                    if (f.B < 0 || f.B + 1 >= buffer.Length) break;
                    bool right = f.Source == VendorBlobProgram.SrcOp.RightPadPressure;
                    bool pdown = right ? state.TouchpadFinger1Active : state.TouchpadFinger0Active;
                    float pp = right ? state.TouchpadFinger1Pressure : state.TouchpadFinger0Pressure;
                    int pv = WireArith.Linear(pp > 0 ? pp : pdown ? 1.0 : 0.0, f.Min, f.Max, invert: false, rest: 0);
                    buffer[f.B]     = (byte)(pv & 0xFF);
                    buffer[f.B + 1] = (byte)((pv >> 8) & 0xFF);
                    break;
                }
                case VendorBlobProgram.FieldOp.U16Trigger:
                {
                    // Unsigned 16-bit trigger, min at release and max at a
                    // full pull. Valve's profiles set max 32767: SDL widens
                    // it as (raw * 2 - 32768) to reach its own full range.
                    if (f.B < 0 || f.B + 1 >= buffer.Length) break;
                    float tv = f.Source switch
                    {
                        VendorBlobProgram.SrcOp.LeftTrigger  => leftTrigger,
                        VendorBlobProgram.SrcOp.RightTrigger => rightTrigger,
                        _ => 0f,
                    };
                    int traw = WireArith.Linear(tv, f.Min, f.Max, f.Invert, rest: 0);
                    buffer[f.B]     = (byte)(traw & 0xFF);
                    buffer[f.B + 1] = (byte)((traw >> 8) & 0xFF);
                    break;
                }
                case VendorBlobProgram.FieldOp.U32Rolling:
                {
                    // Valve's unPacketNum. A consumer that sees the same
                    // number twice may skip the frame, so this has to
                    // advance on every encode or the stream reads as one
                    // frame repeated forever.
                    if (f.B < 0 || f.B + 3 >= buffer.Length) break;
                    uint c32 = counters![f.CounterSlot];
                    buffer[f.B]     = (byte)(c32         & 0xFF);
                    buffer[f.B + 1] = (byte)((c32 >>  8) & 0xFF);
                    buffer[f.B + 2] = (byte)((c32 >> 16) & 0xFF);
                    buffer[f.B + 3] = (byte)((c32 >> 24) & 0xFF);
                    counters[f.CounterSlot] = unchecked(c32 + (uint)Math.Max(1, (int)f.Stride));
                    break;
                }
                case VendorBlobProgram.FieldOp.U32:
                {
                    if (f.B < 0 || f.B + 3 >= buffer.Length) break;
                    // With "clock": "accumulate", the accumulator over the
                    // divisor, modulo 2^32. Otherwise the consumer's stamp.
                    uint v = f.Source != VendorBlobProgram.SrcOp.SensorTimestamp ? 0u
                        : f.Accumulate ? unchecked((uint)(acc / f.Divisor))
                        : state.SensorTimestamp;
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
                            VendorBlobProgram.FlagCable      => state.BatteryCharging || state.BatteryFull,
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
                    int level = f.FullValue >= 0 && state.BatteryFull ? f.FullValue : state.BatteryLevel;
                    byte v = (byte)(level & ((1 << width) - 1));
                    buffer[f.B] = (byte)((buffer[f.B] & ~mask) | ((v << f.BitLo) & mask));
                    break;
                }
                case VendorBlobProgram.FieldOp.HatOctant:
                {
                    // HMHat values 1..8 (N..NW) map to descriptor 0..7;
                    // HMHat.None maps to the neutral value (typically 8).
                    // With dpad "resolved" the octant comes from the
                    // resolved directions instead of Hat alone.
                    if (f.B < 0) break;
                    var hat = f.DpadResolved ? Octant(dUp, dDown, dLeft, dRight) : state.Hat;
                    int hatNibble = hat == HMHat.None ? f.Neutral : ((int)hat - 1) & 0x0F;
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
                    ulong packed = 0;
                    // The buttons that are one HMButton bit each, a masked
                    // shift per distance from HMButton bit to wire bit.
                    foreach (var g in f.ButtonGroups!)
                    {
                        ulong moved = mask & g.Mask;
                        packed |= g.Shift >= 0 ? moved << g.Shift : moved >> -g.Shift;
                    }
                    var h = state.Hat;
                    bool r = f.DpadResolved;
                    foreach (int single in f.ButtonSingles!)
                    {
                        int i = single & 0xFFFF;
                        // Magic trigger-engaged digital buttons: DS4/DS5
                        // report L2/R2 as both analog axis AND digital button.
                        // D-pad directions come from state.Hat, not from the
                        // button mask, so a profile that spells the d-pad as
                        // four discrete buttons still consumes the same
                        // HMHat a hat-encoded profile does. Diagonals set
                        // both of their components, which is what the wire
                        // format expects and what SDL reconstructs a hat
                        // from on the other side.
                        // The trigger bits follow the 8-bit analog value
                        // (issue #64): set exactly when it is nonzero, the
                        // rule DS4Windows applies to its own virtual DS4
                        // (ControlService.cs "cState.L2Btn = cState.L2 > 0").
                        // SDL's PS4 and PS5 drivers read a set bit over a
                        // zero trigger byte as a full pull, which a bare
                        // "above zero" test produced for any value under
                        // half of the first step. A mask with a
                        // triggerThreshold sets them further along the
                        // travel, where the device's own switch or firmware
                        // threshold sets them (TriggerBit). The kind was
                        // read from the position's name when the program
                        // compiled, so this is one jump.
                        bool on = (VendorBlobProgram.SingleKind)(single >> 16) switch
                        {
                            VendorBlobProgram.SingleKind.LtDigital => TriggerBit(leftTrigger, f.Extras),
                            VendorBlobProgram.SingleKind.RtDigital => TriggerBit(rightTrigger, f.Extras),
                            VendorBlobProgram.SingleKind.DpadUp    => r ? dUp : h is HMHat.North or HMHat.NorthEast or HMHat.NorthWest,
                            VendorBlobProgram.SingleKind.DpadDown  => r ? dDown : h is HMHat.South or HMHat.SouthEast or HMHat.SouthWest,
                            VendorBlobProgram.SingleKind.DpadLeft  => r ? dLeft : h is HMHat.West  or HMHat.NorthWest or HMHat.SouthWest,
                            VendorBlobProgram.SingleKind.DpadRight => r ? dRight : h is HMHat.East  or HMHat.NorthEast or HMHat.SouthEast,
                            VendorBlobProgram.SingleKind.Pad0Touch => state.TouchpadFinger0Active,
                            VendorBlobProgram.SingleKind.Pad1Touch => state.TouchpadFinger1Active,
                            _ => (mask & (uint)f.ButtonBits[i]) != 0,
                        };
                        if (on) packed |= 1UL << (f.BitLo + i);
                    }
                    // OR into the existing bytes, preserving bits outside the
                    // declared range. The range may span more than one byte:
                    // Valve's state packets carry a 64-bit button field, and
                    // a single-byte write would drop every button above 7.
                    // With eight bytes of buffer from the field on, that is
                    // one little-endian read, mask and write of a 64-bit
                    // word: bits BitLo to BitHi from packed, every other bit
                    // of the word as it was, the bytes past the field's
                    // among them.
                    if ((long)f.B + 8 <= buffer.Length && f.BitLo >= 0 && f.BitHi <= 63)
                    {
                        ulong range = (f.BitHi >= 63 ? ulong.MaxValue : (1UL << (f.BitHi + 1)) - 1) & ~((1UL << f.BitLo) - 1);
                        ref byte at = ref buffer[f.B];
                        ulong word = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(MemoryMarshal.CreateReadOnlySpan(ref at, 8));
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(MemoryMarshal.CreateSpan(ref at, 8), (word & ~range) | (packed & range));
                        break;
                    }
                    int nBytes = (f.BitHi / 8) + 1;
                    for (int by = 0; by < nBytes; by++)
                    {
                        if ((uint)(f.B + by) >= (uint)buffer.Length) break;
                        int lo = by * 8;
                        int rlo = Math.Max(lo, f.BitLo), rhi = Math.Min(lo + 7, f.BitHi);
                        if (rlo > rhi) continue;
                        byte span = (byte)((((1 << (rhi - rlo + 1)) - 1) << (rlo - lo)) & 0xFF);
                        byte val = (byte)((packed >> lo) & 0xFF);
                        buffer[f.B + by] = (byte)((buffer[f.B + by] & (byte)~span) | (val & span));
                    }
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
                        // A signed value in [-1, 1] about the center, by the
                        // one arithmetic: one product in double and one
                        // rounding, half to even. A NaN rests at the center.
                        int sign = f.Invert ? -1 : 1;
                        if (val is float fv) buffer[f.B] = OutputAxisByte(fv, f.Center, sign);
                        else if (val is double d) buffer[f.B] = OutputAxisByte(d, f.Center, sign);
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
                        // The one arithmetic. A NaN rests at 0.
                        if (val is float fv) buffer[f.B] = (byte)WireArith.Linear(fv, 0, 255, invert: false, rest: 0);
                        else if (val is double d) buffer[f.B] = (byte)WireArith.Linear(d, 0, 255, invert: false, rest: 0);
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
    /// spec. Used by HMController.OutputPollLoop to surface incoming output
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
                    int delta = f.Invert ? f.Center - buffer[f.B] : buffer[f.B] - f.Center;
                    result[field.Semantic] = (float)(delta / 127.0);
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
                        // The mask can span bytes, as the encoder writes it
                        // (a 64-bit Valve mask). A bit past a short host
                        // write reads as released.
                        int bit = f.BitLo + i;
                        int at = f.B + (bit >> 3);
                        if (at < buffer.Length && ((buffer[at] >> (bit & 7)) & 1) != 0
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
                case VendorBlobProgram.FieldOp.PackedUint:
                {
                    if (field.Semantic == null) continue;
                    result[field.Semantic] = DecodePacked(f.Parts!, buffer);
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

    /// <summary>An output uint8-axis byte: <c>center + sign x RoundHalfEven(v
    /// x 127)</c> in double, clamped to a byte, and the center for NaN.</summary>
    private static byte OutputAxisByte(double v, int center, int sign) =>
        double.IsNaN(v) ? (byte)Math.Clamp(center, 0, 255)
            : (byte)Math.Clamp(center + sign * Math.Round(Math.Clamp(v, -2.0, 2.0) * 127), 0, 255);

    /// <summary>Whether the 8-bit value a <c>uint8-trigger</c> field writes
    /// for <paramref name="v"/>, by the one arithmetic, is above zero: the
    /// LT and RT digital bits. RoundHalfEven(c x 255) is above zero exactly
    /// when c x 255, an exact product, is above 0.5, and a NaN, at its rest
    /// of 0, is not.</summary>
    internal static bool TriggerOn(float v) => v * 255.0 > 0.5;

    /// <summary>A trigger bit of a button mask: with the mask's
    /// triggerThreshold, whether the trigger's value on a scale of its max
    /// counts, by the one arithmetic, is at least its at count, which is the
    /// trigger at or past the least float that reaches it, found when the
    /// program compiled (WireArith.LeastAtLeast). A NaN takes the trigger's
    /// rest, 0, and at is 1 or more, so it never sets. Without one,
    /// <see cref="TriggerOn"/>, which is the same test at max 255 and at 1:
    /// RoundHalfEven(c x 255) is 1 or more exactly when c x 255 is above
    /// 0.5.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static bool TriggerBit(float v, VendorBlobProgram.FieldExtras? x) =>
        x is { TriggerMax: > 0 } t ? v >= t.TriggerFrom : TriggerOn(v);

    /// <summary>A pressure-sensitive button's byte: the consumer's pressure
    /// when it sets one, otherwise 255 while the button is pressed and 0
    /// while it is not, so digital-only state still presses fully.</summary>
    private static byte Pressure(VendorBlobProgram.SrcOp source, in HMGamepadState s)
    {
        var h = s.Hat;
        (byte p, bool down) = source switch
        {
            VendorBlobProgram.SrcOp.PressureA           => (s.PressureA, (s.Buttons & HMButton.A) != 0),
            VendorBlobProgram.SrcOp.PressureB           => (s.PressureB, (s.Buttons & HMButton.B) != 0),
            VendorBlobProgram.SrcOp.PressureX           => (s.PressureX, (s.Buttons & HMButton.X) != 0),
            VendorBlobProgram.SrcOp.PressureY           => (s.PressureY, (s.Buttons & HMButton.Y) != 0),
            VendorBlobProgram.SrcOp.PressureLeftBumper  => (s.PressureLeftBumper, (s.Buttons & HMButton.LeftBumper) != 0),
            VendorBlobProgram.SrcOp.PressureRightBumper => (s.PressureRightBumper, (s.Buttons & HMButton.RightBumper) != 0),
            VendorBlobProgram.SrcOp.PressureDpadUp      => (s.PressureDpadUp, h is HMHat.North or HMHat.NorthEast or HMHat.NorthWest),
            VendorBlobProgram.SrcOp.PressureDpadRight   => (s.PressureDpadRight, h is HMHat.East or HMHat.NorthEast or HMHat.SouthEast),
            VendorBlobProgram.SrcOp.PressureDpadDown    => (s.PressureDpadDown, h is HMHat.South or HMHat.SouthEast or HMHat.SouthWest),
            VendorBlobProgram.SrcOp.PressureDpadLeft    => (s.PressureDpadLeft, h is HMHat.West or HMHat.NorthWest or HMHat.SouthWest),
            _ => ((byte)0, false),
        };
        return p != 0 ? p : down ? (byte)255 : (byte)0;
    }

    /// <summary>An int16-le field with a scale, a repeat or a center: a
    /// calibrated value as counts by the one arithmetic (WireArith.Scaled),
    /// or a raw one, offset by the center and written in each motion frame
    /// the repeat gives. Inlined into the encode loop: out of line it cost
    /// about 2.6 ns a field in calls, and inline it leaves the other
    /// profiles' encodes as they were (perf_budget_check report).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void WriteCalibratedI16(in VendorBlobProgram.CompiledField f, in HMGamepadState state, byte[] buffer)
    {
        // Every field here has its extras: read them once.
        var x = f.Extras!;
        short v;
        if (x.ScaleNumerator != 0)
        {
            // The value at its offset in the state, with no switch, and the
            // negation folded into the scale when the program compiled. A
            // denominator that is a power of two is a multiplier there too,
            // which leaves the division to the ratios that need it.
            float g = x.MotionOffset >= 0
                ? Unsafe.ReadUnaligned<float>(ref Unsafe.AddByteOffset(ref Unsafe.As<HMGamepadState, byte>(ref Unsafe.AsRef(in state)), x.MotionOffset))
                : 0f;
            v = x.ScaleMultiplier != 0
                ? WireArith.ScaledBy(g, x.ScaleMultiplier)
                : WireArith.Scaled(g, x.SignedNumerator, x.ScaleDenominator);
        }
        else
        {
            v = f.Source switch
            {
                VendorBlobProgram.SrcOp.GyroPitch => state.GyroPitch,
                VendorBlobProgram.SrcOp.GyroYaw   => state.GyroYaw,
                VendorBlobProgram.SrcOp.GyroRoll  => state.GyroRoll,
                VendorBlobProgram.SrcOp.AccelX    => state.AccelX,
                VendorBlobProgram.SrcOp.AccelY    => state.AccelY,
                VendorBlobProgram.SrcOp.AccelZ    => state.AccelZ,
                _ => (short)0,
            };
        }
        // A center offsets the value on the wire: 32768 gives offset binary,
        // zero at 0x8000. Such a word is unsigned, so it clamps to 0 to 65535,
        // as uint10-be clamps to its range. A report that carries several
        // motion frames of one sample writes it in each (repeat).
        int w = x.OffsetBinary ? Math.Clamp(v + f.Center, 0, 65535) : v;
        // One frame, the common case, the caller having checked its two
        // bytes, needs no loop.
        if (x.RepeatCount == 1)
        {
            buffer[f.B]     = (byte)(w & 0xFF);
            buffer[f.B + 1] = (byte)((w >> 8) & 0xFF);
            return;
        }
        for (int k = 0, at = f.B, n = x.RepeatCount, stride = x.RepeatStride; k < n && at + 1 < buffer.Length; k++, at += stride)
        {
            buffer[at]     = (byte)(w & 0xFF);
            buffer[at + 1] = (byte)((w >> 8) & 0xFF);
        }
    }

    /// <summary>The byte offsets of HMGamepadState's calibrated values,
    /// AccelGX to GyroDpsZ in SrcOp order, read from the layout the runtime
    /// gave the struct.</summary>
    private static readonly nint[] s_motionOffsets = MotionOffsets();

    private static nint[] MotionOffsets()
    {
        var s = new HMGamepadState();
        ref byte at = ref Unsafe.As<HMGamepadState, byte>(ref s);
        return new[]
        {
            Unsafe.ByteOffset(ref at, ref Unsafe.As<float, byte>(ref s.AccelGX)),
            Unsafe.ByteOffset(ref at, ref Unsafe.As<float, byte>(ref s.AccelGY)),
            Unsafe.ByteOffset(ref at, ref Unsafe.As<float, byte>(ref s.AccelGZ)),
            Unsafe.ByteOffset(ref at, ref Unsafe.As<float, byte>(ref s.GyroDpsX)),
            Unsafe.ByteOffset(ref at, ref Unsafe.As<float, byte>(ref s.GyroDpsY)),
            Unsafe.ByteOffset(ref at, ref Unsafe.As<float, byte>(ref s.GyroDpsZ)),
        };
    }

    /// <summary>The offset a calibrated source reads at, -1 for any other
    /// source. Called when a program compiles.</summary>
    internal static nint MotionOffset(VendorBlobProgram.SrcOp source) =>
        source is >= VendorBlobProgram.SrcOp.AccelGX and <= VendorBlobProgram.SrcOp.GyroDpsZ
            ? s_motionOffsets[source - VendorBlobProgram.SrcOp.AccelGX]
            : -1;

    /// <summary>v2.0.0: the d-pad a <c>dpad: "resolved"</c> field reads: the
    /// first of HatDegrees, HatHundredths, HatRaw and Hat that is set, as an
    /// angle, and each direction on within 67.5 degrees of it, so a
    /// diagonal's 45 degrees light two.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static void ResolveDpad(in HMGamepadState state,
                                     out bool up, out bool down, out bool left, out bool right)
    {
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

    /// <summary>The octant of four resolved directions, or None. A NaN
    /// angle lights none.</summary>
    private static HMHat Octant(bool up, bool down, bool left, bool right) =>
        (up, down, left, right) switch
        {
            (true, false, false, false) => HMHat.North,
            (true, false, false, true)  => HMHat.NorthEast,
            (false, false, false, true) => HMHat.East,
            (false, true, false, true)  => HMHat.SouthEast,
            (false, true, false, false) => HMHat.South,
            (false, true, true, false)  => HMHat.SouthWest,
            (false, false, true, false) => HMHat.West,
            (true, false, true, false)  => HMHat.NorthWest,
            _ => HMHat.None,
        };

    /// <summary>A packed-uint field's value: each part's value per frame,
    /// the largest, plus the offset, 0 outside the range, else scaled,
    /// rounded and clamped to 0..255, and the largest of the parts. Boxed
    /// as a byte, as every decoded motor level is.</summary>
    private static byte DecodePacked(VendorBlobProgram.PackedPart[] parts, ReadOnlySpan<byte> buffer)
    {
        int best = 0;
        for (int p = 0; p < parts.Length; p++)
        {
            ref readonly var part = ref parts[p];
            long v = -1;
            for (int fr = 0; fr < part.Frames; fr++)
            {
                long x = 0;
                int shift = 0;
                bool inside = true;
                for (int k = 0; k < part.PieceAt.Length; k++)
                {
                    int at = part.PieceAt[k] + fr * part.StrideBits;
                    for (int bit = 0; bit < part.PieceCount[k]; bit++, shift++)
                    {
                        int pos = at + bit;
                        if ((pos >> 3) >= buffer.Length) { inside = false; break; }
                        x |= (long)((buffer[pos >> 3] >> (pos & 7)) & 1) << shift;
                    }
                    if (!inside) break;
                }
                // A frame past a short host write is not there to read.
                if (inside && x > v) v = x;
            }
            if (v < 0) continue;
            int level = PackedMap.Level(v, part.Offset, part.Lo, part.Hi, part.Numerator, part.Denominator, part.Down);
            if (level > best) best = level;
        }
        return (byte)best;
    }

    // CRC-32/ISO-HDLC, polynomial 0xEDB88320 (matches Sony BT, dualsense-tester,
    // ds4drv, hidapi, OpenRGB SonyDualSenseController, PadForge Ds5RawHidWriter).
    // The state programs' own (Programs.Crc32), which takes sixteen bytes a
    // step, and no System.IO.Hashing, so single-file deployment works
    // without an extra NuGet dep.
    private static uint ComputeCrc32(CrcScope scope, ReadOnlySpan<byte> buffer)
    {
        uint crc = 0xFFFFFFFFu;
        if (scope.Prefix != null)
            crc = Programs.Crc32.Update(crc, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(scope.Prefix));
        int from = scope.From;
        int to = Math.Min(scope.To, buffer.Length - 1);
        // VendorBlobProgram holds a scope to start inside its report. A
        // buffer that ends before the scope starts adds nothing, as the
        // byte loop added nothing.
        if (to >= from)
            crc = Programs.Crc32.Update(crc, buffer.Slice(from, to - from + 1));
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
