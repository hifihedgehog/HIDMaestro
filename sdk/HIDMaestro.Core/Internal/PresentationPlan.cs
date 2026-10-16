using System;
using System.Collections.Generic;
using System.Text;

namespace HIDMaestro.Internal;

/// <summary>How a controller's devnodes present themselves to Windows,
/// decided once per create from its profile: which devnode carries the
/// HID descriptor and under which enumerator, its hardware and compatible
/// IDs, whether an XUSB companion is made, and what the create does around
/// it. <see cref="DeviceNodeCreator"/>, <see cref="DeviceOrchestrator"/> and
/// <see cref="HIDMaestro.HMController"/> read the plan and decide nothing of
/// this themselves.</summary>
internal sealed class PresentationPlan
{
    /// <summary>Which create path makes the controller's devnodes.</summary>
    internal enum CreatePath
    {
        /// <summary>A ROOT devnode bound to the HID driver
        /// (<see cref="DeviceNodeCreator.CreateDeviceNode"/>).</summary>
        Root,
        /// <summary>An SWD devnode that xinputhid's INF binds above the HID
        /// child (<c>DeviceOrchestrator.CreateGamepadCompanion</c>).</summary>
        XinputhidGamepad,
        /// <summary>A composite USB persona on the USB/IP backend, whose
        /// identity its USB descriptors give.</summary>
        Usbip,
    }

    /// <summary>Which devnodes a create stamps with GUID_BUS_TYPE_USB.</summary>
    internal enum BusTypeStamp
    {
        /// <summary>Every present HIDMaestro devnode under ROOT and SWD and
        /// each one's first child, on every create.</summary>
        EveryDevnodeUsb,
    }

    public CreatePath Path { get; private init; }

    /// <summary>The enumerator the HID-bearing parent is created under:
    /// a ROOT enumerator for <see cref="CreatePath.Root"/>, an SWD one for
    /// <see cref="CreatePath.XinputhidGamepad"/>, null otherwise.</summary>
    public string? Enumerator { get; private init; }

    /// <summary>The PID in the parent's hardware IDs, which differs from the
    /// profile's PID when xinputhid's INF needs one it lists.</summary>
    public ushort HardwarePid { get; private init; }

