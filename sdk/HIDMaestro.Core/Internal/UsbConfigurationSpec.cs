using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HIDMaestro.Internal;

/// <summary>Issue #39. The USB device a profile presents on the USB/IP
/// backend, shaped after the standard descriptor set so a profile can be
/// authored directly from a real device's descriptor dump.
///
/// <para>Two forms. With <see cref="DeviceDescriptorHex"/> and
/// <see cref="ConfigurationDescriptorHex"/> it is authored: the device
/// serves those blobs verbatim, and the structured fields describe them.
/// Without them it is a set of pins over the presentation the SDK derives
/// from the profile's own descriptor and identity: one HID interface, each
/// field a pin leaves out taken from the profile (UsbPresentation). A
/// profile with no <c>usbConfiguration</c> at all gets that derived
/// presentation whole. A pin is a value read from a real device. The UMDF2
/// backend reads none of this: Windows composes that device's
/// configuration itself, and the HID report descriptor is the piece the
/// profile owns there.</para></summary>
public sealed class UsbConfigurationSpec
{
    /// <summary>bConfigurationValue, the value SET_CONFIGURATION selects.
    /// One when not given.</summary>
    [JsonPropertyName("configurationValue")]
    public byte? ConfigurationValue { get; set; }

    /// <summary>bmAttributes. 0xC0 is self-powered with no remote wakeup,
    /// which is what both Sony pads report over USB, and 0x80 bus powered,
    /// what a derived presentation reports when not given.</summary>
    [JsonPropertyName("attributes")]
    public byte? Attributes { get; set; }

    /// <summary>Bus current in milliamps, as the device reports it.
    /// bMaxPower carries half this value, so 500 mA is encoded 0xFA. 500
    /// when not given.</summary>
    [JsonPropertyName("maxPowerMilliamps")]
    public int? MaxPowerMilliamps { get; set; }

    /// <summary>The interfaces in bInterfaceNumber order. A DualSense has
    /// four: Audio Control, two Audio Streaming, and HID.</summary>
    [JsonPropertyName("interfaces")]
    public List<UsbInterfaceSpec> Interfaces { get; set; } = new();

    /// <summary>The bus speed the real device enumerates at:
    /// <c>"high"</c> (DualSense), <c>"full"</c> (DualShock 4) or
    /// <c>"low"</c>. When not given, an authored device is high speed, and a
    /// derived one is full speed unless a report it carries is longer than
    /// 64 bytes. Reported in the USB/IP import reply. usbip-win2
    /// patches the endpoint intervals of every device below high speed for
    /// its high-speed UDE presentation itself (wsk_receive.cpp,
    /// post_control_transfer), so the blobs stay verbatim either way. A
    /// device below high speed also stalls Device_Qualifier and
    /// Other_Speed requests, as the real pad does. A low-speed device is
    /// held to an 8-byte control endpoint, interrupt endpoints of at most
    /// 8 bytes and no bulk or isochronous ones.</summary>
    [JsonPropertyName("busSpeed")]
    public string? BusSpeed { get; set; }

    /// <summary>The 18-byte device descriptor, hex, verbatim from a real
    /// pad. The backend serves this blob for GET_DESCRIPTOR(Device); the
    /// profile's vid/pid/versionNumber fields never override it, because
    /// the dump is the ground truth and a divergence between the two is a
    /// profile-authoring bug the create-path guard rejects.</summary>
    [JsonPropertyName("deviceDescriptor")]
    public string? DeviceDescriptorHex { get; set; }

    /// <summary>The full configuration descriptor the device serves at its
    /// operating speed (config header + every interface, class-specific and
    /// endpoint descriptor), hex, verbatim from a real pad. This blob IS
    /// the wire truth; the structured <see cref="Interfaces"/> model above
    /// describes the same configuration semantically so the backend can
    /// route without re-deriving meaning from bytes. The backend
    /// cross-checks the two at create time and refuses on mismatch.</summary>
    [JsonPropertyName("configurationDescriptor")]
    public string? ConfigurationDescriptorHex { get; set; }

