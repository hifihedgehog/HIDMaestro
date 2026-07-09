// Virtual VR controller smoke test (issue #32).
//
// Phased: each phase's asserts only run when its prerequisite exists, so
// the probe is useful on a box with no SteamVR (phase 0 only), SteamVR
// installed but closed (phases 0-1), or SteamVR running (all phases).
// Skipped phases print SKIP and do not fail the run; a prerequisite that
// IS present must pass.
//
//   Phase 0  (always)           IPC channel: section + double-claim +
//                               frame pump, via SDK internals.
//   Phase 1  (vrpathreg found)  EnsureInstalled extracts the driver folder
//                               and `vrpathreg show` lists it.
//   Phase 2  (vrserver running) The driver inside vrserver attaches the
//                               channel (DriverConnected flips true), both
//                               hands enumerate through openvr_api as
//                               TrackedDeviceClass_Controller with our
//                               serials, and a trigger press submitted via
//                               SubmitState reads back through
//                               IVRSystem.GetControllerState with a
//                               same-window release as positive control.
//
// Every phase-2 assert polls with a timeout instead of reading once.
// --cleanup additionally runs vrpathreg removedriver + gate cleanup at
// the end (default leaves the registration in place, it's the product
// install). Exit 0 on PASS/SKIP, 1 on any FAIL.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

using HIDMaestro;
using HIDMaestro.Internal;

using Valve.VR;

internal static class Program
{
    static int s_total = 0;
    static int s_failures = 0;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static void Skip(string name, string why)
        => Console.WriteLine($"  [SKIP] {name}  ({why})");

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetDllDirectoryW(string lpPathName);

