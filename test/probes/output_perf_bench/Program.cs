// Output-path performance bench (issue #34).
//
// Two measurements, printed as one line each so before/after runs diff
// cleanly:
//
//   idle-cpu   N idle controllers for S seconds. Reports total process
//              CPU during the window and the per-controller-per-second
//              share. Pre-#34 the per-controller OutputPollLoop wakes
//              125x/s on an 8 ms timeout; post-#34 it blocks on the
//              driver-signaled output event and the idle share drops to
//              (near) zero.
//
//   out-rtt    One controller. Sends N HID output reports via
//              HidD_SetOutputReport, timestamping until OutputReceived
//              fires for each. Reports median / p95 / max round-trip.
//              Pre-#34 the reader polls every 8 ms so the mean sits at
//              ~half the poll interval; post-#34 the driver signals the
//              event and the RTT drops to dispatch cost.
//
// Usage: OutputPerfBench.exe [controllers=4] [idleSeconds=30] [rttSends=200]
// Requires elevation. Exit 0 always (it is a bench, not a gate).

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
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buffer, int bufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    const uint GENERIC_RW = 0xC0000000, FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3;

    static int Main(string[] args)
    {
        int controllers = args.Length > 0 ? int.Parse(args[0]) : 4;
        int idleSeconds = args.Length > 1 ? int.Parse(args[1]) : 30;
        int rttSends    = args.Length > 2 ? int.Parse(args[2]) : 200;

        using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            if (!new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("[SKIP] requires elevation");
                return 0;
            }
        }

        Console.WriteLine($"=== output perf bench: {controllers} controllers, {idleSeconds}s idle, {rttSends} RTT sends ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        // ── idle-cpu ────────────────────────────────────────────────────
        // dualshock-4-v2 (plain HID, no companion) keeps the measurement
        // to exactly one WUDFHost + one SDK reader thread per controller.
        var profile = ctx.GetProfile("dualshock-4-v2")!;
        var ctrls = new HMController[controllers];
        for (int i = 0; i < controllers; i++)
            ctrls[i] = ctx.CreateController(profile);

        // Let creation transients settle before sampling.
        Thread.Sleep(3000);

        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        TimeSpan cpu0 = proc.TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        Thread.Sleep(idleSeconds * 1000);
        sw.Stop();
        proc.Refresh();
        double cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        double perCtrlPerSec = cpuMs / controllers / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"idle-cpu: total {cpuMs:F1} ms CPU over {sw.Elapsed.TotalSeconds:F1} s, " +
                          $"{perCtrlPerSec:F3} ms CPU per controller per second");

        for (int i = 1; i < controllers; i++) ctrls[i].Dispose();

        // ── out-rtt ─────────────────────────────────────────────────────
        // Reuse controller 0. Send DS4 output report 0x05 (rumble) and
        // time until OutputReceived surfaces it.
        var ctrl = ctrls[0];
        long tSent = 0;
        var rtts = new System.Collections.Generic.List<double>(rttSends);
        using var got = new AutoResetEvent(false);
        ctrl.OutputReceived += (_, pkt) =>
        {
            long t = Stopwatch.GetTimestamp();
            long sent = Interlocked.Read(ref tSent);
            if (sent != 0)
            {
                rtts.Add((t - sent) * 1000.0 / Stopwatch.Frequency);
                Interlocked.Exchange(ref tSent, 0);
                got.Set();
            }
        };

        string? path = null;
        for (int i = 0; i < 50 && path == null; i++)
        {
            path = HidDeviceEnumerator.Enumerate()
                .FirstOrDefault(d => d.VendorId == profile.VendorId && d.ProductId == profile.ProductId)?.DevicePath;
            if (path == null) Thread.Sleep(100);
        }
        if (path == null) { Console.WriteLine("out-rtt: HID open FAILED"); ctrl.Dispose(); return 0; }
        using var h = CreateFileW(path, GENERIC_RW, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) { Console.WriteLine("out-rtt: CreateFile FAILED"); ctrl.Dispose(); return 0; }

        var report = new byte[32];
        report[0] = 0x05;               // DS4 USB output report
        report[4] = 0xFF; report[5] = 0xFF; // rumble bytes vary per send below
        int delivered = 0;
        for (int i = 0; i < rttSends; i++)
        {
            report[4] = (byte)(1 + (i % 254));   // keep payload changing
            Interlocked.Exchange(ref tSent, Stopwatch.GetTimestamp());
            if (!HidD_SetOutputReport(h, report, report.Length))
            {
                Interlocked.Exchange(ref tSent, 0);
                continue;
            }
            if (got.WaitOne(500)) delivered++;
            else Interlocked.Exchange(ref tSent, 0);
            Thread.Sleep(20);           // pace sends; keep well under ring pressure
        }

        rtts.Sort();
        if (rtts.Count > 0)
        {
            double med = rtts[rtts.Count / 2];
            double p95 = rtts[(int)(rtts.Count * 0.95)];
            Console.WriteLine($"out-rtt: {delivered}/{rttSends} delivered, " +
                              $"median {med:F3} ms, p95 {p95:F3} ms, max {rtts[^1]:F3} ms");
        }
        else Console.WriteLine("out-rtt: no deliveries recorded");

        ctrl.Dispose();
        return 0;
    }
}
