// S80: the Wii U GameCube adapter persona behaves as the real adapter, read
// through WinUSB the way libusb hosts read it.
//
// Before 0x13 the IN endpoint answers nothing, SET_PROTOCOL succeeds and 0x12
// replies with zero origins. 0x13 starts report 0x21, a report per 8 ms host
// poll. Each port keeps its own connection, input and rumble: a submit,
// connect or disconnect on one port leaves the others' bytes as they were,
// a controller plugged into an empty port reports itself at rest, one
// plugged in with a state reports that state from its first report, and
// report 0x11 raises each port's own rumble event, behind handlers that
// throw too. 0x12 replies with each
// port's origin, 0x14 replies and stops, a short 0x11, 0x16 and 0x17 stall the
// reports until the next 0x13, other command bytes are ignored, SET_REPORT
// works as the OUT endpoint does, and 0x15 stops the adapter answering until
// a port reset. A transfer with no room for the next packet babbles.
// GET_STATUS answers the configuration's self-powered bit. At a 1 ms host
// poll the adapter answers only when it has read its controllers since its
// last report: every 1.19971 ms, or every 0.9995 ms with controllers in
// exactly ports 1 and 2. docs/design/v1.13.0.md has the sources.
//
// Everything the persona does is the profile's data run by the SDK's generic
// port and protocol model, so this probe also tests that model.
//
// "--offline" runs the in-process engine and model checks alone, which create
// no device and need no elevation. Otherwise requires elevation. Exit 0 PASS,
// 1 FAIL, 2 when another program that embeds HIDMaestro is running.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HIDMaestro;

internal static class Program
{
    const byte In = 0x81, Out = 0x02;
    const int ERROR_SEM_TIMEOUT = 121;

    static WinUsbDevice s_usb = null!;
    static int s_lastError;

    // Every port output change, in order: (port, name, value).
    static readonly ConcurrentQueue<(int Port, string Name, int Value)> s_outputs = new();
    static readonly ConcurrentQueue<IReadOnlyDictionary<string, object>> s_decoded = new();

    static int Main(string[] args)
    {
        Console.WriteLine("=== S80: the GameCube adapter persona's protocol ===");
        if (Probe.OtherSdkRunning()) return 2;

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var gc = ctx.GetProfile("gamecube-adapter");
        if (gc == null || !gc.IsDeployable)
        {
            Probe.Check("create the persona", false, "gamecube-adapter is not deployable");
            return Probe.Finish("S80");
        }
        if (args.Contains("--offline"))
        {
            EngineChecks(ctx, gc);
            return Probe.Finish("S80");
        }

        var before = Devnodes.PresentAdapters();
        var controller = ctx.CreateController(gc);
        try
        {
            if (!Open(controller, before)) return Probe.Finish("S80");
            var ports = controller.Ports;
            var proto = controller.DeviceProtocol!;
            for (int p = 0; p < ports.Count; p++)
            {
                int number = p + 1;
                ports[p].OutputChanged += (_, e) => s_outputs.Enqueue((number, e.Name, e.Value));
            }
            controller.OutputDecoded += (_, e) => s_decoded.Enqueue(e.Fields);

            Console.WriteLine("-- the model");
            Probe.Check("four ports", ports.Count == 4, $"{ports.Count}");
            Probe.Check("connections empty, wired, wireless, wirelessNotCommunicating",
                ports.Connections.SequenceEqual(new[] { "empty", "wired", "wireless", "wirelessNotCommunicating" }),
                string.Join(", ", ports.Connections));
            Probe.Check("one flag, rumblePower, on", ports.Flags.Names.SequenceEqual(new[] { "rumblePower" }) && ports.Flags["rumblePower"]);
            Probe.Check("one output per port, rumble", ports.OutputNames.SequenceEqual(new[] { "rumble" }));
            Probe.Check("states polling, stalled, halted",
                proto.StateNames.SequenceEqual(new[] { "polling", "stalled", "halted" }), string.Join(", ", proto.StateNames));
            Probe.Check("the host polls every 8 ms, as the descriptors say",
                proto.DescriptorPollInterval == TimeSpan.FromMilliseconds(8) && proto.HostPollInterval == TimeSpan.FromMilliseconds(8),
                $"{proto.DescriptorPollInterval.TotalMilliseconds} / {proto.HostPollInterval.TotalMilliseconds} ms");

            BeforeStart(proto);
            Start(proto);
            PortsAndInput(controller);
            Rumble(controller, proto);
            ThrowingHandlers(controller);
            StopAndStall(proto);
            ControlPath(controller, proto);
            EngineChecks(ctx, gc);
            Clock(controller, proto);
            HaltAndReset(controller, proto);
        }
        finally
        {
            s_usb?.Dispose();
            controller.Dispose();
        }

        Recreate(ctx, gc, before);
        IdleFrames(ctx, before);
        LatencyHookDisposes(ctx, gc, before);
        return Probe.Finish("S80");
    }

    static bool Open(HMController controller, List<string> before)
    {
        string? id = null;
        Probe.WaitFor(() => (id = Devnodes.PresentAdapters().Except(before, StringComparer.OrdinalIgnoreCase).FirstOrDefault()) != null, 10_000);
        Probe.Check("the adapter arrives", id != null, id ?? "");
        if (id == null) return false;
        var bound = controller.BindWinUsb();
        Probe.Check("BindWinUsb binds WinUSB", bound == HMWinUsbBinding.Bound, $"{bound}");
        WinUsbDevice? usb = null;
        Probe.WaitFor(() => (usb = WinUsbDevice.Open(id)) != null, 10_000);
        Probe.Check("WinUSB opens it", usb != null);
        if (usb == null) return false;
        s_usb = usb;
        return true;
    }

    // ── Before 0x13 ─────────────────────────────────────────────────────