    /// <summary>GET_DESCRIPTOR(Other_Speed_Configuration) blob, hex,
    /// for dual-speed devices. The DualSense's full-speed variant differs
    /// from the high-speed one (isochronous bInterval 1, a 1-channel
    /// microphone input terminal, a sampling-frequency control bit), so it
    /// cannot be synthesized from the high-speed blob.</summary>
    [JsonPropertyName("otherSpeedConfigurationDescriptor")]
    public string? OtherSpeedConfigurationDescriptorHex { get; set; }

    /// <summary>The UAC1 feature-unit control ranges the device answers on
    /// EP0, captured from a real pad's wire exchanges rather than invented.
    /// usbaudio.sys reads these during endpoint creation and maps the
    /// Windows volume slider across MIN..MAX.</summary>
    [JsonPropertyName("audioControls")]
    public List<UsbAudioControlSpec>? AudioControls { get; set; }

    /// <summary>Issue #66. The Microsoft OS 1.0 descriptors a device that
    /// asks Windows for a driver itself serves. Null for every persona whose
    /// real pad stalls string 0xEE, which is all the Sony and Valve ones.</summary>
    [JsonPropertyName("microsoftOs")]
    public UsbMicrosoftOsSpec? MicrosoftOs { get; set; }

    /// <summary>True when every unit of the device carries the same serial,
    /// so the profile's <c>serialString</c> is served unchanged for every
    /// identity key instead of being varied per identity. The Wii U GameCube
    /// adapter is one: its serial is <c>15/07/2014</c> on every unit, and
    /// Windows gives a second adapter an instance named after its port.</summary>
    [JsonPropertyName("sharedSerial")]
    public bool SharedSerial { get; set; }

    /// <summary>v2.0.0: the device takes a WinUSB binding on request
    /// (HMController.BindWinUsb), for libusb hosts. See
    /// <see cref="WinUsbSpec"/>.</summary>
    [JsonPropertyName("winUsb")]
    public WinUsbSpec? WinUsb { get; set; }

    /// <summary>Pins over the derived device descriptor. Read only when the
    /// blobs are absent.</summary>
    [JsonPropertyName("device")]
    public UsbDevicePins? Device { get; set; }

    /// <summary>Pins over the derived HID interface and its class
    /// descriptor. Read only when the blobs are absent.</summary>
    [JsonPropertyName("hid")]
    public UsbHidPins? Hid { get; set; }

    /// <summary>Pins over the derived interrupt IN endpoint. Read only when
    /// the blobs are absent.</summary>
    [JsonPropertyName("inEndpoint")]
    public UsbEndpointPins? InEndpoint { get; set; }

