// The live parts: controllers of the XUSB companion profiles on the USB/IP
// host controller, read through Windows' own XUSB driver and XInput, and on
// the UMDF2 driver where the machine runs it, to compare.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using Microsoft.Win32;

internal static unsafe partial class Program
{
    /// <summary>Elevated, and no other program that embeds HIDMaestro
    /// running. 2 to skip, 0 to fail, -1 to go on.</summary>
    static int Preflight()
    {
        using (var id = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Check("the live part runs elevated", false, "creating a controller needs elevation");
                return 0;
            }
        if (SdkConsumers.Find() is { } other)
        {
            Console.WriteLine($"  SKIP: {other} has HIDMaestro.Core.dll loaded, and two programs that embed HIDMaestro never test at once.");
            return 2;
        }
        Console.WriteLine($"  {RuntimeInformation.OSArchitecture}, Windows {Environment.OSVersion.Version}, MachineBackend {HMContext.MachineBackend}");
        return -1;
    }

    static HMBackend ParseBackend(string name) => name switch
    {
        "usbip" => HMBackend.Usbip,
        "umdf2" => HMBackend.Umdf2,
        _ => throw new ArgumentException($"--backend {name}: usbip or umdf2"),
    };

    static int Live(string backendName, string profileId)
    {
        var backend = ParseBackend(backendName);
        Console.WriteLine($"\n=== live: {profileId} on {backend}, read through XInput ===");
        if (Preflight() is int pre and >= 0) return pre;

        // Every control request a host sends the devices, as the device
        // logs each setup packet once per import.
        var controls = new List<string>();
        var since = System.Diagnostics.Stopwatch.StartNew();
        DeviceOrchestrator.DiagTap = line =>
        {
            if (line.StartsWith("usbip control", StringComparison.Ordinal) || line.StartsWith("usbip output", StringComparison.Ordinal)
                || line.StartsWith("usbip message", StringComparison.Ordinal) || line.Contains("start wait", StringComparison.Ordinal))
                lock (controls) controls.Add($"{since.ElapsedMilliseconds,6} ms  {line}");
        };

        uint before = ConnectedSlots();
        Console.WriteLine($"  XInput slots connected before the create: {Slots(before)}");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var profile = ctx.GetProfile(profileId);
        Check("the context holds the profile", profile != null);
        if (profile == null) return 0;

        var packets = new List<(HMOutputSource Source, byte Id, byte[] Data)>();
        HMController? pad = null, second = null, control = null;
        string? instance = null;
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            pad = ctx.CreateController(profile, null, backend);
            long createMs = clock.ElapsedMilliseconds;
            uint atReturn = ConnectedSlots();
            pad.OutputReceived += (_, pkt) => { lock (packets) packets.Add((pkt.Source, pkt.ReportId, pkt.Data.ToArray())); };
            Console.WriteLine($"  created in {createMs} ms on {pad.Backend}, transport index {pad.TransportIndex}");
            Check($"the controller runs on {backend}", pad.Backend == backend);

            if (backend == HMBackend.Usbip)
            {
                var set = pad.UsbipHandle!.Device.Descriptors;
                instance = set.UsbInstanceId!;
                string hardwareId = $@"USB\VID_{set.VendorId:X4}&PID_{set.ProductId:X4}";
                Console.WriteLine("\n-- the devnode --");
                var node = Node(instance);
                Check($"{instance} is present and started with no problem as the create returns", node is { Started: true, Problem: 0 },
                      node == null ? "not present" : $"status 0x{node.Status:X8} problem {node.Problem}");
                if (node != null)
                {
                    Console.WriteLine($"  hardware IDs:   {string.Join(" | ", node.HardwareIds)}");
                    Console.WriteLine($"  compatible IDs: {string.Join(" | ", node.CompatibleIds)}");
                    Console.WriteLine($"  service {node.Service}, INF {node.Inf}, description {node.Description}");
                    Console.WriteLine($"  parents: {string.Join(" <- ", node.Parents)}");
                    Check("its service is xusb22", string.Equals(node.Service, "xusb22", StringComparison.OrdinalIgnoreCase), node.Service);
                    Check("its driver package is the inbox xusb22.inf", string.Equals(node.Inf, "xusb22.inf", StringComparison.OrdinalIgnoreCase), node.Inf);
                    Check($"it carries the hardware ID {hardwareId} and, with no OS descriptor, no MS_COMP compatible ID",
                          node.HardwareIds.Contains(hardwareId, StringComparer.OrdinalIgnoreCase)
                          && !node.CompatibleIds.Any(c => c.Contains("MS_COMP", StringComparison.OrdinalIgnoreCase)));
                    Check("it sits under the USB/IP host controller's root hub",
                          node.ParentHardwareIds.Any(h => string.Equals(h, @"ROOT\USBIP_WIN2\UDE", StringComparison.OrdinalIgnoreCase)),
                          string.Join(" | ", node.ParentHardwareIds));
                }
                Check("the create returned with XInput holding one slot more than before it",
                      BitCount(atReturn) == BitCount(before) + 1 || BitCount(before) == 4, $"before {Slots(before)}, at return {Slots(atReturn)}");
            }

            Console.WriteLine("\n-- XInput --");
            var mark = new HMGamepadState
            {
                Buttons = HMButton.A,
                Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 1f, [HMAxis.Y] = 0.5f, [HMAxis.Rx] = 0.5f, [HMAxis.Ry] = 0.5f, [HMAxis.Z] = 0f, [HMAxis.Rz] = 0f },
            };
            int slot = FindSlot(pad, mark);
            Check("XInputGetState returns SUCCESS on a slot that reads the submitted A and left stick", slot >= 0,
                  slot >= 0 ? $"slot {slot}, {clock.ElapsedMilliseconds} ms after the create began" : $"connected now: {Slots(ConnectedSlots())}");
            if (slot >= 0)
            {
                uint s = (uint)slot;
                Check("the slot was free before the create", (before & (1u << slot)) == 0, Slots(before));
                if (XInputGetCapabilities(s, 0, out var caps) == 0)
                    Console.WriteLine($"  capabilities: {Caps(caps)}");
                Check("XInputGetCapabilities reads a gamepad, subtype 1", XInputGetCapabilities(s, 0, out caps) == 0 && caps.Type == 1 && caps.SubType == 1,
                      $"type {caps.Type} subtype {caps.SubType}");

                Reads(pad, s, "at rest", new HMGamepadState(), g => g.wButtons == 0 && g.bLeftTrigger == 0 && g.bRightTrigger == 0
                      && g.sThumbLX == 0 && g.sThumbLY == 0 && g.sThumbRX == 0 && g.sThumbRY == 0);
                foreach (var (name, button, bit) in new (string, HMButton, ushort)[]
                {
                    ("A", HMButton.A, 0x1000), ("B", HMButton.B, 0x2000), ("X", HMButton.X, 0x4000), ("Y", HMButton.Y, 0x8000),
                    ("the left bumper", HMButton.LeftBumper, 0x0100), ("the right bumper", HMButton.RightBumper, 0x0200),
                    ("Back", HMButton.Back, 0x0020), ("Start", HMButton.Start, 0x0010),
                    ("the left stick click", HMButton.LeftStick, 0x0040), ("the right stick click", HMButton.RightStick, 0x0080),
                })
                    Reads(pad, s, $"{name} is wButtons 0x{bit:X4}", new HMGamepadState { Buttons = button }, g => g.wButtons == bit);
                foreach (var (hat, bits) in new (HMHat, ushort)[]
                {
                    (HMHat.North, 0x0001), (HMHat.South, 0x0002), (HMHat.West, 0x0004), (HMHat.East, 0x0008), (HMHat.NorthEast, 0x0009),
                })
                    Reads(pad, s, $"d-pad {hat} is wButtons 0x{bits:X4}", new HMGamepadState { Hat = hat }, g => g.wButtons == bits);
                Reads(pad, s, "Guide is 0x0400 through XInputGetStateEx", new HMGamepadState { Buttons = HMButton.Guide }, g => g.wButtons == 0x0400, extended: true);
                Reads(pad, s, "the triggers: left full, right half", Axes(0.5f, 0.5f, 0.5f, 0.5f, 1f, 0.5f), g => g.bLeftTrigger == 255 && g.bRightTrigger == 128);
                Reads(pad, s, "the left stick: left and up", Axes(0f, 0f, 0.5f, 0.5f, 0f, 0f), g => g.sThumbLX == -32768 && g.sThumbLY == 32767 && g.sThumbRX == 0 && g.sThumbRY == 0);
                Reads(pad, s, "the left stick: right and down", Axes(1f, 1f, 0.5f, 0.5f, 0f, 0f), g => g.sThumbLX == 32767 && g.sThumbLY == -32768);
                Reads(pad, s, "the right stick: left and up", Axes(0.5f, 0.5f, 0f, 0f, 0f, 0f), g => g.sThumbRX == -32768 && g.sThumbRY == 32767 && g.sThumbLX == 0 && g.sThumbLY == 0);
                Reads(pad, s, "the right stick: right and down", Axes(0.5f, 0.5f, 1f, 1f, 0f, 0f), g => g.sThumbRX == 32767 && g.sThumbRY == -32768);
                Reads(pad, s, "back at rest", new HMGamepadState(), g => g.wButtons == 0 && g.sThumbRX == 0 && g.sThumbRY == 0);

                if (backend == HMBackend.Usbip)
                {
                    // SubmitRawReport: the frame is the function's report as
                    // the device sends it, built here by the model's own
                    // layout. B alone.
                    var layout = pad.UsbipHandle!.Device.Presentation.Report!;
                    int size = layout.Size;
                    var rawFrame = new byte[size];
                    var onlyB = new HMGamepadState { Buttons = HMButton.B };
                    VendorBlobCodec.EncodeInput(layout, in onlyB, 0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f, rawFrame, new VendorBlobCodec.EncoderState());
                    XINPUT_STATE rawState = default;
                    bool rawRead = WaitUntil(() =>
                    {
                        pad.SubmitRawReport(rawFrame);
                        return XInputGetState(s, out rawState) == 0 && rawState.Gamepad.wButtons == 0x2000;
                    }, 3_000);
                    Check($"a raw {size}-byte report reads through XInput: B", rawRead, $"{Hex(rawFrame)}, buttons 0x{rawState.Gamepad.wButtons:X4}");
                    foreach (int length in new[] { size - 1, size + 1, size == 20 ? 18 : 20 })
                    {
                        string refused = "accepted";
                        try { pad.SubmitRawReport(new byte[length]); }
                        catch (ArgumentException e) { refused = e.Message; }
                        Check($"a raw frame of {length} bytes is refused with a message that names the length",
                              refused.Contains($"this frame is {length} bytes", StringComparison.Ordinal) && refused.Contains($"{size}-byte", StringComparison.Ordinal), refused);
                    }
                    Reads(pad, s, "and SubmitState takes over again, at rest", new HMGamepadState(), g => g.wButtons == 0);
                }

                Console.WriteLine("\n-- rumble and the LED, as the companion's packets --");
                lock (packets)
                    foreach (var o in packets)
                        Console.WriteLine($"  before any XInputSetState: {o.Source} 0x{o.Id:X2} {Hex(o.Data)}");
                Rumble(s, 0x4000, 0x8000, packets, 0x40, 0x80);
                Rumble(s, 0xFFFF, 0x0100, packets, 0xFF, 0x01);
                Rumble(s, 0, 0, packets, 0x00, 0x00);
                // On UMDF2 the companion publishes every SET_STATE buffer a
                // caller sends, whatever its form, and WGI's own are 9 bytes.
                // On USB/IP the device publishes the reports xusb22 sends,
                // each turned into the 5-byte form, and takes the rest.
                if (backend == HMBackend.Usbip)
                lock (packets)
                    Check("every packet the controller raised is the companion's 5-byte XInput packet",
                          packets.All(o => o.Source == HMOutputSource.XInput && o.Id == 0 && o.Data.Length == 5 && o.Data[0] == 0 && o.Data[4] is 1 or 2),
                          string.Join(", ", packets.Where(o => !(o.Source == HMOutputSource.XInput && o.Data.Length == 5)).Select(o => $"{o.Source} {Hex(o.Data)}")));
            }

