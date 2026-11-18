// The live part: one controller of the profile on the USB/IP host
// controller, read through Windows' own GIP stack, XInput and
// Windows.Gaming.Input.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using Microsoft.Win32;

internal static unsafe partial class Program
{
    static int Live(bool noOptOut)
    {
        string id = noOptOut ? "xbox-one-s-full-no-opt-out" : ProfileId;
        Console.WriteLine($"\n=== live: {id} under Windows' GIP driver, XInput and Windows.Gaming.Input ===");
        using (var who = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(who).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Check("the live part runs elevated", false, "creating a controller needs elevation");
                return 0;
            }
        if (SdkConsumers.Find() is { } other)
        {
            Console.WriteLine($"  SKIP: {other} has HIDMaestro.Core.dll loaded, and two programs that embed HIDMaestro never test at once.");
            return 2;
        }
        Console.WriteLine($"  Windows {Environment.OSVersion.Version}, {RuntimeInformation.OSArchitecture}");

        var controls = new List<string>();
        DeviceOrchestrator.DiagTap = line =>
        {
            if (line.StartsWith("usbip control", StringComparison.Ordinal)) lock (controls) controls.Add(line);
        };

        uint before = ConnectedSlots();
        Console.WriteLine($"  XInput slots connected before the create: {Slots(before)}");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        if (noOptOut) ctx.LoadProfilesFromDirectory(Path.Combine(AppContext.BaseDirectory, "profiles"));
        var profile = ctx.GetProfile(id);
        Check("the context holds the profile", profile != null);
        if (profile == null) return 0;

        var raw = new List<(byte Id, byte[] Data)>();
        HMController? pad = null, control = null;
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            pad = ctx.CreateController(profile);
            pad.OutputReceived += (_, pkt) =>
            {
                if (pkt.Source == HMOutputSource.HidOutput) lock (raw) raw.Add((pkt.ReportId, pkt.Data.ToArray()));
            };
            Console.WriteLine($"  created in {clock.ElapsedMilliseconds} ms, transport index {pad.TransportIndex}");
            var engine = pad.UsbipHandle!.Device.Protocol!;
            string serial = DeviceIdentity.Resolve(pad.IdentityKey, pad.TransportIndex).SyntheticSerial;
            string instance = $@"USB\VID_045E&PID_02EA\{serial}";

            Console.WriteLine("\n-- the devnode --");
            bool started = WaitUntil(() => Node(instance) is { Started: true }, 30_000);
            var node = Node(instance);
            Check($"{instance} is present and started, with no problem", started && node is { Problem: 0 },
                  node == null ? "not present" : $"status 0x{node.Status:X8} problem {node.Problem} after {clock.ElapsedMilliseconds} ms");
            if (node != null)
            {
                Console.WriteLine($"  hardware IDs:   {string.Join(" | ", node.HardwareIds)}");
                Console.WriteLine($"  compatible IDs: {string.Join(" | ", node.CompatibleIds)}");
                Console.WriteLine($"  service {node.Service}, upper filters {string.Join(",", node.UpperFilters)}, INF {node.Inf}, description {node.Description}");
                Console.WriteLine($"  parents: {string.Join(" <- ", node.Parents)}");
                Check("its service is dc1-controller, from the inbox dc1-controller.inf",
                      string.Equals(node.Service, "dc1-controller", StringComparison.OrdinalIgnoreCase)
                      && string.Equals(node.Inf, "dc1-controller.inf", StringComparison.OrdinalIgnoreCase), $"{node.Service} {node.Inf}");
                Check("xboxgip is its upper filter", node.UpperFilters.Contains("xboxgip", StringComparer.OrdinalIgnoreCase), string.Join(",", node.UpperFilters));
                Check(@"it carries USB\VID_045E&PID_02EA and USB\MS_COMP_XGIP10",
                      node.HardwareIds.Contains(@"USB\VID_045E&PID_02EA", StringComparer.OrdinalIgnoreCase)
                      && node.CompatibleIds.Contains(@"USB\MS_COMP_XGIP10", StringComparer.OrdinalIgnoreCase));
                Check("it sits under the USB/IP host controller's root hub",
                      node.ParentHardwareIds.Any(h => string.Equals(h, @"ROOT\USBIP_WIN2\UDE", StringComparison.OrdinalIgnoreCase)),
                      string.Join(" | ", node.ParentHardwareIds));
            }

            Console.WriteLine("\n-- the host's GIP messages, raw at the device --");
            WaitUntil(() => engine.SnapshotLog().Any(x => x.Command.Length >= 5 && x.Command[0] == 0x05 && x.Command[4] == 0x00), 20_000);
            Thread.Sleep(1_000);
            var log = engine.SnapshotLog();
            long t0 = log.Count > 0 ? log[0].Ticks : 0;
            foreach (var (ticks, cmd) in log)
                Console.WriteLine($"  +{(ticks - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency,8:F1} ms  OUT 0x01: {Hex(cmd)}");
            bool askedMetadata = log.Any(x => x.Command.Length >= 4 && x.Command[0] == 0x04 && x.Command[3] == 0x00);
            bool ackedFirst = log.Any(x => x.Command.Length >= 13 && x.Command[0] == 0x01 && x.Command[5] == 0x04 && x.Command[7] == 0x3A);
            bool startedGip = log.Any(x => x.Command.Length >= 5 && x.Command[0] == 0x05 && x.Command[3] == 0x01 && x.Command[4] == 0x00);
            int security = log.Count(x => x.Command.Length >= 1 && x.Command[0] == 0x06);
            Check("the host answered the Hello with a Metadata Request, or knew the metadata already", askedMetadata || startedGip,
                  askedMetadata ? "requested" : "no request: the metadata was cached");
            if (askedMetadata) Check("and acknowledged the first fragment, which let the rest go", ackedFirst);
            Check("the host sent Set Device State Start", startedGip);
            Console.WriteLine($"  security messages (0x06) from the host: {security}");
            if (noOptOut) Check("info: without the opt-out the host sent security messages", true, security.ToString());
            else Check("with the opt-out in the metadata the host sent no security message", security == 0, security.ToString());

            Console.WriteLine("\n-- the HID child xboxgip makes --");
            var children = Descendants(instance);
            foreach (var c in children)
                Console.WriteLine($"  {c.Id}  service {c.Info.Service}, upper filters {string.Join(",", c.Info.UpperFilters)}, hardware {string.Join(" | ", c.Info.HardwareIds.Take(3))}");
            Check("a child under the device carries xinputhid", children.Any(c => c.Info.UpperFilters.Contains("xinputhid", StringComparer.OrdinalIgnoreCase)
                                                                         || string.Equals(c.Info.Service, "xinputhid", StringComparison.OrdinalIgnoreCase)),
                  string.Join(" ; ", children.Select(c => c.Id)));

            Console.WriteLine("\n-- XInput --");
            var mark = new HMGamepadState
            {
                Buttons = HMButton.A,
                Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 1f, [HMAxis.Y] = 0.5f, [HMAxis.Rx] = 0.5f, [HMAxis.Ry] = 0.5f, [HMAxis.Z] = 0f, [HMAxis.Rz] = 0f },
            };
            int slot = -1;
            WaitUntil(() =>
            {
                pad.SubmitState(in mark);
                for (uint i = 0; i < 4; i++)
                    if (XInputGetState(i, out var s) == 0 && s.Gamepad.wButtons == 0x1000 && s.Gamepad.sThumbLX == 32767) { slot = (int)i; return true; }
                return false;
            }, 30_000);
            Check("XInputGetState returns SUCCESS on a slot that reads the submitted A and left stick", slot >= 0,
                  slot >= 0 ? $"slot {slot}, {clock.ElapsedMilliseconds} ms after the create began" : $"connected now: {Slots(ConnectedSlots())}");
            if (slot >= 0)
            {
                uint s = (uint)slot;
                Check("the slot was free before the create", (before & (1u << slot)) == 0, Slots(before));
                if (XInputGetCapabilities(s, 0, out var caps) == 0)
                    Console.WriteLine($"  capabilities: type {caps.Type} subtype {caps.SubType} flags 0x{caps.Flags:X4} buttons 0x{caps.Gamepad.wButtons:X4} " +
                                      $"triggers {caps.Gamepad.bLeftTrigger}/{caps.Gamepad.bRightTrigger} motors {caps.Vibration.wLeftMotorSpeed}/{caps.Vibration.wRightMotorSpeed}");
                Check("XInputGetCapabilities reads a gamepad", XInputGetCapabilities(s, 0, out caps) == 0 && caps.Type == 1 && caps.SubType == 1,
                      $"type {caps.Type} subtype {caps.SubType}");

                Reads(pad, s, "at rest", new HMGamepadState(), g => g.wButtons == 0 && g.bLeftTrigger == 0 && g.bRightTrigger == 0
                      && g.sThumbLX == 0 && g.sThumbLY == 0 && g.sThumbRX == 0 && g.sThumbRY == 0);
                foreach (var (name, button, bit) in new (string, HMButton, ushort)[]
                {
                    ("A", HMButton.A, 0x1000), ("B", HMButton.B, 0x2000), ("X", HMButton.X, 0x4000), ("Y", HMButton.Y, 0x8000),
                    ("the left bumper", HMButton.LeftBumper, 0x0100), ("the right bumper", HMButton.RightBumper, 0x0200),
                    ("View", HMButton.Back, 0x0020), ("Menu", HMButton.Start, 0x0010),
                    ("the left stick click", HMButton.LeftStick, 0x0040), ("the right stick click", HMButton.RightStick, 0x0080),
                })
                    Reads(pad, s, $"{name} is wButtons 0x{bit:X4}", new HMGamepadState { Buttons = button }, g => g.wButtons == bit);
                foreach (var (hat, bits) in new (HMHat, ushort)[]
                {
                    (HMHat.North, 0x0001), (HMHat.South, 0x0002), (HMHat.West, 0x0004), (HMHat.East, 0x0008), (HMHat.NorthEast, 0x0009),
                })
                    Reads(pad, s, $"d-pad {hat} is wButtons 0x{bits:X4}", new HMGamepadState { Hat = hat }, g => g.wButtons == bits);
                Reads(pad, s, "Guide, its own GIP message, is 0x0400 through XInputGetStateEx", new HMGamepadState { Buttons = HMButton.Guide }, g => g.wButtons == 0x0400, extended: true);
                Reads(pad, s, "the triggers: left full, right half", Axes(0.5f, 0.5f, 0.5f, 0.5f, 1f, 0.5f), g => g.bLeftTrigger == 255 && g.bRightTrigger is >= 126 and <= 129);
                Reads(pad, s, "the left stick: left and up", Axes(0f, 0f, 0.5f, 0.5f, 0f, 0f), g => g.sThumbLX == -32768 && g.sThumbLY == 32767 && g.sThumbRX == 0 && g.sThumbRY == 0);
                Reads(pad, s, "the left stick: right and down", Axes(1f, 1f, 0.5f, 0.5f, 0f, 0f), g => g.sThumbLX == 32767 && g.sThumbLY == -32768);
                Reads(pad, s, "the right stick: left and up", Axes(0.5f, 0.5f, 0f, 0f, 0f, 0f), g => g.sThumbRX == -32768 && g.sThumbRY == 32767 && g.sThumbLX == 0 && g.sThumbLY == 0);
                Reads(pad, s, "the right stick: right and down", Axes(0.5f, 0.5f, 1f, 1f, 0f, 0f), g => g.sThumbRX == 32767 && g.sThumbRY == -32768);
                Reads(pad, s, "back at rest", new HMGamepadState(), g => g.wButtons == 0 && g.sThumbRX == 0 && g.sThumbRY == 0);

                Console.WriteLine("\n-- Share, through the XUSB gamepad state --");
                string? xusb = InterfaceUnder(s_xusbInterface, instance);
                Check("an XUSB interface sits under the device", xusb != null, xusb ?? "none");
                if (xusb != null)
                {
                    byte extra = 0xFF;
                    ushort buttons = 0;
                    var share = new HMGamepadState { Buttons = HMButton.Share };
                    bool down = WaitUntil(() => { pad.SubmitState(in share); return XusbState(xusb, out buttons, out extra) && (extra & 1) != 0; }, 5_000);
                    Check("Share held reads ExtraSystemButtons bit 0 (IOCTL 0x8000E00C, version 1.1 reply byte 28, OpenXinput.cpp:978)",
                          down, $"buttons 0x{buttons:X4} extra 0x{extra:X2}");
                    var rest = new HMGamepadState();
                    bool up = WaitUntil(() => { pad.SubmitState(in rest); return XusbState(xusb, out buttons, out extra) && (extra & 1) == 0; }, 5_000);
                    Check("and released clears it", up, $"extra 0x{extra:X2}");
                    var guideOnly = new HMGamepadState { Buttons = HMButton.Guide };
                    bool guideRead = WaitUntil(() => { pad.SubmitState(in guideOnly); return XusbState(xusb, out buttons, out extra) && (buttons & 0x0400) != 0 && (extra & 1) == 0; }, 5_000);
                    Check("positive control in the same reply: Guide reads wButtons 0x0400 and Share stays clear", guideRead, $"buttons 0x{buttons:X4} extra 0x{extra:X2}");
                    pad.SubmitState(in rest);
                }

                Console.WriteLine("\n-- rumble, at the device's OUT endpoint --");
                Motor(engine, "XInputSetState(16384, 32768)", () => { var v = new XINPUT_VIBRATION { wLeftMotorSpeed = 16384, wRightMotorSpeed = 32768 }; return XInputSetState(s, ref v); },
                      c => c[8] is >= 24 and <= 26 && c[9] is >= 49 and <= 51 && (c[5] & 0x03) == 0x03);
                Motor(engine, "XInputSetState(0, 0)", () => { var v = new XINPUT_VIBRATION(); return XInputSetState(s, ref v); },
                      c => c[8] == 0 && c[9] == 0);
            }