    static void BeforeStart(HMDeviceProtocol proto)
    {
        Console.WriteLine("-- before 0x13");
        Probe.Check("no report before 0x13", Read(150) == null && s_lastError == ERROR_SEM_TIMEOUT, $"error {s_lastError}");
        Probe.Check("not polling", !proto.GetState("polling"));
        var sp = s_usb.Control(0x21, 0x0B, 0x0001, 0, 0, Array.Empty<byte>(), out int spErr);
        Probe.Check("SET_PROTOCOL succeeds", sp != null, $"error {spErr}");
        CheckStatus("GET_STATUS: self-powered, as bmAttributes 0xE0 declares", "0100");
        // UdeCx completes SET_FEATURE(DEVICE_REMOTE_WAKEUP) itself and never
        // forwards it to usbip-win2 (Microsoft Learn, "Write a UDE client
        // driver"), so the device never sees the host arm it.
        var set = s_usb.Control(0x00, 0x03, 0x0001, 0, 0, Array.Empty<byte>(), out int setErr);
        Probe.Check("SET_FEATURE(DEVICE_REMOTE_WAKEUP) succeeds", set != null, $"error {setErr}");
        CheckStatus("GET_STATUS: remote wakeup still clear, since UdeCx keeps the request", "0100");
        Send(0x12);
        var origins = Read(150);
        Probe.Check("0x12 replies 0x22 and zero origins while not polling",
            origins != null && origins.Length == 25 && origins[0] == 0x22 && origins.Skip(1).All(b => b == 0), Show(origins));
    }

    static void CheckStatus(string name, string expected)
    {
        var s = s_usb.Control(0x80, 0x00, 0, 0, 2, null, out int err);
        Probe.Check(name, s != null && Probe.Hex(s) == expected, s == null ? $"error {err}" : Probe.Hex(s));
    }

    // ── 0x13 and the ports ──────────────────────────────────────────────

    static void Start(HMDeviceProtocol proto)
    {
        Console.WriteLine("-- 0x13");
        Send(0x13);
        Probe.Check("polling", proto.GetState("polling"));
        var r = Read(150);
        Probe.Check("report 0x21 of 37 bytes", r != null && r.Length == 37 && r[0] == 0x21, Show(r));
        Probe.Check("four empty ports showing rumble power, data zero",
            r != null && Enumerable.Range(0, 4).All(p => Block(r, p).SequenceEqual(new byte[] { 0x04, 0, 0, 0, 0, 0, 0, 0, 0 })), Show(r));
    }

    static void PortsAndInput(HMController controller)
    {
        Console.WriteLine("-- ports");
        var ports = controller.Ports;
        var state = new HMGamepadState
        {
            Buttons = HMButton.A,
            Axes = new Dictionary<HMAxis, float>
            {
                [HMAxis.X] = 1f, [HMAxis.Y] = 0.5f, [HMAxis.Rx] = 0.5f, [HMAxis.Ry] = 0.5f,
                [HMAxis.Z] = 0.5f, [HMAxis.Rz] = 0f,
            },
        };
        ports[0].SubmitState(state);
        var r = ReadFresh();
        Probe.Check("port 1 from HMGamepadState: wired, A, stick right, L half",
            r != null && Probe.Hex(Block(r, 0)) == "1401" + "00" + "FF80" + "8080" + "8000", Show(r));
        Probe.Check("ports 2 to 4 untouched", r != null && Enumerable.Range(1, 3).All(p => Probe.Hex(Block(r, p)) == "040000000000000000"));

        // The origin a controller reports is its first report's analog bytes.
        Send(0x12);
        var origins = Read(150);
        Probe.Check("0x12 replies port 1's origin, the analog bytes of its first report",
            origins != null && origins.Length == 25 && Probe.Hex(origins) == "22" + "FF8080808000" + new string('0', 36), Show(origins));

        ports[1].Connect("wireless");
        r = ReadFresh();
        Probe.Check("port 2 connects as a WaveBird at rest: 0x22 with rumble power, sticks centered, nothing held",
            r != null && Probe.Hex(Block(r, 1)) == "260000808080800000", Show(r));
        Probe.Check("port 1 untouched", r != null && Probe.Hex(Block(r, 0)) == "140100FF8080808000");

        ports[2].SubmitRaw(new byte[] { 0x80, 0x0B, 0x10, 0xF0, 0x20, 0xE0, 0x30, 0x40 });
        r = ReadFresh();
        Probe.Check("port 3 takes raw bytes and connects wired", r != null && Probe.Hex(Block(r, 2)) == "14800B10F020E03040", Show(r));
        Probe.Check("port 3 reads as wired", ports[2].Connection == "wired" && ports[2].IsConnected, ports[2].Connection);
        Probe.Check("ports 1 and 2 untouched", r != null && Probe.Hex(Block(r, 0)) == "140100FF8080808000" && Probe.Hex(Block(r, 1)) == "260000808080800000");

        ports[0].Disconnect();
        r = ReadFresh();
        Probe.Check("port 1 disconnects: status 0x04, data zero", r != null && Probe.Hex(Block(r, 0)) == "040000000000000000", Show(r));
        Probe.Check("ports 2 and 3 untouched by the disconnect",
            r != null && Probe.Hex(Block(r, 1)) == "260000808080800000" && Probe.Hex(Block(r, 2)) == "14800B10F020E03040");
        ports[0].Connect();
        r = ReadFresh();
        Probe.Check("port 1 returns wired at rest: sticks centered, nothing held", r != null && Probe.Hex(Block(r, 0)) == "140000808080800000", Show(r));

        var clicks = new HMGamepadState
        {
            Buttons = HMButton.Start | HMButton.RightBumper | HMButton.LeftTriggerClick | HMButton.RightTriggerClick,
            Hat = HMHat.North,
            Axes = new Dictionary<HMAxis, float>
            {
                [HMAxis.X] = 0.5f, [HMAxis.Y] = 0f, [HMAxis.Rx] = 0.5f, [HMAxis.Ry] = 1f,
                [HMAxis.Z] = 1f, [HMAxis.Rz] = 1f,
            },
        };
        ports[0].SubmitState(clicks);
        r = ReadFresh();
        Probe.Check("D-pad up, Start, Z, L and R clicks; stick up reads 255, C-stick down 0, L and R 255",
            r != null && Probe.Hex(Block(r, 0)) == "1480" + "0F" + "80FF" + "8000" + "FFFF", Show(r));

        controller.SubmitState(new HMGamepadState { Buttons = HMButton.B, Axes = Centered() });
        r = ReadFresh();
        Probe.Check("SubmitState on the controller is port 1", r != null && Probe.Hex(Block(r, 0)) == "140200808080800000", Show(r));

        ports.Flags["rumblePower"] = false;
        r = ReadFresh();
        Probe.Check("rumble power off clears 0x04 on every port",
            r != null && r[1] == 0x10 && r[10] == 0x22 && r[19] == 0x10 && r[28] == 0x00, Show(r));
        ports.Flags["rumblePower"] = true;
        r = ReadFresh();
        Probe.Check("and back on", r != null && r[1] == 0x14 && r[10] == 0x26 && r[19] == 0x14 && r[28] == 0x04, Show(r));

        // With a state, the connection and its input go out in one report:
        // no report shows the controller at rest first.
        ports[3].Connect("wireless", new HMGamepadState { Buttons = HMButton.A, Axes = Centered() });
        var seen = new List<string>();
        for (int k = 0; k < 6; k++)
            if (Read(150) is { } rr) seen.Add(Probe.Hex(Block(rr, 3)));
        Probe.Check("port 4 connects as a WaveBird holding A, in the same report",
            seen.Count > 0 && seen[^1] == "260100808080800000"
            && seen.Where(b => b != "040000000000000000").All(b => b == "260100808080800000"),
            string.Join(" ", seen));
        bool refused;
        try { ports[3].Connect("empty", default); refused = false; }
        catch (ArgumentException) { refused = true; }
        Probe.Check("a state with the empty connection is refused", refused);
        ports[3].Disconnect();
        r = ReadFresh();
        Probe.Check("port 4 leaves again", r != null && Probe.Hex(Block(r, 3)) == "040000000000000000", Show(r));
    }

