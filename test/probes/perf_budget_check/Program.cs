// The budgets of the v1.13.0 design, measured the same way before and after
// on one machine: built against this tree, and with -p:HmBase=true against a
// b4d29d9 tree. The parts that measure what b4d29d9 lacks, the ownership
// claims and the controller ports, are left out of that build.
//
//   frame [runs]   time and bytes allocated per call on each submit path
//   e2e [runs]     SubmitState to the driver's acknowledgement that a read
//                  carried the frame
//   output [runs]  an output report to OutputReceived, and bytes allocated
//                  per report anywhere in the process
//   create [runs] [profile ...] [--settle=<ms>]
//                  CreateController and Dispose for five kinds of device, or
//                  the ones named, with the SDK's own step timings
//                  (HIDMAESTRO_TIMING), each controller up for <ms> first
//   start [runs]   process start to the first controller, each run in a
//                  fresh process, with the session cleanup step's time
//   claim [runs]   the owner claim, the census and the lease (this release)
//   gate           S84: no submit path allocates, and the census finds
//                  what the configuration manager lists
//
// --trace prints wall-clock times around each Dispose, to set beside the
// SDK's diag log. Every measurement prints one line with p50 and p95 over
// all samples.
// Requires elevation. Exit 0 PASS, 1 FAIL (gate), 2 when another program
// that embeds HIDMaestro is running.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    const string Ds4 = "dualshock-4-v2";
    const string Xbox360 = "xbox-360-wired";
    const string Composite = "dualsense-composite";
    const string Keyboard = "microsoft-wired-keyboard-600";
    const string Mouse = "microsoft-notebook-optical-mouse";
    const string GameCube = "gamecube-adapter";

#if HM_BASE
    const string Build = "b4d29d9";
#else
    const string Build = "this release";
