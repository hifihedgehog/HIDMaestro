// Switch Pro acceptance check through REAL SDL3 (issue #33).
//
// The raw-HID probe (switch_pro_check) replays SDL's init sequence as a
// surrogate. THIS probe closes the ticket's actual acceptance line by
// loading the real SDL3.dll (the repo-adjacent fork build Steam-adjacent
// consumers use) and letting its HIDAPI_DriverSwitch drive the virtual
// pad end to end:
//
//   "SDL3's HIDAPI_DriverSwitch opens the virtual pad, completes init,
//    reads calibrated sticks and buttons, reports gyro + accel via
//    SDL_GetGamepadSensorData"
//
// Asserts: the gamepad enumerates and opens (init completed: SDL rejects
// the device if BTrySetupUSB or LoadStickCalibration fail), the name
// identifies a Switch Pro, a SubmitState button press and full-deflection
// stick read back CALIBRATED through SDL_GetGamepadButton/Axis, sensors
// exist and SDL_GetGamepadSensorData returns the submitted IMU values in
// SDL units, and SDL_RumbleGamepad round-trips to OutputDecoded.
//
// SKIPs (exit 0) when SDL3.dll is absent (fresh clone without the
// sibling SDL3-build checkout) or when not elevated. Exit 1 on FAIL.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