    // ── Rumble ──────────────────────────────────────────────────────────

    static void Rumble(HMController controller, HMDeviceProtocol proto)
    {
        Console.WriteLine("-- rumble");
        var ports = controller.Ports;
        while (s_outputs.TryDequeue(out _)) { }
        while (s_decoded.TryDequeue(out _)) { }
        Send(0x11, 0x01, 0x00, 0x01, 0x00);
        Probe.WaitFor(() => s_outputs.Count >= 2, 2000);
        var got = s_outputs.ToArray();
        Probe.Check("0x11 raises rumble 1 on ports 1 and 3 and nothing on 2 and 4",
            got.Length == 2 && got.Contains((1, "rumble", 1)) && got.Contains((3, "rumble", 1)),
            string.Join(" ", got.Select(o => $"{o.Port}:{o.Value}")));
        Probe.Check("GetOutput reads each port's command",
            ports[0].GetOutput("rumble") == 1 && ports[1].GetOutput("rumble") == 0
            && ports[2].GetOutput("rumble") == 1 && ports[3].GetOutput("rumble") == 0);
        Probe.WaitFor(() => !s_decoded.IsEmpty, 2000);
        Probe.Check("OutputDecoded carries port1.rumble to port4.rumble",
            s_decoded.TryPeek(out var fields) && fields.TryGetValue("port1.rumble", out var v1) && Equals(v1, 1)
            && fields.TryGetValue("port4.rumble", out var v4) && Equals(v4, 0));
        Probe.Check("reports go on", Read(150)?.Length == 37);

        while (s_outputs.TryDequeue(out _)) { }
        Send(0x11, 0x02, 0x00, 0x00, 0x00);
        Probe.WaitFor(() => s_outputs.Count >= 2, 2000);
        got = s_outputs.ToArray();
        Probe.Check("0x11 02 00 00 00: port 1 stops hard, port 3 stops",
            got.Contains((1, "rumble", 2)) && got.Contains((3, "rumble", 0)) && got.Length == 2,
            string.Join(" ", got.Select(o => $"{o.Port}:{o.Value}")));

        while (s_outputs.TryDequeue(out _)) { }
        Send(0x11, 0x01);
        Probe.Check("a short 0x11 stalls the reports", Read(150) == null && s_lastError == ERROR_SEM_TIMEOUT && proto.GetState("stalled"),
            $"error {s_lastError}");
        Thread.Sleep(50);
        Probe.Check("and sets no motor", s_outputs.IsEmpty && ports[0].GetOutput("rumble") == 2);
        Send(0x13);
        Probe.Check("0x13 resumes them", Read(150)?.Length == 37 && !proto.GetState("stalled"));
    }

    // ── Handlers that throw ─────────────────────────────────────────────

    /// <summary>A throwing OutputReceived handler, and a throwing
    /// OutputChanged and OutputDecoded handler ahead of a counting one. The
    /// SDK drops each exception: port 1's rumble still changes, and the
    /// handlers after the throwing ones still see it.</summary>
    static void ThrowingHandlers(HMController controller)
    {
        Console.WriteLine("-- handlers that throw");
        var port1 = controller.Ports[0];
        var changes = new ConcurrentQueue<(string Name, int Value)>();
        var decoded = new ConcurrentQueue<object>();
        Action<HMController, HMOutputPacket> throwOnOutput = (_, _) => throw new InvalidOperationException("S80: a throwing OutputReceived handler");
        EventHandler<HMPortOutputEventArgs> throwOnChange = (_, _) => throw new InvalidOperationException("S80: a throwing OutputChanged handler");
        EventHandler<HMOutputDecodedEventArgs> throwOnDecoded = (_, _) => throw new InvalidOperationException("S80: a throwing OutputDecoded handler");
        EventHandler<HMPortOutputEventArgs> countChange = (_, e) => changes.Enqueue((e.Name, e.Value));
        EventHandler<HMOutputDecodedEventArgs> countDecoded = (_, e) =>
        {
            if (e.Fields.TryGetValue("port1.rumble", out var v)) decoded.Enqueue(v);
        };
        controller.OutputReceived += throwOnOutput;
        port1.OutputChanged += throwOnChange;
        port1.OutputChanged += countChange;
        controller.OutputDecoded += throwOnDecoded;
        controller.OutputDecoded += countDecoded;
        try
        {
            Send(0x11, 0x01, 0x00, 0x00, 0x00);
            bool on = Probe.WaitFor(() => changes.Contains(("rumble", 1)), 2000);
            Send(0x11, 0x00, 0x00, 0x00, 0x00);
            bool off = Probe.WaitFor(() => changes.Contains(("rumble", 0)), 2000);
            Probe.Check("behind throwing handlers, port 1's rumble goes to 1 and back to 0, and OutputChanged's next handler sees both",
                on && off && port1.GetOutput("rumble") == 0, string.Join(" ", changes.Select(c => $"{c.Name}={c.Value}")));
            Probe.Check("OutputDecoded's next handler sees both reports",
                Probe.WaitFor(() => decoded.Count >= 2, 2000) && decoded.Select(Convert.ToInt32).SequenceEqual(new[] { 1, 0 }),
                string.Join(" ", decoded));
        }
        finally
        {
            controller.OutputReceived -= throwOnOutput;
            port1.OutputChanged -= throwOnChange;
            port1.OutputChanged -= countChange;
            controller.OutputDecoded -= throwOnDecoded;
            controller.OutputDecoded -= countDecoded;
        }
    }

