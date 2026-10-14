// S83: SDL3 3.4.18 and the Wii U GameCube adapter persona.
//
// SDL opens the adapter only through libusb (src/hidapi/SDL_hidapi.c:855-865
// and 1142-1157), and the official SDL3 build for Windows has no libusb. The
// probe runs both, each SDL3.dll in its own harness process
// (native\build.ps1):
//
//   the official 3.4.18 release, which must list no joystick for the adapter
//   while listing a DualShock 4 persona created in the same run, with the
//   adapter on input.inf, which fails it with Code 10, and again on WinUSB.
//
//   3.4.18 built from source with libusb, which must list one joystick per
//   connected port under that port's number, map each as SDL_gamepad.c:1126
//   says, refuse rumble on a WaveBird and on a port without rumble power,
//   and send each port's rumble to that port alone.
//
// It also measures what an SDL program gets: the time from a port submit
// returning to SDL's joystick holding that state, and the reports SDL
// receives per second, at the descriptor's 8 ms poll and at an overclocked
// 1 ms.
//
// Requires elevation and the reference clones native\build.ps1 names. Exit
// 0 PASS, 1 FAIL, 2 when another program that embeds HIDMaestro is running.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HIDMaestro;

internal static class Program
{
    // SDL_GamepadButton (SDL_gamepad.h:153-183 at 3.4.18), SDL_GamepadAxis
    // (:224-233), SDL_GamepadType (SDL_GAMEPAD_TYPE_GAMECUBE) and
    // SDL_JoystickConnectionState (SDL_joystick.h:147-153).
    const int SOUTH = 0, EAST = 1, WEST = 2, NORTH = 3, START = 6, RIGHT_SHOULDER = 10,
              DPAD_UP = 11, DPAD_DOWN = 12, DPAD_LEFT = 13, DPAD_RIGHT = 14, MISC3 = 22, MISC4 = 23;
    const int LEFTX = 0, LEFTY = 1, RIGHTX = 2, RIGHTY = 3, LEFT_TRIGGER = 4, RIGHT_TRIGGER = 5;
    const int TYPE_GAMECUBE = 11, CONNECTION_WIRED = 1, CONNECTION_WIRELESS = 2;

    // The mapping SDL makes for 057E:0337, SDL_gamepad.c:1126 at 3.4.18.
    const string GameCubeMapping =
        "a:b0,b:b2,dpdown:b6,dpleft:b4,dpright:b5,dpup:b7,lefttrigger:a4,leftx:a0,lefty:a1~,rightshoulder:b9," +
        "righttrigger:a5,rightx:a2,righty:a3~,start:b8,x:b1,y:b3,misc3:b11,misc4:b10," +
        "hint:!SDL_GAMECONTROLLER_USE_GAMECUBE_LABELS:=1";

    // Each port's input: a face button and SDL's joystick button for it
    // (SDL_hidapi_gamecube.c:409-412), then the wire bytes of the main stick,
    // the C-stick, L and R. Every byte lies inside the range SDL's driver
    // assumes for a controller that has just arrived, 40 to 216
    // (ResetAxisRange, SDL_hidapi_gamecube.c:91-99), so SDL reports each
    // without widening its range.
    static readonly (HMButton Button, int JoyButton, byte Sx, byte Sy, byte Cx, byte Cy, byte L, byte R)[] Inputs =
    {
        (HMButton.A, 0, 0x50, 0x90, 0x60, 0xA0, 0x40, 0xB0),
        (HMButton.B, 1, 0x58, 0x98, 0x68, 0xA8, 0x48, 0xB8),
        (HMButton.X, 2, 0x60, 0xA0, 0x70, 0xB0, 0x50, 0xC0),
        (HMButton.Y, 3, 0x68, 0xA8, 0x78, 0xB8, 0x58, 0xC8),
    };

    static readonly ConcurrentQueue<(int Port, string Name, int Value)> s_rumble = new();
    static readonly List<string> s_measurements = new();

