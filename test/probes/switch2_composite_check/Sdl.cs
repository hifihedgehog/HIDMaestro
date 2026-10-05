// Part C of the Switch 2 Pro composite persona check: SDL3 with libusb, the
// client the persona exists for.
//
// The SDL in use is the PadForge fork, whose Switch 2 driver reads input
// through Windows' HID driver and opens interface 1 over libusb. The fork as
// shipped hides every HIDMaestro device from itself, so this needs the
// sibling SDL3-build/build-unfiltered/Release build: the same source with
// that one filter switched off. Parts C and the SDL half of D skip when it
// is absent.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using HIDMaestro;

internal static partial class Program
{
    const string SDL = "SDL3";

    [DllImport(SDL)] static extern bool SDL_SetHint(string name, string value);
    [DllImport(SDL)] static extern bool SDL_Init(uint flags);
    [DllImport(SDL)] static extern void SDL_Quit();
    [DllImport(SDL)] static extern void SDL_PumpEvents();
    [DllImport(SDL)] static extern bool SDL_PollEvent(IntPtr sdlEvent);
    [DllImport(SDL)] static extern void SDL_free(IntPtr mem);
    [DllImport(SDL)] static extern IntPtr SDL_GetError();
    [DllImport(SDL)] static extern IntPtr SDL_GetJoysticks(out int count);
    [DllImport(SDL)] static extern ushort SDL_GetJoystickVendorForID(uint id);
    [DllImport(SDL)] static extern ushort SDL_GetJoystickProductForID(uint id);
    [DllImport(SDL)] static extern IntPtr SDL_GetJoystickNameForID(uint id);
    [DllImport(SDL)] static extern IntPtr SDL_GetJoystickPathForID(uint id);
    [DllImport(SDL)] static extern bool SDL_IsGamepad(uint id);
    [DllImport(SDL)] static extern IntPtr SDL_OpenGamepad(uint id);
    [DllImport(SDL)] static extern void SDL_CloseGamepad(IntPtr gamepad);
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepadName(IntPtr gamepad);
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepadSerial(IntPtr gamepad);
    [DllImport(SDL)] static extern uint SDL_GetGamepadID(IntPtr gamepad);
    [DllImport(SDL)] static extern bool SDL_GetGamepadButton(IntPtr gamepad, int button);
    [DllImport(SDL)] static extern short SDL_GetGamepadAxis(IntPtr gamepad, int axis);
    [DllImport(SDL)] static extern bool SDL_GamepadHasSensor(IntPtr gamepad, int type);
    [DllImport(SDL)] static extern bool SDL_SetGamepadSensorEnabled(IntPtr gamepad, int type, bool enabled);
    [DllImport(SDL)] static extern bool SDL_GetGamepadSensorData(IntPtr gamepad, int type, [Out] float[] data, int count);
    [DllImport(SDL)] static extern float SDL_GetGamepadSensorDataRate(IntPtr gamepad, int type);
    [DllImport(SDL)] static extern bool SDL_RumbleGamepad(IntPtr gamepad, ushort low, ushort high, uint durationMs);

    const uint SDL_INIT_JOYSTICK = 0x00000200, SDL_INIT_GAMEPAD = 0x00002000;
    const uint SDL_EVENT_GAMEPAD_SENSOR_UPDATE = 0x659;
    const int SENSOR_ACCEL = 1, SENSOR_GYRO = 2;
    // SDL_GamepadButton and SDL_GamepadAxis, SDL_gamepad.h.
    const int GP_SOUTH = 0, GP_EAST = 1, GP_WEST = 2, GP_NORTH = 3, GP_BACK = 4, GP_GUIDE = 5, GP_START = 6,
              GP_LEFT_STICK = 7, GP_RIGHT_STICK = 8, GP_LEFT_SHOULDER = 9, GP_RIGHT_SHOULDER = 10,
              GP_DPAD_UP = 11, GP_DPAD_DOWN = 12, GP_DPAD_LEFT = 13, GP_DPAD_RIGHT = 14,
              GP_MISC1 = 15, GP_RIGHT_PADDLE1 = 16, GP_LEFT_PADDLE1 = 17, GP_MISC2 = 21;
    const int AX_LEFTX = 0, AX_LEFTY = 1, AX_RIGHTX = 2, AX_RIGHTY = 3, AX_LEFT_TRIGGER = 4, AX_RIGHT_TRIGGER = 5;

