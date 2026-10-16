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
        /// <summary>No HID devnode, the XUSB companion alone.</summary>
        CompanionOnly,
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

    /// <summary>The plan for <paramref name="profile"/>.</summary>
    public static PresentationPlan Of(ControllerProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        string vid = $"{profile.VendorId:X4}";
        string pid = $"{profile.ProductId:X4}";
        ushort hwPid = profile.ResolvedDriverPid != null ? Convert.ToUInt16(profile.ResolvedDriverPid, 16) : profile.ProductId;
        bool companion = profile.RequiresXusbCompanion;
        bool bluetooth = string.Equals(profile.Connection, "bluetooth", StringComparison.OrdinalIgnoreCase);

        if (profile.RequiresUsbipBackend)
            return new PresentationPlan { Path = CreatePath.Usbip, HardwarePid = profile.ProductId };

        string[] companionHw = Array.Empty<string>(), companionCompat = Array.Empty<string>();
        if (companion)
        {
            companionHw = new[] { $"root\\VID_{vid}&PID_{pid}&XI_00", "root\\HIDMaestroXUSB" };
            companionCompat = new[] { "USB\\MS_COMP_XUSB10", "USB\\Class_FF&SubClass_5D&Prot_01", "USB\\Class_FF&SubClass_5D", "USB\\Class_FF" };
        }

        // xinputhid's INF binds above the HID child on the &IG_00 hardware
        // ID of the parent, with driverPid where its INF lists another PID,
        // and SDL3 and HIDAPI skip an &IG_00 path and read it through
        // XInput.
        if (profile.UsesUpperFilter)
        {
            var compat = new List<string>();
            if (profile.Connection == "bluetooth")
                compat.Add($"BTHLEDEVICE\\{{00001812-0000-1000-8000-00805f9b34fb}}_Dev_VID&02{vid}_PID&{pid}");
            compat.Add("root\\HIDMaestroGamepad");
            compat.Add("root\\HIDMaestro");
            return new PresentationPlan
            {
                Path = CreatePath.XinputhidGamepad,
                Enumerator = $"HIDMAESTRO_VID_{vid}_PID_{hwPid:X4}&IG_00",
                HardwarePid = hwPid,
                HardwareIds = new[] { $"root\\VID_{vid}&PID_{hwPid:X4}&IG_00", "root\\HIDMaestroGamepad", "root\\HIDMaestro" },
                CompatibleIds = compat,
                XusbCompanion = companion,
                CompanionHardwareIds = companionHw,
                CompanionCompatibleIds = companionCompat,
                FunctionMode = companion ? 1 : 0,
                WaitsForXInputSlot = true,
                PacksGip = companion,
                BusType = BusTypeStamp.EveryDevnodeUsb,
            };
        }

        // WGI and GameInputSvc identify an Xbox controller by
        // USB\MS_COMP_XUSB10. SDL3 and hidapi take the bus type from the
        // substrings USB, BTHENUM and BTHLEDEVICE in the parent's compatible
        // IDs (hid_internal_detect_bus_type).
        string enumerator, hwId;
        string[] compatIds = Array.Empty<string>();
        if (profile.VendorId == 0x045E)
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
                compatIds = new[]
                {
                    $"BTHENUM\\Dev_VID&02{vid}_PID&{pid}",
                    $"BTHENUM\\{{00001124-0000-1000-8000-00805f9b34fb}}_VID&02{vid}_PID&{pid}",
                };
        }
        return new PresentationPlan
        {
            Path = profile.CompanionOnly ? CreatePath.CompanionOnly : CreatePath.Root,
            Enumerator = profile.CompanionOnly ? null : enumerator,
            HardwarePid = profile.ProductId,
            HardwareIds = profile.CompanionOnly ? Array.Empty<string>() : new[] { hwId, "root\\HIDMaestro" },
            CompatibleIds = profile.CompanionOnly ? Array.Empty<string>() : compatIds,
            UpperFilterTripwire = companion && !profile.CompanionOnly,
            XusbCompanion = companion,
            CompanionHardwareIds = companionHw,
            CompanionCompatibleIds = companionCompat,
            FunctionMode = companion ? 1 : 0,
            WaitsForXInputSlot = companion,
            PacksGip = companion,
            BusType = BusTypeStamp.EveryDevnodeUsb,
        };
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
