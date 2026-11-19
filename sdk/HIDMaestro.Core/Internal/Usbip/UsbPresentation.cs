using System;
using System.Collections.Generic;
using System.Globalization;

namespace HIDMaestro.Internal.Usbip;

/// <summary>
/// The USB device a profile presents on the USB/IP backend, resolved once
/// per profile instance. Authored when the profile's <c>usbConfiguration</c>
/// carries the device and configuration descriptors: those blobs, verbatim.
/// Derived otherwise: one HID interface built from the profile's own HID
/// descriptor and identity, with any pins the <c>usbConfiguration</c> gives
/// applied over single fields. Every reader of the USB configuration on the
/// backend (<see cref="UsbDescriptorSet"/>, <see cref="UsbAudioEngine"/>,
/// <see cref="HMUsbAudio"/>, the WinUSB package) reads <see cref="Spec"/>.
///
/// <para>The derived device, field by field. Device descriptor: bcdUSB
/// 0x0200, class 0 at the device, bMaxPacketSize0 64 (8 at low speed),
/// idVendor and idProduct the profile's, bcdDevice its
/// <c>versionNumber</c> or 0x0100 (the value the UMDF2 driver is given, so
/// <c>HidD_GetAttributes</c> reads the same on both backends), string 1 the
/// manufacturer string when the profile has one, 2 the product string, 3
/// the serial, one configuration. Configuration: one interface, value 1,
/// string 4 when the profile has a configuration string, bmAttributes 0x80
/// (bus powered), 500 mA. Interface 0, class 3, subclass 0, protocol 0, the
/// HID class descriptor 09 21 11 01 00 01 22 with the report descriptor's
/// length, an interrupt IN endpoint 0x81 and, when the descriptor declares
/// an output report, an interrupt OUT endpoint 0x01, each sized to the
/// longest report it carries. Full speed with a 1 ms interval, or high
/// speed with a 1 ms interval (bInterval 4) when a report is longer than 64
/// bytes, so each report is one transaction. The defaults are the four
/// single-HID-interface blocks the catalog carries from real dumps
/// (gamecube-adapter, steam-controller-2, and the HID interfaces of
/// dualsense-composite and dualshock-4-v2-composite).</para>
///
/// <para>The Xbox profiles that reach XInput through the XUSB companion or
/// xinputhid get no derived HID device: a plain HID function would give
/// Windows the identity of a controller that serves Microsoft OS
/// descriptors with none, and Windows remembers that answer per identity
/// (<see cref="NotPresentableReason"/>).</para>
/// </summary>
internal sealed class UsbPresentation
{
    /// <summary>The configuration the device presents, blobs included.</summary>
    public UsbConfigurationSpec Spec { get; }

    /// <summary>True when the SDK built the descriptors from the profile.</summary>
    public bool Derived { get; }

    /// <summary>True when an endpoint of the configuration is isochronous:
    /// only such a device gets an audio engine.</summary>
    public bool HasIsochronous { get; }

    private UsbPresentation(UsbConfigurationSpec spec, bool derived)
    {
        Spec = spec;
        Derived = derived;
        foreach (var iface in spec.Interfaces ?? new List<UsbInterfaceSpec>())
            foreach (var alt in iface?.AltSettings ?? new List<UsbAltSettingSpec>())
                foreach (var ep in alt?.Endpoints ?? new List<UsbEndpointSpec>())
                    if (ep?.TransferType == "isochronous") HasIsochronous = true;
    }

    /// <summary>The presentation of <paramref name="profile"/>. Throws
    /// <see cref="ProfileDefectException"/> naming the JSON path of a pin
    /// that does not fit, and <see cref="HMBackendNotSupportedException"/>
    /// for a profile the backend has no presentation for.</summary>
    public static UsbPresentation Resolve(ControllerProfile profile) =>
        profile.UsbPresentationCache ??= Build(profile);

