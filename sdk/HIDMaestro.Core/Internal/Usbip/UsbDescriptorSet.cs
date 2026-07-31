using System;
using System.Collections.Generic;
using System.Text;

namespace HIDMaestro.Internal.Usbip;

/// <summary>The descriptor store for one emulated composite device
/// (issue #39). Owns the verbatim blobs the profile carries (device,
/// configuration, other-speed configuration), the profile's HID report
/// descriptor, and the string table, and answers every GET_DESCRIPTOR
/// with real-pad bytes.
///
/// <para>Construction validates the blobs against each other and against
/// the structured <see cref="UsbConfigurationSpec"/>, because the blob is
/// the wire truth and the structured spec drives routing; if the two
/// disagree the profile is mis-authored and the device must not come up
/// half-right. Checks: device blob VID/PID match the profile, the config
/// blob's wTotalLength matches its byte count, every endpoint in the blob
/// appears in the structured spec with the same transfer type and packet
/// size, and the HID class descriptor's wDescriptorLength equals the
/// profile report descriptor's length.</para></summary>
internal sealed class UsbDescriptorSet
{
    public byte[] DeviceDescriptor { get; }
    public byte[] ConfigurationDescriptor { get; }
    public byte[]? OtherSpeedConfiguration { get; }
    public byte[] ReportDescriptor { get; }
    public byte[] DeviceQualifier { get; }

    public byte ConfigurationValue { get; }
    public byte NumInterfaces { get; }
    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public ushort BcdDevice { get; }

    /// <summary>usb_device_speed for the import reply: 3 high, 2 full
    /// (usbip-win2 include/usbip/ch9.h).</summary>
    public uint Speed { get; }

    private readonly string? _manufacturer;
    private readonly string? _product;
    private readonly byte _iManufacturer;
    private readonly byte _iProduct;
    private readonly byte _iSerial;

    /// <summary>Endpoint table parsed from the configuration blob. Keyed
    /// by bEndpointAddress (direction bit included).</summary>
    public IReadOnlyDictionary<byte, EndpointInfo> Endpoints => _endpoints;
    private readonly Dictionary<byte, EndpointInfo> _endpoints = new();

    /// <summary>The 9-byte HID class descriptor found inside the config
    /// blob, and the interface number it belongs to.</summary>
    public byte HidInterfaceNumber { get; }
    private readonly byte[] _hidClassDescriptor;

    internal readonly struct EndpointInfo
    {
        public EndpointInfo(byte address, byte attributes, ushort maxPacketSize, byte interval,
                            byte interfaceNumber, byte altSetting)
        {
            Address = address; Attributes = attributes; MaxPacketSize = maxPacketSize;
            Interval = interval; InterfaceNumber = interfaceNumber; AltSetting = altSetting;
        }
        public byte Address { get; }
        public byte Attributes { get; }
        public ushort MaxPacketSize { get; }
        public byte Interval { get; }
        public byte InterfaceNumber { get; }
        public byte AltSetting { get; }
        public bool IsIn => (Address & 0x80) != 0;
        public byte Number => (byte)(Address & 0x0F);
        public int TransferType => Attributes & 0x03; // 1 iso, 3 interrupt
    }

