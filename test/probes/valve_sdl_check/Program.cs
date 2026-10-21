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
using System.Collections.Generic;
using System.Linq;
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
    [DllImport(SDL)] static extern bool SDL_GetGamepadButton(IntPtr gp, int button);
    [DllImport(SDL)] static extern bool SDL_GamepadHasCapSense(IntPtr gp, int type);
    [DllImport(SDL)] static extern bool SDL_GetGamepadCapSense(IntPtr gp, int type);
    [DllImport(SDL)] static extern unsafe bool SDL_PollEvent(byte* ev);
    [DllImport(SDL)] static extern uint SDL_GetGamepadID(IntPtr gp);
    [DllImport(SDL)] static extern ulong SDL_GetTicksNS();

    // SDL_EVENT_GAMEPAD_SENSOR_UPDATE (SDL_events.h: SDL_EVENT_GAMEPAD_AXIS_MOTION
    // 0x650 and the ninth after it) and SDL_GamepadSensorEvent's layout:
    // which at 16, sensor at 20, data[3] at 24, sensor_timestamp at 40.
    const uint EV_GAMEPAD_SENSOR_UPDATE = 0x659;

    // SDL_GamepadButton (SDL_gamepad.h:156-177) and SDL_GamepadCapSenseType
    // (:243-251).
    const int GP_RIGHT_STICK = 8, GP_MISC1 = 15, GP_RIGHT_PADDLE1 = 16, GP_LEFT_PADDLE1 = 17,
              GP_RIGHT_PADDLE2 = 18, GP_LEFT_PADDLE2 = 19, GP_TOUCHPAD = 20, GP_MISC2 = 21;
    const int CS_LEFT_STICK = 0, CS_RIGHT_STICK = 1, CS_LEFT_GRIP = 2, CS_RIGHT_GRIP = 3;

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

    static bool Sleep10() { Thread.Sleep(10); return true; }

    /// <summary>Whether SDL lists a gamepad with this VID/PID.</summary>
    static bool Listed(ushort vid, ushort pid)
    {
        IntPtr arr = SDL_GetGamepads(out int n);
        if (arr == IntPtr.Zero) return false;
        try
        {
            for (int j = 0; j < n; j++)
            {
                uint id = (uint)Marshal.ReadInt32(arr, j * 4);
                if (SDL_GetJoystickVendorForID(id) == vid && SDL_GetJoystickProductForID(id) == pid) return true;
            }
            return false;
        }
        finally { SDL_free(arr); }
    }

    /// <summary>SDL's joystick IDs now. SDL never reuses one, so a device
    /// created after this has none of them.</summary>
    static HashSet<uint> JoystickIds()
    {
        SDL_UpdateGamepads();
        var ids = new HashSet<uint>();
        IntPtr arr = SDL_GetJoysticks(out int n);
        if (arr == IntPtr.Zero) return ids;
        try { for (int j = 0; j < n; j++) ids.Add((uint)Marshal.ReadInt32(arr, j * 4)); }
        finally { SDL_free(arr); }
        return ids;
    }

    /// <summary>Pump SDL until a gamepad with this VID/PID shows up, other
    /// than one in <paramref name="skip"/>: a real Valve controller on the
    /// machine has the persona's VID/PID too.</summary>
    static IntPtr WaitForGamepad(ushort vid, ushort pid, int seconds, HashSet<uint> skip)
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
                        if (skip.Contains(id)) continue;
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

    /// <summary>Submit a state until SDL's gamepad view shows what the
    /// check after it asks for, on two reads in a row, or for 3 s, far past
    /// the few tens of ms it takes, so load does not end it early. The
    /// persona repeats its last frame every 4 or 8 ms (idleFrameIntervalMs),
    /// so the second read follows newer frames than the first, and frames
    /// keep coming after the submits stop. A view that never shows it
    /// leaves the check to fail on the last read.</summary>
    static void Drive(HMController c, HMGamepadState st, Func<bool> shown)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long nextSubmit = 0;
        int inRow = 0;
        while (sw.ElapsedMilliseconds < 3000)
        {
            if (sw.ElapsedMilliseconds >= nextSubmit && nextSubmit < 400) { c.SubmitState(st); nextSubmit += 50; }
            Thread.Sleep(10);
            SDL_UpdateGamepads();
            inRow = shown() ? inRow + 1 : 0;
            if (inRow == 2) return;
        }
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

    static int Main(string[] args)
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

        if (args.Length > 0 && (args[0] == "cadence" || args[0] == "motion"))
        {
            int seconds = args.Length > 1 ? int.Parse(args[1]) : 10;
            string? only = args.Length > 2 ? args[2] : null;
            s_rawReader = args.Contains("raw");
            foreach (var t in cases)
            {
                if (only != null && t.Id != only) continue;
                if (args[0] == "cadence") Cadence(ctx, t.Id, t.Vid, t.Pid, seconds, assert: true);
                else Motion(ctx, t.Id, t.Vid, t.Pid);
            }
            SDL_Quit();
            return s_fail == 0 ? 0 : 1;
        }

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
                var present = JoystickIds();
                c = ctx.CreateController(prof);
                gp = WaitForGamepad(t.Vid, t.Pid, 60, present);
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
                }, () => SDL_GetGamepadAxis(gp, AXIS_LEFTX) <= -30000 && SDL_GetGamepadAxis(gp, AXIS_RIGHT_TRIGGER) >= 30000);
                short lx1 = SDL_GetGamepadAxis(gp, AXIS_LEFTX);
                short rt1 = SDL_GetGamepadAxis(gp, AXIS_RIGHT_TRIGGER);
                Check("SDL reads the left stick hard left", lx1 <= -30000, $"LEFTX={lx1}");
                Check("SDL reads the right trigger fully pulled", rt1 >= 30000, $"RT={rt1}");

                Drive(c, new HMGamepadState
                {
                    Axes = HMGamepadStateHelpers.StandardAxes(prof, 1.0f, 0.5f, 0.5f, 0.5f, 0.0f, 0.0f),
                }, () => SDL_GetGamepadAxis(gp, AXIS_LEFTX) >= 30000 && SDL_GetGamepadAxis(gp, AXIS_RIGHT_TRIGGER) <= 1000);
                short lx2 = SDL_GetGamepadAxis(gp, AXIS_LEFTX);
                short rt2 = SDL_GetGamepadAxis(gp, AXIS_RIGHT_TRIGGER);
                Check("SDL reads the left stick hard right", lx2 >= 30000, $"LEFTX={lx2}");
                Check("SDL reads the right trigger released", rt2 <= 1000, $"RT={rt2}");

                Drive(c, new HMGamepadState
                {
                    Axes = HMGamepadStateHelpers.StandardAxes(prof, 0.5f, 0.5f, 0.5f, 0.5f, 0.0f, 0.0f),
                    TouchpadFinger0Active = true, TouchpadFinger0X = 1919, TouchpadFinger0Y = 0,
                    TouchpadFinger1Active = true, TouchpadFinger1X = 0, TouchpadFinger1Y = 1079,
                }, () =>
                {
                    if (pads < 2) return true;
                    SDL_GetGamepadTouchpadFinger(gp, 0, 0, out bool a0, out float ax, out float ay, out _);
                    SDL_GetGamepadTouchpadFinger(gp, 1, 0, out bool a1, out float bx, out float by, out _);
                    return a0 && a1 && ax > 1.0f - t.Tol && ay < t.Tol && bx < t.Tol && by > 1.0f - t.Tol;
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

                // The rear buttons, the trackpad clicks, QAM and the touch
                // sensors, one at a time, each read back from SDL's gamepad
                // view: RIGHT_PADDLE1/LEFT_PADDLE1 are R4/L4 and PADDLE2 the
                // lower pair (SDL_gamepad.h:172-175). The Deck and Triton
                // mappings put the left pad's click on TOUCHPAD and the
                // right one's on MISC2 (SDL_gamepad.c, "touchpad:b17" and
                // "misc2:b16"). The 2015 controller has two rear buttons and
                // no QAM, and SDL reads its right pad click as the right stick.
                var buttonCases = t.Id switch
                {
                    "steam-controller-composite" => new (string, HMButton, int)[]
                    {
                        ("right paddle", HMButton.RightPaddle, GP_RIGHT_PADDLE1),
                        ("left paddle", HMButton.LeftPaddle, GP_LEFT_PADDLE1),
                        ("right trackpad click", HMButton.RightTouchpad, GP_RIGHT_STICK),
                    },
                    _ => new (string, HMButton, int)[]
                    {
                        ("R4", HMButton.RightPaddle, GP_RIGHT_PADDLE1),
                        ("L4", HMButton.LeftPaddle, GP_LEFT_PADDLE1),
                        ("R5", HMButton.RightPaddle2, GP_RIGHT_PADDLE2),
                        ("L5", HMButton.LeftPaddle2, GP_LEFT_PADDLE2),
                        ("left trackpad click", HMButton.LeftTouchpad, GP_TOUCHPAD),
                        ("right trackpad click", HMButton.RightTouchpad, GP_MISC2),
                        ("QAM", HMButton.Misc1, GP_MISC1),
                    },
                };
                var neutral = HMGamepadStateHelpers.StandardAxes(prof, 0.5f, 0.5f, 0.5f, 0.5f, 0.0f, 0.0f);
                foreach (var (label, button, gpButton) in buttonCases)
                {
                    Drive(c, new HMGamepadState { Axes = neutral, Buttons = button }, () => SDL_GetGamepadButton(gp, gpButton));
                    bool down = SDL_GetGamepadButton(gp, gpButton);
                    Drive(c, new HMGamepadState { Axes = neutral }, () => !SDL_GetGamepadButton(gp, gpButton));
                    bool up = !SDL_GetGamepadButton(gp, gpButton);
                    Check($"SDL reads {label} ({button}) on its own button and lets go", down && up,
                          $"down={down} released={up}");
                }
                if (t.Id != "steam-controller-composite")
                {
                    var touchCases = new List<(string, HMButton, int)>
                    {
                        ("left stick touch", HMButton.LeftStickTouch, CS_LEFT_STICK),
                        ("right stick touch", HMButton.RightStickTouch, CS_RIGHT_STICK),
                    };
                    if (t.Id == "steam-controller-2")
                    {
                        touchCases.Add(("left grip touch", HMButton.LeftGripTouch, CS_LEFT_GRIP));
                        touchCases.Add(("right grip touch", HMButton.RightGripTouch, CS_RIGHT_GRIP));
                    }
                    foreach (var (label, button, sense) in touchCases)
                    {
                        bool has = SDL_GamepadHasCapSense(gp, sense);
                        Drive(c, new HMGamepadState { Axes = neutral, Buttons = button }, () => SDL_GetGamepadCapSense(gp, sense));
                        bool on = SDL_GetGamepadCapSense(gp, sense);
                        Drive(c, new HMGamepadState { Axes = neutral }, () => !SDL_GetGamepadCapSense(gp, sense));
                        bool off = !SDL_GetGamepadCapSense(gp, sense);
                        Check($"SDL reads the {label} sensor ({button})", has && on && off,
                              $"has={has} on={on} off={off}");
                    }
                }

                // Motion: the calibrated fields, in SDL's sensor frame, read
                // back as the same vector on the same axes (issue #72).
                bool hasGyro = SDL_GamepadHasSensor(gp, SENSOR_GYRO);
                bool hasAccel = SDL_GamepadHasSensor(gp, SENSOR_ACCEL);
                Check("SDL publishes gyroscope and accelerometer sensors", hasGyro && hasAccel,
                      $"gyro={hasGyro} accel={hasAccel}");
                if (hasGyro && hasAccel)
                {
                    SDL_SetGamepadSensorEnabled(gp, SENSOR_GYRO, true);
                    SDL_SetGamepadSensorEnabled(gp, SENSOR_ACCEL, true);
                    CalibratedMotion(c, gp, prof);
                }

                // Pad pressure. The Deck and Triton drivers pass the wire's
                // pressure through as the touch's (SDL_hidapi_steamdeck.c:272
                // and 284, SDL_hidapi_steam_triton.c:258 and 270). The 2015
                // driver makes up its own from the touch and click bits
                // (SDL_hidapi_steam.c:1498 and 1518), so it has none to check.
                if (t.Id != "steam-controller-composite" && pads >= 2)
                    PadPressure(c, gp, prof);
            }
            catch (Exception ex) { Check("persona ran without throwing", false, ex.Message); }
            finally
            {
                if (gp != IntPtr.Zero) SDL_CloseGamepad(gp);
                c?.Dispose();
                // Until SDL lists the persona no more, so the next part
                // does not see this one; a minute is the hang bound.
                var gone = System.Diagnostics.Stopwatch.StartNew();
                do { SDL_UpdateGamepads(); }
                while (Listed(t.Vid, t.Pid) && gone.ElapsedMilliseconds < 60_000 && Sleep10());
            }
        }

        // Issue #72 item 2: each persona reports on its own clock, so the
        // sensor time SDL credits matches wall time at any submit rate.
        foreach (var t in cases)
            if (ctx.GetProfile(t.Id) != null) Cadence(ctx, t.Id, t.Vid, t.Pid, 2, assert: true);

        SDL_Quit();
        Console.WriteLine();
        Console.WriteLine(s_fail == 0
            ? "=== STOCK SDL READS ALL THREE VALVE PERSONAS ==="
            : $"=== {s_fail} check(s) FAILED ===");
        return s_fail == 0 ? 0 : 1;
    }

    /// <summary>Create one persona, wait for stock SDL to list it and turn
    /// its sensors on. Null when SDL never lists it.</summary>
    static (HMController C, IntPtr Gp)? Open(HMContext ctx, string id, ushort vid, ushort pid)
    {
        var prof = ctx.GetProfile(id)!;
        var present = JoystickIds();
        var c = ctx.CreateController(prof);
        IntPtr gp = WaitForGamepad(vid, pid, 60, present);
        if (gp == IntPtr.Zero)
        {
            Check($"{id}: stock SDL enumerates the persona", false);
            c.Dispose();
            return null;
        }
        SDL_SetGamepadSensorEnabled(gp, SENSOR_GYRO, true);
        SDL_SetGamepadSensorEnabled(gp, SENSOR_ACCEL, true);
        return (c, gp);
    }

    /// <summary>cadence ... raw: a second reader on the persona's HID
    /// collection beside SDL, which reads every input report as HID class
    /// hands it over, so a gap in SDL's events can be told apart from a gap
    /// on the wire in the same window.</summary>
    static bool s_rawReader;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string p, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr h, byte[] buf, int n, out int read, IntPtr ov);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid c, string? d, uint f);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid c, string? d, char[] b, uint bl, uint f);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, byte[] caps);
    [DllImport("hid.dll")] static extern bool HidD_SetNumInputBuffers(IntPtr h, uint n);

    sealed class RawLog
    {
        readonly IntPtr _h;
        readonly Thread _t;
        public readonly List<(long T, byte[] F)> Reports = new();

        RawLog(IntPtr h, int len)
        {
            _h = h;
            _t = new Thread(() =>
            {
                var buf = new byte[len];
                while (ReadFile(_h, buf, buf.Length, out int n, IntPtr.Zero) && n > 0)
                    lock (Reports) Reports.Add((System.Diagnostics.Stopwatch.GetTimestamp(), (byte[])buf.Clone()));
            }) { IsBackground = true, Priority = ThreadPriority.Highest };
            _t.Start();
        }

        /// <summary>The persona's collection with the longest input report,
        /// the controller's, opened shared beside SDL's handle.</summary>
        public static RawLog? Start(ushort vid, ushort pid)
        {
            var guid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
            if (CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, null, 0) != 0 || len == 0) return null;
            var buf = new char[len];
            if (CM_Get_Device_Interface_ListW(ref guid, null, buf, len, 0) != 0) return null;
            string? best = null;
            int bestLen = 0;
            foreach (string p in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (p.IndexOf($"vid_{vid:x4}", StringComparison.OrdinalIgnoreCase) < 0 || p.IndexOf($"pid_{pid:x4}", StringComparison.OrdinalIgnoreCase) < 0) continue;
                IntPtr h = CreateFileW(p, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h == new IntPtr(-1)) continue;
                try
                {
                    if (!HidD_GetPreparsedData(h, out IntPtr pp)) continue;
                    var caps = new byte[64];
                    HidP_GetCaps(pp, caps);
                    HidD_FreePreparsedData(pp);
                    int inLen = BitConverter.ToUInt16(caps, 4);
                    if (inLen > bestLen) { best = p; bestLen = inLen; }
                }
                finally { CloseHandle(h); }
            }
            if (best == null) return null;
            IntPtr handle = CreateFileW(best, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle == new IntPtr(-1)) return null;
            HidD_SetNumInputBuffers(handle, 512);
            return new RawLog(handle, bestLen);
        }

        /// <summary>Stops the reader and prints the window's reports: their
        /// rate, the longest gap and the reports either side of it, and how
        /// many carry the same sensor stamp as the report before.</summary>
        public void Stop(int stampAt)
        {
            CloseHandle(_h);
            _t.Join(500);
            List<(long T, byte[] F)> r;
            lock (Reports) r = new(Reports);
            if (r.Count < 2) { Console.WriteLine($"     raw: {r.Count} report(s)"); return; }
            double f = System.Diagnostics.Stopwatch.Frequency;
            int maxAt = 1, sameStamp = 0;
            for (int i = 1; i < r.Count; i++)
            {
                if (r[i].T - r[i - 1].T > r[maxAt].T - r[maxAt - 1].T) maxAt = i;
                if (stampAt >= 0 && r[i].F.AsSpan(stampAt, 4).SequenceEqual(r[i - 1].F.AsSpan(stampAt, 4))) sameStamp++;
            }
            uint Stamp(byte[] b) => stampAt >= 0 ? BitConverter.ToUInt32(b, stampAt) : 0;
            Console.WriteLine($"     raw: {r.Count} reports, {(r.Count - 1) * f / (r[^1].T - r[0].T):F1}/s, longest gap {(r[maxAt].T - r[maxAt - 1].T) * 1000 / f:F2} ms "
                + $"at {(r[maxAt].T - r[0].T) * 1000 / f:F0} ms, stamps {Stamp(r[maxAt - 1].F)} then {Stamp(r[maxAt].F)}, {sameStamp} report(s) repeat the stamp before");
        }
    }

    static void Close(HMController c, IntPtr gp, ushort vid, ushort pid)
    {
        SDL_CloseGamepad(gp);
        c.Dispose();
        var gone = System.Diagnostics.Stopwatch.StartNew();
        do { SDL_UpdateGamepads(); }
        while (Listed(vid, pid) && gone.ElapsedMilliseconds < 60_000 && Sleep10());
    }

    /// <summary>Issue #72 item 1: the calibrated motion fields, in SDL's
    /// frame, read back through stock SDL. Distinct magnitudes on every axis
    /// so a swapped or negated axis shows.</summary>
    static void Motion(HMContext ctx, string id, ushort vid, ushort pid)
    {
        Console.WriteLine();
        Console.WriteLine($"-- {id}: calibrated motion --");
        if (Open(ctx, id, vid, pid) is not { } o) return;
        var prof = ctx.GetProfile(id)!;
        var (c, gp) = o;
        try
        {
            CalibratedMotion(c, gp, prof);
            if (SDL_GetNumGamepadTouchpads(gp) > 0)
            {
                SDL_GetGamepadTouchpadFinger(gp, 0, 0, out bool down, out float x, out float y, out float p);
                Console.WriteLine($"     touch 0: down={down} ({x:F3},{y:F3}) pressure={p:F4}");
            }
        }
        finally { Close(c, gp, vid, pid); }
    }

    /// <summary>The calibrated motion fields, in SDL's frame, read back
    /// through stock SDL with sensors on. Distinct magnitudes on every axis
    /// so a swapped or negated axis shows. A finger rests on the left pad,
    /// so the frame differs from an idle one on every persona.</summary>
    static void CalibratedMotion(HMController c, IntPtr gp, HMProfile prof)
    {
        float[] dps = { 30f, 90f, -60f };
        float[] g = { 0.25f, 1.0f, -0.5f };
        var st = new HMGamepadState
        {
            Axes = HMGamepadStateHelpers.StandardAxes(prof, 0.5f, 0.5f, 0.5f, 0.5f, 0.0f, 0.0f),
            GyroDpsX = dps[0], GyroDpsY = dps[1], GyroDpsZ = dps[2],
            AccelGX = g[0], AccelGY = g[1], AccelGZ = g[2],
            TouchpadFinger0Active = true, TouchpadFinger0X = 960, TouchpadFinger0Y = 540,
        };
        var gy = new float[3];
        var ac = new float[3];
        bool Close3(float[] got, float[] want, float tol)
            => Math.Abs(got[0] - want[0]) <= tol && Math.Abs(got[1] - want[1]) <= tol && Math.Abs(got[2] - want[2]) <= tol;
        var wantGyro = Array.ConvertAll(dps, d => d * MathF.PI / 180f);
        var wantAccel = Array.ConvertAll(g, v => v * 9.80665f);
        Drive(c, st, () =>
        {
            SDL_GetGamepadSensorData(gp, SENSOR_GYRO, gy, 3);
            SDL_GetGamepadSensorData(gp, SENSOR_ACCEL, ac, 3);
            return Close3(gy, wantGyro, 0.01f) && Close3(ac, wantAccel, 0.02f);
        });
        SDL_GetGamepadSensorData(gp, SENSOR_GYRO, gy, 3);
        SDL_GetGamepadSensorData(gp, SENSOR_ACCEL, ac, 3);
        Check("SDL reads GyroDps (30, 90, -60) as that angular velocity",
              Close3(gy, wantGyro, 0.01f),
              $"got [{gy[0]:F4},{gy[1]:F4},{gy[2]:F4}] rad/s, want [{wantGyro[0]:F4},{wantGyro[1]:F4},{wantGyro[2]:F4}]");
        Check("SDL reads AccelG (0.25, 1, -0.5) as that acceleration",
              Close3(ac, wantAccel, 0.02f),
              $"got [{ac[0]:F3},{ac[1]:F3},{ac[2]:F3}] m/s2, want [{wantAccel[0]:F3},{wantAccel[1]:F3},{wantAccel[2]:F3}]");
    }

    /// <summary>Issue #72 item 4: a touch at pressure 0.25 on each pad reads
    /// 0.25 through SDL, and a touch at pressure 0, a consumer that knows
    /// only touch, still reads full pressure.</summary>
    static void PadPressure(HMController c, IntPtr gp, HMProfile prof)
    {
        foreach (float sent in new[] { 0.25f, 0f })
        {
            float want = sent == 0f ? 1f : sent;
            var st = new HMGamepadState
            {
                Axes = HMGamepadStateHelpers.StandardAxes(prof, 0.5f, 0.5f, 0.5f, 0.5f, 0.0f, 0.0f),
                TouchpadFinger0Active = true, TouchpadFinger0X = 480, TouchpadFinger0Y = 270, TouchpadFinger0Pressure = sent,
                TouchpadFinger1Active = true, TouchpadFinger1X = 1440, TouchpadFinger1Y = 810, TouchpadFinger1Pressure = sent,
            };
            float p0 = -1, p1 = -1;
            bool d0 = false, d1 = false;
            Drive(c, st, () =>
            {
                SDL_GetGamepadTouchpadFinger(gp, 0, 0, out d0, out _, out _, out p0);
                SDL_GetGamepadTouchpadFinger(gp, 1, 0, out d1, out _, out _, out p1);
                return d0 && d1 && Math.Abs(p0 - want) <= 0.01f && Math.Abs(p1 - want) <= 0.01f;
            });
            SDL_GetGamepadTouchpadFinger(gp, 0, 0, out d0, out _, out _, out p0);
            SDL_GetGamepadTouchpadFinger(gp, 1, 0, out d1, out _, out _, out p1);
            Check($"SDL reads a touch at pressure {sent:0.##} as {want:0.##} on both pads",
                  d0 && d1 && Math.Abs(p0 - want) <= 0.01f && Math.Abs(p1 - want) <= 0.01f,
                  $"left down={d0} p={p0:F4}, right down={d1} p={p1:F4}");
        }
        // Lift, so the next persona's first frames are not a touch.
        var up = new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(prof, 0.5f, 0.5f, 0.5f, 0.5f, 0.0f, 0.0f) };
        Drive(c, up, () =>
        {
            SDL_GetGamepadTouchpadFinger(gp, 0, 0, out bool a, out _, out _, out _);
            SDL_GetGamepadTouchpadFinger(gp, 1, 0, out bool b, out _, out _, out _);
            return !a && !b;
        });
    }

    /// <summary>Issue #72 item 2. A constant motion vector submitted at
    /// 1 kHz, 250 Hz and 125 Hz. SDL stamps each Deck and 2015 report with a
    /// fixed interval, so the span of its sensor timestamps against the
    /// wall-clock span of the same events is how much rotation SDL
    /// integrates per unit of real rotation. 1.00 is right. With
    /// <paramref name="assert"/>, each rate checks from 0.97 to 1.03 the
    /// median sensor time SDL credits a report over the median wall time
    /// between reports. Medians hold under load and under a stall of SDL's
    /// own thread: on 2026-10-21 the bench saw SDL's Triton events stop for
    /// 110 to 120 ms while a second reader on the same collection, in the
    /// same window, read every report about 4 ms apart (cadence ... raw).
    /// Such a stall at the end of a window moves the ratio of the two spans,
    /// which is printed beside it, by several percent.</summary>
    static unsafe void Cadence(HMContext ctx, string id, ushort vid, ushort pid, int seconds, bool assert = false)
    {
        Console.WriteLine();
        Console.WriteLine($"-- {id}: cadence, {seconds} s per rate --");
        if (Open(ctx, id, vid, pid) is not { } o) return;
        var prof = ctx.GetProfile(id)!;
        var (c, gp) = o;
        uint jid = SDL_GetGamepadID(gp);
        byte* ev = stackalloc byte[256];
        try
        {
            var st = new HMGamepadState
            {
                Axes = HMGamepadStateHelpers.StandardAxes(prof, 0.5f, 0.5f, 0.5f, 0.5f, 0.0f, 0.0f),
                GyroYaw = 4096, AccelY = 8192, GyroDpsY = 90f, AccelGY = 1f,
            };
            foreach (int hz in new[] { 1000, 250, 125 })
            {
                long period = System.Diagnostics.Stopwatch.Frequency / hz;
                int submits = 0;
                var stop = new ManualResetEventSlim();
                var submitter = new Thread(() =>
                {
                    long next = System.Diagnostics.Stopwatch.GetTimestamp();
                    while (!stop.IsSet)
                    {
                        c.SubmitState(st);
                        submits++;
                        next += period;
                        while (System.Diagnostics.Stopwatch.GetTimestamp() < next) Thread.SpinWait(20);
                    }
                }) { IsBackground = true, Priority = ThreadPriority.Highest };
                submitter.Start();
                // One second to settle, then drain what SDL holds from it.
                var settle = System.Diagnostics.Stopwatch.StartNew();
                while (settle.ElapsedMilliseconds < 1000) { while (SDL_PollEvent(ev)) { } Thread.SpinWait(200); }
                var sensorTs = new List<ulong>();
                var wallTs = new List<ulong>();
                float lastY = 0;
                var raw = s_rawReader ? RawLog.Start(vid, pid) : null;
                var window = System.Diagnostics.Stopwatch.StartNew();
                int submitsAtStart = submits;
                while (window.ElapsedMilliseconds < seconds * 1000L)
                {
                    while (SDL_PollEvent(ev))
                    {
                        if (*(uint*)ev != EV_GAMEPAD_SENSOR_UPDATE) continue;
                        if (*(uint*)(ev + 16) != jid || *(int*)(ev + 20) != SENSOR_GYRO) continue;
                        wallTs.Add(*(ulong*)(ev + 8));
                        sensorTs.Add(*(ulong*)(ev + 40));
                        lastY = *(float*)(ev + 28);
                    }
                    Thread.SpinWait(200);
                }
                int submitted = submits - submitsAtStart;
                stop.Set();
                submitter.Join();
                if (raw != null)
                {
                    // The Triton's IMU stamp, report byte 30 (TritonMTUNoQuat_t
                    // behind the report ID). The Deck and the 2015 controller
                    // carry none SDL reads.
                    raw.Stop(id == "steam-controller-2" ? 30 : -1);
                    if (wallTs.Count > 1)
                    {
                        int at = 1;
                        for (int i = 1; i < wallTs.Count; i++) if (wallTs[i] - wallTs[i - 1] > wallTs[at] - wallTs[at - 1]) at = i;
                        Console.WriteLine($"     SDL: longest gap {(wallTs[at] - wallTs[at - 1]) / 1e6:F2} ms at {(wallTs[at] - wallTs[0]) / 1e6:F0} ms");
                    }
                }
                if (sensorTs.Count < 2)
                {
                    Console.WriteLine($"  {hz,5} Hz submits: {sensorTs.Count} gyro event(s)");
                    if (assert) Check($"{id}: SDL's sensor time keeps wall time under {hz} Hz submits", false, "too few gyro events to measure");
                    continue;
                }
                double sensorSpan = (sensorTs[^1] - sensorTs[0]) / 1e9;
                double wallSpan = (wallTs[^1] - wallTs[0]) / 1e9;
                var gaps = new List<double>();
                var sensorGaps = new List<double>();
                for (int i = 1; i < wallTs.Count; i++)
                {
                    gaps.Add((wallTs[i] - wallTs[i - 1]) / 1e6);
                    sensorGaps.Add((sensorTs[i] - sensorTs[i - 1]) / 1e6);
                }
                gaps.Sort();
                sensorGaps.Sort();
                // The sensor time SDL credits a report over the wall time
                // between reports, each the median over the window.
                double perReport = sensorGaps[sensorGaps.Count / 2] / gaps[gaps.Count / 2];
                Console.WriteLine($"  {hz,5} Hz submits ({submitted} in {seconds} s): {sensorTs.Count} gyro events, "
                    + $"{(sensorTs.Count - 1) / wallSpan:F1}/s, sensor time {sensorSpan:F3} s over wall {wallSpan:F3} s = "
                    + $"{sensorSpan / wallSpan:F3}x, per report {perReport:F3}x, gap p50 {gaps[gaps.Count / 2]:F2} ms p95 {gaps[(int)(gaps.Count * 0.95)]:F2} ms max {gaps[^1]:F2} ms, "
                    + $"last gyro y {lastY:F4}");
                if (assert)
                    Check($"{id}: SDL's sensor time keeps wall time under {hz} Hz submits",
                          perReport >= 0.97 && perReport <= 1.03, $"{perReport:F3}x per report, {sensorSpan / wallSpan:F3}x over the window");
            }
        }
        finally { Close(c, gp, vid, pid); }
    }
}
