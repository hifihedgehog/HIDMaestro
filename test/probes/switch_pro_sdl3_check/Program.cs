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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryW(string path);

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
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepadSerial(IntPtr gamepad);
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepadPath(IntPtr gamepad);
    [DllImport(SDL)] static extern bool SDL_GetGamepadButton(IntPtr gamepad, int button);
    [DllImport(SDL)] static extern short SDL_GetGamepadAxis(IntPtr gamepad, int axis);
    [DllImport(SDL)] static extern bool SDL_GamepadHasSensor(IntPtr gamepad, int type);
    [DllImport(SDL)] static extern bool SDL_SetGamepadSensorEnabled(IntPtr gamepad, int type, bool enabled);
    [DllImport(SDL)] static extern bool SDL_GetGamepadSensorData(IntPtr gamepad, int type, float[] data, int num);
    [DllImport(SDL)] static extern bool SDL_RumbleGamepad(IntPtr gamepad, ushort low, ushort high, uint ms);
    [DllImport(SDL)] static extern int SDL_hid_init();
    [DllImport(SDL)] static extern IntPtr SDL_hid_enumerate(ushort vid, ushort pid);
    [DllImport(SDL)] static extern void SDL_hid_free_enumeration(IntPtr devs);

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
        // IMPORTANT: fork head (build/Release, the DLL PadForge ships)
        // carries the HIDMaestro virtual-controller filter (SDL fork
        // commit b318a5bbd1): hid_enumerate deliberately HIDES HIDMaestro
        // virtuals so PadForge never ingests its own outputs. A full-stack
        // run therefore needs an UNFILTERED SDL3 build; the SDL3-build
        // ROOT copy (pre-filter February build) is the one on this box.
        // Steam ships stock SDL without the filter, so this probe against
        // an unfiltered build is the faithful Steam-side stand-in.
        // The fork's HIDAPI loads libusb-1.0.dll at init; pre-load from
        // the root so either candidate resolves it.
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
        string libusb = Path.GetFullPath(Path.Combine(sdlDir, "..", "..", "libusb-1.0.dll"));
        if (!File.Exists(libusb)) libusb = Path.Combine(sdlDir, "libusb-1.0.dll");
        bool libusbLoaded = File.Exists(libusb) && LoadLibraryW(libusb) != IntPtr.Zero;
        SetDllDirectoryW(sdlDir);
        Console.WriteLine($"  SDL3: {sdlDir}\\SDL3.dll (libusb {(libusbLoaded ? "preloaded" : "MISSING")})");

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
        // Select the VIRTUAL pad among SDL's gamepads: a real Pro paired
        // to the box enumerates identically by VID/PID/name (2026-07-22:
        // the raw-HID probes opened the live pad by mistake). The virtual
        // is distinguishable by HID serial (HM-CTL-<index>) or interface
        // path (hid#hidclass# parent; a real BT pad sits under the
        // Bluetooth HID service class GUID).
        IntPtr pad = IntPtr.Zero;
        var logged = new System.Collections.Generic.HashSet<uint>();
        var sw = Stopwatch.StartNew();
        while (pad == IntPtr.Zero && sw.ElapsedMilliseconds < 15000)
        {
            SDL_PumpEvents();
            IntPtr list = SDL_GetGamepads(out int n);
            if (list != IntPtr.Zero)
            {
                for (int i = 0; i < n && pad == IntPtr.Zero; i++)
                {
                    uint id = (uint)Marshal.ReadInt32(list, i * 4);
                    IntPtr cand = SDL_OpenGamepad(id);
                    if (cand == IntPtr.Zero) continue;
                    string serial = Marshal.PtrToStringUTF8(SDL_GetGamepadSerial(cand)) ?? "";
                    string cpath = Marshal.PtrToStringUTF8(SDL_GetGamepadPath(cand)) ?? "";
                    if (logged.Add(id))
                        Console.WriteLine($"  [cand] id={id} serial='{serial}' path={cpath}");
                    if (serial.StartsWith("HM-CTL-", StringComparison.OrdinalIgnoreCase)
                        || cpath.Contains("hid#hidclass#", StringComparison.OrdinalIgnoreCase))
                        pad = cand;
                    else
                        SDL_CloseGamepad(cand);
                }
                SDL_free(list);
            }
            if (pad == IntPtr.Zero) Thread.Sleep(100);
        }
        if (pad == IntPtr.Zero && SdlRevisionHasHmFilter())
        {
            Console.WriteLine("  [SKIP] this SDL3 build carries the fork's HIDMaestro virtual-controller");
            Console.WriteLine("         filter (b318a5bbd1): hid_enumerate hides HIDMaestro pads by design.");
            Console.WriteLine("         Full-stack validation needs an unfiltered (stock/pre-filter) SDL3.dll.");
            SDL_Quit();
            Volatile.Write(ref pumpStop, true);
            pump.Join(1000);
            return 0;
        }
        Check("HIDAPI_DriverSwitch opens the virtual pad (init completed)",
            pad != IntPtr.Zero, $"after {sw.ElapsedMilliseconds} ms");
        if (pad == IntPtr.Zero)
        {
            // Diagnostic: raw HIDAPI enumeration (below the joystick layer).
            Console.WriteLine($"  [diag] SDL_hid_init = {SDL_hid_init()}");
            IntPtr devs = SDL_hid_enumerate(0, 0);
            int hidCount = 0;
            IntPtr cur = devs;
            while (cur != IntPtr.Zero)
            {
                // struct SDL_hid_device_info: path(ptr), vendor_id(u16), product_id(u16)...
                string? dpath = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(cur, 0));
                ushort dvid = (ushort)Marshal.ReadInt16(cur, IntPtr.Size);
                ushort dpid = (ushort)Marshal.ReadInt16(cur, IntPtr.Size + 2);
                if (dvid == 0x057E || hidCount < 6)
                    Console.WriteLine($"  [diag] hid dev {dvid:X4}:{dpid:X4} {dpath}");
                hidCount++;
                // next pointer is the LAST field; walk via known offset: read next at end.
                // SDL_hid_device_info layout ends with 'next'; use SDL3 struct: locate by scanning is fragile,
                // so instead re-enumerate filtered:
                break;
            }
            SDL_hid_free_enumeration(devs);
            IntPtr swDevs = SDL_hid_enumerate(0x057E, 0x2009);
            Console.WriteLine($"  [diag] hid_enumerate(057E,2009) = {(swDevs != IntPtr.Zero ? "FOUND" : "empty")}");
            if (swDevs != IntPtr.Zero) SDL_hid_free_enumeration(swDevs);
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
        // 0x40); give a few frames, then read. Gyro is asserted PER-AXIS
        // (the wire->SDL gyro map is stable across the available builds).
        // Accel is asserted by MAGNITUDE here: the unfiltered pre-filter
        // build predates upstream's accel-axis alignment, so its accel
        // permutation differs from head. The per-axis accel contract is
        // locked byte-exact against HEAD source at the wire layer by
        // switch_pro_check ("accel: SDL +Y maps to wire Z").
        float[] accel = new float[3], gyro = new float[3];
        bool sensorRead = Deadline(() =>
        {
            SDL_PumpEvents();
            return SDL_GetGamepadSensorData(pad, SENSOR_ACCEL, accel, 3)
                && SDL_GetGamepadSensorData(pad, SENSOR_GYRO, gyro, 3)
                && Magnitude(accel) > 1.0f;
        }, 5000);
        Check("SDL_GetGamepadSensorData returns data", sensorRead);
        float am = Magnitude(accel);
        Check("accel magnitude ~= 1 g (8.8..10.8 m/s^2)", am > 8.8f && am < 10.8f,
            $"a=({accel[0]:F2},{accel[1]:F2},{accel[2]:F2})");
        Check("gyro round-trips on SDL +Y (1.745 +/- 0.05, others ~0)",
            Math.Abs(gyro[1] - 1.74533f) < 0.05f && Math.Abs(gyro[0]) < 0.05f && Math.Abs(gyro[2]) < 0.05f,
            $"g=({gyro[0]:F3},{gyro[1]:F3},{gyro[2]:F3})");

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

    /// <summary>True when the loaded SDL3 is a fork-head build carrying the
    /// HIDMaestro enumeration filter. Detected by revision string: the
    /// filter landed after the 3.4.0 release branch point, and every
    /// filtered build on this machine reports release-3.4.0-NNNN-g*.
    /// Pre-filter builds report earlier dev revisions.</summary>
    static bool SdlRevisionHasHmFilter()
    {
        try
        {
            string rev = Marshal.PtrToStringUTF8(SDL_GetRevision()) ?? "";
            return rev.Contains("release-3.4.0-", StringComparison.OrdinalIgnoreCase)
                || rev.Contains("release-3.5", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    [DllImport(SDL)] static extern IntPtr SDL_GetRevision();

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
