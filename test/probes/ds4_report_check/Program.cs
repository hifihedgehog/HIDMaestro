// DualShock 4 input reports against SDL and Linux (issue #64).
//
// The dualshock-4-v2-bt persona switches to report 0x11 once a host reads
// feature report 0x02 or 0xA3. Through v1.9.2 that report carried motion,
// battery and touch two bytes late and left byte 1 at 0, which SDL needs
// to carry 0x80 (SDL_hidapi_ps4.c HIDAPI_DriverPS4_IsPacketValid), so an
// SDL game lost all input the moment the persona armed. The four USB maps
// were two bytes late the same way.
//
// A. Offline. Each of the five DS4 maps is encoded with VendorBlobCodec and
//    read back the way SDL and Linux read it: PS4StatePacket_t cast at
//    data[1] for report 0x01 and data[3] for report 0x11 (SDL_hidapi_ps4.c
//    HIDAPI_DriverPS4_UpdateDevice), and dualshock4_input_report_common
//    inside dualshock4_input_report_usb and _bt (hid-playstation.c). Also
//    the Bluetooth CRC both readers check (seed 0xA1), the battery and cable
//    bits, the touch report, and the sensor timestamp: the consumer's value
//    in 16/3 us ticks, or elapsed time when the consumer sets none. For the
//    four Sony Bluetooth maps, the L2 and R2 digital bits must follow the
//    analog byte, because SDL reads a set bit over a zero byte as a full
//    pull.
// B. Live, raw HID. The persona is created, armed with a feature read of
//    0x02, and its report 0x11 read off the HID stack. A report 0x01 read
//    before arming is the positive control that reads arrive at all.
// C. Live, stock SDL3. SDL's PS4 driver gets the persona: input before
//    arming, input after SDL arms it by enabling sensors, gyro 0 and 1 g at
//    rest, a 90 degree/second yaw, the touchpad, the battery, and a
//    near-zero trigger that must not read as a full pull.
//
// B and C need elevation. Exit 0 PASS, 1 FAIL, 2 when everything that ran
// passed and C was skipped for want of a stock SDL3.dll.
//
// "Ds4ReportCheck --control <profiles dir> <profile id>" runs part C alone
// against a profile loaded from that directory. Aimed at the v1.9.2 map
// under another id, it is the control that shows part C fails on the
// defect it guards.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using HIDMaestro;
using HIDMaestro.Internal;

