// Stop-event resilience check (issue #38).
//
// The driver's shared-input worker waits on a NAMED manual-reset event
// (Global\HIDMaestroStopEvent<N>) that is shared with every past and
// future context at the same controller index and is signaled by any
// other process's RemoveAllVirtualControllers sweep. Pre-#38 the worker
// treated ANY StopEvent signal as its own teardown and returned, leaving
// a healthy device permanently frozen at its last report while the SDK
// writer's submits kept succeeding (the PadForge field symptom).
//
// This probe distills the kill vector deterministically: signal the live
// pad's named stop event from outside the driver, exactly as a foreign
// sweep does, and assert the wire KEEPS MOVING (the #38 fix recycles the
// worker instead of returning). Run against a pre-#38 driver it fails
// the post-signal phases, which is the bug reproduced.
//
// Phases:
//   1. Baseline: virtual DualSense streams, LX toggles reach the wire.
//   2. Foreign stop signal x3: after each SetEvent, the wire must still
//      deliver fresh toggling frames.
//   3. Teardown: dispose completes promptly (the TearingDown exit path)
//      and the HID interface disappears.
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
    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, byte[] buf, uint n, IntPtr read, ref NativeOverlapped ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetOverlappedResultEx(SafeFileHandle h, ref NativeOverlapped ov,
        out uint transferred, uint timeoutMs, bool alertable);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateEventW(IntPtr attrs, bool manual, bool initial, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenEventW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetEvent(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    const uint EVENT_MODIFY_STATE = 0x0002;

    static SafeFileHandle s_hid = null!;
    static IntPtr s_readEvent;
    static int s_reportLen = 64;

    static byte[]? HidRead(int timeoutMs)
    {
        var buf = new byte[s_reportLen];
        var ov = new NativeOverlapped { EventHandle = s_readEvent };
        if (!ReadFile(s_hid, buf, (uint)s_reportLen, IntPtr.Zero, ref ov)
            && Marshal.GetLastWin32Error() != 997 /* ERROR_IO_PENDING */)
            return null;
        if (!GetOverlappedResultEx(s_hid, ref ov, out uint read, (uint)timeoutMs, false))
        {
            CancelIoEx(s_hid, IntPtr.Zero);
            return null;
        }
        return read > 0 ? buf : null;
    }

    /// <summary>Submit LX=0 then LX=1 while reading, and report whether the
    /// wire delivered BOTH extremes within the window. DualSense input
    /// report 0x01 carries LX at byte 1 (0x00 left, 0xFF right).</summary>
    static bool WireTogglesWithin(HMController ctrl, int windowMs)
    {
        bool saw00 = false, sawFF = false;
        var sw = Stopwatch.StartNew();
        float x = 0f;
        var lastFlip = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < windowMs && !(saw00 && sawFF))
        {
            if (lastFlip.ElapsedMilliseconds >= 30)
            {
                x = x == 0f ? 1f : 0f;
                lastFlip.Restart();
            }
            ctrl.SubmitState(new HMGamepadState
            {
                Axes = new System.Collections.Generic.Dictionary<HMAxis, float> { [HMAxis.X] = x },
            });
            var r = HidRead(40);
            if (r == null || r[0] != 0x01) continue;
            if (r[1] <= 0x08) saw00 = true;
            if (r[1] >= 0xF7) sawFF = true;
        }
        return saw00 && sawFF;
    }

    static int Main()
    {
        Console.WriteLine("=== Stop-event resilience check (issue #38) ===");

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

        var profile = ctx.GetProfile("dualsense")
            ?? throw new InvalidOperationException("dualsense profile missing");
        Console.WriteLine("  Creating dualsense virtual controller...");
        var ctrl = ctx.CreateController(profile);

        // VIRTUAL pad only: a real DualSense on the box shares VID/PID.
        // Virtuals serve serial HM-CTL-<index>; real pads serve their MAC.
        string? path = null;
        for (int i = 0; i < 50 && path == null; i++)
        {
            path = HidDeviceEnumerator.Enumerate()
                .FirstOrDefault(d => d.VendorId == 0x054C && d.ProductId == 0x0CE6
                    && d.SerialNumberString != null
                    && d.SerialNumberString.StartsWith("HM-CTL-"))?.DevicePath;
            if (path == null) Thread.Sleep(100);
        }
        Check("virtual HID interface enumerates (VID 054C PID 0CE6, HM-CTL serial)", path != null, path ?? "");
        if (path == null) { ctrl.Dispose(); return 1; }

        s_readEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        s_hid = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        Check("HID device opens", !s_hid.IsInvalid);
        if (s_hid.IsInvalid) { ctrl.Dispose(); return 1; }

        // ── Phase 1: baseline stream ────────────────────────────────────
        Console.WriteLine("\n-- Phase 1: baseline wire movement --");
        Check("LX toggles reach the wire (baseline)", WireTogglesWithin(ctrl, 3000));

        // ── Phase 2: foreign stop-event signals must not kill the pad ───
        Console.WriteLine("\n-- Phase 2: foreign StopEvent signals (the #38 kill vector) --");
        for (int round = 1; round <= 3; round++)
        {
            IntPtr ev = OpenEventW(EVENT_MODIFY_STATE, false, @"Global\HIDMaestroStopEvent0");
            bool signaled = ev != IntPtr.Zero && SetEvent(ev);
            if (ev != IntPtr.Zero) CloseHandle(ev);
            Check($"round {round}: foreign SetEvent on Global\\HIDMaestroStopEvent0", signaled);

            // Give the worker its wake; pre-#38 it returns here and the
            // wire freezes at the last report forever.
            Thread.Sleep(300);
            Check($"round {round}: wire still toggles after foreign stop signal",
                  WireTogglesWithin(ctrl, 3000));
        }

        s_hid.Dispose();

        // ── Phase 3: real teardown still exits the worker promptly ──────
        Console.WriteLine("\n-- Phase 3: teardown path (TearingDown exit) --");
        var dispose = Stopwatch.StartNew();
        ctrl.Dispose();
        dispose.Stop();
        Check("dispose completes promptly (< 10 s)", dispose.ElapsedMilliseconds < 10000,
              $"{dispose.ElapsedMilliseconds}ms");

        bool gone = false;
        for (int i = 0; i < 50 && !gone; i++)
        {
            gone = !HidDeviceEnumerator.Enumerate()
                .Any(d => d.VendorId == 0x054C && d.ProductId == 0x0CE6
                    && d.SerialNumberString != null
                    && d.SerialNumberString.StartsWith("HM-CTL-"));
            if (!gone) Thread.Sleep(100);
        }
        Check("HID interface disappears after dispose", gone);

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }
}