    /// <summary>Pins over the derived interrupt OUT endpoint, which a
    /// derived device has when its descriptor declares an output report.
    /// <c>null</c> written in the file pins a device that has none. Read
    /// only when the blobs are absent.</summary>
    [JsonPropertyName("outEndpoint")]
    [JsonConverter(typeof(UsbEndpointPinsConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UsbEndpointPins? OutEndpoint { get; set; }

    /// <summary>True when the profile authors the device and configuration
    /// descriptors.</summary>
    [JsonIgnore]
    public bool IsAuthored =>
        !string.IsNullOrEmpty(DeviceDescriptorHex) || !string.IsNullOrEmpty(ConfigurationDescriptorHex);
}

/// <summary>Pins over a derived device descriptor's fields.</summary>
public sealed class UsbDevicePins
{
    /// <summary>bcdUSB, hex, such as <c>"0x0200"</c>.</summary>
    [JsonPropertyName("bcdUsb")]
    public string? BcdUsb { get; set; }

    /// <summary>bMaxPacketSize0: 8, 16, 32 or 64.</summary>
    [JsonPropertyName("maxPacketSize0")]
    public byte? MaxPacketSize0 { get; set; }

    /// <summary>bcdDevice, hex. The profile's <c>versionNumber</c> when not
    /// given.</summary>
    [JsonPropertyName("bcdDevice")]
    public string? BcdDevice { get; set; }
}

/// <summary>Pins over a derived HID interface descriptor and its HID class
/// descriptor.</summary>
public sealed class UsbHidPins
{
    /// <summary>bInterfaceNumber.</summary>
    [JsonPropertyName("interfaceNumber")]
    public byte? InterfaceNumber { get; set; }

    /// <summary>bInterfaceSubClass: 0, or 1 for a boot device.</summary>
    [JsonPropertyName("subClass")]
    public byte? SubClass { get; set; }

    /// <summary>bInterfaceProtocol: 0, 1 keyboard or 2 mouse.</summary>
    [JsonPropertyName("protocol")]
    public byte? Protocol { get; set; }

    /// <summary>bcdHID, hex, such as <c>"0x0111"</c>.</summary>
    [JsonPropertyName("bcdHid")]
    public string? BcdHid { get; set; }

    /// <summary>bCountryCode.</summary>
    [JsonPropertyName("countryCode")]
    public byte? CountryCode { get; set; }
}

/// <summary>Pins over a derived interrupt endpoint descriptor.</summary>
public sealed class UsbEndpointPins
{
    /// <summary>bEndpointAddress, hex, direction bit included, such as
    /// <c>"0x84"</c>.</summary>
    [JsonPropertyName("address")]
    public string? Address { get; set; }

    /// <summary>wMaxPacketSize.</summary>
    [JsonPropertyName("maxPacketSize")]
    public int? MaxPacketSize { get; set; }

    /// <summary>bInterval.</summary>
    [JsonPropertyName("interval")]
    public byte? Interval { get; set; }

    /// <summary>True when the file wrote <c>null</c> for the endpoint: the
    /// device has none.</summary>
    [JsonIgnore]
    public bool None { get; set; }
}

/// <summary>Reads <see cref="UsbEndpointPins"/>, and a JSON <c>null</c> as
/// the pin that says the device has no such endpoint. Strict as the
/// profile's own reader: a key the type lacks and a key written twice are
/// refused.</summary>
internal sealed class UsbEndpointPinsConverter : JsonConverter<UsbEndpointPins>
{
    public override bool HandleNull => true;

    public override UsbEndpointPins Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return new UsbEndpointPins { None = true };
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("An endpoint pin is an object, or null for no endpoint.");
        var pins = new UsbEndpointPins();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("An endpoint pin holds named values.");
            string name = reader.GetString()!;
            if (!seen.Add(name)) throw new JsonException($"'{name}' is written twice.");
            reader.Read();
            bool isNull = reader.TokenType == JsonTokenType.Null;
            switch (name.ToLowerInvariant())
            {
                case "address": pins.Address = isNull ? null : reader.GetString(); break;
                case "maxpacketsize": pins.MaxPacketSize = isNull ? null : reader.GetInt32(); break;
                case "interval": pins.Interval = isNull ? null : reader.GetByte(); break;
                default:
                    throw new JsonException($"'{name}' is not a key of an endpoint pin: address, maxPacketSize, interval.");
            }
        }
        return pins;
    }

    public override void Write(Utf8JsonWriter writer, UsbEndpointPins value, JsonSerializerOptions options)
    {
        if (value == null || value.None) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        if (value.Address != null) writer.WriteString("address", value.Address);
        if (value.MaxPacketSize is int size) writer.WriteNumber("maxPacketSize", size);
        if (value.Interval is byte interval) writer.WriteNumber("interval", interval);
        writer.WriteEndObject();
    }
}

/// <summary>Issue #66. Microsoft OS 1.0 descriptors, each a hex string of the
/// exact bytes the device returns. Windows reads the string once per VID, PID
/// and revision, then asks for the two feature descriptors with a vendor
/// request carrying the code the string named.</summary>
public sealed class UsbMicrosoftOsSpec
{
    /// <summary>A note for the reader of the model or profile.</summary>
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    /// <summary>String descriptor 0xEE: 18 bytes, <c>MSFT100</c> in UTF-16
    /// then the vendor code and a pad byte.</summary>
    [JsonPropertyName("string")]
    public string? StringHex { get; set; }