    // ── 0x14, stalls, ignored bytes ─────────────────────────────────────

    static void StopAndStall(HMDeviceProtocol proto)
    {
        Console.WriteLine("-- 0x14, stalls and other bytes");
        Send(0x14);
        var r = Read(150);
        Probe.Check("0x14 replies 0x24 0x01 while polling", r != null && Probe.Hex(r) == "2401", Show(r));
        Probe.Check("then no reports", Read(150) == null && s_lastError == ERROR_SEM_TIMEOUT && !proto.GetState("polling"));
        Send(0x14);
        r = Read(150);
        Probe.Check("0x14 again replies 0x24 0x00", r != null && Probe.Hex(r) == "2400", Show(r));
        Send(0x13);
        Probe.Check("0x13 starts them again", Read(150)?.Length == 37);

        foreach (byte cmd in new byte[] { 0x16, 0x17 })
        {
            Send(cmd);
            Probe.Check($"0x{cmd:X2} stalls the reports", Read(150) == null && s_lastError == ERROR_SEM_TIMEOUT && proto.GetState("stalled"));
            Send(0x13);
            Probe.Check($"0x13 resumes them after 0x{cmd:X2}", Read(150)?.Length == 37);
        }
        foreach (byte cmd in new byte[] { 0x10, 0x18, 0x21, 0xFF })
        {
            Send(cmd);
            Probe.Check($"0x{cmd:X2} is ignored", Read(150)?.Length == 37 && proto.GetState("polling") && !proto.GetState("stalled"));
        }
    }

    // ── The control path ────────────────────────────────────────────────

    static void ControlPath(HMController controller, HMDeviceProtocol proto)
    {
        Console.WriteLine("-- SET_REPORT and GET_REPORT");
        var set = s_usb.Control(0x21, 0x09, 0x0214, 0, 1, new byte[] { 0x14 }, out int err);
        Probe.Check("SET_REPORT(Output 0x14) is read as the OUT endpoint's 0x14", set != null && Probe.Hex(Read(150) ?? Array.Empty<byte>()) == "2401", $"error {err}");
        Probe.Check("and stops the reports", Read(150) == null && s_lastError == ERROR_SEM_TIMEOUT);
        s_usb.Control(0x21, 0x09, 0x0213, 0, 1, new byte[] { 0x13 }, out _);
        var r = Read(150);
        Probe.Check("SET_REPORT(Output 0x13) starts them", r?.Length == 37, Show(r));

        // A SET_REPORT's data stage is one report, however many 8-byte
        // control packets carry it, and the device reads it once. tinyusb
        // first drops a leading byte that matches wValue's report ID from a
        // stage longer than one byte (hid_device.c, SET_REPORT), and loopj
        // reads what is left from its first byte (src/gcca.c:248-252). So
        // the 0x11 report HID stacks send reads as command 0x01, which
        // loopj ignores, and with report ID 0 in wValue it reads as 0x11.
        // Neither halts on the trailing 0x15.
        while (s_outputs.TryDequeue(out _)) { }
        set = s_usb.Control(0x21, 0x09, 0x0211, 0, 6, new byte[] { 0x11, 0x01, 0x00, 0x00, 0x00, 0x15 }, out err);
        Thread.Sleep(100);
        bool halted = proto.GetState("halted");
        Probe.Check("SET_REPORT(Output 0x11) of 11 01 00 00 00 15 loses its ID, as tinyusb drops it: command 0x01, no motor, no halt",
            set != null && controller.Ports[0].GetOutput("rumble") == 0 && s_outputs.IsEmpty && !halted && Read(150)?.Length == 37,
            $"error {err}, rumble {controller.Ports[0].GetOutput("rumble")}, halted {halted}");
        if (halted) Recover(controller);
        set = s_usb.Control(0x21, 0x09, 0x0200, 0, 6, new byte[] { 0x11, 0x01, 0x00, 0x00, 0x00, 0x15 }, out err);
        bool ran = Probe.WaitFor(() => controller.Ports[0].GetOutput("rumble") == 1, 2000);
        halted = proto.GetState("halted");
        Probe.Check("with report ID 0 in wValue the same 6 bytes are one command 0x11: port 1's motor runs, and the 0x15 stops nothing",
            set != null && ran && !halted && Read(150)?.Length == 37, $"error {err}, rumble {controller.Ports[0].GetOutput("rumble")}, halted {halted}");
        if (halted) Recover(controller);
        Send(0x11, 0x00, 0x00, 0x00, 0x00);
        Probe.WaitFor(() => controller.Ports[0].GetOutput("rumble") == 0, 2000);

        // No source shows a real adapter's answer to GET_REPORT, and no host
        // sends one. loopj's firmware supplies no report (src/gcca.c:240-244),
        // and tinyusb then answers a nonzero requested ID with that byte
        // alone and stalls report ID 0 or a one-byte request
        // (hid_device.c, GET_REPORT).
        var get = s_usb.Control(0xA1, 0x01, 0x0121, 0, 37, null, out int gerr);
        Probe.Check("GET_REPORT(Input 0x21) answers the report ID alone", get != null && Probe.Hex(get) == "21",
            get == null ? $"error {gerr}" : Probe.Hex(get));
        get = s_usb.Control(0xA1, 0x01, 0x0122, 0, 37, null, out gerr);
        Probe.Check("GET_REPORT(Input 0x22) answers 0x22 alone, no other report's bytes", get != null && Probe.Hex(get) == "22",
            get == null ? $"error {gerr}" : Probe.Hex(get));
        get = s_usb.Control(0xA1, 0x01, 0x0100, 0, 37, null, out gerr);
        Probe.Check("GET_REPORT(Input) of report ID 0 stalls", get == null && gerr != 0,
            get == null ? $"error {gerr}" : Probe.Hex(get));
        get = s_usb.Control(0xA1, 0x01, 0x0121, 0, 1, null, out gerr);
        Probe.Check("a one-byte GET_REPORT stalls", get == null && gerr != 0,
            get == null ? $"error {gerr}" : Probe.Hex(get));
        Probe.Check("reports go on after them", Read(150)?.Length == 37, $"error {s_lastError}");
    }