using Microsoft.Win32.SafeHandles;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    // ── The readers' layout ─────────────────────────────────────────────
    //
    // Offsets inside the 32-byte block that follows the report id over USB,
    // and the report id plus two bytes over Bluetooth. SDL casts
    // PS4StatePacket_t there and Linux declares
    // dualshock4_input_report_common there, and the two agree byte for byte.
    const int OffButtons0 = 4, OffButtons1 = 5, OffButtons2 = 6;
    const int OffTriggerL = 7, OffTriggerR = 8;
    const int OffTimestamp = 9;    // rgucTimestamp, __le16 sensor_timestamp
    const int OffTemperature = 11; // _rgucPad0, sensor_temperature
    const int OffGyro = 12, OffAccel = 18;
    const int OffReserved2 = 24;   // _rgucPad1[5], reserved2[5]
    const int OffStatus0 = 29;     // ucBatteryLevel, status[0]
    const int OffStatus1 = 30, OffReserved3 = 31;
    // Past the block: num_touch_reports, then touch_reports[0]: its
    // timestamp byte and two 4-byte points. SDL reads the points as
    // ucTouchpadCounter1 and rgucTouchpadData1 at 34, the second at 38.
    const int OffTouchCount = 32, OffTouchTimestamp = 33, OffPoint0 = 34, OffPoint1 = 38;

    static short S16(byte[] r, int at) => (short)(r[at] | (r[at + 1] << 8));
    static ushort U16(byte[] r, int at) => (ushort)(r[at] | (r[at + 1] << 8));

    /// <summary>A touch point as hid-playstation.c (DS4_TOUCH_POINT_X/Y,
    /// DS4_TOUCH_POINT_INACTIVE) and SDL (rgucTouchpadData) decode it.</summary>
    static (bool Active, int Id, int X, int Y) Point(byte[] r, int at)
        => ((r[at] & 0x80) == 0, r[at] & 0x7F,
            r[at + 1] | ((r[at + 2] & 0x0F) << 8),
            (r[at + 2] >> 4) | (r[at + 3] << 4));

    // CRC-32, reflected polynomial 0xEDB88320, written out here rather than
    // taken from the codec so a wrong codec table cannot agree with itself.
    static readonly uint[] s_crcTable = BuildCrcTable();
    static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>CRC-32 of the seed byte followed by the first
    /// <paramref name="count"/> bytes: SDL's VerifyCRC (ubHdr 0xA1) and
    /// hid-playstation.c ps_check_crc32 (PS_INPUT_CRC32_SEED).</summary>
    static uint Crc(byte seed, byte[] data, int count)
    {
        uint c = 0xFFFFFFFFu;
        c = s_crcTable[(c ^ seed) & 0xFF] ^ (c >> 8);
        for (int i = 0; i < count; i++) c = s_crcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    // SDL_PowerState values (SDL_power.h).
    const int PowerUnknown = 0, PowerOnBattery = 1, PowerCharging = 3, PowerCharged = 4;

    /// <summary>The battery block of SDL_hidapi_ps4.c
    /// HIDAPI_DriverPS4_HandleStatePacket.</summary>
    static (int State, int Percent) SdlPower(byte status0)
    {
        int level = status0 & 0x0F;
        if ((status0 & 0x10) != 0)
        {
            if (level <= 10) return (PowerCharging, Math.Min(level * 10 + 5, 100));
            if (level == 11) return (PowerCharged, 100);
            return (PowerUnknown, 0);
        }
        return (PowerOnBattery, Math.Min(level * 10 + 5, 100));
    }

    /// <summary>Every field the layout checks look at, with values whose
    /// bytes are distinct.</summary>
    static HMGamepadState LayoutState() => new()
    {
        Buttons = HMButton.A | HMButton.Start | HMButton.Guide,
        Hat = HMHat.East,
        GyroPitch = 1440, GyroYaw = -2880, GyroRoll = 721,
        AccelX = -411, AccelY = 8192, AccelZ = 1234,
        SensorTimestamp = 0x12345670u,
        TouchpadFinger0Active = true, TouchpadFinger0X = 1000, TouchpadFinger0Y = 500, TouchpadFinger0Id = 5,
        TouchpadFinger1Active = false, TouchpadFinger1X = 300, TouchpadFinger1Y = 900, TouchpadFinger1Id = 6,
        TouchpadPacketCounter = 0x2A,
        BatteryLevel = 7, BatteryCharging = true,
        HeadphonesConnected = true, MicMuted = true,
    };

    // Stick and trigger slots for the layout state: 0.25 -> 64,
    // 0.75 -> 191, 0.5 -> 128, 0 -> 0, 1 -> 255.
    const float LX = 0.25f, LY = 0.75f, RX = 0.5f, RY = 0.0f, LT = 0.0f, RT = 1.0f;

    static byte[] Encode(ExtendedReportSpec spec, HMGamepadState st, VendorBlobCodec.EncoderState enc,
                         float lx = LX, float ly = LY, float rx = RX, float ry = RY, float lt = LT, float rt = RT)
    {
        var buf = new byte[spec.Size];
        VendorBlobCodec.EncodeInput(spec, in st, lx, ly, rx, ry, lt, rt, buf, enc);
        return buf;
    }

    /// <summary>Checks one report built from <see cref="LayoutState"/>
    /// against the readers' layout. <paramref name="r"/> may be longer than
    /// the report (a HID read is padded to the longest input report).</summary>
    static void CheckLayout(string who, byte[] r, bool bt)
    {
        int b = bt ? 3 : 1;
        int end = bt ? 74 : 64;   // first byte past the data: CRC over BT
        if (bt)
        {
            Check($"{who}: report id 0x11", r[0] == 0x11, $"0x{r[0]:X2}");
            Check($"{who}: SDL accepts it (78 bytes and data[1] & 0x80)",
                  r.Length >= 78 && (r[1] & 0x80) != 0, $"data[1] 0x{r[1]:X2}");
            Check($"{who}: byte 1 is 0xC0 and byte 2 is 0, as in the GIMX capture",
                  r[1] == 0xC0 && r[2] == 0x00, $"{r[1]:X2} {r[2]:X2}");
        }
        else
        {
            Check($"{who}: report id 0x01", r[0] == 0x01, $"0x{r[0]:X2}");
        }
        Check($"{who}: sticks at block 0-3",
              r[b] == 64 && r[b + 1] == 191 && r[b + 2] == 128 && r[b + 3] == 0,
              $"{r[b]} {r[b + 1]} {r[b + 2]} {r[b + 3]}");
        Check($"{who}: hat East and Cross in buttons[0]", r[b + OffButtons0] == 0x22, $"0x{r[b + OffButtons0]:X2}");
        Check($"{who}: Options and the R2 digital bit in buttons[1]", r[b + OffButtons1] == 0x28, $"0x{r[b + OffButtons1]:X2}");
        Check($"{who}: PS in buttons[2]", r[b + OffButtons2] == 0x01, $"0x{r[b + OffButtons2]:X2}");
        Check($"{who}: L2 0 and R2 255", r[b + OffTriggerL] == 0 && r[b + OffTriggerR] == 255,
              $"{r[b + OffTriggerL]} {r[b + OffTriggerR]}");
        Check($"{who}: sensor timestamp is the consumer's 0x12345670 / 16 (0x4567)",
              U16(r, b + OffTimestamp) == 0x4567, $"0x{U16(r, b + OffTimestamp):X4}");
        Check($"{who}: temperature byte untouched", r[b + OffTemperature] == 0, $"0x{r[b + OffTemperature]:X2}");
        Check($"{who}: gyro x/y/z at block 12/14/16",
              S16(r, b + OffGyro) == 1440 && S16(r, b + OffGyro + 2) == -2880 && S16(r, b + OffGyro + 4) == 721,
              $"{S16(r, b + OffGyro)} {S16(r, b + OffGyro + 2)} {S16(r, b + OffGyro + 4)}");
        Check($"{who}: accelerometer x/y/z at block 18/20/22",
              S16(r, b + OffAccel) == -411 && S16(r, b + OffAccel + 2) == 8192 && S16(r, b + OffAccel + 4) == 1234,
              $"{S16(r, b + OffAccel)} {S16(r, b + OffAccel + 2)} {S16(r, b + OffAccel + 4)}");
        Check($"{who}: reserved2 is zero",
              r.Skip(b + OffReserved2).Take(5).All(x => x == 0));
        // status[0]: level 7, cable (DS4_STATUS0_CABLE_STATE, BIT(4)),
        // headphones (bit 6). Bit 5 means a microphone is plugged in, which
        // MicMuted does not say, so it stays clear.
        byte s0 = r[b + OffStatus0];
        Check($"{who}: status[0] is level 7, cable and headphones (0x57)", s0 == 0x57, $"0x{s0:X2}");
        var (ps, pct) = SdlPower(s0);
        Check($"{who}: SDL reads it as charging at 75%", ps == PowerCharging && pct == 75, $"state {ps}, {pct}%");
        Check($"{who}: status[1] and reserved3 are zero",
              r[b + OffStatus1] == 0 && r[b + OffReserved3] == 0, $"{r[b + OffStatus1]:X2} {r[b + OffReserved3]:X2}");
        Check($"{who}: one touch report (Linux parses 1..{(bt ? 4 : 3)})", r[b + OffTouchCount] == 1, $"{r[b + OffTouchCount]}");
        Check($"{who}: touch report timestamp is the packet counter 0x2A",
              r[b + OffTouchTimestamp] == 0x2A, $"0x{r[b + OffTouchTimestamp]:X2}");
        var p0 = Point(r, b + OffPoint0);
        Check($"{who}: first point down, id 5, at (1000, 500)",
              p0.Active && p0.Id == 5 && p0.X == 1000 && p0.Y == 500, $"{p0}");
        var p1 = Point(r, b + OffPoint1);
        Check($"{who}: second point up, id 6", !p1.Active && p1.Id == 6, $"{p1}");
        Check($"{who}: nothing past the first touch report",
              r.Skip(b + OffPoint1 + 4).Take(end - (b + OffPoint1 + 4)).All(x => x == 0));
        if (bt)
        {
            uint want = Crc(0xA1, r, 74);
            uint got = (uint)(r[74] | (r[75] << 8) | (r[76] << 16) | (r[77] << 24));
            Check($"{who}: CRC-32 seeded 0xA1 over bytes 0-73", got == want, $"0x{got:X8} vs 0x{want:X8}");
        }
    }

    // ── A. Offline ──────────────────────────────────────────────────────

    static readonly (string Id, bool Bt)[] Ds4Maps =
    {
        ("dualshock-4-v2-bt", true),
        ("dualshock-4-v2", false),
        ("dualshock-4-v1", false),
        ("dualshock-4-v1-full", false),
        ("dualshock-4-v2-composite", false),
    };

    static void PartA(HMContext ctx)
    {
        Console.WriteLine("--- A. The five DS4 maps against the readers' layout ---");
        foreach (var (id, bt) in Ds4Maps)
        {
            Console.WriteLine($"  {id}");
            var spec = ctx.GetProfile(id)?.ExtendedReport;
            Check($"{id}: in the catalog with a report map", spec != null);
            if (spec == null) continue;
            int size = bt ? 78 : 64;
            byte rid = bt ? (byte)0x11 : (byte)0x01;
            Check($"{id}: report 0x{rid:X2} at {size} bytes, the size Linux requires",
                  spec.ReportIdByte == rid && spec.Size == size, $"0x{spec.ReportIdByte:X2}, {spec.Size}");
            int b = bt ? 3 : 1;

            CheckLayout(id, Encode(spec, LayoutState(), new VendorBlobCodec.EncoderState()), bt);

            // Battery states, read the way SDL reads them.
            var full = LayoutState();
            full.BatteryLevel = 10; full.BatteryCharging = false; full.BatteryFull = true;
            byte sFull = Encode(spec, full, new VendorBlobCodec.EncoderState())[b + OffStatus0];
            var pFull = SdlPower(sFull);
            Check($"{id}: full on the cable is level 11 with the cable bit (0x5B), SDL charged",
                  sFull == 0x5B && pFull.State == PowerCharged && pFull.Percent == 100, $"0x{sFull:X2}");
            var dis = LayoutState();
            dis.BatteryLevel = 4; dis.BatteryCharging = false; dis.HeadphonesConnected = false;
            byte sDis = Encode(spec, dis, new VendorBlobCodec.EncoderState())[b + OffStatus0];
            var pDis = SdlPower(sDis);
            Check($"{id}: level 4 off the cable is 0x04, SDL on battery at 45%",
                  sDis == 0x04 && pDis.State == PowerOnBattery && pDis.Percent == 45, $"0x{sDis:X2}");

            // The consumer's timestamp wraps cleanly: 2^32 / 16 is a
            // multiple of 2^16. hid-playstation.c and SDL take the delta
            // modulo 2^16, so a 32 tick step across the wrap reads as 2.
            var enc = new VendorBlobCodec.EncoderState();
            var w1 = LayoutState(); w1.SensorTimestamp = 0xFFFFFFF0u;
            var w2 = LayoutState(); w2.SensorTimestamp = 0x00000010u;
            ushort t1 = U16(Encode(spec, w1, enc), b + OffTimestamp);
            ushort t2 = U16(Encode(spec, w2, enc), b + OffTimestamp);
            int wrapDelta = t1 > t2 ? ushort.MaxValue - t1 + t2 + 1 : t2 - t1;
            Check($"{id}: timestamp wraps from 0xFFFF to 0x0001 as 2 ticks",
                  t1 == 0xFFFF && t2 == 0x0001 && wrapDelta == 2, $"0x{t1:X4} -> 0x{t2:X4}");

            // No consumer timestamp: the elapsed time stands in. The bound
            // is the interval between the two encodes, measured around them.
            var c0 = LayoutState(); c0.SensorTimestamp = 0;
            var encC = new VendorBlobCodec.EncoderState();
            long a0 = Stopwatch.GetTimestamp();
            var rc1 = Encode(spec, c0, encC);
            long a1 = Stopwatch.GetTimestamp();
            Thread.Sleep(40);
            long b0 = Stopwatch.GetTimestamp();
            var rc2 = Encode(spec, c0, encC);
            long b1 = Stopwatch.GetTimestamp();
            CheckElapsed($"{id}: with no consumer timestamp",
                         U16(rc1, b + OffTimestamp), U16(rc2, b + OffTimestamp), a0, a1, b0, b1);
        }

        // The four Sony Bluetooth maps carry the L2 and R2 digital bits. SDL's
        // PS4 and PS5 drivers turn a set bit over a zero analog byte into a
        // full pull (HIDAPI_DriverPS4_HandleStatePacket,
        // HIDAPI_DriverPS5_HandleStatePacketCommon), so the bit must be set
        // exactly when the byte is nonzero.
        Console.WriteLine("  L2 and R2 digital bits on the Bluetooth maps");
        foreach (var id in new[] { "dualshock-4-v2-bt", "dualsense-bt", "dualsense-bt-full", "dualsense-edge-bt" })
        {
            var spec = ctx.GetProfile(id)?.ExtendedReport;
            if (spec == null) { Check($"{id}: in the catalog", false); continue; }
            int ltAt = spec.Fields.First(f => f.Semantic == "leftTrigger").Byte!.Value;
            int rtAt = spec.Fields.First(f => f.Semantic == "rightTrigger").Byte!.Value;
            var mask = spec.Fields.FirstOrDefault(f => f.Buttons != null && f.Buttons.Contains("LT_DIGITAL"));
            Check($"{id}: declares the L2 and R2 digital bits", mask != null);
            if (mask == null) continue;
            VendorBlobProgram.TryParseBitRange(mask.Bits, out int lo, out _);
            int ltBit = lo + mask.Buttons!.IndexOf("LT_DIGITAL"), rtBit = lo + mask.Buttons.IndexOf("RT_DIGITAL");
            bool agree = true;
            var seen = new List<string>();
            foreach (float v in new[] { 0f, 0.001f, 0.0019f, 0.002f, 0.003f, 0.5f, 1f })
            {
                var r = Encode(spec, new HMGamepadState(), new VendorBlobCodec.EncoderState(),
                               0.5f, 0.5f, 0.5f, 0.5f, v, v);
                bool l = ((r[mask.Byte!.Value] >> ltBit) & 1) != 0, rr = ((r[mask.Byte.Value] >> rtBit) & 1) != 0;
                agree &= l == (r[ltAt] != 0) && rr == (r[rtAt] != 0);
                seen.Add($"{v}:{r[ltAt]}/{(l ? 1 : 0)}");
            }
            Check($"{id}: each digital bit is set exactly when its trigger byte is nonzero", agree,
                  string.Join(" ", seen));
        }
    }

    /// <summary>The stamp of the second of two reports, the first taken at
    /// the clock's origin, must equal the time between them in 16/3 us
    /// ticks. a0..a1 and b0..b1 bracket the two encodes.</summary>
    static void CheckElapsed(string who, ushort first, ushort second, long a0, long a1, long b0, long b1)
    {
        double f = 1_000_000.0 / Stopwatch.Frequency;
        double minUs = (b0 - a1) * f, maxUs = (b1 - a0) * f;
        int lo = (int)Math.Floor(minUs * 3 / 16) - 1, hi = (int)Math.Ceiling(maxUs * 3 / 16) + 1;
        int delta = (ushort)(second - first);
        Check($"{who}: the first stamp is the clock's origin, 0", first == 0, $"0x{first:X4}");
        Check($"{who}: the next one advances by the elapsed time in 16/3 us ticks",
              delta >= lo && delta <= hi, $"{delta} ticks, bound {lo}..{hi} ({minUs:F0}-{maxUs:F0} us)");
    }

    // ── B. Live, raw HID ────────────────────────────────────────────────

    static void PartB(HMContext ctx)
    {
        Console.WriteLine();
        Console.WriteLine("--- B. The live dualshock-4-v2-bt persona off the HID stack ---");
        var prof = ctx.GetProfile("dualshock-4-v2-bt")!;
        var pre = Present(0x054C, 0x09CC);
        HMController? c = null;
        try
        {
            c = ctx.CreateController(prof);
            var dev = WaitForNew(0x054C, 0x09CC, pre, 15000);
            Check("the persona's HID interface appears", dev != null, dev?.DevicePath ?? "");
            if (dev == null) return;
            Check("its input length is the descriptor's longest report, 0x19 at 547 bytes",
                  dev.InputReportByteLength == 547, $"{dev.InputReportByteLength}");
            using var rd = new HidReader(dev.DevicePath, Math.Max(dev.InputReportByteLength, (ushort)78));
            Check("the interface opens for reading", rd.IsOpen, rd.OpenError);
            if (!rd.IsOpen) return;

            // Positive control: unarmed, the persona sends report 0x01.
            var left = new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(prof, 0.0f, 0.5f, 0.5f, 0.5f, 0f, 0f) };
            var r01 = DriveUntil(c, rd, left, r => r[0] == 0x01 && r[1] == 0, 3000);
            Check("before arming, report 0x01 arrives", r01 != null);

            // Arm with the read SDL's calibration load makes first and the
            // GIMX capture names ("sent once the GET REPORT FEATURE 0x02 is
            // received").
            var cal = GetFeature(dev.DevicePath, 0x02, 64);
            Check("GetFeature(0x02) answers", cal != null);

            var st = LayoutState();
            st.Axes = HMGamepadStateHelpers.StandardAxes(prof, LX, LY, RX, RY, LT, RT);
            var r11 = DriveUntil(c, rd, st, r => r[0] == 0x11 && r[3] == 64, 3000);
            Check("after arming, report 0x11 arrives", r11 != null);
            if (r11 == null) return;
            CheckLayout("live", r11, bt: true);

            // The clock stands in when the consumer sets no timestamp. One
            // submit per stamp, bracketed, each found by its stick marker.
            var s0 = LayoutState(); s0.SensorTimestamp = 0;
            s0.Axes = HMGamepadStateHelpers.StandardAxes(prof, 0.0f, LY, RX, RY, LT, RT);
            var s1 = LayoutState(); s1.SensorTimestamp = 0;
            s1.Axes = HMGamepadStateHelpers.StandardAxes(prof, 1.0f, LY, RX, RY, LT, RT);
            long a0 = Stopwatch.GetTimestamp(); c.SubmitState(s0); long a1 = Stopwatch.GetTimestamp();
            var ra = ReadUntil(rd, r => r[0] == 0x11 && r[3] == 0, 2000);
            Thread.Sleep(50);
            long b0 = Stopwatch.GetTimestamp(); c.SubmitState(s1); long b1 = Stopwatch.GetTimestamp();
            var rb = ReadUntil(rd, r => r[0] == 0x11 && r[3] == 255, 2000);
            Check("both timestamp reports arrive", ra != null && rb != null);
            if (ra != null && rb != null)
                CheckElapsed("live, no consumer timestamp", U16(ra, 12), U16(rb, 12), a0, a1, b0, b1);
        }
        catch (Exception ex) { Check("part B ran without throwing", false, ex.Message); }
        finally
        {
            c?.Dispose();
            Thread.Sleep(500);
        }
    }

    static byte[]? DriveUntil(HMController c, HidReader rd, HMGamepadState st, Func<byte[], bool> match, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            c.SubmitState(st);
            long until = sw.ElapsedMilliseconds + 80;
            while (sw.ElapsedMilliseconds < until)
            {
                var r = rd.Read(40);
                if (r == null) break;
                if (match(r)) return r;
            }
        }
        return null;
    }

    static byte[]? ReadUntil(HidReader rd, Func<byte[], bool> match, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var r = rd.Read((int)Math.Max(1, ms - sw.ElapsedMilliseconds));
            if (r == null) return null;
            if (match(r)) return r;
        }
        return null;
    }

    static HashSet<string> Present(ushort vid, ushort pid)
        => new(HidDeviceEnumerator.Enumerate()
                   .Where(d => d.VendorId == vid && d.ProductId == pid)
                   .Select(d => d.DevicePath), StringComparer.OrdinalIgnoreCase);

    /// <summary>The first matching HID interface that was not present
    /// before the create. A real pad shares the VID and PID, so taking the
    /// first match could open the bench's own hardware.</summary>
    static HidDeviceEnumerator.RawHidDevice? WaitForNew(ushort vid, ushort pid, HashSet<string> before, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var d = HidDeviceEnumerator.Enumerate()
                .FirstOrDefault(x => x.VendorId == vid && x.ProductId == pid && !before.Contains(x.DevicePath));
            if (d != null) return d;
            Thread.Sleep(100);
        }
        return null;
    }

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3;
    const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);

    static byte[]? GetFeature(string path, byte id, int len)
    {
        using var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var buf = new byte[len];
        buf[0] = id;
        return HidD_GetFeature(h, buf, buf.Length) && buf[0] == id ? buf : null;
    }

    /// <summary>Overlapped reads with a timeout, so a silent device fails a
    /// check instead of hanging the probe.</summary>
    sealed class HidReader : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, IntPtr read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out int transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateEventW(IntPtr sec, bool manualReset, bool initialState, IntPtr name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ResetEvent(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);

        const int ERROR_IO_PENDING = 997;
        readonly SafeFileHandle _h;
        readonly IntPtr _buf, _ov, _event;
        readonly int _len;

        public bool IsOpen => !_h.IsInvalid;
        public string OpenError { get; } = "";

        public HidReader(string path, int len)
        {
            _len = len;
            _h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING,
                             FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (_h.IsInvalid) OpenError = $"error {Marshal.GetLastWin32Error()}";
            _buf = Marshal.AllocHGlobal(len);
            _ov = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            _event = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        }

        public byte[]? Read(int timeoutMs)
        {
            if (!IsOpen) return null;
            Marshal.StructureToPtr(new NativeOverlapped { EventHandle = _event }, _ov, false);
            ResetEvent(_event);
            if (!ReadFile(_h, _buf, _len, IntPtr.Zero, _ov))
            {
                if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return null;
                if (WaitForSingleObject(_event, (uint)Math.Max(1, timeoutMs)) != 0)
                {
                    CancelIoEx(_h, _ov);
                    GetOverlappedResult(_h, _ov, out _, true);
                    return null;
                }
            }
            if (!GetOverlappedResult(_h, _ov, out int n, false) || n <= 0) return null;
            var r = new byte[n];
            Marshal.Copy(_buf, r, 0, n);
            return r;
        }

        public void Dispose()
        {
            _h.Dispose();
            Marshal.FreeHGlobal(_buf);
            Marshal.FreeHGlobal(_ov);
            if (_event != IntPtr.Zero) CloseHandle(_event);
        }
    }

    // ── C. Live, stock SDL3 ─────────────────────────────────────────────

    const string SDL = "SDL3";
    [DllImport(SDL)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_SetHint(string name, string value);
    [DllImport(SDL)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_Init(uint flags);
    [DllImport(SDL)] static extern void SDL_Quit();
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepads(out int count);
    [DllImport(SDL)] static extern ushort SDL_GetGamepadVendorForID(uint id);
    [DllImport(SDL)] static extern ushort SDL_GetGamepadProductForID(uint id);
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepadPathForID(uint id);
    [DllImport(SDL)] static extern IntPtr SDL_OpenGamepad(uint id);
    [DllImport(SDL)] static extern void SDL_CloseGamepad(IntPtr gp);
    [DllImport(SDL)] static extern void SDL_UpdateGamepads();
    [DllImport(SDL)] static extern int SDL_GetGamepadType(IntPtr gp);
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepadName(IntPtr gp);
    [DllImport(SDL)] static extern short SDL_GetGamepadAxis(IntPtr gp, int axis);
    [DllImport(SDL)] static extern int SDL_GetNumGamepadTouchpads(IntPtr gp);
    [DllImport(SDL)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_GetGamepadTouchpadFinger(
        IntPtr gp, int touchpad, int finger, [MarshalAs(UnmanagedType.U1)] out bool down,
        out float x, out float y, out float pressure);
    [DllImport(SDL)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_SetGamepadSensorEnabled(IntPtr gp, int type, [MarshalAs(UnmanagedType.U1)] bool enabled);
    [DllImport(SDL)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_GetGamepadSensorData(IntPtr gp, int type, [Out] float[] data, int count);
    [DllImport(SDL)] static extern int SDL_GetGamepadPowerInfo(IntPtr gp, out int percent);
    [DllImport(SDL)] static extern void SDL_free(IntPtr mem);
    [DllImport(SDL)] static extern IntPtr SDL_GetError();

    const uint SDL_INIT_GAMEPAD = 0x00002000u;
    const int SDL_GAMEPAD_TYPE_PS4 = 5;
    const int AXIS_LEFTX = 0, AXIS_LEFT_TRIGGER = 4;
    const int SENSOR_ACCEL = 1, SENSOR_GYRO = 2;
    const float StandardGravity = 9.80665f;

    static string Err() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "";

    /// <summary>A STOCK SDL3.dll. The sibling SDL3-build checkout carries
    /// two: build\Release is a fork that skips HIDMaestro devices outright
    /// and would make every check below vacuous, and build-stock\Release is
    /// upstream libsdl-org/SDL. Only the second one is searched for.</summary>
    static string? FindStockSdl()
    {
        string root = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && root.Length > 3; i++)
        {
            string sib = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(root, "..", "SDL3-build", "build-stock", "Release", "SDL3.dll"));
            if (System.IO.File.Exists(sib)) return sib;
            root = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, ".."));
        }
        return null;
    }

    static IntPtr WaitForGamepad(ushort vid, ushort pid, string path, int seconds)
    {
        for (int i = 0; i < seconds * 10; i++)
        {
            SDL_UpdateGamepads();
            IntPtr arr = SDL_GetGamepads(out int n);
            if (arr != IntPtr.Zero)
            {
                try
                {
                    for (int j = 0; j < n; j++)
                    {
                        uint id = (uint)Marshal.ReadInt32(arr, j * 4);
                        if (SDL_GetGamepadVendorForID(id) != vid || SDL_GetGamepadProductForID(id) != pid) continue;
                        string? p = Marshal.PtrToStringUTF8(SDL_GetGamepadPathForID(id));
                        if (!string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) continue;
                        IntPtr gp = SDL_OpenGamepad(id);
                        if (gp != IntPtr.Zero) return gp;
                    }
                }
                finally { SDL_free(arr); }
            }
            Thread.Sleep(100);
        }
        return IntPtr.Zero;
    }

    /// <summary>Submit a state for long enough for SDL's reader thread, then
    /// let SDL fold it into the gamepad state.</summary>
    static void Drive(HMController c, HMGamepadState st)
    {
        for (int i = 0; i < 8; i++) { c.SubmitState(st); Thread.Sleep(50); }
        SDL_UpdateGamepads();
        Thread.Sleep(80);
        SDL_UpdateGamepads();
    }

    static bool PartC(HMContext ctx, string profileId = "dualshock-4-v2-bt")
    {
        Console.WriteLine();
        Console.WriteLine($"--- C. The live persona through stock SDL3 ({profileId}) ---");
        string? dll = FindStockSdl();
        if (dll == null)
        {
            Console.WriteLine("  [SKIP] no stock SDL3.dll; this part needs the sibling");
            Console.WriteLine("         SDL3-build/build-stock/Release build of upstream libsdl-org/SDL.");
            return false;
        }
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly,
            (name, asm, path) => name == SDL ? NativeLibrary.Load(dll) : IntPtr.Zero);
        Console.WriteLine($"  SDL3.dll: {dll}");

        // HIDAPI only, so SDL's PS4 driver is the one that can claim the
        // device. SDL_JOYSTICK_ENHANCED_REPORTS stays at its default, "1",
        // which is what a game gets.
        SDL_SetHint("SDL_JOYSTICK_HIDAPI", "1");
        SDL_SetHint("SDL_JOYSTICK_HIDAPI_PS4", "1");
        SDL_SetHint("SDL_JOYSTICK_RAWINPUT", "0");
        SDL_SetHint("SDL_JOYSTICK_THREAD", "1");
        if (!SDL_Init(SDL_INIT_GAMEPAD))
        {
            Check("SDL_Init", false, Err());
            return true;
        }

        var prof = ctx.GetProfile(profileId);
        if (prof == null)
        {
            Check($"profile {profileId} is loaded", false);
            return true;
        }
        var pre = Present(0x054C, 0x09CC);
        HMController? c = null;
        IntPtr gp = IntPtr.Zero;
        try
        {
            c = ctx.CreateController(prof);
            var dev = WaitForNew(0x054C, 0x09CC, pre, 15000);
            Check("the persona's HID interface appears", dev != null);
            if (dev == null) return true;
            gp = WaitForGamepad(0x054C, 0x09CC, dev.DevicePath, 20);
            Check("SDL's HIDAPI driver opens the persona as a gamepad", gp != IntPtr.Zero);
            if (gp == IntPtr.Zero) return true;
            Console.WriteLine($"     SDL name: {Marshal.PtrToStringUTF8(SDL_GetGamepadName(gp))}");
            Check("SDL types it as a PS4 controller", SDL_GetGamepadType(gp) == SDL_GAMEPAD_TYPE_PS4,
                  $"type {SDL_GetGamepadType(gp)}");

            HMGamepadState Rest(float lx, float lt = 0f) => new()
            {
                Axes = HMGamepadStateHelpers.StandardAxes(prof, lx, 0.5f, 0.5f, 0.5f, lt, 0f),
                AccelY = 8192,
                TouchpadFinger0Active = true, TouchpadFinger0X = 960, TouchpadFinger0Y = 460, TouchpadFinger0Id = 3,
                TouchpadFinger1Active = false, TouchpadFinger1Id = 4,
                BatteryLevel = 7, BatteryCharging = true,
            };

            // Unarmed: report 0x01 only.
            Drive(c, Rest(0.0f));
            short lx0 = SDL_GetGamepadAxis(gp, AXIS_LEFTX);
            Check("before arming SDL reads the left stick hard left", lx0 <= -30000, $"LEFTX {lx0}");

            // Enabling the sensors makes SDL load calibration, which reads
            // feature 0x02 and so arms the persona
            // (HIDAPI_DriverPS4_SetJoystickSensorsEnabled,
            // HIDAPI_DriverPS4_LoadOfficialCalibrationData).
            bool acc = SDL_SetGamepadSensorEnabled(gp, SENSOR_ACCEL, true);
            bool gyr = SDL_SetGamepadSensorEnabled(gp, SENSOR_GYRO, true);
            Check("SDL enables the accelerometer and the gyro", acc && gyr, acc && gyr ? "" : Err());

            // The arm lands asynchronously, so frames submitted right after
            // the calibration read can still go out as report 0x01. Read the
            // device through a second handle until report 0x11 shows, so the
            // stick check below cannot pass on a stale pre-arm frame.
            using (var rd = new HidReader(dev.DevicePath, Math.Max(dev.InputReportByteLength, (ushort)78)))
            {
                var armed = DriveUntil(c, rd, Rest(0.5f), r => r[0] == 0x11, 3000);
                Check("the persona now sends report 0x11 (SDL's calibration read armed it)", armed != null);
            }

            // The regression itself: input keeps arriving once armed.
            Drive(c, Rest(1.0f));
            short lx1 = SDL_GetGamepadAxis(gp, AXIS_LEFTX);
            Check("after arming SDL still reads input (left stick hard right)", lx1 >= 30000, $"LEFTX {lx1}");

            // SDL reads touch and battery only from report 0x11, after it has
            // seen one (enhanced_reports), so these show 0x11 was accepted.
            Check("SDL publishes the touchpad", SDL_GetNumGamepadTouchpads(gp) >= 1);
            SDL_GetGamepadTouchpadFinger(gp, 0, 0, out bool d0, out float x0, out float y0, out _);
            Check("SDL reads the first finger at the center of the pad",
                  d0 && Math.Abs(x0 - 0.5f) < 0.002f && Math.Abs(y0 - 0.5f) < 0.002f, $"down {d0} ({x0:F4}, {y0:F4})");
            SDL_GetGamepadTouchpadFinger(gp, 0, 1, out bool d1, out _, out _, out _);
            Check("SDL reads the second finger as up", !d1);

            int ps = SDL_GetGamepadPowerInfo(gp, out int pct);
            Check("SDL reads the battery as charging at 75%", ps == PowerCharging && pct == 75, $"state {ps}, {pct}%");

            // At rest: no rotation, 1 g along +Y. The persona's calibration
            // is the identity for 8192 per g and 16 per degree/second.
            var g = new float[3];
            var a = new float[3];
            SDL_GetGamepadSensorData(gp, SENSOR_GYRO, g, 3);
            SDL_GetGamepadSensorData(gp, SENSOR_ACCEL, a, 3);
            Check("at rest SDL reads the gyro as 0", g[0] == 0f && g[1] == 0f && g[2] == 0f,
                  $"[{g[0]:F4}, {g[1]:F4}, {g[2]:F4}] rad/s");
            Check("at rest SDL reads the accelerometer as 1 g",
                  Math.Abs(a[0]) < 0.001f && Math.Abs(a[1] - StandardGravity) < 0.001f && Math.Abs(a[2]) < 0.001f,
                  $"[{a[0]:F4}, {a[1]:F4}, {a[2]:F4}] m/s2");

            var yaw = Rest(0.5f);
            yaw.GyroYaw = 1440; // 90 degrees/second at 16 per degree/second
            Drive(c, yaw);
            SDL_GetGamepadSensorData(gp, SENSOR_GYRO, g, 3);
            Check("SDL reads a 90 degree/second yaw as pi/2 rad/s on its own axis",
                  g[0] == 0f && Math.Abs(g[1] - MathF.PI / 2) < 0.002f && g[2] == 0f,
                  $"[{g[0]:F4}, {g[1]:F4}, {g[2]:F4}] rad/s");

            var full = Rest(0.5f);
            full.BatteryLevel = 10; full.BatteryCharging = false; full.BatteryFull = true;
            Drive(c, full);
            ps = SDL_GetGamepadPowerInfo(gp, out pct);
            Check("SDL reads a full battery on the cable as charged", ps == PowerCharged && pct == 100, $"state {ps}, {pct}%");
            var off = Rest(0.5f);
            off.BatteryLevel = 4; off.BatteryCharging = false;
            Drive(c, off);
            ps = SDL_GetGamepadPowerInfo(gp, out pct);
            Check("SDL reads level 4 off the cable as on battery at 45%", ps == PowerOnBattery && pct == 45, $"state {ps}, {pct}%");

            // A trigger too light to move the analog byte must not set the
            // digital bit, or SDL reads a full pull.
            Drive(c, Rest(0.5f, 0.001f));
            short lt0 = SDL_GetGamepadAxis(gp, AXIS_LEFT_TRIGGER);
            Check("a near-zero L2 reads as released, not as a full pull", lt0 == 0, $"LEFT_TRIGGER {lt0}");
            Drive(c, Rest(0.5f, 1.0f));
            short lt1 = SDL_GetGamepadAxis(gp, AXIS_LEFT_TRIGGER);
            Check("a full L2 reads as a full pull", lt1 >= 32000, $"LEFT_TRIGGER {lt1}");
        }
        catch (Exception ex) { Check("part C ran without throwing", false, ex.Message); }
        finally
        {
            if (gp != IntPtr.Zero) SDL_CloseGamepad(gp);
            c?.Dispose();
            Thread.Sleep(500);
            SDL_UpdateGamepads();
            SDL_Quit();
        }
        return true;
    }

    static int Main(string[] args)
    {
        Console.WriteLine("=== DualShock 4 input reports against SDL and Linux (issue #64) ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        if (args.Length == 3 && args[0] == "--control")
        {
            Console.WriteLine($"  control run: {ctx.LoadProfilesFromDirectory(args[1])} profile(s) from {args[1]}");
            ctx.InstallDriver();
            bool ran = PartC(ctx, args[2]);
            try { HMContext.RemoveAllVirtualControllers(); } catch { }
            Console.WriteLine();
            Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
            return s_failures > 0 ? 1 : ran ? 0 : 2;
        }

        PartA(ctx);

        bool elevated;
        using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
            elevated = new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        bool sdlRan = false;
        if (!elevated)
        {
            Console.WriteLine();
            Console.WriteLine("  [SKIP] parts B and C create a controller and need elevation");
        }
        else
        {
            ctx.InstallDriver();
            PartB(ctx);
            sdlRan = PartC(ctx);
            try { HMContext.RemoveAllVirtualControllers(); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        if (s_failures > 0) return 1;
        return elevated && sdlRan ? 0 : 2;
    }
}