    static int Main(string[] args)
    {
        // "measure <runs>": the latency and rate measurements alone,
        // repeated and pooled, for the release numbers.
        if (args.Length == 2 && args[0] == "measure" && int.TryParse(args[1], out int runs))
            return Measure(runs);
        Console.WriteLine("=== S83: SDL3 3.4.18 and the GameCube adapter persona ===");
        if (Probe.OtherSdkRunning()) return 2;

        string dir = Path.Combine(AppContext.BaseDirectory, "native");
        string exe = Path.Combine(dir, "sdl_harness.exe");
        string official = Path.Combine(dir, "sdl-official"), libusb = Path.Combine(dir, "sdl-libusb");
        foreach (var f in new[] { exe, Path.Combine(official, "SDL3.dll"), Path.Combine(libusb, "SDL3.dll"), Path.Combine(libusb, "libusb-1.0.dll") })
        {
            if (!File.Exists(f))
            {
                Probe.Check("the SDL harness and both SDL builds exist", false, f);
                return Probe.Finish("S83");
            }
        }

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var ds4 = ctx.GetProfile("dualshock-4-v2");
        var gc = ctx.GetProfile("gamecube-adapter");
        if (ds4 == null || gc == null || !gc.IsDeployable)
        {
            Probe.Check("create the personas", false, "dualshock-4-v2 or gamecube-adapter is missing or not deployable");
            return Probe.Finish("S83");
        }

        // The positive control: a controller every SDL build lists.
        using (var pad = ctx.CreateController(ds4))
        using (var controller = ctx.CreateController(gc))
        {
            SetPorts(controller);
            Official(exe, official, "the adapter on input.inf with Code 10, as a real one is without Zadig");
            var bound = controller.BindWinUsb();
            Probe.Check("BindWinUsb binds WinUSB", bound == HMWinUsbBinding.Bound, $"{bound}");
            Official(exe, official, "the adapter on WinUSB, as a real one is after Zadig");
            for (int p = 0; p < 4; p++)
            {
                int port = p + 1;
                controller.Ports[p].OutputChanged += (_, e) => s_rumble.Enqueue((port, e.Name, e.Value));
            }
            Libusb(controller, exe, libusb);
        }

        Console.WriteLine("-- measurements");
        foreach (var m in s_measurements) Console.WriteLine($"  {m}");
        return Probe.Finish("S83");
    }

    static int Measure(int runs)
    {
        Console.WriteLine($"=== S83 measurements, {runs} runs ===");
        if (Probe.OtherSdkRunning()) return 2;
        string dir = Path.Combine(AppContext.BaseDirectory, "native");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        using var controller = ctx.CreateController(ctx.GetProfile("gamecube-adapter")!);
        Console.WriteLine($"bound {controller.BindWinUsb()}");
        var proto = controller.DeviceProtocol!;
        proto.HostPollInterval = proto.DescriptorPollInterval;
        SetPorts(controller);
        using var h = Harness.Start(Path.Combine(dir, "sdl_harness.exe"), Path.Combine(dir, "sdl-libusb"));
        h.Ask("hint SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS 1");
        h.Ask("init", 15_000);
        var port1 = Joysticks(h.Ask("wait 057e 0337 4 10000", 15_000)).FirstOrDefault(j => IsAdapter(j) && j.Player == 0);
        if (port1 == null || h.Ask($"open {port1.Id}") != "opened")
        {
            Console.WriteLine("SDL did not list port 1");
            return 1;
        }
        var at8 = new List<double>();
        var at1 = new List<double>();
        var medians8 = new List<double>();
        var medians1 = new List<double>();
        var rates = new Dictionary<string, List<double>>();
        for (int run = 0; run < runs; run++)
        {
            var s8 = LatencySamples(h, controller, port1.Id, TimeSpan.FromMilliseconds(8));
            var s1 = LatencySamples(h, controller, port1.Id, TimeSpan.FromMilliseconds(1));
            if (s8 == null || s1 == null) { Console.WriteLine($"run {run} lost a sample"); continue; }
            at8.AddRange(s8);
            at1.AddRange(s1);
            medians8.Add(Probe.Percentile(s8, 0.5));
            medians1.Add(Probe.Percentile(s1, 0.5));
            foreach (var (label, hz, _) in RateValues(h, controller, port1.Id))
            {
                if (!rates.TryGetValue(label, out var list)) rates[label] = list = new List<double>();
                list.Add(hz);
            }
        }
        proto.HostPollInterval = proto.DescriptorPollInterval;
        foreach (var (poll, all, medians) in new[] { (8, at8, medians8), (1, at1, medians1) })
            Console.WriteLine(FormattableString.Invariant(
                $"  SDL, {poll} ms host poll: submit to read p50 {Probe.Percentile(all, 0.5):F3} ms, p95 {Probe.Percentile(all, 0.95):F3} ms over {all.Count} samples; run medians {medians.DefaultIfEmpty().Min():F3} to {medians.DefaultIfEmpty().Max():F3} ms"));
        foreach (var (label, list) in rates)
            Console.WriteLine(FormattableString.Invariant(
                $"  SDL, {label}: reports a second p50 {Probe.Percentile(list, 0.5):F1}, p95 {Probe.Percentile(list, 0.95):F1}, range {list.Min():F1} to {list.Max():F1} over {list.Count} runs"));
        h.Ask("quit", 10_000);
        return 0;
    }

