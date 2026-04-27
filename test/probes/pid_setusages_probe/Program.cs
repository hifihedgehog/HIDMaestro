#nullable enable
// HIDMaestro PID HidP_SetUsages roundtrip probe (S26).
//
// Issue #16 dynamic test: pid.dll's PID_DownloadEffect calls HidP_SetUsages
// three times to compose the Set Effect Output report. If any returns
// HIDP_STATUS_USAGE_NOT_FOUND, dispatch fails before the kernel IOCTL is
// ever issued, and pid.dll AVs in cleanup. v1.1.39's driver instrumentation
// proved the kernel never sees IOCTL_HID_WRITE_REPORT for Set Effect — so
// the failure is in pid.dll's user-mode HidP_SetUsages call, NOT in the
// driver / kernel HID stack.
//
// This probe deploys a virtual using PadForge's Custom profile descriptor
// (transcribed from PadForge's HMaestroFfbDescriptor.Build() — Joystick TLC
// variant since vJoy uses Joystick and we want to A/B against PadForge's
// Gamepad TLC if needed) and exercises HidP_SetUsages for every PID Output
// usage pid.dll would set. Any failure prints the specific usage that
// HidClass's preparsed data couldn't resolve — the same one pid.dll
// silently fails on.

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using HIDMaestro;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    const ushort ProbeVid = 0xBEEF;
    const ushort ProbePid = 0xF099;

    // PID 1.0 Usage Page
    const ushort UP_PID = 0x000F;
    const ushort UP_GENERIC_DESKTOP = 0x0001;
    const ushort UP_ORDINAL = 0x000A;

    // HidP_SetUsages: ReportType
    const int HidP_Output = 1;
    const int HidP_Feature = 2;

    // NTSTATUS values from hidpi.h
    const int HIDP_STATUS_SUCCESS = 0x00110000;
    const int HIDP_STATUS_USAGE_NOT_FOUND = unchecked((int)0xC0110004);
    const int HIDP_STATUS_INVALID_REPORT_TYPE = unchecked((int)0xC0110002);
    const int HIDP_STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0110007);

    static int Main(string[] args)
    {
        Console.WriteLine("=== HIDMaestro PID HidP_SetUsages roundtrip probe (S26) ===");

        // CLI flags:
        //   --gamepad      Use Gamepad TLC (0x05) instead of Joystick (0x04).
        //   --keep-alive   Skip the test phases. Just create the virtual and
        //                  wait for the user to press a key. Useful for
        //                  manual testing with FfbTest or any DI consumer.
        bool joystickTlc = !args.Any(a => a.Equals("--gamepad", StringComparison.OrdinalIgnoreCase));
        bool keepAlive   =  args.Any(a => a.Equals("--keep-alive", StringComparison.OrdinalIgnoreCase));
        // --padforge-profile: use HidDescriptorBuilder to assemble a profile
        // that mirrors PadForge's BuildCustomProfile EXACTLY (4 sticks + 2
        // triggers + hat + 11 buttons + Report ID 0x01 prefix + the FFB
        // block). Reproduces PadForge's setup vs the simpler default
        // probe descriptor, to isolate input-report-shape divergence.
        bool padForgeProfile = args.Any(a => a.Equals("--padforge-profile", StringComparison.OrdinalIgnoreCase));
        // --shared-param: pass sharedParameterBlocks=true on PublishPidPool
        // (PadForge's default; my probe's default is false). Pool
        // MemoryManagement byte goes from 0x01 to 0x03.
        bool sharedParam = args.Any(a => a.Equals("--shared-param", StringComparison.OrdinalIgnoreCase));
        // --pump-input: spawn a background thread that calls SubmitState
        // at ~1 kHz, mirroring PadForge.exe's input pump runtime
        // behavior. The probe-creates-virtual-then-spawns-FfbTest test is
        // otherwise identical to a PadForge run except for this pump.
        bool pumpInput = args.Any(a => a.Equals("--pump-input", StringComparison.OrdinalIgnoreCase));

        byte[] descriptor;
        int inputReportSize;
        if (padForgeProfile)
        {
            // Mirror PadForge's HMaestroProfileCatalog.BuildCustomProfile exactly.
            var dB = new HidDescriptorBuilder().Joystick();
            dB.AddRaw(new byte[] { 0x85, 0x01 });
            dB.AddStick("Left", 16)
              .AddStick("Right", 16)
              .AddTrigger("Left", 16)
              .AddTrigger("Right", 16)
              .AddHat()
              .AddButtons(11)
              .AddRaw(BuildFfbBlock());
            descriptor = dB.Build();
            inputReportSize = dB.InputReportByteSize + 1;
            Console.WriteLine($"  Descriptor size: {descriptor.Length} bytes (PadForge profile shape, Joystick TLC, input={inputReportSize}B)");
        }
        else
        {
            descriptor = BuildPidDescriptor(joystickTlc);
            inputReportSize = 6;
            Console.WriteLine($"  Descriptor size: {descriptor.Length} bytes ({(joystickTlc ? "Joystick" : "Gamepad")} TLC)");
        }

        var profile = new HMProfileBuilder()
            .Id("pid-setusages-probe")
            .Name("PID SetUsages Probe")
            .Vendor("HIDMaestro")
            .Vid(ProbeVid).Pid(ProbePid)
            .ProductString("HIDMaestro PID SetUsages Probe")
            .ManufacturerString("HIDMaestro")
            .Type("joystick")
            .Connection("usb")
            .Descriptor(descriptor)
            .InputReportSize(inputReportSize)
            .Build();

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        Console.Write("  Creating virtual... ");
        using var ctrl = ctx.CreateController(profile);
        Console.WriteLine("OK");

        // ── --keep-alive mode ──
        // Skip the test phases. Just hold the virtual up and wait for the
        // user to press a key. Useful for manual testing — run any
        // DirectInput PID FFB consumer (FfbTest, your own game, joy.cpl,
        // etc.) against this virtual and exit when done.
        if (keepAlive)
        {
            // Publish minimal Pool + State so the device announces FFB
            // capability to the host. Without these, dinput's discovery
            // GetFeature(Pool) returns STATUS_NO_SUCH_DEVICE and dinput
            // concludes "device exists, no FFB" per the vJoy convention.
            ctrl.PublishPidPool(ramPoolSize: 0xFFFF, simultaneousEffectsMax: 16,
                                deviceManagedPool: true, sharedParameterBlocks: sharedParam);
            ctrl.PublishPidState(effectBlockIndex: 0,
                PidStateFlags.ActuatorsEnabled | PidStateFlags.ActuatorPower);

            Console.WriteLine();
            Console.WriteLine("  Virtual is live and FFB-enabled.");
            Console.WriteLine($"    VID/PID: 0x{ProbeVid:X4}/0x{ProbePid:X4}");
            Console.WriteLine($"    Product: HIDMaestro PID SetUsages Probe");
            Console.WriteLine($"    TLC:     {(joystickTlc ? "Joystick (0x04)" : "Gamepad (0x05)")}");
            Console.WriteLine();
            Console.WriteLine("  Test now with any DirectInput PID FFB consumer:");
            Console.WriteLine("    - FfbTest.exe (in this probe's bin/.../FfbTest.exe)");
            Console.WriteLine("    - joy.cpl (Properties → Test for input only)");
            Console.WriteLine("    - your own game / FFB harness");
            Console.WriteLine();
            Console.WriteLine("  Driver trace: C:\\Windows\\Temp\\hidmaestro-driver-trace.log");
            Console.WriteLine();
            Console.Write("  Press ENTER to dispose the virtual and exit... ");
            try { Console.ReadLine(); } catch { /* ignore if stdin closed */ }
            Console.WriteLine("Disposing.");
            return 0;
        }

        // Publish basic Pool + State so the device announces FFB capability
        // to the host. Required before any DInput consumer (FfbTest) tries
        // to enumerate effects. Done HERE — before any other HID I/O — to
        // mirror what the working --keep-alive path does. The HidP probe
        // phase (the many SetUsages/SetUsageValue calls + WriteFile Set
        // Effect) is deferred to AFTER the FfbTest run, because that pre-
        // FfbTest HID activity proved to corrupt device state in some
        // environments (FfbTest CreateEffect AVs in pid.dll cleanup).
        ctrl.PublishPidPool(ramPoolSize: 0xFFFF, simultaneousEffectsMax: 16,
                            deviceManagedPool: true, sharedParameterBlocks: sharedParam);
        ctrl.PublishPidState(effectBlockIndex: 0,
            PidStateFlags.ActuatorsEnabled | PidStateFlags.ActuatorPower);

        // ── Output burst counter ──
        // Subscribe to OutputReceived to verify the v1.1.40 ring buffer
        // doesn't coalesce back-to-back writes. The post-FfbTest burst
        // phase below writes N Output reports in tight succession; the
        // counter here must equal N when the SDK poll loop drains.
        var outputCounts = new System.Collections.Concurrent.ConcurrentDictionary<byte, int>();
        int totalOutputPackets = 0;
        ctrl.OutputReceived += (_, pkt) =>
        {
            outputCounts.AddOrUpdate(pkt.ReportId, 1, (_, n) => n + 1);
            System.Threading.Interlocked.Increment(ref totalOutputPackets);
        };

        // ── Optional: input-pump thread mirroring PadForge.exe ──
        var pumpCts = new System.Threading.CancellationTokenSource();
        System.Threading.Thread? pumpThread = null;
        if (pumpInput)
        {
            Console.WriteLine("  Starting input pump thread (~1 kHz SubmitState)");
            pumpThread = new System.Threading.Thread(() =>
            {
                var rng = new Random();
                while (!pumpCts.IsCancellationRequested)
                {
                    var s = new HMGamepadState
                    {
                        LeftStickX = (float)(rng.NextDouble() * 2 - 1),
                        LeftStickY = (float)(rng.NextDouble() * 2 - 1),
                        Buttons = ((rng.Next() & 1) != 0) ? HMButton.A : HMButton.None,
                    };
                    try { ctrl.SubmitState(in s); } catch { break; }
                    try { System.Threading.Thread.Sleep(1); } catch { break; }
                }
            }) { IsBackground = true, Name = "PadForgeInputPumpMimic" };
            pumpThread.Start();
            // Let the pump get going.
            System.Threading.Thread.Sleep(500);
        }

        // ── FfbTest first — clean device, no prior HID I/O ──
        // The canonical regression test for issue #16. If FfbTest's
        // CreateEffect succeeds here, PID FFB is empirically working.
        int failuresFromFfbTest = RunFfbTest();
        pumpCts.Cancel();
        pumpThread?.Join(2000);

        // ── HidP introspection probe (diagnostic, post-FfbTest) ──
        // Now exercise HidP_SetUsages, HidP_SetUsageValue, and a
        // WriteFile(Set Effect) directly. These are useful for debugging
        // the descriptor's preparsed-data shape but are NOT a regression
        // signal. If the FfbTest above succeeded, the HidP probe results
        // are informational.
        SafeFileHandle? hid = OpenHmHidByVidPid(ProbeVid, ProbePid);
        if (hid == null || hid.IsInvalid)
        {
            Console.Error.WriteLine("HidP probe: could not open HID handle (skipping)");
            return failuresFromFfbTest > 0 ? 1 : 0;
        }

        if (!HidD_GetPreparsedData(hid, out IntPtr pp))
        {
            Console.Error.WriteLine($"HidP probe: HidD_GetPreparsedData failed (Win32={Marshal.GetLastWin32Error()}) — skipping");
            hid.Dispose();
            return failuresFromFfbTest > 0 ? 1 : 0;
        }
        Console.WriteLine($"  PreparsedData: 0x{pp.ToInt64():X16}");

        try
        {
            // Get caps to learn the Set Effect Output report's declared length
            var caps = new HIDP_CAPS();
            int rc = HidP_GetCaps(pp, ref caps);
            if (rc != HIDP_STATUS_SUCCESS)
            {
                Console.Error.WriteLine($"FAIL: HidP_GetCaps NTSTATUS=0x{rc:X8}");
                return 4;
            }
            Console.WriteLine($"  Caps: usage=0x{caps.UsagePage:X4}:0x{caps.Usage:X4} " +
                              $"input={caps.InputReportByteLength} " +
                              $"output={caps.OutputReportByteLength} " +
                              $"feature={caps.FeatureReportByteLength}");

            int reportLen = caps.OutputReportByteLength;
            if (reportLen < 2)
            {
                Console.Error.WriteLine($"FAIL: OutputReportByteLength={reportLen} too small");
                return 5;
            }

            byte[] report = new byte[reportLen];
            int failures = 0;

            // ── Test each PID Output usage pid.dll would exercise ──
            // Format: TestSetUsages("name", reportType, usagePage, linkCollection, usages[])
            // Initialize report for ID 0x11 (Set Effect) before each test.

            // Common: initialize Set Effect Output report (ID 0x11)
            void Init() {
                Array.Clear(report, 0, reportLen);
                int r = HidP_InitializeReportForID(HidP_Output, 0x11, pp, report, (uint)reportLen);
                if (r != HIDP_STATUS_SUCCESS)
                    Console.Error.WriteLine($"  WARN: InitializeReportForID(Output, 0x11) NTSTATUS=0x{r:X8}");
            }

            // Test 1 — Effect Block Index (Usage 0x22, 8-bit Variable Output value)
            Init();
            failures += AssertSetUsageValue("EffectBlockIndex (0x0F:0x22) val=1",
                HidP_Output, UP_PID, 0, 0x22, 1u, pp, report, reportLen);

            // Test 2 — Effect Type ARRAY. Each iteration resets the buffer
            // first; ARRAY items only hold one selector at a time per Report
            // Count, so without a reset HidP reports BUFFER_TOO_SMALL when
            // we try to add a second.
            foreach (ushort effectUsage in new ushort[] { 0x26, 0x27, 0x30, 0x31, 0x32, 0x33, 0x34, 0x40, 0x41, 0x42, 0x43, 0x29 })
            {
                Init();
                ushort[] u = { effectUsage };
                uint n = 1;
                failures += AssertSetUsages($"EffectType ARRAY (0x0F:0x{effectUsage:X2})",
                    HidP_Output, UP_PID, 0, u, ref n, pp, report, reportLen);
            }

            // Test 3 — Direction Enable (0x56) — disassembly's "call #1" candidate
            Init();
            ushort[] u56 = { 0x56 };
            uint n56 = 1;
            failures += AssertSetUsages("DirectionEnable (0x0F:0x56)",
                HidP_Output, UP_PID, 0, u56, ref n56, pp, report, reportLen);

            // Test 4 — Axes Enable: Usage X (0x01:0x30) and Y (0x01:0x31).
            // disassembly's "call #2" candidate (per-axis loop).
            Init();
            ushort[] u30 = { 0x30 };
            uint n30 = 1;
            failures += AssertSetUsages("AxesEnable X (0x01:0x30)",
                HidP_Output, UP_GENERIC_DESKTOP, 0, u30, ref n30, pp, report, reportLen);
            Init();
            ushort[] u31 = { 0x31 };
            uint n31 = 1;
            failures += AssertSetUsages("AxesEnable Y (0x01:0x31)",
                HidP_Output, UP_GENERIC_DESKTOP, 0, u31, ref n31, pp, report, reportLen);

            // Test 5 — UsageValue fields (Duration 0x50, Trigger Repeat 0x54,
            // Sample Period 0x51, Start Delay 0xA7, Gain 0x52, Trigger Button 0x53)
            // These use HidP_SetUsageValue, not SetUsages.
            Init();
            failures += AssertSetUsageValue("Duration (0x0F:0x50) val=1000",
                HidP_Output, UP_PID, 0, 0x50, 1000u, pp, report, reportLen);
            failures += AssertSetUsageValue("TriggerRepeat (0x0F:0x54) val=0",
                HidP_Output, UP_PID, 0, 0x54, 0u, pp, report, reportLen);
            failures += AssertSetUsageValue("SamplePeriod (0x0F:0x51) val=10",
                HidP_Output, UP_PID, 0, 0x51, 10u, pp, report, reportLen);
            failures += AssertSetUsageValue("StartDelay (0x0F:0xA7) val=0",
                HidP_Output, UP_PID, 0, 0xA7, 0u, pp, report, reportLen);
            failures += AssertSetUsageValue("Gain (0x0F:0x52) val=128",
                HidP_Output, UP_PID, 0, 0x52, 128u, pp, report, reportLen);
            failures += AssertSetUsageValue("TriggerButton (0x0F:0x53) val=1",
                HidP_Output, UP_PID, 0, 0x53, 1u, pp, report, reportLen);

            // Test 6 — Direction Ordinal Instance 1 + 2 (0x000A:0x0001, 0x0002).
            // These are 16-bit Variable Output values, so HidP_SetUsageValue
            // is the right API (not SetUsages — which is for Buttons/Arrays).
            Init();
            failures += AssertSetUsageValue("Direction Ord Inst 1 (0x0A:0x0001) val=180",
                HidP_Output, UP_ORDINAL, 0, 0x0001, 180u, pp, report, reportLen);
            failures += AssertSetUsageValue("Direction Ord Inst 2 (0x0A:0x0002) val=0",
                HidP_Output, UP_ORDINAL, 0, 0x0002, 0u, pp, report, reportLen);
            failures += AssertSetUsageValue("Type Specific Block Offset Ord Inst 1 (0x0A:0x0001) val=0",
                HidP_Output, UP_ORDINAL, 0, 0x0001, 0u, pp, report, reportLen);

            // Final: HidD_SetFeature with the assembled report bytes — closing
            // the loop on whether the Output report writes through HidClass.
            // (Independent of HidP_SetUsages success; even with composition
            // errors, the buffer is valid bytes.)
            Init();
            // Manually populate enough bytes that HidClass accepts the buffer
            // length. The driver receives what HidClass forwards.
            byte[] outReport = new byte[reportLen];
            outReport[0] = 0x11; // RID
            outReport[1] = 1;    // EBI = 1
            outReport[2] = 1;    // Effect Type = ConstantForce (0x26 → array index 1 within the LM=1, LX=12 enum)
            // Set Duration = 1000 ms (16-bit at offset 3..4)
            outReport[3] = 0xE8;
            outReport[4] = 0x03;
            // Trigger Repeat, Sample Period, Start Delay = 0 (offsets 5..10)
            // Gain = 0xFF (offset 11)
            if (reportLen > 11) outReport[11] = 0xFF;

            uint written;
            bool wrote = WriteFile(hid, outReport, (uint)reportLen, out written, IntPtr.Zero);
            int writeErr = Marshal.GetLastWin32Error();
            if (wrote)
                Console.WriteLine($"  [WriteFile Set Effect] OK ({written} bytes written)");
            else
            {
                Console.WriteLine($"  [WriteFile Set Effect] FAIL (Win32={writeErr})");
                failures++;
            }

            // ── v1.1.40 ring-buffer roundtrip test ──
            // Issue #16 ask 3: verify back-to-back Output reports all reach
            // the consumer's OutputReceived. pid.dll writes Set Effect →
            // Set Constant Force → Effect Operation Start within 1-3 ms;
            // the pre-1.1.40 single-slot channel coalesced these to one
            // packet per ~8 ms poll cycle. v1.1.40 ring should preserve
            // every write up to RING_SLOTS (64) per drain.
            //
            // Test: spam K bursts of 3 distinct Report IDs (0x11, 0x15,
            // 0x1A) — total 3K writes — then sleep enough for the SDK's
            // poll loop to drain (≥16 ms = 2 poll cycles, plenty of
            // headroom). Assert OutputReceived count == 3K, broken down
            // by Report ID.
            const int kBursts = 8;          // 8 × 3 = 24 packets, well under ring depth
            const int kBytesPerWrite = 22;  // typical PID Output payload size
            byte[] burstBuf = new byte[kBytesPerWrite];
            outputCounts.Clear();
            System.Threading.Interlocked.Exchange(ref totalOutputPackets, 0);
            int writeFailures = 0;
            for (int i = 0; i < kBursts; i++)
            {
                foreach (byte rid in new byte[] { 0x11, 0x15, 0x1A })
                {
                    burstBuf[0] = rid;
                    burstBuf[1] = (byte)(i + 1);  // mark with burst index for traceability
                    if (!WriteFile(hid, burstBuf, (uint)kBytesPerWrite, out _, IntPtr.Zero))
                        writeFailures++;
                }
            }
            // Drain window. SDK polls every 8 ms; 5 cycles (40 ms) gives
            // comfortable headroom even on a busy machine.
            System.Threading.Thread.Sleep(80);
            int total = totalOutputPackets;
            int expected = kBursts * 3;
            int got11 = outputCounts.TryGetValue((byte)0x11, out int n11) ? n11 : 0;
            int got15 = outputCounts.TryGetValue((byte)0x15, out int n15) ? n15 : 0;
            int got1A = outputCounts.TryGetValue((byte)0x1A, out int n1A) ? n1A : 0;

            // Diagnostic via Win32 OpenFileMapping (MemoryMappedFile.OpenExisting
            // requires more permissions than the section's SDDL grants).
            int driverHead = -1;
            string slotDump = "";
            IntPtr secH2 = OpenFileMappingW2(0x4 /*FILE_MAP_READ*/, false, @"Global\HIDMaestroOutput0");
            if (secH2 == IntPtr.Zero)
                Console.WriteLine($"  OpenFileMappingW2 failed Win32={Marshal.GetLastWin32Error()}");
            if (secH2 != IntPtr.Zero)
            {
                IntPtr secView = MapViewOfFile2(secH2, 0x4, 0, 0, (UIntPtr)16904);
                if (secView != IntPtr.Zero)
                {
                    driverHead = Marshal.ReadInt32(secView, 0);
                    var sb = new System.Text.StringBuilder();
                    for (int s = 0; s < Math.Min(5, driverHead); s++)
                    {
                        int @base = 8 + s * 264;
                        int seq = Marshal.ReadInt32(secView, @base);
                        byte src = Marshal.ReadByte(secView, @base + 4);
                        byte rid = Marshal.ReadByte(secView, @base + 5);
                        sb.Append($"slot[{s}]:seq={seq},src={src},rid=0x{rid:X2} ");
                    }
                    slotDump = sb.ToString();
                    UnmapViewOfFile2(secView);
                }
                CloseHandle2(secH2);
            }
            if (slotDump.Length > 0)
                Console.WriteLine($"  Direct section read: {slotDump}");
            Console.WriteLine($"  [Ring burst {kBursts}×3] WriteFile failures={writeFailures} " +
                $"driver Head={driverHead} " +
                $"OutputReceived total={total} expected={expected} " +
                $"(rid 0x11={got11}, 0x15={got15}, 0x1A={got1A})");
            if (total < expected)
            {
                Console.WriteLine($"  [Ring burst] FAIL: lost {expected - total} packet(s)");
                failures++;
            }

            // FfbTest already ran above (before the HidP probe phase). The
            // HidP probe results above are diagnostic only — the regression
            // bar is FfbTest. If FfbTest passed, the probe passes regardless
            // of HidP-side warnings; if FfbTest failed, the probe fails.
            if (failuresFromFfbTest == 0)
            {
                Console.WriteLine($"=== PASS — FfbTest succeeded; HidP probe diagnostic failures: {failures} ===");
                return 0;
            }
            Console.WriteLine($"=== FAIL: FfbTest failed (exit code from --probes-only != 0); HidP probe failures: {failures} ===");
            return 1;
        }
        finally
        {
            if (pp != IntPtr.Zero) HidD_FreePreparsedData(pp);
            if (!hid.IsClosed) hid.Dispose();
        }
    }

    /// <summary>
    /// Spawns FfbTest.exe --probes-only against the live virtual.
    /// FfbTest's --probes-only mode runs the auto-probe loops and exits
    /// cleanly; exit code 0 = ConstantForce CreateEffect succeeded,
    /// non-zero = it failed. No interactive ReadKey, no spurious
    /// 0xE0434352 .NET unhandled exceptions.
    /// </summary>
    static int RunFfbTest()
    {
        Console.WriteLine();
        Console.WriteLine("--- FfbTest.exe --probes-only ---");

        string ffbTest = Path.Combine(AppContext.BaseDirectory, "FfbTest.exe");
        if (!File.Exists(ffbTest))
        {
            Console.WriteLine($"  FfbTest.exe not found at {ffbTest}");
            return 99;
        }

        var psi = new System.Diagnostics.ProcessStartInfo(ffbTest)
        {
            Arguments = "--probes-only",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var sb = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine("[err] " + e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        bool exited = proc.WaitForExit(20000);
        if (!exited)
        {
            Console.WriteLine("  FfbTest hung past 20s — killing");
            try { proc.Kill(entireProcessTree: true); } catch { }
            proc.WaitForExit(2000);
        }
        proc.WaitForExit();
        lock (sb) Console.WriteLine(sb.ToString());
        Console.WriteLine($"  FfbTest exit code: 0x{proc.ExitCode:X8} ({proc.ExitCode})");
        return proc.ExitCode;
    }

    static int AssertSetUsages(string label, int reportType, ushort usagePage,
        ushort linkCollection, ushort[] usages, ref uint usageCount,
        IntPtr preparsed, byte[] report, int reportLen)
    {
        int r = HidP_SetUsages(reportType, usagePage, linkCollection, usages,
            ref usageCount, preparsed, report, (uint)reportLen);
        if (r == HIDP_STATUS_SUCCESS)
        {
            Console.WriteLine($"  [{label}] OK");
            return 0;
        }
        string name = r switch
        {
            unchecked((int)0xC0110004) => "USAGE_NOT_FOUND",
            unchecked((int)0xC0110002) => "INVALID_REPORT_TYPE",
            unchecked((int)0xC0110007) => "BUFFER_TOO_SMALL",
            unchecked((int)0xC0110008) => "INTERNAL_ERROR",
            unchecked((int)0xC0110001) => "INVALID_PREPARSED_DATA",
            _                          => "?",
        };
        Console.WriteLine($"  [{label}] FAIL NTSTATUS=0x{r:X8} ({name})");
        return 1;
    }

    static int AssertSetUsageValue(string label, int reportType, ushort usagePage,
        ushort linkCollection, ushort usage, uint value,
        IntPtr preparsed, byte[] report, int reportLen)
    {
        int r = HidP_SetUsageValue(reportType, usagePage, linkCollection, usage, value,
            preparsed, report, (uint)reportLen);
        if (r == HIDP_STATUS_SUCCESS)
        {
            Console.WriteLine($"  [{label}] OK");
            return 0;
        }
        string name = r switch
        {
            unchecked((int)0xC0110004) => "USAGE_NOT_FOUND",
            unchecked((int)0xC0110002) => "INVALID_REPORT_TYPE",
            unchecked((int)0xC0110007) => "BUFFER_TOO_SMALL",
            _                          => "?",
        };
        Console.WriteLine($"  [{label}] FAIL NTSTATUS=0x{r:X8} ({name})");
        return 1;
    }

    // PadForge's HMaestroFfbDescriptor.Build() output, transcribed verbatim
    // (Set Effect, Set Envelope, Set Condition, Set Periodic, Set Constant,
    //  Set Ramp, Custom Force, Download Sample, Effect Operation, Block Free,
    //  Device Control, Device Gain + 4 Feature reports). With either Joystick
    //  or Gamepad TLC.
    static byte[] BuildPidDescriptor(bool joystickTlc)
    {
        var d = new System.Collections.Generic.List<byte>(640);

        // Top-level Application Collection
        d.AddRange(new byte[] { 0x05, 0x01 });                 // Usage Page Generic Desktop
        d.AddRange(new byte[] { 0x09, joystickTlc ? (byte)0x04 : (byte)0x05 }); // Joystick or Gamepad
        d.AddRange(new byte[] { 0xA1, 0x01 });                 // Collection Application

        // Input report: X+Y axes (16-bit) + 8 buttons (Report ID 0x01).
        // The X/Y axes match PadForge's Custom profile so FfbTest detects
        // them as ForceFeedbackActuator candidates (matched against the
        // Set Effect Output report's Axes Enable subcollection usages
        // 0x30/0x31). Without axes, FfbTest skips CreateEffect entirely.
        d.AddRange(new byte[] { 0x85, 0x01 });                 // Report ID 1
        d.AddRange(new byte[] { 0x09, 0x01, 0xA1, 0x00 });     // Usage Pointer, Collection Physical
        d.AddRange(new byte[] { 0x09, 0x30, 0x09, 0x31 });     // Usage X, Y
        d.AddRange(new byte[] { 0x16, 0x00, 0x80, 0x26, 0xFF, 0x7F });  // LMin -32768, LMax 32767
        d.AddRange(new byte[] { 0x36, 0x00, 0x80, 0x46, 0xFF, 0x7F });  // PMin/PMax
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x02 });     // 16-bit, 2 count
        d.AddRange(new byte[] { 0x81, 0x02 });                 // Input (Var, Abs)
        d.Add(0xC0);                                            // End Physical Collection

        // 8 buttons
        d.AddRange(new byte[] { 0x05, 0x09 });
        d.AddRange(new byte[] { 0x19, 0x01, 0x29, 0x08 });
        d.AddRange(new byte[] { 0x15, 0x00, 0x25, 0x01 });
        d.AddRange(new byte[] { 0x75, 0x01, 0x95, 0x08 });
        d.AddRange(new byte[] { 0x81, 0x02 });

        // Inline PadForge HMaestroFfbDescriptor.Build() — FFB block
        d.AddRange(BuildFfbBlock());

        d.Add(0xC0); // End Application Collection
        return d.ToArray();
    }

    /// <summary>The PID FFB descriptor block (Set Effect, Set Envelope, ...,
    /// Create New Effect Feature, etc.). Transcribed verbatim from
    /// PadForge's HMaestroFfbDescriptor.Build(). Used both standalone
    /// inside the simple BuildPidDescriptor and as AddRaw input when
    /// running --padforge-profile mode through HidDescriptorBuilder.</summary>
    static byte[] BuildFfbBlock()
    {
        var d = new System.Collections.Generic.List<byte>(384);
        d.AddRange(new byte[] { 0x05, 0x0F });                 // Usage Page Physical Interface

        // Set Effect Report (Output, ID 0x11)
        d.AddRange(new byte[] { 0x09, 0x21, 0xA1, 0x02, 0x85, 0x11 });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x25, 0xA1, 0x02 });
        d.AddRange(new byte[] {
            0x09, 0x26, 0x09, 0x27, 0x09, 0x30, 0x09, 0x31, 0x09, 0x32,
            0x09, 0x33, 0x09, 0x34, 0x09, 0x40, 0x09, 0x41, 0x09, 0x42,
            0x09, 0x43, 0x09, 0x29
        });
        d.AddRange(new byte[] { 0x25, 0x0C, 0x15, 0x01, 0x35, 0x01, 0x45, 0x0C });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x00 });
        d.Add(0xC0);
        d.AddRange(new byte[] { 0x09, 0x50, 0x09, 0x54, 0x09, 0x51, 0x09, 0xA7 });
        d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x7F, 0x35, 0x00, 0x46, 0xFF, 0x7F });
        d.AddRange(new byte[] { 0x66, 0x03, 0x10, 0x55, 0xFD });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x04, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x55, 0x00, 0x66, 0x00, 0x00 });
        d.AddRange(new byte[] { 0x09, 0x52 });
        d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x00, 0x35, 0x00, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x53 });
        d.AddRange(new byte[] { 0x15, 0x01, 0x25, 0x08, 0x35, 0x01, 0x45, 0x08 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x55, 0xA1, 0x02 });
        d.AddRange(new byte[] { 0x05, 0x01 });
        d.AddRange(new byte[] { 0x09, 0x30, 0x09, 0x31 });
        d.AddRange(new byte[] { 0x15, 0x00, 0x25, 0x01 });
        d.AddRange(new byte[] { 0x75, 0x01, 0x95, 0x02, 0x91, 0x02 });
        d.Add(0xC0);
        d.AddRange(new byte[] { 0x05, 0x0F });
        d.AddRange(new byte[] { 0x09, 0x56, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x95, 0x05, 0x91, 0x03 });
        d.AddRange(new byte[] { 0x09, 0x57, 0xA1, 0x02 });
        d.AddRange(new byte[] { 0x0B, 0x01, 0x00, 0x0A, 0x00 });
        d.AddRange(new byte[] { 0x0B, 0x02, 0x00, 0x0A, 0x00 });
        d.AddRange(new byte[] { 0x66, 0x14, 0x00, 0x55, 0xFE });
        d.AddRange(new byte[] { 0x15, 0x00, 0x27, 0xFF, 0x7F, 0x00, 0x00 });
        d.AddRange(new byte[] { 0x35, 0x00, 0x47, 0xA0, 0x8C, 0x00, 0x00 });
        d.AddRange(new byte[] { 0x66, 0x00, 0x00 });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x55, 0x00, 0x66, 0x00, 0x00 });
        d.Add(0xC0);
        d.AddRange(new byte[] { 0x05, 0x0F, 0x09, 0x58, 0xA1, 0x02 });
        d.AddRange(new byte[] { 0x0B, 0x01, 0x00, 0x0A, 0x00 });
        d.AddRange(new byte[] { 0x0B, 0x02, 0x00, 0x0A, 0x00 });
        d.AddRange(new byte[] { 0x26, 0xFD, 0x7F, 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
        d.Add(0xC0);
        d.Add(0xC0);   // End Set Effect

        // Set Envelope (Output, ID 0x12)
        d.AddRange(new byte[] { 0x09, 0x5A, 0xA1, 0x02, 0x85, 0x12 });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x5B, 0x09, 0x5D });
        d.AddRange(new byte[] { 0x16, 0x00, 0x00, 0x26, 0x10, 0x27, 0x36, 0x00, 0x00, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x5C, 0x09, 0x5E });
        d.AddRange(new byte[] { 0x66, 0x03, 0x10, 0x55, 0xFD });
        d.AddRange(new byte[] { 0x27, 0xFF, 0x7F, 0x00, 0x00, 0x47, 0xFF, 0x7F, 0x00, 0x00 });
        d.AddRange(new byte[] { 0x75, 0x20, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x45, 0x00, 0x66, 0x00, 0x00, 0x55, 0x00 });
        d.Add(0xC0);

        // Set Condition (Output, ID 0x13)
        d.AddRange(new byte[] { 0x09, 0x5F, 0xA1, 0x02, 0x85, 0x13 });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x23, 0x15, 0x00, 0x25, 0x03, 0x35, 0x00, 0x45, 0x03 });
        d.AddRange(new byte[] { 0x75, 0x04, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x58, 0xA1, 0x02 });
        d.AddRange(new byte[] { 0x0B, 0x01, 0x00, 0x0A, 0x00, 0x0B, 0x02, 0x00, 0x0A, 0x00 });
        d.AddRange(new byte[] { 0x75, 0x02, 0x95, 0x02, 0x91, 0x02 });
        d.Add(0xC0);
        d.AddRange(new byte[] { 0x16, 0xF0, 0xD8, 0x26, 0x10, 0x27, 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x09, 0x60, 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x09, 0x61, 0x09, 0x62, 0x95, 0x02, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x16, 0x00, 0x00, 0x26, 0x10, 0x27, 0x36, 0x00, 0x00, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x09, 0x63, 0x09, 0x64, 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x65 });
        d.AddRange(new byte[] { 0x16, 0x00, 0x00, 0x26, 0x10, 0x27, 0x36, 0x00, 0x00, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x95, 0x01, 0x91, 0x02 });
        d.Add(0xC0);

        // Set Periodic (Output, ID 0x14)
        d.AddRange(new byte[] { 0x09, 0x6E, 0xA1, 0x02, 0x85, 0x14 });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x70, 0x16, 0x00, 0x00, 0x26, 0x10, 0x27, 0x36, 0x00, 0x00, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x6F, 0x16, 0xF0, 0xD8, 0x26, 0x10, 0x27, 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x95, 0x01, 0x75, 0x10, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x71, 0x66, 0x14, 0x00, 0x55, 0xFE });
        d.AddRange(new byte[] { 0x15, 0x00, 0x27, 0x9F, 0x8C, 0x00, 0x00, 0x35, 0x00, 0x47, 0x9F, 0x8C, 0x00, 0x00 });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x72, 0x15, 0x00, 0x27, 0xFF, 0x7F, 0x00, 0x00 });
        d.AddRange(new byte[] { 0x35, 0x00, 0x47, 0xFF, 0x7F, 0x00, 0x00 });
        d.AddRange(new byte[] { 0x66, 0x03, 0x10, 0x55, 0xFD });
        d.AddRange(new byte[] { 0x75, 0x20, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x66, 0x00, 0x00, 0x55, 0x00 });
        d.Add(0xC0);

        // Set Constant Force (Output, ID 0x15)
        d.AddRange(new byte[] { 0x09, 0x73, 0xA1, 0x02, 0x85, 0x15 });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x70 });
        d.AddRange(new byte[] { 0x16, 0xF0, 0xD8, 0x26, 0x10, 0x27, 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
        d.Add(0xC0);

        // Set Ramp Force (Output, ID 0x16)
        d.AddRange(new byte[] { 0x09, 0x74, 0xA1, 0x02, 0x85, 0x16 });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x75, 0x09, 0x76 });
        d.AddRange(new byte[] { 0x16, 0xF0, 0xD8, 0x26, 0x10, 0x27, 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
        d.Add(0xC0);

        // Custom Force Data (Output, ID 0x17)
        d.AddRange(new byte[] { 0x09, 0x68, 0xA1, 0x02, 0x85, 0x17 });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x6C, 0x15, 0x00, 0x26, 0x10, 0x27, 0x35, 0x00, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x69, 0x15, 0x81, 0x25, 0x7F, 0x35, 0x00, 0x46, 0xFF, 0x00 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x0C, 0x92, 0x02, 0x01 });
        d.Add(0xC0);

        // Download Force Sample (Output, ID 0x18)
        d.AddRange(new byte[] { 0x09, 0x66, 0xA1, 0x02, 0x85, 0x18 });
        d.AddRange(new byte[] { 0x05, 0x01, 0x09, 0x30, 0x09, 0x31 });
        d.AddRange(new byte[] { 0x15, 0x81, 0x25, 0x7F, 0x35, 0x00, 0x46, 0xFF, 0x00 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x02, 0x91, 0x02 });
        d.Add(0xC0);

        // Effect Operation (Output, ID 0x1A)
        d.AddRange(new byte[] { 0x05, 0x0F });
        d.AddRange(new byte[] { 0x09, 0x77, 0xA1, 0x02, 0x85, 0x1A });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x78, 0xA1, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x79, 0x09, 0x7A, 0x09, 0x7B });
        d.AddRange(new byte[] { 0x15, 0x01, 0x25, 0x03, 0x75, 0x08, 0x95, 0x01, 0x91, 0x00 });
        d.Add(0xC0);
        d.AddRange(new byte[] { 0x09, 0x7C });
        d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x00, 0x35, 0x00, 0x46, 0xFF, 0x00 });
        d.AddRange(new byte[] { 0x91, 0x02 });
        d.Add(0xC0);

        // PID Block Free (Output, ID 0x1B)
        d.AddRange(new byte[] { 0x09, 0x90, 0xA1, 0x02, 0x85, 0x1B });
        d.AddRange(new byte[] { 0x09, 0x22, 0x25, 0x28, 0x15, 0x01, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.Add(0xC0);

        // PID Device Control (Output, ID 0x1C)
        d.AddRange(new byte[] { 0x09, 0x96, 0xA1, 0x02, 0x85, 0x1C });
        d.AddRange(new byte[] { 0x09, 0x97, 0x09, 0x98, 0x09, 0x99, 0x09, 0x9A, 0x09, 0x9B, 0x09, 0x9C });
        d.AddRange(new byte[] { 0x15, 0x01, 0x25, 0x06, 0x75, 0x08, 0x95, 0x01, 0x91, 0x00 });
        d.Add(0xC0);

        // Device Gain (Output, ID 0x1D)
        d.AddRange(new byte[] { 0x09, 0x7D, 0xA1, 0x02, 0x85, 0x1D });
        d.AddRange(new byte[] { 0x09, 0x7E, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x35, 0x00, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.Add(0xC0);

        // Set Custom Force (Output, ID 0x1E)
        d.AddRange(new byte[] { 0x09, 0x6B, 0xA1, 0x02, 0x85, 0x1E });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x6D, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x35, 0x00, 0x46, 0xFF, 0x00 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x51, 0x66, 0x03, 0x10, 0x55, 0xFD });
        d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x7F, 0x35, 0x00, 0x46, 0xFF, 0x7F });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x55, 0x00, 0x66, 0x00, 0x00 });
        d.Add(0xC0);

        // ── Create New Effect (Feature, ID 0x11) ──
        d.AddRange(new byte[] { 0x09, 0xAB, 0xA1, 0x02, 0x85, 0x11 });
        d.AddRange(new byte[] { 0x09, 0x25, 0xA1, 0x02 });
        d.AddRange(new byte[] {
            0x09, 0x26, 0x09, 0x27, 0x09, 0x30, 0x09, 0x31, 0x09, 0x32,
            0x09, 0x33, 0x09, 0x34, 0x09, 0x40, 0x09, 0x41, 0x09, 0x42,
            0x09, 0x43, 0x09, 0x29
        });
        d.AddRange(new byte[] { 0x25, 0x0C, 0x15, 0x01, 0x35, 0x01, 0x45, 0x0C });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0xB1, 0x00 });
        d.Add(0xC0);
        d.AddRange(new byte[] { 0x05, 0x01, 0x09, 0x3B });
        d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x01, 0x35, 0x00, 0x46, 0xFF, 0x01 });
        d.AddRange(new byte[] { 0x75, 0x0A, 0x95, 0x01, 0xB1, 0x02 });
        d.AddRange(new byte[] { 0x75, 0x06, 0xB1, 0x01 });
        d.Add(0xC0);

        return d.ToArray();
    }

    // ── HID + SetupAPI P/Invoke ────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        // remaining fields ignored
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsedData);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_FreePreparsedData(IntPtr preparsedData);
    [DllImport("hid.dll", SetLastError = true)]
    static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS caps);
    [DllImport("hid.dll", SetLastError = true)]
    static extern int HidP_InitializeReportForID(int reportType, byte reportId,
        IntPtr preparsedData, byte[] report, uint reportLength);
    [DllImport("hid.dll", SetLastError = true)]
    static extern int HidP_SetUsages(int reportType, ushort usagePage, ushort linkCollection,
        ushort[] usageList, ref uint usageLength,
        IntPtr preparsedData, byte[] report, uint reportLength);
    [DllImport("hid.dll", SetLastError = true)]
    static extern int HidP_SetUsageValue(int reportType, ushort usagePage, ushort linkCollection,
        ushort usage, uint value,
        IntPtr preparsedData, byte[] report, uint reportLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle h, byte[] buf, uint bufLen,
        out uint bytesWritten, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string path, uint a, uint b, IntPtr c, uint d, uint e, IntPtr f);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenFileMappingW")]
    static extern IntPtr OpenFileMappingW2(uint a, bool b, string c);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "MapViewOfFile")]
    static extern IntPtr MapViewOfFile2(IntPtr h, uint a, uint b, uint c, UIntPtr n);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "UnmapViewOfFile")]
    static extern bool UnmapViewOfFile2(IntPtr p);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")]
    static extern bool CloseHandle2(IntPtr h);

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3;

    static SafeFileHandle? OpenHmHidByVidPid(ushort vid, ushort pid)
    {
        var match = HMDeviceExtractor.ListDevices()
            .FirstOrDefault(d => d.VendorId == vid && d.ProductId == pid);
        if (match == null) return null;
        var h = CreateFile(match.DevicePath, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return h;
    }
}
