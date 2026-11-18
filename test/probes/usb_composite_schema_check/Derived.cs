// The derived USB presentation (UsbPresentation): what every profile with
// no usbConfiguration presents on the USB/IP backend.
//
//   1. Which deployable profiles the USB/IP backend presents: every one but
//      the Xbox profiles that reach XInput through the XUSB companion or
//      xinputhid, which it refuses by name. The profiles with no descriptor
//      are deployable on neither backend.
//   2. Every presentable profile derives, and the descriptor set the
//      create builds holds its descriptors: one HID interface, the HID
//      class descriptor's length the descriptor's, VID, PID and bcdDevice
//      the profile's, endpoints sized to the longest report at the speed
//      the reports call for, no isochronous endpoint, so no audio engine.
//   3. A golden record of every derived device and configuration
//      descriptor, derived-presentations.txt beside this file. "write"
//      rewrites it.
//   4. The authored personas keep their own blobs, the object the profile
//      carries.
//   5. Pins: each pin lands in its field, a pin that does not fit is
//      refused at its JSON path, a pin beside authored blobs is refused,
//      and the JSON null of outEndpoint survives a profile's copy.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static partial class Program
{
    const string GoldenName = "derived-presentations.txt";

    static void CheckDerived(HMContext ctx, string repoRoot, bool write)
    {
        Console.WriteLine("\n-- Derived USB presentations --");
        var all = ctx.AllProfiles;
        var deployable = all.Where(p => p.IsDeployable).ToList();
        var placeholders = all.Where(p => !p.IsDeployable).ToList();
        Console.WriteLine($"  [note] {all.Count} profiles, {deployable.Count} deployable, {placeholders.Count} with no descriptor");
        Check("a profile that cannot be deployed has no descriptor and runs on no backend",
              placeholders.All(p => !p.Inner.HasDescriptor && !p.SupportsBackend(HMBackend.Umdf2)
                                    && !p.SupportsBackend(HMBackend.Usbip) && !p.SupportsBackend(HMBackend.Auto)),
              string.Join(", ", placeholders.Where(p => p.Inner.HasDescriptor || p.SupportsBackend(HMBackend.Auto)).Select(p => p.Id)));
        Check("every deployable profile runs on Auto", deployable.All(p => p.SupportsBackend(HMBackend.Auto)),
              string.Join(", ", deployable.Where(p => !p.SupportsBackend(HMBackend.Auto)).Select(p => p.Id)));

        var refused = deployable.Where(p => !p.SupportsBackend(HMBackend.Usbip)).ToList();
        var xbox = refused.Where(p =>
        {
            var xi = XInputPresentation.Resolve(p.Inner);
            return xi.XusbCompanion || xi.Xinputhid;
        }).ToList();
        Console.WriteLine($"  [note] {refused.Count} deployable profiles the USB/IP backend does not present yet: {string.Join(", ", refused.Select(p => p.Id))}");
        Check("the USB/IP backend refuses only the Xbox profiles that reach XInput through the companion or xinputhid",
              refused.Count == xbox.Count && refused.Count > 0,
              string.Join(", ", refused.Except(xbox).Select(p => p.Id)));
        Check("none of the Xbox profiles it refuses has a usbConfiguration", refused.All(p => p.Inner.UsbConfiguration == null));
        foreach (var p in refused)
        {
            try { _ = UsbPresentation.Resolve(p.Inner); Check($"{p.Id}: the presentation refuses", false); }
            catch (HMBackendNotSupportedException e)
            {
                if (p == refused[0])
                    Check("the refusal names the profile, the backend and the reason",
                          e.ProfileId == p.Id && e.Backend == HMBackend.Usbip && e.Message.Contains("XInput", StringComparison.Ordinal), e.Message);
            }
        }

        var derived = deployable.Where(p => p.SupportsBackend(HMBackend.Usbip) && p.Inner.UsbConfiguration == null).ToList();
        var authored = deployable.Where(p => p.Inner.UsbConfiguration is { IsAuthored: true }).ToList();
        Console.WriteLine($"  [note] {derived.Count} derive their presentation, {authored.Count} author it");
        Check("every UMDF2-default profile with a descriptor and no XInput stack of its own derives one",
              derived.Count + refused.Count + authored.Count == deployable.Count,
              $"{derived.Count} + {refused.Count} + {authored.Count} of {deployable.Count}");

        var golden = new StringBuilder();
        golden.AppendLine("# The device and configuration descriptors each profile with no usbConfiguration presents on the USB/IP backend.");
        golden.AppendLine("# usb_composite_schema_check (S42) compares them. 'UsbCompositeSchemaCheck.exe write' rewrites this file.");
        var failures = new List<string>();
        foreach (var p in derived.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            string? why = DerivedDefect(p, out var set, out var presentation);
            if (why != null) { failures.Add($"{p.Id}: {why}"); continue; }
            golden.Append(p.Id).Append(' ').Append(presentation!.Spec.BusSpeed)
                  .Append(" device=").Append(Convert.ToHexString(set!.DeviceDescriptor))
                  .Append(" config=").Append(Convert.ToHexString(set.ConfigurationDescriptor)).AppendLine();
        }
        Check($"all {derived.Count} derive, and their descriptor sets meet the derivation's rules", failures.Count == 0,
              string.Join("; ", failures.Take(5)));

        string goldenPath = Path.Combine(repoRoot, "test", "probes", "usb_composite_schema_check", GoldenName);
        string text = golden.ToString().Replace("\r\n", "\n");
        if (write)
        {
            File.WriteAllText(goldenPath, text);
            Console.WriteLine($"  [note] wrote {goldenPath}");
        }
        else if (!File.Exists(goldenPath))
            Check("the golden record of the derived descriptors is here", false, goldenPath);
        else
        {
            var want = File.ReadAllText(goldenPath).Replace("\r\n", "\n").Split('\n');
            var got = text.Split('\n');
            var differ = want.Except(got).Concat(got.Except(want)).Where(l => l.Length > 0 && l[0] != '#').ToList();
            Check("every derived descriptor matches the golden record", differ.Count == 0,
                  string.Join(" | ", differ.Take(4).Select(l => l.Length > 120 ? l[..120] : l)));
        }

        var notOwn = new List<string>();
        foreach (var p in authored)
        {
            var pr = UsbPresentation.Resolve(p.Inner);
            if (!ReferenceEquals(pr.Spec, p.Inner.UsbConfiguration) || pr.Derived)
                notOwn.Add(p.Id);
        }
        Check($"the {authored.Count} authored personas present their own usbConfiguration, not a derived one", notOwn.Count == 0,
              string.Join(", ", notOwn));
        Check("an authored persona with isochronous endpoints keeps its audio engine, one without has none",
              UsbPresentation.Resolve(ctx.GetProfile("dualsense-composite")!.Inner).HasIsochronous
              && !UsbPresentation.Resolve(ctx.GetProfile("steam-controller-2")!.Inner).HasIsochronous);

        CheckPins(ctx);
    }

    /// <summary>Null when the derived presentation of <paramref name="p"/>
    /// meets every rule, else the first it breaks.</summary>
    static string? DerivedDefect(HMProfile p, out UsbDescriptorSet? set, out UsbPresentation? presentation)
    {
        set = null;
        presentation = null;
        try
        {
            presentation = UsbPresentation.Resolve(p.Inner);
            set = new UsbDescriptorSet(p.Inner, 0);
        }
        catch (Exception e) { return $"{e.GetType().Name}: {e.Message}"; }
        if (!presentation.Derived) return "not derived";
        if (presentation.HasIsochronous) return "has an isochronous endpoint";
        // A Bluetooth identity with a USB form derives the form's device
        // (usbForm): every field below is the form's.
        p = p.FormOn(HMBackend.Usbip);
        var d = set.DeviceDescriptor;
        var c = set.ConfigurationDescriptor;
        if (set.VendorId != p.VendorId || set.ProductId != p.ProductId) return "VID or PID";
        ushort version = p.Inner.VersionNumber ?? 0x0100;
        if (set.BcdDevice != version) return $"bcdDevice 0x{set.BcdDevice:X4}, versionNumber 0x{version:X4}";
        if (d[2] != 0x00 || d[3] != 0x02 || d[4] != 0 || d[17] != 1) return "bcdUSB, class or configuration count";
        if (set.NumInterfaces != 1 || !set.HasHidInterface || set.HidInterfaceNumber != 0) return "not one HID interface";
        if (c[2] + (c[3] << 8) != c.Length) return "wTotalLength";
        if (c[7] != 0x80 || c[8] != 250) return $"bmAttributes 0x{c[7]:X2}, bMaxPower {c[8]}";
        var hid = set.GetHidDescriptor(0x21, 0)!;
        int declared = hid[7] | (hid[8] << 8);
        if (declared != p.GetDescriptorBytes()!.Length) return "HID class descriptor length";
        if (hid[2] != 0x11 || hid[3] != 0x01) return "bcdHID";
        var map = p.Inner.GetOrBuildReportMap();
        int longestIn = Math.Max(map.Reports.Where(r => r.Kind == HidReportKind.Input).Select(r => r.FrameBytes).DefaultIfEmpty(0).Max(),
                                 DeviceOrchestrator.DriverInputReport(p.Inner).Length);
        if (p.Inner.ExtendedReport is { Size: > 0 } ext) longestIn = Math.Max(longestIn, ext.Size);
        var outs = map.Reports.Where(r => r.Kind == HidReportKind.Output).ToList();
        int longestOut = outs.Select(r => r.FrameBytes).DefaultIfEmpty(0).Max();
        bool high = Math.Max(longestIn, longestOut) > 64;
        if (set.Speed != (high ? 3u : 2u)) return $"speed {set.Speed}, longest report {Math.Max(longestIn, longestOut)}";
        int cap = high ? 1024 : 64;
        if (!set.Endpoints.TryGetValue(0x81, out var inEp) || inEp.TransferType != 3 || inEp.MaxPacketSize != Math.Min(longestIn > 0 ? longestIn : 8, cap)
            || inEp.Interval != (high ? 4 : 1))
            return "IN endpoint";
        bool hasOut = set.Endpoints.TryGetValue(0x01, out var outEp);
        if (hasOut != (outs.Count > 0)) return $"OUT endpoint {(hasOut ? "present" : "absent")}, {outs.Count} output reports";
        if (hasOut && (outEp.TransferType != 3 || outEp.MaxPacketSize != Math.Min(longestOut, cap))) return "OUT endpoint size";
        if (set.Endpoints.Count != (hasOut ? 2 : 1)) return "endpoint count";
        if (set.UsbInstanceId == null || !set.UsbInstanceId.StartsWith($@"USB\VID_{p.VendorId:X4}&PID_{p.ProductId:X4}\", StringComparison.Ordinal))
            return $"USB instance id {set.UsbInstanceId ?? "none"}";
        return null;
    }

    static void CheckPins(HMContext ctx)
    {
        Console.WriteLine("\n-- Pins over a derived presentation --");
        var source = ctx.GetProfile("dualshock-4-v2")!.Inner;

        ControllerProfile With(UsbConfigurationSpec cfg)
        {
            var p = source.Clone();
            p.UsbConfiguration = cfg;
            return p;
        }
        string? Defect(UsbConfigurationSpec cfg)
        {
            try { _ = new UsbDescriptorSet(With(cfg), 0); return null; }
            catch (ProfileDefectException e) { return e.JsonPath; }
            catch (Exception e) { return $"{e.GetType().Name}: {e.Message}"; }
        }

        var plain = new UsbDescriptorSet(source.Clone(), 0);
        var pinned = new UsbDescriptorSet(With(new UsbConfigurationSpec
        {
            BusSpeed = "high",
            Attributes = 0xC0,
            MaxPowerMilliamps = 100,
            Device = new UsbDevicePins { BcdDevice = "0x0200", MaxPacketSize0 = 64 },
            Hid = new UsbHidPins { InterfaceNumber = 3, CountryCode = 0 },
            InEndpoint = new UsbEndpointPins { Address = "0x84", MaxPacketSize = 64, Interval = 6 },
            OutEndpoint = new UsbEndpointPins { Address = "0x03", MaxPacketSize = 64, Interval = 6 },
        }), 0);
        Check("the pins land in their fields",
              pinned.Speed == 3 && pinned.BcdDevice == 0x0200 && pinned.ConfigurationDescriptor[7] == 0xC0 && pinned.ConfigurationDescriptor[8] == 50
              && pinned.HidInterfaceNumber == 3 && pinned.Endpoints[0x84].Interval == 6 && pinned.Endpoints[0x03].MaxPacketSize == 64,
              Convert.ToHexString(pinned.ConfigurationDescriptor));
        Check("the fields no pin names stay derived",
              pinned.VendorId == plain.VendorId && pinned.ProductId == plain.ProductId
              && pinned.GetHidDescriptor(0x21, 3)![7] == plain.GetHidDescriptor(0x21, 0)![7]);
        var none = new UsbDescriptorSet(With(new UsbConfigurationSpec { OutEndpoint = new UsbEndpointPins { None = true } }), 0);
        Check("outEndpoint null pins a device with no OUT endpoint", none.Endpoints.Count == 1 && plain.Endpoints.Count == 2);

        Check("a pin that does not fit is refused at its path",
              Defect(new UsbConfigurationSpec { BusSpeed = "super" }) == "$.usbConfiguration.busSpeed"
              && Defect(new UsbConfigurationSpec { InEndpoint = new UsbEndpointPins { Address = "0x01" } }) == "$.usbConfiguration.inEndpoint.address"
              && Defect(new UsbConfigurationSpec { InEndpoint = new UsbEndpointPins { MaxPacketSize = 65 } }) == "$.usbConfiguration.inEndpoint.maxPacketSize"
              && Defect(new UsbConfigurationSpec { Device = new UsbDevicePins { MaxPacketSize0 = 12 } }) == "$.usbConfiguration.device.maxPacketSize0"
              && Defect(new UsbConfigurationSpec { Attributes = 0x40 }) == "$.usbConfiguration.attributes"
              && Defect(new UsbConfigurationSpec { InEndpoint = new UsbEndpointPins { None = true } }) == "$.usbConfiguration.inEndpoint",
              string.Join(", ", new[]
              {
                  Defect(new UsbConfigurationSpec { BusSpeed = "super" }),
                  Defect(new UsbConfigurationSpec { InEndpoint = new UsbEndpointPins { Address = "0x01" } }),
              }));
        var low = new UsbDescriptorSet(With(new UsbConfigurationSpec { BusSpeed = "low" }), 0);
        Check("a low-speed pin gives an 8-byte control endpoint, 8-byte interrupt packets and a 10 ms interval",
              low.Speed == 1 && low.DeviceDescriptor[7] == 8 && low.Endpoints.Values.All(e => e.MaxPacketSize == 8 && e.Interval == 10));

        var composite = ctx.GetProfile("dualsense-composite")!.Inner.Clone();
        composite.UsbConfiguration!.Hid = new UsbHidPins { InterfaceNumber = 1 };
        string? beside;
        try { _ = new UsbDescriptorSet(composite, 0); beside = null; }
        catch (ProfileDefectException e) { beside = e.JsonPath; }
        Check("a pin beside authored blobs is refused", beside == "$.usbConfiguration.hid", beside ?? "accepted");

        // The file's null for outEndpoint, read, copied and written.
        var parsed = ControllerProfile.Parse(Encoding.UTF8.GetBytes(
            "{\"id\":\"pin-probe\",\"name\":\"Pin probe\",\"vendor\":\"Probe\",\"vid\":\"0x1234\",\"pid\":\"0x5678\","
          + "\"productString\":\"Pin probe\",\"descriptor\":\"" + source.Descriptor + "\","
          + "\"usbConfiguration\":{\"busSpeed\":\"full\",\"outEndpoint\":null}}"));
        Check("the file's null for outEndpoint reads as the pin of no endpoint",
              parsed?.UsbConfiguration?.OutEndpoint is { None: true } && parsed.UsbConfiguration.InEndpoint == null);
        var copy = parsed!.Clone();
        Check("a copy of the profile keeps the pin, and an absent pin stays absent",
              copy.UsbConfiguration?.OutEndpoint is { None: true } && copy.UsbConfiguration.InEndpoint == null
              && ctx.GetProfile("dualshock-4-v2")!.Inner.Clone().UsbConfiguration == null);
    }
}