#endif

    static int s_failures;
    // --trace: wall-clock times around each Dispose, to set beside the SDK's
    // diag log.
    static bool s_trace;

    static int Main(string[] args)
    {
        // Read once by the SDK's first create, so it is set before any.
        Environment.SetEnvironmentVariable("HIDMAESTRO_TIMING", "1");
        s_trace = args.Contains("--trace");
        args = args.Where(a => a != "--trace").ToArray();
        string mode = args.Length > 0 ? args[0] : "gate";
        int runs = args.Length > 1 && int.TryParse(args[1], out int r) ? r : 30;
        if (mode == "start-child") return StartChild();
        if (OtherSdkRunning()) return 2;

        Console.WriteLine($"=== budgets, {Build}, mode {mode} ===");
        switch (mode)
        {
            case "frame": Frame(runs, gate: false); break;
            case "e2e": EndToEnd(runs); break;
            case "output": Output(runs); break;
            case "create": Create(runs, args.Skip(2).Where(a => !a.StartsWith("--settle=", StringComparison.Ordinal)).ToArray(),
                SettleMs(args)); break;
            case "start": Start(runs); break;
#if !HM_BASE
            case "claim": Claim(runs); break;
#endif
            case "gate": Frame(3, gate: true); break;
            default:
                Console.WriteLine($"unknown mode {mode}");
                return 1;
        }
        if (mode == "gate")
        {
            Console.WriteLine($"S84: {(s_failures == 0 ? "PASS" : $"{s_failures} FAIL")}");
            return s_failures == 0 ? 0 : 1;
        }
        return 0;
    }

    // ── Per-call cost on the submit paths ───────────────────────────────

    /// <summary>Each submit path called many times per run on a live
    /// controller: the time per call, and the bytes the calling thread
    /// allocated per call. The gamepad paths take two states in turn, so
    /// every call writes a new frame. The keyboard and mouse paths wait for
    /// the driver to deliver each report and send at most one a
    /// millisecond, so they run fewer calls.</summary>
    static void Frame(int runs, bool gate)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        var a = new HMGamepadState { Buttons = HMButton.A, Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 0.25f, [HMAxis.Y] = 0.75f } };
        var b = new HMGamepadState { Buttons = HMButton.B, Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 0.75f, [HMAxis.Y] = 0.25f } };

        using (var ds4 = ctx.CreateController(ctx.GetProfile(Ds4)!))
        {
            bool flip = false;
            Measure("SubmitState, dualshock-4-v2", runs, 20_000, () => { ds4.SubmitState(flip ? a : b); flip = !flip; }, gate);
            var raw = new byte[63];
            Measure("SubmitRawReport, dualshock-4-v2", runs, 20_000, () => { raw[0]++; ds4.SubmitRawReport(raw); }, gate);
        }
        using (var x360 = ctx.CreateController(ctx.GetProfile(Xbox360)!))
        {
            bool flip = false;
            Measure("SubmitState, xbox-360-wired", runs, 20_000, () => { x360.SubmitState(flip ? a : b); flip = !flip; }, gate);
#if !HM_BASE
            // The census walks the Enum keys instead of listing every present
            // device. With a shared-enumerator devnode and an SWD companion up,
            // the two must find the same devnodes.
            if (gate)
            {
                var walked = HIDMaestro.Internal.OwnerCensus.PresentOwnedDevnodes().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                var listed = HIDMaestro.Internal.OwnerCensus.PresentOwnedDevnodesListed().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                Check("the census walk finds the devnodes the configuration manager lists, the Xbox 360's two among them",
                    walked.Count >= 2 && walked.SequenceEqual(listed, StringComparer.OrdinalIgnoreCase),
                    $"walked {string.Join(", ", walked)}; listed {string.Join(", ", listed)}");
            }
#endif
        }
        using (var kb = ctx.CreateController(ctx.GetProfile(Keyboard)!))
        {
            var down = new[] { HMKey.A };
            bool flip = false;
            Measure("SubmitKeyboard, " + Keyboard, runs, 200, () =>
            {
                kb.SubmitKeyboard(HMKeyModifiers.None, flip ? new ReadOnlySpan<HMKey>(down) : ReadOnlySpan<HMKey>.Empty);
                flip = !flip;
            }, gate);
        }
        using (var mouse = ctx.CreateController(ctx.GetProfile(Mouse)!))
        {
            var move = new HMMouseState { X = 1 };
            Measure("SubmitMouse, " + Mouse, runs, 200, () => mouse.SubmitMouse(in move), gate);
#if !HM_BASE
            if (gate)
            {
                // A long movement splits across many reports. Once they are
                // sent the controller keeps only a few of their frames.
                mouse.SubmitMouse(new HMMouseState { X = 20_000 });
                var kept = typeof(HMController).GetField("_kbmBuffers", Any)!.GetValue(mouse) as HIDMaestro.Internal.KeyboardMouseBuffers;
                Check($"after a movement of 20,000 counts the mouse keeps at most {HIDMaestro.Internal.KeyboardMouseBuffers.MotionFramesKept} frames",
                      kept != null && kept.Motion.Count <= HIDMaestro.Internal.KeyboardMouseBuffers.MotionFramesKept,
                      $"{kept?.Motion.Count} kept");
            }
#endif
        }
#if !HM_BASE
        using (var gc = ctx.CreateController(ctx.GetProfile(GameCube)!))
        {
            bool flip = false;
            var port = gc.Ports[0];
            Measure("HMPort.SubmitState, gamecube-adapter port 1", runs, 20_000, () => { port.SubmitState(flip ? a : b); flip = !flip; }, gate);
            Measure("SubmitState, gamecube-adapter", runs, 20_000, () => { gc.SubmitState(flip ? a : b); flip = !flip; }, gate);
        }