    public UsbDescriptorSet(ControllerProfile profile)
    {
        var cfg = profile.UsbConfiguration
            ?? throw new InvalidOperationException($"Profile '{profile.Id}' has no usbConfiguration.");
        DeviceDescriptor = FromHex(cfg.DeviceDescriptorHex, "deviceDescriptor");
        ConfigurationDescriptor = FromHex(cfg.ConfigurationDescriptorHex, "configurationDescriptor");
        OtherSpeedConfiguration = cfg.OtherSpeedConfigurationDescriptorHex != null
            ? FromHex(cfg.OtherSpeedConfigurationDescriptorHex, "otherSpeedConfigurationDescriptor") : null;
        ReportDescriptor = profile.GetDescriptorBytes()
            ?? throw new InvalidOperationException($"Profile '{profile.Id}' has no HID report descriptor.");

        if (DeviceDescriptor.Length != 18 || DeviceDescriptor[0] != 18 || DeviceDescriptor[1] != 0x01)
            throw new InvalidOperationException("deviceDescriptor is not an 18-byte USB device descriptor.");

        VendorId = (ushort)(DeviceDescriptor[8] | (DeviceDescriptor[9] << 8));
        ProductId = (ushort)(DeviceDescriptor[10] | (DeviceDescriptor[11] << 8));
        BcdDevice = (ushort)(DeviceDescriptor[12] | (DeviceDescriptor[13] << 8));
        Speed = cfg.BusSpeed?.Equals("full", StringComparison.OrdinalIgnoreCase) == true ? 2u : 3u;
        _iManufacturer = DeviceDescriptor[14];
        _iProduct = DeviceDescriptor[15];
        _iSerial = DeviceDescriptor[16];
        _manufacturer = profile.ManufacturerString;
        _product = profile.ProductString;

        ushort profileVid = profile.VendorId, profilePid = profile.ProductId;
        if (VendorId != profileVid || ProductId != profilePid)
            throw new InvalidOperationException(
                $"Profile '{profile.Id}': deviceDescriptor VID/PID {VendorId:X4}:{ProductId:X4} " +
                $"does not match the profile's {profileVid:X4}:{profilePid:X4}.");

        // Device qualifier, synthesized from the device descriptor per USB
        // 2.0 ch. 9.6.2. Matches the real pad's dump byte-for-byte
        // (0A 06 00 02 00 00 00 40 01 00).
        DeviceQualifier = new byte[10]
        {
            0x0A, 0x06,
            DeviceDescriptor[2], DeviceDescriptor[3],           // bcdUSB
            DeviceDescriptor[4], DeviceDescriptor[5], DeviceDescriptor[6], // class/sub/proto
            DeviceDescriptor[7],                                 // bMaxPacketSize0
            DeviceDescriptor[17],                                // bNumConfigurations
            0x00,
        };

        // Walk the configuration blob: header sanity, endpoint table,
        // HID class descriptor.
        var blob = ConfigurationDescriptor;
        if (blob.Length < 9 || blob[1] != 0x02)
            throw new InvalidOperationException("configurationDescriptor does not start with a configuration header.");
        int total = blob[2] | (blob[3] << 8);
        if (total != blob.Length)
            throw new InvalidOperationException(
                $"configurationDescriptor wTotalLength {total} != blob length {blob.Length}.");
        NumInterfaces = blob[4];
        ConfigurationValue = blob[5];

        byte curIface = 0xFF, curAlt = 0;
        byte hidIface = 0xFF;
        byte[]? hidClass = null;
        for (int off = 0; off + 2 <= blob.Length;)
        {
            int len = blob[off];
            if (len < 2 || off + len > blob.Length)
                throw new InvalidOperationException($"configurationDescriptor is malformed at offset {off}.");
            byte type = blob[off + 1];
            if (type == 0x04) // interface
            {
                curIface = blob[off + 2];
                curAlt = blob[off + 3];
                if (blob[off + 5] == 0x03 && hidIface == 0xFF) hidIface = curIface;
            }
            else if (type == 0x05) // endpoint
            {
                byte addr = blob[off + 2];
                var info = new EndpointInfo(addr, blob[off + 3],
                    (ushort)(blob[off + 4] | (blob[off + 5] << 8)), blob[off + 6], curIface, curAlt);
                _endpoints[addr] = info;
            }
            else if (type == 0x21) // HID class descriptor
            {
                hidClass = new byte[len];
                Array.Copy(blob, off, hidClass, 0, len);
                int declared = hidClass[7] | (hidClass[8] << 8);
                if (declared != ReportDescriptor.Length)
                    throw new InvalidOperationException(
                        $"HID class descriptor declares a {declared}-byte report descriptor " +
                        $"but the profile's is {ReportDescriptor.Length} bytes.");
            }
            off += len;
        }
        if (hidClass == null || hidIface == 0xFF)
            throw new InvalidOperationException("configurationDescriptor has no HID interface.");
        _hidClassDescriptor = hidClass;
        HidInterfaceNumber = hidIface;

        // Cross-check the structured spec against the blob's endpoint table.
        foreach (var iface in cfg.Interfaces)
        {
            foreach (var alt in iface.AltSettings)
            {
                foreach (var ep in alt.Endpoints)
                {
                    if (!_endpoints.TryGetValue(ep.Address, out var found))
                        throw new InvalidOperationException(
                            $"Structured spec endpoint 0x{ep.Address:X2} is absent from the configuration blob.");
                    bool typeOk = ep.TransferType switch
                    {
                        "isochronous" => found.TransferType == 1,
                        "interrupt" => found.TransferType == 3,
                        _ => false,
                    };
                    if (!typeOk || found.MaxPacketSize != ep.MaxPacketSize || found.Interval != ep.Interval)
                        throw new InvalidOperationException(
                            $"Structured spec endpoint 0x{ep.Address:X2} " +
                            $"({ep.TransferType}, {ep.MaxPacketSize}B, interval {ep.Interval}) " +
                            $"disagrees with the blob " +
                            $"(attributes 0x{found.Attributes:X2}, {found.MaxPacketSize}B, interval {found.Interval}).");
                }
            }
        }
    }

