// Virtual VR controller smoke test (issue #32).
//
// Two phases:
//
//   HEADLESS PROTOCOL (always runs, no VR software needed): creates the
//   HMVRController consumer channel, then plays the DRIVER's role against
//   it by opening the section/events by name exactly as
//   driver/openvr/src/vr_transport.cpp does: validates magic/abi/pid CAS,
//   seqlock-reads submitted frames and checks every field lands at its
//   vr_protocol.h offset, writes a haptic ring slot with the driver's
//   publish protocol and asserts HapticReceived fires. This pins the C#
//   and C++ layout mirrors to each other at the byte level.
//
//   LIVE STEAMVR (runs when a SteamVR install is found): registers the
//   embedded driver via HMVR.EnsureDriverRegistered, starts the headless
//   SteamVR stack (null HMD, steamcmd-style install) when not already
//   running, and asserts through Valve's own openvr_api that BOTH virtual
//   controllers enumerate with our serials, hold hand roles, declare the
//   legacy axis types, and round-trip a haptic pulse from the client
//   through vrserver and our C++ driver back to
//   HMVRController.HapticReceived. After the consumer-restart cycle it
//   re-launches THIS exe as "--legacy-reader": a scene app (WaitGetPoses
//   pumped) that verifies the issue #55 legacy binding end to end through
//   GetControllerState. The reader must be a scene app because legacy
//   state follows SteamVR's input focus, and on a headless rig with no
//   scene app nothing holds it (IsInputAvailable reads false and every
//   background read is frozen zeros - measured to 120 s; as the scene app
//   the same read streams in under 10 ms). SKIPs (exit 0) with a loud
//   reason when SteamVR is absent. A same-window positive control is
//   inherent: the null HMD driver enumerating proves the client link.
//
// The headless rig this runs on needs two config seeds beyond the null
// HMD driver, both in the recipe: a chaperone_info.vrchap (universe 2) so
// steamvr_room_setup never launches as a focus-stealing scene app, and
// the dashboard disabled for the same reason.
//
// Requires admin (Global\ section creation + HKLM). Exit 0 PASS/SKIP,
// 1 FAIL.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using HIDMaestro;
using HIDMaestro.Internal;