using HIDMaestro;

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

    // ── SDL3 P/Invoke (minimal surface) ────────────────────────────────

    const string SDL = "SDL3";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetDllDirectoryW(string path);

    [DllImport(SDL)] static extern bool SDL_SetHint(string name, string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void SdlLogFn(IntPtr userdata, int category, int priority, IntPtr message);
    [DllImport(SDL)] static extern void SDL_SetLogOutputFunction(SdlLogFn callback, IntPtr userdata);
    [DllImport(SDL)] static extern void SDL_SetLogPriorities(int priority);
    [DllImport(SDL)] static extern IntPtr SDL_GetJoysticks(out int count);
    [DllImport(SDL)] static extern IntPtr SDL_GetJoystickNameForID(uint id);
    [DllImport(SDL)] static extern IntPtr SDL_GetError();
    static readonly SdlLogFn s_logFn = (u, cat, pri, msg) =>
        Console.WriteLine($"  [sdl:{cat}:{pri}] {Marshal.PtrToStringUTF8(msg)}");
    [DllImport(SDL)] static extern bool SDL_Init(uint flags);
    [DllImport(SDL)] static extern void SDL_Quit();
    [DllImport(SDL)] static extern void SDL_PumpEvents();
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepads(out int count);
    [DllImport(SDL)] static extern void SDL_free(IntPtr mem);
    [DllImport(SDL)] static extern IntPtr SDL_OpenGamepad(uint instanceId);
    [DllImport(SDL)] static extern void SDL_CloseGamepad(IntPtr gamepad);
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepadName(IntPtr gamepad);
    [DllImport(SDL)] static extern bool SDL_GetGamepadButton(IntPtr gamepad, int button);
    [DllImport(SDL)] static extern short SDL_GetGamepadAxis(IntPtr gamepad, int axis);
    [DllImport(SDL)] static extern bool SDL_GamepadHasSensor(IntPtr gamepad, int type);
    [DllImport(SDL)] static extern bool SDL_SetGamepadSensorEnabled(IntPtr gamepad, int type, bool enabled);
    [DllImport(SDL)] static extern bool SDL_GetGamepadSensorData(IntPtr gamepad, int type, float[] data, int num);
    [DllImport(SDL)] static extern bool SDL_RumbleGamepad(IntPtr gamepad, ushort low, ushort high, uint ms);

    const uint SDL_INIT_GAMEPAD = 0x00002000;
    const int BUTTON_SOUTH = 0, BUTTON_EAST = 1;
    const int AXIS_LEFTX = 0, AXIS_LEFTY = 1;
    const int SENSOR_ACCEL = 1, SENSOR_GYRO = 2;

    static int Main()
    {
        Console.WriteLine("=== Switch Pro acceptance via real SDL3 (issue #33) ===");

        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("  [SKIP] all phases (requires elevation, same as CreateController)");
                return 0;
            }
        }

        // The SDL3 fork build lives beside the HIDMaestro checkout. Probe
        // several candidates; SKIP when none exists.
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", ".."));
        // Root first: the runnable set keeps SDL3.dll BESIDE
        // libusb-1.0.dll, which this fork's HIDAPI loads at init.
        // Pointing at build/Release alone leaves HIDAPI dead
        // ("Couldn't load libusb") and SDL3 has no other backend for a
        // non-Xbox HID pad.
        string?[] candidates =
        {
            Path.Combine(repoRoot, "..", "SDL3-build"),
            Path.Combine(repoRoot, "..", "SDL3-build", "build", "Release"),
        };
        string? sdlDir = null;
        foreach (var c in candidates)
        {
            if (c != null && File.Exists(Path.Combine(c, "SDL3.dll"))) { sdlDir = Path.GetFullPath(c); break; }
        }
        if (sdlDir == null)
        {
            Console.WriteLine("  [SKIP] SDL3.dll not found beside the repo (SDL3-build checkout absent)");
            return 0;
        }
        SetDllDirectoryW(sdlDir);
        Console.WriteLine($"  SDL3: {sdlDir}\\SDL3.dll");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        Console.WriteLine("  Creating switch-pro virtual controller...");
        using var ctrl = ctx.CreateController(ctx.GetProfile("switch-pro")!);

        byte decodedLeft = 0, decodedRight = 0;
        int decoded10 = 0;
        ctrl.OutputDecoded += (_, e) =>
        {
            if (e.ReportId != 0x10 && e.ReportId != 0x01) return;
            byte l = (byte)e.Fields["leftMotor"], r = (byte)e.Fields["rightMotor"];
            if (l == 0 && r == 0) return;   // neutral blocks ride every subcommand
            decodedLeft = l; decodedRight = r;
            Interlocked.Increment(ref decoded10);
        };

        // Steady neutral pump on a background thread for SDL's whole
        // session (its init + polling expect a live device).
        var pumpState = new HMGamepadState();
        var pumpLock = new object();
        bool pumpStop = false;
        var pump = new Thread(() =>
        {
            while (!Volatile.Read(ref pumpStop))
            {
                HMGamepadState st;
                lock (pumpLock) st = pumpState;
                ctrl.SubmitState(st);
                Thread.Sleep(8);
            }
        })
        { IsBackground = true };
        pump.Start();

        SDL_SetLogOutputFunction(s_logFn, IntPtr.Zero);
        SDL_SetLogPriorities(1 /* SDL_LOG_PRIORITY_VERBOSE */);
        Check("SDL_Init(GAMEPAD)", SDL_Init(SDL_INIT_GAMEPAD));

        // Detection + full HIDAPI init happen inside SDL's update pump;
        // poll up to 15 s. An open handle is itself the init-completed
        // assert: HIDAPI_DriverSwitch rejects the device when the USB
        // handshake or stick-calibration SPI reads fail
        // (SDL_hidapi_switch.c:2410-2418 return false paths).
        IntPtr pad = IntPtr.Zero;
        var sw = Stopwatch.StartNew();
        while (pad == IntPtr.Zero && sw.ElapsedMilliseconds < 15000)
        {
            SDL_PumpEvents();
            IntPtr list = SDL_GetGamepads(out int n);
            if (list != IntPtr.Zero)
            {
                if (n > 0)
                {
                    uint id = (uint)Marshal.ReadInt32(list, 0);
                    pad = SDL_OpenGamepad(id);
                }
                SDL_free(list);
            }
            if (pad == IntPtr.Zero) Thread.Sleep(100);
        }
        Check("HIDAPI_DriverSwitch opens the virtual pad (init completed)",
            pad != IntPtr.Zero, $"after {sw.ElapsedMilliseconds} ms");
        if (pad == IntPtr.Zero)
        {
            // Diagnostic: joystick-level view + last SDL error.
            IntPtr jlist = SDL_GetJoysticks(out int jn);
            Console.WriteLine($"  [diag] joystick count: {jn}");
            for (int i = 0; i < jn; i++)
            {
                uint jid = (uint)Marshal.ReadInt32(jlist, i * 4);
                Console.WriteLine($"  [diag] joystick {jid}: {Marshal.PtrToStringUTF8(SDL_GetJoystickNameForID(jid))}");
            }
            if (jlist != IntPtr.Zero) SDL_free(jlist);
            Console.WriteLine($"  [diag] SDL_GetError: {Marshal.PtrToStringUTF8(SDL_GetError())}");
            SDL_Quit();
            return Done(ref pumpStop, pump);
        }

        string name = Marshal.PtrToStringUTF8(SDL_GetGamepadName(pad)) ?? "";
        Check("gamepad name identifies Switch Pro", name.Contains("Switch Pro", StringComparison.OrdinalIgnoreCase), name);

        // ── calibrated input: A + full-deflection left stick ────────────
        lock (pumpLock)
        {
            pumpState = new HMGamepadState
            {
                Buttons = (HMButton)(1u << 1),   // layout face_a
                Axes = new System.Collections.Generic.Dictionary<HMAxis, float>
                {
                    [HMAxis.X] = 1.0f,           // full right
                    [HMAxis.Y] = 0.0f,           // full up (HID 0 = up)
                },
                AccelGZ = 1.0f,
                GyroDpsY = 100.0f,
            };
        }
        Deadline(() =>
        {
            SDL_PumpEvents();
            return SDL_GetGamepadButton(pad, BUTTON_SOUTH) || SDL_GetGamepadButton(pad, BUTTON_EAST);
        }, 3000);
        // Nintendo pads report through SDL's label remap; the A press
        // surfaces as SOUTH or EAST depending on the labels hint. Either
        // proves button delivery through the calibrated path.
        Check("A press reads back via SDL_GetGamepadButton",
            SDL_GetGamepadButton(pad, BUTTON_SOUTH) || SDL_GetGamepadButton(pad, BUTTON_EAST));

        short lx = SDL_GetGamepadAxis(pad, AXIS_LEFTX);
        short ly = SDL_GetGamepadAxis(pad, AXIS_LEFTY);
        Check("left stick X calibrated full right (>25000)", lx > 25000, $"lx={lx}");
        Check("left stick Y calibrated full up (<-25000, SDL up-negative)", ly < -25000, $"ly={ly}");

        // ── sensors via SDL_GetGamepadSensorData ────────────────────────
        Check("accel sensor present", SDL_GamepadHasSensor(pad, SENSOR_ACCEL));
        Check("gyro sensor present", SDL_GamepadHasSensor(pad, SENSOR_GYRO));
        Check("enable accel", SDL_SetGamepadSensorEnabled(pad, SENSOR_ACCEL, true));
        Check("enable gyro", SDL_SetGamepadSensorEnabled(pad, SENSOR_GYRO, true));

        // Sensor enable flips the driver into IMU streaming (subcommand
        // 0x40); give a few frames, then read. Assert magnitudes so the
        // Switch->SDL axis permutation stays out of the contract: 1 g
        // accel ~= 9.81 m/s^2, 100 deg/s gyro ~= 1.745 rad/s.
        float[] accel = new float[3], gyro = new float[3];
        bool sensorRead = Deadline(() =>
        {
            SDL_PumpEvents();
            return SDL_GetGamepadSensorData(pad, SENSOR_ACCEL, accel, 3)
                && SDL_GetGamepadSensorData(pad, SENSOR_GYRO, gyro, 3)
                && Magnitude(accel) > 1.0f;
        }, 5000);
        Check("SDL_GetGamepadSensorData returns data", sensorRead);
        float am = Magnitude(accel), gm = Magnitude(gyro);
        Check("accel magnitude ~= 1 g (8.8..10.8 m/s^2)", am > 8.8f && am < 10.8f, $"|a|={am:F2}");
        Check("gyro magnitude ~= 100 dps (1.57..1.92 rad/s)", gm > 1.57f && gm < 1.92f, $"|g|={gm:F3}");

        // ── rumble round-trip ───────────────────────────────────────────
        int before = decoded10;
        Check("SDL_RumbleGamepad", SDL_RumbleGamepad(pad, 0xFFFF, 0xFFFF, 500));
        Deadline(() => { SDL_PumpEvents(); return decoded10 > before; }, 2000);
        Check("rumble reaches OutputDecoded", decoded10 > before,
            $"left={decodedLeft} right={decodedRight}");
        Check("decoded amplitude nonzero", decodedLeft > 0 || decodedRight > 0,
            $"left={decodedLeft} right={decodedRight}");

        SDL_CloseGamepad(pad);
        SDL_Quit();
        return Done(ref pumpStop, pump);
    }

    static float Magnitude(float[] v) => MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

    static bool Deadline(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            try { if (cond()) return true; } catch { }
            Thread.Sleep(20);
        }
        try { return cond(); } catch { return false; }
    }

    static int Done(ref bool pumpStop, Thread pump)
    {
        Volatile.Write(ref pumpStop, true);
        pump.Join(1000);
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }
}
