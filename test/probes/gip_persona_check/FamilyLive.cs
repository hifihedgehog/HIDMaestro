// gip_persona_check, the family live part ("family" with profile ids, or
// all twelve): each xinputhid profile created on the USB/IP backend as its
// GIP form, one at a time, read through Windows' own GIP stack, XInput and
// Windows.Gaming.Input. dualsense-composite is created first as the
// positive control and read over HID in the same window.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;

internal static unsafe partial class Program
{
    static int FamilyLive(IReadOnlyList<string> ids)
    {
        Console.WriteLine("\n=== family live: each xinputhid profile as the GIP function on USB/IP ===");
        using (var who = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(who).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Check("the family live part runs elevated", false, "creating a controller needs elevation");
                return 0;
            }
        if (SdkConsumers.Find() is { } other)
        {
            Console.WriteLine($"  SKIP: {other} has HIDMaestro.Core.dll loaded, and two programs that embed HIDMaestro never test at once.");
            return 2;
        }
        Console.WriteLine($"  Windows {Environment.OSVersion.Version}, {RuntimeInformation.OSArchitecture}");
        var left = new List<string>();
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        // The positive control first, read over HID, and kept for the run.
        HMController? control = null;
        string controlInstance = "";
        try
        {
            control = ctx.CreateController(ctx.GetProfile("dualsense-composite")!);
            controlInstance = $@"USB\VID_054C&PID_0CE6\{DeviceIdentity.Resolve(control.IdentityKey, control.TransportIndex).SyntheticSerial}";
            left.Add(controlInstance);
            string? hidPath = null;
            WaitUntil(() => (hidPath = InterfaceUnder(s_hidInterface, controlInstance, "vid_054c&pid_0ce6")) != null, 30_000);
            bool down = false, up = false;
            if (hidPath != null)
            {
                var cross = new HMGamepadState { Buttons = HMButton.A };
                var rest = new HMGamepadState();
                down = WaitUntil(() => { control.SubmitState(in cross); return HidInput(hidPath, 0x01, 64) is { Length: > 8 } r && (r[8] & 0x20) != 0; }, 10_000);
                up = WaitUntil(() => { control.SubmitState(in rest); return HidInput(hidPath, 0x01, 64) is { Length: > 8 } r && (r[8] & 0x20) == 0; }, 10_000);
            }
            Check("positive control: dualsense-composite's Cross reads back over HID and releases", down && up, hidPath ?? "no HID interface");
        }
        catch (Exception e) { Check("positive control: dualsense-composite creates", false, e.Message); }

        foreach (string id in ids)
        {
            var profile = ctx.GetProfile(id);
            if (profile == null) { Check($"{id}: in the catalog", false); continue; }
            Console.WriteLine($"\n-- {id} ({profile.VendorId:X4}:{profile.ProductId:X4}, gip.identity {profile.Inner.Gip?.Identity}) --");
            HMController? pad = null;
            string instance = "";
            int slot = -1;
            try
            {
                uint before = ConnectedSlots();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                pad = ctx.CreateController(profile, null, HMBackend.Usbip);
                long createdMs = clock.ElapsedMilliseconds;
                uint atReturn = ConnectedSlots();
                Check($"{id}: the create returns with an XInput slot more, as the UMDF2 xinputhid create does",
                      System.Numerics.BitOperations.PopCount(atReturn) > System.Numerics.BitOperations.PopCount(before),
                      $"returned in {createdMs} ms, slots {Slots(before)} before, {Slots(atReturn)} at return");
                var raw = new List<(byte Id, byte[] Data)>();
                pad.OutputReceived += (_, pkt) => { if (pkt.Source == HMOutputSource.HidOutput) lock (raw) raw.Add((pkt.ReportId, pkt.Data.ToArray())); };
                ushort pid = profile.ProductId;
                var engine = pad.UsbipHandle!.Device.Protocol!;
                string serial = DeviceIdentity.Resolve(pad.IdentityKey, pad.TransportIndex).SyntheticSerial;
                instance = $@"USB\VID_045E&PID_{pid:X4}\{serial}";
                left.Add(instance);
                Check($"{id}: the create runs on USB/IP as the GIP form, and the controller keeps the profile it was given", pad.Backend == HMBackend.Usbip && pad.Presented.Inner.ReportsOnGipInterface && ReferenceEquals(pad.Profile, profile) && profile.FormOn(HMBackend.Usbip).Inner.ReportsOnGipInterface,
                      $"backend {pad.Backend}, {clock.ElapsedMilliseconds} ms");

                bool started = WaitUntil(() => Node(instance) is { Started: true }, 30_000);
                var node = Node(instance);
                Check($"{id}: {instance} started under dc1-controller.inf with xboxgip above it",
                      started && node is { Problem: 0 } && string.Equals(node.Service, "dc1-controller", StringComparison.OrdinalIgnoreCase)
                      && node.UpperFilters.Contains("xboxgip", StringComparer.OrdinalIgnoreCase),
                      node == null ? "not present" : $"service {node.Service} INF {node.Inf} filters {string.Join(",", node.UpperFilters)} problem {node.Problem}");
                if (node != null)
                    Console.WriteLine($"  hardware IDs {string.Join(" | ", node.HardwareIds)}; compatible IDs {string.Join(" | ", node.CompatibleIds)}; INF {node.Inf}");

                WaitUntil(() => engine.SnapshotLog().Any(x => x.Command.Length >= 5 && x.Command[0] == 0x05 && x.Command[3] == 0x01 && x.Command[4] == 0x00), 20_000);
                var log = engine.SnapshotLog();
                bool startedGip = log.Any(x => x.Command.Length >= 5 && x.Command[0] == 0x05 && x.Command[3] == 0x01 && x.Command[4] == 0x00);
                var security = log.Where(x => x.Command.Length >= 1 && x.Command[0] == 0x06).Select(x => Hex(x.Command)).ToList();
                Console.WriteLine($"  host messages: {string.Join(" ", log.Take(12).Select(x => Hex(x.Command)))}");
                Check($"{id}: the host started the device (Set Device State Start) and sent no security challenge",
                      startedGip && security.All(x => x.Length == 12 && x.EndsWith("020100", StringComparison.Ordinal)), string.Join(" ", security));

                ulong deviceId = BitConverter.ToUInt64(ExpectedDeviceId(serial), 0);
                var held = GipCapture(4_000).Where(m => m.DeviceId == deviceId).ToList();
                var arrival = held.FirstOrDefault(m => m.Command == 0x02);
                Check($"{id}: xboxgip holds the arrival, 045E:{pid:X4} firmware 4D48.0300",
                      arrival != null && arrival.Data.Length >= 16 && Hex(arrival.Data.AsSpan(8, 8)) == $"5E04{pid & 0xFF:X2}{pid >> 8:X2}484D0003",
                      arrival == null ? $"{held.Count} messages" : Hex(arrival.Data));
                var served = ServedMetadata(pad.Presented.Inner, out _);
                var descriptor = held.FirstOrDefault(m => m.Command == 0x04);
                Check($"{id}: and the {served.Length} bytes of metadata as served",
                      descriptor != null && descriptor.Data.Length >= 4 + served.Length && descriptor.Data.AsSpan(4, served.Length).SequenceEqual(served),
                      descriptor == null ? "no descriptor message" : $"{descriptor.Data.Length - 4} bytes, first difference {FirstDifference(descriptor.Data.AsSpan(4).ToArray(), served)}");

                var children = Descendants(instance);
                foreach (var c in children)
                    Console.WriteLine($"  child {c.Id}  service {c.Info.Service}, filters {string.Join(",", c.Info.UpperFilters)}, hardware {string.Join(" | ", c.Info.HardwareIds.Take(2))}, INF {c.Info.Inf}");
                Check($"{id}: a child carries xinputhid", children.Any(c => c.Info.UpperFilters.Contains("xinputhid", StringComparer.OrdinalIgnoreCase)),
                      string.Join(" ; ", children.Select(c => c.Id)));

                var mark = new HMGamepadState
                {
                    Buttons = HMButton.A,
                    Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 1f, [HMAxis.Y] = 0.5f, [HMAxis.Rx] = 0.5f, [HMAxis.Ry] = 0.5f, [HMAxis.Z] = 0f, [HMAxis.Rz] = 0f },
                };
                WaitUntil(() =>
                {
                    pad.SubmitState(in mark);
                    for (uint i = 0; i < 4; i++)
                        if ((before & (1u << (int)i)) == 0 && XInputGetState(i, out var s) == 0 && s.Gamepad.wButtons == 0x1000 && s.Gamepad.sThumbLX == 32767) { slot = (int)i; return true; }
                    return false;
                }, 30_000);
                Check($"{id}: an XInput slot that was free reads the submitted A and left stick", slot >= 0,
                      slot >= 0 ? $"slot {slot}, {clock.ElapsedMilliseconds} ms after the create began" : $"connected: {Slots(ConnectedSlots())}");
                if (slot >= 0)
                {
                    uint s = (uint)slot;
                    if (XInputGetCapabilities(s, 0, out var caps) == 0)
                        Console.WriteLine($"  capabilities: type {caps.Type} subtype {caps.SubType} flags 0x{caps.Flags:X4}");
                    Reads(pad, s, $"{id}: Y and the right bumper", new HMGamepadState { Buttons = HMButton.Y | HMButton.RightBumper }, g => g.wButtons == 0x8200);
                    Reads(pad, s, $"{id}: d-pad north-east", new HMGamepadState { Hat = HMHat.NorthEast }, g => g.wButtons == 0x0009);
                    Reads(pad, s, $"{id}: Guide through XInputGetStateEx", new HMGamepadState { Buttons = HMButton.Guide }, g => g.wButtons == 0x0400, extended: true);
                    Reads(pad, s, $"{id}: the triggers, left full and right half", Axes(0.5f, 0.5f, 0.5f, 0.5f, 1f, 0.5f), g => g.bLeftTrigger == 255 && g.bRightTrigger is >= 126 and <= 129);
                    Reads(pad, s, $"{id}: the right stick, left and up", Axes(0.5f, 0.5f, 0f, 0f, 0f, 0f), g => g.sThumbRX == -32768 && g.sThumbRY == 32767);
                    Reads(pad, s, $"{id}: at rest", new HMGamepadState(), g => g.wButtons == 0 && g.bLeftTrigger == 0 && g.sThumbRX == 0);

                    // Share where the form carries the console function map.
                    bool hasShare = FormMapsShare(pad.Presented.Inner);
                    string? xusb = InterfaceUnder(s_xusbInterface, instance);
                    if (hasShare && xusb != null)
                    {
                        byte extra = 0xFF; ushort buttons = 0;
                        var share = new HMGamepadState { Buttons = HMButton.Share };
                        bool down = WaitUntil(() => { pad.SubmitState(in share); return XusbState(xusb, out buttons, out extra) && (extra & 1) != 0; }, 5_000);
                        var rest = new HMGamepadState();
                        bool up = WaitUntil(() => { pad.SubmitState(in rest); return XusbState(xusb, out buttons, out extra) && (extra & 1) == 0; }, 5_000);
                        Check($"{id}: Share sets ExtraSystemButtons bit 0 and releases", down && up, $"extra 0x{extra:X2}");
                    }
                    else if (hasShare) Check($"{id}: an XUSB interface for the Share read", false, "none");
                    else if (xusb != null)
                    {
                        byte extra = 0xFF; ushort buttons = 0;
                        var share = new HMGamepadState { Buttons = HMButton.Share };
                        Thread.Sleep(200);
                        pad.SubmitState(in share);
                        Thread.Sleep(300);
                        bool clear = XusbState(xusb, out buttons, out extra) && (extra & 1) == 0;
                        pad.SubmitState(new HMGamepadState());
                        Check($"{id}: has no Share button, and a submitted Share sets no ExtraSystemButtons bit", clear, $"extra 0x{extra:X2}");
                    }

                    Motor(engine, $"{id}: XInputSetState(16384, 32768)", () => { var v = new XINPUT_VIBRATION { wLeftMotorSpeed = 16384, wRightMotorSpeed = 32768 }; return XInputSetState(s, ref v); },
                          c => c[8] is >= 24 and <= 26 && c[9] is >= 49 and <= 51 && (c[5] & 0x03) == 0x03);
                    Motor(engine, $"{id}: XInputSetState(0, 0)", () => { var v = new XINPUT_VIBRATION(); return XInputSetState(s, ref v); }, c => c[8] == 0 && c[9] == 0);
                    // Trigger rumble: the haptics output report the HID
                    // child's descriptor declares, written with
                    // HidD_SetOutputReport, which takes no input focus
                    // (lane GIP's check in Live.cs, for each identity).
                    string? gipHid = InterfaceUnder(s_hidInterface, instance);
                    int gipOut = gipHid == null ? 0 : HidOutputLength(gipHid);
                    // The handle stays open while the effect plays, as a
                    // program's does (Live.cs).
                    using (var handle = gipHid == null ? null : HidHandle.Open(gipHid))
                    {
                        Motor(engine, $"{id}: HidD_SetOutputReport(triggers 80 and 30, motors 100 and 50) on the HID child",
                              () => handle == null ? 2u : handle.SetOutput(Haptics(gipHid!, gipOut, 80, 30, 100, 50)),
                              c => (c[5] & 0x0F) == 0x0F && c[6] == 80 && c[7] == 30 && c[8] == 100 && c[9] == 50);
                        Motor(engine, $"{id}: HidD_SetOutputReport at rest on the HID child",
                              () => handle == null ? 2u : handle.SetOutput(Haptics(gipHid!, gipOut, 0, 0, 0, 0)), c => c[6] == 0 && c[7] == 0 && c[8] == 0 && c[9] == 0);
                    }
                    var motors = engine.SnapshotLog().Where(x => x.Command.Length >= 13 && x.Command[0] == 0x09).Select(x => x.Command).ToList();
                    bool raised;
                    lock (raw) raised = motors.Count > 0 && motors.All(m => raw.Any(o => o.Id == m[5] && o.Data.AsSpan().SequenceEqual(m.AsSpan(6, 7))));
                    Check($"{id}: each Direct Motor command reached OutputReceived in the UMDF2 form's shape", raised, $"{motors.Count} commands, {raw.Count} reports");
                }

                var raws = new List<Windows.Gaming.Input.RawGameController>();
                WaitUntil(() => (raws = WgiPins.Raws().Where(r => r.HardwareVendorId == 0x045E && r.HardwareProductId == pid).ToList()).Count > 0, 10_000);
                Check($"{id}: Windows.Gaming.Input lists one RawGameController of 045E:{pid:X4}", raws.Count == 1,
                      string.Join(" ; ", raws.Select(r => $"{r.DisplayName} wireless {r.IsWireless}")));
                if (raws.Count >= 1)
                {
                    // The Gamepad list can fill after the RawGameController
                    // list, so the count is read until it settles.
                    int pads = 0;
                    var wgiClock = System.Diagnostics.Stopwatch.StartNew();
                    WaitUntil(() => (pads = WgiPins.Pads().Count(g => WgiPins.RawOf(g) is { HardwareVendorId: 0x045E } r && r.HardwareProductId == pid)) >= 1, 10_000);
                    Thread.Sleep(500);
                    pads = WgiPins.Pads().Count(g => WgiPins.RawOf(g) is { HardwareVendorId: 0x045E } r && r.HardwareProductId == pid);
                    Check($"{id}: and one Gamepad", pads == 1, $"{pads}, first seen after {wgiClock.ElapsedMilliseconds - 500} ms");
                    Console.WriteLine($"  WGI: {raws[0].DisplayName}, buttons {raws[0].ButtonCount}, axes {raws[0].AxisCount}, wireless {raws[0].IsWireless}, motors {raws[0].ForceFeedbackMotors.Count}");
                }
            }
            catch (Exception e) { Check($"{id}: the family live part ran to its end", false, e.ToString()); }
            finally
            {
                try { pad?.Dispose(); } catch { }
                pad = null;
                if (instance.Length > 0)
                    Check($"{id}: its devnode is gone after Dispose", WaitUntil(() => Node(instance) == null, 30_000));
                if (slot >= 0)
                    Check($"{id}: its XInput slot is free again", WaitUntil(() => XInputGetState((uint)slot, out _) != 0, 10_000), Slots(ConnectedSlots()));
            }
            Umdf2Haptics(ctx, profile);
        }