    /// <summary>The extended compatible ID descriptor, feature index 4. It
    /// names the driver Windows binds to an interface, <c>WINUSB</c> for the
    /// Switch 2 Pro Controller's vendor interface.</summary>
    [JsonPropertyName("extendedCompatId")]
    public string? ExtendedCompatIdHex { get; set; }

    /// <summary>The extended properties descriptor, feature index 5. It
    /// carries registry values for the interface's device key, such as the
    /// <c>DeviceInterfaceGUID</c> WinUSB registers. Required, except on a
    /// device that reports on an <c>"xusb"</c> or a <c>"gip"</c> interface
    /// (<see cref="UsbInterfaceSpec.Function"/>), which may leave it out and
    /// then stalls the request.</summary>
    [JsonPropertyName("extendedProperties")]
    public string? ExtendedPropertiesHex { get; set; }
}

/// <summary>One UAC1 feature unit's control state: the mute and volume
/// controls its bmaControls advertise, with the raw wire values a real pad
/// returns for GET_MIN / GET_MAX / GET_RES / GET_CUR. Volume values are
/// UAC1 s16 in 1/256 dB units (-25600 = -100 dB).</summary>
public sealed class UsbAudioControlSpec
{
    /// <summary>bUnitID of the feature unit (wIndex high byte of the
    /// class request addresses it).</summary>
    [JsonPropertyName("unitId")]
    public byte UnitId { get; set; }

    /// <summary>The Audio Control interface number the requests arrive on
    /// (wIndex low byte).</summary>
    [JsonPropertyName("controlInterface")]
    public byte ControlInterface { get; set; }

    /// <summary>What the unit controls, for the SDK surface:
    /// <c>"speaker"</c> (the render path feature unit) or
    /// <c>"microphone"</c> (the capture path).</summary>
    [JsonPropertyName("function")]
    public string Function { get; set; } = "";

    /// <summary>Boot-state CUR for the Mute control (0 or 1).</summary>
    [JsonPropertyName("muteCur")]
    public byte MuteCur { get; set; }

    /// <summary>GET_MIN(Volume) raw s16.</summary>
    [JsonPropertyName("volumeMinRaw")]
    public short VolumeMinRaw { get; set; }

    /// <summary>GET_MAX(Volume) raw s16.</summary>
    [JsonPropertyName("volumeMaxRaw")]
    public short VolumeMaxRaw { get; set; }

    /// <summary>GET_RES(Volume) raw s16.</summary>
    [JsonPropertyName("volumeResRaw")]
    public short VolumeResRaw { get; set; }

    /// <summary>Boot-state GET_CUR(Volume) raw s16.</summary>
    [JsonPropertyName("volumeCurRaw")]
    public short VolumeCurRaw { get; set; }
}

/// <summary>One interface, with every alternate setting it offers. USB
/// Audio Class streaming interfaces always carry at least two: alt 0 with
/// no endpoint (the zero-bandwidth setting the host selects when the
/// stream is idle) and alt 1 with the isochronous endpoint.</summary>
public sealed class UsbInterfaceSpec
{
    /// <summary>bInterfaceNumber.</summary>
    [JsonPropertyName("interfaceNumber")]
    public byte InterfaceNumber { get; set; }