    static string Str(IntPtr p) => Marshal.PtrToStringUTF8(p) ?? "";

    static bool s_sdlTried, s_sdlReady;

    /// <summary>Load the unfiltered fork build and start SDL, once.</summary>
    static bool EnsureSdl()
    {
        if (s_sdlTried) return s_sdlReady;
        s_sdlTried = true;

        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        string dir = Path.GetFullPath(Path.Combine(repoRoot, "..", "SDL3-build", "build-unfiltered", "Release"));
        string dll = Path.Combine(dir, "SDL3.dll");
        string libusb = Path.Combine(dir, "libusb-1.0.dll");
        if (!File.Exists(dll) || !File.Exists(libusb))
        {
            Console.WriteLine("  [SKIP] no SDL3-build/build-unfiltered/Release with SDL3.dll and libusb-1.0.dll beside the repo.");
            Console.WriteLine("         It is the PadForge fork's SDL built with libusb and its HIDMaestro filter off.");
            return false;
        }
        Console.WriteLine($"  SDL3.dll: {dll}");
        SetDllDirectoryW(dir);
        if (LoadLibraryW(libusb) == IntPtr.Zero || LoadLibraryW(dll) == IntPtr.Zero)
        {
            Check("SDL3.dll and libusb-1.0.dll load", false, $"error {Marshal.GetLastWin32Error()}");
            return false;
        }

        // What PadForge sets for this pad, and no more.
        SDL_SetHint("SDL_JOYSTICK_HIDAPI_SWITCH2", "1");
        SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
        if (!SDL_Init(SDL_INIT_JOYSTICK | SDL_INIT_GAMEPAD))
        {
            Check("SDL_Init", false, Str(SDL_GetError()));
            return false;
        }
        s_sdlReady = true;
        return true;
    }

    static void QuitSdl()
    {
        if (s_sdlReady) { SDL_Quit(); s_sdlReady = false; }
    }

    /// <summary>Every joystick SDL lists for 057E:2069, opened as a gamepad
    /// when SDL offers one.</summary>
    static List<(uint Id, string Name, bool IsGamepad)> Switch2Joysticks()
    {
        var list = new List<(uint, string, bool)>();
        IntPtr arr = SDL_GetJoysticks(out int n);
        if (arr == IntPtr.Zero) return list;
        for (int i = 0; i < n; i++)
        {
            uint id = (uint)Marshal.ReadInt32(arr, i * 4);
            if (SDL_GetJoystickVendorForID(id) == 0x057E && SDL_GetJoystickProductForID(id) == 0x2069)
                list.Add((id, Str(SDL_GetJoystickNameForID(id)), SDL_IsGamepad(id)));
        }
        SDL_free(arr);
        return list;
    }

