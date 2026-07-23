// Switch Pro pre-handshake simple-mode conformance check (issue #37,
// superseding the #35 synthetic-0x30 shape).
//
// Under the real BLUETOOTH descriptor (extracted from a live Pro's SDP
// cache), report 0x3F is the only report DirectInput can parse: 16
// buttons, null-state hat, X/Y/Rx/Ry 16-bit. The full-mode family is
// vendor-blob, invisible to joy.cpl. Pre-handshake the driver streams
// genuine 12-byte 0x3F simple-mode frames; the first 0x01 subcommand
// arms full mode and the stream becomes the 49-byte BT 0x30.
//
// Phases:
//   1. Idle stream is 12-byte 0x3F: buttons 0 stable (no strobe), hat
//      nibble 8 (null), all four axes 0x8000.
//   2. Submitted state lands in simple-mode fields: A -> byte1 bit1,
//      ZR -> byte1 bit7, dpad up -> hat 0, X full right / Y full up.
//   3. Held pad answers a 0x01 subcommand (0x21 reply, 49 bytes) but
//      the stream stays 0x3F.
//   4. Unheld pad flips to 49-byte 0x30 full-mode on the first
//      subcommand (timer advancing, battery byte, A at byte3 bit3).
//
// Headless, elevation-required, exit 0 PASS / 1 FAIL.

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
    static int s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

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

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

    static SafeFileHandle s_hid = null!;
    static IntPtr s_readEvent, s_writeEvent;

    static int s_lastReadLen;

    // BT descriptor: InputReportByteLength is 362 (the 0x31-0x33 family).
    // HidClass zero-pads every completed read to that length regardless
    // of the report's wire size (0x3F = 12, 0x21/0x30 = 49); a live BT
    // Pro reads identically (2026-07-22 capture). Track the completed
    // length so shape asserts can guard descriptor drift.
    static byte[]? HidRead(int timeoutMs)
    {
        var buf = new byte[362];
        var ov = new NativeOverlapped { EventHandle = s_readEvent };
        if (!ReadFile(s_hid, buf, 362, IntPtr.Zero, ref ov)
            && Marshal.GetLastWin32Error() != 997 /* ERROR_IO_PENDING */)
            return null;
        if (!GetOverlappedResultEx(s_hid, ref ov, out uint read, (uint)timeoutMs, false))
        {
            CancelIoEx(s_hid, IntPtr.Zero);
            return null;
        }
        s_lastReadLen = (int)read;
        return read > 0 ? buf : null;
    }

    // BT descriptor: OutputReportByteLength is 49 (48 vendor bytes + ID).
    static bool HidWrite(byte[] report)
    {
        var buf = new byte[49];
        Array.Copy(report, buf, Math.Min(report.Length, 49));
        var ov = new NativeOverlapped { EventHandle = s_writeEvent };
        if (!WriteFile(s_hid, buf, 49, IntPtr.Zero, ref ov)
            && Marshal.GetLastWin32Error() != 997)
            return false;
        if (!GetOverlappedResultEx(s_hid, ref ov, out uint written, 1000, false))
        {
            CancelIoEx(s_hid, IntPtr.Zero);
            return false;
        }
        return written == 49;
    }

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

    static int Main()
    {
        Console.WriteLine("=== Switch Pro 0x3F simple-mode idle stream (issue #37) ===");

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

        // Descriptor-idle hold (TTL-gated, see driver.c DeviceAdd): keeps
        // THIS pad in descriptor mode even when a Chromium browser is
        // running. Chromium's gamepad service is a legitimate Switch
        // protocol host and handshakes every new Pro within milliseconds
        // (2026-07-21 audit: msedge.exe armed the pad before the first
        // stream tick, exactly as it does real hardware), which would
        // otherwise make phases 1-2 unobservable. Deleted in the finally
        // below; the driver additionally ignores values older than 60 s.
        const string HmKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\HIDMaestro";
        const string HoldValue = "SwitchDescriptorIdleHold";
        Microsoft.Win32.Registry.SetValue(HmKey, HoldValue,
            (long)DateTime.UtcNow.ToFileTimeUtc(),
            Microsoft.Win32.RegistryValueKind.QWord);
        try
        {

        Console.WriteLine("  Creating switch-pro virtual controller (hold armed)...");
        using var ctrl = ctx.CreateController(profile);

        // Neutral pump keeps the shared body populated. NO protocol traffic
        // is written until phase 3, so the stream must stay in descriptor
        // layout throughout phases 1-2.
        var pumpState = new HMGamepadState();
        var pumpLock = new object();
        var pumpStop = false;
        var pump = new Thread(() =>
        {
            while (!Volatile.Read(ref pumpStop))
            {
                lock (pumpLock) ctrl.SubmitState(pumpState);
                Thread.Sleep(8);
            }
        })
        { IsBackground = true };
        pump.Start();

        // VIRTUAL pad only: a real Pro paired to this box shares VID/PID
        // (2026-07-22). The virtual serves serial HM-CTL-<index>; a real
        // one serves its MAC.
        string? path = null;
        for (int i = 0; i < 50 && path == null; i++)
        {
            path = HidDeviceEnumerator.Enumerate()
                .FirstOrDefault(d => d.VendorId == 0x057E && d.ProductId == 0x2009
                    && d.SerialNumberString != null
                    && d.SerialNumberString.StartsWith("HM-CTL-"))?.DevicePath;
            if (path == null) Thread.Sleep(100);
        }
        Check("virtual HID interface enumerates (VID 057E PID 2009, HM-CTL serial)", path != null, path ?? "");
        if (path == null) { Volatile.Write(ref pumpStop, true); return 1; }

        s_readEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        s_writeEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        s_hid = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        Check("HID device opens", !s_hid.IsInvalid);
        if (s_hid.IsInvalid) { Volatile.Write(ref pumpStop, true); return 1; }

        // ── Phase 1: idle stream is descriptor-shaped ───────────────────
        Console.WriteLine("\n-- Phase 1: idle stream (no protocol traffic yet) --");

        var frames = new System.Collections.Generic.List<byte[]>();
        int frameLen = -1;
        var deadline = Stopwatch.StartNew();
        while (frames.Count < 6 && deadline.ElapsedMilliseconds < 2000)
        {
            var r = HidRead(200);
            if (r != null && r[0] == 0x3F) { frames.Add(r); frameLen = s_lastReadLen; }
        }
        Check("received idle 0x3F frames", frames.Count >= 4, $"{frames.Count}");
        if (frames.Count >= 4)
        {
            // HidClass completes every read at InputReportByteLength
            // (362 under the BT descriptor), zero-padded. Verified
            // identical on a live BT Pro (2026-07-22 capture). Guards
            // descriptor drift.
            Check("reads complete at 362 (InputReportByteLength)", frameLen == 362, $"{frameLen}");
            // The regression signature: full-mode bytes through the wrong
            // map strobed byte 1; simple mode has stable button bytes.
            bool byte1Stable = frames.All(f => f[1] == 0);
            bool byte2Stable = frames.All(f => f[2] == 0);
            Check("byte1 stable at 0 across frames (no strobe)", byte1Stable,
                  string.Join(",", frames.Select(f => f[1].ToString("X2"))));
            Check("byte2 stable at 0 across frames", byte2Stable);

            var f0 = frames[^1];
            int x  = f0[4] | (f0[5] << 8);
            int y  = f0[6] | (f0[7] << 8);
            int rx = f0[8] | (f0[9] << 8);
            int ry = f0[10] | (f0[11] << 8);
            Check("X centered (~0x8000)",  Math.Abs(x - 0x8000) <= 0x200, $"0x{x:X4}");
            Check("Y centered (~0x8000)",  Math.Abs(y - 0x8000) <= 0x200, $"0x{y:X4}");
            Check("Rx centered (~0x8000)", Math.Abs(rx - 0x8000) <= 0x200, $"0x{rx:X4}");
            Check("Ry centered (~0x8000)", Math.Abs(ry - 0x8000) <= 0x200, $"0x{ry:X4}");
            Check("hat null (nibble 8)", (f0[3] & 0x0F) == 8, $"0x{f0[3]:X2}");
            Check("pad nibble zero", (f0[3] & 0xF0) == 0, $"0x{f0[3]:X2}");
        }

        // ── Phase 2: submitted state lands in simple-mode fields ────────
        Console.WriteLine("\n-- Phase 2: submitted state in simple-mode fields --");

        // A = layout buttonIndex 1 -> simple-mode bit1 (byte1);
        // ZR = index 7 -> simple-mode bit7 (byte1); dpad up -> hat 0;
        // left stick full right (X~65535) and full up (Y~0, up-positive
        // SDL frame inverts to descriptor low).
        lock (pumpLock)
        {
            pumpState = new HMGamepadState
            {
                Buttons = (HMButton)((1u << 1) | (1u << 7)),
                HatDegrees = 0f,
                Axes = new System.Collections.Generic.Dictionary<HMAxis, float>
                {
                    [HMAxis.X] = 1.0f,
                    [HMAxis.Y] = 0.0f,
                },
            };
        }
        Thread.Sleep(60);

        var fp = ReadUntil(0x3F, 800, r => (r[1] & 0x02) != 0);
        Check("A press lands on simple-mode byte1 bit1", fp != null);
        if (fp != null)
        {
            Check("ZR on simple-mode byte1 bit7", (fp[1] & 0x80) != 0, $"0x{fp[1]:X2}");
            int x = fp[4] | (fp[5] << 8);
            int y = fp[6] | (fp[7] << 8);
            Check("X full right (>= 0xF000)", x >= 0xF000, $"0x{x:X4}");
            Check("Y full up (<= 0x0FFF)", y <= 0x0FFF, $"0x{y:X4}");
            Check("hat up (nibble 0)", (fp[3] & 0x0F) == 0, $"0x{fp[3]:X2}");
        }

        // Back to neutral before the handshake.
        lock (pumpLock) pumpState = new HMGamepadState();
        Thread.Sleep(40);

        // ── Phase 3: held pad answers a subcommand but stays 0x3F ──────
        Console.WriteLine("\n-- Phase 3: held pad answers 0x01 subcommand but stays 0x3F --");

        // Bare 0x01 subcommand, SDL BT framing: counter + neutral rumble
        // + subcommand 0x02 (device info). The 0x80 USB-init family does
        // not exist in the BT descriptor, exactly like real BT hardware.
        var cmd = new byte[49];
        cmd[0] = 0x01; cmd[1] = 0x00;
        cmd[2] = 0x00; cmd[3] = 0x01; cmd[4] = 0x40; cmd[5] = 0x40;
        cmd[6] = 0x00; cmd[7] = 0x01; cmd[8] = 0x40; cmd[9] = 0x40;
        cmd[10] = 0x02;                 // subcommand: device info
        Check("0x01 subcommand write (49-byte BT framing)", HidWrite(cmd));
        var ack = ReadUntil(0x21, 1000);
        Check("0x21 subcommand reply (responder alive under hold)",
              ack != null && ack[14] == 0x02, ack != null ? $"len={s_lastReadLen}" : "no reply");
        Check("0x21 reply completes at 362 (padded)", ack != null && s_lastReadLen == 362, $"{s_lastReadLen}");
        var still = ReadUntil(0x3F, 800);
        Check("stream stays 0x3F under hold", still != null,
              still != null ? $"len={s_lastReadLen}" : "no frame");

        Volatile.Write(ref pumpStop, true);
        pump.Join(1000);
        s_hid.Dispose();
        ctrl.Dispose();

        // Drop the hold BEFORE the phase-4 create: the TTL window is 60 s
        // and the driver reads the value at every Switch DeviceAdd, so a
        // still-present value would hold the second pad too.
        try
        {
            using var hmEarly = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\HIDMaestro", writable: true);
            hmEarly?.DeleteValue(HoldValue, throwOnMissingValue: false);
        }
        catch { }

        // ── Phase 4: unheld pad flips to BT full-mode 0x30 on protocol ──
        // A fresh pad WITHOUT the hold. On a box with a Chromium browser
        // running, Edge/Chrome arms it within milliseconds; otherwise our
        // own 0x01 subcommand does. Either way the stream must be the
        // 49-byte BT full-mode 0x30: timer at byte 1, battery at byte 2.
        Console.WriteLine("\n-- Phase 4: unheld pad locks 49-byte 0x30 full-mode on protocol traffic --");

        using (var ctrl2 = ctx.CreateController(profile))
        {
            var pump2Stop = false;
            var pump2 = new Thread(() =>
            {
                var st = new HMGamepadState { Buttons = (HMButton)(1u << 1) }; // A held
                while (!Volatile.Read(ref pump2Stop)) { ctrl2.SubmitState(st); Thread.Sleep(8); }
            }) { IsBackground = true };
            pump2.Start();

            string? path2 = null;
            for (int i = 0; i < 50 && path2 == null; i++)
            {
                path2 = HidDeviceEnumerator.Enumerate()
                    .FirstOrDefault(d => d.VendorId == 0x057E && d.ProductId == 0x2009
                        && d.SerialNumberString != null
                        && d.SerialNumberString.StartsWith("HM-CTL-"))?.DevicePath;
                if (path2 == null) Thread.Sleep(100);
            }
            Check("second pad enumerates (HM-CTL serial)", path2 != null);
            if (path2 != null)
            {
                s_hid = CreateFileW(path2, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                                    IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                Check("second pad opens", !s_hid.IsInvalid);
                if (!s_hid.IsInvalid)
                {
                    // Arm it ourselves; harmless if a browser already did.
                    HidWrite(cmd);

                    var nf = new System.Collections.Generic.List<byte[]>();
                    bool allLen362 = true;
                    deadline.Restart();
                    while (nf.Count < 4 && deadline.ElapsedMilliseconds < 2000)
                    {
                        var r = HidRead(200);
                        if (r != null && r[0] == 0x30)
                        {
                            nf.Add(r);
                            if (s_lastReadLen != 362) allLen362 = false;
                        }
                    }
                    Check("post-arm 0x30 frames", nf.Count >= 3, $"{nf.Count}");
                    Check("0x30 frames complete at 362 (padded)", nf.Count > 0 && allLen362);
                    if (nf.Count >= 3)
                    {
                        bool timerAdvances = false;
                        for (int i = 1; i < nf.Count; i++)
                            if (nf[i][1] != nf[0][1]) { timerAdvances = true; break; }
                        Check("byte1 advances (Nintendo timer)", timerAdvances,
                              string.Join(",", nf.Select(f => f[1].ToString("X2"))));
                        Check("byte2 battery/conn nonzero", nf[^1][2] != 0, $"0x{nf[^1][2]:X2}");
                        var na = ReadUntil(0x30, 800, r => (r[3] & 0x08) != 0);
                        Check("A press at Nintendo byte3 bit3 post-arm", na != null);
                    }
                    s_hid.Dispose();
                }
            }
            Volatile.Write(ref pump2Stop, true);
            pump2.Join(1000);
        }

        }
        finally
        {
            try
            {
                using var hm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\HIDMaestro", writable: true);
                hm?.DeleteValue(HoldValue, throwOnMissingValue: false);
            }
            catch { }
        }

        Console.WriteLine(s_failures == 0
            ? "\n=== PASS: idle stream 0x3F simple-mode; subcommand locks 49-byte 0x30 on unheld pad ==="
            : $"\n=== FAIL: {s_failures} check(s) failed ===");
        return s_failures == 0 ? 0 : 1;
    }
}