    static void SetPorts(HMController c)
    {
        for (int p = 0; p < 4; p++)
        {
            var i = Inputs[p];
            c.Ports[p].Connect(p == 2 ? "wireless" : "wired", Pad(i.Button, HMHat.None, i.Sx, i.Sy, i.Cx, i.Cy, i.L, i.R));
        }
    }

    /// <summary>A gamepad state whose port bytes are exactly these. Both
    /// stick Y fields are inverted on the wire, so the SDK value is taken
    /// from the far end.</summary>
    static HMGamepadState Pad(HMButton b, HMHat hat, byte sx, byte sy, byte cx, byte cy, byte l, byte r) => new()
    {
        Buttons = b,
        Hat = hat,
        Axes = new Dictionary<HMAxis, float>
        {
            [HMAxis.X] = sx / 255f, [HMAxis.Y] = (255 - sy) / 255f,
            [HMAxis.Rx] = cx / 255f, [HMAxis.Ry] = (255 - cy) / 255f,
            [HMAxis.Z] = l / 255f, [HMAxis.Rz] = r / 255f,
        },
    };

    // ── The official build ──────────────────────────────────────────────

    static void Official(string exe, string sdlDir, string when)
    {
        Console.WriteLine($"-- the official SDL3 3.4.18, {when}");
        using var h = Harness.Start(exe, sdlDir);
        try
        {
            h.Ask("hint SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS 1");
            string? init = h.Ask("init", 15_000);
            Probe.Check("the official SDL3.dll starts and is 3.4.18 at 829a65d7",
                init != null && init.StartsWith("ok 3004018 ", StringComparison.Ordinal) && init.Contains("829a65d7"), init ?? "no answer");
            var list = Joysticks(h.Ask("wait 054c 09cc 1 10000", 15_000));
            Probe.Check("it lists the DualShock 4 persona, the positive control", list.Any(IsDs4), Describe(list));
            // SDL notices new devices as it pumps. Two more seconds of it
            // give an adapter joystick every chance to appear.
            list = Joysticks(h.Ask("wait 057e 0337 1 2000", 5000));
            Probe.Check("it lists no joystick for the adapter", list.Count > 0 && !list.Any(IsAdapter), Describe(list));
            Probe.Check("it shuts down", h.Ask("quit", 10_000) == "bye");
        }
        finally
        {
            if (Probe.Failures > 0) Console.Write(h.TakeLog(20));
        }
    }

    // ── The libusb build ────────────────────────────────────────────────

