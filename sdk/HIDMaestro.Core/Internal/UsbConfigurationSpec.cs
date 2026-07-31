using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HIDMaestro.Internal;

/// <summary>Issue #39. A USB configuration a composite persona presents,
/// shaped after the standard descriptor set so a profile can be authored
/// directly from a real device's descriptor dump.
///
/// <para>Every UMDF2 profile leaves this null. Windows composes the
/// configuration itself there, and the HID report descriptor is the only
/// piece the profile owns. Only a profile whose
/// <see cref="ControllerProfile.Backend"/> is <c>"usbip"</c> carries one,
/// because only the USB/IP backend is in a position to present interfaces
/// Windows did not compose.</para>
///
/// <para>This is a data model, not a driver. Authoring one changes no
/// behavior on its own: nothing instantiates it until the backend exists,
/// and the five existing USB Sony profiles never gain one.</para></summary>
public sealed class UsbConfigurationSpec
{
    /// <summary>bConfigurationValue, the value SET_CONFIGURATION selects.
    /// One on every device in scope.</summary>
    [JsonPropertyName("configurationValue")]
    public byte ConfigurationValue { get; set; } = 1;

    /// <summary>bmAttributes. 0xC0 is self-powered with no remote wakeup,
    /// which is what both Sony pads report over USB.</summary>
    [JsonPropertyName("attributes")]
    public byte Attributes { get; set; } = 0xC0;

    /// <summary>Bus current in milliamps, as the device reports it.
    /// bMaxPower carries half this value, so 500 mA is encoded 0xFA.</summary>
    [JsonPropertyName("maxPowerMilliamps")]
    public int MaxPowerMilliamps { get; set; } = 500;

    /// <summary>The interfaces in bInterfaceNumber order. A DualSense has
    /// four: Audio Control, two Audio Streaming, and HID.</summary>
    [JsonPropertyName("interfaces")]
    public List<UsbInterfaceSpec> Interfaces { get; set; } = new();
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
    /// <c>"audioStreamingOut"</c>, <c>"audioStreamingIn"</c>. The wire
    /// values live in <see cref="UsbAltSettingSpec"/>. The HID interface
    /// keeps serving the profile's existing report descriptor and codec
    /// unchanged, which is why a composite persona reuses everything the
    /// profile already declares.</summary>
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
}

/// <summary>One endpoint descriptor.</summary>
public sealed class UsbEndpointSpec
{
    /// <summary>bEndpointAddress, direction bit included. 0x01 is OUT
    /// endpoint 1; 0x82 is IN endpoint 2.</summary>
    [JsonPropertyName("address")]
    public byte Address { get; set; }

    /// <summary>Transfer type: <c>"isochronous"</c> or
    /// <c>"interrupt"</c>.</summary>
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
