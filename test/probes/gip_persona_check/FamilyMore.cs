// gip_persona_check, five more live parts of the GIP function on USB/IP:
//   nodriver  A GIP device no INF binds (profiles/gip-no-driver.json): the
//             create fails and names the device and the compatible ID no
//             driver bound, and leaves no device.
//   pair      Two GIP pads at once, two identities side by side and two pads
//             of one identity: each its own device ID and XInput slot, each
//             reading its own state, each rumble at its own device.
//   restart   The host's driver stack restarted on the pad's USB device with
//             no port reset (the device disabled and enabled): the pad comes
//             back in XInput and reads again.
//   reuse     One pad created again under a key used before and under
//             fresh keys: each create's XInput read and rumble, and the
//             HID child instance Windows gives it.
//   guide     XInput reads around two Guide presses, in a session with a
//             console user, beside the Game Bar processes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;

internal static unsafe partial class Program
{
    /// <summary>Null when a live part may run here, else the exit code.</summary>
    static int? LiveGuard()
    {
        using (var who = System.Security.Principal.WindowsIdentity.GetCurrent())
            if (!new System.Security.Principal.WindowsPrincipal(who).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Check("the live part runs elevated", false, "creating a controller needs elevation");
                return 0;
            }
        if (SdkConsumers.Find() is { } other)
        {
            Console.WriteLine($"  SKIP: {other} has HIDMaestro.Core.dll loaded, and two programs that embed HIDMaestro never test at once.");
            return 2;
        }
        return null;
    }