    private static UsbPresentation Build(ControllerProfile profile)
    {
        var cfg = profile.UsbConfiguration;
        if (cfg != null && cfg.IsAuthored)
        {
            RefusePinsBesideBlobs(cfg);
            return new UsbPresentation(cfg, derived: false);
        }
        if (NotPresentableReason(profile) is { } reason)
            throw new HMBackendNotSupportedException(profile.Id, HMBackend.Usbip, reason);
        return new UsbPresentation(Derive(profile, cfg), derived: true);
    }

    /// <summary>Why the USB/IP backend cannot present
    /// <paramref name="profile"/>, or null when it can. The profile's data
    /// alone decides.</summary>
    public static string? NotPresentableReason(ControllerProfile profile)
    {
        if (profile.UsbConfiguration is { IsAuthored: true }) return null;
        if (!profile.HasDescriptor) return "it has no HID descriptor.";
        var xi = XInputPresentation.Resolve(profile);
        if (xi.XusbCompanion)
            return "it reaches XInput through the XUSB companion, and the USB/IP backend has no XUSB function for it yet.";
        if (xi.Xinputhid)
            return "it reaches XInput through xinputhid, and the USB/IP backend has no GIP function for it yet.";
        return null;
    }

    private static void RefusePinsBesideBlobs(UsbConfigurationSpec cfg)
    {
        const string why = "is a pin over the derived presentation, and this profile authors its descriptors.";
        if (cfg.Device != null) throw new ProfileDefectException("$.usbConfiguration.device", why);
        if (cfg.Hid != null) throw new ProfileDefectException("$.usbConfiguration.hid", why);
        if (cfg.InEndpoint != null) throw new ProfileDefectException("$.usbConfiguration.inEndpoint", why);
        if (cfg.OutEndpoint != null) throw new ProfileDefectException("$.usbConfiguration.outEndpoint", why);
    }

    // ── The derivation ───────────────────────────────────────────────────

    private const string Root = "$.usbConfiguration";