    static void Libusb(HMController controller, string exe, string sdlDir)
    {
        Console.WriteLine("-- SDL3 3.4.18 built with libusb");
        var proto = controller.DeviceProtocol!;
        proto.HostPollInterval = proto.DescriptorPollInterval;
        SetPorts(controller);
        using var h = Harness.Start(exe, sdlDir);
        try
        {
            h.Ask("hint SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS 1");
            string? init = h.Ask("init", 15_000);
            Probe.Check("the libusb build starts and is 3.4.18 at 829a65d7",
                init != null && init.StartsWith("ok 3004018 ", StringComparison.Ordinal) && init.Contains("829a65d7"), init ?? "no answer");
            var list = Joysticks(h.Ask("wait 057e 0337 4 10000", 15_000));
            var adapter = list.Where(IsAdapter).ToList();
            Probe.Check("it lists four joysticks for the adapter, one per connected port, each a gamepad",
                adapter.Count == 4 && adapter.All(j => j.Gamepad), Describe(list));
            Probe.Check("it also lists the DualShock 4 persona", list.Any(IsDs4), Describe(list));
            if (adapter.Count != 4) return;
            Probe.Check("each is named \"Nintendo GameCube Controller\" (SDL_hidapi_gamecube.c:273)",
                adapter.All(j => j.Name == "Nintendo GameCube Controller"), Describe(adapter));
            Probe.Check("their player indexes are the port numbers (SDL_hidapi_gamecube.c:278-289)",
                adapter.Select(j => j.Player).OrderBy(x => x).SequenceEqual(new[] { 0, 1, 2, 3 }), Describe(adapter));

            var byPort = new uint[4];
            foreach (var j in adapter)
                if (j.Player is >= 0 and < 4) byPort[j.Player] = j.Id;
            for (int p = 0; p < 4; p++)
                Probe.Check($"port {p + 1}: SDL opens it as a gamepad", h.Ask($"open {byPort[p]}") == "opened");

            for (int p = 0; p < 4; p++)
            {
                bool wireless = p == 2;
                var info = Info(h.Ask($"info {byPort[p]}"));
                Probe.Check($"port {p + 1}: player {p}, {(wireless ? "wireless" : "wired")}, type GameCube, rumble {(wireless ? "not offered" : "offered")}",
                    info is { } n && n.Player == p && n.Connection == (wireless ? CONNECTION_WIRELESS : CONNECTION_WIRED)
                        && n.Type == TYPE_GAMECUBE && n.Rumble == !wireless,
                    info?.ToString() ?? "no answer");
                Probe.Check($"port {p + 1}: mapped as SDL_gamepad.c:1126 says", info?.Mapping.Contains(GameCubeMapping) == true, info?.Mapping ?? "");
                var i = Inputs[p];
                var want = new[] { Remap(i.Sx), Remap(i.Sy), Remap(i.Cx), Remap(i.Cy), Remap(i.L), Remap(i.R) };
                var st = WaitState(h, byPort[p], s => s.RawButtons == 1u << i.JoyButton && Near(s.RawAxes, want));
                Probe.Check($"port {p + 1}: its own button and every axis, exactly as SDL's driver scales them",
                    st is { } s2 && s2.RawButtons == 1u << i.JoyButton && Near(s2.RawAxes, want),
                    $"{st} expected buttons {1u << i.JoyButton:x8} axes {string.Join(" ", want)}");
            }

            Controls(h, controller, byPort[0]);
            Rumble(h, controller, byPort);
            byPort[3] = LeaveAndReturn(h, controller, byPort[3]);
            Latency(h, controller, byPort[0], TimeSpan.FromMilliseconds(8));
            Latency(h, controller, byPort[0], TimeSpan.FromMilliseconds(1));
            Rate(h, controller, byPort[0]);
            proto.HostPollInterval = proto.DescriptorPollInterval;
            Probe.Check("it shuts down", h.Ask("quit", 10_000) == "bye");
        }
        finally
        {
            File.WriteAllText(Path.Combine(sdlDir, "..", "sdl-libusb.log"), h.FullLog());
            if (Probe.Failures > 0) Console.Write(h.TakeLog(40));
        }
    }

    // ── Every control of one port ───────────────────────────────────────

