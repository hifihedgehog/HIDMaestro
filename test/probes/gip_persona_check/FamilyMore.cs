// gip_persona_check, three more live parts of the GIP function on USB/IP:
//   nodriver  A GIP device no INF binds (profiles/gip-no-driver.json): the
//             create fails and names the device and the compatible ID no
//             driver bound, and leaves no device.
//   pair      Two GIP pads at once, two identities side by side and two pads
//             of one identity: each its own device ID and XInput slot, each
//             reading its own state, each rumble at its own device.
//   restart   The host's driver stack restarted on the pad's USB device with
//             no port reset (the device disabled and enabled): the pad comes
//             back in XInput and reads again.
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
                Check($"{a} and {b}: each reads its own submitted button in its own XInput slot", s1 >= 0 && s2 >= 0 && s1 != s2, $"slots {s1} and {s2}");
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
            uint disabled = CM_Locate_DevNodeW(out uint dev, pad.Instance, 0) == 0 ? CM_Disable_DevNode(dev, 0x4 /* CM_DISABLE_UI_NOT_OK */) : 0xFFFF;
            Check("the USB device is disabled, which stops its driver stack and leaves the port alone", disabled == 0, $"CM_Disable_DevNode {disabled}");
            bool stopped = WaitUntil(() => Node(pad.Instance) is { Started: false }, 10_000);
            Check("it stops", stopped, Node(pad.Instance) is { } n0 ? $"problem {n0.Problem}" : "gone");
            Check("its XInput slot goes", WaitUntil(() => slot < 0 || XInputGetState((uint)slot, out _) != 0, 10_000));
            uint enabled = CM_Locate_DevNodeW(out dev, pad.Instance, 0) == 0 ? CM_Enable_DevNode(dev, 0) : 0xFFFF;
            Check("it is enabled again", enabled == 0, $"CM_Enable_DevNode {enabled}");
            bool started = WaitUntil(() => Node(pad.Instance) is { Started: true, Problem: 0 }, 20_000);
            Check("it starts again under dc1-controller", started && Node(pad.Instance) is { } n1 && string.Equals(n1.Service, "dc1-controller", StringComparison.OrdinalIgnoreCase));
            int again = SlotOf(pad, HMButton.B, 0x2000, before);
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
}