    /// <summary>A port reset, as HaltAndReset sends one, and 0x13: the
    /// adapter answering again after a check that halted it.</summary>
    static void Recover(HMController controller)
    {
        typeof(HIDMaestro.Internal.Usbip.UsbipEmulatedDevice)
            .GetMethod("ResetDeviceState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(controller.UsbipHandle!.Device, null);
        s_usb.ResetPipe(In);
        s_usb.ResetPipe(Out);
        Send(0x13);
        Read(150);
    }

    // ── The engine on exact clock ticks ─────────────────────────────────

    /// <summary>The protocol engine driven in process with chosen poll
    /// times and buffer sizes. A host on Windows cannot make the device
    /// babble: WinUSB, with ALLOW_PARTIAL_READS on as libusb sets it
    /// (windows_winusb.c:3595-3599), reads whole packets and keeps the rest
    /// for the next read, and hidusb reads the report's own length. So the
    /// transfer-level rules are checked here, with exact arithmetic.</summary>
    static void EngineChecks(HMContext ctx, HMProfile gc)
    {
        Console.WriteLine("-- the protocol engine, transfer by transfer");
        var set = new HIDMaestro.Internal.Usbip.UsbDescriptorSet(gc.Inner);
        var compiled = HIDMaestro.Internal.Usbip.CompiledProtocol.Compile(gc.Inner, set)!;
        var engine = new HIDMaestro.Internal.Usbip.ProtocolEngine(compiled);
        long ms = Stopwatch.Frequency / 1000;
        long origin = 1000 * ms;
        var buf = new byte[64];

        Probe.Check("no packet before 0x13",
            engine.Poll(origin + 8 * ms, origin, buf, out _, out _) == HIDMaestro.Internal.Usbip.ProtocolEngine.PollResult.Nak);
        var report = new byte[37];
        report[0] = 0x21;
        report[1] = 0x14; report[10] = 0x14; report[19] = 0x14; report[28] = 0x04; // ports 1 to 3 wired
        engine.SetReport(report, origin, origin);
        engine.HandlePacket(new byte[] { 0x13 });

        var r1 = engine.Poll(origin + 8 * ms, origin, buf, out int w1, out bool short1);
        Probe.Check("a poll sends the 37-byte report as one full packet, which does not end the transfer",
            r1 == HIDMaestro.Internal.Usbip.ProtocolEngine.PollResult.Packet && w1 == 37 && !short1, $"{r1} {w1} short {short1}");
        var r2 = engine.Poll(origin + 16 * ms, origin, buf.AsSpan(37), out int w2, out _);
        Probe.Check("the next report into the 27 bytes left is babble",
            r2 == HIDMaestro.Internal.Usbip.ProtocolEngine.PollResult.Babble && w2 == 0, $"{r2} {w2}");
        var r3 = engine.Poll(origin + 24 * ms, origin, buf.AsSpan(0, 8), out _, out _);
        Probe.Check("an 8-byte read babbles on the first report",
            r3 == HIDMaestro.Internal.Usbip.ProtocolEngine.PollResult.Babble);
        engine.HandlePacket(new byte[] { 0x14 });
        var r4 = engine.Poll(origin + 32 * ms, origin, buf, out int w4, out bool short4);
        Probe.Check("a reply is a short packet, which ends the transfer",
            r4 == HIDMaestro.Internal.Usbip.ProtocolEngine.PollResult.Packet && w4 == 2 && short4
            && buf[0] == 0x24 && buf[1] == 0x01, $"{r4} {w4} {Probe.Hex(buf.AsSpan(0, w4))}");

        // A report carries what the adapter's latest read of its controllers
        // found. Reads fall every 1.19971 ms from the reset: at 4.799 and
        // 5.999 ms here. A write at 5.0 ms is in no report until the read at
        // 5.999 ms, so a poll at 5.5 ms sends the state written before it.
        engine.Reset();
        engine.HandlePacket(new byte[] { 0x13 });
        long t0 = 200_000 * ms;
        report[4] = 0x0A;
        engine.SetReport(report, t0 + ms / 10, t0);
        report[4] = 0x0B;
        engine.SetReport(report, t0 + 5 * ms, t0);
        engine.Poll(t0 + 5 * ms + ms / 2, t0, buf, out _, out _);
        byte before = buf[4];
        engine.Poll(t0 + 6 * ms + ms / 2, t0, buf, out _, out _);
        byte after = buf[4];
        Probe.Check("a write between the adapter's reads waits for its next read",
            before == 0x0A && after == 0x0B, $"poll at 5.5 ms read 0x{before:X2}, poll at 6.5 ms read 0x{after:X2}");

        // At a 1 ms host poll the adapter answers only once it has read its
        // controllers since its last report: every 1.19971 ms with ports 1
        // to 3, so 6000 polls carry 6000 / 1.19971 reports, give or take one.
        engine.HandlePacket(new byte[] { 0x13 });
        int reports = CountReports(engine, origin, ms, 6000, buf);
        int expected = (int)Math.Floor(6000 / 1.19971);
        Probe.Check("1 ms polls, ports 1 to 3: one report per 1.19971 ms",
            Math.Abs(reports - expected) <= 1, $"{reports} reports in 6000 polls, expected {expected}");
        report[19] = 0x04; // exactly ports 1 and 2
        engine.SetReport(report, origin + 6500 * ms, origin);
        reports = CountReports(engine, origin + 7000 * ms, ms, 6000, buf);
        Probe.Check("1 ms polls, exactly ports 1 and 2: a report every poll",
            reports == 6000, $"{reports} reports in 6000 polls");
        reports = CountReports(engine, origin + 14000 * ms, 8 * ms, 1000, buf);
        Probe.Check("8 ms polls: a report every poll", reports == 1000, $"{reports} reports in 1000 polls");

        // Windows' schedule for an interrupt endpoint, from the tables under
        // USB_ENDPOINT_DESCRIPTOR's bInterval (Microsoft Learn).
        var fs = new[] { (1, 1000), (2, 2000), (3, 2000), (7, 4000), (8, 8000), (15, 8000), (16, 16000), (32, 32000), (255, 32000) };
        var hs = new[] { (1, 125), (2, 250), (3, 500), (4, 1000), (5, 2000), (6, 4000), (7, 4000), (16, 4000) };
        var wrong = fs.Where(t => Period((byte)t.Item1, 2) != t.Item2)
            .Select(t => $"FS {t.Item1}")
            .Concat(hs.Where(t => Period((byte)t.Item1, 3) != t.Item2)
                .Select(t => $"HS {t.Item1}"))
            .ToList();
        Probe.Check("the poll period follows Windows' bInterval tables at full and high speed", wrong.Count == 0, string.Join(", ", wrong));

        Replies(compiled);
        LowSpeed(ctx);
    }

    /// <summary>CompiledProtocol.DescriptorPeriodMicros at a USB/IP speed,
    /// 1 low, 2 full, 3 high, in whichever form the build declares it.</summary>
    static int Period(byte bInterval, uint speed)
    {
        var m = typeof(HIDMaestro.Internal.Usbip.CompiledProtocol).GetMethod("DescriptorPeriodMicros",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        object second = m.GetParameters()[1].ParameterType == typeof(bool) ? speed >= 3 : speed;
        return (int)m.Invoke(null, new object[] { bInterval, second })!;
    }

    /// <summary>A reply waits for the IN endpoint, and one that finds the
    /// endpoint's reply already waiting is dropped, as loopj's firmware
    /// drops a report while its endpoint is busy (src/gcca.c:165, 181, 193,
    /// tud_hid_ready). The command's state change still applies.</summary>
    static void Replies(HIDMaestro.Internal.Usbip.CompiledProtocol compiled)
    {
        Console.WriteLine("-- replies while one waits");
        var engine = new HIDMaestro.Internal.Usbip.ProtocolEngine(compiled);
        long ms = Stopwatch.Frequency / 1000;
        long origin = 1000 * ms;
        var buf = new byte[64];
        for (int i = 0; i < 8; i++) engine.HandlePacket(new byte[] { 0x12 });
        var sent = new List<string>();
        for (int k = 1; k <= 4; k++)
        {
            var r = engine.Poll(origin + 8 * k * ms, origin, buf, out int w, out _);
            sent.Add(r == HIDMaestro.Internal.Usbip.ProtocolEngine.PollResult.Packet ? Probe.Hex(buf.AsSpan(0, Math.Min(w, 2))) : r.ToString());
        }
        Probe.Check("eight 0x12 before a read: one reply, then nothing while not polling",
            sent.SequenceEqual(new[] { "2200", "Nak", "Nak", "Nak" }), string.Join(" ", sent));

        engine.HandlePacket(new byte[] { 0x13 });
        engine.HandlePacket(new byte[] { 0x12 });
        engine.HandlePacket(new byte[] { 0x14 });
        var first = engine.Poll(origin + 48 * ms, origin, buf, out int w1, out _);
        string got = Probe.Hex(buf.AsSpan(0, w1));
        var second = engine.Poll(origin + 56 * ms, origin, buf, out _, out _);
        Probe.Check("0x14 behind a waiting 0x12 reply: its reply dropped, its stop applied",
            first == HIDMaestro.Internal.Usbip.ProtocolEngine.PollResult.Packet && w1 == 25 && got.StartsWith("22", StringComparison.Ordinal)
            && second == HIDMaestro.Internal.Usbip.ProtocolEngine.PollResult.Nak && !engine.GetState(0),
            $"{first} {w1} bytes {got[..Math.Min(4, got.Length)]}, then {second}");
    }

    /// <summary>A low-speed copy of the profile, its input endpoint at
    /// bInterval 4. Windows polls a low-speed interrupt endpoint every 8 ms
    /// for bInterval 0 to 15, 16 ms for 16 to 35 and 32 ms beyond
    /// (USB_ENDPOINT_DESCRIPTOR, Microsoft Learn), and a host modeled
    /// faster takes only those periods.</summary>
    static void LowSpeed(HMContext ctx)
    {
        Console.WriteLine("-- a low-speed device");
        var ls = new[] { (0, 8000), (4, 8000), (15, 8000), (16, 16000), (35, 16000), (36, 32000), (255, 32000) };
        var wrong = ls.Where(t => Period((byte)t.Item1, 1) != t.Item2).Select(t => $"LS {t.Item1}={Period((byte)t.Item1, 1)}").ToList();
        Probe.Check("the poll period follows Windows' low-speed bInterval table", wrong.Count == 0, string.Join(", ", wrong));

        string? name = typeof(HMContext).Assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.Replace('\\', '/').EndsWith("/gamecube-adapter.json", StringComparison.Ordinal));
        if (name == null)
        {
            Probe.Check("gamecube-adapter.json is embedded", false);
            return;
        }
        string json;
        using (var stream = typeof(HMContext).Assembly.GetManifestResourceStream(name)!)
        using (var reader = new System.IO.StreamReader(stream))
            json = reader.ReadToEnd();
        var j = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        j["id"] = "s80-low-speed";
        var usb = j["usbConfiguration"]!.AsObject();
        usb["busSpeed"] = "low";
        usb["configurationDescriptor"] = usb["configurationDescriptor"]!.GetValue<string>()
            .Replace("0705810325000807", "0705810308000407", StringComparison.OrdinalIgnoreCase);
        // A low-speed interrupt endpoint carries at most 8 bytes, so the
        // report goes out in five packets.
        usb["interfaces"]![0]!["altSettings"]![0]!["endpoints"]![0]!["interval"] = 4;
        usb["interfaces"]![0]!["altSettings"]![0]!["endpoints"]![0]!["maxPacketSize"] = 8;
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hm-s80-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "s80-low-speed.json"), j.ToJsonString());
            ctx.LoadProfilesFromDirectory(dir);
            var low = ctx.GetProfile("s80-low-speed")!;
            var set = new HIDMaestro.Internal.Usbip.UsbDescriptorSet(low.Inner);
            Probe.Check("busSpeed low is low speed in the import reply", set.Speed == 1, $"{set.Speed}");
            var compiled = HIDMaestro.Internal.Usbip.CompiledProtocol.Compile(low.Inner, set)!;
            var proto = new HMDeviceProtocol(new HIDMaestro.Internal.Usbip.ProtocolEngine(compiled));
            Probe.Check("bInterval 4 at low speed is an 8 ms poll", proto.DescriptorPollInterval == TimeSpan.FromMilliseconds(8),
                $"{proto.DescriptorPollInterval.TotalMilliseconds} ms");
            var refused = new List<int>();
            var taken = new List<int>();
            foreach (int p in new[] { 1, 2, 4, 8, 16, 32 })
            {
                try { proto.HostPollInterval = TimeSpan.FromMilliseconds(p); taken.Add(p); }
                catch (ArgumentOutOfRangeException) { refused.Add(p); }
            }
            Probe.Check("a low-speed host poll is 8, 16 or 32 ms, and 1, 2 and 4 ms are refused",
                taken.SequenceEqual(new[] { 8, 16, 32 }) && refused.SequenceEqual(new[] { 1, 2, 4 }),
                $"taken {string.Join(",", taken)}, refused {string.Join(",", refused)}");
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    static int CountReports(HIDMaestro.Internal.Usbip.ProtocolEngine engine, long start, long step, int polls, byte[] buf)
    {
        // A port reset puts the source poll grid where the host's starts.
        engine.Reset();
        engine.HandlePacket(new byte[] { 0x13 });
        int count = 0;
        for (int i = 1; i <= polls; i++)
            if (engine.Poll(start + i * step, start, buf, out _, out _) == HIDMaestro.Internal.Usbip.ProtocolEngine.PollResult.Packet)
                count++;
        return count;
    }

