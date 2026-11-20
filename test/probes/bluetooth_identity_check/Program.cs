// bluetooth_identity_check: a Bluetooth profile presents its own form on
// every backend, and a program reads it on the Bluetooth bus.
//
// hidapi reads a HID device's bus from the compatible IDs of the devnode's
// parent, where the first ID that names a bus decides (SDL 3.4.18
// src/hidapi/windows/hid.c:741-808), and SDL sets is_bluetooth from it
// (SDL_hidapijoystick.c:937). Its PS4, PS5 and Switch drivers then speak
// the pad's Bluetooth protocol: output report 0x11 or 0x31, 78 bytes with a
// CRC (SDL_hidapi_ps4.c:967-995, SDL_hidapi_ps5.c:1099-1119), and no 0x80
// USB handshake (SDL_hidapi_switch.c:1617-1622). On UMDF2 the device's node
// carries a real Bluetooth device's IDs (PresentationPlan). On USB/IP the
// node is a USB device, and the SDK writes a Bluetooth marker as its first
// compatible ID at each start (UsbNodeStamp), so the same profile is the
// same device to a program on both.
//
//   offline            every Bluetooth profile of the catalog: what it
//                      presents on each backend, its USB device, the marker
//                      of its transport, the bus hidapi's rule reads from
//                      the node's IDs, and the usbForm and usbFormOf keys,
//                      which a load refuses. No device.
//   live <backend> [ids]
//                      each Bluetooth profile but the Xbox pads, which
//                      bluetooth_hid_check has, created on <backend> (umdf2,
//                      usbip, or auto for what Auto resolves to here), with
//                      dualsense and, on USB/IP, dualsense-composite as the
//                      USB controls of the same run: the bus as hidapi reads
//                      it from the node, each HID collection's version and
//                      product string, and for a pad its report lengths, a
//                      feature read, an output report and the raw frames.
//                      Elevated.
//   sdl <backend> <dir> [ids]
//                      each Bluetooth pad of the Sony, Nintendo, Stadia and
//                      Luna families and each one's USB profile, through the
//                      SDL3.dll in <dir>: open, the driver and bus SDL
//                      reads, the connection state, input, rumble and LEDs.
//                      Elevated.
//
// Exit 0 PASS, 1 FAIL, 2 bad arguments, 3 not elevated.