            Console.WriteLine("\n-- what the create and Windows did, from the probe's start --");
            string busId = HIDMaestro.Internal.Usbip.UsbipEmulatedDevice.BusIdFor(pad.TransportIndex);
            lock (controls)
                foreach (string line in controls.Where(c => c.Contains($" {busId}:", StringComparison.Ordinal) || c.Contains("start wait", StringComparison.Ordinal)))
                    Console.WriteLine("  " + line);

            // A second pad of the profile: its own devnode and slot.
            Console.WriteLine("\n-- a second pad --");
            second = ctx.CreateController(profile, null, backend);
            if (slot >= 0)
            {
                var onFirst = new HMGamepadState { Buttons = HMButton.X };
                var onSecond = new HMGamepadState { Buttons = HMButton.Y };
                int secondSlot = -1;
                XINPUT_STATE first = default;
                WaitUntil(() =>
                {
                    pad.SubmitState(in onFirst);
                    second.SubmitState(in onSecond);
                    for (uint i = 0; i < 4 && secondSlot < 0; i++)
                        if (i != slot && XInputGetState(i, out var other2) == 0 && other2.Gamepad.wButtons == 0x8000) secondSlot = (int)i;
                    return secondSlot >= 0 && XInputGetState((uint)slot, out first) == 0 && first.Gamepad.wButtons == 0x4000;
                }, 20_000);
                Check("it takes a slot of its own, and each slot reads its own pad: X on the first, Y on the second",
                      secondSlot >= 0 && first.Gamepad.wButtons == 0x4000, $"first slot {slot} buttons 0x{first.Gamepad.wButtons:X4}, second slot {secondSlot}");
                var atRest = new HMGamepadState();
                pad.SubmitState(in atRest);
            }
            second.Dispose();
            second = null;