    // ── The poll clock ──────────────────────────────────────────────────

    static void Clock(HMController controller, HMDeviceProtocol proto)
    {
        Console.WriteLine("-- the poll clock");
        var at8 = Rate(60);
        Probe.Check("8 ms host poll: a report every 8 ms",
            at8.MedianMs >= 7.5 && at8.MedianMs <= 8.5, $"median {at8.MedianMs:F3} ms, {at8.Hz:F1} Hz");

        bool threw = false;
        try { proto.HostPollInterval = TimeSpan.FromMilliseconds(3); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Probe.Check("a 3 ms host poll is refused: a full-speed host schedules powers of two", threw);

        // Ports 1, 2 and 3 hold controllers: the adapter reads them every
        // 1.19971 ms, so about one 1 ms poll in six finds nothing new.
        proto.HostPollInterval = TimeSpan.FromMilliseconds(1);
        var at1 = Rate(1200);
        Probe.Check("1 ms host poll, ports 1 to 3: about 833 reports a second",
            at1.Hz >= 800 && at1.Hz <= 860, $"{at1.Hz:F1} Hz, median {at1.MedianMs:F3} ms, p95 {at1.P95Ms:F3} ms");

        // Exactly ports 1 and 2: every 0.9995 ms, so every poll finds news.
        controller.Ports[2].Disconnect();
        var at1two = Rate(1200);
        Probe.Check("1 ms host poll, exactly ports 1 and 2: a report every poll",
            at1two.Hz >= 960 && at1two.Hz <= 1010, $"{at1two.Hz:F1} Hz, median {at1two.MedianMs:F3} ms, p95 {at1two.P95Ms:F3} ms");
        controller.Ports[2].Connect();
        proto.HostPollInterval = proto.DescriptorPollInterval;
        Probe.Check("back to 8 ms", proto.HostPollInterval == TimeSpan.FromMilliseconds(8));
    }

    static (double Hz, double MedianMs, double P95Ms) Rate(int reads)
    {
        var times = new List<long>(reads);
        Read(150); // the first read lands on the poll grid
        for (int i = 0; i < reads; i++)
        {
            if (Read(150) == null) break;
            times.Add(Stopwatch.GetTimestamp());
        }
        if (times.Count < 2) return (0, 0, 0);
        var gaps = new List<double>(times.Count - 1);
        for (int i = 1; i < times.Count; i++) gaps.Add((times[i] - times[i - 1]) * 1000.0 / Stopwatch.Frequency);
        gaps.Sort();
        double total = (times[^1] - times[0]) * 1.0 / Stopwatch.Frequency;
        return ((times.Count - 1) / total, gaps[gaps.Count / 2], gaps[(int)(gaps.Count * 0.95)]);
    }

    // ── 0x15 and the port reset ─────────────────────────────────────────

    static void HaltAndReset(HMController controller, HMDeviceProtocol proto)
    {
        Console.WriteLine("-- 0x15 and a port reset");
        Send(0x11, 0x01, 0x00, 0x00, 0x00);
        Probe.WaitFor(() => controller.Ports[0].GetOutput("rumble") == 1, 2000);
        Send(0x15);
        Probe.Check("0x15 stops the adapter", proto.GetState("halted") && !proto.GetState("polling"));
        var r = Read(150);
        Probe.Check("reads fail", r == null && s_lastError != ERROR_SEM_TIMEOUT && s_lastError != 0, $"error {s_lastError}");
        Probe.Check("writes fail", !s_usb.Write(Out, new byte[] { 0x13 }, out int werr), $"error {werr}");
        Probe.Check("control requests fail", s_usb.Control(0x80, 0x00, 0, 0, 2, null, out int cerr) == null, $"error {cerr}");

        // The port reset a host sends to recover, as the hub delivers it:
        // the device's reset of its own state. WinUSB has no device reset to
        // send one with (libusb windows_winusb.c winusbx_reset_device).
        while (s_outputs.TryDequeue(out _)) { }
        var device = controller.UsbipHandle!.Device;
        typeof(HIDMaestro.Internal.Usbip.UsbipEmulatedDevice)
            .GetMethod("ResetDeviceState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(device, null);
        Probe.Check("a port reset clears the stop", !proto.GetState("halted") && !proto.GetState("polling") && !proto.GetState("stalled"));
        Probe.WaitFor(() => controller.Ports[0].GetOutput("rumble") == 0, 2000);
        Probe.Check("and returns every motor command to zero", controller.Ports[0].GetOutput("rumble") == 0
            && s_outputs.Contains((1, "rumble", 0)));
        s_usb.ResetPipe(In);
        s_usb.ResetPipe(Out);
        Probe.Check("no report until 0x13 again", Read(150) == null && s_lastError == ERROR_SEM_TIMEOUT, $"error {s_lastError}");
        Send(0x13);
        Probe.Check("0x13 starts the adapter again", Read(150)?.Length == 37, $"error {s_lastError}");
    }

    static void Recreate(HMContext ctx, HMProfile gc, List<string> before)
    {
        Console.WriteLine("-- a new adapter after the stop");
        Probe.WaitFor(() => Devnodes.PresentAdapters().Except(before, StringComparer.OrdinalIgnoreCase).Count() == 0, 10_000);
        using var controller = ctx.CreateController(gc);
        if (!Open(controller, before)) return;
        using (s_usb)
        {
            Probe.Check("no report before 0x13", Read(150) == null && s_lastError == ERROR_SEM_TIMEOUT);
            Send(0x13);
            Probe.Check("reports after 0x13", Read(150)?.Length == 37);
        }
        DisposeBesideSubmit(controller);
    }

    /// <summary>Dispose while another thread holds the submit lock, as a
    /// port submit does between its in-lock check and its write. Dispose
    /// marks the controller disposed under that lock, so it waits for the
    /// holder before it marks anything or tears anything down, and a submit
    /// after it is refused.</summary>
    static void DisposeBesideSubmit(HMController controller)
    {
        Console.WriteLine("-- Dispose beside a submit");
        var disposedField = typeof(HMController).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var held = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            lock (controller.SubmitLock)
            {
                held.Set();
                release.Wait(10_000);
            }
        }) { IsBackground = true };
        holder.Start();
        held.Wait(5000);
        var disposer = new Thread(() => controller.Dispose()) { IsBackground = true };
        disposer.Start();
        Thread.Sleep(200);
        bool markedWhileHeld = (bool)disposedField.GetValue(controller)!;
        bool aliveWhileHeld = disposer.IsAlive;
        release.Set();
        holder.Join(5000);
        bool finished = disposer.Join(30_000);
        Probe.Check("while a thread holds the submit lock, Dispose waits and has not marked the controller disposed",
            !markedWhileHeld && aliveWhileHeld, $"marked {markedWhileHeld}, still running {aliveWhileHeld}");
        Probe.Check("and finishes once it is released", finished && (bool)disposedField.GetValue(controller)!);
        bool refused;
        try { controller.Ports[0].SubmitState(default); refused = false; }
        catch (ObjectDisposedException) { refused = true; }
        Probe.Check("a port submit after Dispose is refused", refused);
    }

