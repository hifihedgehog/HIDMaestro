// DualShock 3 with pressure-sensitive buttons (PadForge discussion 476).
//
// PCSX2 and RPCS3 read a DualShock 3's pressure-sensitive buttons on Windows
// from the form Sony's sixaxis.sys driver presents, which DsHidMini's SXS
// mode reproduces and PCSX2's documentation prescribes: a descriptor with no
// report ids, a 12-byte joystick input report, and the native 49-byte DS3
// report served as the report 0 feature reply. The dualshock-3-full persona
// presents that form.
//
// A. Offline. The persona's map is encoded with VendorBlobCodec and read
//    back at the offsets SDL_hidapi_ps3.c, hid-sony.c and RPCS3's
//    ds3_pad_handler.h use: buttons, sticks, the twelve pressure bytes,
//    battery, and the 10-bit big-endian motion RPCS3 derives from a DS4.
// B. Live, raw HID. The persona's descriptor sizes, its 12-byte input
//    report against DsHidMini's conversion (DsHid.c
//    DS3_RAW_TO_SIXAXIS_HID_INPUT_REPORT, rewritten here independently),
//    RPCS3's Windows read sequence (feature 0xF2, which the HID class serves
//    as report 0 for a descriptor with no ids), SDL's report 0 poll, and
//    output commands from SDL and RPCS3 decoded by the SDK.
// C. Live, stock SDL3 with PCSX2's hint, SDL_JOYSTICK_HIDAPI_PS3_SIXAXIS_DRIVER.
//    SDL opens it as a PS3 controller with 16 axes and 11 buttons, the shape
//    PCSX2's IsControllerSixaxis requires, reads every pressure axis, and
//    its rumble and LED commands reach the SDK.
// D. Live, the native-descriptor dualshock-3 persona (issue #65). A host
//    writes the native output report 0x01 directly, the report hid-sony.c
//    and SDL_hidapi_ps3.c send. Through WriteFile and HidD_SetOutputReport
//    it reaches OutputDecoded with the motors and LEDs at hid-sony's
//    offsets, the decode PadForge's DualShock 3 motor handler reads.
//
// B, C and D need elevation. Exit 0 PASS, 1 FAIL, 2 when everything that ran
// passed and C was skipped for want of a stock SDL3.dll.

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

    const string ProfileId = "dualshock-3-full";
    const ushort Vid = 0x054C, Pid = 0x0268;

    // ── The test state ──────────────────────────────────────────────────
    //
    // Cross pressed with no pressure (sent as 255), circle released with a
    // pressure of 77 (sent as given), triangle pressed at 200, L1 pressed
    // with none (255), d-pad up and right pressed through the hat, right
    // at 50. Sticks 64/191/128/0, L2 half pulled. Motion: 1 g on X,
    // -0.5 g on Y, 0.25 g on Z, 90 degrees/second of yaw, in the SDK's
    // DS4-scaled fields (8192 per g, 16 per degree/second). Battery level 7.
    const float LX = 0.25f, LY = 0.75f, RX = 0.5f, RY = 0.0f, LT = 0.5f, RT = 0.0f;

    static HMGamepadState TestState() => new()
    {
        Buttons = HMButton.A | HMButton.Y | HMButton.LeftBumper | HMButton.Start | HMButton.Guide,
        Hat = HMHat.NorthEast,
        PressureB = 77,
        PressureY = 200,
        PressureDpadRight = 50,
        AccelX = 8192, AccelY = -4096, AccelZ = 2048,
        GyroYaw = 1440,
        BatteryLevel = 7,
    };

    /// <summary>The native report the test state must produce, byte by
    /// byte. Offsets per DsHidMini Ds3Types.h DS3_RAW_INPUT_REPORT.</summary>
    static byte[] ExpectedNative()
    {
        var e = new byte[49];
        e[0] = 0x01;
        e[2] = 0x08 | 0x10 | 0x20;        // start, up, right
        e[3] = 0x01 | 0x04 | 0x10 | 0x40; // L2 (byte 18 nonzero), L1, triangle, cross
        e[4] = 0x01;                      // PS
        e[6] = 64; e[7] = 191; e[8] = 128; e[9] = 0;
        e[14] = 255; e[15] = 50; e[16] = 0; e[17] = 0;  // up right down left
        e[18] = 128; e[19] = 0;                          // L2 R2
        e[20] = 255; e[21] = 0;                          // L1 R1
        e[22] = 200; e[23] = 77; e[24] = 255; e[25] = 0; // triangle circle cross square
        e[30] = 4;                                       // level 7 -> 75%
        // 512 + 113 * 1 g = 625. 512 - round(113 * 0.25) = 484.
        // 512 + round(113 * 0.5) = 569. 512 - 123 = 389.
        e[41] = 0x02; e[42] = 0x71;
        e[43] = 0x01; e[44] = 0xE4;
        e[45] = 0x02; e[46] = 0x39;
        e[47] = 0x01; e[48] = 0x85;
        return e;
    }

    static int Be16(byte[] r, int at) => (r[at] << 8) | r[at + 1];
    static int Le16(byte[] r, int at) => r[at] | (r[at + 1] << 8);

    // ── A. Offline ──────────────────────────────────────────────────────

    static (bool ids, Dictionary<string, int> bytes) DescriptorSizes(byte[] desc)
    {
        int i = 0, rsize = 0, rcount = 0;
        bool ids = false;
        var bits = new Dictionary<string, int>();
        while (i < desc.Length)
        {
            int b = desc[i], s = b & 3; if (s == 3) s = 4;
            int tag = b >> 2, val = 0;
            for (int k = 0; k < s; k++) val |= desc[i + 1 + k] << (8 * k);
            i += 1 + s;
            if (tag == 0x21) ids = true;
            else if (tag == 0x1D) rsize = val;
            else if (tag == 0x25) rcount = val;
            else if (tag == 0x20 || tag == 0x24 || tag == 0x2C)
            {
                string k = tag == 0x20 ? "input" : tag == 0x24 ? "output" : "feature";
                bits[k] = (bits.TryGetValue(k, out int v) ? v : 0) + rsize * rcount;
            }
        }
        return (ids, bits.ToDictionary(kv => kv.Key, kv => kv.Value / 8));
    }

    static void PartA(HMContext ctx)
    {
        Console.WriteLine("--- A. The native DS3 report against SDL, Linux and RPCS3 ---");
        var prof = ctx.GetProfile(ProfileId);
        Check($"{ProfileId} is in the catalog", prof != null);
        if (prof == null) return;
        var inner = prof.Inner;
        Check("it is a Sony DualShock, so PadForge's PlayStation list offers it",
              prof.Vendor == "Sony" && prof.Name.Contains("DualShock"), prof.Name);
        Check("it is marked sixaxis-compatible", inner.SixaxisCompatible == true);
        var (ids, sizes) = DescriptorSizes(inner.GetDescriptorBytes()!);
        Check("its descriptor declares no report ids, like DsHidMini's SXS descriptor", !ids);
        Check("input 12, output 48 and feature 49 data bytes (DsHid.h SIXAXIS_* sizes)",
              sizes.GetValueOrDefault("input") == 12 && sizes.GetValueOrDefault("output") == 48
              && sizes.GetValueOrDefault("feature") == 49,
              string.Join(", ", sizes.Select(kv => $"{kv.Key} {kv.Value}")));
        var spec = inner.ExtendedReport!;
        Check("its extended report is the native 0x01 report at 49 bytes, always armed",
              spec.ReportIdByte == 0x01 && spec.Size == 49 && spec.AlwaysArmed == true);

        var r = new byte[49];
        var st = TestState();
        VendorBlobCodec.EncodeInput(spec, in st, LX, LY, RX, RY, LT, RT, r, new VendorBlobCodec.EncoderState());
        var e = ExpectedNative();
        var diff = Enumerable.Range(0, 49).Where(k => r[k] != e[k]).Select(k => $"{k}:{r[k]:X2}/{e[k]:X2}").ToList();
        Check("the encoded report matches the expected bytes", diff.Count == 0, string.Join(" ", diff));

        // SDL_hidapi_ps3.c HandleStatePacket: data[1] 0xFF marks an invalid
        // packet, and pressure axes 6-15 follow button order (button_axis_offsets).
        Check("byte 1 is not SDL's 0xFF invalid-packet mark", r[1] != 0xFF);
        int[] sdlOffsets = { 24, 23, 25, 22, 20, 21, 14, 16, 17, 15 };
        string[] names = { "cross", "circle", "square", "triangle", "L1", "R1", "up", "down", "left", "right" };
        int[] want = { 255, 77, 0, 200, 255, 0, 255, 0, 0, 50 };
        for (int k = 0; k < 10; k++)
            Check($"SDL axis {6 + k} ({names[k]} pressure) reads {want[k]}", r[sdlOffsets[k]] == want[k], $"{r[sdlOffsets[k]]}");

        // hid-sony.c sixaxis_raw_event: battery index into {0,1,25,50,75,100},
        // 0xEE charging, 0xEF full. Motion is big-endian, X = v - 511,
        // Y = 511 - (45..46), Z = 511 - (43..44), 113 per g.
        Check("Linux reads the battery at 75%", r[30] == 4, $"{r[30]}");
        Check("Linux reads about +1 g on X", Math.Abs((Be16(r, 41) - 511) / 113.0 - 1.0) < 0.02, $"{Be16(r, 41)}");
        Check("Linux reads about -0.5 g on Y", Math.Abs((511 - Be16(r, 45)) / 113.0 + 0.5) < 0.02, $"{Be16(r, 45)}");
        Check("Linux reads about +0.25 g on Z", Math.Abs((511 - Be16(r, 43)) / 113.0 - 0.25) < 0.02, $"{Be16(r, 43)}");

        // RPCS3 computes the same PS3 sensor values from a DS4
        // (ds4_pad_handler.cpp get_extended_info): 512 - 113 g per accel axis,
        // 512 - 123/90 degrees/second for yaw. On Linux it reads X as
        // 1024 - raw, and Y and Z as bytes 45 and 43 read straight.
        Check("RPCS3's X matches its own DS4 formula (399)", 1024 - Be16(r, 41) == 399, $"{1024 - Be16(r, 41)}");
        Check("RPCS3's Y matches its own DS4 formula (568.5)", Math.Abs(Be16(r, 45) - 568.5) <= 0.5, $"{Be16(r, 45)}");
        Check("RPCS3's Z matches its own DS4 formula (483.75)", Math.Abs(Be16(r, 43) - 483.75) <= 0.5, $"{Be16(r, 43)}");
        Check("RPCS3's gyro matches its own DS4 formula (389)", Be16(r, 47) == 389, $"{Be16(r, 47)}");

        // Battery states and the clamp.
        var chg = TestState(); chg.BatteryCharging = true;
        var full = TestState(); full.BatteryFull = true;
        var r2 = new byte[49]; var r3 = new byte[49];
        VendorBlobCodec.EncodeInput(spec, in chg, LX, LY, RX, RY, LT, RT, r2, new VendorBlobCodec.EncoderState());
        VendorBlobCodec.EncodeInput(spec, in full, LX, LY, RX, RY, LT, RT, r3, new VendorBlobCodec.EncoderState());
        Check("charging is 0xEE and charged 0xEF", r2[30] == 0xEE && r3[30] == 0xEF, $"{r2[30]:X2} {r3[30]:X2}");
        // Full scale: 4 g (32767 at 8192 per g) is 512 + 452, inside the
        // 10-bit range. 2048 degrees/second of yaw is far past it either way
        // and clamps.
        var big = TestState(); big.AccelX = 32767; big.GyroYaw = -32768;
        var r4 = new byte[49];
        VendorBlobCodec.EncodeInput(spec, in big, LX, LY, RX, RY, LT, RT, r4, new VendorBlobCodec.EncoderState());
        var neg = TestState(); neg.GyroYaw = 32767;
        var r4b = new byte[49];
        VendorBlobCodec.EncodeInput(spec, in neg, LX, LY, RX, RY, LT, RT, r4b, new VendorBlobCodec.EncoderState());
        Check("4 g encodes to 964, and the gyro clamps to 1023 and 0",
              Be16(r4, 41) == 964 && Be16(r4, 47) == 1023 && Be16(r4b, 47) == 0,
              $"{Be16(r4, 41)} {Be16(r4, 47)} {Be16(r4b, 47)}");

        // A consumer that only knows digital state still gets full presses,
        // and a released button with no pressure reads 0.
        var dig = new HMGamepadState { Buttons = HMButton.X | HMButton.RightBumper, Hat = HMHat.SouthWest };
        var r5 = new byte[49];
        VendorBlobCodec.EncodeInput(spec, in dig, 0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f, r5, new VendorBlobCodec.EncoderState());
        Check("digital-only presses read 255 (square, R1, down, left)",
              r5[25] == 255 && r5[21] == 255 && r5[16] == 255 && r5[17] == 255);
        Check("released buttons read 0 (cross, circle, triangle, L1, up, right)",
              r5[24] == 0 && r5[23] == 0 && r5[22] == 0 && r5[20] == 0 && r5[14] == 0 && r5[15] == 0);
    }

    // ── DsHidMini's two derived views, rewritten from its source ────────

    /// <summary>DsHid.c DS3_RAW_TO_SIXAXIS_HID_INPUT_REPORT, without its
    /// optional dead zone.</summary>
    static byte[] SxsInput(byte[] raw)
    {
        var o = new byte[12];
        o[0] = (byte)(((raw[3] & 0xF0) >> 4) | ((raw[3] & 0x0F) << 4));
        o[1] = (byte)(((raw[2] & 0x01) << 1) | ((raw[2] & 0x08) >> 3) | ((raw[2] & 0x02) << 1)
                    | ((raw[2] & 0x04) << 1) | ((raw[4] & 0x01) << 4));
        o[3] = (raw[2] & 0xF0) switch
        {
            0x10 => 0, 0x30 => 1, 0x20 => 2, 0x60 => 3, 0x40 => 4, 0xC0 => 5, 0x80 => 6, 0x90 => 7, _ => 8,
        };
        o[4] = raw[6]; o[5] = raw[7]; o[6] = raw[8]; o[7] = raw[9];
        o[8] = (byte)(0xFF - raw[23]);
        o[9] = (byte)(0xFF - raw[24]);
        o[10] = (byte)(0xFF - raw[18]);
        o[11] = (byte)(0xFF - raw[19]);
        return o;
    }

    /// <summary>HID.FeatureReport.c plus DsHidMiniDrv.c: bytes 0 and 1 set
    /// to 0x00 and 0x3F, motion little-endian, X mirrored as 0x3FF - x.</summary>
    static byte[] SxsFeature(byte[] raw)
    {
        var f = (byte[])raw.Clone();
        f[0] = 0x00; f[1] = 0x3F;
        void Le(int at, int v) { f[at] = (byte)(v & 0xFF); f[at + 1] = (byte)((v >> 8) & 0xFF); }
        Le(41, 0x3FF - Be16(raw, 41));
        Le(43, Be16(raw, 43));
        Le(45, Be16(raw, 45));
        Le(47, Be16(raw, 47));
        return f;
    }

    // ── B. Live, raw HID ────────────────────────────────────────────────

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3;
    const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    const uint IOCTL_HID_GET_FEATURE = 0x000B0192;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen,
                                       out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle h, byte[] buf, int len, out int written, IntPtr overlapped);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")]
    static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr pp, byte[] caps);

    static SafeFileHandle Open(string path, bool overlapped = false)
        => CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING,
                       overlapped ? FILE_FLAG_OVERLAPPED : 0, IntPtr.Zero);

    /// <summary>hidapi's hid_get_feature_report: IOCTL_HID_GET_FEATURE with
    /// the caller's buffer, report id in byte 0. SDL and RPCS3 both read
    /// through it.</summary>
    static byte[]? GetFeature(string path, byte id, int len)
    {
        using var h = Open(path);
        if (h.IsInvalid) return null;
        var buf = new byte[len];
        buf[0] = id;
        return DeviceIoControl(h, IOCTL_HID_GET_FEATURE, buf, len, buf, len, out _, IntPtr.Zero) ? buf : null;
    }

    static (int input, int output, int feature) Caps(string path)
    {
        using var h = Open(path);
        if (h.IsInvalid || !HidD_GetPreparsedData(h, out IntPtr pp)) return (-1, -1, -1);
        try
        {
            var caps = new byte[64];
            HidP_GetCaps(pp, caps);
            return (caps[4] | (caps[5] << 8), caps[6] | (caps[7] << 8), caps[8] | (caps[9] << 8));
        }
        finally { HidD_FreePreparsedData(pp); }
    }

    static bool Write(string path, byte[] report49)
    {
        using var h = Open(path);
        return !h.IsInvalid && WriteFile(h, report49, report49.Length, out int n, IntPtr.Zero) && n == report49.Length;
    }

    static HashSet<string> Present()
        => new(HidDeviceEnumerator.Enumerate().Where(d => d.VendorId == Vid && d.ProductId == Pid)
                   .Select(d => d.DevicePath), StringComparer.OrdinalIgnoreCase);

    static HidDeviceEnumerator.RawHidDevice? WaitForNew(HashSet<string> before, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var d = HidDeviceEnumerator.Enumerate()
                .FirstOrDefault(x => x.VendorId == Vid && x.ProductId == Pid && !before.Contains(x.DevicePath));
            if (d != null) return d;
            Thread.Sleep(100);
        }
        return null;
    }

    /// <summary>The decoded output reports the SDK raised, newest last.</summary>
    sealed class OutputLog
    {
        readonly object _lock = new();
        readonly List<Dictionary<string, object>> _seen = new();
        public void Add(Dictionary<string, object> f) { lock (_lock) _seen.Add(f); }
        public Dictionary<string, object>? WaitFor(Func<Dictionary<string, object>, bool> match, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                lock (_lock)
                {
                    for (int i = _seen.Count - 1; i >= 0; i--)
                        if (match(_seen[i])) return _seen[i];
                }
                Thread.Sleep(20);
            }
            return null;
        }
        public void Clear() { lock (_lock) _seen.Clear(); }
    }

    static int F(Dictionary<string, object> f, string k) => f.TryGetValue(k, out var v) ? Convert.ToInt32(v) : -1;

    static void Drive(HMController c, HMGamepadState st, int frames = 6)
    {
        for (int i = 0; i < frames; i++) { c.SubmitState(st); Thread.Sleep(40); }
    }

    static HMGamepadState LiveState(HMProfile prof)
    {
        var st = TestState();
        st.Axes = HMGamepadStateHelpers.StandardAxes(prof, LX, LY, RX, RY, LT, RT);
        return st;
    }

    static void PartB(HMContext ctx)
    {
        Console.WriteLine();
        Console.WriteLine("--- B. The live persona off the HID stack ---");
        var prof = ctx.GetProfile(ProfileId)!;
        var before = Present();
        HMController? c = null;
        try
        {
            c = ctx.CreateController(prof);
            var log = new OutputLog();
            c.OutputDecoded += (s, e) => log.Add(new Dictionary<string, object>(e.Fields));
            var dev = WaitForNew(before, 15000);
            Check("the persona's HID interface appears", dev != null, dev?.DevicePath ?? "");
            if (dev == null) return;
            string path = dev.DevicePath;

            var (inLen, outLen, featLen) = Caps(path);
            Check("the HID class sees input 13, output 49, feature 50 (each with the report id 0)",
                  inLen == 13 && outLen == 49 && featLen == 50, $"{inLen}/{outLen}/{featLen}");

            Drive(c, LiveState(prof));
            var raw = ExpectedNative();

            // The joystick report DirectInput and RawInput read.
            using (var h = Open(path))
            {
                var buf = new byte[13];
                bool got = !h.IsInvalid && ReadOneMatching(h, buf, c, LiveState(prof));
                var want = SxsInput(raw);
                Check("the input report is DsHidMini's joystick view of the native report",
                      got && buf[0] == 0x00 && buf.Skip(1).SequenceEqual(want),
                      $"{BitConverter.ToString(buf)} vs 00-{BitConverter.ToString(want)}");
            }

            // RPCS3's Windows sequence (ds3_pad_handler.cpp check_add_device,
            // get_data): a 255-byte read of 0xF2, and if 0xF2 is still in byte
            // 0 afterwards that id is the one it polls into 64 bytes, copying
            // the DS3 report from offset 1 (DS3_HID_OFFSET). The descriptor
            // declares no report ids, so the HID class serves every feature
            // read as report 0 and 0xF2 answers, as it does on DsHidMini. SDL's
            // sixaxis driver polls report 0 itself (UpdateDevice).
            var fF2 = GetFeature(path, 0xF2, 0xFF);
            Check("RPCS3's first read, 0xF2, answers with its 0xF2 still in byte 0",
                  fF2 != null && fF2[0] == 0xF2, fF2 == null ? "refused" : BitConverter.ToString(fF2, 0, 4));
            var poll = GetFeature(path, 0xF2, 64);
            var f64 = GetFeature(path, 0x00, 64);
            Check("report 0 answers SDL's 64-byte read with id 0", f64 != null && f64[0] == 0x00);
            if (poll != null && f64 != null)
            {
                var want = SxsFeature(raw);
                var got = f64.Skip(1).Take(49).ToArray();
                var bad = Enumerable.Range(0, 49).Where(k => got[k] != want[k]).Select(k => $"{k}:{got[k]:X2}/{want[k]:X2}").ToList();
                Check("its 49 data bytes are DsHidMini's feature view of the native report", bad.Count == 0,
                      string.Join(" ", bad));
                Check("RPCS3's 0xF2 poll carries the same 49 bytes",
                      poll[0] == 0xF2 && poll.Skip(1).Take(49).SequenceEqual(got));

                // RPCS3 ds3_input_report at buf[1]: buttons[0..2] at 2-4,
                // button_values at 14-25 gated by their digital bits,
                // battery_status at 30, accel_x/z/y and gyro little-endian.
                var rep = poll.Skip(1).Take(49).ToArray();
                int Gate(int bit, int value) => bit != 0 ? value : 0;
                Check("RPCS3 reads cross 255, triangle 200, L1 255, L2 128, up 255, right 50",
                      Gate(rep[3] & 0x40, rep[24]) == 255 && Gate(rep[3] & 0x10, rep[22]) == 200
                      && Gate(rep[3] & 0x04, rep[20]) == 255 && Gate(rep[3] & 0x01, rep[18]) == 128
                      && Gate(rep[2] & 0x10, rep[14]) == 255 && Gate(rep[2] & 0x20, rep[15]) == 50);
                Check("RPCS3 gates the released circle to 0 though its byte is 77",
                      Gate(rep[3] & 0x20, rep[23]) == 0 && rep[23] == 77);
                Check("RPCS3 reads start and PS, and the battery at 75%",
                      (rep[2] & 0x08) != 0 && (rep[4] & 0x01) != 0 && rep[30] == 4);
                Check("RPCS3 reads the PS3 sensors its DS4 formula gives (398/569/484/389)",
                      Le16(rep, 41) == 398 && Le16(rep, 45) == 569 && Le16(rep, 43) == 484 && Le16(rep, 47) == 389,
                      $"{Le16(rep, 41)} {Le16(rep, 45)} {Le16(rep, 43)} {Le16(rep, 47)}");
            }
            else
            {
                Check("both feature reads answer", false, $"0xF2 {(poll != null)}, 0 {(f64 != null)}");
            }

            // Output report 0. SDL's sixaxis.sys commands
            // (SDL_hidapi_ps3.c UpdateRumbleSonySixaxis, UpdateLEDsSonySixaxis)
            // and RPCS3's Windows report (ds3_pad_handler.h ds3_output_report).
            var motors = new byte[49];
            motors[1] = 2; motors[5] = 0xFF; motors[6] = 1; motors[7] = 0xFF; motors[8] = 200;
            log.Clear();
            Check("SDL's motor command writes", Write(path, motors));
            var m = log.WaitFor(f => F(f, "leftMotorForce") == 200, 2000);
            Check("the SDK decodes it as small motor on and large motor 200",
                  m != null && F(m, "rightMotorOn") == 1 && F(m, "leftMotorForce") == 200,
                  m == null ? "nothing decoded" : $"on {F(m, "rightMotorOn")}, force {F(m, "leftMotorForce")}");

            var leds = new byte[49];
            leds[1] = 1; leds[8] = 1; // LED 1 on
            log.Clear();
            Check("SDL's LED command writes", Write(path, leds));
            var l = log.WaitFor(f => F(f, "ledBitmap") == 0x02, 2000);
            Check("the SDK decodes LED 1 (0x02) and the motors keep their state",
                  l != null && F(l, "rightMotorOn") == 1 && F(l, "leftMotorForce") == 200,
                  l == null ? "nothing decoded" : $"leds 0x{F(l, "ledBitmap"):X2}, on {F(l, "rightMotorOn")}, force {F(l, "leftMotorForce")}");

            var rp = new byte[49];
            rp[1] = 0x02;                                   // idk_what_this_is
            rp[5] = 0xFF; rp[6] = 0; rp[7] = 0xFF; rp[8] = 90; // rumble block after its padding
            rp[13] = 0x04;                                  // led_enabled: LED 2
            log.Clear();
            Check("RPCS3's output report writes", Write(path, rp));
            var rr = log.WaitFor(f => F(f, "leftMotorForce") == 90, 2000);
            Check("the SDK decodes RPCS3's motors and LED 2",
                  rr != null && F(rr, "rightMotorOn") == 0 && F(rr, "ledBitmap") == 0x04,
                  rr == null ? "nothing decoded" : $"on {F(rr, "rightMotorOn")}, leds 0x{F(rr, "ledBitmap"):X2}");
        }
        catch (Exception ex) { Check("part B ran without throwing", false, ex.Message); }
        finally
        {
            c?.Dispose();
            Thread.Sleep(500);
        }
    }

    /// <summary>Reads input reports until one matches the state's joystick
    /// view, submitting while it waits so a parked read completes.</summary>
    static bool ReadOneMatching(SafeFileHandle h, byte[] buf, HMController c, HMGamepadState st)
    {
        var want = SxsInput(ExpectedNative());
        var done = new ManualResetEventSlim(false);
        var pump = new Thread(() =>
        {
            try { while (!done.IsSet) { c.SubmitState(st); done.Wait(30); } }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true };
        pump.Start();
        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000)
            {
                if (!ReadFile(h, buf, buf.Length, out int n, IntPtr.Zero) || n <= 0) return false;
                if (buf.Skip(1).SequenceEqual(want)) return true;
            }
            return false;
        }
        finally { done.Set(); pump.Join(2000); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, byte[] buf, int len, out int read, IntPtr overlapped);

    // ── C. Live, stock SDL3 with PCSX2's hint ───────────────────────────

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
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepadJoystick(IntPtr gp);
    [DllImport(SDL)] static extern int SDL_GetNumJoystickAxes(IntPtr js);
    [DllImport(SDL)] static extern int SDL_GetNumJoystickButtons(IntPtr js);
    [DllImport(SDL)] static extern short SDL_GetJoystickAxis(IntPtr js, int axis);
    [DllImport(SDL)] static extern short SDL_GetGamepadAxis(IntPtr gp, int axis);
    [DllImport(SDL)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_GetGamepadButton(IntPtr gp, int button);
    [DllImport(SDL)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_RumbleGamepad(IntPtr gp, ushort low, ushort high, uint ms);
    [DllImport(SDL)] static extern void SDL_free(IntPtr mem);
    [DllImport(SDL)] static extern IntPtr SDL_GetError();

    const uint SDL_INIT_GAMEPAD = 0x00002000u;
    const int SDL_GAMEPAD_TYPE_PS3 = 4;
    const int BTN_SOUTH = 0, BTN_EAST = 1, BTN_WEST = 2, BTN_NORTH = 3, BTN_GUIDE = 5, BTN_START = 6,
              BTN_LEFT_SHOULDER = 9, BTN_DPAD_UP = 11, BTN_DPAD_DOWN = 12, BTN_DPAD_RIGHT = 14;
    const int AXIS_LEFTX = 0, AXIS_LEFTY = 1, AXIS_LEFT_TRIGGER = 4;

    static string Err() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "";

    /// <summary>A STOCK SDL3.dll: the sibling SDL3-build/build-stock/Release
    /// build of upstream libsdl-org/SDL. The fork beside it skips HIDMaestro
    /// devices and would make every check below vacuous.</summary>
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

    static IntPtr WaitForGamepad(string path, int seconds)
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
                        if (SDL_GetGamepadVendorForID(id) != Vid || SDL_GetGamepadProductForID(id) != Pid) continue;
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

    static void DriveSdl(HMController c, HMGamepadState st)
    {
        for (int i = 0; i < 8; i++) { c.SubmitState(st); Thread.Sleep(50); }
        SDL_UpdateGamepads();
        Thread.Sleep(80);
        SDL_UpdateGamepads();
    }

    static bool PartC(HMContext ctx)
    {
        Console.WriteLine();
        Console.WriteLine("--- C. The live persona through stock SDL3 with PCSX2's hint ---");
        string? dll = FindStockSdl();
        if (dll == null)
        {
            Console.WriteLine("  [SKIP] no stock SDL3.dll; this part needs the sibling");
            Console.WriteLine("         SDL3-build/build-stock/Release build of upstream libsdl-org/SDL.");
            return false;
        }
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly,
            (name, asm, p) => name == SDL ? NativeLibrary.Load(dll) : IntPtr.Zero);
        Console.WriteLine($"  SDL3.dll: {dll}");

        // PCSX2's SDLInputSource::SetHints on Windows sets the sixaxis-driver
        // hint and leaves HIDAPI on. RawInput off so SDL's HIDAPI layer is the
        // one that claims the device.
        SDL_SetHint("SDL_JOYSTICK_HIDAPI", "1");
        SDL_SetHint("SDL_JOYSTICK_HIDAPI_PS3_SIXAXIS_DRIVER", "1");
        SDL_SetHint("SDL_JOYSTICK_RAWINPUT", "0");
        SDL_SetHint("SDL_JOYSTICK_THREAD", "1");
        if (!SDL_Init(SDL_INIT_GAMEPAD))
        {
            Check("SDL_Init", false, Err());
            return true;
        }

        var prof = ctx.GetProfile(ProfileId)!;
        var before = Present();
        HMController? c = null;
        IntPtr gp = IntPtr.Zero;
        try
        {
            c = ctx.CreateController(prof);
            var log = new OutputLog();
            c.OutputDecoded += (s, e) => log.Add(new Dictionary<string, object>(e.Fields));
            var dev = WaitForNew(before, 15000);
            Check("the persona's HID interface appears", dev != null);
            if (dev == null) return true;
            // SDL reads state through the feature report, so give it a frame
            // before it opens the device.
            Drive(c, LiveState(prof), 3);
            gp = WaitForGamepad(dev.DevicePath, 20);
            Check("SDL's PS3 sixaxis driver opens the persona as a gamepad", gp != IntPtr.Zero, gp == IntPtr.Zero ? Err() : "");
            if (gp == IntPtr.Zero) return true;
            Console.WriteLine($"     SDL name: {Marshal.PtrToStringUTF8(SDL_GetGamepadName(gp))}");
            IntPtr js = SDL_GetGamepadJoystick(gp);
            int axes = SDL_GetNumJoystickAxes(js), buttons = SDL_GetNumJoystickButtons(js);
            Check("SDL types it as a PS3 controller", SDL_GetGamepadType(gp) == SDL_GAMEPAD_TYPE_PS3, $"type {SDL_GetGamepadType(gp)}");
            Check("16 axes and 11 buttons, PCSX2's IsControllerSixaxis test", axes == 16 && buttons == 11,
                  $"{axes} axes, {buttons} buttons");

            DriveSdl(c, LiveState(prof));
            Check("SDL reads cross, triangle, L1, start and PS pressed",
                  SDL_GetGamepadButton(gp, BTN_SOUTH) && SDL_GetGamepadButton(gp, BTN_NORTH)
                  && SDL_GetGamepadButton(gp, BTN_LEFT_SHOULDER) && SDL_GetGamepadButton(gp, BTN_START)
                  && SDL_GetGamepadButton(gp, BTN_GUIDE));
            Check("SDL reads circle and square released",
                  !SDL_GetGamepadButton(gp, BTN_EAST) && !SDL_GetGamepadButton(gp, BTN_WEST));
            Check("SDL reads the d-pad up and right, not down",
                  SDL_GetGamepadButton(gp, BTN_DPAD_UP) && SDL_GetGamepadButton(gp, BTN_DPAD_RIGHT)
                  && !SDL_GetGamepadButton(gp, BTN_DPAD_DOWN));
            short lx = SDL_GetGamepadAxis(gp, AXIS_LEFTX), ly = SDL_GetGamepadAxis(gp, AXIS_LEFTY);
            Check("SDL reads the left stick at 64 and 191", lx == 64 * 257 - 32768 && ly == 191 * 257 - 32768, $"{lx} {ly}");
            short lt = SDL_GetGamepadAxis(gp, AXIS_LEFT_TRIGGER);
            Check("SDL reads L2 half pulled", Math.Abs(lt - (128 * 257 - 32768 + 32768) / 2) <= 2, $"{lt}");

            // Pressure axes in PCSX2's order (s_sdl_ps3_sxs_pressure_names).
            string[] names = { "Cross", "Circle", "Square", "Triangle", "L1", "R1", "Up", "Down", "Left", "Right" };
            int[] want = { 255, 77, 0, 200, 255, 0, 255, 0, 0, 50 };
            var seen = new List<string>();
            bool all = true;
            for (int k = 0; k < 10; k++)
            {
                short v = SDL_GetJoystickAxis(js, 6 + k);
                all &= v == want[k] * 257 - 32768;
                seen.Add($"{names[k]} {v}");
            }
            Check("SDL reads all ten pressure axes as PCSX2 binds them", all, string.Join(", ", seen));

            var leds = log.WaitFor(f => F(f, "ledBitmap") == 0x02, 2000);
            Check("SDL's player LED command reaches the SDK as LED 1", leds != null);
            log.Clear();
            Check("SDL accepts a rumble request", SDL_RumbleGamepad(gp, 0xC800, 0xFFFF, 1000), Err());
            var m = log.WaitFor(f => F(f, "leftMotorForce") == 200, 2000);
            Check("the SDK decodes SDL's rumble as small motor on and large motor 200",
                  m != null && F(m, "rightMotorOn") == 1,
                  m == null ? "nothing decoded" : $"on {F(m, "rightMotorOn")}");
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

    // ── D. The native-descriptor DualShock 3 (issue #65) ────────────────

    /// <summary>The native output report 0x01 as hid-sony.c's
    /// sixaxis_send_output_report builds it (its default_report, then
    /// rumble.right_motor_on, rumble.left_motor_force and leds_bitmap), padded
    /// to the 49 bytes a Windows host writes. SDL_hidapi_ps3.c's
    /// HIDAPI_DriverPS3_UpdateEffects writes the same bytes behind report id
    /// 0x01 (k_EPS3ReportIdEffects).</summary>
    static byte[] NativeOutput(byte smallOn, byte largeForce, byte leds)
    {
        var r = new byte[49];
        byte[] head =
        {
            0x01,
            0x01, 0xff, 0x00, 0xff, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00,
            0xff, 0x27, 0x10, 0x00, 0x32,
            0xff, 0x27, 0x10, 0x00, 0x32,
            0xff, 0x27, 0x10, 0x00, 0x32,
            0xff, 0x27, 0x10, 0x00, 0x32,
            0x00, 0x00, 0x00, 0x00, 0x00,
        };
        Array.Copy(head, r, head.Length);
        r[3] = smallOn;     // rumble.right_motor_on
        r[5] = largeForce;  // rumble.left_motor_force
        r[10] = leds;       // leds_bitmap, LED 1 = 0x02
        return r;
    }

    static bool SetOutput(string path, byte[] report49)
    {
        using var h = Open(path);
        return !h.IsInvalid && HidD_SetOutputReport(h, report49, report49.Length);
    }

    static void PartD(HMContext ctx)
    {
        Console.WriteLine();
        Console.WriteLine("--- D. The native-descriptor dualshock-3 decodes its output report (issue #65) ---");
        var prof = ctx.GetProfile("dualshock-3")!;
        var spec = prof.ExtendedOutputReport;
        Check("dualshock-3 declares an output decode for report 0x01, 49 bytes",
              spec != null && spec.ReportIdByte == 0x01 && spec.Size == 49,
              spec == null ? "none" : $"0x{spec.ReportIdByte:X2}/{spec.Size}");

        var before = Present();
        HMController? c = null;
        try
        {
            c = ctx.CreateController(prof);
            var decoded = new OutputLog();
            var raws = new List<byte[]>();
            var wholes = new List<byte[]>();
            c.OutputDecoded += (s, e) =>
            {
                lock (wholes) wholes.Add(e.RawBytes.ToArray());
                decoded.Add(new Dictionary<string, object>(e.Fields));
            };
            // The positive control: the raw packet, in the same window as
            // the decode it should produce.
            c.OutputReceived += (s, pkt) =>
            {
                if (pkt.ReportId != 0x01) return;
                var whole = new byte[pkt.Data.Length + 1];
                whole[0] = pkt.ReportId;
                pkt.Data.Span.CopyTo(whole.AsSpan(1));
                lock (raws) raws.Add(whole);
            };
            var dev = WaitForNew(before, 15000);
            Check("the persona's HID interface appears", dev != null, dev?.DevicePath ?? "");
            if (dev == null) return;
            string path = dev.DevicePath;

            var (inLen, outLen, featLen) = Caps(path);
            Check("the HID class sees input, output and feature at 49 bytes with the report id",
                  inLen == 49 && outLen == 49 && featLen == 49, $"{inLen}/{outLen}/{featLen}");

            bool SawRaw(byte[] want)
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 2000)
                {
                    lock (raws) if (raws.Any(r => r.SequenceEqual(want))) return true;
                    Thread.Sleep(20);
                }
                return false;
            }
            bool SawWhole(byte[] want)
            {
                lock (wholes) return wholes.Any(r => r.SequenceEqual(want));
            }

            // SDL's player 1 rumble: the small motor on, the large at 0x80.
            var w1 = NativeOutput(1, 0x80, 0x02);
            Check("WriteFile delivers the native report", Write(path, w1));
            Check("OutputReceived carries it whole as report 0x01", SawRaw(w1));
            var d1 = decoded.WaitFor(f => F(f, "leftMotorForce") == 0x80, 2000);
            Check("OutputDecoded reads the small motor on and the large motor at 0x80",
                  d1 != null && F(d1, "rightMotorOn") == 1,
                  d1 == null ? "nothing decoded" : $"on {F(d1, "rightMotorOn")}, force {F(d1, "leftMotorForce")}");
            Check("and both durations at 0xFF and LED 1",
                  d1 != null && F(d1, "rightMotorDuration") == 0xFF && F(d1, "leftMotorDuration") == 0xFF
                  && F(d1, "ledBitmap") == 0x02,
                  d1 == null ? "" : $"{F(d1, "rightMotorDuration"):X2} {F(d1, "leftMotorDuration"):X2} leds 0x{F(d1, "ledBitmap"):X2}");
            Check("its raw bytes are the 49 the host wrote, the length PadForge requires", SawWhole(w1));

            var w2 = NativeOutput(0, 0x10, 0x04);
            Check("HidD_SetOutputReport delivers the native report", SetOutput(path, w2));
            Check("OutputReceived carries that one whole as well", SawRaw(w2));
            var d2 = decoded.WaitFor(f => F(f, "leftMotorForce") == 0x10, 2000);
            Check("OutputDecoded reads the small motor off, the large at 0x10 and LED 2",
                  d2 != null && F(d2, "rightMotorOn") == 0 && F(d2, "ledBitmap") == 0x04,
                  d2 == null ? "nothing decoded" : $"on {F(d2, "rightMotorOn")}, leds 0x{F(d2, "ledBitmap"):X2}");

            // A stop is a frame with both motors off.
            decoded.Clear();
            var w3 = NativeOutput(0, 0x00, 0x02);
            Check("a stop writes", Write(path, w3));
            var d3 = decoded.WaitFor(f => F(f, "ledBitmap") == 0x02, 2000);
            Check("OutputDecoded reads both motors off",
                  d3 != null && F(d3, "rightMotorOn") == 0 && F(d3, "leftMotorForce") == 0,
                  d3 == null ? "nothing decoded" : $"on {F(d3, "rightMotorOn")}, force {F(d3, "leftMotorForce")}");
        }
        catch (Exception ex) { Check("part D ran without throwing", false, ex.Message); }
        finally
        {
            c?.Dispose();
            Thread.Sleep(500);
        }
    }

    static int Main()
    {
        Console.WriteLine("=== DualShock 3 with pressure-sensitive buttons (PadForge discussion 476) ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        PartA(ctx);

        bool elevated;
        using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
            elevated = new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        bool sdlRan = false;
        if (!elevated)
        {
            Console.WriteLine();
            Console.WriteLine("  [SKIP] parts B, C and D create a controller and need elevation");
        }
        else
        {
            ctx.InstallDriver();
            PartB(ctx);
            sdlRan = PartC(ctx);
            PartD(ctx);
            try { HMContext.RemoveAllVirtualControllers(); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        if (s_failures > 0) return 1;
        return elevated && sdlRan ? 0 : 2;
    }
}