#endif
    }

    static void Measure(string name, int runs, int calls, Action call, bool gate)
    {
        for (int i = 0; i < Math.Min(calls, 2_000); i++) call();
        var perCall = new List<double>(runs);
        long worstBytes = 0;
        for (int run = 0; run < runs; run++)
        {
            long bytes0 = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < calls; i++) call();
            long t1 = Stopwatch.GetTimestamp();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - bytes0;
            worstBytes = Math.Max(worstBytes, bytes);
            perCall.Add((t1 - t0) * 1e9 / Stopwatch.Frequency / calls);
        }
        Report($"{name}: ns per call", perCall, runs, $", most bytes allocated in one run of {calls} calls {worstBytes}");
        if (gate)
            Check($"{name} allocates nothing", worstBytes == 0, $"{worstBytes} bytes over {runs} runs of {calls} calls");
    }

    // ── Submit to the driver's acknowledgement ──────────────────────────

    /// <summary>The time from a SubmitState call to the driver's
    /// acknowledgement that a READ_REPORT carried the frame, read from the
    /// acknowledgement section the SDK and driver share
    /// (Global\HIDMaestroInputAck&lt;N&gt;, DeliveredSeqNo at offset 0).
    /// Fifty submits per run, each at a random offset.</summary>
    static void EndToEnd(int runs)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        foreach (string id in new[] { Ds4, Xbox360 })
        {
            using var c = ctx.CreateController(ctx.GetProfile(id)!);
            int index = (int)typeof(HMController).GetProperty("Index", Any)!.GetValue(c)!;
            var seqField = typeof(HMController).GetField("_inputSeqNo", Any)!;
            IntPtr map = OpenFileMappingW(FILE_MAP_READ, false, $@"Global\HIDMaestroInputAck{index}");
            if (map == IntPtr.Zero)
            {
                Console.WriteLine($"{id}: no acknowledgement section (error {Marshal.GetLastWin32Error()})");
                continue;
            }
            IntPtr view = MapViewOfFile(map, FILE_MAP_READ, 0, 0, UIntPtr.Zero);
            try
            {
                var a = new HMGamepadState { Buttons = HMButton.A };
                var b = new HMGamepadState { Buttons = HMButton.B };
                Thread.Sleep(500);
                var rng = new Random(84);
                var samples = new List<double>(runs * 50);
                int missed = 0;
                bool flip = false;
                for (int run = 0; run < runs; run++)
                {
                    for (int i = 0; i < 50; i++)
                    {
                        Spin(rng.Next(2_000, 4_000));
                        long t0 = Stopwatch.GetTimestamp();
                        c.SubmitState(flip ? a : b);
                        flip = !flip;
                        uint seq = (uint)seqField.GetValue(c)!;
                        long deadline = t0 + Stopwatch.Frequency / 20;
                        bool seen = false;
                        while (Stopwatch.GetTimestamp() < deadline)
                        {
                            if ((int)((uint)Marshal.ReadInt32(view, 0) - seq) >= 0) { seen = true; break; }
                            Thread.SpinWait(16);
                        }
                        if (seen) samples.Add((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
                        else missed++;
                    }
                }
                Report($"{id}: SubmitState to the driver's delivery, microseconds", samples, runs, $", {missed} not acknowledged within 50 ms");
            }
            finally
            {
                UnmapViewOfFile(view);
                CloseHandle(map);
            }
        }
    }

    // ── The output reader ───────────────────────────────────────────────

    /// <summary>Output reports sent to a DualShock 4 through HID, each
    /// timed to OutputReceived, and the bytes the whole process allocated
    /// per report while the SDK's reader handled them.</summary>
    static void Output(int runs)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        var profile = ctx.GetProfile(Ds4)!;
        using var c = ctx.CreateController(profile);
        long sent = 0;
        using var got = new AutoResetEvent(false);
        var rtts = new List<double>(runs * 100);
        c.OutputReceived += (_, _) =>
        {
            long t = Stopwatch.GetTimestamp();
            long s = Interlocked.Exchange(ref sent, 0);
            if (s != 0)
            {
                rtts.Add((t - s) * 1e6 / Stopwatch.Frequency);
                got.Set();
            }
        };
        string? path = null;
        for (int i = 0; i < 50 && path == null; i++)
        {
            path = HidPaths(profile.VendorId, profile.ProductId).FirstOrDefault();
            if (path == null) Thread.Sleep(100);
        }
        if (path == null) { Console.WriteLine("output: the DualShock 4's HID device did not appear"); return; }
        using var h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) { Console.WriteLine($"output: CreateFile failed, error {Marshal.GetLastWin32Error()}"); return; }
        var report = new byte[32];
        report[0] = 0x05;
        var perReport = new List<double>(runs);
        int lost = 0;
        for (int run = 0; run < runs; run++)
        {
            int delivered = 0;
            long bytes0 = GC.GetTotalAllocatedBytes(precise: true);
            for (int i = 0; i < 100; i++)
            {
                report[4] = (byte)(1 + ((run * 100 + i) % 254));
                Interlocked.Exchange(ref sent, Stopwatch.GetTimestamp());
                if (!HidD_SetOutputReport(h, report, report.Length)) { Interlocked.Exchange(ref sent, 0); lost++; continue; }
                if (got.WaitOne(500)) delivered++;
                else { Interlocked.Exchange(ref sent, 0); lost++; }
                Spin(1_000);
            }
            long bytes = GC.GetTotalAllocatedBytes(precise: true) - bytes0;
            if (delivered > 0) perReport.Add((double)bytes / delivered);
        }
        Report("dualshock-4-v2: output report to OutputReceived, microseconds", rtts, runs, $", {lost} lost");
        Report("dualshock-4-v2: bytes the process allocated per output report", perReport, runs, "");
    }

    // ── Create and dispose ──────────────────────────────────────────────

    /// <summary>The profiles named after the run count, or all five.</summary>
    static void Create(int runs, string[] only, int settleMs)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        var ids = new List<string> { Ds4, Xbox360, Composite, Keyboard };
