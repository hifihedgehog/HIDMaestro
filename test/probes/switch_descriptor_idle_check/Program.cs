// Switch Pro pre-handshake descriptor-conformance check (issue #35).
//
// Before any Switch-protocol traffic arrives, the virtual Pro's 0x30
// stream must be packed in the layout the HID descriptor DECLARES
// (buttons at bytes 1-2, four 16-bit axes at 3-10, hat nibble at 11)
// so descriptor-driven parsers (DirectInput, joy.cpl) read a correct
// pad. The pre-#35 stream put the Nintendo full-mode bytes there
// (timer at byte 1, buttons at 3-5, packed sticks at 6-11), which
// joy.cpl parsed as strobing buttons, wandering axes, and a spinning
// hat.
//
// Phases:
//   1. Idle stream is descriptor-shaped: byte1/2 stable at 0 across
//      frames (the timer strobe is the mutant signature), axes at
//      0x8000, hat nibble 8 (null).
//   2. Submitted state lands in descriptor fields: A -> button 2,
//      ZR -> button 8, dpad up -> hat 0, stick full right/up ->
//      X ~65535 / Y ~0.
//   3. One 0x80 0x02 handshake write flips the stream permanently to
//      the Nintendo layout (timer advancing at byte 1, A at byte 3
//      bit 3), which is what SDL locks onto.
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

    static byte[]? HidRead(int timeoutMs)
    {
        var buf = new byte[64];
        var ov = new NativeOverlapped { EventHandle = s_readEvent };
        if (!ReadFile(s_hid, buf, 64, IntPtr.Zero, ref ov)
            && Marshal.GetLastWin32Error() != 997 /* ERROR_IO_PENDING */)
            return null;
        if (!GetOverlappedResultEx(s_hid, ref ov, out uint read, (uint)timeoutMs, false))
        {
            CancelIoEx(s_hid, IntPtr.Zero);
            return null;
        }
        return read > 0 ? buf : null;
    }

    static bool HidWrite(byte[] report64)
    {
        var ov = new NativeOverlapped { EventHandle = s_writeEvent };
        if (!WriteFile(s_hid, report64, 64, IntPtr.Zero, ref ov)
            && Marshal.GetLastWin32Error() != 997)
            return false;
        if (!GetOverlappedResultEx(s_hid, ref ov, out uint written, 1000, false))
        {
            CancelIoEx(s_hid, IntPtr.Zero);
            return false;
        }
        return written == 64;
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
        Console.WriteLine("=== Switch Pro descriptor-conformant idle stream (issue #35) ===");

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

        string? path = null;
        for (int i = 0; i < 50 && path == null; i++)
        {
            path = HidDeviceEnumerator.Enumerate()
                .FirstOrDefault(d => d.VendorId == 0x057E && d.ProductId == 0x2009)?.DevicePath;
            if (path == null) Thread.Sleep(100);
        }
        Check("HID interface enumerates (VID 057E PID 2009)", path != null, path ?? "");
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
        var deadline = Stopwatch.StartNew();
        while (frames.Count < 6 && deadline.ElapsedMilliseconds < 2000)
        {
            var r = HidRead(200);
            if (r != null && r[0] == 0x30) frames.Add(r);
        }
        Check("received idle 0x30 frames", frames.Count >= 4, $"{frames.Count}");
        if (frames.Count >= 4)
        {
            // The mutant signature: byte 1 carried the Nintendo timer and
            // advanced every frame; descriptor layout has stable button
            // bytes (0 while idle).
            bool byte1Stable = frames.All(f => f[1] == 0);
            bool byte2Stable = frames.All(f => f[2] == 0);
            Check("byte1 stable at 0 across frames (no timer strobe)", byte1Stable,
                  string.Join(",", frames.Select(f => f[1].ToString("X2"))));
            Check("byte2 stable at 0 across frames", byte2Stable);

            var f0 = frames[^1];
            int x  = f0[3] | (f0[4] << 8);
            int y  = f0[5] | (f0[6] << 8);
            int z  = f0[7] | (f0[8] << 8);
            int rz = f0[9] | (f0[10] << 8);
            Check("X centered (~0x8000)",  Math.Abs(x - 0x8000) <= 0x200, $"0x{x:X4}");
            Check("Y centered (~0x8000)",  Math.Abs(y - 0x8000) <= 0x200, $"0x{y:X4}");
            Check("Z centered (~0x8000)",  Math.Abs(z - 0x8000) <= 0x200, $"0x{z:X4}");
            Check("Rz centered (~0x8000)", Math.Abs(rz - 0x8000) <= 0x200, $"0x{rz:X4}");
            Check("hat null (nibble 8)", (f0[11] & 0x0F) == 8, $"0x{f0[11]:X2}");
            Check("const tail zero", f0.Skip(12).All(b => b == 0));
        }

        // ── Phase 2: submitted state lands in descriptor fields ─────────
        Console.WriteLine("\n-- Phase 2: submitted state in descriptor fields --");

        // A = layout buttonIndex 1 -> descriptor button 2 (byte1 bit1);
        // ZR = index 7 -> descriptor button 8 (byte1 bit7); dpad up ->
        // hat 0; left stick full right (X~65535) and full up (Y~0).
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

        var fp = ReadUntil(0x30, 800, r => (r[1] & 0x02) != 0);
        Check("A press lands on descriptor button 2 (byte1 bit1)", fp != null);
        if (fp != null)
        {
            Check("ZR on descriptor button 8 (byte1 bit7)", (fp[1] & 0x80) != 0, $"0x{fp[1]:X2}");
            int x = fp[3] | (fp[4] << 8);
            int y = fp[5] | (fp[6] << 8);
            Check("X full right (>= 0xF000)", x >= 0xF000, $"0x{x:X4}");
            Check("Y full up (<= 0x0FFF)", y <= 0x0FFF, $"0x{y:X4}");
            Check("hat up (nibble 0)", (fp[11] & 0x0F) == 0, $"0x{fp[11]:X2}");
        }

        // Back to neutral before the handshake.
        lock (pumpLock) pumpState = new HMGamepadState();
        Thread.Sleep(40);

        // ── Phase 3: held pad answers protocol but keeps descriptor mode ──
        Console.WriteLine("\n-- Phase 3: held pad answers 0x80 but stays descriptor-mode --");

        var cmd = new byte[64];
        cmd[0] = 0x80; cmd[1] = 0x02;   // SDL BTrySetupUSB handshake step
        Check("0x80 0x02 handshake write", HidWrite(cmd));
        var ack = ReadUntil(0x81, 1000);
        Check("0x81 handshake reply (responder alive under hold)", ack != null && ack[1] == 0x02);
        var still = ReadUntil(0x30, 800);
        Check("stream stays descriptor-mode under hold (byte2 == 0)",
              still != null && still[2] == 0, still != null ? $"0x{still[2]:X2}" : "no frame");

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

        // ── Phase 4: unheld pad flips to Nintendo layout on protocol ────
        // A fresh pad WITHOUT the hold. On a box with a Chromium browser
        // running, Edge/Chrome arms it within milliseconds; otherwise our
        // own 0x80 write does. Either way the stream must be Nintendo
        // full-mode: timer at byte 1, battery/conn at byte 2.
        Console.WriteLine("\n-- Phase 4: unheld pad locks Nintendo layout on protocol traffic --");

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
                    .FirstOrDefault(d => d.VendorId == 0x057E && d.ProductId == 0x2009)?.DevicePath;
                if (path2 == null) Thread.Sleep(100);
            }
            Check("second pad enumerates", path2 != null);
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
                    deadline.Restart();
                    while (nf.Count < 4 && deadline.ElapsedMilliseconds < 2000)
                    {
                        var r = HidRead(200);
                        if (r != null && r[0] == 0x30) nf.Add(r);
                    }
                    Check("post-arm 0x30 frames", nf.Count >= 3, $"{nf.Count}");
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
            ? "\n=== PASS: idle stream descriptor-conformant; protocol locks Nintendo on unheld pad ==="
            : $"\n=== FAIL: {s_failures} check(s) failed ===");
        return s_failures == 0 ? 0 : 1;
    }
}
