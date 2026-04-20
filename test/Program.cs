using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;

namespace HIDMaestroTest;

/// <summary>
/// Thin SDK consumer / test client for the HIDMaestro SDK.
///
/// This test app is intentionally minimal: it only exercises the public
/// HIDMaestro SDK surface (HMContext, HMController, HMGamepadState). Anything
/// that needs raw HID handles, shared memory, or device-node bookkeeping
/// belongs INSIDE the SDK, not here.
///
/// Commands:
///   HIDMaestroTest emulate &lt;id&gt; [id ...]   Create N virtual controllers + send test pattern
///   HIDMaestroTest list                    List all controller profiles
///   HIDMaestroTest search &lt;query&gt;          Search profiles by name/vendor
///   HIDMaestroTest info &lt;id&gt;               Show profile details
///   HIDMaestroTest cleanup                 Remove all HIDMaestro virtual devices
///   HIDMaestroTest sdk-demo [id]           Minimal SDK consumer demo (5s)
/// </summary>
class Program
{
    // ── Elevation plumbing ──

    static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    static int RelaunchElevated(string[] args)
    {
        string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(" ", args),
            UseShellExecute = true,
            Verb = "runas"
        };
        try
        {
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
            return proc?.ExitCode ?? 1;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("ERROR: Elevation denied. This command requires administrator privileges.");
            return 1;
        }
    }

    static int Error(string msg) { Console.Error.WriteLine($"ERROR: {msg}"); return 1; }

    // ── Main ──

    static int Main(string[] args)
    {
        Console.WriteLine("=== HIDMaestro Test Client ===\n");

        // Single-instance: kill any other HIDMaestroTest processes
        int myPid = Environment.ProcessId;
        foreach (var proc in Process.GetProcessesByName("HIDMaestroTest"))
        {
            if (proc.Id != myPid)
            {
                try { proc.Kill(); proc.WaitForExit(3000); } catch { }
            }
        }

        // Read-only introspection commands run fine without admin. Everything
        // else needs SeLoadDriverPrivilege for device create / driver install /
        // registry cleanup.
        bool readOnlyCmd = args.Length > 0 && args[0].ToLowerInvariant() is "list" or "search" or "info" or "build-descriptor" or "extract-profile";
        if (!readOnlyCmd && !IsElevated())
        {
            Console.WriteLine("  Requesting elevation (admin required)...\n");
            return RelaunchElevated(args);
        }

        // Safety net: always purge devices on exit so testing leaves no trace.
        // Skip for read-only commands since that path needs admin for the
        // registry cleanup it performs.
        if (!readOnlyCmd)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { HMContext.RemoveAllVirtualControllers(); } catch { }
            };
            try { HMContext.RemoveAllVirtualControllers(); } catch { }
        }

        if (args.Length == 0)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  HIDMaestroTest emulate <id> [id ...]   Create virtual controller(s) + test pattern");
            Console.WriteLine("  HIDMaestroTest custom                  Demo custom controllers (16-btn DualSense + flight stick)");
        Console.WriteLine("  HIDMaestroTest list                    List all controller profiles");
            Console.WriteLine("  HIDMaestroTest search <query>          Search profiles by name/vendor");
            Console.WriteLine("  HIDMaestroTest info <id>               Show profile details");
            Console.WriteLine("  HIDMaestroTest cleanup                 Remove all HIDMaestro virtual devices");
            Console.WriteLine("  HIDMaestroTest sdk-demo [id]           Minimal SDK consumer demo (5s)");
            Console.WriteLine("  HIDMaestroTest oem set <vid> <pid> <label>");
            Console.WriteLine("  HIDMaestroTest oem clear <vid> <pid>");
            Console.WriteLine("  HIDMaestroTest oem recover             Restore overrides left by a crashed consumer");
            Console.WriteLine("  HIDMaestroTest oem list                Show currently-tracked overrides");
            Console.WriteLine("  HIDMaestroTest build-descriptor        Inspect HidDescriptorBuilder output (read-only)");
            Console.WriteLine("  HIDMaestroTest extract-profile         List connected HID devices (read-only)");
            Console.WriteLine("  HIDMaestroTest extract-profile <vid> <pid>");
            Console.WriteLine("                                        Emit HIDMaestro profile JSON for the device");
            Console.WriteLine("\nMust run elevated.");
            return 1;
        }

        return args[0].ToLower() switch
        {
            "emulate"  => Emulate(args.Skip(1).ToArray()),
            "custom"   => EmulateCustom(),
            "list"     => ListProfiles(),
            "search"   => SearchProfiles(args.Length > 1 ? args[1] : ""),
            "info"     => ShowProfile(args.Length > 1 ? args[1] : ""),
            "cleanup"  => RunCleanup(),
            "sdk-demo" => SdkDemo(args.Skip(1).ToArray()),
            "oem"      => OemCommand(args.Skip(1).ToArray()),
            "build-descriptor" => BuildDescriptorCommand(),
            "issue6-repro" => Issue6ReproCommand(),
            "extract-profile" => ExtractProfileCommand(args.Skip(1).ToArray()),
            _          => Error($"Unknown command: {args[0]}")
        };
    }

    // ── emulate ──
    //
    // Per-controller pattern threads + per-controller cancellation tokens so
    // individual controllers can be disposed/replaced live without disturbing
    // the others. Output passthrough is wired via HMController.OutputReceived
    // — every rumble/haptic/FFB packet from any host app is decoded and
    // printed to the console with profile context, so the host→virtual
    // direction can be verified end-to-end without launching a real game.

    sealed class RunningController
    {
        public HMController Ctrl = default!;
        public CancellationTokenSource Cts = new();
        public Thread? Thread;
        // True = pattern thread is suspended (idle test). The thread spins
        // on a long Sleep instead of submitting frames so we can measure
        // the driver's true idle CPU cost (should be ~0% with event-driven
        // IPC; was a busy-polled core per controller before the fix).
        public volatile bool Paused;
        // -1 = not in mark mode. >=0 = pattern thread submits a STATIC frame
        // holding only HMButton bit (1 << MarkButton), so the browser-order
        // diagnostic can read pad.buttons[MarkButton].pressed to find which
        // creation-order controller maps to which Chromium gamepad index.
        // Buttons are integer-indexed in the Chromium Standard Gamepad mapping
        // (buttons[0]=A, [1]=B, [2]=X, [3]=Y, [4]=LB, [5]=RB) so we get an
        // unambiguous fingerprint with no float-comparison ambiguity.
        public volatile int MarkButton = -1;
        // 'park' mode: hold the LEFT stick at literal (ParkX, ParkY) values
        // in the [-1..+1] range and submit a static frame. Used by the
        // Cemu-style SDL2 readback diagnostic — we can park at known
        // positions and verify SDL_GameControllerGetAxis returns the
        // expected value at each. Sentinel float.NaN = not in park mode.
        public float ParkX = float.NaN;
        public float ParkY = float.NaN;
    }

    static int Emulate(string[] profileIds)
    {
        // Optional --paused-at-zero flag: every pattern thread starts in
        // park mode at (0, 0). Useful when a downstream consumer (Cemu's
        // input config dialog, Citron, anything that auto-calibrates the
        // resting stick position on first poll) needs to see the controller
        // sitting still at center before the time-varying circle pattern
        // kicks in. After observing zero, send 'park off' over stdin to
        // start the circle.
        bool startPaused = profileIds.Contains("--paused-at-zero");
        bool startMarked = profileIds.Contains("--mark");
        // --rate-hz N : override the per-virtual SubmitState rate (default 250 Hz,
        // which comes from the pattern thread's Thread.Sleep(4)). PadForge polls
        // at 1 kHz; issue #3 uses 1 kHz and 125 Hz for saturation-rate tests.
        int rateHz = 250;
        for (int i = 0; i < profileIds.Length; i++)
        {
            if (profileIds[i] == "--rate-hz" && i + 1 < profileIds.Length
                && int.TryParse(profileIds[i + 1], out int r) && r > 0 && r <= 4000)
            {
                rateHz = r;
                profileIds = profileIds.Where((_, idx) => idx != i && idx != i + 1).ToArray();
                break;
            }
        }
        profileIds = profileIds.Where(p => p != "--paused-at-zero" && p != "--mark").ToArray();
        if (profileIds.Length == 0)
            return Error("Usage: HIDMaestroTest emulate [--paused-at-zero] [--mark] [--rate-hz N] <profile-id> [profile-id ...]");
        Console.WriteLine($"  Submission rate: {rateHz} Hz (~{1000 / rateHz} ms/frame)");

        using var ctx = new HMContext();
        int loaded = ctx.LoadDefaultProfiles();
        Console.WriteLine($"  Loaded {loaded} profiles (embedded)");

        Console.Write("  Installing driver... ");
        ctx.InstallDriver();
        Console.WriteLine("OK");

        // Phase 1: create all controllers sequentially
        var slots = new List<RunningController>();
        for (int i = 0; i < profileIds.Length; i++)
        {
            var profile = ctx.GetProfile(profileIds[i]);
            if (profile == null) return Error($"Profile not found: {profileIds[i]}");
            Console.WriteLine($"  Creating controller {i}: {profile.Id} ({profile.Name})");
            var slot = new RunningController { Ctrl = ctx.CreateController(profile) };
            // Pre-park BEFORE the pattern thread starts so the very first
            // submitted frame is (0, 0), not whatever the circle was at.
            if (startPaused) { slot.ParkX = 0f; slot.ParkY = 0f; }
            HookOutputReceived(slot.Ctrl, i);
            slots.Add(slot);
        }

        // Phase 1.5: re-apply friendly names (PnP race fix — see HMContext.FinalizeNames doc).
        Console.Write("  Finalizing device names... ");
        ctx.FinalizeNames();
        Console.WriteLine("OK");

        // --mark: auto-activate mark mode before the pattern threads start, so
        // each controller's pattern thread comes up with MarkButton=i set and
        // begins submitting the marker frame on its first iteration. Used by
        // automated diagnostics that can't reliably send "mark" via stdin.
        if (startMarked)
        {
            for (int i = 0; i < slots.Count; i++) slots[i].MarkButton = i;
            Console.WriteLine($"  --mark: marked {slots.Count} controller(s) — each holds button=its index");
        }

        Console.WriteLine($"\n  All {slots.Count} controller(s) ready.\n");

        // Phase 2: input threads — one test-pattern thread per controller.
        s_patternSleepMs = System.Math.Max(1000 / rateHz, 1);
        // Raise timer resolution to 1 ms so Thread.Sleep(1) doesn't coarsen to
        // 15.6 ms. Honored process-wide; released at emulate exit.
        if (rateHz >= 500) timeBeginPeriod(1);
        for (int i = 0; i < slots.Count; i++)
            StartPatternThread(slots, i);

        // Console reader: "quit" to exit, "<idx> <profile-id>" to live-swap a controller.
        Console.WriteLine("  Sending input. Commands:");
        Console.WriteLine("    quit                          exit");
        Console.WriteLine("    pause                         stop submitting input frames (idle CPU test)");
        Console.WriteLine("    resume                        resume submitting input frames");
        Console.WriteLine("    mark                          static frame: each ctrl holds button = its index (browser-order test)");
        Console.WriteLine("    unmark                        leave mark mode and resume the time-varying pattern");
        Console.WriteLine("    park <idx|all> <x> <y>        pin slot N's left stick to literal x,y in [-1..+1]");
        Console.WriteLine("    park off                      leave park mode and resume the time-varying pattern");
        Console.WriteLine("    remove <index>                dispose a single controller (others stay live)");
        Console.WriteLine("    <index> <profile-id>          replace controller at index with new profile");
        while (Console.ReadLine() is string line)
        {
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
            if (line.Equals("pause", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in slots) s.Paused = true;
                Console.WriteLine($"  paused {slots.Count} pattern thread(s) — driver should now be idle");
                continue;
            }
            if (line.Equals("resume", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in slots) s.Paused = false;
                Console.WriteLine($"  resumed {slots.Count} pattern thread(s)");
                continue;
            }
            if (line.Equals("mark", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < slots.Count; i++) slots[i].MarkButton = i;
                Console.WriteLine($"  marked {slots.Count} controller(s) — each holds button=its index");
                continue;
            }
            if (line.Equals("unmark", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in slots) s.MarkButton = -1;
                Console.WriteLine($"  unmarked {slots.Count} controller(s) — back to time-varying pattern");
                continue;
            }
            // park <idx> <x> <y>     pin slot N's left stick to literal x,y in [-1..+1]
            // park all <x> <y>       pin every slot to the same x,y
            // park off               leave park mode (resume time-varying pattern)
            if (line.StartsWith("park", StringComparison.OrdinalIgnoreCase)
                && (line.Length == 4 || line[4] == ' '))
            {
                var sub = line.Substring(4).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (sub.Length == 1 && sub[0].Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var s in slots) { s.ParkX = float.NaN; s.ParkY = float.NaN; }
                    Console.WriteLine($"  park off — {slots.Count} controller(s) back to time-varying pattern");
                    continue;
                }
                if (sub.Length == 3
                    && float.TryParse(sub[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float px)
                    && float.TryParse(sub[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float py))
                {
                    if (sub[0].Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var s in slots) { s.ParkX = px; s.ParkY = py; }
                        Console.WriteLine($"  parked all {slots.Count} controller(s) at ({px:F3}, {py:F3})");
                    }
                    else if (int.TryParse(sub[0], out int parkIdx) && parkIdx >= 0 && parkIdx < slots.Count)
                    {
                        slots[parkIdx].ParkX = px;
                        slots[parkIdx].ParkY = py;
                        Console.WriteLine($"  parked slot {parkIdx} at ({px:F3}, {py:F3})");
                    }
                    else
                    {
                        Console.WriteLine($"  ! park: bad index '{sub[0]}'");
                    }
                    continue;
                }
                Console.WriteLine($"  ! park usage: park <idx|all> <x> <y>  |  park off");
                continue;
            }

            // remove <index> — dispose a single controller without replacement
            if (line.StartsWith("remove ", StringComparison.OrdinalIgnoreCase))
            {
                var rmArg = line.Substring(7).Trim();
                if (int.TryParse(rmArg, out int rmIdx) && rmIdx >= 0 && rmIdx < slots.Count)
                {
                    var s = slots[rmIdx];
                    if (s.Ctrl == null!)
                    {
                        Console.WriteLine($"  slot {rmIdx} already removed");
                    }
                    else
                    {
                        var rmSw = Stopwatch.StartNew();
                        try { s.Cts.Cancel(); } catch { }
                        try { s.Thread?.Join(2000); } catch { }
                        try { s.Ctrl.Dispose(); } catch { }
                        s.Ctrl = null!;
                        Console.WriteLine($"  removed slot {rmIdx} in {rmSw.ElapsedMilliseconds} ms — {slots.Count(x => x.Ctrl != null!)} controller(s) remain");
                    }
                }
                else
                {
                    Console.WriteLine($"  ! remove: bad index '{rmArg}' (0..{slots.Count - 1})");
                }
                continue;
            }

            // Live profile switch: "<index> <profile-id>"
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out int swapIdx))
            {
                if (swapIdx < 0 || swapIdx >= slots.Count)
                {
                    Console.WriteLine($"  ! index {swapIdx} out of range (0..{slots.Count - 1})");
                    continue;
                }
                var newProfile = ctx.GetProfile(parts[1]);
                if (newProfile == null)
                {
                    Console.WriteLine($"  ! profile '{parts[1]}' not found");
                    continue;
                }
                Console.WriteLine($"  === Swapping slot {swapIdx} → {newProfile.Id} ===");
                LiveSwap(ctx, slots, swapIdx, newProfile);
                continue;
            }

            Console.WriteLine($"  ! unknown command: {line}");
        }

        var cleanupSw = Stopwatch.StartNew();
        for (int i = 0; i < slots.Count; i++)
        {
            var perCtrl = Stopwatch.StartNew();
            var s = slots[i];
            try { s.Cts.Cancel(); } catch { }
            try { s.Thread?.Join(2000); } catch { }
            try { s.Ctrl.Dispose(); } catch { }
            Console.WriteLine($"  disposed slot {i} in {perCtrl.ElapsedMilliseconds} ms");
        }
        Console.WriteLine($"  total cleanup: {cleanupSw.ElapsedMilliseconds} ms");
        if (rateHz >= 500) timeEndPeriod(1);
        return 0;
    }

    // ── custom ──
    //
    // Demonstrates custom controller creation: a 16-button DualSense variant
    // and a fully custom flight stick built from scratch. Both use the public
    // HMProfileBuilder + HidDescriptorBuilder APIs.

    static int EmulateCustom()
    {
        using var ctx = new HMContext();
        int loaded = ctx.LoadDefaultProfiles();
        Console.WriteLine($"  Loaded {loaded} profiles");

        Console.Write("  Installing driver... ");
        ctx.InstallDriver();
        Console.WriteLine("OK");

        // ── Custom DualSense with 16 buttons ────────────────────────────
        var dsProfile = ctx.GetProfile("dualsense")!;
        Console.WriteLine($"\n  Base DualSense: {dsProfile.ButtonCount} buttons, {dsProfile.AxisCount} axes");

        byte[] customDsDesc = new HidDescriptorBuilder()
            .Gamepad()
            .AddStick("Left", bits: 8)
            .AddStick("Right", bits: 8)
            .AddTrigger("Left", bits: 8)
            .AddTrigger("Right", bits: 8)
            .AddButtons(16)
            .AddHat()
            .Build();

        var customDs = new HMProfileBuilder()
            .FromProfile(dsProfile)
            .Id("dualsense-16btn")
            .Name("DualSense (16 buttons)")
            .Descriptor(customDsDesc)
            .InputReportSize(new HidDescriptorBuilder()
                .Gamepad().AddStick("Left",8).AddStick("Right",8)
                .AddTrigger("Left",8).AddTrigger("Right",8)
                .AddButtons(16).AddHat().InputReportByteSize)
            .Notes("Custom: 16 buttons instead of 15")
            .Build();

        Console.Write($"  Creating {customDs.Name} ({customDs.ButtonCount} buttons)... ");
        using var ctrl0 = ctx.CreateController(customDs);
        Console.WriteLine("OK");

        // ── Fully custom flight stick ────────────────────────────────────
        byte[] stickDesc = new HidDescriptorBuilder()
            .Joystick()
            .AddStick("Left", bits: 16)
            .AddTrigger("Left", bits: 8)
            .AddTrigger("Right", bits: 8)
            .AddButtons(6)
            .AddHat()
            .Build();

        var flightStick = new HMProfileBuilder()
            .Id("custom-flight-stick")
            .Name("Custom Flight Stick")
            .Vendor("Custom")
            .Vid(0x0483).Pid(0x0001)
            .ProductString("My Flight Stick")
            .ManufacturerString("Homebrew")
            .Type("flightstick")
            .Connection("usb")
            .Descriptor(stickDesc)
            .InputReportSize(new HidDescriptorBuilder()
                .Joystick().AddStick("Left",16).AddTrigger("Left",8)
                .AddTrigger("Right",8).AddButtons(6).AddHat().InputReportByteSize)
            .Build();

        Console.Write($"  Creating {flightStick.Name} (VID={flightStick.VendorId:X4} " +
                      $"PID={flightStick.ProductId:X4})... ");
        using var ctrl1 = ctx.CreateController(flightStick);
        Console.WriteLine("OK");

        ctx.FinalizeNames();
        Console.WriteLine($"\n  Both custom controllers ready. Open joy.cpl to inspect.");
        Console.WriteLine("  Sending input... (quit to exit)\n");

        var sw = Stopwatch.StartNew();
        var cts = new CancellationTokenSource();
        var inputThread = new Thread(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                double t = sw.Elapsed.TotalSeconds;
                double angle = t * 2 * Math.PI;

                ctrl0.SubmitState(new HMGamepadState
                {
                    LeftStickX   = (float)Math.Cos(angle),
                    LeftStickY   = (float)Math.Sin(angle),
                    RightStickX  = (float)Math.Sin(angle * 0.5),
                    RightStickY  = (float)Math.Cos(angle * 0.5),
                    LeftTrigger  = (float)(0.5 + 0.5 * Math.Sin(t * 3)),
                    RightTrigger = (float)(0.5 + 0.5 * Math.Cos(t * 3)),
                    Buttons      = (HMButton)(1u << ((int)t % 16)),
                    Hat          = (HMHat)(1 + ((int)(t * 2) % 8)),
                });

                ctrl1.SubmitState(new HMGamepadState
                {
                    LeftStickX   = (float)Math.Sin(t * 1.5),
                    LeftStickY   = (float)Math.Cos(t * 1.5),
                    LeftTrigger  = (float)(0.5 + 0.5 * Math.Sin(t)),
                    RightTrigger = (float)(0.5 + 0.5 * Math.Cos(t * 2)),
                    Buttons      = ((int)t % 3 == 0) ? HMButton.A : HMButton.None,
                    Hat          = (HMHat)(1 + ((int)(t * 3) % 8)),
                });

                Thread.Sleep(4);
            }
        }) { IsBackground = true };
        inputThread.Start();

        while (Console.ReadLine() is string line)
        {
            if (line.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
        }
        cts.Cancel();
        return 0;
    }

    /// <summary>Cancel the pattern thread for a slot, dispose its controller,
    /// create a fresh controller pinned to the same index with the new profile,
    /// re-hook the output event, and restart the pattern thread.</summary>
    static void LiveSwap(HMContext ctx, List<RunningController> slots, int idx, HMProfile profile)
    {
        var old = slots[idx];
        try { old.Cts.Cancel(); } catch { }
        try { old.Thread?.Join(2000); } catch { }
        try { old.Ctrl.Dispose(); } catch { }

        // Pin to the same index so live switching preserves slot identity
        // (XInput slot, joy.cpl ordering, etc.). HMContext.CreateControllerAt
        // throws if the index is still in use; old.Ctrl.Dispose above frees it.
        var fresh = new RunningController { Ctrl = ctx.CreateControllerAt(idx, profile) };
        HookOutputReceived(fresh.Ctrl, idx);
        slots[idx] = fresh;
        StartPatternThread(slots, idx);
    }

    static void StartPatternThread(List<RunningController> slots, int idx)
    {
        var slot = slots[idx];
        slot.Thread = new Thread(() => SendTestPattern(slot, idx, slot.Cts.Token))
        {
            IsBackground = true,
            Name = $"Pattern_{idx}",
        };
        slot.Thread.Start();
    }

    /// <summary>Subscribe to OutputReceived for the given controller and print
    /// each packet (decoded if recognized, hex-dumped otherwise). The handler
    /// runs on the SDK's polling thread, not the test app's UI thread, so
    /// keep it cheap and Console-only.</summary>
    static void HookOutputReceived(HMController ctrl, int idx)
    {
        ctrl.OutputReceived += (c, pkt) =>
        {
            string label = c.Profile.Inner.DeviceDescription
                           ?? c.Profile.Inner.ProductString
                           ?? c.Profile.Id;
            string decoded = DecodeOutputPacket(c.Profile, pkt);
            // Raw-byte dump in addition to decoded line, so the on-wire layout
            // is inspectable directly instead of trusting the decode. Per Opus
            // 2026-04-18: "the driver-side log is load-bearing for every
            // hypothesis; if it's not trusted and double-checked with raw-byte
            // dumps early, every hypothesis built on top of it inherits the
            // uncertainty."
            var dbg = pkt.Data.Span;
            int n = System.Math.Min(dbg.Length, 16);
            var raw = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++) { if (i > 0) raw.Append(' '); raw.Append(dbg[i].ToString("X2")); }
            Console.WriteLine($"  [out{idx} {label}] {pkt.Source} id=0x{pkt.ReportId:X2} raw[{dbg.Length}B]={raw} | {decoded}");
        };
    }

    /// <summary>Best-effort one-line decode of common output packet types.
    /// Falls back to a hex dump for unrecognized payloads.</summary>
    static string DecodeOutputPacket(HMProfile profile, in HMOutputPacket pkt)
    {
        var data = pkt.Data.Span;

        // XInput rumble: 5 bytes [0x00, 0x00, lo, hi, 0x00] (xusb22 GET_STATE response format)
        if (pkt.Source == HMOutputSource.XInput && data.Length >= 5)
            return $"XInput rumble lo={data[2]} hi={data[3]}";

        // DualSense / DS4 output report (HID OUTPUT, report ID 0x05 / 0x02):
        // first few bytes carry rumble + LED + adaptive trigger config.
        if (pkt.Source == HMOutputSource.HidOutput
            && profile.VendorId == 0x054C && data.Length >= 4)
        {
            // Report ID 0x05 (DualSense USB) layout: byte[2..3] are rumble lo/hi
            return $"PS rumble lo={data[2]} hi={data[3]} ({data.Length}B)";
        }

        // Xbox 360 / One HID rumble output report (when sent via HID OUTPUT)
        if (pkt.Source == HMOutputSource.HidOutput
            && profile.VendorId == 0x045E && data.Length >= 8)
            return $"XB rumble {data[3]:X2} {data[4]:X2} {data[5]:X2} {data[6]:X2} ({data.Length}B)";

        // Fallback: hex dump first 16 bytes
        int n = Math.Min(data.Length, 16);
        var sb = new System.Text.StringBuilder($"{data.Length}B [");
        for (int i = 0; i < n; i++) { if (i > 0) sb.Append(' '); sb.Append(data[i].ToString("X2")); }
        if (data.Length > n) sb.Append(" …");
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>Per-controller exercise pattern, ported verbatim from the
    /// pre-SDK test app. Each controller gets a unique speed/direction/phase
    /// so that all controllers are visually distinguishable across every API
    /// surface (XInput slots, joy.cpl, browser Gamepad Tester, Dolphin, etc).
    /// Left stick traces a circle at 92% deflection (inset from edges so it
    /// doesn't peg the limits). Right stick stays centered. A button toggles
    /// every second. Triggers do a clean 15-second sweep through 5 phases
    /// rather than continuous oscillation.
    /// </summary>
    // Per-virtual submission period in milliseconds. Default 4 ms = 250 Hz;
    // overridden via emulate --rate-hz flag. Set from Emulate() before pattern
    // threads start; all threads read the same value.
    static volatile int s_patternSleepMs = 4;

    // timeBeginPeriod support so Sleep(1) actually yields ~1 ms (default Windows
    // timer resolution coarsens Sleep(1) to 15.6 ms on non-multimedia threads).
    // Needed to reproduce PadForge's 1 kHz SubmitState rate in the issue #3 test
    // battery — without this, --rate-hz 1000 silently runs at ~200 Hz.
    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint uPeriod);
    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    static extern uint timeEndPeriod(uint uPeriod);

    static void SendTestPattern(RunningController slot, int ctrlIndex, CancellationToken ct)
    {
        HMController ctrl = slot.Ctrl;
        var sw = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            int sleepMs = s_patternSleepMs;
            int gateSleepMs = System.Math.Max(sleepMs * 2, 1);  // mark/park gates: 2x the normal rate
            // Pause gate: when paused, sleep in 100ms chunks (so we still
            // notice cancellation promptly) and submit no frames. This
            // exercises the driver's idle path so we can measure that the
            // event-driven worker thread truly costs ~0% CPU when nothing
            // is being submitted.
            if (slot.Paused)
            {
                try { Thread.Sleep(100); } catch { break; }
                continue;
            }
            // Mark gate: submit a static frame holding only the marker button.
            // The pattern thread keeps re-submitting at the normal rate so the
            // browser snapshot reliably catches it. Sticks centered, no triggers.
            int markBtn = slot.MarkButton;
            if (markBtn >= 0)
            {
                var markState = new HMGamepadState
                {
                    Buttons = (HMButton)(1u << markBtn),
                };
                try { ctrl.SubmitState(in markState); } catch { break; }
                try { Thread.Sleep(gateSleepMs); } catch { break; }
                continue;
            }
            // Park gate: hold left stick at a literal (ParkX, ParkY) for SDL2
            // readback testing. Right stick centered, triggers released, no
            // buttons. Read these as locals to get a consistent snapshot
            // (volatile float read isn't atomic in C# but it's stable enough
            // for parked-position testing where the values change rarely).
            float px = slot.ParkX, py = slot.ParkY;
            if (!float.IsNaN(px) && !float.IsNaN(py))
            {
                var parkState = new HMGamepadState
                {
                    LeftStickX = Math.Clamp(px, -1f, 1f),
                    LeftStickY = Math.Clamp(py, -1f, 1f),
                };
                try { ctrl.SubmitState(in parkState); } catch { break; }
                try { Thread.Sleep(gateSleepMs); } catch { break; }
                continue;
            }
            double t = sw.Elapsed.TotalSeconds;

            // Per-controller circle: differentiated speed, direction, and phase.
            double speed = 0.5 + ctrlIndex * 0.3;            // 0.5..N Hz
            double direction = (ctrlIndex % 2 == 0) ? 1.0 : -1.0;
            double phase = ctrlIndex * Math.PI / 2;
            double angle = t * Math.PI * 2 * speed * direction + phase;

            // Inset stick range [0.04..0.96] in HidReportBuilder's [0..1] space.
            // The HMGamepadState API uses [-1..+1], so map back: +1 → 0.96,
            // -1 → 0.04. The factor 0.92 = 2 * 0.46 keeps the circle off the
            // physical max so it doesn't trigger edge dead-zones.
            float stickX = (float)(0.92 * Math.Sin(angle));
            float stickY = (float)(0.92 * Math.Cos(angle));

            // Trigger sweep: 15-second cycle through 5 phases.
            //  0-3:   LT ramps 0 → 1
            //  3-6:   LT ramps 1 → 0
            //  6-9:   RT ramps 0 → 1
            //  9-12:  LT ramps 0 → 1 while RT holds 1
            // 12-15:  both ramp 1 → 0
            double tp = t % 15.0;
            float lt, rt;
            if      (tp < 3.0)  { lt = (float)(tp / 3.0);            rt = 0f; }
            else if (tp < 6.0)  { lt = (float)(1.0 - (tp - 3.0)/3.0); rt = 0f; }
            else if (tp < 9.0)  { lt = 0f;                            rt = (float)((tp - 6.0)/3.0); }
            else if (tp < 12.0) { lt = (float)((tp - 9.0)/3.0);       rt = 1f; }
            else                { float k = (float)(1.0 - (tp - 12.0)/3.0); lt = k; rt = k; }

            var state = new HMGamepadState
            {
                LeftStickX  = stickX,
                LeftStickY  = stickY,
                RightStickX = 0f,
                RightStickY = 0f,
                LeftTrigger  = lt,
                RightTrigger = rt,
                // A at 1Hz, Share at 0.33Hz — distinct cadences so each is
                // individually observable in any consumer (joy.cpl, browser
                // Gamepad Tester). Guide is deliberately OMITTED because the
                // Windows shell fires a Guide-long-press haptic ack to slot 0
                // whenever Guide is seen pressed on our virtual, which
                // pollutes the xusbshim/HMCOMP log with `[00 00 00 7F 02]`
                // and masks real Chromium playEffect dispatch.
                Buttons      = (((int)t % 2 == 0) ? HMButton.A : HMButton.None)
                             | ((((int)(t / 3)) % 2 == 0) ? HMButton.Share : HMButton.None),
            };
            try { ctrl.SubmitState(in state); } catch { break; }
            try { Thread.Sleep(sleepMs); } catch { break; }
        }
    }

    // ── Profile browse commands ──

    static int ListProfiles()
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        Console.WriteLine($"-- Controller Profile Database ({ctx.AllProfiles.Count}) --\n");
        foreach (var p in ctx.AllProfiles)
            Console.WriteLine($"  {p.Id,-35} {p.Name,-45} {p.VendorId:X4}:{p.ProductId:X4}");
        return 0;
    }

    static int SearchProfiles(string query)
    {
        if (string.IsNullOrEmpty(query))
            return Error("Usage: HIDMaestroTest search <query>");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var results = ctx.AllProfiles.Where(p =>
            p.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            p.Vendor.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        Console.WriteLine($"-- Search results for \"{query}\" ({results.Count} found) --\n");
        foreach (var p in results)
            Console.WriteLine($"  {p.Id,-35} {p.Name,-45} {p.VendorId:X4}:{p.ProductId:X4}");

        if (results.Count == 0)
            Console.WriteLine("  No profiles found.");
        return 0;
    }

    static int ShowProfile(string id)
    {
        if (string.IsNullOrEmpty(id))
            return Error("Usage: HIDMaestroTest info <profile-id>");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var p = ctx.GetProfile(id);
        if (p == null)
        {
            Console.Error.WriteLine($"Profile '{id}' not found. Use 'list' to see all profiles.");
            return 1;
        }

        Console.WriteLine($"-- Profile: {p.Name} --\n");
        Console.WriteLine($"  ID:             {p.Id}");
        Console.WriteLine($"  Vendor:         {p.Vendor}");
        Console.WriteLine($"  VID:            0x{p.VendorId:X4}");
        Console.WriteLine($"  PID:            0x{p.ProductId:X4}");
        Console.WriteLine($"  Product String: {p.ProductString}");
        Console.WriteLine($"  Connection:     {p.Inner.Connection ?? "usb"}");
        Console.WriteLine($"  Driver Mode:    {p.Inner.DriverMode ?? "(default)"}");
        Console.WriteLine($"  Report Size:    {p.Inner.InputReportSize} bytes");
        Console.WriteLine();

        // Parse and display the HID descriptor layout
        var descBytes = p.Inner.GetDescriptorBytes();
        if (descBytes != null)
        {
            var builder = HIDMaestro.Internal.HidReportBuilder.Parse(descBytes);
            builder.PrintLayout();

            // List all parsed input fields for full visibility
            Console.WriteLine($"\n  All input fields ({builder.InputFields.Count}):");
            foreach (var f in builder.InputFields)
            {
                string kind = f.IsConstant ? "Const" : "Data ";
                string page = f.UsagePage switch
                {
                    0x01 => "GenericDesktop",
                    0x02 => "Simulation",
                    0x09 => "Button",
                    0x0C => "Consumer",
                    0x0F => "PID",
                    _ => $"0x{f.UsagePage:X2}"
                };
                string usage = (f.UsagePage, f.Usage) switch
                {
                    (0x01, 0x30) => "X",
                    (0x01, 0x31) => "Y",
                    (0x01, 0x32) => "Z",
                    (0x01, 0x33) => "Rx",
                    (0x01, 0x34) => "Ry",
                    (0x01, 0x35) => "Rz",
                    (0x01, 0x39) => "Hat",
                    (0x01, 0x40) => "Vx",
                    (0x01, 0x41) => "Vy",
                    (0x01, 0x80) => "SysControl",
                    (0x01, 0x85) => "SysMainMenu",
                    (0x02, 0xC4) => "Accelerator",
                    (0x02, 0xC5) => "Brake",
                    (0x09, _) => $"Btn{f.Usage}",
                    _ => $"0x{f.Usage:X2}"
                };
                Console.WriteLine($"    [{kind}] {page}/{usage,-12} bit {f.BitOffset,4}, {f.BitSize,2}b, range [{f.LogicalMin}..{f.LogicalMax}]");
            }

            Console.WriteLine($"\n  Descriptor hex ({descBytes.Length} bytes):");
            Console.Write("    ");
            for (int i = 0; i < descBytes.Length; i++)
            {
                Console.Write($"{descBytes[i]:X2}");
                if ((i + 1) % 32 == 0) Console.Write("\n    ");
            }
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine("  (no descriptor)");
        }
        return 0;
    }

    // ── cleanup ──

    static int RunCleanup()
    {
        Console.WriteLine("Removing all HIDMaestro virtual devices...");
        HMContext.RemoveAllVirtualControllers();
        Console.WriteLine("  OK");
        return 0;
    }

    // ── oem ──
    //
    // Wraps HMOemNameOverride so you can set / clear / inspect the DirectInput
    // OEM-name table entry for any VID:PID from the CLI. Useful for verifying
    // joy.cpl behavior without building a full consumer.

    static int OemCommand(string[] args)
    {
        if (args.Length == 0)
            return Error("Usage: oem {set|clear|recover|list} [args...]");

        switch (args[0].ToLowerInvariant())
        {
            case "set":
                if (args.Length < 4) return Error("Usage: oem set <vid> <pid> <label>");
                if (!TryParseHex(args[1], out ushort setVid)) return Error($"Bad vid: {args[1]}");
                if (!TryParseHex(args[2], out ushort setPid)) return Error($"Bad pid: {args[2]}");
                string label = string.Join(' ', args.Skip(3));
                HMOemNameOverride.Set(setVid, setPid, label);
                Console.WriteLine($"  Set OEM Name for VID_{setVid:X4}&PID_{setPid:X4} = \"{label}\"");
                Console.WriteLine($"  (joy.cpl and DirectInput will now show this label for this VID:PID)");
                return 0;

            case "clear":
                if (args.Length < 3) return Error("Usage: oem clear <vid> <pid>");
                if (!TryParseHex(args[1], out ushort clrVid)) return Error($"Bad vid: {args[1]}");
                if (!TryParseHex(args[2], out ushort clrPid)) return Error($"Bad pid: {args[2]}");
                HMOemNameOverride.Clear(clrVid, clrPid);
                Console.WriteLine($"  Cleared override for VID_{clrVid:X4}&PID_{clrPid:X4}");
                return 0;

            case "recover":
                int restored = HMOemNameOverride.RecoverOrphans();
                Console.WriteLine($"  Recovered {restored} orphan override(s).");
                return 0;

            case "list":
                var entries = HMOemNameOverride.ListActive();
                if (entries.Count == 0)
                {
                    Console.WriteLine("  (no active overrides)");
                    return 0;
                }
                Console.WriteLine($"  {"VID:PID",-20} {"Original Existed",-18} Original OEM Name");
                foreach (var e in entries)
                {
                    string existed = e.OriginalKeyExisted ? "yes" : "no";
                    string orig = e.OriginalOemName ?? "(no value)";
                    Console.WriteLine($"  {e.VidPid,-20} {existed,-18} {orig}");
                }
                return 0;

            default:
                return Error($"Unknown oem subcommand: {args[0]}");
        }
    }

    static bool TryParseHex(string s, out ushort v)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return ushort.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out v);
    }

    // ── issue6-repro ──
    //
    // Creates the exact custom profile from issue #6 (2 sticks 16b + 2 triggers
    // 8b + hat + 11 buttons, VID beef PID f000) and keeps it live for 30s so
    // an external probe can inspect Chrome's Gamepad API, joy.cpl, or the
    // PnP tree while it's active. Emits a ready marker and periodic
    // heartbeats. Requires admin.
    static int Issue6ReproCommand()
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        Console.Write("  Installing driver... ");
        ctx.InstallDriver();
        Console.WriteLine("OK");

        byte[] desc = new HidDescriptorBuilder()
            .Gamepad()
            .AddStick("Left", 16)
            .AddStick("Right", 16)
            .AddTrigger("Left", 8)
            .AddTrigger("Right", 8)
            .AddHat()
            .AddButtons(11)
            .Build();

        var profile = new HMProfileBuilder()
            .Id("padforge-custom-issue6")
            .Name("Custom")
            .Vendor("Custom")
            .Vid(0xBEEF)
            .Pid(0xF000)
            .ProductString("PadForge Game Controller")
            .ManufacturerString("PadForge")
            .Type("gamepad")
            .Connection("usb")
            .Descriptor(desc)
            .Build();

        Console.Write($"  Creating {profile.Name} (VID_{profile.VendorId:X4}&PID_{profile.ProductId:X4})... ");
        using var ctrl = ctx.CreateController(profile);
        Console.WriteLine("OK");
        Console.WriteLine($"  Descriptor: {desc.Length} bytes");
        Console.WriteLine($"  Hex:        {Convert.ToHexString(desc).ToLowerInvariant()}");
        Console.WriteLine();
        Console.WriteLine("  ==== READY ==== open chrome://gamepad or joy.cpl to inspect");
        Console.WriteLine("  (holding virtual live for 3 min; feeding a left-stick circle so entries show activity)");
        Console.WriteLine("  (press Ctrl+C or close the window to tear down early)");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 180_000)
        {
            double t = sw.Elapsed.TotalSeconds;
            var state = new HMGamepadState
            {
                LeftStickX  = (float)Math.Cos(t * 2 * Math.PI),
                LeftStickY  = (float)Math.Sin(t * 2 * Math.PI),
                RightStickX = (float)Math.Cos(t * 2 * Math.PI * 2),
                RightStickY = (float)Math.Sin(t * 2 * Math.PI * 2),
                LeftTrigger  = 0.25f,
                RightTrigger = 0.75f,
                Buttons = HMButton.A,
            };
            ctrl.SubmitState(in state);
            Thread.Sleep(8);
            if ((int)sw.Elapsed.TotalSeconds % 5 == 0 && sw.ElapsedMilliseconds % 5000 < 10)
                Console.WriteLine($"  [heartbeat t={sw.Elapsed.TotalSeconds:F0}s]");
        }
        Console.WriteLine("  Done. Disposing virtual.");
        return 0;
    }

    // ── build-descriptor ──
    //
    // Reconstructs the HidDescriptorBuilder output for the padforge-custom
    // repro shape (2 sticks + 2 triggers + hat + 11 buttons) and prints the
    // raw descriptor bytes plus the parsed layout via HidReportBuilder. Used
    // to verify that the builder doesn't emit phantom Const axes (issue #6).

    static int BuildDescriptorCommand()
    {
        byte[] desc = new HidDescriptorBuilder()
            .Gamepad()
            .AddStick("Left", 16)
            .AddStick("Right", 16)
            .AddTrigger("Left", 8)
            .AddTrigger("Right", 8)
            .AddHat()
            .AddButtons(11)
            .Build();
        Console.WriteLine($"Descriptor: {desc.Length} bytes");
        Console.WriteLine($"Hex:        {Convert.ToHexString(desc).ToLowerInvariant()}");
        Console.WriteLine();
        var parsed = HIDMaestro.Internal.HidReportBuilder.Parse(desc);
        parsed.PrintLayout();
        Console.WriteLine($"\n  All input fields ({parsed.InputFields.Count}):");
        foreach (var f in parsed.InputFields)
        {
            string kind = f.IsConstant ? "Const" : "Data ";
            string page = f.UsagePage switch
            {
                0x01 => "GenericDesktop",
                0x09 => "Button",
                0xFF00 => "VendorDefined",
                _ => $"0x{f.UsagePage:X4}",
            };
            Console.WriteLine($"    [{kind}] {page,-16}/0x{f.Usage:X2}   bit {f.BitOffset,3}, {f.BitSize,2}b, range [{f.LogicalMin}..{f.LogicalMax}]");
        }
        return 0;
    }

    // ── extract-profile ──
    //
    // Enumerates connected HID devices and extracts their HIDMaestro
    // profile JSON from the real preparsed-data via HMDeviceExtractor.
    // Read-only; no admin. Useful for profile contribution (the 99
    // undeployable profiles from issue #4) and cross-verification of
    // shipped profiles against real hardware.

    static int ExtractProfileCommand(string[] args)
    {
        if (args.Length == 0)
        {
            // Listing mode.
            var devices = HMDeviceExtractor.ListDevices();
            Console.WriteLine($"  Found {devices.Count} HID interface(s):");
            Console.WriteLine($"  {"VID:PID",-12} {"Usage",-16} {"Product",-40} {"Manufacturer"}");
            Console.WriteLine($"  {new string('-', 12)} {new string('-', 16)} {new string('-', 40)} {new string('-', 20)}");
            foreach (var d in devices.OrderBy(x => x.VendorId).ThenBy(x => x.ProductId))
            {
                string vidpid = $"{d.VendorId:X4}:{d.ProductId:X4}";
                string usage = $"0x{d.TopLevelUsagePage:X2}:0x{d.TopLevelUsage:X2}";
                string product = d.ProductString ?? "(unknown)";
                if (product.Length > 40) product = product.Substring(0, 37) + "...";
                string mfg = d.ManufacturerString ?? "";
                Console.WriteLine($"  {vidpid,-12} {usage,-16} {product,-40} {mfg}");
            }
            Console.WriteLine();
            Console.WriteLine("  Usage: HIDMaestroTest extract-profile <vid> <pid>");
            return 0;
        }

        if (args.Length < 2)
            return Error("Usage: extract-profile <vid> <pid>");
        if (!TryParseHex(args[0], out ushort vid)) return Error($"Bad vid: {args[0]}");
        if (!TryParseHex(args[1], out ushort pid)) return Error($"Bad pid: {args[1]}");

        try
        {
            var profile = HMDeviceExtractor.ExtractByVidPid(vid, pid);
            string json = HMDeviceExtractor.ToJson(profile);
            Console.Write(json);
            return 0;
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    // ── sdk-demo ──

    /// <summary>Minimal SDK consumer demo — exercises HMContext → CreateController → SubmitState.
    /// Creates one controller, feeds a left-stick circle for 5s, then cleans up.</summary>
    static int SdkDemo(string[] args)
    {
        string profileId = args.Length > 0 ? args[0] : "dualsense";
        Console.WriteLine("=== HIDMaestro SDK Demo ===\n");

        using var ctx = new HMContext();
        int loaded = ctx.LoadDefaultProfiles();
        Console.WriteLine($"  Loaded {loaded} profiles (embedded)");

        Console.Write("  Installing driver... ");
        ctx.InstallDriver();
        Console.WriteLine("OK");

        var profile = ctx.GetProfile(profileId);
        if (profile == null) return Error($"Profile '{profileId}' not found");

        Console.Write($"  Creating controller ({profile.Name})... ");
        using var ctrl = ctx.CreateController(profile);
        Console.WriteLine("OK");

        Console.WriteLine("  Sending input (left stick circle, 5s)...");
        var sw = Stopwatch.StartNew();
        int frame = 0;
        while (sw.ElapsedMilliseconds < 5000)
        {
            double t = sw.Elapsed.TotalSeconds * 2 * Math.PI;
            var state = new HMGamepadState
            {
                LeftStickX = (float)Math.Cos(t),
                LeftStickY = (float)Math.Sin(t),
            };
            ctrl.SubmitState(in state);
            frame++;
            Thread.Sleep(4);
        }
        Console.WriteLine($"  Sent {frame} frames ({frame * 1000.0 / sw.ElapsedMilliseconds:F0} Hz)");

        Console.Write("  Disposing... ");
        ctrl.Dispose();
        ctx.Dispose();
        Console.WriteLine("OK\n=== Demo complete ===");
        return 0;
    }
}