#if !HM_BASE
        ids.Add(GameCube);
#endif
        if (only.Length > 0) ids = ids.Where(only.Contains).ToList();
        string log = TimingLog();
        foreach (string id in ids)
        {
            var profile = ctx.GetProfile(id)!;
            // One create and dispose first, so a cold first create (driver
            // binding, the USB/IP server's first start) is not a sample.
            ctx.CreateController(profile).Dispose();
            long logStart = new FileInfo(log).Exists ? new FileInfo(log).Length : 0;
            var create = new List<double>(runs);
            var dispose = new List<double>(runs);
            for (int run = 0; run < runs; run++)
            {
                long t0 = Stopwatch.GetTimestamp();
                var c = ctx.CreateController(profile);
                long t1 = Stopwatch.GetTimestamp();
                if (settleMs > 0) Thread.Sleep(settleMs);
                long t2 = Stopwatch.GetTimestamp();
                if (s_trace) Console.WriteLine($"    [{DateTime.Now:HH:mm:ss.fff}] dispose begins");
                c.Dispose();
                long t3 = Stopwatch.GetTimestamp();
                if (s_trace) Console.WriteLine($"    [{DateTime.Now:HH:mm:ss.fff}] dispose returned after {(t3 - t2) * 1000.0 / Stopwatch.Frequency:F1} ms");
                create.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
                dispose.Add((t3 - t2) * 1000.0 / Stopwatch.Frequency);
            }
            Report($"{id}: CreateController, ms", create, runs, "");
            Report($"{id}: Dispose, ms", dispose, runs, "");
            foreach (var (step, values) in TimingSteps(log, logStart, id))
                Report($"{id}: step {step}, ms", values, runs, "");
        }
    }

    /// <summary>--settle=&lt;ms&gt;: the time each created controller stays up
    /// before its Dispose is timed.</summary>
    static int SettleMs(string[] args)
    {
        foreach (string a in args)
            if (a.StartsWith("--settle=", StringComparison.Ordinal) && int.TryParse(a[9..], out int ms)) return ms;
        return 0;
    }

    // ── Process start to the first controller ───────────────────────────

    /// <summary>Fresh processes, each creating one DualShock 4 on the
    /// installed driver and exiting. One run first makes sure the driver is
    /// current, so every timed run takes the same-version fast path.</summary>
    static void Start(int runs)
    {
        string self = Environment.ProcessPath!;
        string log = TimingLog();
        var ready = new List<double>(runs);
        var sweep = new List<double>(runs);
        for (int run = -1; run < runs; run++)
        {
            long logStart = File.Exists(log) ? new FileInfo(log).Length : 0;
            var psi = new ProcessStartInfo(self, "start-child")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (run < 0) continue;
            var line = output.Split('\n').FirstOrDefault(l => l.StartsWith("ready ", StringComparison.Ordinal));
            if (line == null) { Console.WriteLine($"start: run {run} gave no time: {output.Trim()}"); continue; }
            ready.Add(double.Parse(line[6..].Trim(), System.Globalization.CultureInfo.InvariantCulture));
            foreach (var (step, values) in TimingSteps(log, logStart, Ds4))
                if (step == "0.ghost_cleanup") sweep.AddRange(values);
        }
        Report("process start to the first controller, ms", ready, runs, "");
        Report("the session cleanup step in that create (0.ghost_cleanup), ms", sweep, runs, "");
    }

    static int StartChild()
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        using var c = ctx.CreateController(ctx.GetProfile(Ds4)!);
        DateTime started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        double ms = (DateTime.UtcNow - started).TotalMilliseconds;
        Console.WriteLine(FormattableString.Invariant($"ready {ms:F1}"));
        return 0;
    }