    private static UsbConfigurationSpec Derive(ControllerProfile profile, UsbConfigurationSpec? pins)
    {
        if (pins != null)
        {
            // What only an authored device carries.
            if (pins.Interfaces is { Count: > 0 })
                throw new ProfileDefectException($"{Root}.interfaces", "describes authored descriptors, and this profile gives no deviceDescriptor or configurationDescriptor.");
            if (pins.AudioControls is { Count: > 0 })
                throw new ProfileDefectException($"{Root}.audioControls", "goes with authored audio interfaces, and a derived presentation has none.");
            if (!string.IsNullOrEmpty(pins.OtherSpeedConfigurationDescriptorHex))
                throw new ProfileDefectException($"{Root}.otherSpeedConfigurationDescriptor", "goes with an authored configurationDescriptor.");
        }

        byte[] report = profile.GetDescriptorBytes()!;
        var map = profile.GetOrBuildReportMap();
        int longestIn = 0, longestOut = 0;
        bool declaresOutput = false;
        foreach (var r in map.Reports)
        {
            if (r.Kind == HidReportKind.Input) longestIn = Math.Max(longestIn, r.FrameBytes);
            else if (r.Kind == HidReportKind.Output) { declaresOutput = true; longestOut = Math.Max(longestOut, r.FrameBytes); }
        }
        // The report the data lane goes out as (inputReportSize), and an
        // extended report, which the descriptor declares in its own right.
        longestIn = Math.Max(longestIn, DeviceOrchestrator.DriverInputReport(profile).Length);
        if (profile.ExtendedReport is { Size: > 0 } ext) longestIn = Math.Max(longestIn, ext.Size);
        if (longestIn <= 0) longestIn = 8;

        // Speed: the pin, else full unless a report is longer than a
        // full-speed interrupt packet.
        string speed = pins?.BusSpeed?.ToLowerInvariant() switch
        {
            null => Math.Max(longestIn, longestOut) > 64 ? "high" : "full",
            "high" or "full" or "low" => pins.BusSpeed!.ToLowerInvariant(),
            _ => throw new ProfileDefectException($"{Root}.busSpeed", $"\"{pins.BusSpeed}\" is not one of high, full, low."),
        };
        int packetCap = speed switch { "high" => 1024, "full" => 64, _ => 8 };
        byte defaultInterval = speed switch { "high" => 4, "full" => 1, _ => 10 };
        // A low-speed interrupt packet carries 8 bytes (USB 2.0 5.7.3), and
        // a report goes in one packet.
        if (speed == "low" && Math.Max(longestIn, longestOut) > 8)
            throw new ProfileDefectException($"{Root}.busSpeed",
                $"\"low\" carries interrupt packets of 8 bytes (USB 2.0 5.7.3), and this profile has a {Math.Max(longestIn, longestOut)}-byte report.");

        // Device descriptor.
        var dev = pins?.Device;
        ushort bcdUsb = HexWord(dev?.BcdUsb, $"{Root}.device.bcdUsb") ?? 0x0200;
        if (speed == "high" && bcdUsb < 0x0200)
            throw new ProfileDefectException($"{Root}.device.bcdUsb", $"0x{bcdUsb:X4} is under 0x0200, and a high-speed device reports USB 2.0 (USB 2.0 9.6.1).");
        byte maxPacket0 = dev?.MaxPacketSize0 ?? (byte)(speed == "low" ? 8 : 64);
        // USB 2.0 5.5.3: 8 at low speed, 64 at high speed, any of the four
        // at full speed.
        if (maxPacket0 is not (8 or 16 or 32 or 64))
            throw new ProfileDefectException($"{Root}.device.maxPacketSize0", $"{maxPacket0} is not one of 8, 16, 32, 64.");
        if (speed == "low" && maxPacket0 != 8)
            throw new ProfileDefectException($"{Root}.device.maxPacketSize0", $"{maxPacket0} is not the 8 bytes a low-speed control endpoint carries (USB 2.0 5.5.3).");
        if (speed == "high" && maxPacket0 != 64)
            throw new ProfileDefectException($"{Root}.device.maxPacketSize0", $"{maxPacket0} is not the 64 bytes a high-speed control endpoint carries (USB 2.0 5.5.3).");
        ushort bcdDevice = HexWord(dev?.BcdDevice, $"{Root}.device.bcdDevice") ?? profile.VersionNumber ?? 0x0100;
        byte iManufacturer = string.IsNullOrEmpty(profile.ManufacturerString) ? (byte)0 : (byte)1;
        byte iProduct = string.IsNullOrEmpty(profile.ProductString) ? (byte)0 : (byte)2;
        const byte iSerial = 3;
        byte iConfiguration = string.IsNullOrEmpty(profile.ConfigurationString) ? (byte)0 : (byte)4;
        var device = new byte[]
        {
            18, 0x01, (byte)bcdUsb, (byte)(bcdUsb >> 8), 0x00, 0x00, 0x00, maxPacket0,
            (byte)profile.VendorId, (byte)(profile.VendorId >> 8),
            (byte)profile.ProductId, (byte)(profile.ProductId >> 8),
            (byte)bcdDevice, (byte)(bcdDevice >> 8),
            iManufacturer, iProduct, iSerial, 0x01,
        };

        // The HID interface.
        var hid = pins?.Hid;
        byte interfaceNumber = hid?.InterfaceNumber ?? 0;
        // The configuration has one interface, and bInterfaceNumber is its
        // zero-based index (USB 2.0 9.6.5).
        if (interfaceNumber != 0)
            throw new ProfileDefectException($"{Root}.hid.interfaceNumber", $"{interfaceNumber} is not 0, the number of the one interface this configuration has (USB 2.0 9.6.5).");
        byte subClass = hid?.SubClass ?? 0;
        if (subClass > 1)
            throw new ProfileDefectException($"{Root}.hid.subClass", $"{subClass} is reserved: 0 is no subclass and 1 the boot interface (HID 1.11 4.2).");
        byte protocol = hid?.Protocol ?? 0;
        if (protocol > 2)
            throw new ProfileDefectException($"{Root}.hid.protocol", $"{protocol} is reserved: 1 is the keyboard and 2 the mouse (HID 1.11 4.3).");
        if (protocol != 0 && subClass != 1)
            throw new ProfileDefectException($"{Root}.hid.protocol", $"{protocol} names a boot protocol, which only a boot interface (subClass 1) carries (HID 1.11 4.3).");
        ushort bcdHid = HexWord(hid?.BcdHid, $"{Root}.hid.bcdHid") ?? 0x0111;
        byte country = hid?.CountryCode ?? 0;

        var inEp = Endpoint(pins?.InEndpoint, $"{Root}.inEndpoint", 0x81, longestIn, packetCap, defaultInterval, speed, wantIn: true);
        // The two addresses differ in their direction bit, so they never
        // collide.
        (byte Address, int MaxPacket, byte Interval)? outEp = pins?.OutEndpoint is { None: true } ? null
            : declaresOutput || pins?.OutEndpoint != null
                ? Endpoint(pins?.OutEndpoint, $"{Root}.outEndpoint", 0x01, Math.Max(1, longestOut), packetCap, defaultInterval, speed, wantIn: false)
                : null;

        byte attributes = pins?.Attributes ?? 0x80;
        if ((attributes & 0x80) == 0)
            throw new ProfileDefectException($"{Root}.attributes", $"0x{attributes:X2} has bit 7 clear, which USB 2.0 9.6.3 requires set.");
        if ((attributes & 0x1F) != 0)
            throw new ProfileDefectException($"{Root}.attributes", $"0x{attributes:X2} sets a reserved bit, D4 to D0, which USB 2.0 9.6.3 requires clear.");
        int milliamps = pins?.MaxPowerMilliamps ?? 500;
        if (milliamps < 0 || milliamps > 510)
            throw new ProfileDefectException($"{Root}.maxPowerMilliamps", $"{milliamps} is outside 0 to 510.");
        if (milliamps % 2 != 0)
            throw new ProfileDefectException($"{Root}.maxPowerMilliamps", $"{milliamps} is odd, and bMaxPower counts 2 mA units (USB 2.0 9.6.3).");
        byte configurationValue = pins?.ConfigurationValue ?? 1;
        if (configurationValue == 0)
            throw new ProfileDefectException($"{Root}.configurationValue", "0 is the unconfigured state, which no configuration takes.");

        int numEndpoints = outEp == null ? 1 : 2;
        int total = 9 + 9 + 9 + 7 * numEndpoints;
        var config = new List<byte>(total)
        {
            9, 0x02, (byte)total, (byte)(total >> 8), 0x01, configurationValue, iConfiguration, attributes, (byte)(milliamps / 2),
            9, 0x04, interfaceNumber, 0x00, (byte)numEndpoints, 0x03, subClass, protocol, 0x00,
            9, 0x21, (byte)bcdHid, (byte)(bcdHid >> 8), country, 0x01, 0x22, (byte)report.Length, (byte)(report.Length >> 8),
        };
        void AddEndpoint((byte Address, int MaxPacket, byte Interval) e) =>
            config.AddRange(new byte[] { 7, 0x05, e.Address, 0x03, (byte)e.MaxPacket, (byte)(e.MaxPacket >> 8), e.Interval });
        AddEndpoint(inEp);
        if (outEp != null) AddEndpoint(outEp.Value);

        var endpoints = new List<UsbEndpointSpec>
        {
            new() { Address = inEp.Address, TransferType = "interrupt", MaxPacketSize = inEp.MaxPacket, Interval = inEp.Interval },
        };
        if (outEp != null)
            endpoints.Add(new() { Address = outEp.Value.Address, TransferType = "interrupt", MaxPacketSize = outEp.Value.MaxPacket, Interval = outEp.Value.Interval });

        return new UsbConfigurationSpec
        {
            ConfigurationValue = configurationValue,
            Attributes = attributes,
            MaxPowerMilliamps = milliamps,
            BusSpeed = speed,
            DeviceDescriptorHex = Convert.ToHexString(device),
            ConfigurationDescriptorHex = Convert.ToHexString(config.ToArray()),
            Interfaces = new List<UsbInterfaceSpec>
            {
                new()
                {
                    InterfaceNumber = interfaceNumber,
                    Function = "hid",
                    AltSettings = new List<UsbAltSettingSpec>
                    {
                        new() { AltSetting = 0, InterfaceClass = 0x03, InterfaceSubClass = subClass, InterfaceProtocol = protocol, Endpoints = endpoints },
                    },
                },
            },
            MicrosoftOs = pins?.MicrosoftOs,
            SharedSerial = pins?.SharedSerial ?? false,
            WinUsb = pins?.WinUsb,
        };
    }

