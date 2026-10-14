// S82: stock Dolphin and Slippi read the Wii U GameCube adapter persona
// through libusb, with BindWinUsb the only setup, as they read a real adapter
// once Zadig has run.
//
// The emulators read the adapter only while a game runs, so the probe drives
// their adapter code directly: native\build.ps1 compiles Dolphin's and
// Slippi's GCAdapter.cpp and LibusbUtils.cpp unmodified, with the libusb and
// fmt each one pins, and the harness calls GCAdapter's own Init, Input,
// Output and DeviceConnected as the emulator's serial interface does.
//
// Each emulator must find the adapter, read every port's distinct input at
// that port, send each wired port's rumble to that port alone and none to a
// WaveBird, and see a port leave and return with a new origin. The probe also
// measures what a host gets: the time from a port submit returning to the
// emulator's read holding that state, and the reports the emulator receives
// per second, at the descriptor's 8 ms poll and at an overclocked 1 ms.
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
    const ushort PAD_GET_ORIGIN = 0x2000;
    const ushort PAD_BUTTON_A = 0x0100, PAD_BUTTON_B = 0x0200, PAD_BUTTON_X = 0x0400, PAD_BUTTON_Y = 0x0800;

    // Each port's distinct input: buttons, then the wire bytes of the main
    // stick, the C-stick, L and R.
    static readonly (HMButton Button, ushort Pad, byte Sx, byte Sy, byte Cx, byte Cy, byte L, byte R)[] Inputs =
    {
        (HMButton.A, PAD_BUTTON_A, 0x11, 0xA1, 0x21, 0xB1, 0x31, 0xC1),
        (HMButton.B, PAD_BUTTON_B, 0x12, 0xA2, 0x22, 0xB2, 0x32, 0xC2),
        (HMButton.X, PAD_BUTTON_X, 0x13, 0xA3, 0x23, 0xB3, 0x33, 0xC3),
        (HMButton.Y, PAD_BUTTON_Y, 0x14, 0xA4, 0x24, 0xB4, 0x34, 0xC4),
    };

    static readonly ConcurrentQueue<(int Port, int Value)> s_rumble = new();
    static int s_libusbLogLevel;
    static readonly List<string> s_measurements = new();

    static int Main(string[] args)
    {
        // "hold <seconds>": create and bind the persona, then keep it for an
        // investigation run from another process.
        if (args.Length == 2 && args[0] == "hold" && int.TryParse(args[1], out int seconds))
            return Hold(seconds);
        if (args.Length == 2 && args[0] == "--libusb-log" && int.TryParse(args[1], out int level))
            s_libusbLogLevel = level;

        Console.WriteLine("=== S82: Dolphin and Slippi read the GameCube adapter persona ===");
        if (Probe.OtherSdkRunning()) return 2;

        string dir = Path.Combine(AppContext.BaseDirectory, "native");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var gc = ctx.GetProfile("gamecube-adapter");
        if (gc == null || !gc.IsDeployable)
        {
            Probe.Check("create the persona", false, "gamecube-adapter is not deployable");
            return Probe.Finish("S82");
        }

        using (var controller = ctx.CreateController(gc))
        {
            var bound = controller.BindWinUsb();
            Probe.Check("BindWinUsb binds WinUSB, the only setup", bound == HMWinUsbBinding.Bound, $"{bound}");
            for (int p = 0; p < 4; p++)
            {
                int port = p + 1;
                controller.Ports[p].OutputChanged += (_, e) => s_rumble.Enqueue((port, e.Value));
            }
            foreach (var flavor in new[] { "dolphin", "slippi" })
                RunEmulator(controller, Path.Combine(dir, $"gc_harness_{flavor}.exe"), flavor);
        }

        Console.WriteLine("-- measurements");
        foreach (var m in s_measurements) Console.WriteLine($"  {m}");
        return Probe.Finish("S82");
    }

    static int Hold(int seconds)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        using var controller = ctx.CreateController(ctx.GetProfile("gamecube-adapter")!);
        Console.WriteLine($"bound {controller.BindWinUsb()}");
        SetPorts(controller);
        Console.WriteLine("ready");
        var until = Stopwatch.StartNew();
        while (until.Elapsed.TotalSeconds < seconds) Thread.Sleep(250);
        return 0;
    }

    static void SetPorts(HMController c)
    {
        var ports = c.Ports;
        for (int p = 0; p < 4; p++)
        {
            var i = Inputs[p];
            ports[p].Connect(p == 2 ? "wireless" : "wired");
            ports[p].SubmitState(Pad(i.Button, i.Sx, i.Sy, i.Cx, i.Cy, i.L, i.R));
        }
    }

    /// <summary>A gamepad state whose port bytes are exactly these. Both
    /// stick Y fields are inverted on the wire, so the SDK value is taken
    /// from the far end.</summary>
    static HMGamepadState Pad(HMButton b, byte sx, byte sy, byte cx, byte cy, byte l, byte r) => new()
    {
        Buttons = b,
        Axes = new Dictionary<HMAxis, float>
        {
            [HMAxis.X] = sx / 255f, [HMAxis.Y] = (255 - sy) / 255f,
            [HMAxis.Rx] = cx / 255f, [HMAxis.Ry] = (255 - cy) / 255f,
            [HMAxis.Z] = l / 255f, [HMAxis.Rz] = r / 255f,
        },
    };

    static void RunEmulator(HMController controller, string exe, string name)
    {
        Console.WriteLine($"-- {name}");
        if (!File.Exists(exe))
        {
            Probe.Check($"{name}: the harness exists", false, exe);
            return;
        }
        var proto = controller.DeviceProtocol!;
        proto.HostPollInterval = proto.DescriptorPollInterval;
        SetPorts(controller);
        using var h = Harness.Start(exe);
        try
        {
            // --libusb-log <level> asks for libusb's own log, written to the
            // harness's log file beside it. An argument, since sudo passes
            // no environment variables.
            if (s_libusbLogLevel > 0)
                h.Ask($"loglevel {s_libusbLogLevel}");
            string? init = h.Ask("init", 30_000);
            Probe.Check($"{name} finds and opens the adapter through libusb", init == "detected", init ?? "no answer");
            if (init != "detected") return;

            // Every port's input at its own channel. The first read after a
            // controller arrives is its origin, flagged PAD_GET_ORIGIN.
            for (int c = 0; c < 4; c++)
            {
                Probe.Check($"{name} port {c + 1}: connected", PollAnswer(h, $"connected {c}", "connected 1"));
                var first = Pad(h.Ask($"input {c}"));
                var pad = NextStatus(h, c);
                var i = Inputs[c];
                Probe.Check($"{name} port {c + 1}: the first read is the origin", first != null && (first.Value.Button & PAD_GET_ORIGIN) != 0,
                    first?.ToString() ?? "no answer");
                Probe.Check($"{name} port {c + 1}: its own buttons, sticks and triggers",
                    pad == (i.Pad, i.Sx, i.Sy, i.Cx, i.Cy, i.L, i.R), $"{pad} expected {(i.Pad, i.Sx, i.Sy, i.Cx, i.Cy, i.L, i.R)}");
            }

            Rumble(h, controller, name);
            LeaveAndReturn(h, controller, name);
            Latency(h, controller, name, TimeSpan.FromMilliseconds(8));
            Latency(h, controller, name, TimeSpan.FromMilliseconds(1));
            Rate(h, controller, name);
            proto.HostPollInterval = proto.DescriptorPollInterval;
            Probe.Check($"{name} shuts down", h.Ask("quit", 10_000) == "bye");
        }
        finally
        {
            // The emulator's whole log beside the harness, and its tail here
            // when anything failed.
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe)!, $"{name}.log"), h.FullLog());
            if (Probe.Failures > 0) Console.Write(h.TakeLog(40));
        }
    }

    // ── Rumble ──────────────────────────────────────────────────────────

    static void Rumble(Harness h, HMController controller, string name)
    {
        for (int c = 0; c < 4; c++)
        {
            while (s_rumble.TryDequeue(out _)) { }
            h.Ask($"output {c} 1");
            bool wireless = c == 2;
            if (wireless)
            {
                Thread.Sleep(300);
                Probe.Check($"{name} sends no rumble to port 3's WaveBird", s_rumble.IsEmpty && controller.Ports[2].GetOutput("rumble") == 0,
                    string.Join(" ", s_rumble.Select(r => $"{r.Port}:{r.Value}")));
                continue;
            }
            bool on = Probe.WaitFor(() => s_rumble.Contains((c + 1, 1)), 2000);
            Probe.Check($"{name} port {c + 1}'s rumble reaches port {c + 1} alone",
                on && s_rumble.All(r => r.Port == c + 1)
                && Enumerable.Range(0, 4).All(p => controller.Ports[p].GetOutput("rumble") == (p == c ? 1 : 0)),
                string.Join(" ", s_rumble.Select(r => $"{r.Port}:{r.Value}")));
            while (s_rumble.TryDequeue(out _)) { }
            h.Ask($"output {c} 0");
            Probe.Check($"{name} port {c + 1}'s rumble stops", Probe.WaitFor(() => s_rumble.Contains((c + 1, 0)), 2000));
        }
    }

    // ── A port leaves and returns ───────────────────────────────────────

    static void LeaveAndReturn(Harness h, HMController controller, string name)
    {
        var port4 = controller.Ports[3];
        port4.Disconnect();
        Probe.Check($"{name} sees port 4 leave", PollAnswer(h, "connected 3", "connected 0"));
        Probe.Check($"{name} keeps port 1 meanwhile", Pad(h.Ask("input 0"))?.Sx == Inputs[0].Sx);
        var i = Inputs[3];
        port4.Connect();
        port4.SubmitState(Pad(i.Button, i.Sx, i.Sy, i.Cx, i.Cy, i.L, i.R));
        Probe.Check($"{name} sees port 4 return", PollAnswer(h, "connected 3", "connected 1"));
        var first = Pad(h.Ask("input 3"));
        Probe.Check($"{name} reads a new origin for the returning controller",
            first != null && (first.Value.Button & PAD_GET_ORIGIN) != 0, first?.ToString() ?? "no answer");
        var pad = NextStatus(h, 3);
        Probe.Check($"{name} reads port 4's input again", pad == (i.Pad, i.Sx, i.Sy, i.Cx, i.Cy, i.L, i.R), $"{pad}");
    }

    /// <summary>The port's status once a report after the connecting one
    /// has arrived. ProcessInputPayload stores the connecting report's state,
    /// origin flag included, as the port's status too, so the flag reads
    /// until the next report replaces it.</summary>
    static (ushort Button, byte Sx, byte Sy, byte Cx, byte Cy, byte L, byte R)? NextStatus(Harness h, int chan)
    {
        (ushort Button, byte Sx, byte Sy, byte Cx, byte Cy, byte L, byte R)? pad = null;
        Probe.WaitFor(() => (pad = Pad(h.Ask($"input {chan}"))) is { } p && (p.Button & PAD_GET_ORIGIN) == 0, 1000);
        return pad;
    }

    static bool PollAnswer(Harness h, string command, string want)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000)
        {
            if (h.Ask(command) == want) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    // ── Latency and rate ────────────────────────────────────────────────

    /// <summary>Submit to read: a new main stick X on port 1, timed from the
    /// submit returning to the emulator's Input first returning it. Forty
    /// samples at random offsets from the poll grid.</summary>
    static void Latency(Harness h, HMController controller, string name, TimeSpan poll)
    {
        controller.DeviceProtocol!.HostPollInterval = poll;
        var port1 = controller.Ports[0];
        var i = Inputs[0];
        var rng = new Random(82);
        var samples = new List<double>();
        byte value = 0x40;
        for (int n = 0; n < 40; n++)
        {
            Probe.SpinMicros(rng.Next(2_000, 20_000));
            value = (byte)(value == 0xF0 ? 0x40 : value + 1);
            if (h.Ask($"watch 0 {value}") != "armed") break;
            port1.SubmitState(Pad(i.Button, value, i.Sy, i.Cx, i.Cy, i.L, i.R));
            long t0 = Stopwatch.GetTimestamp();
            string? seen = h.Next(3000);
            if (seen == null || !seen.StartsWith("seen ", StringComparison.Ordinal)) break;
            samples.Add((long.Parse(seen[5..]) - t0) * 1000.0 / Stopwatch.Frequency);
        }
        port1.SubmitState(Pad(i.Button, i.Sx, i.Sy, i.Cx, i.Cy, i.L, i.R));
        double ms = poll.TotalMilliseconds;
        bool complete = samples.Count == 40;
        samples.Sort();
        double p50 = complete ? samples[19] : double.NaN, p95 = complete ? samples[37] : double.NaN;
        s_measurements.Add($"{name}, {ms} ms host poll: submit to read p50 {p50:F3} ms, p95 {p95:F3} ms, max {(complete ? samples[^1] : double.NaN):F3} ms (40 samples)");
        // A submit waits at most one controller read, 1.19971 ms, and one
        // host poll. The bound adds 2 ms for the loopback, the emulator's
        // read loop and Windows' timer scheduling.
        double bound = ms + 1.19971 + 2;
        Probe.Check($"{name} at a {ms} ms poll: every submit read within {bound:F1} ms",
            complete && samples[^1] <= bound, $"p50 {p50:F3}, p95 {p95:F3}, max {(complete ? samples[^1] : double.NaN):F3} ms");
    }

    /// <summary>Reports per second the emulator receives, counted where the
    /// device sends them: each one completes a read the emulator's libusb
    /// queued, and the emulator is the only reader. Meanwhile port 1's stick
    /// changes every 250 microseconds, and the harness counts the changes its
    /// Input sees, which is how many new states reach the emulator. Dolphin's
    /// own reads-per-second figure is printed beside them.</summary>
    static void Rate(Harness h, HMController controller, string name)
    {
        var engine = controller.UsbipHandle!.Device.Protocol!;
        var proto = controller.DeviceProtocol!;
        var cases = new (string Label, double Ms, int[] Connected, double Lo, double Hi)[]
        {
            ("8 ms, four ports", 8, new[] { 1, 2, 3, 4 }, 120, 130),
            ("1 ms, four ports", 1, new[] { 1, 2, 3, 4 }, 790, 875),
            ("1 ms, exactly ports 1 and 2", 1, new[] { 1, 2 }, 950, 1010),
        };
        foreach (var (label, ms, connected, lo, hi) in cases)
        {
            for (int p = 0; p < 4; p++)
            {
                var i = Inputs[p];
                if (connected.Contains(p + 1))
                {
                    controller.Ports[p].Connect(p == 2 ? "wireless" : "wired");
                    controller.Ports[p].SubmitState(Pad(i.Button, i.Sx, i.Sy, i.Cx, i.Cy, i.L, i.R));
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
            string? answer = h.Ask("count 0 2000", 5000);
            uint sent1 = engine.ReportsSent;
            double seconds = (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
            cts.Cancel();
            feeder.Wait();
            double hz = (sent1 - sent0) / seconds;
            double states = answer != null && answer.StartsWith("changes ", StringComparison.Ordinal) ? int.Parse(answer[8..]) / 2.0 : 0;
            string own = h.Ask("rate") ?? "";
            s_measurements.Add($"{name}, {label}: {hz:F1} reports a second, {states:F1} new states a second seen by Input ({own})");
            Probe.Check($"{name} at {label}: {lo}-{hi} reports a second", hz >= lo && hz <= hi, $"{hz:F1}");
        }
        for (int p = 0; p < 4; p++)
        {
            var i = Inputs[p];
            controller.Ports[p].Connect(p == 2 ? "wireless" : "wired");
            controller.Ports[p].SubmitState(Pad(i.Button, i.Sx, i.Sy, i.Cx, i.Cy, i.L, i.R));
        }
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
            port1.SubmitState(Pad(i.Button, v, i.Sy, i.Cx, i.Cy, i.L, i.R));
            next += step;
            while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(32);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    static (ushort Button, byte Sx, byte Sy, byte Cx, byte Cy, byte L, byte R)? Pad(string? line)
    {
        if (line == null || !line.StartsWith("pad ", StringComparison.Ordinal)) return null;
        var f = line[4..].Split(' ').Select(int.Parse).ToArray();
        return ((ushort)f[0], (byte)f[1], (byte)f[2], (byte)f[3], (byte)f[4], (byte)f[5], (byte)f[6]);
    }
}
