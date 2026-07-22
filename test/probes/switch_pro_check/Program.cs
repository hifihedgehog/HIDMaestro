// Switch Pro protocol responder check (issue #33).
//
// Acceptance surrogate for SDL3's HIDAPI_DriverSwitch: opens the virtual
// pad over raw HID and performs the SAME init sequence SDL performs
// (BTrySetupUSB + BReadDeviceInfo + LoadStickCalibration +
// LoadIMUCalibration + SetVibrationEnabled + SetInputMode), then
// validates streaming, input round-trip, IMU bytes, and rumble decode.
// Every wire byte asserted here is quoted from SDL_hidapi_switch.c (the
// client), nxbt protocol.py (the device-side reference), or the
// dekuNukem notes; file:line cites sit next to each phase.
//
// Runs fully headless: no Steam, no SDL, no physical hardware. Creates
// the controller, drives it, asserts, cleans up. Exit 0 PASS / 1 FAIL.
// Requires elevation (CreateController).

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using HIDMaestro;
using HIDMaestro.Internal;

using Microsoft.Win32.SafeHandles;

internal static class Program
{
    static int s_total = 0;
    static int s_failures = 0;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    // ── raw HID I/O (overlapped, timeout-capable) ──────────────────────

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, byte[] buf, uint n, IntPtr read, ref NativeOverlapped ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle h, byte[] buf, uint n, IntPtr written, ref NativeOverlapped ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetOverlappedResultEx(SafeFileHandle h, ref NativeOverlapped ov,
        out uint transferred, uint timeoutMs, bool alertable);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateEventW(IntPtr attrs, bool manual, bool initial, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

    static SafeFileHandle s_hid = null!;
    static IntPtr s_readEvent, s_writeEvent;

    static bool HidWrite(byte[] report64)
    {
        var ov = new NativeOverlapped { EventHandle = s_writeEvent };
        if (!WriteFile(s_hid, report64, 64, IntPtr.Zero, ref ov)
            && Marshal.GetLastWin32Error() != 997 /* ERROR_IO_PENDING */)
            return false;
        if (!GetOverlappedResultEx(s_hid, ref ov, out uint written, 1000, false))
        {
            CancelIoEx(s_hid, IntPtr.Zero);
            return false;
        }
        return written == 64;
    }

    static byte[]? HidRead(int timeoutMs)
    {
        var buf = new byte[64];
        var ov = new NativeOverlapped { EventHandle = s_readEvent };
        if (!ReadFile(s_hid, buf, 64, IntPtr.Zero, ref ov)
            && Marshal.GetLastWin32Error() != 997)
            return null;
        if (!GetOverlappedResultEx(s_hid, ref ov, out uint read, (uint)timeoutMs, false))
        {
            CancelIoEx(s_hid, IntPtr.Zero);
            return null;
        }
        return read > 0 ? buf : null;
    }

    /// <summary>Read until a report with the wanted ID (and optional second
    /// byte) arrives. Mirrors SDL's ReadProprietaryReply / ReadSubcommandReply
    /// skip-others loop (SDL_hidapi_switch.c:437-460 / :470-482).</summary>
    static byte[]? ReadUntil(byte reportId, int timeoutMs, Func<byte[], bool>? accept = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var r = HidRead((int)Math.Max(1, timeoutMs - sw.ElapsedMilliseconds));
            if (r == null) continue;
            if (r[0] != reportId) continue;
            if (accept != null && !accept(r)) continue;
            return r;
        }
        return null;
    }

    // SDL ConstructSubcommand (SDL_hidapi_switch.c:486-501): packet type
    // 0x01, counter, neutral rumble 00 01 40 40 per side, subcommand, args.
    static int s_counter;

    static byte[]? Subcommand(byte id, byte[] args, int timeoutMs = 500)
    {
        var pkt = new byte[64];
        pkt[0] = 0x01;
        pkt[1] = (byte)(s_counter++ & 0xF);
        pkt[2] = 0x00; pkt[3] = 0x01; pkt[4] = 0x40; pkt[5] = 0x40;
        pkt[6] = 0x00; pkt[7] = 0x01; pkt[8] = 0x40; pkt[9] = 0x40;
        pkt[10] = id;
        Array.Copy(args, 0, pkt, 11, args.Length);
        if (!HidWrite(pkt)) return null;
        // SDL validates ack & 0x80 and the echoed subcommand id
        // (SDL_hidapi_switch.c:441).
        return ReadUntil(0x21, timeoutMs, r => r[14] == id && (r[13] & 0x80) != 0);
    }

