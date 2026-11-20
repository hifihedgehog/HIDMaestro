// bluetooth_hid_check: a Bluetooth pad that reaches XInput through xinputhid,
// presented on the USB/IP backend as the HID device it is over the air.
//
// Windows installs xinputhid for such a pad on the pad's transport node,
// above the HID driver (xinputhid.inf, Bth_Bus and Btle_Bus: UpperFilters
// xinputhid on the node, DevicePropertyFlags 1 in its driver key), and the
// pad's game controller collection sits below as a plain HID device whose
// path carries &IG_ and that holds the XUSB interface. A profile's
// bluetoothHid block is that HID device, and a create on USB/IP presents it
// with the same values on the device's own node.
//
//   offline          each catalog profile's form: its descriptor and device
//                    version, where each button, stick, trigger and hat
//                    direction lands in the report against the two public
//                    parsers of these pads, the node values, the load
//                    rules. No device.
//   guide <id> [n]   the Xbox button pressed and released n times (50), each
//                    read through XInputGetStateEx. Elevated.
//   hold <id>        the Xbox button held through further submits and a moved
//                    stick. Elevated.
//   polled <id> [n]  guide, with another thread reading XInput from before
//                    the create, as a running game does. Elevated.
//   live [ids]       each profile created on USB/IP: the device tree, XInput
//                    control by control, what XInput and
//                    Windows.Gaming.Input report it as, the rumble packet a
//                    program receives, a second create on the same record,
//                    and a plain profile on that record. Elevated.
//
// The parsers: SDL 3.4.18 src/joystick/hidapi/SDL_hidapi_xboxone.c
// (HIDAPI_DriverXboxOneBluetooth_HandleButtons16 at 1252,
// _HandleButtons at 1279, _HandleStatePacket at 1370) and xpadneo
// hid-xpadneo/src/xpadneo/events.c 96-114.
//
// Exit 0 PASS, 1 FAIL, 2 bad arguments or skipped, 3 not elevated.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static partial class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    /// <summary>Where a pad's report carries a button: the byte after the
    /// report ID counts as 1, as both parsers index it.</summary>
    readonly record struct Bit(int Byte, byte Mask);

    /// <summary>One catalog profile: its PID, the revision a real pad
    /// reports where one is on record, and where its report carries each
    /// button.</summary>
    sealed record Pad(string Id, ushort Pid, ushort? Version, bool Le, int ReportBytes, Dictionary<HMButton, Bit> Buttons);

    // The report of 17 bytes and more, report ID included (SDL 1279-1304,
    // xpadneo 96-114): the buttons at usages 1, 2, 4, 5, 7, 8, 12, 13, 14
    // and 15, and View at usage 11 on a pad with a Share button or as AC
    // Back in the byte after on one without.
    static Dictionary<HMButton, Bit> Long(bool share) => new()
    {
        [HMButton.A] = new(14, 0x01), [HMButton.B] = new(14, 0x02), [HMButton.X] = new(14, 0x08), [HMButton.Y] = new(14, 0x10),
        [HMButton.LeftBumper] = new(14, 0x40), [HMButton.RightBumper] = new(14, 0x80),
        [HMButton.Back] = share ? new(15, 0x04) : new(16, 0x01),
        [HMButton.Start] = new(15, 0x08), [HMButton.Guide] = new(15, 0x10),
        [HMButton.LeftStick] = new(15, 0x20), [HMButton.RightStick] = new(15, 0x40),
    };

    // The 16-byte report (SDL 1252-1268): ten buttons in order. The Xbox
    // button travels in a report of its own (SDL 1446).
    static Dictionary<HMButton, Bit> Short() => new()
    {
        [HMButton.A] = new(14, 0x01), [HMButton.B] = new(14, 0x02), [HMButton.X] = new(14, 0x04), [HMButton.Y] = new(14, 0x08),
        [HMButton.LeftBumper] = new(14, 0x10), [HMButton.RightBumper] = new(14, 0x20),
        [HMButton.Back] = new(14, 0x40), [HMButton.Start] = new(14, 0x80),
        [HMButton.LeftStick] = new(15, 0x01), [HMButton.RightStick] = new(15, 0x02),
    };

    static readonly Pad[] Pads =
    {
        // Revisions 0523 and 0513: two real pads' Bluetooth nodes carry them in
        // their hardware IDs (BTHLEDevice\{00001812-...}_Dev_VID&02045e_PID&0b13_REV&0523).
        new("xbox-series-xs-bt", 0x0B13, 0x0523, true, 17, With(Long(share: true), HMButton.Share, new Bit(16, 0x01))),
        new("xbox-one-s-bt", 0x02FD, null, false, 16, Short()),
        // The same pad's other catalog entry. A 02FD pad serves a Windows host
        // the 16-byte report, and xinputhid reads its buttons in that order
        // whatever descriptor it is given (measured 2026-11-19: with the
        // 17-byte layout X read as Y and RB as Start).
        new("xbox-one-s-bt-full", 0x02FD, null, false, 16, Short()),
        // The Elite Series 2. No public source carries the descriptor it
        // serves a Windows host, so the form carries the one a Linux host
        // reads, and its buttons where xinputhid reads this PID's (measured
        // 2026-11-19: in order, as the One S pad's).
        new("xbox-elite-v2-bt", 0x0B05, null, false, 39, With(Short(), HMButton.Guide, new Bit(15, 0x04))),
        new("xbox-elite-v2-bt-v2", 0x0B22, 0x0513, true, 20, With(Short(), HMButton.Guide, new Bit(15, 0x04))),
    };

    static Dictionary<HMButton, Bit> With(Dictionary<HMButton, Bit> map, HMButton button, Bit at) { map[button] = at; return map; }

    static readonly string[] Wired =
        { "xbox-one-original", "xbox-one-rev1", "xbox-elite-v1", "xbox-one-s", "xbox-elite-v2", "xbox-adaptive", "xbox-series-xs" };

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "offline";
        try
        {
            switch (mode)
            {
                case "offline": Offline(); break;
                case "live":
                    int code = Live(args.Skip(1).ToArray());
                    if (code != 0) return code;
                    break;
                case "hold" when args.Length >= 2:
                    int holdCode = Hold(args[1]);
                    if (holdCode != 0) return holdCode;
                    break;
                case "guide" or "polled" when args.Length >= 2:
                    int guideCode = Guide(args[1], args.Length > 2 ? int.Parse(args[2]) : 50, polled: mode == "polled");
                    if (guideCode != 0) return guideCode;
                    break;
                default:
                    Console.WriteLine("usage: BluetoothHidCheck offline | live [profile ids] | guide <profile id> [presses] | polled <profile id> [presses] | hold <profile id>");
                    return 2;
            }
        }
        catch (Exception e)
        {
            Check($"{mode} ran to its end", false, $"{e.GetType().Name}: {e.Message}");
            Console.WriteLine(e);
        }
        Console.WriteLine($"\n{s_total - s_failures} of {s_total} checks passed");
        Console.WriteLine(s_failures == 0 ? "PASS" : "FAIL");
        return s_failures == 0 ? 0 : 1;
    }

    static void Offline()
    {
        Console.WriteLine("=== offline: each profile's Bluetooth HID form ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        foreach (var pad in Pads)
        {
            var profile = ctx.GetProfile(pad.Id);
            if (profile == null) { Check($"{pad.Id} is in the catalog", false); continue; }
            Check($"{pad.Id}: the USB/IP backend presents it", profile.SupportsBackend(HMBackend.Usbip));
            HMProfile form;
            try { form = profile.FormOn(HMBackend.Usbip); }
            catch (Exception e) { Check($"{pad.Id}: its form builds", false, e.Message); continue; }
            var inner = form.Inner;
            Check($"{pad.Id}: the form is its own profile with the pad's VID and PID 045E:{pad.Pid:X4}",
                  !ReferenceEquals(form, profile) && inner.VendorId == 0x045E && inner.ProductId == pad.Pid, $"{inner.VendorId:X4}:{inner.ProductId:X4}");
            Check($"{pad.Id}: on UMDF2 the profile runs as it is", ReferenceEquals(profile.FormOn(HMBackend.Umdf2), profile));
            var presentation = UsbPresentation.Resolve(inner);
            Check($"{pad.Id}: a derived HID device, one HID interface", presentation.Kind == UsbPresentationKind.DerivedHid, presentation.Kind.ToString());
            byte[] descriptor = inner.GetDescriptorBytes()!;
            Check($"{pad.Id}: the form's descriptor is the block's", string.Equals(Convert.ToHexString(descriptor), profile.Inner.BluetoothHid!.Descriptor, StringComparison.OrdinalIgnoreCase),
                  $"{descriptor.Length} bytes");
            var builder = inner.GetOrBuildReportBuilder();
            Check($"{pad.Id}: the input report is report 1, {pad.ReportBytes} bytes with its ID", builder.InputReportId == 1 && builder.InputReportByteSize == pad.ReportBytes,
                  $"report {builder.InputReportId}, {builder.InputReportByteSize} bytes");
            if (pad.Version is { } version)
                Check($"{pad.Id}: device version {version:X4}, the real pad's revision", inner.VersionNumber == version, $"{inner.VersionNumber:X4}");
            var plan = UsbNodeStamp.For(inner);
            Check($"{pad.Id}: xinputhid on the transport node and the {(pad.Le ? "Bluetooth LE" : "classic Bluetooth")} bus marker",
                  plan.Xinputhid && plan.BusMarker == (pad.Le ? UsbNodeStamp.LeMarker : UsbNodeStamp.ClassicMarker), $"{plan.Xinputhid}, {plan.BusMarker}");
            Check($"{pad.Id}: the rumble report reaches a program without its report ID, as the profile's own descriptor has it", inner.UnnumberedOutputs);
            if (!pad.Buttons.ContainsKey(HMButton.Guide))
                Check($"{pad.Id}: the Xbox button travels in report 2, which the device sends when it changes",
                      builder.GuideReport is { ReportId: 2, FrameBytes: 2, BitOffset: 0 }, $"{builder.GuideReport}");
            else
                Check($"{pad.Id}: the Xbox button travels in the input report", inner.ButtonMap is { Length: > 10 } map && map[10] >= 0);
            Check($"{pad.Id}: the form names no XInput presentation of its own and no GIP identity",
                  XInputPresentation.Resolve(inner).Spec == null && inner.Gip == null && !inner.ReportsOnGipInterface);

            // Each button alone, where the parsers read it, and nowhere else.
            var rest = builder.BuildReport();
            foreach (var (button, at) in pad.Buttons)
            {
                var report = builder.BuildReport(buttonMask: (uint)button);
                var changed = Enumerable.Range(0, report.Length).Where(i => report[i] != rest[i]).ToList();
                bool ok = changed.Count == 1 && changed[0] == at.Byte && (report[at.Byte] ^ rest[at.Byte]) == at.Mask;
                Check($"{pad.Id}: {button} is byte {at.Byte} mask 0x{at.Mask:X2}", ok,
                      ok ? "" : $"changed {string.Join(", ", changed.Select(i => $"byte {i} 0x{rest[i]:X2} to 0x{report[i]:X2}"))}");
            }
            // The sticks, triggers and hat (SDL 1384-1440): X, Y, right X and Y
            // as 16-bit values around 0x8000 at bytes 1 to 8, the triggers as
            // 10-bit values at 9 and 11, the hat 1 to 8 at 13 and 0 at rest.
            Check($"{pad.Id}: at rest the sticks read 0x8000, the triggers 0 and the hat 0",
                  U16(rest, 1) == 0x8000 && U16(rest, 3) == 0x8000 && U16(rest, 5) == 0x8000 && U16(rest, 7) == 0x8000
                  && U16(rest, 9) == 0 && U16(rest, 11) == 0 && rest[13] == 0, Convert.ToHexString(rest));
            var sticks = form.Sticks; var triggers = form.Triggers;
            if (sticks.Count >= 2 && triggers.Count >= 2)
            {
                var axes = new Dictionary<HMAxis, float> {
                    [sticks[0].XAxis] = 1f, [sticks[0].YAxis] = 0f, [sticks[1].XAxis] = 0f, [sticks[1].YAxis] = 1f,
                    [triggers[0].Axis] = 1f, [triggers[1].Axis] = 0.5f };
                var r = builder.BuildReport(axes, hatValue: (int)HMHat.SouthWest);
                Check($"{pad.Id}: left stick full right and up, right stick full left and down, left trigger full, right trigger half, hat down-left",
                      U16(r, 1) == 0xFFFF && U16(r, 3) == 0 && U16(r, 5) == 0 && U16(r, 7) == 0xFFFF && U16(r, 9) == 1023
                      && U16(r, 11) is >= 510 and <= 513 && r[13] == 6, Convert.ToHexString(r));
            }
            else Check($"{pad.Id}: the form declares two sticks and two triggers", false, $"{sticks.Count} sticks, {triggers.Count} triggers");
        }

        Console.WriteLine("\n=== offline: the wired pads keep the GIP function ===");
        foreach (var id in Wired)
        {
            var profile = ctx.GetProfile(id);
            if (profile == null) { Check($"{id} is in the catalog", false); continue; }
            Check($"{id}: presents the GIP function on USB/IP", profile.FormOn(HMBackend.Usbip).Inner.ReportsOnGipInterface && profile.Inner.BluetoothHid == null);
        }

        Console.WriteLine("\n=== offline: the load rules ===");
        string dir = Path.Combine(Path.GetTempPath(), $"hm-bthid-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        try
        {
            var source = Embedded("xbox-series-xs-bt");
            Refused(ctx, dir, "a GIP identity on a Bluetooth pad", "bthid-gip", o => o["gip"] = new JsonObject { ["identity"] = "xbox-series-1914" }, source, "$.gip");
            Refused(ctx, dir, "the block on a pad that is not a Bluetooth identity", "bthid-wired", o => o["connection"] = "usb", source, "$.bluetoothHid");
            Refused(ctx, dir, "the block on a profile that does not reach XInput through xinputhid", "bthid-plain", o => o.Remove("driverMode"), source, "$.bluetoothHid");
            Refused(ctx, dir, "the block with no descriptor", "bthid-empty", o => ((JsonObject)o["bluetoothHid"]!).Remove("descriptor"), source, "$.bluetoothHid");
            // A Bluetooth pad with no block loads, runs on UMDF2 and is refused on USB/IP with the reason.
            var bare = (JsonObject)JsonNode.Parse(source)!;
            bare["id"] = "bthid-bare"; bare.Remove("bluetoothHid");
            File.WriteAllText(Path.Combine(dir, "bthid-bare.json"), bare.ToJsonString());
            ctx.LoadProfilesFromDirectory(dir);
            var loaded = ctx.GetProfile("bthid-bare");
            Check("a Bluetooth pad with no block loads", loaded != null, loaded == null ? Issues(ctx, "bthid-bare") : "");
            if (loaded != null)
            {
                Check("it is not presented on USB/IP", !loaded.SupportsBackend(HMBackend.Usbip));
                string reason = UsbPresentation.NotPresentableReason(loaded.Inner) ?? "";
                Check("the refusal names the missing block", reason.Contains("bluetoothHid", StringComparison.Ordinal), reason);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static int U16(byte[] r, int at) => r[at] | (r[at + 1] << 8);

    /// <summary>A catalog profile's file, from the SDK's resources.</summary>
    static string Embedded(string id)
    {
        var asm = typeof(HMContext).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            string text = new StreamReader(stream, System.Text.Encoding.UTF8).ReadToEnd();
            if (JsonNode.Parse(text) is JsonObject o && (string?)o["id"] == id) return text;
        }
        throw new InvalidOperationException($"The SDK carries no profile {id}.");
    }

    /// <summary>A copy of the profile with one change is refused at load,
    /// at <paramref name="path"/>.</summary>
    static void Refused(HMContext ctx, string dir, string what, string id, Action<JsonObject> change, string source, string path)
    {
        var o = (JsonObject)JsonNode.Parse(source)!;
        o["id"] = id;
        change(o);
        string sub = Path.Combine(dir, id);
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, id + ".json"), o.ToJsonString());
        ctx.LoadProfilesFromDirectory(sub);
        string issues = Issues(ctx, id);
        Check($"refused at load: {what}", ctx.GetProfile(id) == null && issues.Contains(path, StringComparison.Ordinal), issues.Length > 0 ? issues : "loaded, no issue listed");
    }

    static string Issues(HMContext ctx, string id) =>
        string.Join(" | ", ctx.ProfileLoadIssues.Where(i => i.ProfileId == id || (i.File ?? "").Contains(id, StringComparison.OrdinalIgnoreCase)).Select(i => $"{i.JsonPath}: {i.Reason}"));
}