            Console.WriteLine("\n-- Windows.Gaming.Input --");
            var raws = new List<Windows.Gaming.Input.RawGameController>();
            WaitUntil(() =>
            {
                raws = WgiPins.Raws().Where(r => r.HardwareVendorId == 0x045E && r.HardwareProductId == 0x02EA).ToList();
                return raws.Count > 0;
            }, 10_000);
            Check("RawGameController lists the pad once, 045E:02EA", raws.Count == 1,
                  string.Join(" ; ", WgiPins.Raws().Select(r => $"{r.HardwareVendorId:X4}:{r.HardwareProductId:X4} {r.DisplayName} wireless {r.IsWireless}")));
            if (raws.Count >= 1)
            {
                var r0 = raws[0];
                Console.WriteLine($"  RawGameController: {r0.DisplayName}, buttons {r0.ButtonCount}, axes {r0.AxisCount}, switches {r0.SwitchCount}, wireless {r0.IsWireless}, " +
                                  $"motors {r0.ForceFeedbackMotors.Count}, NonRoamableId {r0.NonRoamableId}");
                var gp = WgiPins.PadOf(r0);
                int pads = WgiPins.Pads().Count(g => WgiPins.RawOf(g) is { HardwareVendorId: 0x045E, HardwareProductId: 0x02EA });
                Check("Gamepad lists it once", gp != null && pads == 1, $"{pads} gamepads of 045E:02EA");
                if (gp != null)
                {
                    var on = new HMGamepadState { Buttons = HMButton.Y };
                    Windows.Gaming.Input.GamepadReading reading = default;
                    bool y = WaitUntil(() => { pad.SubmitState(in on); reading = gp.GetCurrentReading(); return (reading.Buttons & Windows.Gaming.Input.GamepadButtons.Y) != 0; }, 5_000);
                    Check("Gamepad.GetCurrentReading reads Y", y, $"buttons {reading.Buttons}");
                    var rest = new HMGamepadState();
                    pad.SubmitState(in rest);
                    Motor(engine, "Gamepad.Vibration with trigger levels (left 1.0, right 0.5, left trigger 1.0, right trigger 0.5)", () =>
                    {
                        gp.Vibration = new Windows.Gaming.Input.GamepadVibration { LeftMotor = 1.0, RightMotor = 0.5, LeftTrigger = 1.0, RightTrigger = 0.5 };
                        return 0;
                    }, c => c[6] >= 99 && c[7] is >= 49 and <= 51 && c[8] >= 99 && c[9] is >= 49 and <= 51 && (c[5] & 0x0F) == 0x0F);
                    Motor(engine, "Gamepad.Vibration at rest", () => { gp.Vibration = default; return 0; }, c => c[6] == 0 && c[7] == 0 && c[8] == 0 && c[9] == 0);
                }
            }

