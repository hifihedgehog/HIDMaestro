using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HIDMaestro.Internal;

/// <summary>A profile's XInput presentation, the JSON <c>xinput</c> block:
/// how the device reaches XInput, and what its XUSB companion answers.
///
/// <para><c>via</c> is <c>"xusbCompanion"</c> (an XUSB companion devnode
/// beside the HID device, the presentation xusb22 gives a wired Xbox 360
/// pad), <c>"xinputhid"</c> (Microsoft's xinputhid binds above the HID
/// child, what <c>driverMode: "xinputhid"</c> says, which stays an alias)
/// or absent for no XInput. An empty block says the device has no XInput
/// presentation. The rest describes the companion's replies. What a block
/// leaves out comes from the catalog's XUSB model
/// (<c>profiles/models/xusb-companion.json</c>, <see cref="XusbAnswers"/>),
/// a wired gamepad's, the answers the companion gave every profile before
/// v2.0.0: subtype gamepad, flags voice and PMD, battery wired and full,
/// the profile's VID and PID in every reply.</para></summary>
public sealed class XInputSpec
{
    /// <summary><c>"xusbCompanion"</c>, <c>"xinputhid"</c> or absent.</summary>
    [JsonPropertyName("via")]
    public string? Via { get; set; }

    /// <summary>The XInput device subtype (XINPUT_DEVSUBTYPE_*): a name from
    /// <see cref="SubTypes"/> or a number 0 to 255. Default gamepad.</summary>
    [JsonPropertyName("subType")]
    public string? SubType { get; set; }

    /// <summary>The capability flags the 36-byte GET_CAPABILITIES reply
    /// carries, by name (<see cref="FlagBits"/>). Default voice and pmd. A
    /// block names them here or in <c>capabilities.flags</c>, not
    /// both.</summary>
    [JsonPropertyName("flags")]
    public List<string>? Flags { get; set; }

    /// <summary>What GET_BATTERY_INFORMATION reports. Default wired,
    /// full.</summary>
    [JsonPropertyName("battery")]
    public XInputBatterySpec? Battery { get; set; }

    /// <summary>The IDs GET_INFORMATION and GET_INFORMATION_EX carry, and
    /// the companion's hardware ID, when they differ from the HID
    /// child's.</summary>
    [JsonPropertyName("information")]
    public XInputIdSpec? Information { get; set; }

    /// <summary>The IDs and flags of the 36-byte GET_CAPABILITIES reply,
    /// when they differ from the HID child's and the block's.</summary>
    [JsonPropertyName("capabilities")]
    public XInputCapabilitiesSpec? Capabilities { get; set; }

    /// <summary>A copy that shares nothing with this block, so a caller
    /// that changes it never changes a catalog profile.</summary>
    internal XInputSpec Clone() => new()
    {
        Via = Via,
        SubType = SubType,
        Flags = Flags == null ? null : new List<string>(Flags),
        Battery = Battery == null ? null : new XInputBatterySpec { Type = Battery.Type, Level = Battery.Level },
        Information = Information == null ? null : new XInputIdSpec { Vid = Information.Vid, Pid = Information.Pid },
        Capabilities = Capabilities == null ? null : new XInputCapabilitiesSpec
        {
            Vid = Capabilities.Vid,
            Pid = Capabilities.Pid,
            Flags = Capabilities.Flags == null ? null : new List<string>(Capabilities.Flags),
        },
    };

    internal const string ViaXusbCompanion = "xusbCompanion";
    internal const string ViaXinputhid = "xinputhid";

    /// <summary>XINPUT_DEVSUBTYPE_* by name (Learn, "XInput and controller
    /// subtypes", xinput.h).</summary>
    internal static readonly IReadOnlyDictionary<string, byte> SubTypes = new Dictionary<string, byte>(StringComparer.Ordinal)
    {
        ["unknown"] = 0x00,
        ["gamepad"] = 0x01,
        ["wheel"] = 0x02,
        ["arcadeStick"] = 0x03,
        ["flightStick"] = 0x04,
        ["dancePad"] = 0x05,
        ["guitar"] = 0x06,
        ["guitarAlternate"] = 0x07,
        ["drumKit"] = 0x08,
        ["guitarBass"] = 0x0B,
        ["arcadePad"] = 0x13,
    };

    /// <summary>The XUSB capability flag bits as the 36-byte reply carries
    /// them, which xinput1_4 maps to XINPUT_CAPS_* (OpenXInput
    /// TranslateCapabilities, OpenXinput.cpp:1040-1054 at 6af3c14).</summary>
    internal static readonly IReadOnlyDictionary<string, ushort> FlagBits = new Dictionary<string, ushort>(StringComparer.Ordinal)
    {
        ["ffb"] = 0x0001,
        ["wireless"] = 0x0002,
        ["voice"] = 0x0004,
        ["pmd"] = 0x0008,
        ["noNavigation"] = 0x0010,
    };

    /// <summary>BATTERY_TYPE_* and BATTERY_LEVEL_* by name (xinput.h).</summary>
    internal static readonly IReadOnlyDictionary<string, byte> BatteryTypes = new Dictionary<string, byte>(StringComparer.Ordinal)
    {
        ["disconnected"] = 0x00,
        ["wired"] = 0x01,
        ["alkaline"] = 0x02,
        ["nimh"] = 0x03,
        ["unknown"] = 0xFF,
    };

    internal static readonly IReadOnlyDictionary<string, byte> BatteryLevels = new Dictionary<string, byte>(StringComparer.Ordinal)
    {
        ["empty"] = 0x00,
        ["low"] = 0x01,
        ["medium"] = 0x02,
        ["full"] = 0x03,
    };
}

/// <summary>The <c>battery</c> of an <see cref="XInputSpec"/>.</summary>
public sealed class XInputBatterySpec
{
    /// <summary><c>"disconnected"</c>, <c>"wired"</c>, <c>"alkaline"</c>,
    /// <c>"nimh"</c> or <c>"unknown"</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary><c>"empty"</c>, <c>"low"</c>, <c>"medium"</c> or
    /// <c>"full"</c>.</summary>
    [JsonPropertyName("level")]
    public string? Level { get; set; }
}

/// <summary>A vendor and product ID pair inside an
/// <see cref="XInputSpec"/>, each a hex string such as <c>"0x045E"</c>.</summary>
public sealed class XInputIdSpec
{
    [JsonPropertyName("vid")]
    public string? Vid { get; set; }

    [JsonPropertyName("pid")]
    public string? Pid { get; set; }
}

/// <summary>The <c>capabilities</c> of an <see cref="XInputSpec"/>.</summary>
public sealed class XInputCapabilitiesSpec
{
    [JsonPropertyName("vid")]
    public string? Vid { get; set; }

    [JsonPropertyName("pid")]
    public string? Pid { get; set; }

    /// <summary>The 36-byte reply's flags by name, in place of the block's
    /// <c>flags</c>.</summary>
    [JsonPropertyName("flags")]
    public List<string>? Flags { get; set; }
}