    /// <summary>One interrupt endpoint: the pins over the address, the
    /// packet size and the interval, the rest derived. An OUT pin that is
    /// the file's <c>null</c> is handled before this is called, and
    /// <c>"inEndpoint": null</c> reads as no pin.</summary>
    private static (byte Address, int MaxPacket, byte Interval) Endpoint(
        UsbEndpointPins? pin, string path, byte defaultAddress, int longest, int packetCap, byte defaultInterval, string speed, bool wantIn)
    {
        byte address = defaultAddress;
        if (pin?.Address is { } text)
        {
            address = (byte)(HexWord(text, $"{path}.address") is ushort a && a <= 0xFF ? a
                : throw new ProfileDefectException($"{path}.address", $"\"{text}\" is not one byte."));
            if (((address & 0x80) != 0) != wantIn || (address & 0x0F) == 0 || (address & 0x70) != 0)
                throw new ProfileDefectException($"{path}.address",
                    $"0x{address:X2} is not an {(wantIn ? "IN" : "OUT")} endpoint address from 1 to 15.");
        }
        int maxPacket = pin?.MaxPacketSize ?? Math.Min(longest, packetCap);
        if (maxPacket < 1 || maxPacket > packetCap)
            throw new ProfileDefectException($"{path}.maxPacketSize", $"{maxPacket} is outside 1 to {packetCap}, the interrupt packet sizes at this speed.");
        byte interval = pin?.Interval ?? defaultInterval;
        if (interval == 0)
            throw new ProfileDefectException($"{path}.interval", "0 is no interrupt interval.");
        // A high-speed interrupt bInterval is an exponent from 1 to 16
        // (USB 2.0 9.6.6), and a low-speed one 10 to 255 ms (USB 2.0 5.7.4).
        if (speed == "high" && interval > 16)
            throw new ProfileDefectException($"{path}.interval", $"{interval} is past 16: at high speed the period is 2^(bInterval-1) microframes, bInterval 1 to 16 (USB 2.0 9.6.6).");
        if (speed == "low" && interval < 10)
            throw new ProfileDefectException($"{path}.interval", $"{interval} is under 10, the shortest low-speed interrupt interval in milliseconds (USB 2.0 5.7.4).");
        return (address, maxPacket, interval);
    }

    /// <summary>A 16-bit hex value with or without 0x, or null when absent.</summary>
    private static ushort? HexWord(string? text, string path)
    {
        if (text == null) return null;
        string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (digits.Length is < 1 or > 4 || !ushort.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort v))
            throw new ProfileDefectException(path, $"\"{text}\" is not a 16-bit hex value such as \"0x0200\".");
        return v;
    }
}