            lock (raw)
                Console.WriteLine($"  OutputReceived for the pad: {raw.Count} reports, ids {string.Join(" ", raw.Select(o => o.Id.ToString("X2")).Distinct())}");
            Check("each Direct Motor command reached OutputReceived as the host wrote it", raw.Any(o => o.Id == 0x09), raw.Count.ToString());

            Console.WriteLine("\n-- the control requests Windows sent the pad --");
            string busId = HIDMaestro.Internal.Usbip.UsbipEmulatedDevice.BusIdFor(pad.TransportIndex);
            lock (controls)
                foreach (string line in controls.Where(c => c.Contains($" {busId}:", StringComparison.Ordinal)))
                    Console.WriteLine("  " + line);

            Console.WriteLine("\n-- positive control: dualsense-composite beside it --");
            control = ctx.CreateController(ctx.GetProfile("dualsense-composite")!);
            string controlSerial = DeviceIdentity.Resolve(control.IdentityKey, control.TransportIndex).SyntheticSerial;
            string controlInstance = $@"USB\VID_054C&PID_0CE6\{controlSerial}";
            bool controlStarted = WaitUntil(() => Node(controlInstance) is { Started: true }, 30_000);
            Check($"{controlInstance} is present and started", controlStarted, Node(controlInstance) is { } n ? $"service {n.Service}, problem {n.Problem}" : "not present");
            string? hidPath = null;
            WaitUntil(() => (hidPath = InterfaceUnder(s_hidInterface, controlInstance, "vid_054c&pid_0ce6")) != null, 30_000);
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
            bool gone = WaitUntil(() => Node(instance) == null && Node(controlInstance) == null, 30_000);
            Check("both devnodes are gone after Dispose", gone,
                  $"{(Node(instance) == null ? "pad gone" : "pad present")}, {(Node(controlInstance) == null ? "control gone" : "control present")}");
            if (slot >= 0)
                Check("the XInput slot is free again", WaitUntil(() => XInputGetState((uint)slot, out _) != 0, 10_000), Slots(ConnectedSlots()));
            Console.WriteLine($"  not-present records this run leaves: {instance}, {controlInstance}");
        }
        catch (Exception e)
        {
            Check("the live part ran to its end", false, e.ToString());
        }
        finally
        {
            try { control?.Dispose(); } catch { }
            try { pad?.Dispose(); } catch { }
            DeviceOrchestrator.DiagTap = null;
        }
        return 0;
    }

    /// <summary>A vibration request, then the Direct Motor command the device
    /// logged after it, raw: 09 00 seq 09 00, the motor bitmap, the left and
    /// right trigger levels, the left and right vibration levels, duration,
    /// delay and repeat (the GIP specification's Table 467).</summary>
    static void Motor(HIDMaestro.Internal.Usbip.ProtocolEngine engine, string name, Func<uint> send, Func<byte[], bool> want)
    {
        int from = engine.SnapshotLog().Count;
        long sentAt = System.Diagnostics.Stopwatch.GetTimestamp();
        uint code = 0;
        try { code = send(); } catch (Exception e) { Check(name, false, e.Message); return; }
        byte[]? got = null;
        bool ok = WaitUntil(() =>
        {
            var after = engine.SnapshotLog().Where(x => x.Ticks >= sentAt && x.Command.Length >= 13 && x.Command[0] == 0x09).ToList();
            got = after.LastOrDefault().Command;
            return got != null && want(got);
        }, 3_000);
        Console.WriteLine($"  {name} returned {code}. Direct Motor: {(got == null ? "(nothing)" : Hex(got))}");
        Check($"{name} arrives as the Direct Motor command with those levels", code == 0 && ok, got == null ? "nothing" : Hex(got));
    }

    static HMGamepadState Axes(float lx, float ly, float rx, float ry, float lt, float rt) => new()
    {
        Axes = new Dictionary<HMAxis, float>
        {
            [HMAxis.X] = lx, [HMAxis.Y] = ly, [HMAxis.Rx] = rx, [HMAxis.Ry] = ry, [HMAxis.Z] = lt, [HMAxis.Rz] = rt,
        },
    };

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

    // ── XInput and XUSB ──────────────────────────────────────────────────

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

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr device, uint code, byte* input, uint inputSize, byte* output, uint outputSize, out uint returned, IntPtr overlapped);

    /// <summary>IOCTL_XINPUT_GET_GAMEPAD_STATE, version 1.1, on an XUSB
    /// interface: the request is the version and device index 0, the reply
    /// 29 bytes with wButtons at 11 and the extra buttons at 28 (OpenXInput
    /// GamepadState0101, OpenXinput.cpp:214-236 and 2560-2568).</summary>
    static bool XusbState(string path, out ushort buttons, out byte extra)
    {
        buttons = 0; extra = 0;
        IntPtr h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) return false;
        try
        {
            byte* request = stackalloc byte[3];
            request[0] = 0x01; request[1] = 0x01; request[2] = 0x00;
            byte* reply = stackalloc byte[29];
            if (!DeviceIoControl(h, 0x8000E00C, request, 3, reply, 29, out uint got, IntPtr.Zero) || got < 29) return false;
            buttons = (ushort)(reply[11] | (reply[12] << 8));
            extra = reply[28];
            return true;
        }
        finally { CloseHandle(h); }
    }

    // ── devnodes ─────────────────────────────────────────────────────────

    sealed record NodeInfo(uint Status, uint Problem, string Service, string Inf, string Description,
                           string[] HardwareIds, string[] CompatibleIds, string[] UpperFilters, string[] Parents, string[] ParentHardwareIds)
    {
        public bool Started => (Status & DN_STARTED) != 0;
    }

    const uint DN_STARTED = 0x00000008;
    // cfgmgr32's property numbers, one above setupapi's.
    const uint CM_DRP_DEVICEDESC = 1, CM_DRP_HARDWAREID = 2, CM_DRP_COMPATIBLEIDS = 3, CM_DRP_SERVICE = 5, CM_DRP_UPPERFILTERS = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    static DEVPROPKEY s_driverInfPath = new() { fmtid = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), pid = 5 };
    static DEVPROPKEY s_instanceId = new() { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };
    static Guid s_hidInterface = new("4d1e55b2-f16f-11cf-88cb-001111000030");
    // XUSB_INTERFACE_CLASS_GUID (OpenXinput.cpp:518).
    static Guid s_xusbInterface = new("ec87f1e3-c13b-4100-b5f7-8b84d54260cb");

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

    [DllImport("cfgmgr32.dll")]
    static extern uint CM_Get_Child(out uint child, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern uint CM_Get_Sibling(out uint sibling, uint devInst, uint flags);

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

    static NodeInfo? Info(uint devInst)
    {
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
            RegistryStrings(devInst, CM_DRP_UPPERFILTERS), parents.ToArray(), parentHardware.ToArray());
    }

    /// <summary>The present devnode of <paramref name="instanceId"/>, or
    /// null when it is not present.</summary>
    static NodeInfo? Node(string instanceId) =>
        CM_Locate_DevNodeW(out uint devInst, instanceId, 0) == 0 ? Info(devInst) : null;

    /// <summary>Every present devnode under <paramref name="instanceId"/>.</summary>
    static List<(string Id, NodeInfo Info)> Descendants(string instanceId)
    {
        var list = new List<(string, NodeInfo)>();
        if (CM_Locate_DevNodeW(out uint root, instanceId, 0) != 0) return list;
        void Walk(uint at)
        {
            if (CM_Get_Child(out uint child, at, 0) != 0) return;
            for (uint c = child; ;)
            {
                if (Info(c) is { } info) list.Add((DeviceId(c), info));
                Walk(c);
                if (CM_Get_Sibling(out uint next, c, 0) != 0) break;
                c = next;
            }
        }
        Walk(root);
        return list;
    }

    /// <summary>A present interface of <paramref name="interfaceClass"/>
    /// whose device sits under <paramref name="usbInstance"/>.</summary>
    static string? InterfaceUnder(Guid interfaceClass, string usbInstance, string? pathHolds = null)
    {
        if (CM_Get_Device_Interface_List_SizeW(out uint length, ref interfaceClass, null, 0) != 0 || length <= 1) return null;
        var list = new char[length];
        fixed (char* b = list)
        {
            if (CM_Get_Device_Interface_ListW(ref interfaceClass, null, b, length, 0) != 0) return null;
            char* buffer = stackalloc char[512];
            foreach (string path in MultiSz(b, length * 2))
            {
                if (pathHolds != null && !path.Contains(pathHolds, StringComparison.OrdinalIgnoreCase)) continue;
                uint size = 1024;
                if (CM_Get_Device_Interface_PropertyW(path, ref s_instanceId, out _, buffer, ref size, 0) != 0) continue;
                string device = new(buffer);
                if (string.Equals(device, usbInstance, StringComparison.OrdinalIgnoreCase)
                    || Node(device) is { } node && node.Parents.Contains(usbInstance, StringComparer.OrdinalIgnoreCase)) return path;
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