        try { control?.Dispose(); } catch { }
        if (controlInstance.Length > 0)
            Check("the positive control's devnode is gone after Dispose", WaitUntil(() => Node(controlInstance) == null, 30_000));
        Console.WriteLine($"\n  not-present USB records this run leaves: {string.Join(", ", left)}");
        return 0;
    }

    /// <summary>The same haptics output report on the profile's UMDF2 form,
    /// for parity, where the UMDF2 driver runs: it must reach
    /// OutputReceived with the trigger magnitudes.</summary>
    static void Umdf2Haptics(HMContext ctx, HMProfile profile)
    {
        string? why = null;
        try { if (ctx.ResolveBackend(profile, HMBackend.Umdf2) != HMBackend.Umdf2) why = "it resolves elsewhere"; }
        catch (Exception e) { why = e.GetType().Name; }
        if (why != null) { Console.WriteLine($"  no UMDF2 form on this machine for the parity write: {why}"); return; }
        HMController? umdf2 = null;
        try
        {
            var got = new List<(byte Id, byte[] Data)>();
            umdf2 = ctx.CreateController(profile, null, HMBackend.Umdf2);
            umdf2.OutputReceived += (_, pkt) => { if (pkt.Source == HMOutputSource.HidOutput) lock (got) got.Add((pkt.ReportId, pkt.Data.ToArray())); };
            string? hid = null;
            string parent = umdf2.InstanceId ?? "";
            WaitUntil(() => (hid = InterfaceUnder(s_hidInterface, parent)) != null, 30_000);
            Check($"{profile.Id}: parity, the UMDF2 form has its HID interface", hid != null, hid ?? parent);
            if (hid == null) return;
            using var handle = HidHandle.Open(hid);
            uint code = handle?.SetOutput(Haptics(hid, HidOutputLength(hid), 80, 30, 100, 50)) ?? 2u;
            bool arrived = WaitUntil(() => { lock (got) return got.Any(o => o.Id == 0x0F && o.Data.Length >= 4 && o.Data[0] == 80 && o.Data[1] == 30 && o.Data[2] == 100 && o.Data[3] == 50); }, 3_000);
            string seen;
            lock (got) seen = string.Join(" | ", got.Select(o => $"{o.Id:X2} {Hex(o.Data)}"));
            Check($"{profile.Id}: parity, the same report reaches the UMDF2 form's OutputReceived with the trigger magnitudes", code == 0 && arrived,
                  $"HidD_SetOutputReport {code}, OutputReceived {seen}");
        }
        catch (Exception e) { Check($"{profile.Id}: parity, the UMDF2 form", false, e.Message); }
        finally { try { umdf2?.Dispose(); } catch { } }
    }

    /// <summary>True when the identity's input report carries Share: an
    /// op of its inputProgram copies the state block's Share bit, bit 112
    /// (byte 14, bit 0).</summary>
    static bool FormMapsShare(ControllerProfile form) =>
        HIDMaestro.Internal.Usbip.GipFunction.Model["identities"]?[form.Gip?.Identity ?? ""]?["inputProgram"]?.ToJsonString()
            .Replace(" ", "", StringComparison.Ordinal).Contains("[[112,", StringComparison.Ordinal) == true;
}