    static void Controls(Harness h, HMController controller, uint id)
    {
        var port = controller.Ports[0];
        var i = Inputs[0];
        var buttons = new (string Name, HMButton Button, HMHat Hat, int Bit)[]
        {
            ("A is the south button", HMButton.A, HMHat.None, SOUTH),
            ("B is the west button", HMButton.B, HMHat.None, WEST),
            ("X is the east button", HMButton.X, HMHat.None, EAST),
            ("Y is the north button", HMButton.Y, HMHat.None, NORTH),
            ("Start", HMButton.Start, HMHat.None, START),
            ("Z is the right shoulder", HMButton.RightBumper, HMHat.None, RIGHT_SHOULDER),
            ("L's click is misc3", HMButton.LeftTriggerClick, HMHat.None, MISC3),
            ("R's click is misc4", HMButton.RightTriggerClick, HMHat.None, MISC4),
            ("D-pad up", HMButton.None, HMHat.North, DPAD_UP),
            ("D-pad down", HMButton.None, HMHat.South, DPAD_DOWN),
            ("D-pad left", HMButton.None, HMHat.West, DPAD_LEFT),
            ("D-pad right", HMButton.None, HMHat.East, DPAD_RIGHT),
        };
        var missed = new List<string>();
        foreach (var (name, button, hat, bit) in buttons)
        {
            port.SubmitState(Pad(button, hat, i.Sx, i.Sy, i.Cx, i.Cy, i.L, i.R));
            var st = WaitState(h, id, s => s.Buttons == 1u << bit);
            if (st?.Buttons != 1u << bit) missed.Add($"{name} ({st?.Buttons:x8})");
            port.SubmitState(Pad(HMButton.None, HMHat.None, i.Sx, i.Sy, i.Cx, i.Cy, i.L, i.R));
            WaitState(h, id, s => s.Buttons == 0);
        }
        Probe.Check("port 1: all twelve buttons read on their SDL gamepad buttons and no others", missed.Count == 0, string.Join(", ", missed));

        // Full travel reads as full scale, and both Y axes grow upward on the
        // wire and downward in SDL.
        var axes = new (string Name, byte Sx, byte Sy, byte Cx, byte Cy, byte L, byte R, int Axis, int Want)[]
        {
            ("main stick right", 0xFF, i.Sy, i.Cx, i.Cy, i.L, i.R, LEFTX, 32767),
            ("main stick up", i.Sx, 0xFF, i.Cx, i.Cy, i.L, i.R, LEFTY, -32768),
            ("C-stick left", i.Sx, i.Sy, 0x00, i.Cy, i.L, i.R, RIGHTX, -32768),
            ("C-stick down", i.Sx, i.Sy, i.Cx, 0x00, i.L, i.R, RIGHTY, 32767),
            ("L pressed fully", i.Sx, i.Sy, i.Cx, i.Cy, 0xFF, i.R, LEFT_TRIGGER, 32767),
            ("R pressed fully", i.Sx, i.Sy, i.Cx, i.Cy, i.L, 0xFF, RIGHT_TRIGGER, 32767),
        };
        var wrong = new List<string>();
        foreach (var (name, sx, sy, cx, cy, l, r, axis, want) in axes)
        {
            port.SubmitState(Pad(HMButton.None, HMHat.None, sx, sy, cx, cy, l, r));
            var st = WaitState(h, id, s => s.Axes[axis] == want);
            if (st?.Axes[axis] != want) wrong.Add($"{name}: {st?.Axes[axis]}");
        }
        port.SubmitState(Pad(i.Button, HMHat.None, i.Sx, i.Sy, i.Cx, i.Cy, i.L, i.R));
        Probe.Check("port 1: full travel reads full scale, the main stick and C-stick up as negative Y", wrong.Count == 0, string.Join(", ", wrong));
    }

    // ── Rumble ──────────────────────────────────────────────────────────

    static void Rumble(Harness h, HMController controller, uint[] byPort)
    {
        for (int p = 0; p < 4; p++)
        {
            int port = p + 1;
            Drain();
            string? answer = h.Ask($"rumble {byPort[p]} 65535 65535");
            if (p == 2)
            {
                Probe.Check("SDL refuses rumble on port 3's WaveBird (SDL_hidapi_gamecube.c:551-553)",
                    answer == "error Nintendo GameCube WaveBird controllers do not support rumble", answer ?? "no answer");
                Thread.Sleep(200);
                Probe.Check("and nothing reaches the adapter", s_rumble.IsEmpty && controller.Ports[2].GetOutput("rumble") == 0, Events());
                continue;
            }
            Probe.Check($"SDL accepts rumble on port {port}", answer == "ok", answer ?? "no answer");
            bool on = Probe.WaitFor(() => s_rumble.Contains((port, "rumble", 1)), 2000);
            Probe.Check($"port {port}'s rumble reaches port {port} alone",
                on && s_rumble.All(e => e.Port == port)
                && Enumerable.Range(0, 4).All(q => controller.Ports[q].GetOutput("rumble") == (q == p ? 1 : 0)), Events());
            Drain();
            h.Ask($"rumble {byPort[p]} 0 0");
            Probe.Check($"port {port}'s rumble stops", Probe.WaitFor(() => s_rumble.Contains((port, "rumble", 0)), 2000), Events());
        }

        // The rumble-power bit is the second USB cable. Without it SDL
        // refuses rumble on a wired port too (SDL_hidapi_gamecube.c:382, 554-556).
        // SDL reads the bit from each report, so the request waits for some.
        controller.Ports.Flags["rumblePower"] = false;
        Thread.Sleep(200);
        Drain();
        string? refused = h.Ask($"rumble {byPort[0]} 65535 65535");
        Probe.Check("with rumble power off, SDL refuses rumble on wired port 1: \"Second USB cable for WUP-028 not connected\"",
            refused == "error Second USB cable for WUP-028 not connected", refused ?? "no answer");
        controller.Ports.Flags["rumblePower"] = true;
        Thread.Sleep(200);
        string? accepted = h.Ask($"rumble {byPort[0]} 65535 65535");
        Probe.Check("with rumble power back, SDL accepts it and port 1's motor runs",
            accepted == "ok" && Probe.WaitFor(() => s_rumble.Contains((1, "rumble", 1)), 2000), $"{accepted} {Events()}");
        h.Ask($"rumble {byPort[0]} 0 0");
        Probe.WaitFor(() => controller.Ports[0].GetOutput("rumble") == 0, 2000);

        // SDL's rumble brake (SDL_hidapi_gamecube.c:557-564): both motors
        // off is a hard stop, 2, and the low one off alone a plain stop, 0.
        h.Ask("hint SDL_JOYSTICK_HIDAPI_GAMECUBE_RUMBLE_BRAKE 1");
        var steps = new (ushort Low, ushort High, int Want)[] { (65535, 65535, 1), (0, 0, 2), (65535, 65535, 1), (0, 65535, 0) };
        var seen = new List<string>();
        bool all = true;
        foreach (var (low, high, want) in steps)
        {
            Drain();
            string? a = h.Ask($"rumble {byPort[0]} {low} {high}");
            bool got = a == "ok" && Probe.WaitFor(() => s_rumble.Contains((1, "rumble", want)), 2000);
            all &= got;
            seen.Add($"({low},{high})->{(got ? want.ToString() : a + " " + Events())}");
        }
        h.Ask("hint SDL_JOYSTICK_HIDAPI_GAMECUBE_RUMBLE_BRAKE 0");
        Probe.Check("under SDL's rumble-brake hint, port 1 reads run 1, hard stop 2, run 1, stop 0", all, string.Join(" ", seen));
    }