    static List<(uint Id, string Name, bool IsGamepad)> WaitForSwitch2(int count, int ms)
    {
        var seen = new List<(uint, string, bool)>();
        long deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            SDL_PumpEvents();
            seen = Switch2Joysticks();
            if (seen.Count >= count && seen.All(s => s.Item3)) break;
            Thread.Sleep(50);
        }
        return seen;
    }

    /// <summary>Pump SDL for a while, discarding events.</summary>
    static void Pump(int ms)
    {
        IntPtr ev = Marshal.AllocHGlobal(128);
        try
        {
            long deadline = Environment.TickCount64 + ms;
            do
            {
                SDL_PumpEvents();
                while (SDL_PollEvent(ev)) { }
                Thread.Sleep(2);
            } while (Environment.TickCount64 < deadline);
        }
        finally { Marshal.FreeHGlobal(ev); }
    }

    /// <summary>Pump until <paramref name="done"/> or the time is up.</summary>
    static bool PumpUntil(Func<bool> done, int ms)
    {
        IntPtr ev = Marshal.AllocHGlobal(128);
        try
        {
            long deadline = Environment.TickCount64 + ms;
            while (true)
            {
                SDL_PumpEvents();
                while (SDL_PollEvent(ev)) { }
                if (done()) return true;
                if (Environment.TickCount64 >= deadline) return false;
                Thread.Sleep(2);
            }
        }
        finally { Marshal.FreeHGlobal(ev); }
    }

    /// <summary>Pump and collect the sensor timestamps SDL attaches to one
    /// sensor's events for one gamepad, in nanoseconds.</summary>
    static List<ulong> SensorTimestamps(uint joystick, int sensor, int ms)
    {
        var stamps = new List<ulong>();
        IntPtr ev = Marshal.AllocHGlobal(128);
        try
        {
            long deadline = Environment.TickCount64 + ms;
            do
            {
                SDL_PumpEvents();
                while (SDL_PollEvent(ev))
                {
                    // SDL_GamepadSensorEvent: type 0, which 16, sensor 20,
                    // data 24, sensor_timestamp 40.
                    if ((uint)Marshal.ReadInt32(ev, 0) != SDL_EVENT_GAMEPAD_SENSOR_UPDATE) continue;
                    if ((uint)Marshal.ReadInt32(ev, 16) != joystick || Marshal.ReadInt32(ev, 20) != sensor) continue;
                    stamps.Add((ulong)Marshal.ReadInt64(ev, 40));
                }
                Thread.Sleep(1);
            } while (Environment.TickCount64 < deadline);
        }
        finally { Marshal.FreeHGlobal(ev); }
        return stamps;
    }

    static string LogLine(byte[] p) => p.Length >= 4 ? $"{p[0]:X2}/{p[3]:X2}" : X(p);

    // ══════════════════════════════════════════════════════════════════════
    //  Part C
    // ══════════════════════════════════════════════════════════════════════

    static void PartC(HMContext ctx)
    {
        Console.WriteLine("\n== Part C: SDL3 with libusb ==");
        if (s_liveSkipped) { Console.WriteLine("  [SKIP] see part B"); return; }
        if (!EnsureSdl()) return;
        var profile = ctx.GetProfile(ProfileId)!;

        var a = Attached.Create(ctx, profile, null, out string? skip);
        if (a == null) { Console.WriteLine($"  [SKIP] {skip}"); return; }
        IntPtr gp = IntPtr.Zero;
        try
        {
            var s2 = a.Pad.UsbipHandle!.Device.Switch2!;
            var seen = WaitForSwitch2(1, 20000);
            foreach (var j in seen) Console.WriteLine($"  [joy] id={j.Id} gamepad={j.IsGamepad} '{j.Name}' path={Str(SDL_GetJoystickPathForID(j.Id))}");
            Check("SDL lists exactly one 057E:2069 device, as a gamepad", seen.Count == 1 && seen[0].IsGamepad, $"{seen.Count} listed");
            if (seen.Count != 1 || !seen[0].IsGamepad)
            {
                Console.WriteLine($"  commands the persona received: {string.Join(" ", s2.SnapshotBulkLog().Select(LogLine))}");
                return;
            }

            gp = SDL_OpenGamepad(seen[0].Id);
            Check("SDL_OpenGamepad succeeds", gp != IntPtr.Zero, Str(SDL_GetError()));
            if (gp == IntPtr.Zero) return;
            string name = Str(SDL_GetGamepadName(gp));
            Check("SDL opens the persona as \"Nintendo Switch Pro Controller\"", name == "Nintendo Switch Pro Controller", name);
            Check("SDL's serial for it, read from flash over bulk, is the persona's", Str(SDL_GetGamepadSerial(gp)) == a.Serial, Str(SDL_GetGamepadSerial(gp)));

            // SDL's start, as the persona saw it.
            var log = s2.SnapshotBulkLogTimed();
            string sequence = string.Join(" ", log.Select(e => LogLine(e.Payload)));
            Console.WriteLine($"  bulk commands received: {sequence}");
            const string expected = "02/01 02/01 02/01 02/01 02/01 02/01 02/01 07/01 0C/02 11/01 0A/08 0C/04 01/0C 01/01 08/02 03/0A 03/0D";
            Check("the persona received SDL's start whole and in order: seven flash reads, then the ten commands",
                  sequence.StartsWith(expected, StringComparison.Ordinal), sequence);
            var reads = log.Where(e => e.Payload.Length >= 16 && e.Payload[0] == 0x02 && e.Payload[3] == 0x01)
                           .Select(e => BitConverter.ToUInt32(e.Payload, 12)).Take(7).ToArray();
            Check("the flash reads are 0x13000, 0x13040, 0x13080, 0x130C0, 0x13100, 0x1FC040 and 0x1FC080",
                  reads.SequenceEqual(new uint[] { 0x13000, 0x13040, 0x13080, 0x130C0, 0x13100, 0x1FC040, 0x1FC080 }),
                  string.Join(" ", reads.Select(r => $"0x{r:X}")));
            if (log.Count >= 17)
            {
                double startMs = (log[16].Ticks - log[0].Ticks) * 1000.0 / Stopwatch.Frequency;
                // SDL waits up to 100 ms for each of 17 answers. An answer
                // that never came would cost that much each time.
                Check("every command was answered at once: the 17-command start took well under one 100 ms wait each",
                      startMs < 600, $"{startMs:F0} ms");
            }
            Check("SDL selected report 0x05, set mask 0x27 and started the pad",
                  s2.SelectedReport == 0x05 && s2.FeatureMask == 0x27 && s2.ReportsOn,
                  $"report 0x{s2.SelectedReport:X2}, mask 0x{s2.FeatureMask:X2}, enabled 0x{s2.FeatureEnabled:X2}");

            // ── Motion ──────────────────────────────────────────────────
            Console.WriteLine("\n-- C1 motion --");
            Check("SDL reports an accelerometer and a gyro", SDL_GamepadHasSensor(gp, SENSOR_ACCEL) && SDL_GamepadHasSensor(gp, SENSOR_GYRO));
            Check("both enable", SDL_SetGamepadSensorEnabled(gp, SENSOR_ACCEL, true) && SDL_SetGamepadSensorEnabled(gp, SENSOR_GYRO, true), Str(SDL_GetError()));
            Console.WriteLine($"  SDL's stated rate: {SDL_GetGamepadSensorDataRate(gp, SENSOR_GYRO)} Hz");

            var v = new float[3];
            bool Near(float got, float want) => Math.Abs(got - want) <= Math.Max(0.01f * Math.Abs(want), 0.02f);
            (float, float, float) Read(int sensor, Func<float[], bool> settled)
            {
                PumpUntil(() => SDL_GetGamepadSensorData(gp, sensor, v, 3) && settled(v), 3000);
                SDL_GetGamepadSensorData(gp, sensor, v, 3);
                return (v[0], v[1], v[2]);
            }

            a.Stage(new HMGamepadState { AccelGY = 1.0f });
            var acc = Read(SENSOR_ACCEL, d => Near(d[1], 9.80665f));
            Check("AccelGY 1.0 reads as (0, 9.807, 0) m/s/s within 1 percent",
                  Near(acc.Item1, 0) && Near(acc.Item2, 9.80665f) && Near(acc.Item3, 0), $"({acc.Item1:F3}, {acc.Item2:F3}, {acc.Item3:F3})");
            a.Stage(new HMGamepadState { AccelGX = 1.0f });
            acc = Read(SENSOR_ACCEL, d => Near(d[0], 9.80665f));
            Check("AccelGX 1.0 reads on X", Near(acc.Item1, 9.80665f) && Near(acc.Item2, 0) && Near(acc.Item3, 0), $"({acc.Item1:F3}, {acc.Item2:F3}, {acc.Item3:F3})");
            a.Stage(new HMGamepadState { AccelGZ = -1.0f });
            acc = Read(SENSOR_ACCEL, d => Near(d[2], -9.80665f));
            Check("AccelGZ minus 1.0 reads on Z with its sign", Near(acc.Item1, 0) && Near(acc.Item2, 0) && Near(acc.Item3, -9.80665f), $"({acc.Item1:F3}, {acc.Item2:F3}, {acc.Item3:F3})");

            const float Rad = 1.7453293f;   // 100 deg/s
            foreach (var (label, state, axis, sign) in new (string, HMGamepadState, int, float)[]
                     {
                         ("X", new HMGamepadState { GyroDpsX = 100f }, 0, 1f),
                         ("Y", new HMGamepadState { GyroDpsY = 100f }, 1, 1f),
                         ("Z", new HMGamepadState { GyroDpsZ = 100f }, 2, 1f),
                         ("X, turning the other way", new HMGamepadState { GyroDpsX = -100f }, 0, -1f),
                         ("Y, turning the other way", new HMGamepadState { GyroDpsY = -100f }, 1, -1f),
                         ("Z, turning the other way", new HMGamepadState { GyroDpsZ = -100f }, 2, -1f),
                     })
            {
                a.Stage(state);
                var g = Read(SENSOR_GYRO, d => Near(d[axis], sign * Rad));
                var arr = new[] { g.Item1, g.Item2, g.Item3 };
                bool ok = true;
                for (int i = 0; i < 3; i++) ok &= Near(arr[i], i == axis ? sign * Rad : 0);
                Check($"100 deg/s on {label} reads as {sign * 1.745f:F3} rad/s on that axis within 1 percent", ok, $"({g.Item1:F4}, {g.Item2:F4}, {g.Item3:F4})");
            }
            a.Stage(new HMGamepadState { AccelGY = 1.0f });

            var stamps = SensorTimestamps(SDL_GetGamepadID(gp), SENSOR_GYRO, 1500);
            var steps = new List<long>();
            for (int i = 1; i < stamps.Count; i++) steps.Add((long)(stamps[i] - stamps[i - 1]));
            Check("SDL's sensor timestamps step by 4,000,000 ns",
                  steps.Count >= 200 && steps.All(s => s == 4_000_000),
                  $"{steps.Count} steps, {string.Join(", ", steps.GroupBy(s => s).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key} x{g.Count()}"))}");

            // ── Controls ────────────────────────────────────────────────
            Console.WriteLine("\n-- C2 controls --");
            bool Pressed(HMGamepadState st, int button)
            {
                a.Stage(st);
                bool hit = PumpUntil(() => SDL_GetGamepadButton(gp, button), 1500);
                a.Stage(new HMGamepadState());
                PumpUntil(() => !SDL_GetGamepadButton(gp, button), 1500);
                return hit;
            }
            var controls = new (string Name, HMGamepadState State, int Button)[]
            {
                ("B is the south button", new() { Buttons = HMButton.B }, GP_SOUTH),
                ("A is the east button", new() { Buttons = HMButton.A }, GP_EAST),
                ("Y is the west button", new() { Buttons = HMButton.Y }, GP_WEST),
                ("X is the north button", new() { Buttons = HMButton.X }, GP_NORTH),
                ("L", new() { Buttons = HMButton.LeftBumper }, GP_LEFT_SHOULDER),
                ("R", new() { Buttons = HMButton.RightBumper }, GP_RIGHT_SHOULDER),
                ("Minus", new() { Buttons = HMButton.Back }, GP_BACK),
                ("Plus", new() { Buttons = HMButton.Start }, GP_START),
                ("Home", new() { Buttons = HMButton.Guide }, GP_GUIDE),
                ("Capture", new() { Buttons = HMButton.Share }, GP_MISC1),
                ("C", new() { Buttons = HMButton.Misc1 }, GP_MISC2),
                ("GR", new() { Buttons = HMButton.RightPaddle }, GP_RIGHT_PADDLE1),
                ("GL", new() { Buttons = HMButton.LeftPaddle }, GP_LEFT_PADDLE1),
                ("left stick click", new() { Buttons = HMButton.LeftStick }, GP_LEFT_STICK),
                ("right stick click", new() { Buttons = HMButton.RightStick }, GP_RIGHT_STICK),
                ("D-pad up", new() { Hat = HMHat.North }, GP_DPAD_UP),
                ("D-pad down", new() { Hat = HMHat.South }, GP_DPAD_DOWN),
                ("D-pad left", new() { Hat = HMHat.West }, GP_DPAD_LEFT),
                ("D-pad right", new() { Hat = HMHat.East }, GP_DPAD_RIGHT),
            };
            var missed = controls.Where(c => !Pressed(c.State, c.Button)).Select(c => c.Name).ToList();
            Check("all 19 buttons read on their SDL gamepad buttons", missed.Count == 0, string.Join(", ", missed));

            short Axis(HMGamepadState st, int axis, Func<short, bool> settled)
            {
                a.Stage(st);
                PumpUntil(() => settled(SDL_GetGamepadAxis(gp, axis)), 1500);
                return SDL_GetGamepadAxis(gp, axis);
            }
            HMGamepadState Sticks(float lx, float ly, float rx, float ry, float lt = 0, float rt = 0)
            {
                var axes = HMGamepadStateHelpers.StandardAxes(profile, lx, ly, rx, ry, lt, rt);
                axes[HMController.ResolveCanonicalAxis(profile.AxisMap, "lefttrigger", HMAxis.Z)] = lt;
                axes[HMController.ResolveCanonicalAxis(profile.AxisMap, "righttrigger", HMAxis.Rz)] = rt;
                return new HMGamepadState { Axes = axes };
            }
            short lxFull = Axis(Sticks(1f, 0.5f, 0.5f, 0.5f), AX_LEFTX, x => x > 32000);
            short lyUp = Axis(Sticks(0.5f, 0f, 0.5f, 0.5f), AX_LEFTY, x => x < -32000);
            short rxLeft = Axis(Sticks(0.5f, 0.5f, 0f, 0.5f), AX_RIGHTX, x => x < -32000);
            short ryDown = Axis(Sticks(0.5f, 0.5f, 0.5f, 1f), AX_RIGHTY, x => x > 32000);
            Check("full deflection reads as full scale: left stick right and up, right stick left and down",
                  lxFull >= 32700 && lyUp <= -32700 && rxLeft <= -32700 && ryDown >= 32700, $"{lxFull} {lyUp} {rxLeft} {ryDown}");
            a.Stage(Sticks(0.5f, 0.5f, 0.5f, 0.5f));
            bool Centered() => new[] { AX_LEFTX, AX_LEFTY, AX_RIGHTX, AX_RIGHTY }.All(ax => Math.Abs((int)SDL_GetGamepadAxis(gp, ax)) < 64);
            PumpUntil(Centered, 1500);
            Check("centered sticks read center on all four axes", Centered(),
                  string.Join(" ", new[] { AX_LEFTX, AX_LEFTY, AX_RIGHTX, AX_RIGHTY }.Select(ax => SDL_GetGamepadAxis(gp, ax))));
            // The right stick's Y is usage Rz, the default right trigger
            // axis. It must move the stick and leave ZR alone. SDL's gamepad
            // API reads a released trigger as 0.
            a.Stage(Sticks(0.5f, 0.5f, 0.5f, 1f));
            PumpUntil(() => SDL_GetGamepadAxis(gp, AX_RIGHTY) > 32000, 1500);
            Pump(40);
            Check("the right stick pushed down reads on RIGHTY and does not press ZR",
                  SDL_GetGamepadAxis(gp, AX_RIGHTY) >= 32700 && SDL_GetGamepadAxis(gp, AX_RIGHT_TRIGGER) == 0,
                  $"RIGHTY {SDL_GetGamepadAxis(gp, AX_RIGHTY)}, right trigger {SDL_GetGamepadAxis(gp, AX_RIGHT_TRIGGER)}");
            var stickOnly = new HMGamepadState { Axes = new Dictionary<HMAxis, float> { [profile.Sticks[1].YAxis] = 1f } };
            a.Stage(stickOnly);
            Pump(80);
            Check("the same holds for a consumer that writes the stick axis and nothing else",
                  SDL_GetGamepadAxis(gp, AX_RIGHTY) >= 32700 && SDL_GetGamepadAxis(gp, AX_RIGHT_TRIGGER) == 0,
                  $"RIGHTY {SDL_GetGamepadAxis(gp, AX_RIGHTY)}, right trigger {SDL_GetGamepadAxis(gp, AX_RIGHT_TRIGGER)}");
            short zl = Axis(Sticks(0.5f, 0.5f, 0.5f, 0.5f, lt: 1f), AX_LEFT_TRIGGER, x => x > 32000);
            short zr = Axis(Sticks(0.5f, 0.5f, 0.5f, 0.5f, rt: 1f), AX_RIGHT_TRIGGER, x => x > 32000);
            Check("ZL and ZR read as full triggers", zl >= 32700 && zr >= 32700, $"{zl} {zr}");
            a.Stage(new HMGamepadState());
            Pump(60);

            // ── Rumble ──────────────────────────────────────────────────
            Console.WriteLine("\n-- C3 rumble --");
            while (a.Decoded.TryDequeue(out _)) { }
            Check("SDL_RumbleGamepad(0x8000, 0x4000) is accepted", SDL_RumbleGamepad(gp, 0x8000, 0x4000, 2000), Str(SDL_GetError()));
            bool half = PumpUntil(() => a.Decoded.Any(d => Math.Abs(d.Left - 127) <= 1 && Math.Abs(d.Right - 64) <= 1), 2000);
            Check("it raises OutputDecoded with leftMotor 127 and rightMotor 64, within 1",
                  half, string.Join(" ", a.Decoded.Take(6).Select(d => $"{d.Left}/{d.Right}")));
            var raw = a.Decoded.FirstOrDefault(d => d.Left != 0).Raw;
            Check("the bytes SDL wrote are 87 c5 21 91 38 in both blocks",
                  raw != null && raw.Length == 64 && raw.AsSpan(2, 5).SequenceEqual(H("87c5219138")) && raw.AsSpan(0x12, 5).SequenceEqual(H("87c5219138")),
                  raw != null ? X(raw.AsSpan(0, 0x18)) : "none");
            while (a.Decoded.TryDequeue(out _)) { }
            SDL_RumbleGamepad(gp, 0, 0, 0);
            Check("a stop raises 0 and 0", PumpUntil(() => a.Decoded.Any(d => d.Left == 0 && d.Right == 0), 2000));
            while (a.Decoded.TryDequeue(out _)) { }
            SDL_RumbleGamepad(gp, 0xFFFF, 0xFFFF, 500);
            Check("full scale raises 255 and 255", PumpUntil(() => a.Decoded.Any(d => d.Left == 255 && d.Right == 255), 2000));
            SDL_RumbleGamepad(gp, 0, 0, 0);
            Pump(100);
        }
        finally
        {
            if (gp != IntPtr.Zero) SDL_CloseGamepad(gp);
            a.Dispose();
            Check("teardown removes the USB device", a.WaitGone(15000));
            Pump(500);   // let SDL see the removal and free its handle
        }
    }

    /// <summary>The SDL half of part D. The fork opens every 057E:2069
    /// device over libusb and keeps the one whose serial string equals the
    /// HID device's (SDL_hidapi_switch2.c, the Windows branch of InitUSB).
    /// The serial SDL then reports comes from flash over that libusb handle,
    /// so a gamepad that shows one persona's input under the other's serial
    /// would be reading the wrong handle.</summary>
    static void SdlTwoPads(Attached one, Attached two)
    {
        if (!EnsureSdl()) return;
        var seen = WaitForSwitch2(2, 20000);
        Check("SDL lists both personas as gamepads", seen.Count == 2 && seen.All(s => s.IsGamepad), $"{seen.Count} listed");
        if (seen.Count != 2 || !seen.All(s => s.IsGamepad)) return;

        var pads = seen.Select(s => SDL_OpenGamepad(s.Id)).ToArray();
        try
        {
            if (pads.Any(p => p == IntPtr.Zero)) { Check("both open", false, Str(SDL_GetError())); return; }
            var serials = pads.Select(p => Str(SDL_GetGamepadSerial(p))).ToArray();
            Check("the two gamepads carry the two personas' serials",
                  serials.OrderBy(s => s).SequenceEqual(new[] { one.Serial, two.Serial }.OrderBy(s => s)), string.Join(" / ", serials));

            foreach (var (persona, other) in new[] { (one, two), (two, one) })
            {
                persona.Stage(new HMGamepadState { Buttons = HMButton.B });
                other.Stage(new HMGamepadState());
                PumpUntil(() => pads.Any(p => SDL_GetGamepadButton(p, GP_SOUTH)), 2000);
                Pump(60);
                var pressed = pads.Where(p => SDL_GetGamepadButton(p, GP_SOUTH)).ToArray();
                Check($"a press on persona {persona.Serial} shows on one gamepad, the one whose flash serial is {persona.Serial}",
                      pressed.Length == 1 && Str(SDL_GetGamepadSerial(pressed[0])) == persona.Serial,
                      $"{pressed.Length} pressed{(pressed.Length == 1 ? ", serial " + Str(SDL_GetGamepadSerial(pressed[0])) : "")}");
                persona.Stage(new HMGamepadState());
                PumpUntil(() => !pads.Any(p => SDL_GetGamepadButton(p, GP_SOUTH)), 2000);
            }
        }
        finally
        {
            foreach (var p in pads) if (p != IntPtr.Zero) SDL_CloseGamepad(p);
        }
    }
}