using System;
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>The catalog's Bluetooth profiles, each with the transport it
    /// declares. A profile added or dropped shows as a difference from this
    /// list.</summary>
    internal static readonly (string Id, bool Le)[] Bluetooth =
    {
        ("amazon-luna-ble", true), ("apple-wireless-keyboard", false),
        ("dualsense-bt", false), ("dualsense-bt-full", false), ("dualsense-edge-bt", false), ("dualshock-4-v2-bt", false),
        ("genesis-nso", false), ("hhkb-studio", true), ("joycon-l", false), ("joycon-r", false),
        ("lenovo-thinkpad-compact-bluetooth-keyboard", false), ("logitech-m557", false),
        ("logitech-mx-anywhere-3s", true), ("logitech-mx-master-3s", true),
        ("microsoft-designer-keyboard", true), ("microsoft-sculpt-touch-mouse", false),
        ("n64-nso", false), ("ps3-remote", false), ("snes-nso", false), ("stadia-controller-bt", true), ("switch-pro", false),
        ("xbox-elite-v2-bt", false), ("xbox-elite-v2-bt-v2", true), ("xbox-one-s-bt", false), ("xbox-one-s-bt-full", false),
        ("xbox-series-xs-bt", true),
    };

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "offline";
        try
        {
            switch (mode)
            {
                case "offline":
                    Offline();
                    break;
                case "live" when args.Length >= 2 && ParseBackend(args[1]) is { } lb:
                    if (!Elevated()) { Console.WriteLine("  refused: live needs elevation"); return 3; }
                    Live(lb, args.Skip(2).ToArray());
                    break;
                case "sdl" when args.Length >= 3 && ParseBackend(args[1]) is { } sb:
                    if (!Elevated()) { Console.WriteLine("  refused: sdl needs elevation"); return 3; }
                    Sdl.Run(sb, args[2], args.Skip(3).ToArray());
                    break;
                default:
                    Console.WriteLine("usage: BluetoothIdentityCheck offline | live <umdf2|usbip|auto> [profile ids] | sdl <umdf2|usbip|auto> <SDL3 dir> [profile ids]");
                    return 2;
            }
        }
        catch (Exception e)
        {
            Check("the run ends without an exception", false, e.ToString());
        }
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static HMBackend? ParseBackend(string s) => s.ToLowerInvariant() switch
    {
        "umdf2" => HMBackend.Umdf2,
        "usbip" => HMBackend.Usbip,
        // What Auto resolves to here, HIDMAESTRO_BACKEND included: the
        // backend the regression battery runs.
        "auto" => HMContext.MachineBackend,
        _ => null,
    };

    static bool Elevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    // ── offline ──────────────────────────────────────────────────────────

    static void Offline()
    {
        var db = ProfileDatabase.LoadEmbedded();
        var byId = db.All.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

        Console.WriteLine("\n-- The catalog's Bluetooth profiles --");
        Check("the catalog load refused no profile", !db.Issues.Any(i => i.Kind == HMProfileLoadIssueKind.Refused),
            string.Join("; ", db.Issues.Select(i => $"{i.ProfileId} {i.JsonPath} {i.Reason}")));
        var inCatalog = db.All.Where(p => p.IsBluetooth).Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var listed = Bluetooth.Select(b => b.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check($"the catalog's Bluetooth profiles are the {listed.Length} this check lists", inCatalog.SequenceEqual(listed),
            "catalog only: " + string.Join(", ", inCatalog.Except(listed)) + " | list only: " + string.Join(", ", listed.Except(inCatalog)));

        foreach (var (id, le) in Bluetooth)
        {
            if (!byId.TryGetValue(id, out var p)) continue;
            Console.WriteLine($"\n-- {id} --");
            Check($"its transport is {(le ? "LE" : "classic")}", p.IsBluetoothLe == le, p.BluetoothTransport ?? "none");
            var hm = new HMProfile(p);
            Check("FormOn(Umdf2) is the profile itself", ReferenceEquals(hm.FormOn(HMBackend.Umdf2), hm));
            string why = BackendSelection.NotSupportedReason(p, HMBackend.Usbip) ?? "";
            Check("the USB/IP backend presents it", hm.SupportsBackend(HMBackend.Usbip), why);
            if (!hm.SupportsBackend(HMBackend.Usbip)) continue;
            var onUsbip = hm.FormOn(HMBackend.Usbip);
            if (p.UsesXinputhid)
            {
                // A pad that reaches XInput through xinputhid: the HID device
                // it is over Bluetooth, under its own id, VID and PID
                // (bluetooth_hid_check has the form itself).
                Check("FormOn(Usbip) is its Bluetooth HID form, under its own id, VID and PID",
                    !ReferenceEquals(onUsbip, hm) && onUsbip.Inner.XinputhidOnTransport && onUsbip.Id == p.Id
                    && onUsbip.VendorId == p.VendorId && onUsbip.ProductId == p.ProductId && onUsbip.Inner.IsBluetooth,
                    $"{onUsbip.Id} {onUsbip.VendorId:X4}:{onUsbip.ProductId:X4} {onUsbip.Inner.Connection}");
            }
            else
            {
                Check("FormOn(Usbip) is the profile itself", ReferenceEquals(onUsbip, hm), onUsbip.Id);
                var table = (bool[]?)typeof(HMController).GetMethod("OtherFormRawLengths",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { p, onUsbip.Inner });
                Check("no raw frame length is refused on USB/IP", table == null,
                    table == null ? "" : string.Join(",", Enumerable.Range(0, table.Length).Where(i => table[i])));
            }
            var presented = onUsbip.Inner;
            var pres = UsbPresentation.Resolve(presented);
            Check("its USB device is derived from the profile: one HID function", pres.Kind == UsbPresentationKind.DerivedHid, pres.Kind.ToString());
            var set = new UsbDescriptorSet(presented);
            int version = presented.VersionNumber ?? 0x0100;
            Check($"the USB device descriptor carries {p.VendorId:X4}:{p.ProductId:X4} and version {version:X4}",
                set.VendorId == p.VendorId && set.ProductId == p.ProductId && set.BcdDevice == version,
                $"{set.VendorId:X4}:{set.ProductId:X4} version {set.BcdDevice:X4}");
            Check("the HID interface serves the presented profile's report descriptor",
                set.ReportDescriptor.AsSpan().SequenceEqual(presented.GetDescriptorBytes()),
                $"{set.ReportDescriptor.Length} bytes against {presented.GetDescriptorBytes()?.Length ?? 0}");

            // The node: the marker of the profile's transport, which hidapi's
            // rule reads as Bluetooth ahead of the USB IDs Windows writes.
            var plan = UsbNodeStamp.For(presented);
            string wantMarker = le ? UsbNodeStamp.LeMarker : UsbNodeStamp.ClassicMarker;
            Check($"the node's marker is the {(le ? "LE" : "classic")} one", plan.BusMarker == wantMarker, plan.BusMarker ?? "none");
            Check("xinputhid goes on the node for a pad that reaches XInput through it, and for no other",
                plan.Xinputhid == p.UsesXinputhid, plan.Xinputhid.ToString());
            if (plan.BusMarker != null)
            {
                var ids = new[] { plan.BusMarker, @"USB\Class_03&SubClass_00&Prot_00", @"USB\Class_03&SubClass_00", @"USB\Class_03" };
                var bus = HidDeviceEnumerator.BusFromIds(ids, new[] { $@"USB\VID_{p.VendorId:X4}&PID_{p.ProductId:X4}&REV_{version:X4}" });
                Check($"hidapi's rule reads a node with the marker first as Bluetooth {(le ? "LE" : "classic")}",
                    bus.Connection == "bluetooth" && bus.BluetoothTransport == (le ? "le" : "classic"), bus.ToString());
            }
        }

        Console.WriteLine("\n-- The marker --");
        foreach (var (name, marker) in new[] { ("classic", UsbNodeStamp.ClassicMarker), ("LE", UsbNodeStamp.LeMarker) })
        {
            string up = marker.ToUpperInvariant();
            // hidapi tests USB first in each ID, so a marker that held those
            // letters would read as USB.
            Check($"the {name} marker names its Bluetooth enumerator and not USB",
                !up.Contains("USB") && up.Contains(name == "LE" ? "BTHLEDEVICE" : "BTHENUM"), marker);
            // The bare service ID is what hidbth.inf and hidbthle.inf match,
            // so the marker must be more than that.
            string bare = name == "LE" ? @"BTHLEDevice\{00001812-0000-1000-8000-00805f9b34fb}" : @"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}";
            Check($"the {name} marker is not the bare service ID Windows' Bluetooth HID INF matches",
                marker.StartsWith(bare, StringComparison.OrdinalIgnoreCase) && marker.Length > bare.Length, marker);
        }
        var plain = HidDeviceEnumerator.BusFromIds(new[] { @"USB\Class_03&SubClass_00&Prot_00", @"USB\Class_03" }, Array.Empty<string>());
        Check("a node without the marker reads as USB", plain.Connection == "usb", plain.ToString());
        foreach (var id in new[] { "dualsense", "switch-pro-usb", "stadia-controller-usb" })
            if (byId.TryGetValue(id, out var usb))
                Check($"{id}, a USB profile, gets no marker", UsbNodeStamp.For(usb).BusMarker == null, UsbNodeStamp.For(usb).BusMarker ?? "");

        Console.WriteLine("\n-- usbForm and usbFormOf, which a load refuses --");
        // The catalog as the SDK carries it: each profile file's text.
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var asm = typeof(HMContext).Assembly;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal)))
        {
            using var st = asm.GetManifestResourceStream(res)!;
            string t = new System.IO.StreamReader(st, System.Text.Encoding.UTF8).ReadToEnd();
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(t) is System.Text.Json.Nodes.JsonObject o && (string?)o["id"] is { } id) texts[id] = t;
            }
            catch (System.Text.Json.JsonException) { }
        }
        var carry = texts.Where(k => System.Text.Json.Nodes.JsonNode.Parse(k.Value) is System.Text.Json.Nodes.JsonObject o
                                     && (o.ContainsKey("usbForm") || o.ContainsKey("usbFormOf"))).Select(k => k.Key).ToArray();
        Check($"none of the catalog's {texts.Count} profile files carries usbForm or usbFormOf", texts.Count > 300 && carry.Length == 0, string.Join(", ", carry));
        if (!texts.TryGetValue("dualsense-bt", out var dsbt)) { Check("dualsense-bt's file is in the SDK", false); return; }
        foreach (var (key, value) in new[] { ("usbForm", "\"dualsense\""), ("usbFormOf", "[\"dualsense-bt\"]") })
        {
            var file = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(dsbt)!;
            file["id"] = "x-bt";
            file[key] = System.Text.Json.Nodes.JsonNode.Parse(value);
            var loaded = ProfileDatabase.LoadTexts(new[] { ("x.json", file.ToJsonString()) }, ModelRegistry.Catalog);
            var issue = loaded.Issues.FirstOrDefault(i => i.Kind == HMProfileLoadIssueKind.Refused);
            Check($"a profile file with {key} is refused, and the refusal names the key",
                issue != null && (issue.JsonPath.Contains(key, StringComparison.Ordinal) || issue.Reason.Contains(key, StringComparison.Ordinal)),
                issue == null ? $"loaded {loaded.All.Count}" : $"{issue.JsonPath}: {issue.Reason}");
        }
    }
}