    static void Drain()
    {
        while (s_rumble.TryDequeue(out _)) { }
    }

    static string Events() => string.Join(" ", s_rumble.Select(e => $"{e.Port}:{e.Name}={e.Value}"));

    // ── A port leaves and returns ───────────────────────────────────────

    /// <summary>Port 4's controller leaves and returns. Returns the new
    /// joystick SDL gives it.</summary>
    static uint LeaveAndReturn(Harness h, HMController controller, uint old)
    {
        var port4 = controller.Ports[3];
        port4.Disconnect();
        List<Joystick> list = new();
        bool gone = Probe.WaitFor(() => !(list = Joysticks(h.Ask("list"))).Any(j => j.Id == old), 2000);
        Probe.Check("SDL removes port 4's joystick when its controller leaves, and keeps the other three",
            gone && list.Count(IsAdapter) == 3 && list.Where(IsAdapter).Select(j => j.Player).OrderBy(x => x).SequenceEqual(new[] { 0, 1, 2 }), Describe(list));
        var i = Inputs[3];
        port4.Connect(null, Pad(i.Button, HMHat.None, i.Sx, i.Sy, i.Cx, i.Cy, i.L, i.R));
        list = Joysticks(h.Ask("wait 057e 0337 4 3000", 5000));
        var back = list.Where(IsAdapter).FirstOrDefault(j => j.Player == 3);
        Probe.Check("it returns as a new joystick with player index 3", back != null && back.Id != old, Describe(list));
        if (back == null) return old;
        Probe.Check("SDL opens the returning controller", h.Ask($"open {back.Id}") == "opened");
        var want = new[] { Remap(i.Sx), Remap(i.Sy), Remap(i.Cx), Remap(i.Cy), Remap(i.L), Remap(i.R) };
        var st = WaitState(h, back.Id, s => s.RawButtons == 1u << i.JoyButton && Near(s.RawAxes, want));
        Probe.Check("it reads port 4's input again, on a range SDL reset for the new controller",
            st is { } s2 && s2.RawButtons == 1u << i.JoyButton && Near(s2.RawAxes, want), $"{st}");
        return back.Id;
    }

    // ── Latency and rate ────────────────────────────────────────────────

    /// <summary>Submit to read: a new main stick X on port 1, timed from the
    /// submit returning to SDL's joystick first holding it, read in a loop
    /// of SDL_UpdateJoysticks. Forty samples at random offsets from the poll
    /// grid.</summary>
    static void Latency(Harness h, HMController controller, uint id, TimeSpan poll)
    {
        var samples = LatencySamples(h, controller, id, poll);
        double ms = poll.TotalMilliseconds;
        bool complete = samples != null;
        double p50 = complete ? Probe.Percentile(samples!, 0.5) : double.NaN;
        double p95 = complete ? Probe.Percentile(samples!, 0.95) : double.NaN;
        double max = complete ? samples!.Max() : double.NaN;
        s_measurements.Add($"SDL, {ms} ms host poll: submit to read p50 {p50:F3} ms, p95 {p95:F3} ms, max {max:F3} ms (40 samples)");
        // A submit waits at most one controller read, 1.19971 ms, and one
        // host poll. The bound adds 2 ms for the loopback, SDL's libusb read
        // thread and Windows' timer scheduling, as S82's does.
        double bound = ms + 1.19971 + 2;
        Probe.Check($"SDL at a {ms} ms poll: every submit read within {bound:F1} ms", complete && max <= bound,
            $"p50 {p50:F3}, p95 {p95:F3}, max {max:F3} ms");
    }