    static int Main(string[] args)
    {
        bool cleanup = Array.IndexOf(args, "--cleanup") >= 0;
        Console.WriteLine("=== Virtual VR controller smoke (OpenVR driver + HMVRController) ===");

        // Same elevation prerequisite as every HIDMaestro consumer: the
        // Global\ section needs SeCreateGlobalPrivilege and the install
        // gate writes HKLM. Bail with guidance instead of a Win32 crash.
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("  [SKIP] all phases (probe requires elevation, same as CreateController)");
                return 0;
            }
        }

        // ── Phase 0: IPC channel, no SteamVR required ─────────────────────
        Console.WriteLine("\n-- Phase 0: IPC channel --");

        using var left = new HMVRController(HMVRProfile.ValveIndexController, HMVRHand.Left);
        using var right = new HMVRController(HMVRProfile.ValveIndexController, HMVRHand.Right);

        string? vrpathreg = VrDriverBuilder.LocateVrPathReg();
        bool steamVrInstalled = vrpathreg != null;

        if (!steamVrInstalled)
        {
            // Connect() requires the driver registration step; without
            // SteamVR we exercise the channel primitives directly.
            Skip("Connect() end-to-end", "SteamVR not installed; driving VrSharedMemoryIO directly");
            VrSharedMemoryIO.EnsureChannel();
            VrSharedMemoryIO.RegisterHand(0, 1, 0);
            VrSharedMemoryIO.RegisterHand(1, 1, 0);
        }
        else
        {
            Check("left.Connect()", left.Connect());
            Check("right.Connect()", right.Connect());
        }

        bool doubleClaim = false;
        try
        {
            using var dup = new HMVRController(HMVRProfile.ValveIndexController, HMVRHand.Left);
            if (steamVrInstalled) dup.Connect();
            else VrSharedMemoryIO.RegisterHand(0, 1, 0);
        }
        catch (InvalidOperationException) { doubleClaim = true; }
        Check("second left-hand claim throws", doubleClaim);

        // Pump some frames; asserts only that the seqlock path doesn't throw.
        var state = new HMVRState();
        state.SetButton(HMVRButton.TriggerClick, false);
        state.SetScalar(HMVRScalar.TriggerValue, 0f);
        bool pumped = true;
        try
        {
            for (int i = 0; i < 25; i++)
            {
                if (steamVrInstalled) { left.SubmitState(state); right.SubmitState(state); }
                else
                {
                    VrSharedMemoryIO.WriteFrame(0, state.ButtonBits, state.Scalars);
                    VrSharedMemoryIO.WriteFrame(1, state.ButtonBits, state.Scalars);
                }
                Thread.Sleep(4);
            }
        }
        catch { pumped = false; }
        Check("250 Hz frame pump (25 frames)", pumped);

        // ── Phase 1: vrpathreg registration ───────────────────────────────
        Console.WriteLine("\n-- Phase 1: vrpathreg registration --");
        if (!steamVrInstalled)
        {
            Skip("EnsureInstalled + vrpathreg show", "vrpathreg.exe not found (SteamVR not installed)");
        }
        else
        {
            bool extracted = File.Exists(Path.Combine(
                VrDriverBuilder.InstallDir, "driver.vrdrivermanifest"));
            Check("driver folder extracted", extracted, VrDriverBuilder.InstallDir);
            Check("bin\\win64\\driver_hidmaestro.dll present", File.Exists(Path.Combine(
                VrDriverBuilder.InstallDir, "bin", "win64", "driver_hidmaestro.dll")));

            var show = RunTool(vrpathreg!, "show");
            Check("vrpathreg show lists the driver path",
                show.Contains(VrDriverBuilder.InstallDir, StringComparison.OrdinalIgnoreCase));
        }

        // ── Phase 2: live SteamVR round-trip ──────────────────────────────
        Console.WriteLine("\n-- Phase 2: live SteamVR round-trip --");
        bool vrRunning = Process.GetProcessesByName("vrserver").Length > 0;
        if (!steamVrInstalled || !vrRunning)
        {
            Skip("driver attach / enumeration / input round-trip",
                 vrRunning ? "SteamVR not installed" : "vrserver not running (start SteamVR and re-run)");
        }
        else
        {
            // The driver worker polls for the section every 200 ms; give a
            // freshly-hot-plugged driver up to 15 s (vrpathreg adddriver
            // hot-plugs a running SteamVR per the OpenVR driver docs).
            Check("driver attached the IPC channel (DriverConnected)",
                PollFor(() => VrSharedMemoryIO.IsDriverConnected(), 15000),
                "vrserver read the section and wrote DriverPid");

            // openvr_api.dll lives beside vrpathreg.exe in SteamVR's
            // bin\win64; point the loader there before the first call.
            SetDllDirectoryW(Path.GetDirectoryName(vrpathreg!)!);

            EVRInitError initErr = EVRInitError.None;
            CVRSystem? vrSystem = OpenVR.Init(ref initErr, EVRApplicationType.VRApplication_Background);
            Check("OpenVR.Init(Background)", vrSystem != null && initErr == EVRInitError.None,
                initErr.ToString());

            if (vrSystem != null)
            {
                uint leftIdx = uint.MaxValue, rightIdx = uint.MaxValue;
                bool found = PollFor(() =>
                {
                    leftIdx = FindBySerial(vrSystem, "HIDMAESTRO_VR_LEFT");
                    rightIdx = FindBySerial(vrSystem, "HIDMAESTRO_VR_RIGHT");
                    return leftIdx != uint.MaxValue && rightIdx != uint.MaxValue;
                }, 10000);
                Check("both hands enumerate by serial", found,
                    $"left={(leftIdx == uint.MaxValue ? "?" : leftIdx.ToString())} right={(rightIdx == uint.MaxValue ? "?" : rightIdx.ToString())}");

                if (found)
                {
                    Check("left is TrackedDeviceClass_Controller",
                        vrSystem.GetTrackedDeviceClass(leftIdx) == ETrackedDeviceClass.Controller);
                    Check("right is TrackedDeviceClass_Controller",
                        vrSystem.GetTrackedDeviceClass(rightIdx) == ETrackedDeviceClass.Controller);
                    Check("right is connected",
                        PollFor(() => vrSystem.IsTrackedDeviceConnected(rightIdx), 5000));

                    // Input round-trip with same-window positive control:
                    // press the trigger on the RIGHT hand only, poll the
                    // legacy controller state, then release and confirm it
                    // clears. The left hand stays neutral the whole time.
                    //
                    // Legacy GetControllerState is withheld when system UI
                    // consumes the input stream (openvr.h documents
                    // IsInputAvailable as exactly this gate), and the
                    // dashboard is usually up when no scene app runs. Skip
                    // rather than fail on a healthy driver in that state.
                    if (!vrSystem.IsInputAvailable())
                    {
                        Skip("trigger press/release round-trip",
                             "system UI is consuming the input stream; close the SteamVR dashboard and re-run");
                    }
                    else
                    {
                        var pressed = new HMVRState();
                        pressed.SetButton(HMVRButton.TriggerClick, true);
                        pressed.SetScalar(HMVRScalar.TriggerValue, 1.0f);

                        bool sawPress = PollFor(() =>
                        {
                            right.SubmitState(pressed);
                            return TriggerDown(vrSystem, rightIdx);
                        }, 5000);
                        Check("trigger press reads back via GetControllerState", sawPress);

                        var released = new HMVRState();
                        bool sawRelease = PollFor(() =>
                        {
                            right.SubmitState(released);
                            return !TriggerDown(vrSystem, rightIdx);
                        }, 5000);
                        Check("trigger release reads back (positive control)", sawRelease);
                    }
                }

                OpenVR.Shutdown();
            }
        }

        // ── teardown ──────────────────────────────────────────────────────
        if (steamVrInstalled) { left.Disconnect(); right.Disconnect(); }
        else { VrSharedMemoryIO.UnregisterHand(0); VrSharedMemoryIO.UnregisterHand(1); }

        if (cleanup && steamVrInstalled)
        {
            VrDriverBuilder.Uninstall();
            var show = RunTool(vrpathreg!, "show");
            Check("--cleanup: vrpathreg removedriver",
                !show.Contains(VrDriverBuilder.InstallDir, StringComparison.OrdinalIgnoreCase));
        }

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static bool PollFor(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try { if (cond()) return true; } catch { }
            Thread.Sleep(50);
        }
        try { return cond(); } catch { return false; }
    }

    static uint FindBySerial(CVRSystem sys, string serial)
    {
        var sb = new System.Text.StringBuilder(64);
        for (uint i = 0; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
        {
            ETrackedPropertyError err = ETrackedPropertyError.TrackedProp_Success;
            sb.Clear();
            sys.GetStringTrackedDeviceProperty(i,
                ETrackedDeviceProperty.Prop_SerialNumber_String, sb, 64, ref err);
            if (err == ETrackedPropertyError.TrackedProp_Success
                && string.Equals(sb.ToString(), serial, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return uint.MaxValue;
    }

    static bool TriggerDown(CVRSystem sys, uint deviceIdx)
    {
        VRControllerState_t cs = default;
        if (!sys.GetControllerState(deviceIdx, ref cs,
                (uint)Marshal.SizeOf<VRControllerState_t>()))
            return false;
        ulong triggerMask = 1ul << (int)EVRButtonId.k_EButton_SteamVR_Trigger;
        return (cs.ulButtonPressed & triggerMask) != 0 || cs.rAxis1.x > 0.5f;
    }

    static string RunTool(string exe, string args)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        p.Start();
        string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return outp;
    }
}