            Console.WriteLine("\n-- positive control: dualsense-composite beside it --");
            control = ctx.CreateController(ctx.GetProfile("dualsense-composite")!);
            string controlSerial = DeviceIdentity.Resolve(control.IdentityKey, control.TransportIndex).SyntheticSerial;
            string controlInstance = $@"USB\VID_054C&PID_0CE6\{controlSerial}";
            bool controlStarted = WaitUntil(() => Node(controlInstance) is { Started: true }, 30_000);
            Check($"{controlInstance} is present and started", controlStarted, Node(controlInstance) is { } n ? $"service {n.Service}, problem {n.Problem}" : "not present");
            string? hidPath = null;
            WaitUntil(() => (hidPath = HidPathUnder(controlInstance, "vid_054c&pid_0ce6")) != null, 30_000);
            Check("its HID interface is present", hidPath != null, hidPath ?? "");
            if (hidPath != null)
            {
                var cross = new HMGamepadState { Buttons = HMButton.A };
                var rest = new HMGamepadState();
                byte pressed = 0, released = 0;
                bool down = WaitUntil(() => { control.SubmitState(in cross); return HidInput(hidPath, 0x01, 64) is { Length: > 8 } r && ((pressed = r[8]) & 0x20) != 0; }, 10_000);
                bool up = WaitUntil(() => { control.SubmitState(in rest); return HidInput(hidPath, 0x01, 64) is { Length: > 8 } r && ((released = r[8]) & 0x20) == 0; }, 10_000);
                Check("a submitted Cross reads back over HID and releases (report 0x01, byte 8)", down && up, $"pressed 0x{pressed:X2}, released 0x{released:X2}");
            }
            if (slot >= 0)
            {
                Reads(pad, (uint)slot, "the pad still reads through XInput beside it: B", new HMGamepadState { Buttons = HMButton.B }, g => g.wButtons == 0x2000);
                Reads(pad, (uint)slot, "and back at rest", new HMGamepadState(), g => g.wButtons == 0);
            }