    /// <summary>Forty submit-to-read times in milliseconds at one host
    /// poll, or null when SDL missed one.</summary>
    static List<double>? LatencySamples(Harness h, HMController controller, uint id, TimeSpan poll)
    {
        controller.DeviceProtocol!.HostPollInterval = poll;
        var port1 = controller.Ports[0];
        var i = Inputs[0];
        var rng = new Random(83);
        var samples = new List<double>();
        byte value = 0x40;
        for (int n = 0; n < 40; n++)
        {
            Probe.SpinMicros(rng.Next(2_000, 20_000));
            value = (byte)(value == 0xF0 ? 0x40 : value + 1);
            if (h.Ask($"watch {id} {LEFTX}") != "armed") break;
            port1.SubmitState(Pad(i.Button, HMHat.None, value, i.Sy, i.Cx, i.Cy, i.L, i.R));
            long t0 = Stopwatch.GetTimestamp();
            string? seen = h.Next(3000);
            if (seen == null || !seen.StartsWith("seen ", StringComparison.Ordinal)) break;
            samples.Add((long.Parse(seen[5..]) - t0) * 1000.0 / Stopwatch.Frequency);
        }
        port1.SubmitState(Pad(i.Button, HMHat.None, i.Sx, i.Sy, i.Cx, i.Cy, i.L, i.R));
        return samples.Count == 40 ? samples : null;
    }

    /// <summary>Reports per second SDL receives, counted where the device
    /// sends them: each completes a read SDL's libusb thread queued, and SDL
    /// is the only reader. Meanwhile port 1's stick changes every 250
    /// microseconds, and the harness counts the changes SDL's joystick shows,
    /// which is how many new states reach an SDL program.</summary>
    static void Rate(Harness h, HMController controller, uint id)
    {
        var bounds = new Dictionary<string, (double Lo, double Hi)>
        {
            [RateCases[0].Label] = (120, 130),
            [RateCases[1].Label] = (790, 875),
            [RateCases[2].Label] = (950, 1010),
        };
        foreach (var (label, hz, states) in RateValues(h, controller, id))
        {
            var (lo, hi) = bounds[label];
            s_measurements.Add($"SDL, {label}: {hz:F1} reports a second, {states:F1} new states a second seen by SDL");
            Probe.Check($"SDL at {label}: {lo}-{hi} reports a second", hz >= lo && hz <= hi, $"{hz:F1}");
        }
    }

    static readonly (string Label, double Ms, int[] Connected)[] RateCases =
    {
        ("8 ms, four ports", 8, new[] { 1, 2, 3, 4 }),
        ("1 ms, four ports", 1, new[] { 1, 2, 3, 4 }),
        ("1 ms, exactly ports 1 and 2", 1, new[] { 1, 2 }),
    };

