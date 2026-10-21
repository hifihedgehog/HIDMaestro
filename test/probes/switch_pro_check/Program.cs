// Switch Pro protocol responder check (issues #33/#36/#37).
//
// Acceptance surrogate for SDL3's HIDAPI_DriverSwitch under the real
// BLUETOOTH descriptor (issue #37): opens the virtual pad over raw HID
// and performs the BT init sequence SDL performs after BTrySetupUSB
// fails (BReadDeviceInfo + LoadStickCalibration + LoadIMUCalibration +
// SetVibrationEnabled + SetInputMode), then validates streaming, input
// round-trip, IMU bytes, and rumble decode. The 0x80 USB-init family is
// absent from the BT descriptor, so phase 1 asserts it is DEAD, exactly
// as on real BT hardware. Wire sizes are the BT ones: writes are
// exactly 49 bytes; reads complete padded to InputReportByteLength 362
// (HidClass behavior, verified identical on a live BT Pro).
// Every wire byte asserted here is quoted from SDL_hidapi_switch.c (the
// client), nxbt protocol.py (the device-side reference), or the
// dekuNukem notes; file:line cites sit next to each phase.
//
// Runs fully headless: no Steam, no SDL, no physical hardware. Creates
// the controller, drives it, asserts, cleans up. Exit 0 PASS / 1 FAIL.
// Requires elevation (CreateController).
//
// record <out dir> instead records the same init and 3 s of the stream for
// device_oracle_check's compare-live mode (see Record below). Exit 0
// recorded, 1 a step failed, 2 bad arguments, 3 refused without elevation,
// before anything is created.

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

    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_SetNumInputBuffers(SafeFileHandle h, uint n);

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

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_FlushQueue(SafeFileHandle h);

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

    static SafeFileHandle s_hid = null!;
    static IntPtr s_readEvent, s_writeEvent;

    static int s_lastReadLen;

    // BT descriptor: OutputReportByteLength is 49 (48 vendor bytes + ID).
    // Pads like hidapi does; a write of any other length fails at HidClass.
    static bool HidWrite(byte[] report)
    {
        var buf = new byte[49];
        Array.Copy(report, buf, Math.Min(report.Length, 49));
        return HidWriteRaw(buf, 49);
    }

    // Exact-length write for negative tests (the 64-byte SDL USB framing
    // must FAIL under the BT descriptor).
    static bool HidWriteRaw(byte[] report, int len)
    {
        var buf = GC.AllocateArray<byte>(len, pinned: true);
        Array.Copy(report, buf, len);
        var ov = new NativeOverlapped { EventHandle = s_writeEvent };
        if (!WriteFile(s_hid, buf, (uint)len, IntPtr.Zero, ref ov)
            && Marshal.GetLastWin32Error() != 997 /* ERROR_IO_PENDING */)
            return false;
        if (!GetOverlappedResultEx(s_hid, ref ov, out uint written, 1000, false))
        {
            CancelAndWait(ref ov);
            return false;
        }
        return written == (uint)len;
    }

    // A canceled read or write still completes, and the system writes its
    // status into the OVERLAPPED and its bytes into the buffer when it does.
    // CancelIoEx on Microsoft Learn: "The application must not free or reuse
    // the OVERLAPPED structure associated with the canceled I/O operations
    // until they have completed." The OVERLAPPED lives in the caller's
    // frame, so this waits for the completion before the frame returns, and
    // the buffers are pinned so the collector cannot move them meanwhile.
    static void CancelAndWait(ref NativeOverlapped ov)
    {
        CancelIoEx(s_hid, IntPtr.Zero);
        GetOverlappedResultEx(s_hid, ref ov, out _, 0xFFFFFFFF /* INFINITE */, false);
    }

    // BT descriptor: InputReportByteLength is 362 (the 0x31-0x33 family).
    // HidClass zero-pads every completed read to that length regardless
    // of the report's wire size (0x3F = 12, 0x21/0x30 = 49); a live BT
    // Pro reads identically (2026-07-22 capture). Track the completed
    // length so shape asserts can guard descriptor drift.
    static byte[]? HidRead(int timeoutMs)
    {
        var buf = GC.AllocateArray<byte>(362, pinned: true);
        var ov = new NativeOverlapped { EventHandle = s_readEvent };
        if (!ReadFile(s_hid, buf, 362, IntPtr.Zero, ref ov)
            && Marshal.GetLastWin32Error() != 997)
            return null;
        if (!GetOverlappedResultEx(s_hid, ref ov, out uint read, (uint)timeoutMs, false))
        {
            CancelAndWait(ref ov);
            return null;
        }
        s_lastReadLen = (int)read;
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
    // 0x01, counter, neutral rumble 00 01 40 40 per side, subcommand,
    // args. On the BT path SDL writes these at the 49-byte output size
    // (hidapi pads to OutputReportByteLength); HidWrite does the same.
    static int s_counter;

    // The reply deadline only detects a hang: a reply comes within one
    // 15 ms stream tick, and the read returns as soon as it does.
    static byte[]? Subcommand(byte id, byte[] args, int timeoutMs = 60000)
    {
        var pkt = new byte[49];
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

    static int Main(string[] args)
    {
        if (args.Length == 0) return Checks();
        if (args.Length == 2 && args[0] == "record") return Record(args[1]);
        Console.WriteLine("usage: SwitchProCheck [record <out dir>]");
        return 2;
    }

    static int Checks()
    {
        Console.WriteLine("=== Switch Pro protocol responder check (issues #33/#36/#37) ===");

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

        // Locate THIS pad's HID interface (poll: enumeration follows
        // device creation by a few hundred ms). VID/PID alone is ambiguous
        // when a real Pro Controller is paired to the same box (2026-07-22:
        // the probe opened the live pad and asserted against its real
        // firmware/calibration), and so is any HM-CTL- serial while another
        // program runs a virtual Switch Pro. This one serves HM-CTL- and its
        // own transport index (DeviceOrchestrator.DriverSerial). A real one
        // serves its MAC.
        string serial = DeviceOrchestrator.DriverSerial(ctrl.TransportIndex);
        string? path = null;
        for (int i = 0; i < 300 && path == null; i++)
        {
            path = HidDeviceEnumerator.Enumerate()
                .FirstOrDefault(d => d.VendorId == 0x057E && d.ProductId == 0x2009
                    && d.SerialNumberString == serial)?.DevicePath;
            if (path == null) Thread.Sleep(100);
        }
        Check($"virtual HID interface enumerates (VID 057E PID 2009, serial {serial})", path != null, path ?? "");
        if (path == null) return Fail(ref pumpStop, pump);

        s_readEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        s_writeEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        s_hid = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (!s_hid.IsInvalid) HidD_SetNumInputBuffers(s_hid, 512);
        Check("CreateFile read/write", !s_hid.IsInvalid);
        if (s_hid.IsInvalid) return Fail(ref pumpStop, pump);

        // ── Phase 1: 0x80 USB-init family is DEAD (BT parity) ───────────
        Console.WriteLine("\n-- Phase 1: 0x80 USB init absent (real-BT parity, issue #37) --");

        // The BT descriptor carries no 0x80 output / 0x81 input reports.
        // SDL's BTrySetupUSB writes 0x80 0x02 at the 64-byte USB size
        // (SDL_hidapi_switch.c:724-746); under this descriptor the write
        // must fail at HidClass, or at minimum no 0x81 ack can surface
        // (an input report ID absent from the descriptor is dropped).
        // Either way SDL concludes Bluetooth and uses subcommand init.
        var cmd80 = new byte[64]; cmd80[0] = 0x80; cmd80[1] = 0x02;
        bool wrote80 = HidWriteRaw(cmd80, 64);
        Check("0x80 handshake dead (write rejected or no 0x81 ack)",
              !wrote80 || ReadUntil(0x81, 400) == null,
              wrote80 ? "write accepted, no ack" : "write rejected");

        // ── Phase 2: subcommands (SDL BT init order) ────────────────────
        Console.WriteLine("\n-- Phase 2: subcommand request-reply (49-byte BT framing) --");

        var info = Subcommand(0x02, Array.Empty<byte>());
        Check("0x02 device info reply", info != null);
        // HidClass completes every read at InputReportByteLength (362
        // under the BT descriptor), zero-padded past the report's actual
        // bytes. Verified byte-identical on a live BT Pro on this stack
        // (2026-07-22 capture). Guards descriptor drift, not driver-side
        // completion length (invisible from user mode).
        Check("reads complete at 362 (InputReportByteLength, real-BT parity)",
              info != null && s_lastReadLen == 362, $"{s_lastReadLen}");
        Check("device info ACK 0x82 (nxbt :300)", info != null && info[13] == 0x82);
        Check("device info type Pro (payload[2]=0x03)", info != null && info[17] == 0x03);
        Check("device info firmware 03.8B (nxbt :306-307)", info != null && info[15] == 0x03 && info[16] == 0x8B);
        Check("device info MAC nonzero (payload[4..9])",
              info != null && info.Skip(19).Take(6).Any(b => b != 0));

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

        // Drop whatever is queued, then time 120 fresh frames. Reading until
        // a read times out never ends while frames keep coming faster than
        // the timeout, and the pad streams every 15 ms (driver.c's stream
        // clock): beside the 2026-10-19 gate this loop ran for 14 minutes.
        // HidD_FlushQueue empties the HID class's queue in one call, as
        // DS4Windows does before reading (HidLibrary/HidDevice.cs:162).
        // The clock's upper bound is judged by the median gap between
        // consecutive frames: a stall of this process or of the driver under
        // load moves a few gaps, not the median of 119. Its lower bound is
        // judged by the run's average, since a stalled reader finds queued
        // frames back to back but never more frames than the clock made.
        Check("input queue flushed", HidD_FlushQueue(s_hid), $"error {Marshal.GetLastWin32Error()}");
        const int streamFrames = 120;
        int frames = 0, timerRepeats = 0; byte lastTimer = 0;
        bool allLen362 = true;
        var gaps = new List<double>(streamFrames);
        long lastTicks = 0;
        var cadence = Stopwatch.StartNew();
        for (int i = 0; i < streamFrames; i++)
        {
            var r = ReadUntil(0x30, 60000);
            if (r == null) break;
            long now = Stopwatch.GetTimestamp();
            if (frames > 0)
            {
                gaps.Add((now - lastTicks) * 1000.0 / Stopwatch.Frequency);
                if (r[1] == lastTimer) timerRepeats++;
            }
            lastTicks = now;
            lastTimer = r[1];
            if (s_lastReadLen != 362) allLen362 = false;
            frames++;
        }
        cadence.Stop();
        Check($"{streamFrames} consecutive 0x30 frames", frames == streamFrames, $"got {frames}");
        Check("0x30 frames complete at 362 (padded, real-BT parity)", frames > 0 && allLen362);
        gaps.Sort();
        double medianGap = gaps.Count > 0 ? gaps[gaps.Count / 2] : 0;
        double msPerFrame = cadence.Elapsed.TotalMilliseconds / Math.Max(1, frames);
        Check("stream clock ~15 ms (median gap at most 25 ms, average at least 10 ms)",
            gaps.Count > 0 && medianGap <= 25 && msPerFrame >= 10,
            $"median {medianGap:F1} ms over {gaps.Count} gaps, average {msPerFrame:F1} ms");
        // The timer counts every frame (driver.c:1855), so no two
        // consecutive frames carry the same value. Frames the HID class
        // drops while this process stalls only make the step larger.
        Check("timer byte advances on every frame", frames >= 2 && timerRepeats == 0, $"{timerRepeats} repeats");

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

        var f = ReadUntil(0x30, 60000, r => (r[3] & 0x08) != 0);
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
        // neutral (00 01 40 40). 49-byte BT write like every output.
        var rumble = new byte[49];
        rumble[0] = 0x10; rumble[1] = 0x01;
        rumble[2] = 0x00; rumble[3] = 0xC8; rumble[4] = 0x60; rumble[5] = 0x00;
        rumble[6] = 0x00; rumble[7] = 0x01; rumble[8] = 0x40; rumble[9] = 0x40;
        Check("0x10 rumble write", HidWrite(rumble));
        // A hang detector: the decode fires within milliseconds, and the
        // wait ends as soon as it does.
        var decodeSw = Stopwatch.StartNew();
        while (decoded10Count == 0 && decodeSw.ElapsedMilliseconds < 10000) Thread.Sleep(10);
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

    // ── record: the init and the stream for device_oracle_check ─────────
    //
    // The same pad, pump and writes as phases 1 and 2 above, then 3 s of
    // the stream, with every read the host makes recorded in order. Case
    // live-switch-pro, streams:
    //   switch-pro/index<N>/state          body: the 48-byte body the pump
    //                                      submits (SwitchProPacker.BuildBody
    //                                      of the neutral state, whose sticks
    //                                      resolve to 0.5, HMController.cs:997)
    //   switch-pro/index<N>/pre-handshake  300 ms of reads before any write
    //   switch-pro/index<N>/init           per write, in order: write (the
    //                                      bytes written), writeOk, writeError
    //                                      (u32, 0 on success), written (u32),
    //                                      the frames read before the reply,
    //                                      then reply and timer, or noReply
    //   switch-pro/index<N>/stream         3 s of reads after the init, then
    //                                      timerSteps
    //
    // What depends on time, and how it is kept apart so a comparison can
    // mask exactly it. The driver's timer byte (byte 1 of 0x21 and 0x30,
    // driver.c:1631 and 1855) counts every reply and every stream frame,
    // so its value depends on how many frames streamed first: a reply is
    // recorded with byte 1 zeroed under reply and the byte itself under
    // timer. How many frames arrive in a window, and whether any arrive
    // ahead of a reply, depends on the 15 ms stream clock (driver.c:1819):
    // reads are recorded as runs, the frame with byte 1 zeroed under frame
    // (or skipped, ahead of a reply) and the run's length under repeat (or
    // skippedRepeat). timerSteps lists the distinct differences between
    // consecutive stream timers in first-seen order. A comparison masks
    // timer, repeat, skipped, skippedRepeat and timerSteps, and every other
    // byte must match. Reads are the HID class's whole completions, 362
    // bytes under the Bluetooth descriptor.

    /// <summary>A record-mode write: the BOOLEAN, the error and the count,
    /// where <see cref="HidWriteRaw"/> folds them into one bool.</summary>
    static (bool ok, uint error, uint written) RecordWrite(byte[] report, int len)
    {
        var buf = GC.AllocateArray<byte>(len, pinned: true);
        Array.Copy(report, buf, len);
        var ov = new NativeOverlapped { EventHandle = s_writeEvent };
        if (!WriteFile(s_hid, buf, (uint)len, IntPtr.Zero, ref ov))
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 997 /* ERROR_IO_PENDING */) return (false, (uint)err, 0);
        }
        if (!GetOverlappedResultEx(s_hid, ref ov, out uint written, 1000, false))
        {
            int err = Marshal.GetLastWin32Error();
            CancelAndWait(ref ov);
            return (false, (uint)err, 0);
        }
        return (true, 0, written);
    }

    /// <summary>Consecutive identical reads folded into one frame and a
    /// count, byte 1 zeroed where it is the timer.</summary>
    sealed class Runs
    {
        readonly LiveRecorder _rec;
        readonly string _frameTag, _repeatTag;
        byte[]? _last;
        uint _count;

        public Runs(LiveRecorder rec, string frameTag, string repeatTag)
        {
            _rec = rec; _frameTag = frameTag; _repeatTag = repeatTag;
        }

        public void Add(byte[] masked)
        {
            if (_last != null && masked.AsSpan().SequenceEqual(_last)) { _count++; return; }
            Flush();
            _last = masked;
            _count = 1;
        }

        public void Flush()
        {
            if (_last == null) return;
            _rec.Out(_frameTag, _last);
            _rec.OutU32(_repeatTag, _count);
            _last = null;
        }
    }

    static bool HasTimer(byte reportId) => reportId == 0x21 || reportId == 0x30;

    /// <summary>The read as the host got it, with the timer byte zeroed.</summary>
    static byte[] Masked(byte[] r, int len)
    {
        var m = r.AsSpan(0, len).ToArray();
        if (HasTimer(m[0])) m[1] = 0;
        return m;
    }

    static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    static int Record(string outDir)
    {
        Console.WriteLine("=== Switch Pro init and stream, recorded for device_oracle_check ===");
        if (!IsElevated())
        {
            Console.WriteLine("  [REFUSED] record creates a controller and needs elevation. Nothing was created");
            return 3;
        }

        using var rec = new LiveRecorder(outDir, "live-switch-pro");
        var pumpStop = false;
        Thread? pump = null;
        try
        {
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            ctx.InstallDriver();
            var profile = ctx.GetProfile("switch-pro")
                ?? throw new InvalidOperationException("switch-pro profile missing");
            using var ctrl = ctx.CreateController(profile);
            string head = $"switch-pro/index{ctrl.TransportIndex}";

            var neutral = new HMGamepadState();
            // The controller is disposed when the try block ends, before the
            // finally stops this thread, so a submit can meet a disposed
            // controller on the way out.
            pump = new Thread(() =>
            {
                try
                {
                    while (!Volatile.Read(ref pumpStop))
                    {
                        ctrl.SubmitState(neutral);
                        Thread.Sleep(8);
                    }
                }
                catch (ObjectDisposedException) { }
            })
            { IsBackground = true };
            pump.Start();

            rec.Stream(head + "/state");
            // The state block the neutral state encodes to: switch-pro's
            // stateBlock through the codec, as SubmitState encodes it.
            var stateSpec = ProfileDatabase.LoadEmbedded().All.First(x => x.Id == "switch-pro").StateBlockSpecForCodec();
            var body = new byte[stateSpec.Size];
            VendorBlobCodec.EncodeInput(stateSpec, in neutral, 0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f, body, new VendorBlobCodec.EncoderState());
            rec.Out("body", body);

            // THIS pad, by its own serial as in Checks: a real Pro and
            // another program's virtual Switch Pro share VID, PID and the
            // HM-CTL- prefix.
            string serial = DeviceOrchestrator.DriverSerial(ctrl.TransportIndex);
            string? path = null;
            for (int i = 0; i < 300 && path == null; i++)
            {
                path = HidDeviceEnumerator.Enumerate()
                    .FirstOrDefault(d => d.VendorId == 0x057E && d.ProductId == 0x2009
                        && d.SerialNumberString == serial)?.DevicePath;
                if (path == null) Thread.Sleep(100);
            }
            if (path == null) throw new InvalidOperationException("the virtual pad's HID interface did not appear");
            s_readEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            s_writeEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            s_hid = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (!s_hid.IsInvalid) HidD_SetNumInputBuffers(s_hid, 512);
            if (s_hid.IsInvalid) throw new InvalidOperationException($"CreateFile failed, error {Marshal.GetLastWin32Error()}");

            // Before any protocol traffic the driver streams 0x3F simple
            // frames (driver.c:1843-1849, SwitchBuildSimpleFrame).
            rec.Stream(head + "/pre-handshake");
            var pre = new Runs(rec, "frame", "repeat");
            for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < 300;)
            {
                var r = HidRead(100);
                if (r != null) pre.Add(Masked(r, s_lastReadLen));
            }
            pre.Flush();

            rec.Stream(head + "/init");
            var skipped = new Runs(rec, "skipped", "skippedRepeat");

            // Phase 1: SDL's BTrySetupUSB opens with the 0x80 0x02 handshake
            // at the 64-byte USB size (SDL-reference 804cf65
            // SDL_hidapi_switch.c:707-714). Under the Bluetooth descriptor
            // HidClass rejects it, or no 0x81 comes back.
            var cmd80 = new byte[64]; cmd80[0] = 0x80; cmd80[1] = 0x02;
            RecordExchange(rec, skipped, cmd80, 64, 0x81, 400, _ => true);

            // Phase 2: SDL's Bluetooth init, in phase 2's order and bytes.
            // At SDL-reference 804cf65 SDL_hidapi_switch.c: device info
            // (BReadDeviceInfo, 676), the SPI reads of LoadStickCalibration
            // (979, user 0x8010 at 991-995 then factory 0x603D at 1020) and
            // LoadIMUCalibration (0x6020, 1106-1115), the stick parameters
            // (0x6086, issue #36), SetVibrationEnabled (0x48, 731),
            // SetIMUEnabled (0x40, 973), player lights (0x30), an unknown
            // subcommand (0x50) and SetInputMode 0x30 (0x03, 735). Packets
            // as ConstructSubcommand builds them (469-484) at the 49-byte
            // output size, each reply matched on its ID and ACK bit as
            // ReadSubcommandReply does (414-424).
            var subcommands = new (byte id, byte[] args)[]
            {
                (0x02, Array.Empty<byte>()),
                (0x10, new byte[] { 0x10, 0x80, 0x00, 0x00, 22 }),
                (0x10, new byte[] { 0x3D, 0x60, 0x00, 0x00, 18 }),
                (0x10, new byte[] { 0x20, 0x60, 0x00, 0x00, 24 }),
                (0x10, new byte[] { 0x86, 0x60, 0x00, 0x00, 18 }),
                (0x48, new byte[] { 0x01 }),
                (0x40, new byte[] { 0x01 }),
                (0x30, new byte[] { 0x01 }),
                (0x50, Array.Empty<byte>()),
                (0x03, new byte[] { 0x30 }),
            };
            s_counter = 0;
            foreach (var (id, a) in subcommands)
            {
                var pkt = new byte[49];
                pkt[0] = 0x01;
                pkt[1] = (byte)(s_counter++ & 0xF);
                pkt[2] = 0x00; pkt[3] = 0x01; pkt[4] = 0x40; pkt[5] = 0x40;
                pkt[6] = 0x00; pkt[7] = 0x01; pkt[8] = 0x40; pkt[9] = 0x40;
                pkt[10] = id;
                Array.Copy(a, 0, pkt, 11, a.Length);
                if (!RecordExchange(rec, skipped, pkt, 49, 0x21, 2000, r => r[14] == id && (r[13] & 0x80) != 0))
                    throw new InvalidOperationException($"subcommand 0x{id:X2} got no reply in 2000 ms");
            }

            // 3 s of the stream in mode 0x30 with the IMU on.
            rec.Stream(head + "/stream");
            var stream = new Runs(rec, "frame", "repeat");
            var steps = new List<byte>();
            int lastTimer = -1;
            for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < 3000;)
            {
                var r = HidRead(100);
                if (r == null) continue;
                stream.Add(Masked(r, s_lastReadLen));
                if (!HasTimer(r[0])) continue;
                if (lastTimer >= 0 && !steps.Contains((byte)(r[1] - lastTimer))) steps.Add((byte)(r[1] - lastTimer));
                lastTimer = r[1];
            }
            stream.Flush();
            rec.Out("timerSteps", steps.ToArray());

            rec.Save();
            Console.WriteLine($"  wrote {outDir}");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"  [FAIL] {e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            Volatile.Write(ref pumpStop, true);
            pump?.Join(1000);
            s_hid?.Dispose();
            if (s_readEvent != IntPtr.Zero) CloseHandle(s_readEvent);
            if (s_writeEvent != IntPtr.Zero) CloseHandle(s_writeEvent);
            // The batch checks the driver store between steps, so record keeps
            // the driver packages, as the batch's cleanup --keep-install does.
            try { HMContext.RemoveAllVirtualControllers(HMCleanupScope.Everything, preserveInstall: true); } catch { }
        }
    }

    /// <summary>Write, then read until the wanted report arrives or the time
    /// is up, recording every read. True when the reply arrived.</summary>
    static bool RecordExchange(LiveRecorder rec, Runs skipped, byte[] pkt, int len,
                               byte replyId, int timeoutMs, Func<byte[], bool> accept)
    {
        rec.Out("write", pkt.AsSpan(0, len));
        var (ok, error, written) = RecordWrite(pkt, len);
        rec.OutByte("writeOk", ok ? (byte)1 : (byte)0);
        rec.OutU32("writeError", error);
        rec.OutU32("written", written);
        if (ok)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var r = HidRead((int)Math.Max(1, timeoutMs - sw.ElapsedMilliseconds));
                if (r == null) continue;
                if (r[0] == replyId && accept(r))
                {
                    skipped.Flush();
                    rec.Out("reply", Masked(r, s_lastReadLen));
                    rec.OutByte("timer", HasTimer(r[0]) ? r[1] : (byte)0);
                    return true;
                }
                skipped.Add(Masked(r, s_lastReadLen));
            }
        }
        skipped.Flush();
        rec.Out("noReply", ReadOnlySpan<byte>.Empty);
        return false;
    }
}
