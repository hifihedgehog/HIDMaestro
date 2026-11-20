// bluetooth_hid_check, the live part: each profile created on the USB/IP
// backend, one at a time, and read through the device tree, HID, XInput and
// Windows.Gaming.Input, then a second create on the same record and a plain
// HID profile on it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static partial class Program
{
    static readonly Guid s_hidInterface = new("4d1e55b2-f16f-11cf-88cb-001111000030");
    static readonly Guid s_xusbInterface = new("ec87f1e3-c13b-4100-b5f7-8b84d54260cb");

    static int Live(string[] ids)
    {
        Console.WriteLine("=== live: each Bluetooth pad's HID form on USB/IP ===");
        using (var who = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(who).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("  refused: live needs elevation");
                return 3;
            }
        if (SdkConsumers.Find() is { } other)
        {
            Console.WriteLine($"  SKIP: {other} has HIDMaestro.Core.dll loaded, and two programs that embed HIDMaestro never test at once.");
            return 2;
        }
        Console.WriteLine($"  Windows {Environment.OSVersion.Version}, {RuntimeInformation.OSArchitecture}, session {Process.GetCurrentProcess().SessionId}");
        var pads = ids.Length == 0 ? Pads : Pads.Where(p => ids.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (pads.Length == 0) { Console.WriteLine("  no such profile"); return 2; }
        using var ctx = new HMContext(HMBackend.Usbip);
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        WgiPins.Listen();
        foreach (var pad in pads) One(ctx, pad);
        return 0;
    }

    /// <summary>The Xbox button pressed and released <paramref name="presses"/>
    /// times on one device. A pad that sends the button in a report of its
    /// own sends that report once per change, so one lost report is one
    /// press XInput never reads.</summary>
    /// <param name="polled">Another thread reads XInput's four slots every
    /// millisecond from before the create, as a running game does. XInput
    /// then opens the pad's collection the moment it appears, which on a
    /// machine that installs the collection for the first time is during
    /// that install, the case the create's wait plugs the device in again
    /// for.</param>
    static int Guide(string id, int presses, bool polled = false)
    {
        Console.WriteLine($"=== {(polled ? "polled" : "guide")}: {id}, the Xbox button {presses} times{(polled ? ", XInput read from before the create" : "")} ===");
        bool stopPolling = false;
        if (polled)
        {
            new Thread(() =>
            {
                while (!Volatile.Read(ref stopPolling))
                {
                    for (uint i = 0; i < 4; i++) XInputGetStateEx(i, out _);
                    Thread.Sleep(1);
                }
            }) { IsBackground = true }.Start();
        }
        try { return GuideRun(id, presses); }
        finally { Volatile.Write(ref stopPolling, true); }
    }

    static int GuideRun(string id, int presses)
    {
        using (var who = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(who).IsInRole(WindowsBuiltInRole.Administrator)) { Console.WriteLine("  refused: guide needs elevation"); return 3; }
        if (SdkConsumers.Find() is { } other) { Console.WriteLine($"  SKIP: {other} has HIDMaestro.Core.dll loaded."); return 2; }
        var pad = Pads.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (pad == null) { Console.WriteLine("  no such profile"); return 2; }
        using var ctx = new HMContext(HMBackend.Usbip);
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        uint before = ConnectedSlots();
        using var c = ctx.CreateController(ctx.GetProfile(pad.Id)!, null, HMBackend.Usbip);
        uint slot = Slot(pad, before);
        if (slot > 3) return 0;
        var down = new HMGamepadState { Buttons = HMButton.Guide };
        var up = new HMGamepadState();
        int missedDown = 0, missedUp = 0;
        long worst = 0;
        for (int i = 0; i < presses; i++)
        {
            var clock = Stopwatch.StartNew();
            if (!WaitUntil(() => { c.SubmitState(in down); return XInputGetStateEx(slot, out var st) == 0 && (st.Gamepad.wButtons & 0x0400) != 0; }, 1_500)) missedDown++;
            worst = Math.Max(worst, clock.ElapsedMilliseconds);
            if (!WaitUntil(() => { c.SubmitState(in up); return XInputGetStateEx(slot, out var st) == 0 && (st.Gamepad.wButtons & 0x0400) == 0; }, 1_500)) missedUp++;
        }
        Check($"{pad.Id}: XInput read each of {presses} presses of the Xbox button and each release", missedDown == 0 && missedUp == 0,
              $"{missedDown} presses and {missedUp} releases not read, slowest press {worst} ms");
        return 0;
    }

    /// <summary>The Xbox button held: XInput reads it down through further
    /// submits of the same state and of a moved stick, and up after the
    /// release. xinputhid holds the button of a pad that sends it in a
    /// report of its own until the pad's next layout report, so the device
    /// reports on change and sends that report again after each layout
    /// report while the button is down.</summary>
    static int Hold(string id)
    {
        Console.WriteLine($"=== hold: {id}, the Xbox button held ===");
        using (var who = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(who).IsInRole(WindowsBuiltInRole.Administrator)) { Console.WriteLine("  refused: hold needs elevation"); return 3; }
        if (SdkConsumers.Find() is { } other) { Console.WriteLine($"  SKIP: {other} has HIDMaestro.Core.dll loaded."); return 2; }
        var pad = Pads.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (pad == null) { Console.WriteLine("  no such profile"); return 2; }
        using var ctx = new HMContext(HMBackend.Usbip);
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        uint before = ConnectedSlots();
        var profile = ctx.GetProfile(pad.Id)!;
        using var c = ctx.CreateController(profile, null, HMBackend.Usbip);
        uint slot = Slot(pad, before);
        if (slot > 3) return 0;
        // How many of a run of reads 10 ms apart have the button down. The
        // first reads of a run can come before the report lands.
        int Held(int reads)
        {
            int held = 0;
            for (int i = 0; i < reads; i++)
            {
                if (XInputGetStateEx(slot, out var st) == 0 && (st.Gamepad.wButtons & 0x0400) != 0) held++;
                Thread.Sleep(10);
            }
            return held;
        }
        var up = new HMGamepadState();
        var down = new HMGamepadState { Buttons = HMButton.Guide };
        c.SubmitState(in up);
        int rest = Held(20);
        Check($"{pad.Id}: at rest the Xbox button reads up", rest == 0, $"{rest} of 20 reads down");
        c.SubmitState(in down);
        int once = Held(50);
        Check($"{pad.Id}: one submit with the button down, and it reads down from then on", once >= 46, $"{once} of 50 reads down");
        for (int i = 0; i < 6; i++) c.SubmitState(in down);
        int again = Held(40);
        Check($"{pad.Id}: the same state submitted six times more, and it still reads down", again >= 38, $"{again} of 40 reads down");
        var moved = new HMGamepadState { Buttons = HMButton.Guide, Axes = new Dictionary<HMAxis, float> { [profile.Sticks[0].XAxis] = 0.9f } };
        c.SubmitState(in moved);
        int withStick = Held(40);
        Check($"{pad.Id}: the left stick moved with the button down, and it still reads down", withStick >= 36, $"{withStick} of 40 reads down");
        c.SubmitState(in up);
        Thread.Sleep(60);
        int released = Held(20);
        Check($"{pad.Id}: released, it reads up", released == 0, $"{released} of 20 reads down");
        return 0;
    }

    static void One(HMContext ctx, Pad pad)
    {
        Console.WriteLine($"\n--- {pad.Id} ---");
        var profile = ctx.GetProfile(pad.Id)!;
        uint before = ConnectedSlots();
        if (before == 0xF) { Check($"{pad.Id}: an XInput slot is free", false, "all four are taken"); return; }
        string usb = "";
        string? plainDir = null;
        try
        {
            // 1. The first create, on whatever record the machine holds.
            var clock = Stopwatch.StartNew();
            using (var c = ctx.CreateController(profile, null, HMBackend.Usbip))
            {
                long createMs = clock.ElapsedMilliseconds;
                usb = $@"USB\VID_045E&PID_{pad.Pid:X4}\{DeviceIdentity.Resolve(c.IdentityKey, c.TransportIndex).SyntheticSerial}";
                Console.WriteLine($"  created in {createMs} ms as {usb}");
                Tree(pad, usb, wantXinputhid: true);
                uint slot = Slot(pad, before);
                if (slot < 4)
                {
                    Controls(pad, c, slot);
                    Identity(pad, slot);
                    Rumble(pad, c, slot);
                }
            }
            WaitUntil(() => ConnectedSlots() == before, 5_000);
            Check($"{pad.Id}: the slot is free after the dispose", ConnectedSlots() == before, $"slots {Slots(ConnectedSlots())}");

            // 2. A second create finds the values on the record: no refused
            // start, no restart.
            clock.Restart();
            using (var c = ctx.CreateController(profile, null, HMBackend.Usbip))
            {
                long ms = clock.ElapsedMilliseconds;
                Console.WriteLine($"  created again in {ms} ms");
                Tree(pad, usb, wantXinputhid: true, brief: true);
                Check($"{pad.Id}: the second create takes an XInput slot", Slot(pad, before, quiet: true) < 4);
            }
            WaitUntil(() => ConnectedSlots() == before, 5_000);

            // 3. A plain HID profile with the same VID, PID and serial runs on
            // the same record: the values come off it, and the pad is a plain
            // HID gamepad with no XInput slot.
            plainDir = Path.Combine(Path.GetTempPath(), $"hm-bthid-plain-{Environment.ProcessId}-{pad.Pid:X4}");
            Directory.CreateDirectory(plainDir);
            var plain = new JsonObject
            {
                ["id"] = $"bthid-plain-{pad.Id}", ["name"] = "Plain HID pad", ["vendor"] = "Microsoft",
                ["vid"] = "0x045E", ["pid"] = $"0x{pad.Pid:X4}", ["productString"] = "Plain HID pad", ["manufacturerString"] = "Microsoft",
                ["type"] = "gamepad", ["connection"] = "usb", ["descriptor"] = profile.Inner.BluetoothHid!.Descriptor, ["xinput"] = new JsonObject(),
            };
            File.WriteAllText(Path.Combine(plainDir, "plain.json"), plain.ToJsonString());
            ctx.LoadProfilesFromDirectory(plainDir);
            var plainProfile = ctx.GetProfile($"bthid-plain-{pad.Id}");
            if (plainProfile == null) Check($"{pad.Id}: the plain copy loads", false, Issues(ctx, $"bthid-plain-{pad.Id}"));
            else
            {
                using (var c = ctx.CreateController(plainProfile, null, HMBackend.Usbip))
                {
                    Tree(pad, usb, wantXinputhid: false, brief: true);
                    Thread.Sleep(600);
                    Check($"{pad.Id}: the plain profile on the same record takes no XInput slot", ConnectedSlots() == before, $"slots {Slots(ConnectedSlots())}");
                }
                // 4. The pad again: the values go back on.
                using (var c = ctx.CreateController(profile, null, HMBackend.Usbip))
                {
                    Tree(pad, usb, wantXinputhid: true, brief: true);
                    Check($"{pad.Id}: after the plain profile the pad takes an XInput slot again", Slot(pad, before, quiet: true) < 4);
                }
            }
        }
        catch (Exception e)
        {
            Check($"{pad.Id}: created and read", false, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            if (plainDir != null) try { Directory.Delete(plainDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ── The device tree ──────────────────────────────────────────────────

    static void Tree(Pad pad, string usb, bool wantXinputhid, bool brief = false)
    {
        // The pad's game controller collection: the one with the XUSB
        // interface under xinputhid, the first one without it. A pad's
        // descriptor can declare more, as the Elite's declares a keyboard.
        string? child = null;
        var others = new List<string>();
        WaitUntil(() =>
        {
            var hids = DeviceManager.GetChildIds(usb).Where(c => c.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)).ToList();
            child = wantXinputhid ? hids.FirstOrDefault(c => VhciClient.HasPresentInterface(c, s_xusbInterface)) ?? hids.FirstOrDefault() : hids.FirstOrDefault();
            others = hids.Where(c => c != child).ToList();
            return child != null && child.Contains("&IG_", StringComparison.OrdinalIgnoreCase) == wantXinputhid
                   && VhciClient.HasPresentInterface(child, s_hidInterface);
        }, 8_000);
        if (!brief)
            foreach (var o in others)
            {
                var (present, status, problem) = DeviceOrchestrator.DevnodeStatus(o);
                Console.WriteLine($"  [NOTE] {pad.Id}: another collection, {o}: {(present && (status & 8) != 0 ? "started" : $"not started, problem code {problem}")}");
            }
        string tag = wantXinputhid ? pad.Id : $"{pad.Id}, plain profile";
        bool? on = UsbNodeStamp.XinputhidOn(usb);
        Check($"{tag}: the node {(wantXinputhid ? "carries" : "does not carry")} xinputhid's filter name and flag value", on == wantXinputhid, $"{(on is { } v ? v.ToString() : "one without the other")}");
        using var node = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + usb);
        var compatible = node?.GetValue("CompatibleIDs") as string[] ?? Array.Empty<string>();
        string marker = pad.Le ? UsbNodeStamp.LeMarker : UsbNodeStamp.ClassicMarker;
        if (wantXinputhid)
            Check($"{tag}: the node's first compatible ID is the Bluetooth marker", compatible.Length > 0 && compatible[0] == marker, string.Join(" ; ", compatible));
        else
            Check($"{tag}: the node carries no Bluetooth marker", !compatible.Contains(UsbNodeStamp.LeMarker) && !compatible.Contains(UsbNodeStamp.ClassicMarker), string.Join(" ; ", compatible));
        Check($"{tag}: the game controller collection's path {(wantXinputhid ? "carries" : "carries no")} &IG_",
              child != null && child.Contains("&IG_", StringComparison.OrdinalIgnoreCase) == wantXinputhid, child ?? "no HID child");
        if (child == null) return;
        Check($"{tag}: the XUSB interface {(wantXinputhid ? "is" : "is not")} on the collection",
              VhciClient.HasPresentInterface(child, s_xusbInterface) == wantXinputhid);
        if (brief) return;
        using var hid = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + child);
        string? service = hid?.GetValue("Service") as string;
        string? driverKey = hid?.GetValue("Driver") as string;
        using var driver = driverKey == null ? null : Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driverKey);
        Check($"{tag}: the collection is input.inf's raw game controller, with no filter of its own",
              string.IsNullOrEmpty(service) && hid?.GetValue("UpperFilters") == null
              && string.Equals(driver?.GetValue("InfPath") as string, "input.inf", StringComparison.OrdinalIgnoreCase)
              && string.Equals(driver?.GetValue("MatchingDeviceId") as string, "HID_DEVICE_SYSTEM_GAME", StringComparison.OrdinalIgnoreCase),
              $"service '{service}', inf {driver?.GetValue("InfPath")}, matched {driver?.GetValue("MatchingDeviceId")}");
        string path = @"\\?\" + child.Replace('\\', '#') + "#{" + s_hidInterface + "}";
        using var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        bool read = !handle.IsInvalid && HidD_GetAttributes(handle, ref attributes);
        ushort version = pad.Version ?? 0x0100;
        Check($"{tag}: HID attributes 045E:{pad.Pid:X4} version {version:X4}",
              read && attributes.VendorID == 0x045E && attributes.ProductID == pad.Pid && attributes.VersionNumber == version,
              read ? $"{attributes.VendorID:X4}:{attributes.ProductID:X4} version {attributes.VersionNumber:X4}" : $"open or read failed, error {Marshal.GetLastWin32Error()}");
    }

    // ── XInput ───────────────────────────────────────────────────────────

    static uint Slot(Pad pad, uint before, bool quiet = false)
    {
        uint slot = 4;
        WaitUntil(() =>
        {
            uint now = ConnectedSlots() & ~before;
            for (uint i = 0; i < 4; i++) if ((now & (1u << (int)i)) != 0) { slot = i; return true; }
            return false;
        }, 5_000);
        if (!quiet) Check($"{pad.Id}: XInput lists one slot more", slot < 4, $"slots before {Slots(before)}, now {Slots(ConnectedSlots())}");
        return slot;
    }

    static void Controls(Pad pad, HMController c, uint slot)
    {
        var profile = c.Profile;
        void Reads(string name, HMGamepadState state, Func<XINPUT_GAMEPAD, bool> want, bool extended = false)
        {
            XINPUT_STATE last = default;
            uint code = 0;
            bool ok = WaitUntil(() =>
            {
                c.SubmitState(in state);
                code = extended ? XInputGetStateEx(slot, out last) : XInputGetState(slot, out last);
                return code == 0 && want(last.Gamepad);
            }, 3_000);
            var g = last.Gamepad;
            Check($"{pad.Id}: {name}", ok, $"code {code} buttons 0x{g.wButtons:X4} LT {g.bLeftTrigger} RT {g.bRightTrigger} L {g.sThumbLX},{g.sThumbLY} R {g.sThumbRX},{g.sThumbRY}");
        }
        bool Rest(XINPUT_GAMEPAD g) => g.bLeftTrigger == 0 && g.bRightTrigger == 0 && Math.Abs((int)g.sThumbLX) < 600 && Math.Abs((int)g.sThumbLY) < 600
                                       && Math.Abs((int)g.sThumbRX) < 600 && Math.Abs((int)g.sThumbRY) < 600;
        Reads("at rest XInput reads no button, centered sticks and released triggers", default, g => g.wButtons == 0 && Rest(g));
        foreach (var (button, mask) in new (HMButton, ushort)[] {
                     (HMButton.A, 0x1000), (HMButton.B, 0x2000), (HMButton.X, 0x4000), (HMButton.Y, 0x8000),
                     (HMButton.LeftBumper, 0x0100), (HMButton.RightBumper, 0x0200), (HMButton.Back, 0x0020), (HMButton.Start, 0x0010),
                     (HMButton.LeftStick, 0x0040), (HMButton.RightStick, 0x0080) })
            Reads($"{button} alone is 0x{mask:X4} in XInput", new HMGamepadState { Buttons = button }, g => g.wButtons == mask && Rest(g));
        Reads("the Xbox button is 0x0400 through XInputGetStateEx", new HMGamepadState { Buttons = HMButton.Guide }, g => g.wButtons == 0x0400, extended: true);
        Reads("the Xbox button released", default, g => g.wButtons == 0, extended: true);
        foreach (var (hat, mask) in new (HMHat, ushort)[] { (HMHat.North, 0x0001), (HMHat.South, 0x0002), (HMHat.West, 0x0004), (HMHat.East, 0x0008), (HMHat.NorthEast, 0x0009) })
            Reads($"the D-pad {hat} is 0x{mask:X4}", new HMGamepadState { Hat = hat }, g => g.wButtons == mask);
        var sticks = profile.Sticks; var triggers = profile.Triggers;
        if (sticks.Count < 2 || triggers.Count < 2) { Check($"{pad.Id}: the profile declares two sticks and two triggers", false); return; }
        HMGamepadState Axes(params (HMAxis Axis, float Value)[] set)
        {
            var d = new Dictionary<HMAxis, float>();
            foreach (var (axis, value) in set) d[axis] = value;
            return new HMGamepadState { Axes = d };
        }
        Reads("the left trigger alone, full", Axes((triggers[0].Axis, 1f)), g => g.bLeftTrigger == 255 && g.bRightTrigger == 0);
        Reads("the right trigger alone, full", Axes((triggers[1].Axis, 1f)), g => g.bRightTrigger == 255 && g.bLeftTrigger == 0);
        Reads("the left stick full right", Axes((sticks[0].XAxis, 1f)), g => g.sThumbLX > 32000 && Math.Abs((int)g.sThumbLY) < 600 && Math.Abs((int)g.sThumbRX) < 600);
        Reads("the left stick full up", Axes((sticks[0].YAxis, 0f)), g => g.sThumbLY > 32000 && Math.Abs((int)g.sThumbLX) < 600);
        Reads("the right stick full left", Axes((sticks[1].XAxis, 0f)), g => g.sThumbRX < -32000 && Math.Abs((int)g.sThumbRY) < 600 && Math.Abs((int)g.sThumbLX) < 600);
        Reads("the right stick full down", Axes((sticks[1].YAxis, 1f)), g => g.sThumbRY < -32000 && Math.Abs((int)g.sThumbRX) < 600);
        Reads("back at rest", default, g => g.wButtons == 0 && Rest(g));
    }

    static void Identity(Pad pad, uint slot)
    {
        ushort version = pad.Version ?? 0x0100;
        bool ex = XInputGetCapabilitiesEx(1, slot, 0, out var caps) == 0;
        Check($"{pad.Id}: XInput reports 045E:{pad.Pid:X4} revision {version:X4}, a wireless gamepad",
              ex && caps.Vid == 0x045E && caps.Pid == pad.Pid && caps.Rev == version && (caps.Caps.Flags & 0x0002) != 0 && caps.Caps.Type == 1 && caps.Caps.SubType == 1,
              ex ? $"{caps.Vid:X4}:{caps.Pid:X4} revision {caps.Rev:X4} flags 0x{caps.Caps.Flags:X4} type {caps.Caps.Type} subtype {caps.Caps.SubType}" : "no extended capabilities");
        Windows.Gaming.Input.RawGameController? raw = null;
        int count = 0;
        WaitUntil(() =>
        {
            var mine = WgiPins.Raws().Where(r => r.HardwareVendorId == 0x045E && r.HardwareProductId == pad.Pid).ToList();
            count = mine.Count;
            raw = mine.FirstOrDefault();
            return raw != null && WgiPins.PadOf(raw) != null;
        }, 6_000);
        Check($"{pad.Id}: Windows.Gaming.Input lists the pad once", count == 1, $"{count}");
        if (raw == null) return;
        // The name, wireless and the counts are what the owner's Series pad
        // read over Bluetooth on 2026-04-21: "HID-compliant game controller",
        // IsWireless True, 14 buttons, 6 axes, 0 switches.
        Check($"{pad.Id}: it is \"HID-compliant game controller\", wireless, a Gamepad, 14 buttons, 6 axes, 0 switches",
              raw.DisplayName == "HID-compliant game controller" && raw.IsWireless && WgiPins.PadOf(raw) != null
              && raw.ButtonCount == 14 && raw.AxisCount == 6 && raw.SwitchCount == 0,
              $"\"{raw.DisplayName}\" wireless {raw.IsWireless} gamepad {WgiPins.PadOf(raw) != null} buttons {raw.ButtonCount} axes {raw.AxisCount} switches {raw.SwitchCount}");
    }

    static void Rumble(Pad pad, HMController c, uint slot)
    {
        var packets = new List<HMOutputPacket>();
        void On(HMController _, HMOutputPacket p) { lock (packets) packets.Add(new HMOutputPacket(p.Source, p.ReportId, p.Data.ToArray(), p.SeqNo)); }
        c.OutputReceived += On;
        try
        {
            var on = new XINPUT_VIBRATION { wLeftMotorSpeed = 40000, wRightMotorSpeed = 20000 };
            uint code = XInputSetState(slot, ref on);
            HMOutputPacket? hit = null;
            // The packet a program reads on the UMDF2 form: the motor mask as
            // the packet's ID, then the trigger motors, the left and right
            // motors as percentages, and the duration, delay and repeat bytes.
            WaitUntil(() =>
            {
                lock (packets)
                    foreach (var p in packets)
                        if (p.Source == HMOutputSource.HidOutput && p.Data.Length == 7 && p.Data.Span[2] is >= 60 and <= 62 && p.Data.Span[3] is >= 29 and <= 31) { hit = p; return true; }
                return false;
            }, 3_000);
            string seen;
            lock (packets) seen = string.Join(" | ", packets.TakeLast(3).Select(p => $"{p.Source} id 0x{p.ReportId:X2} {Convert.ToHexString(p.Data.Span)}"));
            Check($"{pad.Id}: XInputSetState 40000, 20000 reaches the program as ID 0x0F and 7 bytes with motors 61 and 30",
                  code == 0 && hit is { ReportId: 0x0F }, $"code {code}; {seen}");
            var off = new XINPUT_VIBRATION();
            XInputSetState(slot, ref off);
        }
        finally { c.OutputReceived -= On; }
    }

    static bool WaitUntil(Func<bool> done, int ms)
    {
        var clock = Stopwatch.StartNew();
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

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_VIBRATION { public ushort wLeftMotorSpeed, wRightMotorSpeed; }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_CAPABILITIES { public byte Type, SubType; public ushort Flags; public XINPUT_GAMEPAD Gamepad; public XINPUT_VIBRATION Vibration; }

    // XInputGetCapabilitiesEx's answer: the capabilities, then the device's
    // VID, PID and revision (OpenXInput, XInputGetCapabilitiesEx).
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_CAPABILITIES_EX { public XINPUT_CAPABILITIES Caps; public ushort Vid, Pid, Rev; public uint Unknown; }

    [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint index, out XINPUT_STATE state);
    // Ordinal 100: XInputGetState with the Guide button in wButtons.
    [DllImport("xinput1_4.dll", EntryPoint = "#100")] static extern uint XInputGetStateEx(uint index, out XINPUT_STATE state);
    [DllImport("xinput1_4.dll")] static extern uint XInputSetState(uint index, ref XINPUT_VIBRATION vibration);
    // Ordinal 108.
    [DllImport("xinput1_4.dll", EntryPoint = "#108")] static extern uint XInputGetCapabilitiesEx(uint one, uint index, uint flags, out XINPUT_CAPABILITIES_EX capabilities);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HIDD_ATTRIBUTES attributes);
}
