using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HIDMaestro.Internal.Usbip;

/// <summary>The descriptor store for one emulated USB device (issue #39).
/// Owns the device, configuration and other-speed configuration blobs of
/// the profile's presentation (<see cref="UsbPresentation"/>: authored
/// verbatim or derived), the profile's HID report descriptor, and the
/// string table, and answers every GET_DESCRIPTOR.
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

    /// <summary>bInterfaceClass, bInterfaceSubClass and bInterfaceProtocol of
    /// the configuration's first interface descriptor, from which Windows
    /// builds a single-interface device's compatible IDs.</summary>
    public (byte Class, byte SubClass, byte Protocol) FirstInterfaceClass { get; }
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
    private readonly byte _iConfiguration;
    private readonly string? _serial;
    private readonly string? _configurationName;

    /// <summary>The serial this device serves at its iSerial index: the
    /// profile's captured one or the identity's. The Switch 2 Pro persona
    /// also writes it into its flash image, where SDL reads it (issue
    /// #66).</summary>
    public string? SerialString => _serial;

    /// <summary>The instance id Windows gives the device on the host
    /// controller's hub, <c>USB\VID_vvvv&amp;PID_pppp\&lt;serial&gt;</c>: the
    /// hub driver names a device by its serial when it serves one Windows
    /// takes (no character below 0x21 or above 0x7F, and no comma). Null for
    /// a device without such a serial, and for one whose units all carry the
    /// same serial (<see cref="UsbConfigurationSpec.SharedSerial"/>), which
    /// Windows names by its port instead.</summary>
    public string? UsbInstanceId { get; }

    /// <summary>The identity's <see cref="DeviceIdentity.SerialVariant"/>,
    /// which varies a captured serial here and the captured text of an
    /// instanceSerial patch in the device's feature answers (issue
    /// #72).</summary>
    public int SerialVariant { get; }

    // Issue #66. Microsoft OS 1.0 descriptors, null unless the profile
    // declares them. A device without them stalls string 0xEE and every
    // vendor request, as the Sony and Valve pads do.
    // A device bound to WinUSB by these descriptors gets them after its
    // construction (ServeWinUsb), the string last, since Windows asks for it
    // before the two features.
    private volatile byte[]? _msOsString;
    private volatile byte[]? _msOsCompatId;
    private volatile byte[]? _msOsProperties;

    /// <summary>Endpoint table parsed from the configuration blob. Keyed
    /// by bEndpointAddress (direction bit included).</summary>
    public IReadOnlyDictionary<byte, EndpointInfo> Endpoints => _endpoints;
    private readonly Dictionary<byte, EndpointInfo> _endpoints = new();

    /// <summary>The 9-byte HID class descriptor found inside the config
    /// blob, and the interface number it belongs to. This is the PRIMARY
    /// HID interface: the one serving the profile's own report descriptor,
    /// carrying its input reports and answering its feature requests. On a
    /// device with no HID interface (<see cref="HasHidInterface"/>) it is
    /// the interface the profile's reports travel on, and there is no
    /// class descriptor.</summary>
    public byte HidInterfaceNumber { get; }
    private readonly byte[]? _hidClassDescriptor;

    /// <summary>False for a device whose profile names an interface
    /// <c>"xusb"</c> or <c>"gip"</c> (<see cref="UsbInterfaceSpec.Function"/>):
    /// it has no HID interface and serves no HID descriptor, and its reports
    /// travel on that interface's interrupt endpoints. True for every other
    /// persona.</summary>
    public bool HasHidInterface { get; }

    /// <summary>The function of the interface a device with no HID
    /// interface reports on: <c>"xusb"</c> or <c>"gip"</c>
    /// (<see cref="UsbInterfaceSpec.Function"/>). Null on every device with a
    /// HID interface.</summary>
    public string? ReportFunction { get; }

    /// <summary>Issue #56. Secondary HID interfaces, keyed by interface
    /// number: the class descriptor found in the blob paired with the
    /// report descriptor the profile declares for it. A Steam Deck persona
    /// presents two (keyboard and mouse, the pair its lizard mode drives)
    /// alongside its vendor controller interface. Empty for every
    /// single-HID profile, which is all three Sony composites.</summary>
    private readonly Dictionary<byte, (byte[] ClassDescriptor, byte[] ReportDescriptor)> _secondaryHid = new();

    /// <summary>Interface numbers of the secondary HID interfaces.</summary>
    public IReadOnlyCollection<byte> SecondaryHidInterfaces => _secondaryHid.Keys;

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

    /// <summary>Vary a captured serial per controller instance.
    ///
    /// Real units have unique serials and Steam keys its per-controller
    /// config off the string it reads: the client writes
    /// <c>configset_&lt;serial&gt;.vdf</c>. Two virtual Decks sharing one
    /// serial therefore share one configuration, which is wrong the moment
    /// somebody runs a pair of them.
    ///
    /// Instance 0 keeps the captured serial byte for byte, so a single
    /// controller stays identical to the unit it was dumped from and the
    /// golden descriptor hashes do not move. Later instances add the index
    /// into the trailing digit run, which is where a real serial varies and
    /// which preserves both the length and the character classes the
    /// manufacturer's format uses.</summary>
    internal static string? InstanceSerial(string? captured, int index)
    {
        if (index <= 0 || string.IsNullOrEmpty(captured)) return captured;
        var chars = captured.ToCharArray();
        int at = chars.Length - 1;
        int carry = index;
        while (at >= 0 && carry > 0 && char.IsDigit(chars[at]))
        {
            int v = (chars[at] - '0') + (carry % 10);
            carry /= 10;
            if (v > 9) { v -= 10; carry++; }
            chars[at] = (char)('0' + v);
            at--;
        }
        return new string(chars);
    }

    /// <summary>Build the store for one persona. <paramref name="identity"/>
    /// supplies the USB serial (issue #60): a profile with a captured serial
    /// keeps it, varied by <see cref="DeviceIdentity.SerialVariant"/>; a
    /// profile without one gets <see cref="DeviceIdentity.SyntheticSerial"/>
    /// and its device descriptor's iSerial is pointed at the next free
    /// string index, so Windows keys the USB instance id on the serial
    /// instead of on the vhci port. Null means the index identity, which
    /// is what every caller before the identity existed got.</summary>
    public UsbDescriptorSet(ControllerProfile profile, int index = 0, DeviceIdentity? identity = null)
    {
        // A Bluetooth identity with a USB form is that form on the USB bus
        // (ControllerProfile.UsbFormOf), descriptor and strings alike.
        profile = profile.UsbFormOf(HMBackend.Usbip);
        identity ??= DeviceIdentity.ForIndex(index);
        SerialVariant = identity.SerialVariant;
        // The authored blobs, or the ones the presentation derives from the
        // profile, which then meet every check below by construction.
        var cfg = UsbPresentation.Resolve(profile).Spec;
        CheckStructure(cfg);
        DeviceDescriptor = FromHex(cfg.DeviceDescriptorHex, "deviceDescriptor");
        ConfigurationDescriptor = FromHex(cfg.ConfigurationDescriptorHex, "configurationDescriptor");
        OtherSpeedConfiguration = cfg.OtherSpeedConfigurationDescriptorHex != null
            ? FromHex(cfg.OtherSpeedConfigurationDescriptorHex, "otherSpeedConfigurationDescriptor") : null;
        // A device with no HID interface names the interface its reports
        // travel on "xusb" (UsbInterfaceSpec.Function) and carries no report
        // descriptor. Every other persona is held to both.
        int xusbAt = cfg.Interfaces.FindIndex(x => x.Function == "xusb");
        string xusbPath = $"$.usbConfiguration.interfaces[{xusbAt}].function";
        // A device whose messages travel on a GIP data interface has no HID
        // interface either, and builds every message from its state block
        // and protocol. One interface carries a device's reports.
        int gipAt = cfg.Interfaces.FindIndex(x => x.Function == "gip");
        string gipPath = $"$.usbConfiguration.interfaces[{gipAt}].function";
        if (gipAt >= 0 && xusbAt >= 0)
            throw new ProfileDefectException(gipPath, $"\"gip\" and {xusbPath} \"xusb\" each mark the one interface a device with no HID interface reports on.");
        if (xusbAt >= 0 && profile.HasDescriptor)
            throw new ProfileDefectException("$.descriptor",
                $"has no interface to present it: {xusbPath} \"xusb\" marks a device with no HID interface.");
        // With no descriptor, the always-armed extended report is the one
        // report the profile describes, and the device sends nothing else.
        if (xusbAt >= 0 && profile.ExtendedReport == null)
            throw new ProfileDefectException("$.extendedReport",
                "is required for a device with no HID interface: it is the report the device sends.");
        if (xusbAt >= 0 && !profile.ExtendedReport!.AlwaysArmed)
            throw new ProfileDefectException("$.extendedReport.alwaysArmed",
                "must be true on a device with no HID interface, which has no other report to send.");
        if (gipAt >= 0 && profile.HasDescriptor)
            throw new ProfileDefectException("$.descriptor",
                $"has no interface to present it: {gipPath} \"gip\" marks a device with no HID interface.");
        if (gipAt >= 0 && !profile.BuildsItsOwnReports)
            throw new ProfileDefectException("$.inputReports",
                "is required for a device that reports on a GIP interface: it builds every message it sends from its state block.");
        ReportDescriptor = profile.GetDescriptorBytes()
            ?? (xusbAt >= 0 || gipAt >= 0 ? Array.Empty<byte>()
                : throw new ProfileDefectException("$.descriptor", "is required for the usbip backend: the HID interface presents it."));

        if (DeviceDescriptor.Length != 18 || DeviceDescriptor[0] != 18 || DeviceDescriptor[1] != 0x01)
            throw new ProfileDefectException("$.usbConfiguration.deviceDescriptor", "is not an 18-byte USB device descriptor.");

        VendorId = (ushort)(DeviceDescriptor[8] | (DeviceDescriptor[9] << 8));
        ProductId = (ushort)(DeviceDescriptor[10] | (DeviceDescriptor[11] << 8));
        BcdDevice = (ushort)(DeviceDescriptor[12] | (DeviceDescriptor[13] << 8));
        // The import reply's speed, Linux usb_device_speed: 1 low, 2 full,
        // 3 high. usbip-win2 hands each to UdeCx as that speed
        // (drivers/ude/device.cpp to_udex_speed).
        Speed = cfg.BusSpeed?.Equals("full", StringComparison.OrdinalIgnoreCase) == true ? 2u
            : cfg.BusSpeed?.Equals("low", StringComparison.OrdinalIgnoreCase) == true ? 1u
            : 3u;
        _iManufacturer = DeviceDescriptor[14];
        _iProduct = DeviceDescriptor[15];
        _iSerial = DeviceDescriptor[16];
        _iConfiguration = ConfigurationDescriptor.Length > 6 ? ConfigurationDescriptor[6] : (byte)0;
        if (!string.IsNullOrEmpty(profile.SerialString))
        {
            // A device whose units all carry one serial keeps it on every
            // identity, as a second real unit does (UsbConfigurationSpec.SharedSerial).
            _serial = cfg.SharedSerial
                ? profile.SerialString
                : InstanceSerial(profile.SerialString, identity.SerialVariant);
        }
        else
        {
            // No captured serial: serve the identity's. The real pad declares
            // iSerial 0, so the descriptor has to point at a string index for
            // Windows to read one; take the first index none of the other
            // strings use. This is the one byte of the captured device
            // descriptor a persona changes, and it is what makes
            // USB\VID_054C&PID_0CE6\<serial> the same on every life.
            _serial = identity.SyntheticSerial;
            if (_iSerial == 0)
            {
                byte next = 1;
                while (next == _iManufacturer || next == _iProduct || next == _iConfiguration) next++;
                _iSerial = next;
                DeviceDescriptor[16] = next;
            }
        }
        _configurationName = profile.ConfigurationString;
        _manufacturer = profile.ManufacturerString;
        _product = profile.ProductString;
        if (!cfg.SharedSerial && _iSerial != 0 && !string.IsNullOrEmpty(_serial)
            && _serial.All(c => c > ' ' && c < (char)0x7F && c != ','))
            UsbInstanceId = $@"USB\VID_{profile.VendorId:X4}&PID_{profile.ProductId:X4}\{_serial}";

        ushort profileVid = profile.VendorId, profilePid = profile.ProductId;
        if (VendorId != profileVid || ProductId != profilePid)
            throw new ProfileDefectException("$.usbConfiguration.deviceDescriptor",
                $"its VID/PID {VendorId:X4}:{ProductId:X4} does not match the profile's {profileVid:X4}:{profilePid:X4}.");

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
            throw new ProfileDefectException(ConfigurationPath, "does not start with a configuration header.");
        int total = blob[2] | (blob[3] << 8);
        if (total != blob.Length)
            throw new ProfileDefectException(ConfigurationPath, $"its wTotalLength {total} is not its length {blob.Length}.");
        NumInterfaces = blob[4];
        ConfigurationValue = blob[5];

        // The PRIMARY HID interface is the one the structured spec marks
        // function "hid"; it serves the profile's own report descriptor. A
        // single-HID profile (every Sony composite) names exactly one, and
        // this resolves to the same interface the old first-HID rule found.
        byte declaredPrimary = 0xFF;
        foreach (var iface in cfg.Interfaces)
            if (iface.Function == "hid") { declaredPrimary = iface.InterfaceNumber; break; }

        // Report descriptors the spec declares per interface, for the
        // secondary HID interfaces (issue #56).
        var declaredReports = new Dictionary<byte, byte[]>();
        for (int i = 0; i < cfg.Interfaces.Count; i++)
            for (int j = 0; j < cfg.Interfaces[i].AltSettings.Count; j++)
                if (cfg.Interfaces[i].AltSettings[j].ReportDescriptor is { Length: > 0 } rdHex)
                    declaredReports[cfg.Interfaces[i].InterfaceNumber] =
                        HexAt(rdHex, $"$.usbConfiguration.interfaces[{i}].altSettings[{j}].reportDescriptor");

        byte curIface = 0xFF, curAlt = 0;
        byte hidIface = 0xFF;
        bool sawInterface = false;
        var classByIface = new Dictionary<byte, byte[]>();
        // Each interface's class and subclass, from its first descriptor.
        var classOf = new Dictionary<byte, (byte Class, byte SubClass)>();
        // The same with the protocol, for an interface named "gip".
        var protocolOf = new Dictionary<byte, (byte Class, byte SubClass, byte Protocol)>();
        // Every endpoint descriptor, alternate settings included. _endpoints
        // keeps one per address, the last one written.
        var everyEndpoint = new List<EndpointInfo>();
        for (int off = 0; off + 2 <= blob.Length;)
        {
            int len = blob[off];
            if (len < 2 || off + len > blob.Length)
                throw new ProfileDefectException(ConfigurationPath, $"is malformed at offset {off}.");
            byte type = blob[off + 1];
            if (type == 0x04) // interface
            {
                curIface = blob[off + 2];
                curAlt = blob[off + 3];
                classOf.TryAdd(curIface, (blob[off + 5], blob[off + 6]));
                protocolOf.TryAdd(curIface, (blob[off + 5], blob[off + 6], blob[off + 7]));
                if (blob[off + 5] == 0x03 && hidIface == 0xFF) hidIface = curIface;
                if (!sawInterface)
                {
                    FirstInterfaceClass = (blob[off + 5], blob[off + 6], blob[off + 7]);
                    sawInterface = true;
                }
            }
            else if (type == 0x05) // endpoint
            {
                byte addr = blob[off + 2];
                var info = new EndpointInfo(addr, blob[off + 3],
                    (ushort)(blob[off + 4] | (blob[off + 5] << 8)), blob[off + 6], curIface, curAlt);
                _endpoints[addr] = info;
                everyEndpoint.Add(info);
            }
            // An XUSB interface carries a descriptor of this type too, the
            // XUSB interface descriptor ([MS-XUSBI] 2.2.4.5), which is no
            // HID class descriptor.
            else if (type == 0x21 && xusbAt < 0) // HID class descriptor
            {
                var thisClass = new byte[len];
                Array.Copy(blob, off, thisClass, 0, len);
                classByIface[curIface] = thisClass;
            }
            off += len;
        }
        if (xusbAt >= 0)
        {
            // The reports travel on the XUSB interface: vendor class 0xFF,
            // subclass 0x5D ([MS-XUSBI] 2.2.4.3), with the interrupt IN
            // endpoint the input report goes out on (3.2).
            byte number = cfg.Interfaces[xusbAt].InterfaceNumber;
            if (hidIface != 0xFF)
                throw new ProfileDefectException(xusbPath,
                    $"\"xusb\" marks a device with no HID interface, and interface {hidIface} of the configuration descriptor is one.");
            if (!classOf.TryGetValue(number, out var kind))
                throw new ProfileDefectException(xusbPath,
                    $"\"xusb\" names interface {number}, which the configuration descriptor lacks.");
            if (kind.Class != 0xFF || kind.SubClass != 0x5D)
                throw new ProfileDefectException(xusbPath,
                    $"\"xusb\" names interface {number}, whose class is 0x{kind.Class:X2} and subclass 0x{kind.SubClass:X2} " +
                    "in the configuration descriptor. An XUSB interface is class 0xFF, subclass 0x5D.");
            if (!everyEndpoint.Exists(e => e.InterfaceNumber == number && e.TransferType == 3 && e.IsIn))
                throw new ProfileDefectException(xusbPath,
                    $"\"xusb\" names interface {number}, which has no interrupt IN endpoint to report on.");
            HidInterfaceNumber = number;
            ReportFunction = "xusb";
        }
        else if (gipAt >= 0)
        {
            // The GIP data interface: vendor class 0xFF, subclass 0x47 and
            // protocol 0xD0, with an interrupt IN and an interrupt OUT
            // endpoint (Microsoft's GIP specification, tables 36 to 38).
            byte number = cfg.Interfaces[gipAt].InterfaceNumber;
            if (hidIface != 0xFF)
                throw new ProfileDefectException(gipPath,
                    $"\"gip\" marks a device with no HID interface, and interface {hidIface} of the configuration descriptor is one.");
            if (!protocolOf.TryGetValue(number, out var kind))
                throw new ProfileDefectException(gipPath,
                    $"\"gip\" names interface {number}, which the configuration descriptor lacks.");
            if (kind.Class != 0xFF || kind.SubClass != 0x47 || kind.Protocol != 0xD0)
                throw new ProfileDefectException(gipPath,
                    $"\"gip\" names interface {number}, whose class, subclass and protocol are 0x{kind.Class:X2}, 0x{kind.SubClass:X2} " +
                    $"and 0x{kind.Protocol:X2} in the configuration descriptor. A GIP data interface is class 0xFF, subclass 0x47, protocol 0xD0.");
            if (!everyEndpoint.Exists(e => e.InterfaceNumber == number && e.TransferType == 3 && e.IsIn)
                || !everyEndpoint.Exists(e => e.InterfaceNumber == number && e.TransferType == 3 && !e.IsIn))
                throw new ProfileDefectException(gipPath,
                    $"\"gip\" names interface {number}, which lacks the interrupt IN and interrupt OUT endpoints a GIP data interface carries.");
            HidInterfaceNumber = number;
            ReportFunction = "gip";
        }
        else
        {
            if (classByIface.Count == 0 || hidIface == 0xFF)
                throw new ProfileDefectException(ConfigurationPath, "has no HID interface.");

            // A spec-declared "hid" function wins; otherwise the first HID
            // interface in the blob, which is the pre-#56 rule verbatim.
            if (declaredPrimary != 0xFF && classByIface.ContainsKey(declaredPrimary))
                hidIface = declaredPrimary;
            byte[] hidClass = classByIface[hidIface];

            // Every HID class descriptor must agree with the descriptor its own
            // interface serves: the profile's for the primary, the spec's for a
            // secondary. A device that declares one length then serves another
            // does not enumerate, so a mismatch is refused at create time.
            int declaredPrimaryLen = hidClass[7] | (hidClass[8] << 8);
            if (declaredPrimaryLen != ReportDescriptor.Length)
                throw new ProfileDefectException(ConfigurationPath,
                    $"its HID class descriptor declares a {declaredPrimaryLen}-byte report descriptor, " +
                    $"and the profile's descriptor is {ReportDescriptor.Length} bytes.");
            foreach (var kv in classByIface)
            {
                if (kv.Key == hidIface) continue;
                int specAt = cfg.Interfaces.FindIndex(x => x.InterfaceNumber == kv.Key);
                string ifacePath = specAt >= 0 ? $"$.usbConfiguration.interfaces[{specAt}]" : "$.usbConfiguration.interfaces";
                if (!declaredReports.TryGetValue(kv.Key, out var rd))
                    throw new ProfileDefectException(ifacePath,
                        $"interface {kv.Key} is a HID interface in the configuration descriptor, and no alternate setting gives its reportDescriptor.");
                int declared = kv.Value[7] | (kv.Value[8] << 8);
                if (declared != rd.Length)
                    throw new ProfileDefectException(ifacePath,
                        $"interface {kv.Key}'s HID class descriptor declares a {declared}-byte report " +
                        $"descriptor, and its reportDescriptor is {rd.Length} bytes.");
                _secondaryHid[kv.Key] = (kv.Value, rd);
            }
            _hidClassDescriptor = hidClass;
            HidInterfaceNumber = hidIface;
            HasHidInterface = true;
        }

        // Cross-check the structured spec against the blob's endpoint
        // descriptors, each against the one in its own interface and
        // alternate setting.
        for (int i = 0; i < cfg.Interfaces.Count; i++)
        {
            var iface = cfg.Interfaces[i];
            for (int j = 0; j < iface.AltSettings.Count; j++)
            {
                var alt = iface.AltSettings[j];
                for (int k = 0; k < alt.Endpoints.Count; k++)
                {
                    var ep = alt.Endpoints[k];
                    string epPath = $"$.usbConfiguration.interfaces[{i}].altSettings[{j}].endpoints[{k}]";
                    int at = everyEndpoint.FindIndex(e => e.Address == ep.Address
                        && e.InterfaceNumber == iface.InterfaceNumber && e.AltSetting == alt.AltSetting);
                    if (at < 0)
                        throw new ProfileDefectException(epPath,
                            $"endpoint 0x{ep.Address:X2} is absent from the configuration descriptor " +
                            $"(interface {iface.InterfaceNumber}, alternate setting {alt.AltSetting}).");
                    var found = everyEndpoint[at];
                    bool typeOk = ep.TransferType switch
                    {
                        "isochronous" => found.TransferType == 1,
                        "bulk" => found.TransferType == 2,
                        "interrupt" => found.TransferType == 3,
                        _ => false,
                    };
                    if (!typeOk || found.MaxPacketSize != ep.MaxPacketSize || found.Interval != ep.Interval)
                        throw new ProfileDefectException(epPath,
                            $"endpoint 0x{ep.Address:X2} " +
                            $"({ep.TransferType}, {ep.MaxPacketSize}B, interval {ep.Interval}) " +
                            $"disagrees with the configuration descriptor " +
                            $"(attributes 0x{found.Attributes:X2}, {found.MaxPacketSize}B, interval {found.Interval}).");
                }
            }
        }

        // A low-speed device has an 8-byte control endpoint, no bulk
        // endpoints and interrupt endpoints of at most 8 bytes (USB 2.0
        // 5.5.3, 5.8.3 and 5.7.3), and Linux allows it no isochronous ones
        // (low_speed_maxpacket_maxes, drivers/usb/core/config.c). Every
        // endpoint descriptor of every alternate setting is checked, as
        // usb_parse_endpoint checks each, and wMaxPacketSize whole: bits
        // 12..11 count additional transactions on a high-speed periodic
        // endpoint alone (USB 2.0 9.6.6), and Linux masks them only there.
        if (Speed == 1)
        {
            string at = $"Profile '{profile.Id}': usbConfiguration.busSpeed \"low\"";
            if (DeviceDescriptor[7] != 8)
                throw new InvalidOperationException(
                    $"{at}: the device descriptor's bMaxPacketSize0 {DeviceDescriptor[7]} is not the 8 bytes a low-speed control endpoint carries.");
            foreach (var ep in everyEndpoint)
            {
                string which = $"interface {ep.InterfaceNumber} alternate setting {ep.AltSetting} endpoint 0x{ep.Address:X2}";
                if (ep.TransferType is 1 or 2)
                    throw new InvalidOperationException(
                        $"{at}: {which} is {(ep.TransferType == 1 ? "isochronous" : "bulk")}, which a low-speed device does not have.");
                if (ep.TransferType == 3 && ep.MaxPacketSize > 8)
                    throw new InvalidOperationException(
                        $"{at}: {which} has wMaxPacketSize 0x{ep.MaxPacketSize:X4}, over the 8 bytes a low-speed interrupt endpoint carries.");
            }
        }

        // The device serves each address from one descriptor (_endpoints,
        // the last one written) and never switches interrupt or bulk
        // endpoints with SET_INTERFACE. An isochronous URB is stamped with
        // that descriptor's setting (HandleSubmit), so its stream follows
        // the one setting that declares it. So an address keeps its
        // transfer type across alternate settings, an interrupt or bulk
        // address keeps its whole descriptor, and an isochronous address
        // is declared in one setting alone.
        static string TypeName(EndpointInfo e) => e.TransferType switch
        {
            0 => "control",
            1 => "isochronous",
            2 => "bulk",
            _ => "interrupt",
        };
        static string Shape(EndpointInfo e) =>
            $"{TypeName(e)}, wMaxPacketSize 0x{e.MaxPacketSize:X4}, bInterval {e.Interval}, " +
            $"in interface {e.InterfaceNumber} alternate setting {e.AltSetting}";
        for (int i = 0; i < everyEndpoint.Count; i++)
        {
            var first = everyEndpoint[i];
            for (int k = i + 1; k < everyEndpoint.Count; k++)
            {
                var other = everyEndpoint[k];
                if (other.Address != first.Address) continue;
                string both = $"Profile '{profile.Id}': configurationDescriptor endpoint 0x{first.Address:X2} is {Shape(first)}, and {Shape(other)}.";
                if (other.TransferType != first.TransferType)
                    throw new InvalidOperationException($"{both} An endpoint keeps one transfer type across alternate settings.");
                if (first.TransferType == 1)
                    throw new InvalidOperationException($"{both} The device serves an isochronous endpoint from the one alternate setting that declares it.");
                if (other.Attributes != first.Attributes || other.MaxPacketSize != first.MaxPacketSize || other.Interval != first.Interval)
                    throw new InvalidOperationException($"{both} An interrupt or bulk endpoint keeps one descriptor across alternate settings.");
            }
        }

        // Microsoft OS 1.0 descriptors (issue #66). All three or none: a
        // device that answers the string and then stalls a feature request
        // leaves its interface with no driver. Each blob states its own
        // length, so a mis-authored one is refused here. A device that
        // reports on an XUSB or a GIP interface may leave the extended
        // properties out: Windows matches its driver on the compatible ID
        // alone, and such a device stalls every other feature request
        // ([MS-XUSBI] 2.2.4.6, and the GIP specification's "Extended
        // Properties Request being STALLed").
        var os = cfg.MicrosoftOs;
        if (os != null)
        {
            _msOsString = FromHex(os.StringHex, "microsoftOs.string");
            _msOsCompatId = FromHex(os.ExtendedCompatIdHex, "microsoftOs.extendedCompatId");
            _msOsProperties = (xusbAt >= 0 || gipAt >= 0) && os.ExtendedPropertiesHex == null
                ? null
                : FromHex(os.ExtendedPropertiesHex, "microsoftOs.extendedProperties");

            bool signature = _msOsString.Length == 18 && _msOsString[0] == 18 && _msOsString[1] == 0x03
                && Encoding.Unicode.GetString(_msOsString, 2, 14) == "MSFT100";
            if (!signature)
                throw new InvalidOperationException(
                    "microsoftOs.string is not the 18-byte MSFT100 string descriptor.");
            CheckMicrosoftOsFeature(_msOsCompatId, 4, 16, "extendedCompatId");
            if (_msOsProperties != null) CheckMicrosoftOsFeature(_msOsProperties, 5, 10, "extendedProperties");
        }
    }

    /// <summary>A Microsoft OS 1.0 feature descriptor opens with its total
    /// length (u32), bcdVersion 1.00 and its feature index.</summary>
    private static void CheckMicrosoftOsFeature(byte[] blob, int index, int headerSize, string field)
    {
        if (blob.Length < headerSize)
            throw new InvalidOperationException($"microsoftOs.{field} is shorter than its {headerSize}-byte header.");
        uint declared = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(blob);
        int featureIndex = blob[6] | (blob[7] << 8);
        if (declared != blob.Length || featureIndex != index)
            throw new InvalidOperationException(
                $"microsoftOs.{field} declares length {declared} and index {featureIndex}, " +
                $"but is {blob.Length} bytes and must carry index {index}.");
    }

    /// <summary>True when the device serves the Microsoft OS descriptors
    /// <see cref="ServeWinUsb"/> builds.</summary>
    public bool ServesWinUsb { get; private set; }

    /// <summary>The vendor code the Microsoft OS string names, which Windows
    /// then puts in its feature requests. The device answers a feature by
    /// its wIndex alone (<see cref="GetMicrosoftOsFeature"/>), so any code
    /// would do.</summary>
    internal const byte WinUsbVendorCode = 0x01;

    /// <summary>Serve, from the next enumeration on, the Microsoft OS 1.0
    /// descriptors that make Windows bind its own WinUSB driver to the
    /// device with no INF: string 0xEE, the extended compatible ID
    /// <c>WINUSB</c> for interface 0 and the extended property
    /// <c>DeviceInterfaceGUID</c>, the interface WinUSB registers, which
    /// libusb opens (Microsoft OS 1.0 Descriptors Specification; Microsoft
    /// Learn, "WinUSB Device"). The layouts are those the Switch 2 Pro
    /// persona's vendor interface serves (switch2-pro-composite.json), with
    /// interface 0 and this GUID. For a device with one interface: Windows
    /// reads the compatible ID of a device that is not composite for the
    /// device itself.</summary>
    internal void ServeWinUsb(Guid interfaceGuid)
    {
        byte[] str = new byte[18];
        str[0] = 18; str[1] = 0x03;
        Encoding.Unicode.GetBytes("MSFT100").CopyTo(str, 2);
        str[16] = WinUsbVendorCode;

        byte[] compat = new byte[40];
        compat[0] = 40;                     // dwLength
        compat[5] = 0x01;                   // bcdVersion 1.00
        compat[6] = 0x04;                   // wIndex 4
        compat[8] = 1;                      // bCount
        compat[16] = 0;                     // bFirstInterfaceNumber
        compat[17] = 0x01;                  // reserved, 1 as the specification writes it
        Encoding.ASCII.GetBytes("WINUSB").CopyTo(compat, 18);

        byte[] name = Encoding.Unicode.GetBytes("DeviceInterfaceGUID\0");
        byte[] data = Encoding.Unicode.GetBytes(interfaceGuid.ToString("B").ToUpperInvariant() + "\0");
        int section = 4 + 4 + 2 + name.Length + 4 + data.Length;
        byte[] props = new byte[10 + section];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(props, (uint)props.Length);
        props[5] = 0x01;                    // bcdVersion 1.00
        props[6] = 0x05;                    // wIndex 5
        props[8] = 1;                       // wCount
        int at = 10;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(props.AsSpan(at), (uint)section); at += 4;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(props.AsSpan(at), 1); at += 4;   // REG_SZ
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(props.AsSpan(at), (ushort)name.Length); at += 2;
        name.CopyTo(props, at); at += name.Length;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(props.AsSpan(at), (uint)data.Length); at += 4;
        data.CopyTo(props, at);

        CheckMicrosoftOsFeature(compat, 4, 16, "extendedCompatId");
        CheckMicrosoftOsFeature(props, 5, 10, "extendedProperties");
        _priorOs ??= (_msOsString, _msOsCompatId, _msOsProperties);
        _msOsCompatId = compat;
        _msOsProperties = props;
        _msOsString = str;
        ServesWinUsb = true;
    }

    // What the device served before ServeWinUsb, for StopServingWinUsb.
    private (byte[]? String, byte[]? CompatId, byte[]? Properties)? _priorOs;

    /// <summary>Serve again what the device served before
    /// <see cref="ServeWinUsb"/>, the string first, so Windows asks for no
    /// feature the device no longer names. A bind whose re-plug failed puts
    /// the device back as it was with this.</summary>
    internal void StopServingWinUsb()
    {
        if (_priorOs is not { } prior) return;
        _msOsString = prior.String;
        _msOsCompatId = prior.CompatId;
        _msOsProperties = prior.Properties;
        ServesWinUsb = false;
        _priorOs = null;
    }

    /// <summary>The Microsoft OS 1.0 feature descriptor a vendor request
    /// asks for, by its wIndex: 4 is the extended compatible ID and 5 the
    /// extended properties. Null stalls, which is every index on a persona
    /// that declares none (issue #66).</summary>
    public byte[]? GetMicrosoftOsFeature(ushort featureIndex) => featureIndex switch
    {
        0x0004 => _msOsCompatId,
        0x0005 => _msOsProperties,
        _ => null,
    };

    /// <summary>Answer a standard GET_DESCRIPTOR. Returns null to stall
    /// (unknown descriptor), matching the real pad. A Sony or Valve pad
    /// stalls the Microsoft OS string (0xEE) with everything else it lacks.
    /// A persona that declares the string serves it (issue #66).</summary>
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

    /// <summary>HID-class GET_DESCRIPTOR on a HID interface: 0x22 is the
    /// report descriptor, 0x21 the HID class descriptor. Answered per
    /// interface, so a composite presenting several HID interfaces serves
    /// each its own (issue #56). Null, a stall, on a device with no HID
    /// interface.</summary>
    public byte[]? GetHidDescriptor(byte type, byte interfaceNumber)
    {
        if (!HasHidInterface) return null;
        if (interfaceNumber != HidInterfaceNumber
            && _secondaryHid.TryGetValue(interfaceNumber, out var sec))
            return type switch { 0x22 => sec.ReportDescriptor, 0x21 => sec.ClassDescriptor, _ => null };
        return type switch { 0x22 => ReportDescriptor, 0x21 => _hidClassDescriptor, _ => null };
    }

    /// <summary>True when the interface number is a HID interface this
    /// device presents, primary or secondary.</summary>
    public bool IsHidInterface(byte interfaceNumber)
        => (HasHidInterface && interfaceNumber == HidInterfaceNumber) || _secondaryHid.ContainsKey(interfaceNumber);

    private static readonly byte[] LangIds = { 0x04, 0x03, 0x09, 0x04 }; // one LANGID: en-US

    // Each string descriptor, built the first time a host asks for it. The
    // strings never change after construction.
    private readonly byte[]?[] _strings = new byte[]?[256];

    private byte[]? GetStringDescriptor(byte index)
    {
        if (index == 0)
            return LangIds;
        // The Microsoft OS string, for a persona that declares one (issue
        // #66). Windows asks for it once per VID, PID and revision and
        // remembers a stall, so it is answered from the first enumeration.
        if (index == 0xEE && _msOsString != null)
            return _msOsString;
        return _strings[index] ??= BuildStringDescriptor(index);
    }

    private byte[]? BuildStringDescriptor(byte index)
    {
        // A device that declares a string index has to serve it. Sony's pads
        // declare iSerial 0 and get null below, unchanged; Valve's declare a
        // real serial at index 3 and a configuration name at 4, and Steam
        // logs the serial it reads here (issue #56).
        string? s = index == _iManufacturer && _iManufacturer != 0 ? _manufacturer
                  : index == _iProduct && _iProduct != 0 ? _product
                  : index == _iSerial && _iSerial != 0 ? _serial
                  : index == _iConfiguration && _iConfiguration != 0 ? _configurationName
                  : null;
        if (s == null) return null;
        var bytes = Encoding.Unicode.GetBytes(s);
        var d = new byte[2 + bytes.Length];
        d[0] = (byte)d.Length;
        d[1] = 0x03;
        bytes.CopyTo(d, 2);
        return d;
    }

    private const string ConfigurationPath = "$.usbConfiguration.configurationDescriptor";

    private static byte[] FromHex(string? hex, string field)
    {
        if (string.IsNullOrEmpty(hex))
            throw new ProfileDefectException($"$.usbConfiguration.{field}", "is required for the usbip backend.");
        return HexAt(hex, $"$.usbConfiguration.{field}");
    }

    private static byte[] HexAt(string hex, string path)
    {
        try { return Convert.FromHexString(hex); }
        catch (FormatException e) { throw new ProfileDefectException(path, $"is not whole hex bytes: {e.Message}"); }
    }

    /// <summary>The interfaces, alternate settings and endpoints the
    /// constructor walks, each present: a file can write null for any of
    /// them.</summary>
    private static void CheckStructure(UsbConfigurationSpec cfg)
    {
        const string root = "$.usbConfiguration.interfaces";
        if (cfg.Interfaces == null) throw new ProfileDefectException(root, "is null. Write [] for none.");
        for (int i = 0; i < cfg.Interfaces.Count; i++)
        {
            var iface = cfg.Interfaces[i] ?? throw new ProfileDefectException($"{root}[{i}]", "is null.");
            if (iface.AltSettings == null) throw new ProfileDefectException($"{root}[{i}].altSettings", "is null. Write [] for none.");
            for (int j = 0; j < iface.AltSettings.Count; j++)
            {
                var alt = iface.AltSettings[j] ?? throw new ProfileDefectException($"{root}[{i}].altSettings[{j}]", "is null.");
                if (alt.Endpoints == null) throw new ProfileDefectException($"{root}[{i}].altSettings[{j}].endpoints", "is null. Write [] for none.");
                for (int k = 0; k < alt.Endpoints.Count; k++)
                    if (alt.Endpoints[k] == null)
                        throw new ProfileDefectException($"{root}[{i}].altSettings[{j}].endpoints[{k}]", "is null.");
            }
        }
    }
}
