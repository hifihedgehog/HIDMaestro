// Virtual VR controller smoke test (issue #32).
//
// Three phases:
//
//   OFFLINE (always runs first, and alone with --offline, with no
//   elevation): HMVRController on channels of this process's own memory.
//   Submits from several threads at once never write a frame mixed from
//   two, submits racing Dispose return or are refused with
//   ObjectDisposedException and never write once Dispose has returned, and
//   nothing reads the channel Dispose released (Offline.cs).
//
//   HEADLESS PROTOCOL (no VR software needed): creates the
//   HMVRController consumer channel, then plays the DRIVER's role against
//   it by opening the section/events by name exactly as
//   driver/openvr/src/vr_transport.cpp does: validates magic/abi/pid CAS,
//   seqlock-reads submitted frames and checks every field lands at its
//   vr_protocol.h offset, writes a haptic ring slot with the driver's
//   publish protocol and asserts HapticReceived fires. This pins the C#
//   and C++ layout mirrors to each other at the byte level. A child
//   process then holds the channel with a HapticReceived handler that
//   throws ahead of one that counts: the second still gets every pulse and
//   the reader goes on. Another child holds a HapticReceived handler past
//   Dispose's join of the haptic reader: the reader then ends on its own,
//   touching nothing Dispose released, and the channel can be claimed
//   again. A third has a HapticReceived handler dispose its own
//   controller.
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
// The phases after the offline one require admin (Global\ section
// creation + HKLM). Exit 0 PASS/SKIP, 1 FAIL.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using HIDMaestro;
using HIDMaestro.Internal;

using Valve.VR;

internal static class Program
{
    static int s_total, s_failures;

    internal static void Check(string name, bool cond, string detail = "")
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

    /// <summary>Produce one haptic slot exactly as controller_device.cpp
    /// ProcessEvent does: seqNo zero, barrier, fill, barrier, seqNo,
    /// barrier, head.</summary>
    static void PublishHaptic(IntPtr view, uint hand, float amplitude)
    {
        ulong seqNo = Read<ulong>(view, VrSharedMemory.HapticHeadOffset) + 1;
        int slot = VrSharedMemory.HapticRingOffset + (int)((seqNo - 1) % VrSharedMemory.HapticSlots) * VrSharedMemory.HapticReplySize;
        Write<ulong>(view, slot, 0);
        Thread.MemoryBarrier();
        Write<uint>(view, slot + 8, hand);        // 0 left, 1 right
        Write<float>(view, slot + 12, 0.25f);     // duration
        Write<float>(view, slot + 16, 160f);      // frequency
        Write<float>(view, slot + 20, amplitude);
        Write<ulong>(view, slot + 24, (ulong)Stopwatch.GetTimestamp());
        Thread.MemoryBarrier();
        Write<ulong>(view, slot, seqNo);
        Thread.MemoryBarrier();
        Write<ulong>(view, VrSharedMemory.HapticHeadOffset, seqNo);
    }

    // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX
    const uint QuietErrorMode = 0x8003;

    [DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint mode);

    static (int Code, string Line) RunThrowingHapticChild() => RunChild("--throwing-haptic");