    /// <summary>Answer a standard GET_DESCRIPTOR. Returns null to stall
    /// (unknown descriptor), matching the real pad, which stalls the
    /// Microsoft OS string (0xEE) and everything else it lacks.</summary>
    public byte[]? GetDescriptor(byte type, byte index, ushort langId)
    {
        switch (type)
        {
            case 0x01: return DeviceDescriptor;
            case 0x02: return index == 0 ? ConfigurationDescriptor : null;
            case 0x03: return GetStringDescriptor(index);
            // A full-speed-only device stalls Device_Qualifier and
            // Other_Speed (USB 2.0 ch. 9.6.2); the DualShock 4 v2 dump
            // shows neither. A high-speed device always answers the
            // qualifier (the DualSense and Edge dumps both show it), but
            // the other-speed blob is served only when the profile
            // carries a real capture of it; the Edge has no full-speed
            // capture yet, so it answers the qualifier and stalls
            // Other_Speed, a named residual.
            case 0x06: return Speed >= 3 ? DeviceQualifier : null;
            case 0x07: return index == 0 ? OtherSpeedConfiguration : null;
            default: return null;
        }
    }

    /// <summary>HID-class GET_DESCRIPTOR on the HID interface: 0x22 is the
    /// report descriptor, 0x21 the HID class descriptor.</summary>
    public byte[]? GetHidDescriptor(byte type)
        => type switch { 0x22 => ReportDescriptor, 0x21 => _hidClassDescriptor, _ => null };

    private byte[]? GetStringDescriptor(byte index)
    {
        if (index == 0)
            return new byte[] { 0x04, 0x03, 0x09, 0x04 }; // one LANGID: en-US
        string? s = index == _iManufacturer && _iManufacturer != 0 ? _manufacturer
                  : index == _iProduct && _iProduct != 0 ? _product
                  : index == _iSerial && _iSerial != 0 ? null // real pad has no serial string
                  : null;
        if (s == null) return null;
        var bytes = Encoding.Unicode.GetBytes(s);
        var d = new byte[2 + bytes.Length];
        d[0] = (byte)d.Length;
        d[1] = 0x03;
        bytes.CopyTo(d, 2);
        return d;
    }

    private static byte[] FromHex(string? hex, string field)
    {
        if (string.IsNullOrEmpty(hex))
            throw new InvalidOperationException($"usbConfiguration.{field} is required for the usbip backend.");
        return Convert.FromHexString(hex);
    }
}