    public IReadOnlyList<string> HardwareIds { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> CompatibleIds { get; private init; } = Array.Empty<string>();

    /// <summary>UpperFilters xinputhid on the HID parent from its
    /// registration, so WGI leaves its HID child to the companion's XUSB
    /// gamepad (issue #59).</summary>
    public bool UpperFilterTripwire { get; private init; }

    /// <summary>An XUSB companion (<c>SWD\HIDMAESTRO</c>) is made.</summary>
    public bool XusbCompanion { get; private init; }
    public IReadOnlyList<string> CompanionHardwareIds { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> CompanionCompatibleIds { get; private init; } = Array.Empty<string>();

    /// <summary>The HID driver's <c>FunctionMode</c> value.</summary>
    public int FunctionMode { get; private init; }

    /// <summary>The create waits for an XInput slot to be claimed.</summary>
    public bool WaitsForXInputSlot { get; private init; }

    /// <summary>The controller packs the GIP slice the companion reads.</summary>
    public bool PacksGip { get; private init; }

    public BusTypeStamp BusType { get; private init; }

    /// <summary>The XInput presentation the plan was made from
    /// (<see cref="XInputPresentation.Resolve"/>), and where it came
    /// from.</summary>
    public string PresentationSource { get; private init; } = "none";

    /// <summary>The plan for <paramref name="profile"/>, from its resolved
    /// <c>xinput</c> block. Throws <see cref="InvalidOperationException"/>
    /// naming the JSON path of a block that does not validate.</summary>
    public static PresentationPlan Of(ControllerProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        var xi = XInputPresentation.Resolve(profile);
        if (profile.RequiresUsbipBackend)
            return new PresentationPlan { Path = CreatePath.Usbip, HardwarePid = profile.ProductId, PresentationSource = xi.Source };

        string vid = $"{profile.VendorId:X4}";
        string pid = $"{profile.ProductId:X4}";
        ushort hwPid = profile.ResolvedDriverPid != null ? Convert.ToUInt16(profile.ResolvedDriverPid, 16) : profile.ProductId;
        bool companion = xi.XusbCompanion;
        bool bluetooth = string.Equals(profile.Connection, "bluetooth", StringComparison.OrdinalIgnoreCase);

        // The companion's hardware ID carries the IDs its GET_INFORMATION
        // reports, which are the HID child's unless the block's information
        // gives others, so each companion keeps the hidmaestro_xusb.inf
        // model line it matches (root\VID_045E&PID_0719&XI_00 for the
        // wireless pad behind its 02A1 HID child).
        string[] companionHw = Array.Empty<string>(), companionCompat = Array.Empty<string>();
        if (companion)
        {
            string infoVid = vid, infoPid = pid;
            if (xi.Spec!.Information is { } info)
            {
                infoVid = $"{XInputPresentation.ParseId(info.Vid!, "information.vid"):X4}";
                infoPid = $"{XInputPresentation.ParseId(info.Pid!, "information.pid"):X4}";
            }
            companionHw = new[] { $"root\\VID_{infoVid}&PID_{infoPid}&XI_00", "root\\HIDMaestroXUSB" };
            companionCompat = new[] { "USB\\MS_COMP_XUSB10", "USB\\Class_FF&SubClass_5D&Prot_01", "USB\\Class_FF&SubClass_5D", "USB\\Class_FF" };
        }

        // xinputhid's INF binds above the HID child on the &IG_00 hardware
        // ID of the parent, with driverPid where its INF lists another PID,
        // and SDL3 and HIDAPI skip an &IG_00 path and read it through
        // XInput.
        // A Bluetooth xinputhid device keeps its three hardware IDs: the
        // inbox xinputhid.inf lists the real pads' Bluetooth hardware IDs
        // (Bth_Bus, Btle_Bus), and one of them ahead of root\HIDMaestro would
        // outrank this driver's INF and bind xinputhid's Bluetooth sections
        // to the devnode. It takes the Bluetooth compatible IDs alone.
        var bt = bluetooth ? BluetoothIds(profile, vid, pid) : default;
        if (xi.Xinputhid)
        {
            var compat = new List<string>();
            if (bluetooth) compat.AddRange(bt.Compatible);
            compat.Add("root\\HIDMaestroGamepad");
            compat.Add("root\\HIDMaestro");
            return new PresentationPlan
            {
                Path = CreatePath.XinputhidGamepad,
                Enumerator = $"HIDMAESTRO_VID_{vid}_PID_{hwPid:X4}&IG_00",
                HardwarePid = hwPid,
                HardwareIds = new[] { $"root\\VID_{vid}&PID_{hwPid:X4}&IG_00", "root\\HIDMaestroGamepad", "root\\HIDMaestro" },
                CompatibleIds = compat,
                FunctionMode = 0,
                WaitsForXInputSlot = true,
                BusType = BusTypeStamp.EveryDevnodeUsb,
                PresentationSource = xi.Source,
            };
        }

        // Only an XInput device carries IG_ in its enumerator. Microsoft's
        // own test for one is the substring ("check each device ID to see
        // if it contains "IG_"", Learn, Comparison of XInput and
        // DirectInput features), and SDL skips an IG_ path in its HIDAPI
        // and DirectInput backends (hid.c:1016-1018,
        // SDL_dinputjoystick.c:251-255 at 3.4.18). WGI and GameInputSvc
        // identify an Xbox controller by USB\MS_COMP_XUSB10. SDL3 and
        // hidapi take the bus type from the substrings USB, BTHENUM and
        // BTHLEDEVICE in the parent's compatible IDs
        // (hid_internal_detect_bus_type, hid.c:741-808).
        string enumerator, hwId;
        string[] compatIds = Array.Empty<string>(), btHardware = Array.Empty<string>();
        if (companion)
        {
            enumerator = $"VID_{vid}&PID_{pid}&IG_00";
            hwId = $"root\\VID_{vid}&PID_{pid}&IG_00";
            compatIds = new[] { "USB\\MS_COMP_XUSB10", "USB\\Class_FF&SubClass_5D&Prot_01", "USB\\Class_FF&SubClass_5D" };
        }
        else
        {
            enumerator = "HIDClass";
            hwId = $"root\\VID_{vid}&PID_{pid}";
            if (bluetooth)
            {
                btHardware = bt.Hardware;
                compatIds = bt.Compatible;
            }
        }
        // root\HIDMaestro, which this driver's INF matches, comes before the
        // Bluetooth hardware IDs, so no INF that lists one of those can
        // outrank it (identifier score, Learn, How Windows ranks driver
        // packages). The HID child takes its hardware IDs from these, as a
        // real Bluetooth device's child does.
        var hardwareIds = new List<string> { hwId, "root\\HIDMaestro" };
        hardwareIds.AddRange(btHardware);
        return new PresentationPlan
        {
            Path = CreatePath.Root,
            Enumerator = enumerator,
            HardwarePid = profile.ProductId,
            HardwareIds = hardwareIds,
            CompatibleIds = compatIds,
            UpperFilterTripwire = companion,
            XusbCompanion = companion,
            CompanionHardwareIds = companionHw,
            CompanionCompatibleIds = companionCompat,
            FunctionMode = companion ? 1 : 0,
            WaitsForXInputSlot = companion,
            PacksGip = companion,
            BusType = BusTypeStamp.EveryDevnodeUsb,
            PresentationSource = xi.Source,
        };
    }

    /// <summary>The hardware and compatible IDs a real Bluetooth device of
    /// the profile's transport carries, as the devbox reads them from a
    /// present DualSense (classic) and Xbox Series pad (LE):
    /// <c>BTHENUM\{00001124-...}_VID&amp;0002054c_PID&amp;0ce6</c> and
    /// <c>..._LOCALMFG&amp;0002</c> with compatible IDs <c>BTHENUM\{00001124-...}</c>
    /// and <c>..._GENERIC</c>, and
    /// <c>BTHLEDevice\{00001812-...}_Dev_VID&amp;02045e_PID&amp;0b13_REV&amp;0523</c>,
    /// the same without <c>_REV</c>, and <c>..._LOCALMFG&amp;0002</c> with
    /// compatible IDs <c>BTHLEDevice\{00001812-...}</c> and
    /// <c>BTHLEDevice\GenericATTService</c>. The <c>_LOCALMFG</c> ID names
    /// the host's Bluetooth radio, not the device: every Bluetooth device on
    /// the devbox, Sony, Nintendo and Valve alike, carries
    /// <c>_LOCALMFG&amp;0002</c>, and its radio is Intel's
    /// (<c>USB\VID_8087&amp;PID_0032</c>), whose SIG company identifier is
    /// 0x0002. HIDMaestro writes that form on every machine, so its
    /// Bluetooth devnodes claim the devbox's Intel radio wherever they run.
    /// No INF on the devbox matches a <c>_LOCALMFG</c> ID, so the claim moves no
    /// driver ranking. The vendor ID source is 2 (the
    /// USB-IF) or 1 (the Bluetooth SIG), as the Luna's
    /// <c>_Dev_VID&amp;010171</c> carries (ControllersInfo
    /// lunacontroller/README.md:11), and the revision is the profile's
    /// version number. SDL3 and hidapi read the bus as Bluetooth from
    /// BTHENUM and BTHLEDEVICE in the parent's compatible IDs, and LE from
    /// the second (hid.c:784-795 at 3.4.18).</summary>
    static (string[] Hardware, string[] Compatible) BluetoothIds(ControllerProfile profile, string vid, string pid)
    {
        string v = vid.ToLowerInvariant(), p = pid.ToLowerInvariant();
        bool sig = profile.BluetoothVendorIdSource switch
        {
            null or "usb" => false,
            "bluetooth" => true,
            var s => throw new InvalidOperationException(
                $"Profile '{profile.Id}': $.bluetoothVendorIdSource is \"{s}\", not \"usb\" or \"bluetooth\"."),
        };
        const string Hid = "{00001124-0000-1000-8000-00805f9b34fb}";
        const string Hogp = "{00001812-0000-1000-8000-00805f9b34fb}";
        // The vendor ID source (DIS PnP ID: 1 the Bluetooth SIG, 2 the
        // USB-IF), and the host radio's SIG company identifier the devbox's
        // Bluetooth devices carry in _LOCALMFG.
        int source = sig ? 1 : 2;
        const int LocalRadioCompany = 2;
        switch (profile.BluetoothTransport)
        {
            case null:
            case "classic":
                return (new[] { $"BTHENUM\\{Hid}_VID&{source:x4}{v}_PID&{p}", $"BTHENUM\\{Hid}_LOCALMFG&{LocalRadioCompany:x4}" },
                        new[] { $"BTHENUM\\{Hid}", $"BTHENUM\\{Hid}_GENERIC" });
            case "le":
                string rev = $"{profile.VersionNumber ?? 0x0100:x4}";
                string dev = $"BTHLEDevice\\{Hogp}_Dev_VID&{source:x2}{v}_PID&{p}";
                return (new[] { $"{dev}_REV&{rev}", dev, $"BTHLEDevice\\{Hogp}_LOCALMFG&{LocalRadioCompany:x4}" },
                        new[] { $"BTHLEDevice\\{Hogp}", "BTHLEDevice\\GenericATTService" });
            default:
                throw new InvalidOperationException(
                    $"Profile '{profile.Id}': $.bluetoothTransport is \"{profile.BluetoothTransport}\", not \"classic\" or \"le\".");
        }
    }

    /// <summary>Whether <paramref name="enumerator"/> is one a plan creates
    /// ROOT parents under: <c>HIDClass</c>, or
    /// <c>VID_xxxx&amp;PID_yyyy&amp;IG_00</c> with four hex digits each. The
    /// rival scan (<see cref="DeviceManager.ClearParentIdPrefixRivals"/>)
    /// covers every one of them.</summary>
    public static bool IsRootParentEnumerator(string enumerator)
    {
        if (string.Equals(enumerator, "HIDClass", StringComparison.OrdinalIgnoreCase)) return true;
        // VID_xxxx&PID_yyyy&IG_00: 4 + 4 + 5 + 4 + 6 characters.
        if (enumerator.Length != 23) return false;
        return enumerator.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
            && IsHex4(enumerator, 4)
            && string.Compare(enumerator, 8, "&PID_", 0, 5, StringComparison.OrdinalIgnoreCase) == 0
            && IsHex4(enumerator, 13)
            && string.Compare(enumerator, 17, "&IG_00", 0, 6, StringComparison.OrdinalIgnoreCase) == 0;
    }

    /// <summary>Whether <paramref name="enumerator"/> is one a plan creates
    /// SWD parents under, <c>HIDMAESTRO_VID_xxxx_PID_yyyy&amp;IG_00</c>
    /// (the xinputhid path).</summary>
    public static bool IsSwdParentEnumerator(string enumerator)
    {
        // HIDMAESTRO_VID_xxxx_PID_yyyy&IG_00: 15 + 4 + 5 + 4 + 6 characters.
        if (enumerator.Length != 34) return false;
        return enumerator.StartsWith("HIDMAESTRO_VID_", StringComparison.OrdinalIgnoreCase)
            && IsHex4(enumerator, 15)
            && string.Compare(enumerator, 19, "_PID_", 0, 5, StringComparison.OrdinalIgnoreCase) == 0
            && IsHex4(enumerator, 24)
            && string.Compare(enumerator, 28, "&IG_00", 0, 6, StringComparison.OrdinalIgnoreCase) == 0;
    }

    static bool IsHex4(string s, int at)
    {
        for (int i = at; i < at + 4; i++)
            if (!Uri.IsHexDigit(s[i])) return false;
        return true;
    }

    /// <summary>The plan as text, one field to a line, for the golden
    /// record layout_audit_check keeps.</summary>
    public string Describe(string profileId)
    {
        var sb = new StringBuilder();
        void Line(string name, string value) => sb.Append(profileId).Append(' ').Append(name).Append('=').Append(value).Append('\n');
        Line("path", Path.ToString());
        Line("enumerator", Enumerator ?? "-");
        Line("hardwarePid", $"{HardwarePid:X4}");
        Line("hardwareIds", string.Join("|", HardwareIds));
        Line("compatibleIds", string.Join("|", CompatibleIds));
        Line("upperFilterTripwire", UpperFilterTripwire ? "1" : "0");
        Line("xusbCompanion", XusbCompanion ? "1" : "0");
        Line("companionHardwareIds", string.Join("|", CompanionHardwareIds));
        Line("companionCompatibleIds", string.Join("|", CompanionCompatibleIds));
        Line("functionMode", FunctionMode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Line("waitsForXInputSlot", WaitsForXInputSlot ? "1" : "0");
        Line("packsGip", PacksGip ? "1" : "0");
        Line("busType", BusType.ToString());
        return sb.ToString();
    }
}