    static (int Code, string Line) RunChild(string mode)
    {
        // The child inherits this error mode: a crash there ends in an exit
        // code, never in a dialog on the desktop.
        SetErrorMode(QuietErrorMode);
        var psi = new ProcessStartInfo(Environment.ProcessPath!, mode)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30_000))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            p.WaitForExit();
        }
        var lines = (stdout.Result + "\n" + stderr.Result).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return (p.ExitCode, lines.LastOrDefault(l => l.StartsWith("CHILD ", StringComparison.Ordinal)) ?? lines.FirstOrDefault() ?? "no output");
    }

    /// <summary>The child: the consumer channel with a HapticReceived
    /// handler that throws ahead of one that counts, and two pulses written
    /// in the driver's role.</summary>
    static int ThrowingHapticMain()
    {
        SetErrorMode(QuietErrorMode);
        int seen = 0;
        using var vr = new HMVRController();
        vr.HapticReceived += (_, _) => throw new InvalidOperationException("a throwing HapticReceived handler");
        vr.HapticReceived += (_, _) => Interlocked.Increment(ref seen);
        IntPtr mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, false, VrSharedMemory.SectionName);
        if (mapping == IntPtr.Zero)
        {
            Console.WriteLine($"CHILD haptic failed: OpenFileMapping error {Marshal.GetLastWin32Error()}");
            return 1;
        }
        IntPtr view = MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, (UIntPtr)VrSharedMemory.SectionSize);
        try
        {
            // The reader finds a slot this probe writes on its 250 ms poll,
            // since no event is signaled. 10 s is a hang bound, not a speed.
            for (int n = 1; n <= 2; n++)
            {
                PublishHaptic(view, hand: (uint)(n - 1), amplitude: 0.25f * n);
                System.Threading.SpinWait.SpinUntil(() => Volatile.Read(ref seen) >= n, 10_000);
            }
            Console.WriteLine($"CHILD haptic seen={Volatile.Read(ref seen)}");
            return 0;
        }
        finally
        {
            if (view != IntPtr.Zero) UnmapViewOfFile(view);
            CloseHandle(mapping);
        }
    }

    /// <summary>The child: a HapticReceived handler still running when
    /// Dispose's join of the haptic reader gives up. Before this release
    /// Dispose then unmapped the channel and closed the events the reader
    /// waits on, and the reader's next read of the ring ended the
    /// process.</summary>
    static int SlowHapticMain()
    {
        SetErrorMode(QuietErrorMode);
        var vr = new HMVRController();
        var reader = (Thread)typeof(HMVRController).GetField("_hapticThread",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vr)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        vr.HapticReceived += (_, _) =>
        {
            if (entered.IsSet) return;
            entered.Set();
            // Held until Dispose has returned. The bound only keeps a
            // stalled main thread from holding the handler forever, and it
            // must outlast Dispose's 1000 ms join under load.
            release.Wait(15_000);
        };
        IntPtr mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, false, VrSharedMemory.SectionName);
        if (mapping == IntPtr.Zero)
        {
            Console.WriteLine($"CHILD slow-haptic failed: OpenFileMapping error {Marshal.GetLastWin32Error()}");
            return 1;
        }
        IntPtr view = MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, (UIntPtr)VrSharedMemory.SectionSize);
        try
        {
            PublishHaptic(view, hand: 1, amplitude: 0.5f);
            bool inHandler = entered.Wait(10_000);
            vr.Dispose();
            bool stillInHandler = !release.IsSet && reader.IsAlive;
            release.Set();
            bool ended = System.Threading.SpinWait.SpinUntil(() => !reader.IsAlive, 3000);
            bool reclaimed;
            try { using var again = new HMVRController(); reclaimed = true; }
            catch (InvalidOperationException) { reclaimed = false; }
            Console.WriteLine($"CHILD slow-haptic held={inHandler && stillInHandler} ended={ended} reclaimed={reclaimed}");
            return 0;
        }
        finally
        {
            release.Set();
            if (view != IntPtr.Zero) UnmapViewOfFile(view);
            CloseHandle(mapping);
        }
    }

    /// <summary>The child: a HapticReceived handler that disposes its own
    /// controller, on the haptic reader's thread. Before this release that
    /// Dispose joined its own thread for a second, then released the
    /// channel under the reader, whose next read ended the process.</summary>
    static int SelfDisposeHapticMain()
    {
        SetErrorMode(QuietErrorMode);
        var vr = new HMVRController();
        var reader = (Thread)typeof(HMVRController).GetField("_hapticThread",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vr)!;
        long disposeMs = -1;
        using var returned = new ManualResetEventSlim();
        vr.HapticReceived += (_, _) =>
        {
            if (returned.IsSet) return;
            var sw = Stopwatch.StartNew();
            vr.Dispose();
            disposeMs = sw.ElapsedMilliseconds;
            returned.Set();
        };
        IntPtr mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, false, VrSharedMemory.SectionName);
        if (mapping == IntPtr.Zero)
        {
            Console.WriteLine($"CHILD self-dispose failed: OpenFileMapping error {Marshal.GetLastWin32Error()}");
            return 1;
        }
        IntPtr view = MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, (UIntPtr)VrSharedMemory.SectionSize);
        try
        {
            // Two pulses, so the reader has another slot to read after the
            // handler that disposed the controller returns.
            PublishHaptic(view, hand: 0, amplitude: 0.25f);
            PublishHaptic(view, hand: 1, amplitude: 0.5f);
            bool ran = returned.Wait(10_000);
            bool ended = System.Threading.SpinWait.SpinUntil(() => !reader.IsAlive, 3000);
            bool reclaimed;
            try { using var again = new HMVRController(); reclaimed = true; }
            catch (InvalidOperationException) { reclaimed = false; }
            // A self-join waits Dispose's full 1000 ms join, so 900 ms still
            // catches it and leaves the rest of the second to load.
            Console.WriteLine($"CHILD self-dispose returned={ran && disposeMs < 900} ended={ended} reclaimed={reclaimed} disposeMs={disposeMs}");
            return 0;
        }
        finally
        {
            if (view != IntPtr.Zero) UnmapViewOfFile(view);
            CloseHandle(mapping);
        }
    }

    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--legacy-reader")
            return LegacyReaderMain(args[1]);
        if (args.Length >= 1 && args[0] == "--throwing-haptic")
            return ThrowingHapticMain();
        if (args.Length >= 1 && args[0] == "--slow-haptic")
            return SlowHapticMain();
        if (args.Length >= 1 && args[0] == "--self-dispose-haptic")
            return SelfDisposeHapticMain();

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

        // Offline first, on channels of this process's own memory, with or
        // without elevation.
        Offline.Run();
        if (args.Length >= 1 && args[0] == "--offline")
        {
            Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed (offline) ===");
            return s_failures == 0 ? 0 : 1;
        }

        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("  [SKIP] the section and live phases (require elevation)");
                return s_failures == 0 ? 0 : 1;
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
            // The input event is auto-reset. When a running vrserver's
            // driver attaches to this channel, its wait takes the signal
            // first, so a miss counts only when no driver is attached.
            bool inputSignaled = WaitForSingleObject(inputEv, 1000) == 0;
            if (!inputSignaled && vr.DriverConnected)
                Console.WriteLine("  info: input event check skipped: a running vrserver's driver attached and took the signal");
            else
                Check("input event signaled per frame", inputSignaled);

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
            // ProcessEvent does (seqNo zero, barrier, fill, barrier, seqNo,
            // barrier, head).
            var got = new ManualResetEventSlim(false);
            HMVRHapticEventArgs? received = null;
            vr.HapticReceived += (_, e) => { received = e; got.Set(); };

            PublishHaptic(view, hand: 1, amplitude: 0.9f);

            // Found on the reader's 250 ms poll, since no event is signaled.
            Check("HapticReceived fires from a driver-protocol ring write", got.Wait(10_000));
            Check("haptic hand decoded", received?.Hand == HMVRHand.Right);
            Check("haptic amplitude decoded", received != null && Math.Abs(received.Amplitude - 0.9f) < 1e-6);

            UnmapViewOfFile(view);
            CloseHandle(mapping);
            CloseHandle(inputEv);
        }

        // A second consumer must be able to claim after disposal.
        using (var again = new HMVRController())
            Check("channel re-claimable after Dispose (pid released)", true);

        // A throwing haptic handler, in a child: before v2.0.0 its
        // exception ended the haptic reader's thread and the process.
        var (hapticCode, hapticLine) = RunThrowingHapticChild();
        Check("behind a throwing HapticReceived handler, the next one gets both pulses and the reader goes on",
              hapticCode == 0 && hapticLine == "CHILD haptic seen=2", $"exit {hapticCode}: {hapticLine}");

        // A handler still running when Dispose's join gives up, in a child:
        // before this release the reader's next read ended the process.
        var (slowCode, slowLine) = RunChild("--slow-haptic");
        Check("a HapticReceived handler past Dispose's join: the reader ends on its own and the channel can be claimed again",
              slowCode == 0 && slowLine == "CHILD slow-haptic held=True ended=True reclaimed=True", $"exit {slowCode}: {slowLine}");

        // A handler that disposes its own controller, in a child: Dispose
        // returns at once on the reader's thread, the reader ends and the
        // channel can be claimed again.
        var (selfCode, selfLine) = RunChild("--self-dispose-haptic");
        Check("a HapticReceived handler that disposes its controller: Dispose returns, the reader ends and the channel can be claimed again",
              selfCode == 0 && selfLine.StartsWith("CHILD self-dispose returned=True ended=True reclaimed=True ", StringComparison.Ordinal),
              $"exit {selfCode}: {selfLine}");

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

                // Polled until the counter moves, so a slow machine cannot
                // fail it. A frozen counter still fails at the deadline.
                var st2 = new VRControllerState_t();
                bool advanced = SpinWait(() =>
                    system.GetControllerState(leftIdx, ref st2, stSize) &&
                    st2.unPacketNum > st1.unPacketNum, 5000);
                Check("[reader] legacy packet counter advances", advanced,
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
        // up, this same exe in --legacy-reader mode. The reader is a scene
        // app, and SteamVR's compositor has no display in session 0, where a
        // battery run over SSH runs. The checks above run there.
        if (handsVerified && Process.GetCurrentProcess().SessionId == 0)
            Console.WriteLine("  [SKIP] legacy reader app: not run here: this program runs in session 0, which has no display for SteamVR's compositor.");
        else if (handsVerified)
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