#if !HM_BASE
    // ── The ownership claims ────────────────────────────────────────────

    /// <summary>What a create spends on ownership: the claim on one name,
    /// the census of owners and devices it takes, and the whole lease
    /// (census, index and identity claims, and the record entry) with its
    /// release. The owner claim a process takes once is timed in fresh
    /// processes by the start mode's first create.</summary>
    static void Claim(int runs)
    {
        HIDMaestro.Internal.MachineClaims.EnsureOwner();
        var claim = new List<double>(runs * 10);
        var census = new List<double>(runs * 10);
        var lease = new List<double>(runs * 10);
        var release = new List<double>(runs * 10);
        for (int i = 0; i < runs * 10; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            var h = HIDMaestro.Internal.MachineClaims.TryClaim($"HIDMaestro.Budget.{Environment.ProcessId}.{i}");
            h?.Dispose();
            long t1 = Stopwatch.GetTimestamp();
            HIDMaestro.Internal.OwnerCensus.Take();
            long t2 = Stopwatch.GetTimestamp();
            var held = HIDMaestro.Internal.ControllerLease.Acquire(0, null, "umdf2", Ds4);
            long t3 = Stopwatch.GetTimestamp();
            held.Dispose();
            long t4 = Stopwatch.GetTimestamp();
            claim.Add((t1 - t0) * 1e6 / Stopwatch.Frequency);
            census.Add((t2 - t1) * 1e6 / Stopwatch.Frequency);
            lease.Add((t3 - t2) * 1e6 / Stopwatch.Frequency);
            release.Add((t4 - t3) * 1e6 / Stopwatch.Frequency);
        }
        Report("one claim taken and released, microseconds", claim, runs, "");
        Report("the census of owners and devices, microseconds", census, runs, "");
        Report("a lease taken, what a create pays, census included, microseconds", lease, runs, "");
        Report("a lease released, what a dispose pays, microseconds", release, runs, "");

        // The census's parts.
        var records = new List<double>(runs * 10);
        var rootIds = new List<double>(runs * 10);
        var swdIds = new List<double>(runs * 10);
        var owned = new List<double>(runs * 10);
        var imports = new List<double>(runs * 10);
        int rootCount = 0, swdCount = 0, ownedCount = 0;
        for (int i = 0; i < runs * 10; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            HIDMaestro.Internal.MachineClaims.ReadAll();
            long t1 = Stopwatch.GetTimestamp();
            rootCount = HIDMaestro.Internal.OwnerCensus.PresentDeviceIds("ROOT").Count;
            long t2 = Stopwatch.GetTimestamp();
            swdCount = HIDMaestro.Internal.OwnerCensus.PresentDeviceIds("SWD").Count;
            long t3 = Stopwatch.GetTimestamp();
            ownedCount = HIDMaestro.Internal.OwnerCensus.PresentOwnedDevnodes().Count;
            long t4 = Stopwatch.GetTimestamp();
            HIDMaestro.Internal.Usbip.VhciClient.GetImportedDevices();
            long t5 = Stopwatch.GetTimestamp();
            records.Add((t1 - t0) * 1e6 / Stopwatch.Frequency);
            rootIds.Add((t2 - t1) * 1e6 / Stopwatch.Frequency);
            swdIds.Add((t3 - t2) * 1e6 / Stopwatch.Frequency);
            owned.Add((t4 - t3) * 1e6 / Stopwatch.Frequency);
            imports.Add((t5 - t4) * 1e6 / Stopwatch.Frequency);
        }
        Report("census part: the owner records, microseconds", records, runs, "");
        Report($"the listing the census made before, present ROOT ids ({rootCount}), microseconds", rootIds, runs, "");
        Report($"the listing the census made before, present SWD ids ({swdCount}), microseconds", swdIds, runs, "");
        Report($"census part: the owned devnodes ({ownedCount}), microseconds", owned, runs, "");
        Report("census part: the USB/IP imports, microseconds", imports, runs, "");
    }