    /// <summary>What this interface is, for the backend's routing rather
    /// than for the wire: <c>"hid"</c>, <c>"audioControl"</c>,
    /// <c>"audioStreamingOut"</c>, <c>"audioStreamingIn"</c>,
    /// <c>"vendor"</c> for a class 0xFF interface whose protocol the backend
    /// answers itself (issue #66), <c>"xusb"</c> or <c>"gip"</c>. The wire
    /// values live in <see cref="UsbAltSettingSpec"/>. The HID interface
    /// keeps serving the profile's existing report descriptor and codec
    /// unchanged, which is why a composite persona reuses everything the
    /// profile already declares.
    ///
    /// <para><c>"xusb"</c> marks a device with no HID interface: its
    /// reports travel on an XUSB game controller interface, class 0xFF and
    /// subclass 0x5D ([MS-XUSBI] 2.2.4.3), which Windows gives its inbox
    /// XUSB driver. The profile then carries no <c>descriptor</c>, and its
    /// <c>extendedReport</c>, always armed, is the report the device
    /// sends. The interface's interrupt IN endpoint sends that report and
    /// its interrupt OUT endpoint takes the output reports, as the HID
    /// interface's endpoints do on every other persona. A raw frame is the
    /// report whole, at the extended report's length. Of the HID class's
    /// requests the interface answers GET_REPORT(Input) and
    /// SET_REPORT(Output) and stalls the rest, and the device may leave
    /// <c>microsoftOs.extendedProperties</c> out ([MS-XUSBI] 2.2.8.1 and
    /// 2.2.4.6). A profile that names no interface <c>"xusb"</c> or
    /// <c>"gip"</c> is held to a HID interface and a <c>descriptor</c>.</para>
    ///
    /// <para><c>"gip"</c> marks a device with no HID interface whose
    /// messages travel on a GIP data interface, class 0xFF, subclass 0x47,
    /// protocol 0xD0 (Microsoft's GIP specification H001419, table 36), which
    /// Windows gives its inbox GIP driver. The profile carries no
    /// <c>descriptor</c> and builds every message it sends from its state
    /// block and protocol: its <c>protocol.input.endpoint</c> is the
    /// interface's interrupt IN endpoint and its commands come from the
    /// interface's interrupt OUT endpoint. The interface stalls every class
    /// request, and the device may leave
    /// <c>microsoftOs.extendedProperties</c> out, as the specification has
    /// a GIP device stall that request.</para></summary>
    [JsonPropertyName("function")]
    public string Function { get; set; } = "";

    /// <summary>Alternate settings in bAlternateSetting order.</summary>
    [JsonPropertyName("altSettings")]
    public List<UsbAltSettingSpec> AltSettings { get; set; } = new();
}

/// <summary>One alternate setting: the interface descriptor's class
/// triple, its endpoints, and the class-specific descriptors that follow
/// it verbatim on the wire.</summary>
public sealed class UsbAltSettingSpec
{
    /// <summary>bAlternateSetting.</summary>
    [JsonPropertyName("altSetting")]
    public byte AltSetting { get; set; }

    /// <summary>bInterfaceClass. 0x01 Audio, 0x03 HID.</summary>
    [JsonPropertyName("interfaceClass")]
    public byte InterfaceClass { get; set; }

    /// <summary>bInterfaceSubClass. Under class 0x01: 0x01 Audio Control,
    /// 0x02 Audio Streaming.</summary>
    [JsonPropertyName("interfaceSubClass")]
    public byte InterfaceSubClass { get; set; }

    /// <summary>bInterfaceProtocol.</summary>
    [JsonPropertyName("interfaceProtocol")]
    public byte InterfaceProtocol { get; set; }

    /// <summary>Class-specific descriptors that follow the interface
    /// descriptor, as a hex string of the exact bytes. For Audio Control
    /// this is the whole topology (header, input and output terminals,
    /// feature units); for Audio Streaming it is the AS general descriptor
    /// plus the format type. Carried verbatim rather than modeled field by
    /// field, because the backend's job is to reproduce a real device's
    /// bytes, and a dump is the ground truth.</summary>
    [JsonPropertyName("classDescriptors")]
    public string? ClassDescriptors { get; set; }

    /// <summary>Endpoints this setting exposes. Empty on an Audio Control
    /// interface and on every zero-bandwidth alt 0.</summary>
    [JsonPropertyName("endpoints")]
    public List<UsbEndpointSpec> Endpoints { get; set; } = new();