using Valve.VR;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    // ── Win32 for the driver-role phase ────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint hi, uint lo, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenEventW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetEvent(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetDllDirectoryW(string path);

    const uint FILE_MAP_READ = 0x0004, FILE_MAP_WRITE = 0x0002;
    const uint SYNCHRONIZE = 0x00100000, EVENT_MODIFY_STATE = 0x0002;

    static unsafe T Read<T>(IntPtr view, int offset) where T : unmanaged => *(T*)(view + offset);
    static unsafe void Write<T>(IntPtr view, int offset, T value) where T : unmanaged => *(T*)(view + offset) = value;

    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--legacy-reader")
            return LegacyReaderMain(args[1]);

        // Diagnostic: hold a pumping consumer with the known legacy state
        // for N seconds so a reader can be timed independently.
        if (args.Length >= 2 && args[0] == "--pump-only")
        {
            var pumpState = new HMVRState();
            pumpState.Left.StickX = -0.5f;
            pumpState.Left.StickY = 0.25f;
            pumpState.Left.Trigger = 0.5f;
            pumpState.Left.Grip = 0.75f;
            pumpState.Left.Buttons = HMVRButton.A | HMVRButton.TriggerClick | HMVRButton.GripClick;
            pumpState.Right.Trigger = 1.0f;
            using var pumpOnly = new PumpedConsumer();
            pumpOnly.SetState(in pumpState);
            Console.WriteLine($"pumping for {args[1]}s...");
            Thread.Sleep(int.Parse(args[1]) * 1000);
            return 0;
        }

        Console.WriteLine("=== Virtual VR controller smoke (issue #32) ===");

        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("  [SKIP] all phases (requires elevation)");
                return 0;
            }
        }

        // ── Phase 1: headless protocol, probe plays the driver ─────────
        Console.WriteLine("\n-- phase 1: IPC protocol, probe in the driver's role --");
        using (var vr = new HMVRController())
        {
            IntPtr mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, false, VrSharedMemory.SectionName);
            Check("driver-role OpenFileMapping succeeds (SDDL admits it)", mapping != IntPtr.Zero,
                  $"err={Marshal.GetLastWin32Error()}");
            if (mapping == IntPtr.Zero) return 1;
            IntPtr view = MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, (UIntPtr)VrSharedMemory.SectionSize);
            Check("driver-role MapViewOfFile succeeds", view != IntPtr.Zero);
            IntPtr inputEv = OpenEventW(SYNCHRONIZE, false, VrSharedMemory.InputEventName);
            Check("driver-role OpenEvent(input) succeeds with SYNCHRONIZE", inputEv != IntPtr.Zero);

            Check("magic is 'HMVR'", Read<uint>(view, VrSharedMemory.MagicOffset) == VrSharedMemory.Magic,
                  $"0x{Read<uint>(view, VrSharedMemory.MagicOffset):X8}");
            Check("abiVersion matches", Read<uint>(view, VrSharedMemory.AbiVersionOffset) == VrSharedMemory.AbiVersion);
            Check("consumerPid is this process", Read<ulong>(view, VrSharedMemory.ConsumerPidOffset) == (ulong)Environment.ProcessId);
            Check("config activates both hands", Read<uint>(view, VrSharedMemory.ConfigOffset) == 0x3);

            // Submit a distinctive frame and read it back at protocol offsets.
            var state = new HMVRState
            {
                Left = new HMVRHandState
                {
                    Buttons = HMVRButton.A | HMVRButton.TriggerClick,
                    Trigger = 0.75f, Grip = 0.25f, StickX = -0.5f, StickY = 1.0f,
                    PoseValid = true,
                    PositionX = 1.5f, PositionY = 2.5f, PositionZ = -3.5f,
                    OrientationW = 0.5f, OrientationX = 0.5f, OrientationY = 0.5f, OrientationZ = 0.5f,
                },
                Right = new HMVRHandState { Buttons = HMVRButton.System, Trigger = 1.0f },
            };
            vr.SubmitState(in state);
            Check("input event signalled per frame", WaitForSingleObject(inputEv, 1000) == 0);

            uint seq = Read<uint>(view, VrSharedMemory.InputSeqNoOffset);
            Check("input seqlock is even (stable) after write", (seq & 1) == 0 && seq != 0, $"seq={seq}");

            int L = VrSharedMemory.InputFrameOffset + 8;
            int R = L + VrSharedMemory.ControllerStateSize;
            Check("left buttonBits at offset", Read<uint>(view, L) == (uint)(HMVRButton.A | HMVRButton.TriggerClick));
            Check("left trigger scalar", Math.Abs(Read<float>(view, L + VrSharedMemory.StateScalarsOffset) - 0.75f) < 1e-6);
            Check("left grip scalar", Math.Abs(Read<float>(view, L + VrSharedMemory.StateScalarsOffset + 4) - 0.25f) < 1e-6);
            Check("left stickX scalar", Math.Abs(Read<float>(view, L + VrSharedMemory.StateScalarsOffset + 8) + 0.5f) < 1e-6);
            Check("left poseValid flag", Read<uint>(view, L + VrSharedMemory.StatePoseValidOffset) == 1);
            Check("left position triple", Math.Abs(Read<float>(view, L + VrSharedMemory.StatePositionOffset + 8) + 3.5f) < 1e-6);
            Check("left orientation quat", Math.Abs(Read<float>(view, L + VrSharedMemory.StateOrientationOffset) - 0.5f) < 1e-6);
            Check("right buttonBits independent", Read<uint>(view, R) == (uint)HMVRButton.System);
            Check("right pose stays invalid when not set", Read<uint>(view, R + VrSharedMemory.StatePoseValidOffset) == 0);

            // Haptic ring: produce one slot exactly as controller_device.cpp
            // ProcessEvent does (fill, barrier, seqNo, barrier, head).
            var got = new ManualResetEventSlim(false);
            HMVRHapticEventArgs? received = null;
            vr.HapticReceived += (_, e) => { received = e; got.Set(); };

            ulong seqNo = Read<ulong>(view, VrSharedMemory.HapticHeadOffset) + 1;
            int slot = VrSharedMemory.HapticRingOffset + (int)((seqNo - 1) % VrSharedMemory.HapticSlots) * VrSharedMemory.HapticReplySize;
            Write<uint>(view, slot + 8, 1);           // right hand
            Write<float>(view, slot + 12, 0.25f);     // duration
            Write<float>(view, slot + 16, 160f);      // frequency
            Write<float>(view, slot + 20, 0.9f);      // amplitude
            Write<ulong>(view, slot + 24, (ulong)Stopwatch.GetTimestamp());
            Thread.MemoryBarrier();
            Write<ulong>(view, slot, seqNo);
            Thread.MemoryBarrier();
            Write<ulong>(view, VrSharedMemory.HapticHeadOffset, seqNo);

            Check("HapticReceived fires from a driver-protocol ring write", got.Wait(2000));
            Check("haptic hand decoded", received?.Hand == HMVRHand.Right);
            Check("haptic amplitude decoded", received != null && Math.Abs(received.Amplitude - 0.9f) < 1e-6);

            UnmapViewOfFile(view);
            CloseHandle(mapping);
            CloseHandle(inputEv);
        }

        // A second consumer must be able to claim after disposal.
        using (var again = new HMVRController())
            Check("channel re-claimable after Dispose (pid released)", true);

        // ── Phase 2: live SteamVR ──────────────────────────────────────
        Console.WriteLine("\n-- phase 2: live SteamVR --");
        string? steamVr = HMVR.SteamVRPath;
        if (steamVr == null)
        {
            Console.WriteLine("  [SKIP] SteamVR not installed on this machine; live phase requires it.");
            Console.WriteLine("         (steamcmd +login anonymous +app_update 250820 installs it Steam-free.)");
            Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed (live phase skipped) ===");
            return s_failures == 0 ? 0 : 1;
        }
        Console.WriteLine($"  SteamVR: {steamVr}");

        Check("EnsureDriverRegistered", HMVR.EnsureDriverRegistered());
        Check("driver extracted to the stable path",
              File.Exists(Path.Combine(VrDriverBuilder.ExtractRoot, "bin", "win64", "driver_hidmaestro.dll")));

        bool startedHere = false;
        if (!HMVR.IsSteamVRRunning)
        {
            // Only auto-start the headless rig shape (steamcmd install).
            // A user's Steam-client SteamVR is not ours to launch.
            bool rigShape = string.Equals(steamVr, @"C:\SteamVR", StringComparison.OrdinalIgnoreCase)
                         || Environment.GetEnvironmentVariable("HMVR_ALLOW_START") == "1";
            if (!rigShape)
            {
                Console.WriteLine("  [SKIP] SteamVR installed but not running; not starting a user's install.");
                Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed (live phase skipped) ===");
                return s_failures == 0 ? 0 : 1;
            }
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(steamVr, "bin", "win64", "vrstartup.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
            startedHere = true;
            Console.WriteLine("  started headless SteamVR stack");
        }

        int exit = 1;
        try
        {
            exit = LivePhase(steamVr);
        }
        finally
        {
            if (startedHere)
            {
                foreach (var name in new[] { "vrmonitor", "vrcompositor", "vrserver", "vrwebhelper", "vrdashboard", "vrstartup" })
                    foreach (var p in Process.GetProcessesByName(name))
                        try { p.Kill(); } catch { }
            }
        }
        return exit;
    }

    // A consumer plus the neutral-frame pump that keeps it looking live to
    // the driver. Wrapped so the live phase can drop the channel and
    // re-attach it, which is what a consumer app restarting looks like.
    sealed class PumpedConsumer : IDisposable
    {
        public readonly HMVRController Vr = new();
        readonly Thread _pump;
        readonly object _gate = new();
        HMVRState _state;
        volatile bool _stop;

        public PumpedConsumer()
        {
            _pump = new Thread(() =>
            {
                // A real hand never submits a bit-identical state forever,
                // and vrserver's idle logic treats a constant stream as an
                // inactive controller (the bench that proved the legacy lane
                // drove a scripted sweep). Dither every scalar by +/-0.02 -
                // inside every assertion tolerance (0.05) - so the wire
                // always carries input edges.
                int tick = 0;
                while (!_stop)
                {
                    HMVRState st;
                    lock (_gate) st = _state;
                    float d = ((tick++ & 1) == 0) ? 0.02f : -0.02f;
                    st.Left.StickX += d; st.Left.StickY += d;
                    st.Left.Trigger += d; st.Left.Grip += d;
                    st.Right.StickX += d; st.Right.StickY += d;
                    st.Right.Trigger += d; st.Right.Grip += d;
                    Vr.SubmitState(in st);
                    Thread.Sleep(8);
                }
            })
            { IsBackground = true };
            _pump.Start();
        }

        // The legacy-lane assertions need a non-neutral, known state on the
        // wire; the pump keeps submitting whatever was set last.
        public void SetState(in HMVRState state)
        {
            lock (_gate) _state = state;
        }

        public void Dispose()
        {
            _stop = true;
            _pump.Join(1000);
            Vr.Dispose();
        }
    }

    // Child-process mode: a fresh legacy app reading the hands through
    // GetControllerState. See the launch site for why this must be its own
    // process. Prints its own [PASS]/[FAIL] lines (relayed by the parent)
    // and reports through its exit code.
    static int LegacyReaderMain(string steamVr)
    {
        SetDllDirectoryW(Path.Combine(steamVr, "bin", "win64"));

        // Legacy GetControllerState follows SteamVR's input focus. On this
        // rig nothing else takes it (IsInputAvailable reads false for a
        // plain background client and every read is zeros), so the reader
        // takes the scene-app role - the shape of the legacy game this lane
        // exists for - and pumps WaitGetPoses so the compositor treats it
        // as the live app.
        EVRInitError initError = EVRInitError.None;
        CVRSystem? system = OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Scene);
        Check("[reader] OpenVR client init (Scene)", system != null && initError == EVRInitError.None, initError.ToString());
        if (system == null) return 1;

        var poseStop = false;
        var posePump = new Thread(() =>
        {
            var render = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
            var game = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
            while (!Volatile.Read(ref poseStop))
            {
                var comp = OpenVR.Compositor;
                if (comp != null) comp.WaitGetPoses(render, game);
                else Thread.Sleep(11);
            }
        })
        { IsBackground = true };
        posePump.Start();

        uint leftIdx = uint.MaxValue, rightIdx = uint.MaxValue;
        bool found = SpinWait(() =>
        {
            leftIdx = FindBySerial(system, "HMVR-LEFT-0001");
            rightIdx = FindBySerial(system, "HMVR-RIGHT-0001");
            return leftIdx != uint.MaxValue && rightIdx != uint.MaxValue;
        }, 10000);
        Check("[reader] both hands visible", found);

        if (found)
        {
            // The legacy binding routes by role, so a reader that freezes
            // with roles unassigned is measuring #51's problem, not #55's.
            bool readerRoled = SpinWait(() =>
                system.GetControllerRoleForTrackedDeviceIndex(leftIdx) == ETrackedControllerRole.LeftHand &&
                system.GetControllerRoleForTrackedDeviceIndex(rightIdx) == ETrackedControllerRole.RightHand, 15000);
            Check("[reader] both hands hold roles", readerRoled,
                  $"left={system.GetControllerRoleForTrackedDeviceIndex(leftIdx)} right={system.GetControllerRoleForTrackedDeviceIndex(rightIdx)}");

            Console.WriteLine($"    IsInputAvailable={system.IsInputAvailable()}");

            var st1 = new VRControllerState_t();
            uint stSize = (uint)Marshal.SizeOf<VRControllerState_t>();
            var clock = Stopwatch.StartNew();
            bool legacyLive = SpinWait(() =>
                system.GetControllerState(leftIdx, ref st1, stSize) &&
                st1.unPacketNum > 0 &&
                Math.Abs(st1.rAxis0.x - (-0.5f)) < 0.05f, 30000);
            Console.WriteLine($"    IsInputAvailable after wait={system.IsInputAvailable()}");
            Check("[reader] legacy GetControllerState streams (packet>0, axis0 tracks stick)", legacyLive,
                  $"packet={st1.unPacketNum} axis0.x={st1.rAxis0.x:F3} after {clock.ElapsedMilliseconds}ms");

            if (legacyLive)
            {
                Check("[reader] axis0.y tracks stick Y", Math.Abs(st1.rAxis0.y - 0.25f) < 0.05f, $"got {st1.rAxis0.y:F3}");
                Check("[reader] axis1 tracks trigger pull", Math.Abs(st1.rAxis1.x - 0.5f) < 0.05f, $"got {st1.rAxis1.x:F3}");
                Check("[reader] axis2 tracks grip pull", Math.Abs(st1.rAxis2.x - 0.75f) < 0.05f, $"got {st1.rAxis2.x:F3}");

                ulong pressed = st1.ulButtonPressed;
                ulong wantMask = (1UL << (int)EVRButtonId.k_EButton_A)
                               | (1UL << (int)EVRButtonId.k_EButton_Axis1)
                               | (1UL << (int)EVRButtonId.k_EButton_Grip);
                Check("[reader] legacy buttons carry A + trigger + grip presses",
                      (pressed & wantMask) == wantMask, $"pressed=0x{pressed:X}");

                var stR = new VRControllerState_t();
                bool rightTracks = SpinWait(() =>
                    system.GetControllerState(rightIdx, ref stR, stSize) &&
                    Math.Abs(stR.rAxis1.x - 1.0f) < 0.05f, 5000);
                Check("[reader] right hand routes independently (trigger=1.0)", rightTracks,
                      $"right axis1.x={stR.rAxis1.x:F3}");

                var st2 = new VRControllerState_t();
                Thread.Sleep(150);
                system.GetControllerState(leftIdx, ref st2, stSize);
                Check("[reader] legacy packet counter advances", st2.unPacketNum > st1.unPacketNum,
                      $"{st1.unPacketNum} -> {st2.unPacketNum}");
            }
        }

        Volatile.Write(ref poseStop, true);
        posePump.Join(1000);
        OpenVR.Shutdown();
        return s_failures == 0 ? 0 : 1;
    }

    static int LivePhase(string steamVr)
    {
        var consumer = new PumpedConsumer();
        var vr = consumer.Vr;
        bool handsVerified = false;

        // The driver's bootstrap poll runs every 200 ms once vrserver is
        // up; give the whole stack a generous window.
        bool driverUp = SpinWait(() => vr.DriverConnected, 30000);
        Check("C++ driver attached to the IPC channel inside vrserver", driverUp);
        if (!driverUp)
        {
            consumer.Dispose();
            Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
            return s_failures == 0 ? 0 : 1;
        }

        Check("both controllers registered live in vrserver", SpinWait(() => vr.ControllersLive, 15000));

        // Client side, through Valve's own binding against the runtime's
        // own openvr_api.dll.
        SetDllDirectoryW(Path.Combine(steamVr, "bin", "win64"));
        EVRInitError initError = EVRInitError.None;
        CVRSystem? system = OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Background);
        Check("OpenVR client init (Background)", system != null && initError == EVRInitError.None, initError.ToString());

        if (system != null)
        {
            uint leftIdx = uint.MaxValue, rightIdx = uint.MaxValue;
            bool found = SpinWait(() =>
            {
                leftIdx = FindBySerial(system, "HMVR-LEFT-0001");
                rightIdx = FindBySerial(system, "HMVR-RIGHT-0001");
                return leftIdx != uint.MaxValue && rightIdx != uint.MaxValue;
            }, 15000);
            Check("both virtual controllers enumerate by serial", found,
                  $"left={(leftIdx == uint.MaxValue ? "-" : leftIdx.ToString())} right={(rightIdx == uint.MaxValue ? "-" : rightIdx.ToString())}");

            if (found)
            {
                Check("device class is Controller",
                      system.GetTrackedDeviceClass(leftIdx) == ETrackedDeviceClass.Controller &&
                      system.GetTrackedDeviceClass(rightIdx) == ETrackedDeviceClass.Controller);
                Check("devices report connected",
                      system.IsTrackedDeviceConnected(leftIdx) && system.IsTrackedDeviceConnected(rightIdx));

                // Hand roles (issue #51). The role hint is advisory; what
                // decides /user/hand/left|right and everything role-addressed
                // (SteamVR's own Test Controller included) is the runtime's
                // hand assignment, which a driver influences only through
                // Prop_ControllerHandSelectionPriority_Int32. Roles settle
                // asynchronously after the devices activate, hence the wait.
                Check("role hint reads back left/right",
                      Int32Prop(system, leftIdx, ETrackedDeviceProperty.Prop_ControllerRoleHint_Int32) == (int)ETrackedControllerRole.LeftHand &&
                      Int32Prop(system, rightIdx, ETrackedDeviceProperty.Prop_ControllerRoleHint_Int32) == (int)ETrackedControllerRole.RightHand);

                Console.WriteLine($"    handSelectionPriority: left={Int32Prop(system, leftIdx, ETrackedDeviceProperty.Prop_ControllerHandSelectionPriority_Int32)}" +
                                  $" right={Int32Prop(system, rightIdx, ETrackedDeviceProperty.Prop_ControllerHandSelectionPriority_Int32)}");

                bool roled = SpinWait(() =>
                    system.GetControllerRoleForTrackedDeviceIndex(leftIdx) == ETrackedControllerRole.LeftHand &&
                    system.GetControllerRoleForTrackedDeviceIndex(rightIdx) == ETrackedControllerRole.RightHand, 15000);
                Check("SteamVR promoted both devices to real hand roles", roled,
                      $"left={system.GetControllerRoleForTrackedDeviceIndex(leftIdx)} right={system.GetControllerRoleForTrackedDeviceIndex(rightIdx)}");

                uint roleLeft = system.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
                uint roleRight = system.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand);
                Check("both hands resolve back to our device indices",
                      roleLeft == leftIdx && roleRight == rightIdx,
                      $"left={Idx(roleLeft)} (want {leftIdx}) right={Idx(roleRight)} (want {rightIdx})");

                // Axis classification for legacy consumers (issue #55's
                // adjacent finding): vrserver does not synthesize these for
                // IVRDriverInput drivers, so the driver states them, the
                // VRCHOTAS pattern.
                Check("axis types declared: joystick/trigger/trigger",
                      Int32Prop(system, leftIdx, ETrackedDeviceProperty.Prop_Axis0Type_Int32) == (int)EVRControllerAxisType.k_eControllerAxis_Joystick &&
                      Int32Prop(system, leftIdx, ETrackedDeviceProperty.Prop_Axis1Type_Int32) == (int)EVRControllerAxisType.k_eControllerAxis_Trigger &&
                      Int32Prop(system, leftIdx, ETrackedDeviceProperty.Prop_Axis2Type_Int32) == (int)EVRControllerAxisType.k_eControllerAxis_Trigger);

                // Haptic round trip: client -> vrserver -> C++ driver ->
                // IPC ring -> HMVRController event.
                var got = new ManualResetEventSlim(false);
                HMVRHand? hand = null;
                vr.HapticReceived += (_, e) => { hand = e.Hand; got.Set(); };
                bool anyHaptic = false;
                for (int attempt = 0; attempt < 40 && !anyHaptic; attempt++)
                {
                    system.TriggerHapticPulse(rightIdx, 0, 3000);
                    anyHaptic = got.Wait(250);
                }
                Check("haptic pulse round-trips client->vrserver->driver->consumer", anyHaptic);
                if (anyHaptic)
                    Check("haptic landed on the right hand", hand == HMVRHand.Right);

                // Consumer restart (issue #51). The driver keeps both devices
                // across a consumer's lifetime and flips deviceIsConnected
                // instead of removing them, since OpenVR has no
                // TrackedDeviceRemoved. SteamVR drops the hand roles while
                // they are disconnected, so the roles have to come back on
                // their own when the consumer returns. This is the path a
                // user actually walks: start the app, close it, start it
                // again.
                consumer.Dispose();
                Check("controllers go disconnected when the consumer leaves",
                      SpinWait(() => !system.IsTrackedDeviceConnected(leftIdx) &&
                                     !system.IsTrackedDeviceConnected(rightIdx), 20000));

                consumer = new PumpedConsumer();
                vr = consumer.Vr;
                Check("controllers reconnect when a new consumer attaches",
                      SpinWait(() => system.IsTrackedDeviceConnected(leftIdx) &&
                                     system.IsTrackedDeviceConnected(rightIdx), 20000));

                bool reRoled = SpinWait(() =>
                    system.GetControllerRoleForTrackedDeviceIndex(leftIdx) == ETrackedControllerRole.LeftHand &&
                    system.GetControllerRoleForTrackedDeviceIndex(rightIdx) == ETrackedControllerRole.RightHand, 20000);
                Check("hand roles survive a consumer restart", reRoled,
                      $"left={system.GetControllerRoleForTrackedDeviceIndex(leftIdx)} right={system.GetControllerRoleForTrackedDeviceIndex(rightIdx)}");

                handsVerified = reRoled;
            }

            OpenVR.Shutdown();
        }

        // Legacy input lane (issue #55). Background clients read the hands
        // through GetControllerState, which only carries data vrserver
        // generates through the profile's legacy_binding. Without the
        // binding the signature is unPacketNum frozen at 0 with all buttons
        // and axes zero while the pose streams fine. Measured on this rig
        // (2026-08-18, A/B and T1/T2 discriminations): vrserver builds an
        // app's legacy state generator ONCE, when that app session first
        // enables legacy input, and a session whose enable ran before the
        // hands held roles stays frozen for the life of that process no
        // matter how long it waits (measured to 120 s; in-process
        // Shutdown+Init does not shed the record). A fresh process whose
        // session starts after roles exist streams within ~150 ms. So the
        // legacy lane is verified the way real legacy apps meet it: a
        // separate reader process launched against hands that are already
        // up, this same exe in --legacy-reader mode.
        if (handsVerified)
        {
            var known = new HMVRState();
            known.Left.StickX = -0.5f;
            known.Left.StickY = 0.25f;
            known.Left.Trigger = 0.5f;
            known.Left.Grip = 0.75f;
            known.Left.Buttons = HMVRButton.A | HMVRButton.TriggerClick | HMVRButton.GripClick;
            known.Right.Trigger = 1.0f;
            consumer.SetState(in known);

            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--legacy-reader");
            psi.ArgumentList.Add(steamVr);
            using var reader = Process.Start(psi)!;
            string readerOut = reader.StandardOutput.ReadToEnd();
            bool readerDone = reader.WaitForExit((int)(90000 * s_scale));
            if (!readerDone) { try { reader.Kill(); } catch { } }
            foreach (var line in readerOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Console.WriteLine("  " + line.TrimEnd());
            Check("legacy reader app verifies GetControllerState end to end",
                  readerDone && reader.ExitCode == 0, $"exit={(readerDone ? reader.ExitCode : -1)}");
        }

        consumer.Dispose();

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static int Int32Prop(CVRSystem system, uint idx, ETrackedDeviceProperty prop)
    {
        var err = ETrackedPropertyError.TrackedProp_Success;
        int value = system.GetInt32TrackedDeviceProperty(idx, prop, ref err);
        return err == ETrackedPropertyError.TrackedProp_Success ? value : int.MinValue;
    }

    static string Idx(uint i) => i == OpenVR.k_unTrackedDeviceIndexInvalid ? "invalid" : i.ToString();

    static uint FindBySerial(CVRSystem system, string serial)
    {
        var sb = new StringBuilder(64);
        for (uint i = 0; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
        {
            if (system.GetTrackedDeviceClass(i) != ETrackedDeviceClass.Controller)
                continue;
            var err = ETrackedPropertyError.TrackedProp_Success;
            sb.Clear();
            system.GetStringTrackedDeviceProperty(i, ETrackedDeviceProperty.Prop_SerialNumber_String, sb, 64, ref err);
            if (err == ETrackedPropertyError.TrackedProp_Success && sb.ToString() == serial)
                return i;
        }
        return uint.MaxValue;
    }

    // The Atom runs the full live phase at HIDMAESTRO_TIMEOUT_SCALE=2, and
    // vrserver bring-up there is far slower than the devbox. Same env-var
    // contract as the battery harness, applied at the single wait choke
    // point so every phase-2 window stretches together.
    static readonly double s_scale =
        double.TryParse(Environment.GetEnvironmentVariable("HIDMAESTRO_TIMEOUT_SCALE"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var sc) && sc > 1
            ? sc : 1.0;

    static bool SpinWait(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        long budget = (long)(timeoutMs * s_scale);
        while (sw.ElapsedMilliseconds < budget)
        {
            if (cond()) return true;
            Thread.Sleep(100);
        }
        return cond();
    }
}