    static int NoDriver()
    {
        Console.WriteLine("\n=== nodriver: a GIP device no INF binds ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.LoadProfilesFromDirectory(Path.Combine(AppContext.BaseDirectory, "profiles"));
        var profile = ctx.GetProfile("gip-no-driver");
        Check("the test profile loads", profile != null);
        if (profile == null) return 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        HMController? pad = null;
        Exception? thrown = null;
        string? instance = null;
        try { pad = ctx.CreateController(profile); }
        catch (Exception e) { thrown = e; }
        long ms = clock.ElapsedMilliseconds;
        try
        {
            if (pad != null)
            {
                string serial = DeviceIdentity.Resolve(pad.IdentityKey, pad.TransportIndex).SyntheticSerial;
                instance = $@"USB\VID_1209&PID_0001\{serial}";
                var node = Node(instance);
                Console.WriteLine($"  the create returned a controller after {ms} ms: {instance} problem {node?.Problem}, slots {Slots(ConnectedSlots())}");
            }
            Check("the create fails, after the start wait, with an exception that names the device and XGIP99",
                  thrown is InvalidOperationException && thrown.Message.Contains("VID_1209&PID_0001", StringComparison.OrdinalIgnoreCase)
                  && thrown.Message.Contains("XGIP99", StringComparison.Ordinal),
                  thrown == null ? $"no exception in {ms} ms" : $"{thrown.GetType().Name} after {ms} ms: {thrown.Message}");
        }
        finally { try { pad?.Dispose(); } catch { } }
        if (thrown != null && instance == null)
        {
            var m = System.Text.RegularExpressions.Regex.Match(thrown.Message, @"USB\\VID_1209&PID_0001\\\w+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            instance = m.Success ? m.Value : null;
        }
        if (instance != null)
            Check("the device is gone after the create's failure", WaitUntil(() => Node(instance) == null, 30_000), instance);
        Console.WriteLine($"  not-present USB records this run leaves: {instance ?? "(none named)"}");
        return 0;
    }

    sealed record Pad(string Id, HMController Controller, string Instance, ulong DeviceId, HIDMaestro.Internal.Usbip.ProtocolEngine Engine);

    static Pad OpenPad(HMContext ctx, string id, string key)
    {
        var profile = ctx.GetProfile(id) ?? throw new ArgumentException($"no profile {id}");
        var c = ctx.CreateController(profile, key, HMBackend.Usbip);
        string serial = DeviceIdentity.Resolve(c.IdentityKey, c.TransportIndex).SyntheticSerial;
        return new Pad(id, c, $@"USB\VID_{profile.VendorId:X4}&PID_{profile.ProductId:X4}\{serial}",
                       BitConverter.ToUInt64(ExpectedDeviceId(serial), 0), c.UsbipHandle!.Device.Protocol!);
    }

    /// <summary>The XInput slot that reads <paramref name="pad"/>'s
    /// submitted marker button and no other pad's, or -1.</summary>
    static int SlotOf(Pad pad, HMButton marker, ushort bit, uint exclude)
    {
        int slot = -1;
        var state = new HMGamepadState { Buttons = marker };
        WaitUntil(() =>
        {
            pad.Controller.SubmitState(in state);
            for (uint i = 0; i < 4; i++)
                if ((exclude & (1u << (int)i)) == 0 && XInputGetState(i, out var s) == 0 && s.Gamepad.wButtons == bit) { slot = (int)i; return true; }
            return false;
        }, 15_000);
        var rest = new HMGamepadState();
        pad.Controller.SubmitState(in rest);
        return slot;
    }

    static int Pair()
    {
        Console.WriteLine("\n=== pair: two GIP pads at once ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        foreach (var (a, b) in new[] { ("xbox-series-xs-bt", "xbox-one-s"), ("xbox-series-xs-bt", "xbox-series-xs-bt") })
        {
            Console.WriteLine($"\n-- {a} and {b} --");
            uint before = ConnectedSlots();
            Pad? p1 = null, p2 = null;
            try
            {
                p1 = OpenPad(ctx, a, "gippair:1");
                p2 = OpenPad(ctx, b, "gippair:2");
                Check($"{a} and {b}: two devices, two device IDs", p1.Instance != p2.Instance && p1.DeviceId != p2.DeviceId,
                      $"{p1.Instance} {p1.DeviceId:X16}, {p2.Instance} {p2.DeviceId:X16}");
                var held = GipCapture(4_000);
                Check($"{a} and {b}: xboxgip holds an arrival for each device ID",
                      held.Any(m => m.Command == 0x02 && m.DeviceId == p1.DeviceId) && held.Any(m => m.Command == 0x02 && m.DeviceId == p2.DeviceId),
                      string.Join(" ", held.Where(m => m.Command == 0x02).Select(m => m.DeviceId.ToString("X16"))));
                int s1 = SlotOf(p1, HMButton.A, 0x1000, before);
                int s2 = SlotOf(p2, HMButton.B, 0x2000, before | (s1 >= 0 ? 1u << s1 : 0));
                Check($"{a} and {b}: each reads its own submitted button in its own XInput slot", s1 >= 0 && s2 >= 0 && s1 != s2, $"slots {s1} and {s2}; {SlotStates()}");
                if (s1 >= 0 && s2 >= 0)
                {
                    // Each pad's state stays its own while the other's changes.
                    var y = new HMGamepadState { Buttons = HMButton.Y };
                    bool apart = WaitUntil(() =>
                    {
                        p1.Controller.SubmitState(in y);
                        return XInputGetState((uint)s1, out var g1) == 0 && g1.Gamepad.wButtons == 0x8000
                            && XInputGetState((uint)s2, out var g2) == 0 && g2.Gamepad.wButtons == 0;
                    }, 5_000);
                    p1.Controller.SubmitState(new HMGamepadState());
                    Check($"{a} and {b}: a button on the first reads on its slot and not on the second's", apart);
                    Motor(p2.Engine, $"{a} and {b}: XInputSetState(16384, 32768) on the second slot",
                          () => { var v = new XINPUT_VIBRATION { wLeftMotorSpeed = 16384, wRightMotorSpeed = 32768 }; return XInputSetState((uint)s2, ref v); },
                          c => c[8] is >= 24 and <= 26 && c[9] is >= 49 and <= 51);
                    var none = p1.Engine.SnapshotLog().Where(x => x.Command.Length >= 13 && x.Command[0] == 0x09 && x.Command[8] is >= 24 and <= 26).ToList();
                    Check($"{a} and {b}: and none of it at the first device", none.Count == 0, $"{none.Count} commands");
                    var off = new XINPUT_VIBRATION();
                    XInputSetState((uint)s2, ref off);
                }
            }
            catch (Exception e) { Check($"{a} and {b}: the part ran to its end", false, e.ToString()); }
            finally
            {
                try { p2?.Controller.Dispose(); } catch { }
                try { p1?.Controller.Dispose(); } catch { }
                foreach (var p in new[] { p1, p2 })
                    if (p != null) Check($"{p.Id}: its devnode is gone after Dispose", WaitUntil(() => Node(p.Instance) == null, 30_000), p.Instance);
            }
        }
        return 0;
    }

    // cfgmgr32.h: CM_REMOVE_UI_NOT_OK 0x1 and CM_REMOVE_DISABLE 0x4. The
    // query and the removal of the subtree, which leaves the device
    // disabled, name the program or driver that vetoes it.
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Query_And_Remove_SubTreeW(uint devInst, out int vetoType, System.Text.StringBuilder vetoName, int nameLength, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Disable_DevNode(uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Enable_DevNode(uint devInst, uint flags);

    static int Restart()
    {
        Console.WriteLine("\n=== restart: the driver stack restarted with no port reset ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        Pad? pad = null;
        try
        {
            uint before = ConnectedSlots();
            pad = OpenPad(ctx, "xbox-series-xs-bt", "giprestart:1");
            int slot = SlotOf(pad, HMButton.A, 0x1000, before);
            Check("the pad reads in XInput before the restart", slot >= 0, $"slot {slot}");
            int from = pad.Engine.SnapshotLog().Count;
            var veto = new System.Text.StringBuilder(260);
            int vetoType = 0;
            uint disabled = CM_Locate_DevNodeW(out uint dev, pad.Instance, 0) == 0 ? CM_Query_And_Remove_SubTreeW(dev, out vetoType, veto, veto.Capacity, 0x1 | 0x4) : 0xFFFF;
            // A handle open on the HID child vetoes the query (veto type 5,
            // PNP_VetoOutstandingOpen, measured on the Surface). The disable
            // then goes ahead without asking the drivers (CM_DISABLE_ABSOLUTE),
            // and the open handles see the device go.
            Console.WriteLine($"  the query and removal: CM_Query_And_Remove_SubTree {disabled}, veto type {vetoType}{(veto.Length > 0 ? $" by {veto}" : "")}");
            // Windows 10 19044 refuses CM_REMOVE_DISABLE as CR_INVALID_FLAG
            // (measured on the Celeron): the polite disable goes first there.
            if (disabled == 0x4 /* CR_INVALID_FLAG */)
                disabled = CM_Disable_DevNode(dev, 0x4 /* CM_DISABLE_UI_NOT_OK */);
            if (disabled == 0x17 /* CR_REMOVE_VETOED */)
                disabled = CM_Disable_DevNode(dev, 0x1 /* CM_DISABLE_ABSOLUTE */ | 0x4 /* CM_DISABLE_UI_NOT_OK */);
            Check("the USB device is disabled, which stops its driver stack and leaves the port alone", disabled == 0, $"{disabled}");
            bool stopped = WaitUntil(() => Node(pad.Instance) is { Started: false }, 10_000);
            Check("it stops", stopped, Node(pad.Instance) is { } n0 ? $"problem {n0.Problem}" : "gone");
            Check("its XInput slot goes", WaitUntil(() => slot < 0 || XInputGetState((uint)slot, out _) != 0, 10_000));
            uint enabled = CM_Locate_DevNodeW(out dev, pad.Instance, 0) == 0 ? CM_Enable_DevNode(dev, 0) : 0xFFFF;
            Check("it is enabled again", enabled == 0, $"CM_Enable_DevNode {enabled}");
            bool started = WaitUntil(() => Node(pad.Instance) is { Started: true, Problem: 0 }, 20_000);
            Check("it starts again under dc1-controller", started && Node(pad.Instance) is { } n1 && string.Equals(n1.Service, "dc1-controller", StringComparison.OrdinalIgnoreCase));
            int again = SlotOf(pad, HMButton.B, 0x2000, before);
            string tree = string.Join(", ", Descendants(pad.Instance).Select(d => $"{d.Id} started {d.Info.Started} problem {d.Info.Problem} {d.Info.Service}"));
            Console.WriteLine($"  the device's tree after the restart: {tree}");
            var log = pad.Engine.SnapshotLog().Skip(from).Select(x => Hex(x.Command)).ToList();
            Console.WriteLine($"  host messages after the restart: {string.Join(" ", log.Take(16))}");
            Check("the pad reads in XInput again", again >= 0, $"slot {again}");
            if (again >= 0)
                Motor(pad.Engine, "XInputSetState(16384, 32768) after the restart",
                      () => { var v = new XINPUT_VIBRATION { wLeftMotorSpeed = 16384, wRightMotorSpeed = 32768 }; return XInputSetState((uint)again, ref v); },
                      c => c[8] is >= 24 and <= 26 && c[9] is >= 49 and <= 51);
        }
        catch (Exception e) { Check("the restart part ran to its end", false, e.ToString()); }
        finally
        {
            try { pad?.Controller.Dispose(); } catch { }
            if (pad != null) Check("its devnode is gone after Dispose", WaitUntil(() => Node(pad.Instance) == null, 30_000), pad.Instance);
        }
        return 0;
    }

    /// <summary>The same GIP pad created again and again, under a key used
    /// before and under keys no create has used: each create's XInput read
    /// and rumble, and the device instance Windows gives its HID child.</summary>
    static int Reuse()
    {
        Console.WriteLine("\n=== reuse: one pad's identity used again against fresh ones ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var keys = new List<(string Label, string? Key)>
        {
            ("the default key", null), ("the default key again", null),
            ("a fresh key", "gipreuse:" + Guid.NewGuid().ToString("N")), ("another fresh key", "gipreuse:" + Guid.NewGuid().ToString("N")),
            ("the default key once more", null),
        };
        foreach (var (label, key) in keys)
        {
            Pad? pad = null;
            try
            {
                uint before = ConnectedSlots();
                var profile = ctx.GetProfile("xbox-one-s")!;
                var c = ctx.CreateController(profile, key, HMBackend.Usbip);
                string serial = DeviceIdentity.Resolve(c.IdentityKey, c.TransportIndex).SyntheticSerial;
                pad = new Pad("xbox-one-s", c, $@"USB\VID_045E&PID_02EA\{serial}", BitConverter.ToUInt64(ExpectedDeviceId(serial), 0), c.UsbipHandle!.Device.Protocol!);
                string hid = string.Join(" ", Descendants(pad.Instance).Where(d => d.Id.StartsWith("HID", StringComparison.OrdinalIgnoreCase)).Select(d => d.Id));
                int slot = SlotOf(pad, HMButton.A, 0x1000, before);
                Console.WriteLine($"  {label}: serial {serial}, HID child {hid}, slot {slot}{(slot < 0 ? "; " + SlotStates() : "")}");
                Check($"{label}: XInput reads the submitted A", slot >= 0, $"slot {slot}");
                if (slot >= 0)
                    Motor(pad.Engine, $"{label}: XInputSetState(16384, 32768)",
                          () => { var v = new XINPUT_VIBRATION { wLeftMotorSpeed = 16384, wRightMotorSpeed = 32768 }; return XInputSetState((uint)slot, ref v); },
                          cmd => cmd[8] is >= 24 and <= 26 && cmd[9] is >= 49 and <= 51);
            }
            catch (Exception e) { Check($"{label}: the create ran to its end", false, e.Message); }
            finally
            {
                try { pad?.Controller.Dispose(); } catch { }
                if (pad != null) WaitUntil(() => Node(pad.Instance) == null, 30_000);
            }
        }
        return 0;
    }

    /// <summary>Guide pressed on a GIP pad in a session with a console user:
    /// XInput reads before the press, after it, and after a second press.
    /// The Game Bar processes' start times are printed beside each.</summary>
    static int Guide() => GuidePresses(2);

    /// <summary>One Guide press, then the read: what a second run sees after
    /// the first left the Game Bar open or closed.</summary>
    static int GuideOnce() => GuidePresses(1);

    static int GuidePresses(int presses)
    {
        Console.WriteLine("\n=== guide: XInput reads around a Guide press ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        Pad? pad = null;
        string GameBar() => string.Join(", ", System.Diagnostics.Process.GetProcesses()
            .Where(p => p.ProcessName.StartsWith("GameBar", StringComparison.OrdinalIgnoreCase) || p.ProcessName.StartsWith("XboxGameBar", StringComparison.OrdinalIgnoreCase))
            .Select(p => { try { return $"{p.ProcessName} {p.StartTime:HH:mm:ss}"; } catch { return p.ProcessName; } }));
        try
        {
            uint before = ConnectedSlots();
            pad = OpenPad(ctx, "xbox-one-s", "gipguide:1");
            int slot = SlotOf(pad, HMButton.A, 0x1000, before);
            Console.WriteLine($"  before the press: slot {slot}; Game Bar: {GameBar()}");
            Check("XInput reads the submitted A before any Guide press", slot >= 0, SlotStates());
            for (int press = 1; press <= presses; press++)
            {
                var guide = new HMGamepadState { Buttons = HMButton.Guide };
                pad.Controller.SubmitState(in guide);
                Thread.Sleep(200);
                pad.Controller.SubmitState(new HMGamepadState());
                Thread.Sleep(3000);
                int again = SlotOf(pad, HMButton.B, 0x2000, before);
                Console.WriteLine($"  after Guide press {press}: slot {again}; {SlotStates()}; Game Bar: {GameBar()}");
                Check($"after Guide press {press}, XInput reads the submitted B", again >= 0, SlotStates());
            }
        }
        catch (Exception e) { Check("the guide part ran to its end", false, e.Message); }
        finally
        {
            try { pad?.Controller.Dispose(); } catch { }
            if (pad != null) WaitUntil(() => Node(pad.Instance) == null, 30_000);
        }
        return 0;
    }
}