    static int Main()
    {
        Console.WriteLine("=== Switch Pro protocol responder check (issue #33) ===");

        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("  [SKIP] all phases (requires elevation, same as CreateController)");
                return 0;
            }
        }

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        var profile = ctx.GetProfile("switch-pro")
            ?? throw new InvalidOperationException("switch-pro profile missing");
        Console.WriteLine("  Creating switch-pro virtual controller...");
        using var ctrl = ctx.CreateController(profile);

        // Every 0x01 subcommand carries (neutral) rumble bytes, so
        // OutputDecoded fires throughout phase 2 with 0/0. Phase 5 keys
        // on the 0x10-report decode specifically.
        byte decodedLeft = 0, decodedRight = 0;
        int decoded10Count = 0;
        ctrl.OutputDecoded += (_, e) =>
        {
            if (e.ReportId != 0x10) return;
            if (e.Fields.TryGetValue("leftMotor", out var l)) decodedLeft = (byte)l;
            if (e.Fields.TryGetValue("rightMotor", out var r)) decodedRight = (byte)r;
            Interlocked.Increment(ref decoded10Count);
        };

        // Neutral pump keeps the shared body populated while we talk.
        var pumpStop = false;
        var pump = new Thread(() =>
        {
            var st = new HMGamepadState();
            while (!Volatile.Read(ref pumpStop))
            {
                ctrl.SubmitState(st);
                Thread.Sleep(8);
            }
        })
        { IsBackground = true };
        pump.Start();

        // Locate the HID interface by VID/PID (poll: enumeration follows
        // device creation by a few hundred ms).
        string? path = null;
        for (int i = 0; i < 50 && path == null; i++)
        {
            path = HidDeviceEnumerator.Enumerate()
                .FirstOrDefault(d => d.VendorId == 0x057E && d.ProductId == 0x2009)?.DevicePath;
            if (path == null) Thread.Sleep(100);
        }
        Check("HID interface enumerates (VID 057E PID 2009)", path != null, path ?? "");
        if (path == null) return Fail(ref pumpStop, pump);

        s_readEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        s_writeEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        s_hid = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        Check("CreateFile read/write", !s_hid.IsInvalid);
        if (s_hid.IsInvalid) return Fail(ref pumpStop, pump);

        // ── Phase 1: USB init, the BTrySetupUSB sequence ────────────────
        Console.WriteLine("\n-- Phase 1: 0x80 USB init (SDL BTrySetupUSB :724-746) --");

        var cmd = new byte[64]; cmd[0] = 0x80; cmd[1] = 0x02;
        Check("80 02 handshake write", HidWrite(cmd));
        Check("81 02 handshake ack", ReadUntil(0x81, 500, r => r[1] == 0x02) != null);

        cmd[1] = 0x03;
        Check("80 03 high-speed write", HidWrite(cmd));
        Check("81 03 ack", ReadUntil(0x81, 500, r => r[1] == 0x03) != null);

        cmd[1] = 0x01;
        Check("80 01 status write", HidWrite(cmd));
        var status = ReadUntil(0x81, 500, r => r[1] == 0x01);
        Check("81 01 status reply", status != null);
        // SwitchProprietaryStatusPacket_t: type at byte 3 (0x03 = Pro),
        // MAC at 4..9 LSB-first (dekuNukem USB-HID-Notes 80 01 sample).
        Check("status device type = Pro (0x03)", status != null && status[3] == 0x03);
        Check("status MAC nonzero", status != null && status.Skip(4).Take(6).Any(b => b != 0));

        cmd[1] = 0x04;
        Check("80 04 ForceUSB write (no reply expected)", HidWrite(cmd));

        // ── Phase 2: subcommands (SDL init order) ───────────────────────
        Console.WriteLine("\n-- Phase 2: subcommand request-reply --");

        var info = Subcommand(0x02, Array.Empty<byte>());
        Check("0x02 device info reply", info != null);
        Check("device info ACK 0x82 (nxbt :300)", info != null && info[13] == 0x82);
        Check("device info type Pro (payload[2]=0x03)", info != null && info[17] == 0x03);
        Check("device info firmware 03.8B (nxbt :306-307)", info != null && info[15] == 0x03 && info[16] == 0x8B);

        // User stick calibration read: must succeed with 0xFF bytes so
        // SDL's magic check (0xB2 0xA1, LoadStickCalibration :1766) fails
        // over to factory data.
        var user = Subcommand(0x10, new byte[] { 0x10, 0x80, 0x00, 0x00, 22 });
        Check("SPI 0x8010 user cal reply (ACK 0x90)", user != null && user[13] == 0x90);
        Check("user cal magic absent (0xFF fill)", user != null && user[20] == 0xFF && user[21] == 0xFF);

        var fact = Subcommand(0x10, new byte[] { 0x3D, 0x60, 0x00, 0x00, 18 });
        Check("SPI 0x603D factory cal reply", fact != null && fact[13] == 0x90);
        // SDL memcmp's the echoed opData (ReadSubcommandReply :444-449).
        Check("factory cal address echo", fact != null
            && fact[15] == 0x3D && fact[16] == 0x60 && fact[17] == 0x00 && fact[18] == 0x00 && fact[19] == 18);
        // pack12(0x600,0x600)=00 06 60, pack12(0x800,0x800)=00 08 80:
        // left = max/center/min (SDL :1809), decodes to center 0x800
        // range 0x600 on every axis.
        Check("left cal bytes (max/center/min)", fact != null
            && fact[20] == 0x00 && fact[21] == 0x06 && fact[22] == 0x60
            && fact[23] == 0x00 && fact[24] == 0x08 && fact[25] == 0x80
            && fact[26] == 0x00 && fact[27] == 0x06 && fact[28] == 0x60);

        var imuCal = Subcommand(0x10, new byte[] { 0x20, 0x60, 0x00, 0x00, 24 });
        Check("SPI 0x6020 IMU cal reply", imuCal != null && imuCal[13] == 0x90);
        // Zero origins + coeffs 0x4000/0x343B reduce SDL's math to its
        // own default scales (LoadIMUCalibration :1920-1927 vs :1931-1932).
        Check("IMU accel coeff 0x4000", imuCal != null && imuCal[26] == 0x00 && imuCal[27] == 0x40);
        Check("IMU gyro coeff 0x343B", imuCal != null && imuCal[38] == 0x3B && imuCal[39] == 0x34);

        // Issue #36: the analog-stick parameter block at 0x6086 must serve
        // a ZERO dead zone. Chromium's Nintendo driver (nintendo_controller
        // .cc UnpackSwitchAnalogStickParameters) unpacks dead_zone and
        // range_ratio from data bytes 3-5 of this block and radially snaps
        // both axes to center inside the dead zone; nxbt's captured
        // hardware bytes carried 0x096 (150 counts, ~10% of the 1536-count
        // range). Guard so a future parameter-block refresh from real
        // captures cannot silently reintroduce the browser dead band.
        var stickParams = Subcommand(0x10, new byte[] { 0x86, 0x60, 0x00, 0x00, 18 });
        Check("SPI 0x6086 stick parameters reply", stickParams != null && stickParams[13] == 0x90);
        int deadZone = stickParams != null ? stickParams[23] | ((stickParams[24] & 0x0F) << 8) : -1;
        Check("stick dead zone is ZERO (issue #36, Chromium applies none)",
              deadZone == 0, $"0x{deadZone:X3}");
        int rangeRatio = stickParams != null ? (stickParams[24] >> 4) | (stickParams[25] << 4) : -1;
        Check("range ratio 0xF33 preserved beside the zeroed nibble",
              rangeRatio == 0xF33, $"0x{rangeRatio:X3}");

        Check("0x48 enable vibration ACK", Subcommand(0x48, new byte[] { 0x01 }) != null);
        Check("0x40 enable IMU ACK", Subcommand(0x40, new byte[] { 0x01 }) != null);
        Check("0x30 player lights ACK", Subcommand(0x30, new byte[] { 0x01 }) != null);
        Check("0x50 unknown subcommand gets generic ACK (no NACK loop)",
            Subcommand(0x50, Array.Empty<byte>()) != null);
        Check("0x03 set input mode 0x30 ACK", Subcommand(0x03, new byte[] { 0x30 }) != null);

        // ── Phase 3: 0x30 streaming ─────────────────────────────────────
        Console.WriteLine("\n-- Phase 3: 0x30 streaming at ~60 Hz --");

        // Drain whatever is queued, then time 30 fresh frames.
        while (HidRead(30) != null) { }
        int frames = 0; byte firstTimer = 0, lastTimer = 0;
        var cadence = Stopwatch.StartNew();
        for (int i = 0; i < 30; i++)
        {
            var r = ReadUntil(0x30, 500);
            if (r == null) break;
            if (frames == 0) firstTimer = r[1];
            lastTimer = r[1];
            frames++;
        }
        cadence.Stop();
        Check("30 consecutive 0x30 frames", frames == 30, $"got {frames}");
        double msPerFrame = cadence.Elapsed.TotalMilliseconds / Math.Max(1, frames);
        Check("cadence ~60 Hz (10..25 ms/frame)", msPerFrame >= 10 && msPerFrame <= 25,
            $"{msPerFrame:F1} ms/frame");
        Check("timer byte advances", frames >= 2 && ((byte)(lastTimer - firstTimer)) >= (byte)(frames - 1));

        // ── Phase 4: input round-trip ───────────────────────────────────
        Console.WriteLine("\n-- Phase 4: input round-trip (SubmitState -> wire bytes) --");

        Volatile.Write(ref pumpStop, true);
        pump.Join(1000);

        // Switch A = layout buttonIndex 1 -> wire byte0 bit3
        // (SDL_hidapi_switch.c:3412 EAST = 0x08); dpad Up -> byte2 bit1
        // (:3437); left stick full right+up; right trigger digital ZR =
        // index 7 -> byte0 bit7 (:3457).
        var pressed = new HMGamepadState
        {
            Buttons = (HMButton)((1u << 1) | (1u << 7)),
            HatDegrees = 0f,
            Axes = new System.Collections.Generic.Dictionary<HMAxis, float>
            {
                [HMAxis.X] = 1.0f,   // left stick X full right
                [HMAxis.Y] = 0.0f,   // left stick Y full up (HID: 0 = up)
            },
            AccelGY = 1.0f,          // gravity at rest (SDL frame: +Y up)
            GyroDpsY = 100.0f,       // yaw-left (SDL frame)
        };
        ctrl.SubmitState(pressed);
        Thread.Sleep(50);

        var f = ReadUntil(0x30, 500, r => (r[3] & 0x08) != 0);
        Check("A press reaches wire (byte3 bit3)", f != null);
        if (f != null)
        {
            Check("ZR press (byte3 bit7)", (f[3] & 0x80) != 0);
            Check("dpad Up (byte5 bit1)", (f[5] & 0x02) != 0);
            int lx = f[6] | ((f[7] & 0x0F) << 8);
            int ly = (f[7] >> 4) | (f[8] << 4);
            Check("left stick X full right (~0xE00)", Math.Abs(lx - 0xE00) <= 8, $"0x{lx:X3}");
            Check("left stick Y full up (~0xE00, wire up-positive)", Math.Abs(ly - 0xE00) <= 8, $"0x{ly:X3}");
            // IMU frame 0 at bytes 13..24. Packer inverse of SDL's
            // wire->SDL map (sdl = (-Y, +Z, -X)): AccelGY=1 (SDL +Y up)
            // lands on WIRE Z = +4096; GyroDpsY=100 (SDL yaw) lands on
            // WIRE Z = +round(100 * 13371/936) = 1429; wire X/Y stay 0.
            short wax = (short)(f[13] | (f[14] << 8));
            short way = (short)(f[15] | (f[16] << 8));
            short waz = (short)(f[17] | (f[18] << 8));
            short wgz = (short)(f[23] | (f[24] << 8));
            Check("accel: SDL +Y maps to wire Z = 4096", waz == 4096, $"raw {waz}");
            Check("accel: wire X/Y stay zero", wax == 0 && way == 0, $"x={wax} y={way}");
            Check("gyro: SDL yaw maps to wire Z = 1429", Math.Abs(wgz - 1429) <= 1, $"raw {wgz}");
        }

        // ── Phase 5: rumble decode ──────────────────────────────────────
        Console.WriteLine("\n-- Phase 5: rumble -> OutputDecoded leftMotor/rightMotor --");

        // Output 0x10: [id, counter, rumbleL 4B, rumbleR 4B]. Left at max
        // HF amplitude (0xC8, EncodeRumbleHighAmplitude table tail), right
        // neutral (00 01 40 40).
        var rumble = new byte[64];
        rumble[0] = 0x10; rumble[1] = 0x01;
        rumble[2] = 0x00; rumble[3] = 0xC8; rumble[4] = 0x60; rumble[5] = 0x00;
        rumble[6] = 0x00; rumble[7] = 0x01; rumble[8] = 0x40; rumble[9] = 0x40;
        Check("0x10 rumble write", HidWrite(rumble));
        var decodeSw = Stopwatch.StartNew();
        while (decoded10Count == 0 && decodeSw.ElapsedMilliseconds < 1000) Thread.Sleep(10);
        Check("OutputDecoded fired for report 0x10", decoded10Count > 0);
        Check("leftMotor = 255 (HF amp 0xC8)", decodedLeft == 255, $"got {decodedLeft}");
        Check("rightMotor = 0 (neutral)", decodedRight == 0, $"got {decodedRight}");

        s_hid.Dispose();
        if (s_readEvent != IntPtr.Zero) CloseHandle(s_readEvent);
        if (s_writeEvent != IntPtr.Zero) CloseHandle(s_writeEvent);

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static int Fail(ref bool pumpStop, Thread pump)
    {
        Volatile.Write(ref pumpStop, true);
        pump.Join(500);
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return 1;
    }
}
