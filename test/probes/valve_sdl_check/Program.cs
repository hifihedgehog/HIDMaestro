// Valve personas through STOCK SDL3 (issue #56).
//
// S52 reads each persona's frame off the HID stack and decodes it with the
// arithmetic copied out of SDL's drivers. This probe removes the copy: it
// loads a real SDL3.dll built from libsdl-org/SDL at upstream main and asks
// SDL itself what it sees. SDL's Steam drivers (SDL_hidapi_steam.c,
// SDL_hidapi_steamdeck.c, SDL_hidapi_steam_triton.c) are the decoders Steam
// Input is built on, so a persona that satisfies them is a persona a
// Valve-aware consumer reads correctly.
//
// The SDL checkout beside this repo is a fork carrying a deliberate
// HIDMaestro skip filter (issue #42's owner identifier), which would make
// this test vacuous. The build feeds in a stock checkout instead.
//
// Discriminator for "a Valve driver bound it, not a generic HID fallback":
// touchpad count. Only the three Steam drivers publish two touchpads on
// these devices; anything else publishes none.
//
// Exit 0 PASS, 1 FAIL, 2 SKIP (no SDL3.dll beside the probe).

using System;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;

static class ValveSdlCheck
{
    const string SDL = "SDL3";

    [DllImport(SDL)] static extern bool SDL_SetHint(string name, string value);
    [DllImport(SDL)] static extern bool SDL_Init(uint flags);
    [DllImport(SDL)] static extern void SDL_Quit();
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepads(out int count);
    [DllImport(SDL)] static extern IntPtr SDL_OpenGamepad(uint id);
    [DllImport(SDL)] static extern void SDL_CloseGamepad(IntPtr gp);
    [DllImport(SDL)] static extern void SDL_UpdateGamepads();
    [DllImport(SDL)] static extern ushort SDL_GetGamepadVendor(IntPtr gp);
    [DllImport(SDL)] static extern ushort SDL_GetGamepadProduct(IntPtr gp);
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepadName(IntPtr gp);
    [DllImport(SDL)] static extern short SDL_GetGamepadAxis(IntPtr gp, int axis);
    [DllImport(SDL)] static extern int SDL_GetNumGamepadTouchpads(IntPtr gp);
    [DllImport(SDL)] static extern bool SDL_GetGamepadTouchpadFinger(
        IntPtr gp, int touchpad, int finger, out bool down,
        out float x, out float y, out float pressure);
    [DllImport(SDL)] static extern bool SDL_SetGamepadSensorEnabled(IntPtr gp, int type, bool enabled);
    [DllImport(SDL)] static extern bool SDL_GamepadHasSensor(IntPtr gp, int type);
    [DllImport(SDL)] static extern bool SDL_GetGamepadSensorData(
        IntPtr gp, int type, [Out] float[] data, int num_values);
    [DllImport(SDL)] static extern void SDL_free(IntPtr mem);
    [DllImport(SDL)] static extern IntPtr SDL_GetError();
    [DllImport(SDL)] static extern IntPtr SDL_GetJoysticks(out int count);
    [DllImport(SDL)] static extern ushort SDL_GetJoystickVendorForID(uint id);
    [DllImport(SDL)] static extern ushort SDL_GetJoystickProductForID(uint id);
    [DllImport(SDL)] static extern IntPtr SDL_GetJoystickNameForID(uint id);
    [DllImport(SDL)] static extern bool SDL_IsGamepad(uint id);

    const uint SDL_INIT_GAMEPAD = 0x00002000u;
    const int AXIS_LEFTX = 0, AXIS_RIGHT_TRIGGER = 5;
    const int SENSOR_ACCEL = 1, SENSOR_GYRO = 2;

    static int s_fail;

