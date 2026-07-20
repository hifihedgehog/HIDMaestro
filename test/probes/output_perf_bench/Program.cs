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
//   dual       (mode arg "dual") One xbox-360-wheel-v1 controller, BOTH
//              ring producers driven concurrently: XInputSetState rumble
//              through the XUSB companion and HidD_SetFeature (PID
//              Create New Effect 0x11) through the main HID driver, 300
//              sends each at ~1 ms pacing. Asserts full delivery per
//              source. Locks the multi-producer reservation protocol
//              (audit of #34): the pre-fix local-counter scheme could
//              mint colliding sequence numbers and silently drop one
//              producer's packet.
//
// Usage: OutputPerfBench.exe [controllers=4] [idleSeconds=30] [rttSends=200]
//        OutputPerfBench.exe dual
// Requires elevation. Exit: dual mode exits 1 on delivery loss (it IS a
// gate); the timing modes exit 0 always (they are benches).

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

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int bufferLength);

    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, byte[] caps);

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_VIBRATION { public ushort LeftMotor; public ushort RightMotor; }

    [DllImport("xinput1_4.dll")]
    static extern uint XInputSetState(uint index, ref XINPUT_VIBRATION vib);

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE { public uint PacketNumber; public ulong Pad1; public ulong Pad2; }

    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetState(uint index, out XINPUT_STATE state);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    const uint GENERIC_RW = 0xC0000000, FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3;

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "dual") return DualProducerGate();

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

    // Dual-producer delivery gate (audit of #34). Drives the XUSB
    // companion (XInputSetState) and the main HID driver (HidD_SetFeature
    // 0x11, PID Create New Effect) into the SAME output ring concurrently
    // and asserts every packet from both sources surfaces exactly once.
    static int DualProducerGate()
    {
        const int SendsPerSource = 300;
        Console.WriteLine("=== dual-producer ring gate: custom Xbox PID wheel, XInput + PID feature concurrently ===");

        using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            if (!new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("[SKIP] requires elevation");
                return 0;
            }
        }

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        // No SHIPPED companion profile's HID child declares host-writable
        // reports (verified empirically: xbox-360-wheel-v1 caps report
        // Output/FeatureReportByteLength = 0), so the dual-producer
        // scenario is reachable only through the custom-profile surface.
        // Build exactly that: an 045E custom wheel (id/name carry "xbox"
        // so IsXboxBranded admits the XUSB companion) whose descriptor
        // carries a PID FFB block (feature 0x11 + output set) so the MAIN
        // driver publishes feature/output packets while the XUSB
        // companion publishes XInput rumble into the same ring.
        var descBuilder = new HidDescriptorBuilder()
            .Joystick()
            .AddStick("Left", bits: 16)
            .AddTrigger("Left", bits: 8)
            .AddTrigger("Right", bits: 8)
            .AddButtons(10)
            .AddPidFfbBlock();
        var profile = new HMProfileBuilder()
            .Id("xbox-dual-gate-wheel")
            .Name("Xbox Dual Gate Wheel (bench)")
            .Vendor("Bench")
            .Vid(0x045E).Pid(0xB719)
            .ProductString("Xbox Dual Gate Wheel")
            .Type("wheel")
            .Connection("usb")
            .FromDescriptorBuilder(descBuilder)
            .Build();
        using var ctrl = ctx.CreateController(profile);

        int gotXInput = 0, gotFeature = 0;
        ctrl.OutputReceived += (_, pkt) =>
        {
            if (pkt.Source == HMOutputSource.XInput) Interlocked.Increment(ref gotXInput);
            else Interlocked.Increment(ref gotFeature);   // HidFeature or HidOutput lane
        };

        // Publish the PID pool so the feature lane mirrors a real
        // DirectInput FFB session (the driver publishes packets either way).
        ctrl.PublishPidPool(0xFFFF, 16, true, false);

        uint slot = 0xFFFFFFFF;
        for (int i = 0; i < 50 && slot == 0xFFFFFFFF; i++)
        {
            for (uint s = 0; s < 4; s++)
                if (XInputGetState(s, out _) == 0) { slot = s; break; }
            if (slot == 0xFFFFFFFF) Thread.Sleep(100);
        }
        string? path = null;
        for (int i = 0; i < 50 && path == null; i++)
        {
            path = HidDeviceEnumerator.Enumerate()
                .FirstOrDefault(d => d.VendorId == profile.VendorId && d.ProductId == profile.ProductId)?.DevicePath;
            if (path == null) Thread.Sleep(100);
        }
        if (slot == 0xFFFFFFFF || path == null)
        {
            Console.WriteLine($"[FAIL] setup: xinput slot={slot} hidPath={(path == null ? "absent" : "ok")}");
            return 1;
        }
        using var h = CreateFileW(path, GENERIC_RW, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) { Console.WriteLine("[FAIL] HID open"); return 1; }

        var tA = new Thread(() =>
        {
            var vib = new XINPUT_VIBRATION();
            for (int i = 0; i < SendsPerSource; i++)
            {
                vib.LeftMotor = (ushort)(1 + (i % 65000));
                vib.RightMotor = (ushort)(65000 - (i % 65000));
                XInputSetState(slot, ref vib);
                Thread.Sleep(1);
            }
        });
        // HidClass rejects writes whose buffer is not EXACTLY the declared
        // report byte length. HIDP_CAPS layout: Usage@0 UsagePage@2
        // InputReportByteLength@4 OutputReportByteLength@6
        // FeatureReportByteLength@8.
        int featLen = 0, outLen = 0;
        if (HidD_GetPreparsedData(h, out IntPtr pp))
        {
            var caps = new byte[64];
            HidP_GetCaps(pp, caps);
            outLen  = BitConverter.ToUInt16(caps, 6);
            featLen = BitConverter.ToUInt16(caps, 8);
            HidD_FreePreparsedData(pp);
        }
        Console.WriteLine($"  caps: OutputReportByteLength={outLen}, FeatureReportByteLength={featLen}");

        // Main-driver lane: prefer a feature write (PID Create New Effect
        // 0x11); when the wheel's descriptor declares different RIDs, fall
        // back to sweeping output-report IDs until one lands. Both paths
        // publish through the main HID driver's PublishOutput.
        int sentFeature = 0;
        var tB = new Thread(() =>
        {
            byte workingRid = 0; bool useOutput = false;
            if (featLen > 1)
            {
                var probeBuf = new byte[featLen];
                probeBuf[0] = 0x11;
                if (HidD_SetFeature(h, probeBuf, probeBuf.Length)) { workingRid = 0x11; }
            }
            if (workingRid == 0 && outLen > 1)
            {
                var probeBuf = new byte[outLen];
                for (byte rid = 0x01; rid <= 0x20 && workingRid == 0; rid++)
                {
                    probeBuf[0] = rid;
                    if (HidD_SetOutputReport(h, probeBuf, probeBuf.Length)) { workingRid = rid; useOutput = true; }
                }
            }
            if (workingRid == 0) { Console.WriteLine("  [lane-b] no accepted feature or output RID"); return; }
            Console.WriteLine($"  [lane-b] using {(useOutput ? "output" : "feature")} RID 0x{workingRid:X2}");
            sentFeature++;                      // the successful probe published too

            var buf = new byte[useOutput ? outLen : featLen];
            buf[0] = workingRid;
            for (int i = 1; i < SendsPerSource; i++)
            {
                if (buf.Length > 1) buf[1] = (byte)(1 + (i % 254));
                bool ok = useOutput ? HidD_SetOutputReport(h, buf, buf.Length)
                                    : HidD_SetFeature(h, buf, buf.Length);
                if (ok) sentFeature++;
                Thread.Sleep(1);
            }
        });
        tA.Start(); tB.Start();
        tA.Join(); tB.Join();
        Thread.Sleep(1500);                    // let the reader drain the tail

        Console.WriteLine($"dual: XInput {gotXInput}/{SendsPerSource} (>= asserted; slot-claim can add one), " +
                          $"HidFeature {gotFeature}/{sentFeature} sent-and-delivered of {SendsPerSource} attempts");
        bool pass = gotXInput >= SendsPerSource && sentFeature > 0 && gotFeature == sentFeature;

        // Staggered phase (the divergence case): a burst on ONE producer
        // first, THEN the other. The old local-counter scheme survives
        // lockstep interleaving (counters advance together) but fails
        // exactly here: after the companion drives Head far ahead, the
        // main driver's stale counter would mint low sequence numbers
        // and regress Head, and the reader never sees its packets.
        int burstBase = gotXInput;
        var vib2 = new XINPUT_VIBRATION();
        for (int i = 0; i < 100; i++)
        {
            vib2.LeftMotor = (ushort)(100 + i);
            vib2.RightMotor = (ushort)(200 + i);
            XInputSetState(slot, ref vib2);
            Thread.Sleep(1);
        }
        Thread.Sleep(300);
        int featBase = gotFeature, sentStagger = 0;
        var buf2 = new byte[featLen > 1 ? featLen : 2];
        buf2[0] = 0x11;
        for (int i = 0; i < 100; i++)
        {
            buf2[1] = (byte)(1 + i);
            if (HidD_SetFeature(h, buf2, buf2.Length)) sentStagger++;
            Thread.Sleep(1);
        }
        Thread.Sleep(1500);
        int gotStagger = gotFeature - featBase;

        // Ground truth: raw ring header + slot SeqNos straight from the
        // shared section (no parser in the way), plus the devnode's bound
        // driver version. Distinguishes stale-binary from protocol-logic
        // hypotheses without narrative.
        IntPtr view = SharedMemoryIO.EnsureOutputMapping(ctrl.Index);
        uint headRaw = (uint)Marshal.ReadInt32(view, 0);
        Console.Write($"  [raw] Head={headRaw} slotSeqs:");
        for (int sl = 0; sl < 12; sl++)
        {
            int slotBase = 8 + sl * 264;   // header 8, slot stride 264 (see SharedMemoryIO)
            Console.Write($" {(uint)Marshal.ReadInt32(view, slotBase)}");
        }
        Console.WriteLine();
        Console.WriteLine($"stagger: XInput burst then {sentStagger} features, delivered {gotStagger}/{sentStagger}");
        pass &= sentStagger > 0 && gotStagger == sentStagger;
        Console.WriteLine(pass
            ? "=== PASS: no cross-producer packet loss ==="
            : "=== FAIL: packets lost (multi-producer sequence collision) ===");
        return pass ? 0 : 1;
    }
}