    /// <summary>For an audio streaming setting, what the stream carries.
    /// Null on HID and Audio Control interfaces.</summary>
    [JsonPropertyName("audioStream")]
    public UsbAudioStreamSpec? AudioStream { get; set; }

    /// <summary>Issue #56. The HID report descriptor this interface serves,
    /// hex, for a SECONDARY HID interface. The primary HID interface (the
    /// one whose <see cref="UsbInterfaceSpec.Function"/> is <c>"hid"</c>)
    /// leaves this null and serves the profile's own
    /// <c>descriptor</c>, which is what every consumer and the whole codec
    /// path already reads. A composite that presents more than one HID
    /// interface carries the others' descriptors here, verbatim from the
    /// real device: the Steam Deck's keyboard and mouse interfaces (the
    /// pair its lizard mode drives) are the shipped example.</summary>
    [JsonPropertyName("reportDescriptor")]
    public string? ReportDescriptor { get; set; }
}

/// <summary>One endpoint descriptor.</summary>
public sealed class UsbEndpointSpec
{
    /// <summary>bEndpointAddress, direction bit included. 0x01 is OUT
    /// endpoint 1; 0x82 is IN endpoint 2.</summary>
    [JsonPropertyName("address")]
    public byte Address { get; set; }

    /// <summary>Transfer type: <c>"isochronous"</c>, <c>"interrupt"</c> or
    /// <c>"bulk"</c>.</summary>
    [JsonPropertyName("transferType")]
    public string TransferType { get; set; } = "";

    /// <summary>Synchronisation type for isochronous endpoints:
    /// <c>"adaptive"</c> on the Sony OUT stream, <c>"asynchronous"</c> on
    /// the IN stream.</summary>
    [JsonPropertyName("syncType")]
    public string? SyncType { get; set; }

    /// <summary>wMaxPacketSize. This is the per-interval byte budget the
    /// backend must produce or consume, so it sets the buffer sizes on
    /// the audio surfaces.</summary>
    [JsonPropertyName("maxPacketSize")]
    public int MaxPacketSize { get; set; }

    /// <summary>bInterval. At high speed the service interval is
    /// 2^(bInterval-1) microframes, so 4 means 8 microframes, 1 ms. This
    /// is the cadence the pacing spike measured.</summary>
    [JsonPropertyName("interval")]
    public byte Interval { get; set; }

    /// <summary>Class-specific endpoint descriptor bytes, hex, appended
    /// after the endpoint descriptor.</summary>
    [JsonPropertyName("classDescriptors")]
    public string? ClassDescriptors { get; set; }
}

/// <summary>The PCM format an audio streaming alt setting carries, plus
/// what the channels mean, which is the part the backend routes on.</summary>
public sealed class UsbAudioStreamSpec
{
    /// <summary>Channel count. Four on the DualSense OUT stream: two
    /// speaker plus two voice-coil actuators.</summary>
    [JsonPropertyName("channels")]
    public int Channels { get; set; }

    /// <summary>Bits per sample.</summary>
    [JsonPropertyName("bitsPerSample")]
    public int BitsPerSample { get; set; }

    /// <summary>Sample rate in Hz.</summary>
    [JsonPropertyName("sampleRateHz")]
    public int SampleRateHz { get; set; }

    /// <summary>wChannelConfig from the input terminal, verbatim. The
    /// DualSense reports 0x0033: Left Front, Right Front, Left Surround,
    /// Right Surround.</summary>
    [JsonPropertyName("channelConfig")]
    public int ChannelConfig { get; set; }

    /// <summary>What each channel is for, in channel order, so a consumer
    /// can address the haptic actuators without decoding terminal
    /// topology: <c>"speakerLeft"</c>, <c>"speakerRight"</c>,
    /// <c>"hapticLeft"</c>, <c>"hapticRight"</c>, <c>"microphone"</c>.
    /// This is HIDMaestro's semantic layer, not a USB field. It is what
    /// makes the four-channel stream usable rather than merely
    /// present.</summary>
    [JsonPropertyName("channelRoles")]
    public List<string> ChannelRoles { get; set; } = new();
}