    static void Check(string what, bool ok, string detail = "")
    {
        if (!ok) s_fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static string Err() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "";

    /// <summary>Pump SDL until a gamepad with this VID/PID shows up.</summary>
    static IntPtr WaitForGamepad(ushort vid, ushort pid, int seconds)
    {
        for (int i = 0; i < seconds * 10; i++)
        {
            SDL_UpdateGamepads();
            IntPtr arr = SDL_GetGamepads(out int n);
            if (arr != IntPtr.Zero)
            {
                try
                {
                    for (int j = 0; j < n; j++)
                    {
                        uint id = (uint)Marshal.ReadInt32(arr, j * 4);
                        IntPtr gp = SDL_OpenGamepad(id);
                        if (gp == IntPtr.Zero) continue;
                        if (SDL_GetGamepadVendor(gp) == vid && SDL_GetGamepadProduct(gp) == pid)
                            return gp;
                        SDL_CloseGamepad(gp);
                    }
                }
                finally { SDL_free(arr); }
            }
            Thread.Sleep(100);
        }
        return IntPtr.Zero;
    }

    /// <summary>Submit a state and let SDL's reader thread pick it up.</summary>
    static void Drive(HMController c, HMGamepadState st)
    {
        for (int i = 0; i < 8; i++) { c.SubmitState(st); Thread.Sleep(50); }
        SDL_UpdateGamepads();
        Thread.Sleep(80);
        SDL_UpdateGamepads();
    }

    /// <summary>Find a STOCK SDL3.dll. The sibling SDL3-build checkout
    /// carries two: build\Release is the fork, which skips HIDMaestro
    /// devices outright (issue #42's owner identifier) and would make every
    /// assertion below vacuous, and build-stock\Release is upstream
    /// libsdl-org/SDL. Only the second one counts, so only the second one
    /// is searched for.</summary>
    static string? FindStockSdl()
    {
        string root = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && root.Length > 3; i++)
        {
            string sib = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(root, "..", "SDL3-build", "build-stock", "Release", "SDL3.dll"));
            if (System.IO.File.Exists(sib)) return sib;
            root = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, ".."));
        }
        string local = System.IO.Path.Combine(AppContext.BaseDirectory, "SDL3.dll");
        return System.IO.File.Exists(local) ? local : null;
    }

    static int Main()
    {
        string? dll = FindStockSdl();
        if (dll == null)
        {
            Console.WriteLine("[SKIP] no stock SDL3.dll found; this check needs the sibling");
            Console.WriteLine("       SDL3-build/build-stock/Release build of upstream libsdl-org/SDL.");
            return 2;
        }
        NativeLibrary.SetDllImportResolver(typeof(ValveSdlCheck).Assembly,
            (name, asm, path) => name == SDL ? NativeLibrary.Load(dll) : IntPtr.Zero);
        Console.WriteLine("=== Valve personas through stock SDL3 ===");
        Console.WriteLine($"  SDL3.dll: {dll}");

        // The Steam drivers are opt-in, and Triton shares the 2015
        // controller's hint. RAWINPUT off so the hidapi driver is the only
        // path that can claim the device.
        SDL_SetHint("SDL_JOYSTICK_HIDAPI", "1");
        SDL_SetHint("SDL_JOYSTICK_HIDAPI_STEAM", "1");
        SDL_SetHint("SDL_JOYSTICK_HIDAPI_STEAMDECK", "1");
        SDL_SetHint("SDL_JOYSTICK_RAWINPUT", "0");
        SDL_SetHint("SDL_JOYSTICK_THREAD", "1");

        if (!SDL_Init(SDL_INIT_GAMEPAD))
        {
            Console.WriteLine($"  SDL_Init failed: {Err()}");
            return 1;
        }

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        // Tol is how far a corner contact may sit from the corner. The
        // 2015 controller gets 0.25 because SDL rotates that model's pads
        // by 15 degrees and adds a 1000-unit offset while a finger is down
        // (SDL_hidapi_steam.c: RotatePad(&nLeftPadX, &nLeftPadY,
        // -flRotationAngle) and the nPadOffset block right below it). That
        // is hardware compensation SDL applies to real units too, so the
        // corner is expected to land inside the rotated box, not on it.
        var cases = new (string Id, ushort Vid, ushort Pid, float Tol)[]
        {
            ("steam-deck-composite",       0x28DE, 0x1205, 0.10f),
            ("steam-controller-composite", 0x28DE, 0x1102, 0.25f),
            ("steam-controller-2",         0x28DE, 0x1302, 0.10f),
        };

        foreach (var t in cases)
        {
            Console.WriteLine();
            Console.WriteLine($"-- {t.Id} --");
            var prof = ctx.GetProfile(t.Id);
            Check("profile is in the catalog", prof != null);
            if (prof == null) continue;

            HMController? c = null;
            IntPtr gp = IntPtr.Zero;
            try
            {
                c = ctx.CreateController(prof);
                gp = WaitForGamepad(t.Vid, t.Pid, 20);
                Check("stock SDL enumerates the persona as a gamepad", gp != IntPtr.Zero);
                if (gp == IntPtr.Zero)
                {
                    // Say what SDL DID see, so a miss names its own cause
                    // instead of leaving the joystick layer unexamined.
                    IntPtr js = SDL_GetJoysticks(out int jn);
                    Console.WriteLine($"     SDL sees {jn} joystick(s):");
                    if (js != IntPtr.Zero)
                    {
                        try
                        {
                            for (int j = 0; j < jn; j++)
                            {
                                uint id = (uint)Marshal.ReadInt32(js, j * 4);
                                Console.WriteLine($"       {SDL_GetJoystickVendorForID(id):X4}:{SDL_GetJoystickProductForID(id):X4} " +
                                                  $"gamepad={SDL_IsGamepad(id)} " +
                                                  $"{Marshal.PtrToStringUTF8(SDL_GetJoystickNameForID(id))}");
                            }
                        }
                        finally { SDL_free(js); }
                    }
                    continue;
                }

                Console.WriteLine($"     SDL name: {Marshal.PtrToStringUTF8(SDL_GetGamepadName(gp))}");

                // Two touchpads is the tell that one of SDL's three Steam
                // drivers claimed the device rather than a generic fallback.
                int pads = SDL_GetNumGamepadTouchpads(gp);
                Check("SDL bound it with a Valve driver (two touchpads published)",
                      pads >= 2, $"touchpads={pads}");

                Drive(c, new HMGamepadState
                {
                    Axes = HMGamepadStateHelpers.StandardAxes(prof, 0.0f, 0.5f, 0.5f, 0.5f, 0.0f, 1.0f),
                });
                short lx1 = SDL_GetGamepadAxis(gp, AXIS_LEFTX);
                short rt1 = SDL_GetGamepadAxis(gp, AXIS_RIGHT_TRIGGER);
                Check("SDL reads the left stick hard left", lx1 <= -30000, $"LEFTX={lx1}");
                Check("SDL reads the right trigger fully pulled", rt1 >= 30000, $"RT={rt1}");

                Drive(c, new HMGamepadState
                {
                    Axes = HMGamepadStateHelpers.StandardAxes(prof, 1.0f, 0.5f, 0.5f, 0.5f, 0.0f, 0.0f),
                });
                short lx2 = SDL_GetGamepadAxis(gp, AXIS_LEFTX);
                short rt2 = SDL_GetGamepadAxis(gp, AXIS_RIGHT_TRIGGER);
                Check("SDL reads the left stick hard right", lx2 >= 30000, $"LEFTX={lx2}");
                Check("the axis swept rather than sticking", lx2 - lx1 >= 60000, $"span={lx2 - lx1}");
                Check("SDL reads the right trigger released", rt2 <= 1000, $"RT={rt2}");

                Drive(c, new HMGamepadState
                {
                    Axes = HMGamepadStateHelpers.StandardAxes(prof, 0.5f, 0.5f, 0.5f, 0.5f, 0.0f, 0.0f),
                    TouchpadFinger0Active = true, TouchpadFinger0X = 1919, TouchpadFinger0Y = 0,
                    TouchpadFinger1Active = true, TouchpadFinger1X = 0, TouchpadFinger1Y = 1079,
                });
                if (pads >= 2)
                {
                    SDL_GetGamepadTouchpadFinger(gp, 0, 0, out bool d0, out float x0, out float y0, out float p0);
                    SDL_GetGamepadTouchpadFinger(gp, 1, 0, out bool d1, out float x1, out float y1, out float p1);
                    Check("SDL sees a contact on the left touchpad", d0, $"({x0:F3},{y0:F3}) p={p0:F2}");
                    Check("SDL places it at the right edge, top",
                          x0 > 1.0f - t.Tol && y0 < t.Tol, $"tol={t.Tol:F2}");
                    Check("SDL sees a contact on the right touchpad", d1, $"({x1:F3},{y1:F3}) p={p1:F2}");
                    Check("SDL places it at the opposite corner",
                          x1 < t.Tol && y1 > 1.0f - t.Tol, $"tol={t.Tol:F2}");
                }

                // Motion. SDL scales the raw firmware units into rad/s and
                // m/s2, so the assertion is that non-zero motion arrives on
                // every axis, not a specific magnitude.
                bool hasGyro = SDL_GamepadHasSensor(gp, SENSOR_GYRO);
                bool hasAccel = SDL_GamepadHasSensor(gp, SENSOR_ACCEL);
                Check("SDL publishes gyroscope and accelerometer sensors", hasGyro && hasAccel,
                      $"gyro={hasGyro} accel={hasAccel}");
                if (hasGyro && hasAccel)
                {
                    SDL_SetGamepadSensorEnabled(gp, SENSOR_GYRO, true);
                    SDL_SetGamepadSensorEnabled(gp, SENSOR_ACCEL, true);
                    Drive(c, new HMGamepadState
                    {
                        Axes = HMGamepadStateHelpers.StandardAxes(prof, 0.5f, 0.5f, 0.5f, 0.5f, 0.0f, 0.0f),
                        AccelX = 8000, AccelY = -12000, AccelZ = 16000,
                        GyroPitch = -20000, GyroYaw = 24000, GyroRoll = -28000,
                    });
                    var g = new float[3];
                    var a = new float[3];
                    SDL_GetGamepadSensorData(gp, SENSOR_GYRO, g, 3);
                    SDL_GetGamepadSensorData(gp, SENSOR_ACCEL, a, 3);
                    Check("SDL reports angular velocity on all three gyro axes",
                          g[0] != 0f && g[1] != 0f && g[2] != 0f,
                          $"[{g[0]:F2},{g[1]:F2},{g[2]:F2}] rad/s");
                    Check("SDL reports acceleration on all three axes",
                          a[0] != 0f && a[1] != 0f && a[2] != 0f,
                          $"[{a[0]:F2},{a[1]:F2},{a[2]:F2}] m/s2");
                }
            }
            catch (Exception ex) { Check("persona ran without throwing", false, ex.Message); }
            finally
            {
                if (gp != IntPtr.Zero) SDL_CloseGamepad(gp);
                c?.Dispose();
                Thread.Sleep(500);
                SDL_UpdateGamepads();
            }
        }

        SDL_Quit();
        Console.WriteLine();
        Console.WriteLine(s_fail == 0
            ? "=== STOCK SDL READS ALL THREE VALVE PERSONAS ==="
            : $"=== {s_fail} check(s) FAILED ===");
        return s_fail == 0 ? 0 : 1;
    }
}