    // ── Idle frames, and a Dispose beside a submit ──────────────────────

    /// <summary>A copy of the profile that streams idle frames every 4 ms.
    /// The pump repeats the ports' report as it stands, so a port the
    /// program never connected stays empty.</summary>
    static void IdleFrames(HMContext ctx, List<string> before)
    {
        Console.WriteLine("-- idle frames on a device with ports");
        Probe.WaitFor(() => Devnodes.PresentAdapters().Except(before, StringComparer.OrdinalIgnoreCase).Count() == 0, 10_000);
        string? name = typeof(HMContext).Assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.Replace('\\', '/').EndsWith("/gamecube-adapter.json", StringComparison.Ordinal));
        if (name == null)
        {
            Probe.Check("gamecube-adapter.json is embedded", false);
            return;
        }
        string json;
        using (var stream = typeof(HMContext).Assembly.GetManifestResourceStream(name)!)
        using (var reader = new System.IO.StreamReader(stream))
            json = reader.ReadToEnd();
        var j = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        j["id"] = "s80-idle-frames";
        j["extendedReport"]!["idleFrameIntervalMs"] = 4;
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hm-s80-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        HMController? controller = null;
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "s80-idle-frames.json"), j.ToJsonString());
            ctx.LoadProfilesFromDirectory(dir);
            controller = ctx.CreateController(ctx.GetProfile("s80-idle-frames")!);
            Thread.Sleep(100);
            var engine = controller.UsbipHandle!.Device.Protocol!;
            byte[] report = engine.CopyReport();
            Probe.Check("idle frames leave port 1 empty, as created: no controller, status 0x04, data zero",
                !controller.Ports[0].IsConnected && Probe.Hex(report.AsSpan(1, 9)) == "040000000000000000",
                $"connected {controller.Ports[0].IsConnected}, port 1 {Probe.Hex(report.AsSpan(1, 9))}");
            controller.Ports[1].Connect();
            Thread.Sleep(50);
            report = engine.CopyReport();
            Probe.Check("and repeat a port connected since, with port 1 still empty",
                Probe.Hex(report.AsSpan(1, 18)) == "040000000000000000" + "140000808080800000", Probe.Hex(report.AsSpan(1, 18)));
        }
        finally
        {
            controller?.Dispose();
            System.IO.Directory.Delete(dir, recursive: true);
        }
        Probe.Check("the device leaves with the controller",
            Probe.WaitFor(() => Devnodes.PresentAdapters().Except(before, StringComparer.OrdinalIgnoreCase).Count() == 0, 10_000));
    }

    // ── A latency hook that disposes ────────────────────────────────────

    /// <summary>OnSubmitLatencyMicros runs consumer code. A handler that
    /// hands Dispose to another thread and waits for it must find the
    /// submit lock free, or Dispose waits on the submit that waits on
    /// Dispose.</summary>
    static void LatencyHookDisposes(HMContext ctx, HMProfile gc, List<string> before)
    {
        Console.WriteLine("-- a latency hook that disposes from another thread");
        Probe.WaitFor(() => Devnodes.PresentAdapters().Except(before, StringComparer.OrdinalIgnoreCase).Count() == 0, 10_000);
        var controller = ctx.CreateController(gc);
        bool joined = false, called = false;
        controller.OnSubmitLatencyMicros = _ =>
        {
            if (called) return;
            called = true;
            var disposer = new Thread(() => controller.Dispose()) { IsBackground = true };
            disposer.Start();
            joined = disposer.Join(5000);
        };
        try
        {
            controller.SubmitState(new HMGamepadState { Axes = Centered() });
            Probe.Check("the hook ran, and Dispose on another thread finished inside it", called && joined,
                $"called {called}, joined {joined}");
        }
        finally
        {
            controller.Dispose();
        }
        Probe.Check("the device leaves with the controller",
            Probe.WaitFor(() => Devnodes.PresentAdapters().Except(before, StringComparer.OrdinalIgnoreCase).Count() == 0, 10_000));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    static byte[]? Read(int timeoutMs, int length = 37)
    {
        s_usb.SetTimeout(In, (uint)timeoutMs);
        var r = s_usb.Read(In, length, out s_lastError);
        return r;
    }

    /// <summary>The report one poll after the next. A host poll that comes
    /// before the adapter's next read of its controllers carries what the
    /// read before the change found, so the second report is the first
    /// sure to carry a change.</summary>
    static byte[]? ReadFresh()
    {
        Read(150);
        return Read(150);
    }

    static void Send(params byte[] command)
    {
        if (!s_usb.Write(Out, command, out int err))
            Console.WriteLine($"  [info] write {Probe.Hex(command)} failed, error {err}");
    }

    static byte[] Block(byte[] report, int port) => report.AsSpan(1 + 9 * port, 9).ToArray();

    static Dictionary<HMAxis, float> Centered() => new()
    {
        [HMAxis.X] = 0.5f, [HMAxis.Y] = 0.5f, [HMAxis.Rx] = 0.5f, [HMAxis.Ry] = 0.5f,
        [HMAxis.Z] = 0f, [HMAxis.Rz] = 0f,
    };

    static string Show(byte[]? b) => b == null ? $"none, error {s_lastError}" : Probe.Hex(b);
}
