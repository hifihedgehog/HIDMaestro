// The live part: one controller of the profile on the USB/IP host
// controller, read through Windows' own XUSB driver and XInput.
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
    static int Live()
    {
        Console.WriteLine("\n=== live: the pad under xusb22 and XInput ===");
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

        // Every control request a host sends the devices, as the device
        // logs each setup packet once per import.
        var controls = new List<string>();
        DeviceOrchestrator.DiagTap = line =>
        {
            if (line.StartsWith("usbip control", StringComparison.Ordinal)) lock (controls) controls.Add(line);
        };

        uint before = ConnectedSlots();
        Console.WriteLine($"  XInput slots connected before the create: {Slots(before)}");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var profile = ctx.GetProfile(ProfileId);
        Check("the context holds the profile", profile != null);
        if (profile == null) return 0;

        var raw = new List<(byte Id, byte[] Data)>();
        var decoded = new List<(byte Id, string Fields)>();
        HMController? pad = null, second = null, control = null;
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            pad = ctx.CreateController(profile);
            pad.OutputReceived += (_, pkt) =>
            {
                if (pkt.Source == HMOutputSource.HidOutput) lock (raw) raw.Add((pkt.ReportId, pkt.Data.ToArray()));
            };
            pad.OutputDecoded += (_, e) =>
            {
                lock (decoded) decoded.Add((e.ReportId, string.Join(" ", e.Fields.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))));
            };
            Console.WriteLine($"  created in {clock.ElapsedMilliseconds} ms, transport index {pad.TransportIndex}");
            string serial = DeviceIdentity.Resolve(pad.IdentityKey, pad.TransportIndex).SyntheticSerial;
            string instance = $@"USB\VID_045E&PID_028E\{serial}";

            Console.WriteLine("\n-- the devnode --");
            bool started = WaitUntil(() => Node(instance) is { Started: true }, 30_000);
            var node = Node(instance);
            Check($"{instance} is present and started, with no problem", started && node is { Problem: 0 },
                  node == null ? "not present" : $"status 0x{node.Status:X8} problem {node.Problem} after {clock.ElapsedMilliseconds} ms");
            if (node != null)
            {
                Console.WriteLine($"  hardware IDs:   {string.Join(" | ", node.HardwareIds)}");
                Console.WriteLine($"  compatible IDs: {string.Join(" | ", node.CompatibleIds)}");
                Console.WriteLine($"  service {node.Service}, INF {node.Inf}, description {node.Description}");
                Console.WriteLine($"  parents: {string.Join(" <- ", node.Parents)}");
                Check("its service is xusb22", string.Equals(node.Service, "xusb22", StringComparison.OrdinalIgnoreCase), node.Service);
                Check("its driver package is the inbox xusb22.inf", string.Equals(node.Inf, "xusb22.inf", StringComparison.OrdinalIgnoreCase), node.Inf);
                Check(@"it carries the hardware ID USB\VID_045E&PID_028E and the compatible ID USB\MS_COMP_XUSB10",
                      node.HardwareIds.Contains(@"USB\VID_045E&PID_028E", StringComparer.OrdinalIgnoreCase)
                      && node.CompatibleIds.Contains(@"USB\MS_COMP_XUSB10", StringComparer.OrdinalIgnoreCase));
                Check("it sits under the USB/IP host controller's root hub",
                      node.ParentHardwareIds.Any(h => string.Equals(h, @"ROOT\USBIP_WIN2\UDE", StringComparison.OrdinalIgnoreCase)),
                      string.Join(" | ", node.ParentHardwareIds));
            }
            string sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "xusb22.sys");
            using (var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\xusb22"))
                Check("xusb22.sys and the xusb22 service exist", File.Exists(sys) && service != null,
                      $"{sys} {(File.Exists(sys) ? "exists" : "missing")}, service {(service != null ? "exists" : "missing")}");

            Console.WriteLine("\n-- XInput --");
            // The pad's slot: the one that reads the state submitted here.
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
            }, 20_000);
            Check("XInputGetState returns SUCCESS on a slot that reads the submitted A and left stick", slot >= 0,
                  slot >= 0 ? $"slot {slot}, {clock.ElapsedMilliseconds} ms after the create began" : $"connected now: {Slots(ConnectedSlots())}");
            if (slot >= 0)
            {
                uint s = (uint)slot;
                Check("the slot was free before the create", (before & (1u << slot)) == 0, Slots(before));
                if (XInputGetCapabilities(s, 0, out var caps) == 0)
                    Console.WriteLine($"  capabilities: type {caps.Type} subtype {caps.SubType} flags 0x{caps.Flags:X4} buttons 0x{caps.Gamepad.wButtons:X4} " +
                                      $"motors {caps.Vibration.wLeftMotorSpeed}/{caps.Vibration.wRightMotorSpeed}");
                Check("XInputGetCapabilities reads a gamepad", XInputGetCapabilities(s, 0, out caps) == 0 && caps.Type == 1 && caps.SubType == 1,
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
                // SubmitRawReport: the frame is the 20-byte report as the
                // device sends it. B alone.
                byte[] rawFrame = { 0x00, 0x14, 0x00, 0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                XINPUT_STATE rawState = default;
                bool rawRead = WaitUntil(() =>
                {
                    pad.SubmitRawReport(rawFrame);
                    return XInputGetState(s, out rawState) == 0 && rawState.Gamepad.wButtons == 0x2000;
                }, 3_000);
                Check("a raw 20-byte report reads through XInput: B", rawRead, $"buttons 0x{rawState.Gamepad.wButtons:X4}");
                Reads(pad, s, "and SubmitState takes over again, at rest", new HMGamepadState(), g => g.wButtons == 0);

                Console.WriteLine("\n-- rumble and the LED, at the device's OUT endpoint --");
                // The slot's LED request, which Windows sends when the pad
                // takes its slot.
                bool ledSeen = WaitUntil(() => { lock (raw) return raw.Any(o => o.Id == 0x01); }, 5_000);
                lock (raw)
                    foreach (var o in raw.Where(o => o.Id == 0x01))
                        Console.WriteLine($"  OUT 0x02: 01 {Hex(o.Data)}");
                string ledDecoded;
                lock (decoded) ledDecoded = string.Join(", ", decoded.Where(d => d.Id == 0x01).Select(d => d.Fields));
                Check("the LED request for the slot arrives and decodes as ledPattern", ledSeen && ledDecoded.Contains("ledPattern=", StringComparison.Ordinal), ledDecoded);

                Rumble(s, 0x4000, 0x8000, raw, decoded, 0x40, 0x80);
                Rumble(s, 0xFFFF, 0x0100, raw, decoded, 0xFF, 0x01);
                Rumble(s, 0, 0, raw, decoded, 0x00, 0x00);
            }

            Console.WriteLine("\n-- the control requests Windows sent the pad --");
            string busId = HIDMaestro.Internal.Usbip.UsbipEmulatedDevice.BusIdFor(pad.TransportIndex);
            lock (controls)
                foreach (string line in controls.Where(c => c.Contains($" {busId}:", StringComparison.Ordinal)))
                    Console.WriteLine("  " + line);

            // A second pad of the profile: its own serial, devnode and slot.
            Console.WriteLine("\n-- a second pad --");
            second = ctx.CreateController(profile);
            string secondInstance = $@"USB\VID_045E&PID_028E\{DeviceIdentity.Resolve(second.IdentityKey, second.TransportIndex).SyntheticSerial}";
            bool secondStarted = WaitUntil(() => Node(secondInstance) is { Started: true, Problem: 0 }, 30_000);
            Check($"{secondInstance} is present and started under xusb22",
                  secondStarted && secondInstance != instance && string.Equals(Node(secondInstance)?.Service, "xusb22", StringComparison.OrdinalIgnoreCase),
                  Node(secondInstance) is { } sn ? $"service {sn.Service}, problem {sn.Problem}" : "not present");
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
                      secondSlot >= 0 && first.Gamepad.wButtons == 0x4000,
                      $"first slot {slot} buttons 0x{first.Gamepad.wButtons:X4}, second slot {secondSlot}");
                var atRest = new HMGamepadState();
                pad.SubmitState(in atRest);
            }
            second.Dispose();
            second = null;
            Check("the second pad's devnode is gone after its Dispose, and the first stays started",
                  WaitUntil(() => Node(secondInstance) == null, 30_000) && Node(instance) is { Started: true });

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
            bool gone = WaitUntil(() => Node(instance) == null && Node(controlInstance) == null, 30_000);
            Check("both devnodes are gone after Dispose", gone,
                  $"{(Node(instance) == null ? "pad gone" : "pad present")}, {(Node(controlInstance) == null ? "control gone" : "control present")}");
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

    /// <summary>XInputSetState, then the report at the device's OUT
    /// endpoint, raw and decoded. XInput's 16-bit speeds reach the device as
    /// their high bytes.</summary>
    static void Rumble(uint slot, ushort left, ushort right, List<(byte Id, byte[] Data)> raw, List<(byte Id, string Fields)> decoded, byte wantLeft, byte wantRight)
    {
        int rawFrom, decodedFrom;
        lock (raw) rawFrom = raw.Count;
        lock (decoded) decodedFrom = decoded.Count;
        var v = new XINPUT_VIBRATION { wLeftMotorSpeed = left, wRightMotorSpeed = right };
        uint code = XInputSetState(slot, ref v);
        byte[] want = { 0x08, 0x00, wantLeft, wantRight, 0x00, 0x00, 0x00 };
        byte[]? got = null;
        bool arrived = WaitUntil(() =>
        {
            lock (raw) got = raw.Skip(rawFrom).LastOrDefault(o => o.Id == 0x00).Data;
            return got != null && got.AsSpan().SequenceEqual(want);
        }, 3_000);
        Console.WriteLine($"  XInputSetState({left}, {right}) returned {code}. OUT 0x02: 00 {(got == null ? "(nothing)" : Hex(got))}");
        Check($"XInputSetState({left}, {right}) arrives as 00 08 00 {wantLeft:X2} {wantRight:X2} 00 00 00", code == 0 && arrived);
        string fields = "";
        bool decodedOk = WaitUntil(() =>
        {
            lock (decoded) fields = decoded.Skip(decodedFrom).LastOrDefault(d => d.Id == 0x00).Fields ?? "";
            var each = fields.Split(' ');
            return each.Contains($"leftMotor={wantLeft}") && each.Contains($"rightMotor={wantRight}");
        }, 3_000);
        Check($"and decodes as leftMotor {wantLeft}, rightMotor {wantRight}", decodedOk, fields);
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
