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

        // A/B: --joystick (default) or --gamepad. PadForge's Custom profile
        // uses Gamepad (0x05); vJoy uses Joystick (0x04). Run both to see
        // if the TLC affects HidP's view of the Set Effect Output report.
        bool joystickTlc = !args.Any(a => a.Equals("--gamepad", StringComparison.OrdinalIgnoreCase));
        byte[] descriptor = BuildPidDescriptor(joystickTlc);
        Console.WriteLine($"  Descriptor size: {descriptor.Length} bytes ({(joystickTlc ? "Joystick" : "Gamepad")} TLC)");

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
            .InputReportSize(6)  // RID + 4 bytes XY + 1 byte buttons
            .Build();

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        Console.Write("  Creating virtual... ");
        using var ctrl = ctx.CreateController(profile);
        Console.WriteLine("OK");

        SafeFileHandle? hid = OpenHmHidByVidPid(ProbeVid, ProbePid);
        if (hid == null || hid.IsInvalid)
        {
            Console.Error.WriteLine("FAIL: could not open HID handle");
            return 2;
        }

        if (!HidD_GetPreparsedData(hid, out IntPtr pp))
        {
            Console.Error.WriteLine($"FAIL: HidD_GetPreparsedData (Win32={Marshal.GetLastWin32Error()})");
            return 3;
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

            // ── Spawn FfbTest.exe against this virtual ──
            // FfbTest is a SharpDX/DirectInput8 PID FFB harness, copied into
            // this probe's directory at FfbTest/FfbTest.exe. It finds the
            // first FFB-capable controller (this virtual, since the probe
            // owns it for the duration of this test), enumerates its FFB
            // device objects, and tries CreateEffect(GUID_ConstantForce).
            // Exit code 0 = end-to-end PID FFB works; non-zero = pid.dll
            // crashed or DI rejected the device.
            string ffbTest = Path.Combine(AppContext.BaseDirectory, "FfbTest.exe");
            if (File.Exists(ffbTest))
            {
                Console.WriteLine("");
                Console.WriteLine("--- FfbTest.exe roundtrip ---");
                hid.Dispose();  // close our handle so FfbTest can open exclusively
                HidD_FreePreparsedData(pp);
                pp = IntPtr.Zero;

                var psi = new System.Diagnostics.ProcessStartInfo(ffbTest)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi)!;
                /* FfbTest auto-runs the constant + sine effect probes
                 * (the "--- Probing ... ---" phase) and then drops into an
                 * interactive command menu. Feed 'Q\n' so it quits cleanly
                 * after the probes; closing stdin would EOF Console.ReadKey
                 * and throw InvalidOperationException (exit 0xE0434352). */
                proc.StandardInput.WriteLine("Q");
                proc.StandardInput.Close();
                var sb = new System.Text.StringBuilder();
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine("[err] " + e.Data); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                bool exited = proc.WaitForExit(20000);
                if (!exited)
                {
                    Console.WriteLine("FfbTest hung past 20s — killing");
                    try { proc.Kill(entireProcessTree: true); } catch { /* may already be dead */ }
                    proc.WaitForExit(2000);
                }
                proc.WaitForExit(); // drain async readers
                string output = sb.ToString();
                lock (sb) Console.WriteLine(output);
                Console.WriteLine($"FfbTest exit code: 0x{proc.ExitCode:X8} ({proc.ExitCode})");

                /* The success criterion that matters: did the constant-force
                 * and sine effect probes succeed? FfbTest prints a "SUCCESS:"
                 * line per probe variant. Count successes; require at least
                 * one Constant Force success (the original issue #16 crash
                 * was at GUID_ConstantForce CreateEffect). */
                int constSuccesses = output.Split('\n')
                    .Count(l => l.Contains("SUCCESS:") && !l.Contains("Sine"));
                int sineSuccesses = output.Split('\n')
                    .Count(l => l.Contains("SUCCESS:") && l.Contains("Sine"));
                bool didConstantForceCreate = output.Contains("Probing constant force")
                                            && constSuccesses > 0;
                bool noFatalAv = !output.Contains("0xC0000005")
                              && !output.Contains("Fatal error");

                if (didConstantForceCreate && noFatalAv && exited)
                {
                    Console.WriteLine($"=== FfbTest PASS — Constant Force CreateEffect succeeded ({constSuccesses} variants); Sine: {sineSuccesses} variants. PID FFB end-to-end works. ===");
                }
                else
                {
                    Console.WriteLine($"=== FfbTest FAILED — constForce variants={constSuccesses}, sine variants={sineSuccesses}, fatalAV={!noFatalAv}, exited={exited} ===");
                    failures++;
                }
            }
            else
            {
                Console.WriteLine($"  FfbTest.exe not found at {ffbTest} — skipping E2E");
            }

            if (failures == 0)
            {
                Console.WriteLine("=== PASS — full PID FFB roundtrip ===");
                return 0;
            }
            Console.WriteLine($"=== FAIL: {failures} check(s) failed ===");
            return 1;
        }
        finally
        {
            if (pp != IntPtr.Zero) HidD_FreePreparsedData(pp);
            if (!hid.IsClosed) hid.Dispose();
        }
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

        // Inline PadForge HMaestroFfbDescriptor.Build() ───────────────────
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

        d.Add(0xC0); // End Application Collection
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
