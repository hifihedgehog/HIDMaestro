using System;
using System.Collections.Generic;
using System.Globalization;

namespace HIDMaestro.Internal;

/// <summary>The XInput presentation a profile resolves to, and where it
/// came from. The <c>xinput</c> block is part of the presentation group of
/// the device model (<see cref="ModelGroup.Presentation"/>), resolved as
/// the other groups are (<see cref="ModelRegistry.Resolve"/>): the
/// profile's own block, or its <c>driverMode: "xinputhid"</c> alias, which
/// count as its own keys, else the model it names, else its identity
/// default, else none. The catalog's identity default for the XUSB
/// companion is data, the <c>microsoft-xbox-named</c> entry of
/// <c>profiles/models/identity-defaults.json</c>, written from the gate it
/// replaces, and this code names no device.</summary>
internal static class XInputPresentation
{
    /// <summary>The resolved block, null for no XInput, and its source:
    /// <c>own</c>, <c>own:driverMode</c>, <c>named:&lt;model&gt;</c>,
    /// <c>identity:&lt;entry&gt;</c> or <c>none</c>.</summary>
    internal readonly record struct Resolved(XInputSpec? Spec, string Source)
    {
        public string? Via => Spec?.Via;
        public bool XusbCompanion => Via == XInputSpec.ViaXusbCompanion;
        public bool Xinputhid => Via == XInputSpec.ViaXinputhid;
    }

    /// <summary>Whether <paramref name="p"/>'s <c>driverMode</c> is the
    /// alias of <c>"via": "xinputhid"</c>. driverMode "xusb22" took the
    /// same path as "xinputhid" at master 3dd5e2a (UsesUpperFilter): the SWD
    /// devnode with the &amp;IG_00 hardware ID and no companion. It stays an
    /// alias for that path.</summary>
    internal static bool DriverModeAlias(ControllerProfile p) =>
        string.Equals(p.DriverMode, XInputSpec.ViaXinputhid, StringComparison.OrdinalIgnoreCase)
        || string.Equals(p.DriverMode, "xusb22", StringComparison.OrdinalIgnoreCase);

    /// <summary>The block the profile states itself: its own <c>xinput</c>,
    /// or for the driverMode alias the block it stands for. Null when it
    /// states none, and the presentation comes from a model.</summary>
    internal static XInputSpec? OwnBlock(ControllerProfile p) =>
        p.XInput ?? (DriverModeAlias(p) ? new XInputSpec { Via = XInputSpec.ViaXinputhid } : null);

    /// <summary>Checks a profile's own block: its values, and that it
    /// agrees with the driverMode alias. Throws
    /// <see cref="ProfileDefectException"/> with the JSON path.</summary>
    internal static void ValidateOwn(ControllerProfile p)
    {
        if (p.XInput is not { } own) return;
        Validate(p.Id, own, "$.xinput");
        if (DriverModeAlias(p) && own.Via != XInputSpec.ViaXinputhid)
            throw new ProfileDefectException("$.xinput.via",
                $"\"{own.Via}\" disagrees with driverMode \"{p.DriverMode}\". Give the presentation once, in xinput.via.");
    }

    /// <summary>Resolves the presentation of <paramref name="p"/> from its
    /// resolved device model.</summary>
    internal static Resolved Resolve(ControllerProfile p)
    {
        var r = p.Resolved;
        string source = r.Sources.TryGetValue(DeviceModel.GroupName(ModelGroup.Presentation), out var s) ? s : "none";
        var x = r.Model.XInput;
        if (x == null) return new Resolved(null, source == "own" ? "own" : "none");
        if (source == "own" && p.XInput == null) source = "own:driverMode";
        return new Resolved(x.Via == null ? null : x, source);
    }

