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

        // Elevate — every command here needs admin (device create / driver install / cleanup)
        if (!IsElevated())
        {
            Console.WriteLine("  Requesting elevation (admin required)...\n");
            return RelaunchElevated(args);
        }

        // Safety net: always purge devices on exit so testing leaves no trace
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { HMContext.RemoveAllVirtualControllers(); } catch { }
        };

        // Clean up any leftover devices from previous sessions
        try { HMContext.RemoveAllVirtualControllers(); } catch { }

        if (args.Length == 0)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  HIDMaestroTest emulate <id> [id ...]   Create virtual controller(s) + test pattern");
            Console.WriteLine("  HIDMaestroTest list                    List all controller profiles");
            Console.WriteLine("  HIDMaestroTest search <query>          Search profiles by name/vendor");
            Console.WriteLine("  HIDMaestroTest info <id>               Show profile details");
            Console.WriteLine("  HIDMaestroTest cleanup                 Remove all HIDMaestro virtual devices");
            Console.WriteLine("  HIDMaestroTest sdk-demo [id]           Minimal SDK consumer demo (5s)");
            Console.WriteLine("\nMust run elevated.");
            return 1;
        }

        return args[0].ToLower() switch
        {
            "emulate"  => Emulate(args.Skip(1).ToArray()),
            "list"     => ListProfiles(),
            "search"   => SearchProfiles(args.Length > 1 ? args[1] : ""),
            "info"     => ShowProfile(args.Length > 1 ? args[1] : ""),
            "cleanup"  => RunCleanup(),
            "sdk-demo" => SdkDemo(args.Skip(1).ToArray()),
            _          => Error($"Unknown command: {args[0]}")
        };
    }

    // ── emulate ──

    static int Emulate(string[] profileIds)
    {
        if (profileIds.Length == 0)
            return Error("Usage: HIDMaestroTest emulate <profile-id> [profile-id ...]");

        using var ctx = new HMContext();
        int loaded = ctx.LoadDefaultProfiles();
        Console.WriteLine($"  Loaded {loaded} profiles (embedded)");

        Console.Write("  Installing driver... ");
        ctx.InstallDriver();
        Console.WriteLine("OK");

        // Phase 1: create all controllers sequentially
        var controllers = new List<HMController>();
        for (int i = 0; i < profileIds.Length; i++)
        {
            var profile = ctx.GetProfile(profileIds[i]);
            if (profile == null) return Error($"Profile not found: {profileIds[i]}");
            Console.WriteLine($"  Creating controller {i}: {profile.Id} ({profile.Name})");
            controllers.Add(ctx.CreateController(profile));
        }

        // Phase 1.5: re-apply friendly names. Without this, the FIRST
        // controller often shows the generic INF default ("Game Controller")
        // because PnP driver-bind for the SECOND controller racing with our
        // initial CM_Set_DevNode_PropertyW writes clobbers them.
        Console.Write("  Finalizing device names... ");
        ctx.FinalizeNames();
        Console.WriteLine("OK");

        Console.WriteLine($"\n  All {controllers.Count} controller(s) ready.\n");

        // Phase 2: input threads — one test-pattern thread per controller
        var cts = new CancellationTokenSource();
        var threads = new List<Thread>();
        for (int i = 0; i < controllers.Count; i++)
        {
            int idx = i;
            var t = new Thread(() => SendTestPattern(controllers[idx], idx, cts.Token))
            {
                IsBackground = true,
                Name = $"Pattern_{idx}"
            };
            t.Start();
            threads.Add(t);
        }

        // Console reader — 'quit' to stop
        Console.WriteLine("  Sending input. Type 'quit' to exit.");
        while (Console.ReadLine() is string line)
        {
            if (line.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
        }

        cts.Cancel();
        foreach (var t in threads) t.Join(2000);
        foreach (var c in controllers) c.Dispose();
        return 0;
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
    static void SendTestPattern(HMController ctrl, int ctrlIndex, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
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
                Buttons      = ((int)t % 2 == 0) ? HMButton.A : HMButton.None,
            };
            try { ctrl.SubmitState(in state); } catch { break; }
            try { Thread.Sleep(4); } catch { break; }
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