            Console.WriteLine("\n-- cleanup --");
            control.Dispose();
            control = null;
            pad.Dispose();
            pad = null;
            bool gone = WaitUntil(() => (instance == null || Node(instance) == null) && Node(controlInstance) == null, 30_000);
            Check("the devnodes are gone after Dispose", gone);
            if (slot >= 0)
                Check("the XInput slot is free again", WaitUntil(() => XInputGetState((uint)slot, out _) != 0, 10_000), Slots(ConnectedSlots()));
        }
        catch (Exception e)
        {
            Check("the live part ran to its end", false, e.ToString());
        }
        finally
        {
            try { control?.Dispose(); } catch { }
            try { second?.Dispose(); } catch { }
            try { pad?.Dispose(); } catch { }
            DeviceOrchestrator.DiagTap = null;
        }
        return 0;
    }

    static int BitCount(uint mask) => System.Numerics.BitOperations.PopCount(mask);

    /// <summary>The slot that reads <paramref name="mark"/>, submitted on
    /// <paramref name="pad"/>, or -1.</summary>
    static int FindSlot(HMController pad, HMGamepadState mark)
    {
        int slot = -1;
        WaitUntil(() =>
        {
            pad.SubmitState(in mark);
            for (uint i = 0; i < 4; i++)
                if (XInputGetState(i, out var s) == 0 && s.Gamepad.wButtons == (ushort)XInputButtons(mark) && (mark.Axes == null || s.Gamepad.sThumbLX == 32767))
                { slot = (int)i; return true; }
            return false;
        }, 20_000);
        return slot;
    }

    static int XInputButtons(HMGamepadState s) => s.Buttons switch
    {
        HMButton.A => 0x1000,
        HMButton.B => 0x2000,
        HMButton.X => 0x4000,
        HMButton.Y => 0x8000,
        HMButton.Start => 0x0010,
        HMButton.Back => 0x0020,
        _ => -1,
    };

    static string Caps(XINPUT_CAPABILITIES c) =>
        $"type {c.Type} subtype 0x{c.SubType:X2} flags 0x{c.Flags:X4} buttons 0x{c.Gamepad.wButtons:X4} triggers {c.Gamepad.bLeftTrigger}/{c.Gamepad.bRightTrigger} " +
        $"thumbs {c.Gamepad.sThumbLX:X4},{c.Gamepad.sThumbLY:X4},{c.Gamepad.sThumbRX:X4},{c.Gamepad.sThumbRY:X4} motors {c.Vibration.wLeftMotorSpeed}/{c.Vibration.wRightMotorSpeed}";

    // ── family: each profile on each backend ─────────────────────────────

    /// <summary>What one controller of a profile presents.</summary>
    sealed record Presented(string Caps, string Hid, string Product, string State, string Packet, string Failure);

    static int FamilyPart(List<string> ids, string backendName)
    {
        Console.WriteLine("\n=== family: the XUSB companion profiles on each backend ===");
        if (Preflight() is int pre and >= 0) return pre;
        var backends = backendName switch
        {
            "" => HMContext.MachineBackend == HMBackend.Umdf2 ? new[] { HMBackend.Umdf2, HMBackend.Usbip } : new[] { HMBackend.Usbip },
            "both" => new[] { HMBackend.Umdf2, HMBackend.Usbip },
            _ => new[] { ParseBackend(backendName) },
        };
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var list = ids.Count > 0 ? ids : Family.ToList();
        foreach (var id in list)
        {
            var p = ctx.GetProfile(id);
            if (p == null) { Check($"{id}: in the catalog", false); continue; }
            Console.WriteLine($"\n-- {id} --");
            var seen = new Dictionary<HMBackend, Presented>();
            foreach (var b in backends)
            {
                var x = Present(ctx, p, b);
                seen[b] = x;
                Console.WriteLine($"  {b,-6} XInputGetCapabilities: {x.Caps}");
                Console.WriteLine($"  {b,-6} HID collection: {x.Hid}");
                Console.WriteLine($"  {b,-6} HID product string: {x.Product}");
                Console.WriteLine($"  {b,-6} XInput state of the probe's input: {x.State}");
                Console.WriteLine($"  {b,-6} XInputSetState(0x4000, 0x8000): {x.Packet}");
                Check($"{id} on {b}: created, a slot, capabilities, a HID collection and a packet", x.Failure.Length == 0, x.Failure);
            }
            if (seen.TryGetValue(HMBackend.Umdf2, out var u) && seen.TryGetValue(HMBackend.Usbip, out var s))
            {
                Check($"{id}: XInputGetCapabilities the same on both backends", u.Caps == s.Caps, $"UMDF2 {u.Caps} | USB/IP {s.Caps}");
                Check($"{id}: the XInput state of one input the same on both", u.State == s.State, $"UMDF2 {u.State} | USB/IP {s.State}");
                Check($"{id}: XInputSetState's packet the same on both", u.Packet == s.Packet, $"UMDF2 {u.Packet} | USB/IP {s.Packet}");
                // Not a pass condition: the HID child is the profile's
                // descriptor on UMDF2 and xusb22's own on USB/IP (R8).
                Console.WriteLine($"  [R8] HID collection {(u.Hid == s.Hid ? "the same" : "differs")}, product string {(u.Product == s.Product ? "the same" : $"'{u.Product}' against '{s.Product}'")}");
            }
        }
        return 0;
    }

    static readonly HMGamepadState ProbeInput = new()
    {
        Buttons = HMButton.A | HMButton.Back | HMButton.RightStick,
        Hat = HMHat.SouthWest,
        Axes = new Dictionary<HMAxis, float>
        {
            [HMAxis.X] = 0.25f, [HMAxis.Y] = 0.75f, [HMAxis.Rx] = 0.9f, [HMAxis.Ry] = 0.1f, [HMAxis.Z] = 0.3f, [HMAxis.Rz] = 0.6f,
            [HMAxis.Vx] = 0.3f, [HMAxis.Vy] = 0.6f, [HMAxis.Slider] = 0.4f, [HMAxis.Dial] = 0.2f,
        },
    };

    static Presented Present(HMContext ctx, HMProfile p, HMBackend backend)
    {
        HMController? c = null;
        var packets = new List<string>();
        try
        {
            uint before = ConnectedSlots();
            c = ctx.CreateController(p, "xusbfam:" + p.Id, backend);
            c.OutputReceived += (_, pkt) => { lock (packets) packets.Add($"{pkt.Source} 0x{pkt.ReportId:X2} {Hex(pkt.Data.Span)}"); };
            // The new slot: the one connected now that was not before.
            int slot = -1;
            WaitUntil(() =>
            {
                uint now = ConnectedSlots() & ~before;
                if (now == 0) return false;
                slot = System.Numerics.BitOperations.TrailingZeroCount(now);
                return true;
            }, 10_000);
            if (slot < 0) return new("", "", "", "", "", "no new XInput slot");
            XInputGetCapabilities((uint)slot, 0, out var caps);
            string capsText = Caps(caps);
            XINPUT_STATE st = default;
            string state = "";
            var input = ProbeInput;
            WaitUntil(() =>
            {
                c.SubmitState(in input);
                if (XInputGetState((uint)slot, out st) != 0) return false;
                string now = $"buttons 0x{st.Gamepad.wButtons:X4} LT {st.Gamepad.bLeftTrigger} RT {st.Gamepad.bRightTrigger} L {st.Gamepad.sThumbLX},{st.Gamepad.sThumbLY} R {st.Gamepad.sThumbRX},{st.Gamepad.sThumbRY}";
                bool same = now == state;
                state = now;
                return same && st.Gamepad.wButtons != 0;
            }, 3_000);
            lock (packets) packets.Clear();
            var v = new XINPUT_VIBRATION { wLeftMotorSpeed = 0x4000, wRightMotorSpeed = 0x8000 };
            uint code = XInputSetState((uint)slot, ref v);
            string packet = "";
            WaitUntil(() => { lock (packets) packet = packets.LastOrDefault(x => x.Contains("XInput", StringComparison.Ordinal)) ?? ""; return packet.Length > 0; }, 2_000);
            packet = $"code {code}, {(packet.Length > 0 ? packet : "nothing")}";
            v = default;
            XInputSetState((uint)slot, ref v);
            // The HID collection under the controller's devnode, a child or
            // a grandchild: xusb22 makes its HID device under the USB device.
            string parent = backend == HMBackend.Usbip ? c.UsbipHandle!.Device.Descriptors.UsbInstanceId! : c.InstanceId!;
            var cols = Collections(parent);
            string hid = cols.Count == 0 ? "none" : string.Join(" || ", cols.Select(x => x.ParsedKey[x.ParsedKey.IndexOf(" caps", StringComparison.Ordinal)..]));
            string product = cols.Count == 0 ? "" : string.Join(" | ", cols.Select(x => x.Product ?? "(none)"));
            string failure = cols.Count == 0 ? "no HID collection" : caps.Type == 0 ? "no capabilities" : packet.Contains("nothing", StringComparison.Ordinal) ? "no packet" : "";
            return new(capsText, hid, product, state, packet, failure);
        }
        catch (Exception e) { return new("", "", "", "", "", $"{e.GetType().Name}: {e.Message}"); }
        finally { c?.Dispose(); }
    }

    /// <summary>The HID collections under <paramref name="parent"/>, its
    /// children's or, where a child has no HID interface, its
    /// grandchildren's.</summary>
    static List<Collection> Collections(string parent)
    {
        var list = new List<Collection>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 10_000)
        {
            list.Clear();
            foreach (var child in Hid.Children(parent))
            {
                var paths = Hid.Interfaces(child);
                if (paths.Count == 0)
                    foreach (var grand in Hid.Children(child)) paths.AddRange(Hid.Interfaces(grand));
                foreach (var path in paths)
                    if (Hid.ReadOne(path, out _) is { } col) list.Add(col);
            }
            if (list.Count > 0) break;
            Thread.Sleep(50);
        }
        return list;
    }

    // ── sweep: xusb22's answer for each subtype ──────────────────────────

    static int Sweep(List<string> args)
    {
        Console.WriteLine("\n=== sweep: xusb22's capabilities and HID collection for each subtype ===");
        if (Preflight() is int pre and >= 0) return pre;
        var subtypes = args.Count > 0 ? args.Select(a => Convert.ToByte(a, 16)).ToList() : Enumerable.Range(1, 0x13).Select(i => (byte)i).ToList();
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var basis = ctx.GetProfile(ProfileId)!.Inner;
        foreach (byte sub in subtypes)
        {
            var o = Json(basis);
            o["id"] = $"sweep-subtype-{sub:X2}";
            o["name"] = $"Subtype 0x{sub:X2}";
            o["xinput"] = new JsonObject { ["via"] = "xusbCompanion", ["subType"] = $"0x{sub:X2}" };
            var loaded = ProfileDatabase.LoadTexts(new[] { ($"sweep-{sub:X2}.json", o.ToJsonString()) }, ModelRegistry.Catalog);
            var copy = loaded.All.SingleOrDefault();
            if (copy == null) { Check($"subtype 0x{sub:X2}: the copy loads", false, string.Join("; ", loaded.Issues.Select(i => i.Reason))); continue; }
            var x = Present(ctx, new HMProfile(copy), HMBackend.Usbip);
            Console.WriteLine($"  0x{sub:X2} XInputGetCapabilities: {x.Caps}");
            Console.WriteLine($"  0x{sub:X2} HID collection: {x.Hid}");
            Console.WriteLine($"  0x{sub:X2} HID product string: {x.Product}");
            Console.WriteLine($"  0x{sub:X2} XInput state of the probe's input: {x.State}");
            Check($"subtype 0x{sub:X2}: created, a slot, capabilities, a HID collection", x.Failure.Length == 0 || x.Failure == "no packet", x.Failure);
        }
        return 0;
    }

    static HMGamepadState Axes(float lx, float ly, float rx, float ry, float lt, float rt) => new()
    {
        Axes = new Dictionary<HMAxis, float>
        {
            [HMAxis.X] = lx, [HMAxis.Y] = ly, [HMAxis.Rx] = rx, [HMAxis.Ry] = ry, [HMAxis.Z] = lt, [HMAxis.Rz] = rt,
        },
    };

    /// <summary>Submit <paramref name="state"/> and wait for XInput to read
    /// what <paramref name="want"/> describes on the pad's slot.</summary>
    static void Reads(HMController pad, uint slot, string name, HMGamepadState state, Func<XINPUT_GAMEPAD, bool> want, bool extended = false)
    {
        XINPUT_STATE last = default;
        uint code = 0;
        bool ok = WaitUntil(() =>
        {
            pad.SubmitState(in state);
            code = extended ? XInputGetStateEx(slot, out last) : XInputGetState(slot, out last);
            return code == 0 && want(last.Gamepad);
        }, 3_000);
        var g = last.Gamepad;
        Check(name, ok, $"code {code} buttons 0x{g.wButtons:X4} LT {g.bLeftTrigger} RT {g.bRightTrigger} L {g.sThumbLX},{g.sThumbLY} R {g.sThumbRX},{g.sThumbRY}");
    }

    /// <summary>XInputSetState, then the packet the controller raises: the
    /// companion's 5-byte IOCTL_XUSB_SET_STATE buffer, source XInput, on
    /// both backends. XInput's 16-bit speeds reach it as their high
    /// bytes.</summary>
    static void Rumble(uint slot, ushort left, ushort right, List<(HMOutputSource Source, byte Id, byte[] Data)> packets, byte wantLeft, byte wantRight)
    {
        int from;
        lock (packets) from = packets.Count;
        var v = new XINPUT_VIBRATION { wLeftMotorSpeed = left, wRightMotorSpeed = right };
        uint code = XInputSetState(slot, ref v);
        byte[] want = { 0x00, 0x00, wantLeft, wantRight, 0x02 };
        (HMOutputSource Source, byte Id, byte[] Data) got = default;
        bool arrived = WaitUntil(() =>
        {
            lock (packets) got = packets.Skip(from).LastOrDefault(o => o.Data.Length == 5 && o.Data[4] == 2);
            return got.Data != null && got.Source == HMOutputSource.XInput && got.Id == 0 && got.Data.AsSpan().SequenceEqual(want);
        }, 3_000);
        Console.WriteLine($"  XInputSetState({left}, {right}) returned {code}: {(got.Data == null ? "(nothing)" : $"{got.Source} 0x{got.Id:X2} {Hex(got.Data)}")}");
        Check($"XInputSetState({left}, {right}) arrives as XInput 00 00 {wantLeft:X2} {wantRight:X2} 02", code == 0 && arrived);
    }

    static bool WaitUntil(Func<bool> done, int ms)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (done()) return true;
            if (clock.ElapsedMilliseconds >= ms) return false;
            Thread.Sleep(10);
        }
    }

    static uint ConnectedSlots()
    {
        uint mask = 0;
        for (uint i = 0; i < 4; i++)
            if (XInputGetState(i, out _) == 0) mask |= 1u << (int)i;
        return mask;
    }

    static string Slots(uint mask) => mask == 0 ? "none" : string.Join(", ", Enumerable.Range(0, 4).Where(i => (mask & (1u << i)) != 0));

    // ── XInput ───────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_VIBRATION
    {
        public ushort wLeftMotorSpeed, wRightMotorSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_CAPABILITIES
    {
        public byte Type, SubType;
        public ushort Flags;
        public XINPUT_GAMEPAD Gamepad;
        public XINPUT_VIBRATION Vibration;
    }

    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetState(uint index, out XINPUT_STATE state);

    // Ordinal 100: XInputGetState with the Guide button in wButtons.
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    static extern uint XInputGetStateEx(uint index, out XINPUT_STATE state);

    [DllImport("xinput1_4.dll")]
    static extern uint XInputSetState(uint index, ref XINPUT_VIBRATION vibration);

    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetCapabilities(uint index, uint flags, out XINPUT_CAPABILITIES capabilities);

    // ── devnodes ─────────────────────────────────────────────────────────

    sealed record NodeInfo(uint Status, uint Problem, string Service, string Inf, string Description,
                           string[] HardwareIds, string[] CompatibleIds, string[] Parents, string[] ParentHardwareIds)
    {
        public bool Started => (Status & DN_STARTED) != 0;
    }

    const uint DN_STARTED = 0x00000008;
    // cfgmgr32's property numbers, one above setupapi's.
    const uint CM_DRP_DEVICEDESC = 1, CM_DRP_HARDWAREID = 2, CM_DRP_COMPATIBLEIDS = 3, CM_DRP_SERVICE = 5;

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    static DEVPROPKEY s_driverInfPath = new() { fmtid = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), pid = 5 };
    static DEVPROPKEY s_instanceId = new() { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };
    static Guid s_hidInterface = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_DevNode_Registry_PropertyW(uint devInst, uint property, out uint type, char* buffer, ref uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, char* buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_IDW(uint devInst, char* buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? deviceId, char* buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_PropertyW(string deviceInterface, ref DEVPROPKEY key, out uint type, char* buffer, ref uint size, uint flags);

    static string[] MultiSz(char* buffer, uint bytes)
    {
        var all = new string(buffer, 0, (int)(bytes / 2));
        return all.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    static string[] RegistryStrings(uint devInst, uint property)
    {
        char* buffer = stackalloc char[2048];
        uint bytes = 4096;
        return CM_Get_DevNode_Registry_PropertyW(devInst, property, out _, buffer, ref bytes, 0) == 0 ? MultiSz(buffer, bytes) : Array.Empty<string>();
    }

    static string DeviceId(uint devInst)
    {
        char* buffer = stackalloc char[512];
        return CM_Get_Device_IDW(devInst, buffer, 512, 0) == 0 ? new string(buffer) : "";
    }

    /// <summary>The present devnode of <paramref name="instanceId"/>, or
    /// null when it is not present.</summary>
    static NodeInfo? Node(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0) return null;
        if (CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) != 0) return null;
        char* buffer = stackalloc char[512];
        uint size = 1024;
        string inf = CM_Get_DevNode_PropertyW(devInst, ref s_driverInfPath, out _, buffer, ref size, 0) == 0 ? new string(buffer) : "";
        var parents = new List<string>();
        var parentHardware = new List<string>();
        for (uint at = devInst; CM_Get_Parent(out uint parent, at, 0) == 0; at = parent)
        {
            parents.Add(DeviceId(parent));
            parentHardware.AddRange(RegistryStrings(parent, CM_DRP_HARDWAREID));
        }
        return new NodeInfo(status, problem,
            RegistryStrings(devInst, CM_DRP_SERVICE).FirstOrDefault() ?? "", inf,
            RegistryStrings(devInst, CM_DRP_DEVICEDESC).FirstOrDefault() ?? "",
            RegistryStrings(devInst, CM_DRP_HARDWAREID), RegistryStrings(devInst, CM_DRP_COMPATIBLEIDS),
            parents.ToArray(), parentHardware.ToArray());
    }

    /// <summary>The present HID interface whose device sits under
    /// <paramref name="usbInstance"/>, by the parents of its devnode.</summary>
    static string? HidPathUnder(string usbInstance, string pathHolds)
    {
        if (CM_Get_Device_Interface_List_SizeW(out uint length, ref s_hidInterface, null, 0) != 0 || length <= 1) return null;
        var list = new char[length];
        fixed (char* b = list)
        {
            if (CM_Get_Device_Interface_ListW(ref s_hidInterface, null, b, length, 0) != 0) return null;
            char* buffer = stackalloc char[512];
            foreach (string path in MultiSz(b, length * 2))
            {
                if (!path.Contains(pathHolds, StringComparison.OrdinalIgnoreCase)) continue;
                uint size = 1024;
                if (CM_Get_Device_Interface_PropertyW(path, ref s_instanceId, out _, buffer, ref size, 0) != 0) continue;
                if (Node(new string(buffer)) is { } node && node.Parents.Contains(usbInstance, StringComparer.OrdinalIgnoreCase)) return path;
            }
        }
        return null;
    }

    // ── HID ──────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetInputReport(IntPtr device, byte* report, uint length);

    /// <summary>GET_REPORT(Input) of <paramref name="reportId"/> through
    /// the HID stack, or null when the device does not answer.</summary>
    static byte[]? HidInput(string path, byte reportId, int length)
    {
        IntPtr h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) return null;
        try
        {
            var report = new byte[length];
            report[0] = reportId;
            fixed (byte* r = report)
                return HidD_GetInputReport(h, r, (uint)length) ? report : null;
        }
        finally { CloseHandle(h); }
    }
}
