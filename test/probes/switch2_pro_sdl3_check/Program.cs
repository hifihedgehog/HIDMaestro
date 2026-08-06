// Switch 2 Pro acceptance check through REAL SDL3.
//
// The v1.5.0 release notes asserted that "DirectInput, joy.cpl, WGI and
// anything parsing the HID report descriptor see a complete 21-button
// 4-axis gamepad" without ever testing it, and an ad-hoc SDL run then
// showed the pad missing. The release was withdrawn. This probe is the
// missing test: it drives the real SDL3.dll against a live switch2-pro
// virtual and reports what SDL actually sees.
//
// Four phases, in order, each one a precondition for the next:
//
//   1 ENUMERATION. Two layers, deliberately separate, because they fail
//     for different reasons and conflating them is what produced the
//     wrong claim. The JOYSTICK layer (SDL_GetJoysticks) is reached
//     through whichever backend claims the device: HIDAPI, RAWINPUT,
//     DINPUT, WGI. The GAMEPAD layer (SDL_GetGamepads) additionally
//     requires a mapping for the device's GUID.
//   2 SHAPE. Axis / button / hat counts SDL reads out of the report
//     descriptor, which is the actual test of the "21-button 4-axis"
//     claim.
//   3 INDEX DISCOVERY. Drive one input at a time and observe which SDL
//     index moves. This derives the numbering from the running stack
//     instead of from a reading of the descriptor, so a drift in either
//     fails rather than agreeing with itself. It is also what catches
//     two inputs landing on one index.
//   4 MAPPING. Register the profile's SDL mapping and re-check the
//     gamepad layer, then read buttons and sticks back through the
//     gamepad API.
//
// Same-window positive control: a switch-pro (v1 Pro) virtual is created
// alongside and must appear. Without it, "SDL saw nothing" cannot be
// distinguished from "SDL was never running".
//
// SKIPs (exit 0) when SDL3.dll is absent or when not elevated.
// Exit 1 on FAIL.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using HIDMaestro;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    const string SDL = "SDL3";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetDllDirectoryW(string path);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryW(string path);

    [StructLayout(LayoutKind.Sequential)]
    struct SdlGuid { public ulong Lo, Hi; }

    [DllImport(SDL)] static extern bool SDL_Init(uint flags);
    [DllImport(SDL)] static extern void SDL_Quit();
    [DllImport(SDL)] static extern void SDL_PumpEvents();
    [DllImport(SDL)] static extern void SDL_free(IntPtr mem);
    [DllImport(SDL)] static extern IntPtr SDL_GetError();
    [DllImport(SDL)] static extern IntPtr SDL_GetJoysticks(out int count);
    [DllImport(SDL)] static extern IntPtr SDL_GetJoystickNameForID(uint id);
    [DllImport(SDL)] static extern IntPtr SDL_GetJoystickPathForID(uint id);
    [DllImport(SDL)] static extern ushort SDL_GetJoystickVendorForID(uint id);
    [DllImport(SDL)] static extern ushort SDL_GetJoystickProductForID(uint id);
    [DllImport(SDL)] static extern SdlGuid SDL_GetJoystickGUIDForID(uint id);
    [DllImport(SDL)] static extern IntPtr SDL_OpenJoystick(uint id);
    [DllImport(SDL)] static extern void SDL_CloseJoystick(IntPtr joystick);
    [DllImport(SDL)] static extern int SDL_GetNumJoystickAxes(IntPtr joystick);
    [DllImport(SDL)] static extern int SDL_GetNumJoystickButtons(IntPtr joystick);
    [DllImport(SDL)] static extern int SDL_GetNumJoystickHats(IntPtr joystick);
    [DllImport(SDL)] static extern short SDL_GetJoystickAxis(IntPtr joystick, int axis);
    [DllImport(SDL)] static extern bool SDL_GetJoystickButton(IntPtr joystick, int button);
    [DllImport(SDL)] static extern void SDL_UpdateJoysticks();
    [DllImport(SDL)] static extern bool SDL_IsGamepad(uint id);
    [DllImport(SDL)] static extern int SDL_AddGamepadMapping(string mapping);
    [DllImport(SDL)] static extern IntPtr SDL_OpenGamepad(uint id);
    [DllImport(SDL)] static extern void SDL_CloseGamepad(IntPtr gamepad);
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepadName(IntPtr gamepad);
    [DllImport(SDL)] static extern bool SDL_GetGamepadButton(IntPtr gamepad, int button);
    [DllImport(SDL)] static extern short SDL_GetGamepadAxis(IntPtr gamepad, int axis);
    [DllImport(SDL)] static extern void SDL_UpdateGamepads();

    const uint SDL_INIT_JOYSTICK = 0x00000200;
    const uint SDL_INIT_GAMEPAD = 0x00002000;

    // SDL_GamepadButton / SDL_GamepadAxis, SDL_gamepad.h
    const int GP_SOUTH = 0, GP_EAST = 1, GP_WEST = 2, GP_NORTH = 3;
    const int GP_DPAD_UP = 11;
    // The three Switch 2 additions. SDL's own switch2 mapping puts Share on
    // misc1, C on misc2, GR on paddle1 and GL on paddle2, and
    // map_StringForGamepadButton is index-aligned with SDL_GamepadButton.
    const int GP_MISC1 = 15, GP_RIGHT_PADDLE1 = 16, GP_LEFT_PADDLE1 = 17, GP_MISC2 = 21;
    const int GP_LEFTX = 0, GP_RIGHTY = 3;
    const int GP_LEFT_TRIGGER = 4, GP_RIGHT_TRIGGER = 5;

    static string Str(IntPtr p) => Marshal.PtrToStringUTF8(p) ?? "";

    static string GuidString(SdlGuid g)
    {
        var b = new byte[16];
        BitConverter.GetBytes(g.Lo).CopyTo(b, 0);
        BitConverter.GetBytes(g.Hi).CopyTo(b, 8);
        var sb = new StringBuilder(32);
        foreach (var x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    readonly struct Seen
    {
        public readonly uint Id;
        public readonly ushort Vid, Pid;
        public readonly string Name, Path;
        public readonly bool IsGamepad;
        public Seen(uint id, ushort vid, ushort pid, string name, string path, bool gp)
        { Id = id; Vid = vid; Pid = pid; Name = name; Path = path; IsGamepad = gp; }
    }

    static List<Seen> Enumerate()
    {
        var result = new List<Seen>();
        IntPtr list = SDL_GetJoysticks(out int n);
        if (list == IntPtr.Zero) return result;
        for (int i = 0; i < n; i++)
        {
            uint id = (uint)Marshal.ReadInt32(list, i * 4);
            result.Add(new Seen(id,
                SDL_GetJoystickVendorForID(id), SDL_GetJoystickProductForID(id),
                Str(SDL_GetJoystickNameForID(id)), Str(SDL_GetJoystickPathForID(id)),
                SDL_IsGamepad(id)));
        }
        SDL_free(list);
        return result;
    }

    // Our virtuals are identifiable by their HID interface path: every
    // HIDMaestro pad hangs off the software devnode, so the path carries
    // "hid#hidclass#". A real Nintendo pad paired to this box would sit
    // under the Bluetooth HID class instead. This matters because a real
    // Switch 2 Pro enumerates with the SAME VID/PID.
    static bool IsOurs(in Seen s) =>
        s.Path.Contains("hid#hidclass#", StringComparison.OrdinalIgnoreCase);

    // ── staged state, published by the pump thread ─────────────────────
    static readonly object s_stageLock = new();
    static HMGamepadState s_staged = Neutral();
    static HMGamepadState Neutral() => new() { Axes = new Dictionary<HMAxis, float>() };
    static void Stage(HMGamepadState s) { lock (s_stageLock) s_staged = s; }
    static void StageNeutral() => Stage(Neutral());

    // The trigger axes the SDK will actually read for this profile,
    // resolved from the profile rather than assumed, so a change to its
    // axisMap is exercised instead of bypassed.
    static HMAxis s_lt = HMAxis.Z, s_rt = HMAxis.Rz;

    // The 21 report-0x09 buttons in descriptor bit order, each with the
    // state that drives exactly that bit. Transcribed from
    // profiles/nintendo/switch2-pro.json extendedReport bytes 3-5, which
    // in turn came from VIIPER's proButtonMap. HMGamepadState is a struct,
    // so each entry BUILDS a state rather than mutating one.
    static readonly (string Label, Func<HMGamepadState> Make)[] Inputs =
    {
        /*  0 */ ("B",         () => Btn(HMButton.B)),
        /*  1 */ ("A",         () => Btn(HMButton.A)),
        /*  2 */ ("Y",         () => Btn(HMButton.Y)),
        /*  3 */ ("X",         () => Btn(HMButton.X)),
        /*  4 */ ("R",         () => Btn(HMButton.RightBumper)),
        /*  5 */ ("ZR",        () => Axis(s_rt, 1f)),
        /*  6 */ ("Plus",      () => Btn(HMButton.Start)),
        /*  7 */ ("RStick",    () => Btn(HMButton.RightStick)),
        /*  8 */ ("DpadDown",  () => Hat(HMHat.South)),
        /*  9 */ ("DpadRight", () => Hat(HMHat.East)),
        /* 10 */ ("DpadLeft",  () => Hat(HMHat.West)),
        /* 11 */ ("DpadUp",    () => Hat(HMHat.North)),
        /* 12 */ ("L",         () => Btn(HMButton.LeftBumper)),
        /* 13 */ ("ZL",        () => Axis(s_lt, 1f)),
        /* 14 */ ("Minus",     () => Btn(HMButton.Back)),
        /* 15 */ ("LStick",    () => Btn(HMButton.LeftStick)),
        /* 16 */ ("Home",      () => Btn(HMButton.Guide)),
        /* 17 */ ("Capture",   () => Btn(HMButton.Share)),
        /* 18 */ ("GR",        () => Btn(HMButton.RightPaddle)),
        /* 19 */ ("GL",        () => Btn(HMButton.LeftPaddle)),
        /* 20 */ ("C",         () => Btn(HMButton.Misc1)),
    };

    static HMGamepadState Btn(HMButton b) { var s = Neutral(); s.Buttons = b; return s; }
    static HMGamepadState Hat(HMHat h) { var s = Neutral(); s.Hat = h; return s; }
    static HMGamepadState Axis(HMAxis a, float v) { var s = Neutral(); s.Axes![a] = v; return s; }

    static int Main()
    {
        Console.WriteLine("=== Switch 2 Pro through real SDL3 ===");

        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("  [SKIP] all phases (requires elevation, same as CreateController)");
                return 0;
            }
        }

        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", ".."));
        // The fork head under build/Release carries PadForge's HIDMaestro
        // virtual-controller filter, which hides our pads from HIDAPI,
        // RawInput and DirectInput by design. Validation needs the
        // UNFILTERED root copy, which is also the faithful stand-in for
        // the stock SDL a game or Steam ships.
        string?[] candidates =
        {
            Path.Combine(repoRoot, "..", "SDL3-build"),
            Path.Combine(repoRoot, "..", "SDL3-build", "build", "Release"),
        };
        string? sdlDir = null;
        foreach (var c in candidates)
            if (c != null && File.Exists(Path.Combine(c, "SDL3.dll"))) { sdlDir = Path.GetFullPath(c); break; }
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

        var s2 = ctx.GetProfile("switch2-pro-controller");
        var s1 = ctx.GetProfile("switch-pro");
        if (s2 == null) { Console.WriteLine("  [FAIL] switch2-pro-controller profile not in catalog"); return 1; }
        if (s1 == null) { Console.WriteLine("  [FAIL] switch-pro profile not in catalog (positive control)"); return 1; }

        // Digital ZL/ZR still travel as trigger VALUES through the codec's
        // LT_DIGITAL / RT_DIGITAL sentinels, so the probe has to write the
        // same axis the encoder reads.
        s_lt = HMController.ResolveCanonicalAxis(s2.AxisMap, "lefttrigger", HMAxis.Z);
        s_rt = HMController.ResolveCanonicalAxis(s2.AxisMap, "righttrigger", HMAxis.Rz);
        Console.WriteLine($"  trigger axes: LT={s_lt} (0x{(int)s_lt:X4})  RT={s_rt} (0x{(int)s_rt:X4})");
        for (int i = 0; i < s2.Sticks.Count; i++)
            Console.WriteLine($"  stick {i} ({s2.Sticks[i].Label}): X={s2.Sticks[i].XAxis} Y={s2.Sticks[i].YAxis}");

        Check("trigger axes do not alias a stick axis",
              !AxisCollides(s2, s_lt) && !AxisCollides(s2, s_rt),
              $"LT={s_lt} RT={s_rt}");

        Console.WriteLine("  Creating switch2-pro virtual controller...");
        using var padS2 = ctx.CreateController(s2);
        Console.WriteLine("  Creating switch-pro virtual controller (positive control)...");
        using var padS1 = ctx.CreateController(s1);
        ctx.FinalizeNames();

        bool pumpStop = false;
        var pump = new Thread(() =>
        {
            var neutral = new HMGamepadState();
            while (!Volatile.Read(ref pumpStop))
            {
                HMGamepadState s;
                lock (s_stageLock) s = s_staged;
                padS2.SubmitState(s);
                padS1.SubmitState(neutral);
                Thread.Sleep(8);
            }
        })
        { IsBackground = true };
        pump.Start();

        Check("SDL_Init(JOYSTICK|GAMEPAD)", SDL_Init(SDL_INIT_JOYSTICK | SDL_INIT_GAMEPAD));

        // ── Phase 1: enumeration ──────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("  -- phase 1: enumeration --");
        var logged = new HashSet<uint>();
        List<Seen> seen = new();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 15000)
        {
            SDL_PumpEvents();
            seen = Enumerate();
            foreach (var d in seen)
                if (logged.Add(d.Id))
                    Console.WriteLine($"  [joy] id={d.Id} {d.Vid:X4}:{d.Pid:X4} gamepad={d.IsGamepad} '{d.Name}' path={d.Path}");
            if (seen.Exists(d => IsOurs(d) && d.Pid == 0x2069) &&
                seen.Exists(d => IsOurs(d) && d.Pid == 0x2009)) break;
            Thread.Sleep(150);
        }

        var s2Seen = seen.FindAll(d => IsOurs(d) && d.Vid == 0x057E && d.Pid == 0x2069);
        var s1Seen = seen.FindAll(d => IsOurs(d) && d.Vid == 0x057E && d.Pid == 0x2009);

        Check("positive control: switch-pro (057E:2009) reaches SDL's JOYSTICK layer",
              s1Seen.Count > 0, $"{s1Seen.Count} instance(s), after {sw.ElapsedMilliseconds} ms");
        Check("switch2-pro (057E:2069) reaches SDL's JOYSTICK layer",
              s2Seen.Count > 0, $"{s2Seen.Count} instance(s)");
        if (s2Seen.Count == 0) return Finish(pump, ref pumpStop);

        uint id2 = s2Seen[0].Id;
        string guid = GuidString(SDL_GetJoystickGUIDForID(id2));
        Console.WriteLine($"  switch2-pro SDL GUID = {guid}");

        // ── Phase 2: shape ────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("  -- phase 2: shape as read from the report descriptor --");
        IntPtr joy = SDL_OpenJoystick(id2);
        Check("SDL_OpenJoystick succeeds", joy != IntPtr.Zero, Str(SDL_GetError()));
        if (joy == IntPtr.Zero) return Finish(pump, ref pumpStop);

        int nAxes = SDL_GetNumJoystickAxes(joy);
        int nButtons = SDL_GetNumJoystickButtons(joy);
        int nHats = SDL_GetNumJoystickHats(joy);
        Console.WriteLine($"  axes={nAxes} buttons={nButtons} hats={nHats}");
        Check("SDL reads 21 buttons", nButtons == 21, $"got {nButtons}");
        Check("SDL reads 4 axes", nAxes == 4, $"got {nAxes}");
        Check("SDL reads 0 hats (this pad's dpad is four discrete buttons)", nHats == 0, $"got {nHats}");

        // ── Phase 3: index discovery ──────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("  -- phase 3: index discovery, one input at a time --");
        bool indexOk = true;
        for (int i = 0; i < Inputs.Length; i++)
        {
            Stage(Inputs[i].Make());
            var pressed = SettleButtons(joy, nButtons);
            StageNeutral();
            SettleReleased(joy, nButtons);

            string got = pressed.Count == 0 ? "NOTHING"
                       : "b" + string.Join("+b", pressed);
            bool ok = pressed.Count == 1 && pressed[0] == i;
            if (!ok) indexOk = false;
            Console.WriteLine($"  {(ok ? "  ok" : "DIFF")}  {Inputs[i].Label,-9} expected b{i,-2} -> {got}");
        }
        Check("every input lands on its own descriptor button, one to one",
              indexOk, "see the DIFF lines above");

        // Axes. Full deflection on one stick axis at a time; SDL reports
        // -32768..32767, so a full positive deflection reads well above
        // zero and every other axis stays near center.
        var axisNames = new[] { "leftStickX", "leftStickY", "rightStickX", "rightStickY" };
        var axisKeys = new[] { s2.Sticks[0].XAxis, s2.Sticks[0].YAxis, s2.Sticks[1].XAxis, s2.Sticks[1].YAxis };
        bool axisOk = true;
        for (int i = 0; i < axisKeys.Length; i++)
        {
            Stage(Axis(axisKeys[i], 1f));
            var moved = SettleAxes(joy, nAxes);
            StageNeutral();
            SettleAxesCentered(joy, nAxes);

            string got = moved.Count == 0 ? "NOTHING" : "a" + string.Join("+a", moved);
            bool ok = moved.Count == 1 && moved[0] == i;
            if (!ok) axisOk = false;
            Console.WriteLine($"  {(ok ? "  ok" : "DIFF")}  {axisNames[i],-11} expected a{i} -> {got}");
        }
        Check("every stick axis lands on its own SDL axis, one to one",
              axisOk, "see the DIFF lines above");

        // ── Phase 4: mapping ──────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("  -- phase 4: gamepad mapping --");
        string? body = s2.SdlMapping;
        Check("profile carries an SDL mapping", !string.IsNullOrEmpty(body));
        if (string.IsNullOrEmpty(body))
        {
            SDL_CloseJoystick(joy);
            return Finish(pump, ref pumpStop);
        }

        string mapping = $"{guid},{s2.Name},{body}";
        Console.WriteLine($"  {mapping}");
        Check("SDL_AddGamepadMapping accepts it", SDL_AddGamepadMapping(mapping) >= 0, Str(SDL_GetError()));

        SDL_PumpEvents();
        Check("switch2-pro is now promoted to SDL's GAMEPAD layer", SDL_IsGamepad(id2));

        IntPtr gp = SDL_OpenGamepad(id2);
        Check("SDL_OpenGamepad succeeds", gp != IntPtr.Zero, Str(SDL_GetError()));
        if (gp != IntPtr.Zero)
        {
            Console.WriteLine($"  gamepad name = '{Str(SDL_GetGamepadName(gp))}'");

            // Nintendo face buttons are positional in SDL: the pad's B is
            // SDL's SOUTH and its A is SDL's EAST, which is what a game
            // showing an "A to continue" prompt actually reads.
            Check("physical B reads as SDL_GAMEPAD_BUTTON_SOUTH",
                  GamepadButtonAfter(gp, Btn(HMButton.B), GP_SOUTH));
            Check("physical A reads as SDL_GAMEPAD_BUTTON_EAST",
                  GamepadButtonAfter(gp, Btn(HMButton.A), GP_EAST));
            Check("physical Y reads as SDL_GAMEPAD_BUTTON_WEST",
                  GamepadButtonAfter(gp, Btn(HMButton.Y), GP_WEST));
            Check("physical X reads as SDL_GAMEPAD_BUTTON_NORTH",
                  GamepadButtonAfter(gp, Btn(HMButton.X), GP_NORTH));
            Check("dpad up reads as SDL_GAMEPAD_BUTTON_DPAD_UP",
                  GamepadButtonAfter(gp, Hat(HMHat.North), GP_DPAD_UP));

            // The three buttons the original Switch Pro does not have.
            Check("Capture reads as SDL_GAMEPAD_BUTTON_MISC1",
                  GamepadButtonAfter(gp, Btn(HMButton.Share), GP_MISC1));
            Check("GR reads as SDL_GAMEPAD_BUTTON_RIGHT_PADDLE1",
                  GamepadButtonAfter(gp, Btn(HMButton.RightPaddle), GP_RIGHT_PADDLE1));
            Check("GL reads as SDL_GAMEPAD_BUTTON_LEFT_PADDLE1",
                  GamepadButtonAfter(gp, Btn(HMButton.LeftPaddle), GP_LEFT_PADDLE1));
            Check("C reads as SDL_GAMEPAD_BUTTON_MISC2",
                  GamepadButtonAfter(gp, Btn(HMButton.Misc1), GP_MISC2));

            short lt = GamepadAxisAfter(gp, Axis(s_lt, 1f), GP_LEFT_TRIGGER);
            Check("ZL reads on SDL_GAMEPAD_AXIS_LEFT_TRIGGER", lt > 16000, $"{lt}");
            short rt = GamepadAxisAfter(gp, Axis(s_rt, 1f), GP_RIGHT_TRIGGER);
            Check("ZR reads on SDL_GAMEPAD_AXIS_RIGHT_TRIGGER", rt > 16000, $"{rt}");
            short lx = GamepadAxisAfter(gp, Axis(s2.Sticks[0].XAxis, 1f), GP_LEFTX);
            Check("left stick full right reads on SDL_GAMEPAD_AXIS_LEFTX", lx > 16000, $"{lx}");
            short ry = GamepadAxisAfter(gp, Axis(s2.Sticks[1].YAxis, 1f), GP_RIGHTY);
            Check("right stick full down reads on SDL_GAMEPAD_AXIS_RIGHTY", ry > 16000, $"{ry}");

            SDL_CloseGamepad(gp);
        }

        SDL_CloseJoystick(joy);
        return Finish(pump, ref pumpStop);
    }

    // Publish one edit, then wait for it to travel the whole stack. 40
    // polls at 25 ms covers the SDK's 8 ms submit cadence plus HID class
    // latency with room to spare.
    const int Polls = 40, PollMs = 25;

    static List<int> SettleButtons(IntPtr joy, int nButtons)
    {
        var result = new List<int>();
        for (int poll = 0; poll < Polls; poll++)
        {
            Thread.Sleep(PollMs);
            SDL_UpdateJoysticks();
            result.Clear();
            for (int b = 0; b < nButtons; b++)
                if (SDL_GetJoystickButton(joy, b)) result.Add(b);
            if (result.Count > 0) return result;
        }
        return result;
    }

    static void SettleReleased(IntPtr joy, int nButtons)
    {
        for (int poll = 0; poll < Polls; poll++)
        {
            Thread.Sleep(PollMs);
            SDL_UpdateJoysticks();
            bool any = false;
            for (int b = 0; b < nButtons && !any; b++) any = SDL_GetJoystickButton(joy, b);
            if (!any) return;
        }
    }

    static List<int> SettleAxes(IntPtr joy, int nAxes)
    {
        var result = new List<int>();
        for (int poll = 0; poll < Polls; poll++)
        {
            Thread.Sleep(PollMs);
            SDL_UpdateJoysticks();
            result.Clear();
            for (int a = 0; a < nAxes; a++)
                if (SDL_GetJoystickAxis(joy, a) > 16000) result.Add(a);
            if (result.Count > 0) return result;
        }
        return result;
    }

    static void SettleAxesCentered(IntPtr joy, int nAxes)
    {
        for (int poll = 0; poll < Polls; poll++)
        {
            Thread.Sleep(PollMs);
            SDL_UpdateJoysticks();
            bool any = false;
            for (int a = 0; a < nAxes && !any; a++) any = SDL_GetJoystickAxis(joy, a) > 16000;
            if (!any) return;
        }
    }

    static bool GamepadButtonAfter(IntPtr gp, HMGamepadState st, int button)
    {
        Stage(st);
        bool hit = false;
        for (int poll = 0; poll < Polls && !hit; poll++)
        {
            Thread.Sleep(PollMs);
            SDL_UpdateGamepads();
            hit = SDL_GetGamepadButton(gp, button);
        }
        StageNeutral();
        return hit;
    }

    static short GamepadAxisAfter(IntPtr gp, HMGamepadState st, int axis)
    {
        Stage(st);
        short v = 0;
        for (int poll = 0; poll < Polls && v <= 16000; poll++)
        {
            Thread.Sleep(PollMs);
            SDL_UpdateGamepads();
            v = SDL_GetGamepadAxis(gp, axis);
        }
        StageNeutral();
        return v;
    }

    static bool AxisCollides(HMProfile p, HMAxis axis)
    {
        foreach (var st in p.Sticks)
            if (st.XAxis == axis || st.YAxis == axis) return true;
        return false;
    }

    static int Finish(Thread pump, ref bool pumpStop)
    {
        SDL_Quit();
        Volatile.Write(ref pumpStop, true);
        pump.Join(1000);
        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }
}