    /// <summary>One two-second count per case: the reports a second the
    /// device sent and the new states a second SDL's joystick showed. Every
    /// port is connected again after.</summary>
    static List<(string Label, double Hz, double States)> RateValues(Harness h, HMController controller, uint id)
    {
        var engine = controller.UsbipHandle!.Device.Protocol!;
        var proto = controller.DeviceProtocol!;
        var results = new List<(string, double, double)>();
        foreach (var (label, ms, connected) in RateCases)
        {
            for (int p = 0; p < 4; p++)
            {
                var i = Inputs[p];
                if (connected.Contains(p + 1))
                {
                    controller.Ports[p].Connect(p == 2 ? "wireless" : "wired", Pad(i.Button, HMHat.None, i.Sx, i.Sy, i.Cx, i.Cy, i.L, i.R));
                }
                else
                {
                    controller.Ports[p].Disconnect();
                }
            }
            proto.HostPollInterval = TimeSpan.FromMilliseconds(ms);
            Thread.Sleep(100);
            using var cts = new CancellationTokenSource();
            var feeder = Task.Run(() => Feed(controller, cts.Token));
            uint sent0 = engine.ReportsSent;
            long t0 = Stopwatch.GetTimestamp();
            string? answer = h.Ask($"count {id} {LEFTX} 2000", 5000);
            uint sent1 = engine.ReportsSent;
            double seconds = (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
            cts.Cancel();
            feeder.Wait();
            double hz = (sent1 - sent0) / seconds;
            double states = answer != null && answer.StartsWith("changes ", StringComparison.Ordinal) ? int.Parse(answer[8..]) / 2.0 : 0;
            results.Add((label, hz, states));
        }
        SetPorts(controller);
        return results;
    }

    static void Feed(HMController controller, CancellationToken ct)
    {
        var port1 = controller.Ports[0];
        var i = Inputs[0];
        long step = Stopwatch.Frequency / 4000;
        byte v = 1;
        long next = Stopwatch.GetTimestamp();
        while (!ct.IsCancellationRequested)
        {
            v = (byte)(v >= 254 ? 1 : v + 1);
            port1.SubmitState(Pad(i.Button, HMHat.None, v, i.Sy, i.Cx, i.Cy, i.L, i.R));
            next += step;
            while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(32);
        }
    }

    // ── What the harness answers ────────────────────────────────────────

    sealed record Joystick(uint Id, ushort Vid, ushort Pid, bool Gamepad, int Player, string Name)
    {
        public override string ToString() => $"{Id} {Vid:x4}:{Pid:x4} player {Player} '{Name}'";
    }

    sealed record GamepadInfo(int Player, int Connection, bool Rumble, int Type, string Mapping)
    {
        public override string ToString() => $"player {Player}, connection {Connection}, rumble {Rumble}, type {Type}";
    }

    sealed record State(uint Buttons, short[] Axes, uint RawButtons, short[] RawAxes)
    {
        public override string ToString() =>
            $"buttons {Buttons:x8} axes {string.Join(" ", Axes)} raw {RawButtons:x8} {string.Join(" ", RawAxes)}";
    }

    static bool IsAdapter(Joystick j) => j.Vid == 0x057E && j.Pid == 0x0337;

    static bool IsDs4(Joystick j) => j.Vid == 0x054C && j.Pid == 0x09CC;

    static string Describe(IEnumerable<Joystick> list) => list.Any() ? string.Join("; ", list) : "no joysticks";

    static List<Joystick> Joysticks(string? line)
    {
        var list = new List<Joystick>();
        if (line == null || !line.StartsWith("list", StringComparison.Ordinal)) return list;
        foreach (var entry in line.Split('\t').Skip(1))
        {
            var f = entry.Split(':', 6);
            list.Add(new Joystick(uint.Parse(f[0]), Convert.ToUInt16(f[1], 16), Convert.ToUInt16(f[2], 16), f[3] == "1", int.Parse(f[4]), f[5]));
        }
        return list;
    }

    static GamepadInfo? Info(string? line)
    {
        if (line == null || !line.StartsWith("info ", StringComparison.Ordinal)) return null;
        var parts = line.Split('\t', 2);
        var f = parts[0].Split(' ');
        return new GamepadInfo(int.Parse(f[1]), int.Parse(f[2]), f[3] == "1", int.Parse(f[4]), parts.Length > 1 ? parts[1] : "");
    }

    static State? ParseState(string? line)
    {
        if (line == null || !line.StartsWith("state ", StringComparison.Ordinal)) return null;
        var f = line.Split(' ');
        // state <buttons> a0..a5 raw <buttons> j0..j5
        return new State(Convert.ToUInt32(f[1], 16), f[2..8].Select(short.Parse).ToArray(),
            Convert.ToUInt32(f[9], 16), f[10..16].Select(short.Parse).ToArray());
    }

    /// <summary>The gamepad's state once <paramref name="done"/> holds, or
    /// the last one read after two seconds.</summary>
    static State? WaitState(Harness h, uint id, Func<State, bool> done)
    {
        State? st = null;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000)
        {
            st = ParseState(h.Ask($"state {id}"));
            if (st != null && done(st)) return st;
            Thread.Sleep(2);
        }
        return st;
    }

    /// <summary>A byte as SDL's GameCube driver reports it on a range of 40
    /// to 216: HIDAPI_RemapVal (SDL_hidapijoystick.c:241-244) in single
    /// precision, truncated to Sint16.</summary>
    static short Remap(byte v) => (short)(-32768f + (32767f - -32768f) * (v - 40f) / (216f - 40f));

    static bool Near(short[] got, short[] want) => got.Length == want.Length && got.Zip(want).All(p => Math.Abs(p.First - p.Second) <= 1);
}