#endif

    // ── Helpers ─────────────────────────────────────────────────────────

    static void Report(string name, List<double> samples, int runs, string tail)
    {
        if (samples.Count == 0)
        {
            Console.WriteLine($"  {name}: no samples{tail}");
            return;
        }
        var s = samples.OrderBy(x => x).ToList();
        double P(double q) => s[Math.Clamp((int)Math.Ceiling(q * s.Count) - 1, 0, s.Count - 1)];
        Console.WriteLine(FormattableString.Invariant(
            $"  {name}: p50 {P(0.50):F3}, p95 {P(0.95):F3}, max {s[^1]:F3} ({s.Count} samples, {runs} runs){tail}"));
    }

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}  {detail}");
    }

    static void Spin(long micros)
    {
        long end = Stopwatch.GetTimestamp() + micros * Stopwatch.Frequency / 1_000_000;
        while (Stopwatch.GetTimestamp() < end) Thread.SpinWait(64);
    }

    static string TimingLog() => Path.Combine(Path.GetTempPath(), "HIDMaestro", "setup_timing.log");

    /// <summary>The SDK's step timings for one profile written to the log
    /// after <paramref name="from"/>, by step.</summary>
    static List<(string Step, List<double> Values)> TimingSteps(string log, long from, string profileId)
    {
        var steps = new Dictionary<string, List<double>>();
        if (!File.Exists(log)) return new();
        using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(Math.Min(from, fs.Length), SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // [hh:mm:ss.fff] ctrl=N profile=<id> step=<step> <n>ms
            int p = line.IndexOf(" profile=", StringComparison.Ordinal);
            int s = line.IndexOf(" step=", StringComparison.Ordinal);
            if (p < 0 || s < 0) continue;
            string profile = line.Substring(p + 9, s - p - 9).Trim();
            if (!string.Equals(profile, profileId, StringComparison.Ordinal)) continue;
            string rest = line[(s + 6)..].Trim();
            int sp = rest.LastIndexOf(' ');
            if (sp < 0) continue;
            string step = rest[..sp].Trim();
            string ms = rest[(sp + 1)..].Trim().TrimEnd('s').TrimEnd('m');
            if (!double.TryParse(ms, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)) continue;
            if (!steps.TryGetValue(step, out var list)) steps[step] = list = new List<double>();
            list.Add(v);
        }
        return steps.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => (k.Key, k.Value)).ToList();
    }

    /// <summary>The HID device paths of one VID and PID, through the SDK's
    /// own enumerator, reached by reflection so this builds on b4d29d9.</summary>
    static IEnumerable<string> HidPaths(ushort vid, ushort pid)
    {
        var t = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.HidDeviceEnumerator")!;
        var list = (System.Collections.IEnumerable)t.GetMethod("Enumerate", Any)!.Invoke(null, null)!;
        foreach (var d in list)
        {
            var dt = d.GetType();
            if (Convert.ToUInt16(dt.GetField("VendorId")!.GetValue(d)) != vid) continue;
            if (Convert.ToUInt16(dt.GetField("ProductId")!.GetValue(d)) != pid) continue;
            if (dt.GetField("DevicePath")!.GetValue(d) is string path) yield return path;
        }
    }

    static bool OtherSdkRunning()
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                foreach (ProcessModule m in p.Modules)
                    if (string.Equals(m.ModuleName, "HIDMaestro.Core.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"  [SKIP] {p.ProcessName} ({p.Id}) is running and shares the SDK's devices.");
                        return true;
                    }
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        return false;
    }

    const uint FILE_MAP_READ = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHigh, uint offLow, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    static extern bool UnmapViewOfFile(IntPtr view);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buffer, int length);
}
