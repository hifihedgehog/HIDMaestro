// The derived USB presentation (UsbPresentation): what every profile with
// no usbConfiguration presents on the USB/IP backend.
//
//   1. Which deployable profiles the USB/IP backend presents: every one but
//      the Xbox profiles that reach XInput through xinputhid, which it
//      refuses by name. The profiles with no descriptor are deployable on
//      neither backend. Every profile that reaches XInput through the XUSB
//      companion presents the XUSB function of the XUSB model, its VID, PID
//      and resolved subtype written in, and the golden record holds each.
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

    /// <summary>A catalog profile whose input reports fit one low-speed
    /// packet and which declares no output report.</summary>
    const string LowSpeedId = "daemonbite-nes";

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
        // SupportsBackend(Auto) answers for the backend Auto resolves to on
        // this machine: on a USB/IP machine the profiles that backend does
        // not present are refused, and on a UMDF2 machine none is.
        var autoRefused = deployable.Where(p => !p.SupportsBackend(HMBackend.Auto)).Select(p => p.Id).ToList();
        var autoExpected = HMContext.MachineBackend == HMBackend.Usbip
            ? deployable.Where(p => !p.SupportsBackend(HMBackend.Usbip)).Select(p => p.Id).ToList()
            : new List<string>();
        Check($"Auto refuses exactly the deployable profiles the machine's backend, {HMContext.MachineBackend}, does not present ({autoExpected.Count})",
              autoRefused.SequenceEqual(autoExpected),
              $"refused {string.Join(", ", autoRefused.Take(6))}; expected {string.Join(", ", autoExpected.Take(6))}");

        var refused = deployable.Where(p => !p.SupportsBackend(HMBackend.Usbip)).ToList();
        var xbox = refused.Where(p => XInputPresentation.Resolve(p.Inner).Xinputhid).ToList();
        Console.WriteLine($"  [note] {refused.Count} deployable profiles the USB/IP backend does not present yet: {string.Join(", ", refused.Select(p => p.Id))}");
        Check("the USB/IP backend refuses only the Xbox profiles that reach XInput through xinputhid",
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

        var xusb = deployable.Where(p => XInputPresentation.Resolve(p.Inner).XusbCompanion && p.Inner.UsbConfiguration == null).ToList();
        // A Bluetooth identity whose USB form authors its configuration (a
        // Joy-Con presents the charging grip) presents that, not a derived
        // device.
        static bool AuthoredOnUsbip(HMProfile p) =>
            p.SupportsBackend(HMBackend.Usbip) && p.FormOn(HMBackend.Usbip).Inner.UsbConfiguration is { IsAuthored: true };
        var derived = deployable.Where(p => p.SupportsBackend(HMBackend.Usbip) && p.Inner.UsbConfiguration == null && !xusb.Contains(p) && !AuthoredOnUsbip(p)).ToList();
        var authored = deployable.Where(p => p.Inner.UsbConfiguration is { IsAuthored: true } || (p.Inner.UsbConfiguration == null && AuthoredOnUsbip(p))).ToList();
        Console.WriteLine($"  [note] {derived.Count} derive their presentation, {xusb.Count} present the XUSB function, {authored.Count} author it");
        Check("every UMDF2-default profile with a descriptor and no XInput stack of its own derives one",
              derived.Count + xusb.Count + refused.Count + authored.Count == deployable.Count,
              $"{derived.Count} + {xusb.Count} + {refused.Count} + {authored.Count} of {deployable.Count}");

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

        // The XUSB function, one line each, with the speed written "xusb".
        var xusbFailures = new List<string>();
        foreach (var p in xusb.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            string? why = XusbDefect(p, out var set);
            if (why != null) { xusbFailures.Add($"{p.Id}: {why}"); continue; }
            golden.Append(p.Id).Append(" xusb")
                  .Append(" device=").Append(Convert.ToHexString(set!.DeviceDescriptor))
                  .Append(" config=").Append(Convert.ToHexString(set.ConfigurationDescriptor)).AppendLine();
        }
        Check($"all {xusb.Count} XUSB companion profiles present the XUSB function: the model's descriptors with the profile's VID, PID and subtype, no HID interface",
              xusb.Count > 0 && xusbFailures.Count == 0, string.Join("; ", xusbFailures.Take(5)));

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
            // A Bluetooth identity whose usbForm authors a configuration
            // presents that form on the USB/IP backend.
            var inner = p.FormOn(HMBackend.Usbip).Inner;
            var pr = UsbPresentation.Resolve(inner);
            if (!ReferenceEquals(pr.Spec, inner.UsbConfiguration) || pr.Derived)
                notOwn.Add(p.Id);
        }
        Check($"the {authored.Count} authored personas present their own usbConfiguration, not a derived one", notOwn.Count == 0,
              string.Join(", ", notOwn));
        Check("an authored persona with isochronous endpoints keeps its audio engine, one without has none",
              UsbPresentation.Resolve(ctx.GetProfile("dualsense-composite")!.Inner).HasIsochronous
              && !UsbPresentation.Resolve(ctx.GetProfile("steam-controller-2")!.Inner).HasIsochronous);

        CheckPins(ctx);
    }

    /// <summary>Null when <paramref name="p"/> presents the XUSB function
    /// as the model gives it, else the first rule it breaks.</summary>
    static string? XusbDefect(HMProfile p, out UsbDescriptorSet? set)
    {
        set = null;
        UsbPresentation presentation;
        try
        {
            presentation = UsbPresentation.Resolve(p.Inner);
            set = new UsbDescriptorSet(p.Inner, 0);
        }
        catch (Exception e) { return $"{e.GetType().Name}: {e.Message}"; }
        if (presentation.Kind != UsbPresentationKind.XusbFunction) return $"kind {presentation.Kind}";
        // The descriptor set serves the function's blobs with the profile's
        // values written in (xusb_persona_check holds them to the retail
        // dumps), and its subtype is the one the UMDF2 companion answers.
        var f = XusbFunction.For(p.Inner);
        var built = XusbFunction.Build(p.Inner, f);
        if (Convert.ToHexString(set.DeviceDescriptor) != built.DeviceDescriptorHex) return "device descriptor";
        if (Convert.ToHexString(set.ConfigurationDescriptor) != built.ConfigurationDescriptorHex) return "configuration descriptor";
        foreach (var (descriptor, at, value) in f.Fields)
            if (value == "subType" && set.ConfigurationDescriptor[at] != XusbFunction.SubType(p.Inner)) return "subtype";
        if (set.HasHidInterface || set.HidInterfaceNumber != 0 || set.ReportDescriptor.Length != 0) return "a HID interface";
        if (set.GetMicrosoftOsFeature(4) != null || set.GetDescriptor(0x03, 0xEE, 0) != null) return "Microsoft OS descriptors";
        if (presentation.Report is not { AlwaysArmed: true } || !ReferenceEquals(presentation.Report, f.Report)) return "the function's report";
        if (set.UsbInstanceId == null || !set.UsbInstanceId.StartsWith($@"USB\VID_{set.VendorId:X4}&PID_{set.ProductId:X4}\", StringComparison.Ordinal))
            return $"USB instance id {set.UsbInstanceId ?? "none"}";
        return null;
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
            Hid = new UsbHidPins { InterfaceNumber = 0, CountryCode = 33 },
            InEndpoint = new UsbEndpointPins { Address = "0x84", MaxPacketSize = 64, Interval = 6 },
            OutEndpoint = new UsbEndpointPins { Address = "0x03", MaxPacketSize = 64, Interval = 6 },
        }), 0);
        Check("the pins land in their fields",
              pinned.Speed == 3 && pinned.BcdDevice == 0x0200 && pinned.ConfigurationDescriptor[7] == 0xC0 && pinned.ConfigurationDescriptor[8] == 50
              && pinned.GetHidDescriptor(0x21, 0)![4] == 33 && pinned.Endpoints[0x84].Interval == 6 && pinned.Endpoints[0x03].MaxPacketSize == 64,
              Convert.ToHexString(pinned.ConfigurationDescriptor));
        Check("the fields no pin names stay derived",
              pinned.VendorId == plain.VendorId && pinned.ProductId == plain.ProductId
              && pinned.GetHidDescriptor(0x21, 0)![7] == plain.GetHidDescriptor(0x21, 0)![7]);
        var none = new UsbDescriptorSet(With(new UsbConfigurationSpec { OutEndpoint = new UsbEndpointPins { None = true } }), 0);
        Check("outEndpoint null pins a device with no OUT endpoint", none.Endpoints.Count == 1 && plain.Endpoints.Count == 2);

        // Each pin that contradicts USB 2.0, HID 1.11 or the bus speed is
        // refused at its own JSON path, one row per rule.
        void Refused(string rule, UsbConfigurationSpec cfg, string path)
        {
            string? got = Defect(cfg);
            Check($"refused at {path}: {rule}", got == path, got ?? "accepted");
        }
        Refused("a speed that is not high, full or low", new UsbConfigurationSpec { BusSpeed = "super" }, "$.usbConfiguration.busSpeed");
        Refused("an IN pin with an OUT address", new UsbConfigurationSpec { InEndpoint = new UsbEndpointPins { Address = "0x01" } }, "$.usbConfiguration.inEndpoint.address");
        Refused("a full-speed interrupt packet over 64 bytes (USB 2.0 5.7.3)", new UsbConfigurationSpec { InEndpoint = new UsbEndpointPins { MaxPacketSize = 65 } }, "$.usbConfiguration.inEndpoint.maxPacketSize");
        Refused("a control packet size that is not 8, 16, 32 or 64 (USB 2.0 5.5.3)", new UsbConfigurationSpec { Device = new UsbDevicePins { MaxPacketSize0 = 12 } }, "$.usbConfiguration.device.maxPacketSize0");
        Refused("bmAttributes with D7 clear (USB 2.0 9.6.3)", new UsbConfigurationSpec { Attributes = 0x40 }, "$.usbConfiguration.attributes");
        Refused("bmAttributes with a reserved bit D4..D0 set (USB 2.0 9.6.3)", new UsbConfigurationSpec { Attributes = 0x81 }, "$.usbConfiguration.attributes");
        Refused("a current bMaxPower cannot carry: it counts 2 mA units (USB 2.0 9.6.3)", new UsbConfigurationSpec { MaxPowerMilliamps = 101 }, "$.usbConfiguration.maxPowerMilliamps");
        Refused("high speed with a control packet under 64 bytes (USB 2.0 5.5.3)", new UsbConfigurationSpec { BusSpeed = "high", Device = new UsbDevicePins { MaxPacketSize0 = 8 } }, "$.usbConfiguration.device.maxPacketSize0");
        Refused("high speed with bcdUSB under 0x0200 (USB 2.0 9.6.1)", new UsbConfigurationSpec { BusSpeed = "high", Device = new UsbDevicePins { BcdUsb = "0x0110" } }, "$.usbConfiguration.device.bcdUsb");
        Refused("a high-speed interrupt interval past 16 (USB 2.0 9.6.6)", new UsbConfigurationSpec { BusSpeed = "high", InEndpoint = new UsbEndpointPins { Interval = 17 } }, "$.usbConfiguration.inEndpoint.interval");
        Refused("the same on the OUT endpoint (USB 2.0 9.6.6)", new UsbConfigurationSpec { BusSpeed = "high", OutEndpoint = new UsbEndpointPins { Interval = 255 } }, "$.usbConfiguration.outEndpoint.interval");
        Refused("low speed with reports over 8 bytes (USB 2.0 5.7.3)", new UsbConfigurationSpec { BusSpeed = "low" }, "$.usbConfiguration.busSpeed");
        Refused("an interface number past the one interface the configuration has (USB 2.0 9.6.5)", new UsbConfigurationSpec { Hid = new UsbHidPins { InterfaceNumber = 3 } }, "$.usbConfiguration.hid.interfaceNumber");
        Refused("a reserved HID subclass (HID 1.11 4.2)", new UsbConfigurationSpec { Hid = new UsbHidPins { SubClass = 2 } }, "$.usbConfiguration.hid.subClass");
        Refused("a boot protocol on an interface that is no boot interface (HID 1.11 4.3)", new UsbConfigurationSpec { Hid = new UsbHidPins { Protocol = 1 } }, "$.usbConfiguration.hid.protocol");
        Refused("a reserved boot protocol (HID 1.11 4.3)", new UsbConfigurationSpec { Hid = new UsbHidPins { SubClass = 1, Protocol = 3 } }, "$.usbConfiguration.hid.protocol");

        // A device whose reports fit one low-speed packet takes a low-speed
        // pin whole.
        var lowSource = ctx.GetProfile(LowSpeedId)!.Inner;
        var low = new UsbDescriptorSet(WithLow(new UsbConfigurationSpec { BusSpeed = "low" }), 0);
        Check($"a low-speed pin on {LowSpeedId}, whose reports fit 8 bytes, gives an 8-byte control endpoint, 8-byte interrupt packets and a 10 ms interval",
              low.Speed == 1 && low.DeviceDescriptor[7] == 8 && low.Endpoints.Values.All(e => e.MaxPacketSize <= 8 && e.Interval == 10),
              Convert.ToHexString(low.ConfigurationDescriptor));

        ControllerProfile WithLow(UsbConfigurationSpec cfg)
        {
            var p = lowSource.Clone();
            p.UsbConfiguration = cfg;
            return p;
        }
        string? DefectOf(ControllerProfile p)
        {
            try { _ = new UsbDescriptorSet(p, 0); return null; }
            catch (ProfileDefectException e) { return e.JsonPath; }
            catch (Exception e) { return $"{e.GetType().Name}: {e.Message}"; }
        }
        void RefusedLow(string rule, UsbConfigurationSpec cfg, string path)
        {
            string? got = DefectOf(WithLow(cfg));
            Check($"refused at {path}: {rule}", got == path, got ?? "accepted");
        }
        RefusedLow("low speed with a 64-byte control packet (USB 2.0 5.5.3)", new UsbConfigurationSpec { BusSpeed = "low", Device = new UsbDevicePins { MaxPacketSize0 = 64 } }, "$.usbConfiguration.device.maxPacketSize0");
        RefusedLow("a low-speed interrupt interval under 10 ms (USB 2.0 5.7.4)", new UsbConfigurationSpec { BusSpeed = "low", InEndpoint = new UsbEndpointPins { Interval = 4 } }, "$.usbConfiguration.inEndpoint.interval");

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