    /// <summary>Checks every value of a block against what it may hold.</summary>
    internal static void Validate(string profileId, XInputSpec x, string path)
    {
        string Where(string key) => $"Profile '{profileId}': {path}.{key}";
        if (x.Via != null && x.Via != XInputSpec.ViaXusbCompanion && x.Via != XInputSpec.ViaXinputhid)
            throw new InvalidOperationException($"{Where("via")} is \"{x.Via}\", not \"xusbCompanion\" or \"xinputhid\".");
        if (x.Via == null && (x.SubType != null || x.Flags != null || x.Battery != null || x.Information != null || x.Capabilities != null))
            throw new InvalidOperationException($"{Where("via")} is absent, so the block describes no XInput device and takes nothing else.");
        if (x.Via == XInputSpec.ViaXinputhid && (x.SubType != null || x.Flags != null || x.Battery != null || x.Information != null || x.Capabilities != null))
            throw new InvalidOperationException(
                $"{Where("via")} is \"xinputhid\", whose replies xinputhid makes, so the block takes no subType, flags, battery, information or capabilities.");
        _ = SubTypeByte(profileId, x, path);
        _ = FlagWord(profileId, x, path);
        _ = BatteryBytes(profileId, x, path);
        if (x.Information != null)
        {
            _ = ParseId(x.Information.Vid ?? throw new InvalidOperationException($"{Where("information.vid")} is missing."), Where("information.vid"));
            _ = ParseId(x.Information.Pid ?? throw new InvalidOperationException($"{Where("information.pid")} is missing."), Where("information.pid"));
        }
        if (x.Capabilities != null)
        {
            if (x.Capabilities.Vid != null) _ = ParseId(x.Capabilities.Vid, Where("capabilities.vid"));
            if (x.Capabilities.Pid != null) _ = ParseId(x.Capabilities.Pid, Where("capabilities.pid"));
        }
    }

    internal static byte SubTypeByte(string profileId, XInputSpec x, string path = "$.xinput")
    {
        if (x.SubType == null) return XInputSpec.SubTypes["gamepad"];
        if (XInputSpec.SubTypes.TryGetValue(x.SubType, out byte named)) return named;
        string s = x.SubType;
        bool hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (byte.TryParse(hex ? s.AsSpan(2) : s.AsSpan(), hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                          CultureInfo.InvariantCulture, out byte n))
            return n;
        throw new InvalidOperationException(
            $"Profile '{profileId}': {path}.subType is \"{s}\", neither a subtype name ({string.Join(", ", XInputSpec.SubTypes.Keys)}) nor a number 0 to 255.");
    }

    internal static ushort FlagWord(string profileId, XInputSpec x, string path = "$.xinput")
    {
        if (x.Flags != null && x.Capabilities?.Flags != null)
            throw new InvalidOperationException($"Profile '{profileId}': {path}.flags and {path}.capabilities.flags are both given. Give the flags once.");
        var names = x.Capabilities?.Flags ?? x.Flags;
        string at = x.Capabilities?.Flags != null ? path + ".capabilities.flags" : path + ".flags";
        if (names == null) return (ushort)(XInputSpec.FlagBits["voice"] | XInputSpec.FlagBits["pmd"]);
        ushort word = 0;
        foreach (string f in names)
        {
            if (!XInputSpec.FlagBits.TryGetValue(f, out ushort bit))
                throw new InvalidOperationException(
                    $"Profile '{profileId}': {at} names \"{f}\", not one of {string.Join(", ", XInputSpec.FlagBits.Keys)}.");
            word |= bit;
        }
        return word;
    }

    internal static (byte Type, byte Level) BatteryBytes(string profileId, XInputSpec x, string path = "$.xinput")
    {
        byte type = XInputSpec.BatteryTypes["wired"], level = XInputSpec.BatteryLevels["full"];
        if (x.Battery?.Type != null && !XInputSpec.BatteryTypes.TryGetValue(x.Battery.Type, out type))
            throw new InvalidOperationException(
                $"Profile '{profileId}': {path}.battery.type is \"{x.Battery.Type}\", not one of {string.Join(", ", XInputSpec.BatteryTypes.Keys)}.");
        if (x.Battery?.Level != null && !XInputSpec.BatteryLevels.TryGetValue(x.Battery.Level, out level))
            throw new InvalidOperationException(
                $"Profile '{profileId}': {path}.battery.level is \"{x.Battery.Level}\", not one of {string.Join(", ", XInputSpec.BatteryLevels.Keys)}.");
        return (type, level);
    }

    /// <summary>A vendor or product ID written as hex, <c>"0x045E"</c>.</summary>
    internal static ushort ParseId(string s, string where)
    {
        string digits = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Substring(2) : s;
        if (digits.Length is < 1 or > 4
            || !ushort.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort v))
            throw new InvalidOperationException($"{where} is \"{s}\", not a hex ID of one to four digits.");
        return v;
    }
}
