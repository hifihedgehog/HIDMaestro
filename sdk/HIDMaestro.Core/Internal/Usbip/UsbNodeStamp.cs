using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace HIDMaestro.Internal.Usbip;

/// <summary>What a USB/IP device of a Bluetooth identity carries on its own
/// transport node, the USB devnode HidUsb runs on, so the programs that read
/// that node read what they read on the real pad's.
///
/// <para><b>xinputhid on the transport node.</b> Windows installs xinputhid
/// for a Bluetooth Xbox pad on the pad's transport node, above the HID
/// driver: <c>xinputhid.inf</c>'s Bth_Bus and Btle_Bus sections write
/// <c>UpperFilters</c> = <c>xinputhid</c> on the node (XInput_Hid.AddRegHW)
/// and <c>DevicePropertyFlags</c> = 1 in its driver key (Bth_Bus.AddReg,
/// Btle_Bus.AddReg, over XInput_Hid.AddReg's <c>ConfigFlags</c> = 1). There
/// xinputhid names the pad's game controller collection with
/// <c>&amp;IG_</c>, publishes the XUSB interface on it and reads the pad's
/// own reports. Microsoft's XInputHID document gives a USB HID device the
/// same three values (GIPDocumentation, "Custom Driver Stacks", the sample
/// INF's USB_Input_Device sections). <see cref="WriteXinputhid"/> writes
/// them on the device's node, and Windows builds that stack at the node's
/// next start.</para>
///
/// <para><b>The bus a reader takes from the parent.</b> hidapi reads a HID
/// device's bus from the compatible IDs of its parent, the first that names
/// a bus (SDL src/hidapi/windows/hid.c, hid_internal_detect_bus_type), and
/// SDL's PlayStation and Switch drivers pick the Bluetooth or the USB
/// protocol from it. A USB node's compatible IDs all begin <c>USB\</c>.
/// <see cref="EnsureMarker"/> puts one ID ahead of them that names the
/// Bluetooth enumerator of the identity's transport and that no INF
/// matches. Windows writes the node's compatible IDs again at every start,
/// so the device writes the marker during each start, before its HID child
/// exists (<see cref="UsbipEmulatedDevice"/>).</para></summary>
internal static class UsbNodeStamp
{
    private const string Service = "xinputhid";

    /// <summary>The marker of a classic Bluetooth identity: BTHENUM and the
    /// HID service class a real pad's node carries, with a suffix no INF
    /// names. hidbth.inf matches the bare service ID, so the suffix is what
    /// keeps Windows from ranking a Bluetooth driver for the USB
    /// node.</summary>
    internal const string ClassicMarker = @"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_HIDMAESTRO";

    /// <summary>The marker of a Bluetooth LE identity: BTHLEDevice and the
    /// HID-over-GATT service, with the same suffix. hidbthle.inf matches
    /// the bare service ID.</summary>
    internal const string LeMarker = @"BTHLEDevice\{00001812-0000-1000-8000-00805f9b34fb}_HIDMAESTRO";

    /// <summary>What one device's node carries.</summary>
    internal readonly record struct Plan(bool Xinputhid, string? BusMarker);

    /// <summary>The plan of the profile a device presents: xinputhid for the
    /// Bluetooth HID form of an xinputhid profile, and the bus marker of
    /// every Bluetooth identity.</summary>
    internal static Plan For(ControllerProfile presented) =>
        new(presented.XinputhidOnTransport, presented.IsBluetooth ? (presented.IsBluetoothLe ? LeMarker : ClassicMarker) : null);

    private static string EnumPath(string instanceId) => @"SYSTEM\CurrentControlSet\Enum\" + instanceId;

    private static string? DriverKeyPath(RegistryKey node) =>
        node.GetValue("Driver") is string driver && driver.Length > 0 ? @"SYSTEM\CurrentControlSet\Control\Class\" + driver : null;

    /// <summary>Whether the node carries the values that put xinputhid on
    /// it: the filter name and the driver key's flag value, both
    /// (<c>true</c>) or neither (<c>false</c>). Null for one without the
    /// other, which is neither arrangement: the filter name alone keeps the
    /// HID device out of Windows.Gaming.Input and gives it no XInput
    /// slot.</summary>
    internal static bool? XinputhidOn(string instanceId)
    {
        try
        {
            using var node = Registry.LocalMachine.OpenSubKey(EnumPath(instanceId));
            if (node == null) return false;
            bool filter = node.GetValue("UpperFilters") is string[] filters && filters.Contains(Service, StringComparer.OrdinalIgnoreCase);
            bool flag = false;
            if (DriverKeyPath(node) is { } path)
            {
                using var driver = Registry.LocalMachine.OpenSubKey(path);
                flag = driver?.GetValue("DevicePropertyFlags") is int flags && flags == 1;
            }
            return filter == flag ? filter : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    /// <summary>Writes the three values. False when the node has no driver
    /// key yet, as before Windows installed its driver, or a write was
    /// refused.</summary>
    internal static bool WriteXinputhid(string instanceId)
    {
        try
        {
            using var node = Registry.LocalMachine.OpenSubKey(EnumPath(instanceId), writable: true);
            if (node == null || DriverKeyPath(node) is not { } path) return false;
            using var driver = Registry.LocalMachine.OpenSubKey(path, writable: true);
            if (driver == null) return false;
            driver.SetValue("DevicePropertyFlags", 1, RegistryValueKind.DWord);
            driver.SetValue("ConfigFlags", 1, RegistryValueKind.DWord);
            var filters = (node.GetValue("UpperFilters") as string[] ?? Array.Empty<string>()).ToList();
            if (!filters.Contains(Service, StringComparer.OrdinalIgnoreCase)) filters.Add(Service);
            node.SetValue("UpperFilters", filters.ToArray(), RegistryValueKind.MultiString);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (DeviceOrchestrator.DiagEnabled) DeviceOrchestrator.LogDiag($"usbip node stamp {instanceId}: xinputhid write refused: {e.Message}");
            return false;
        }
    }

    /// <summary>Takes the three values off a node an earlier device of
    /// another profile left them on: a record is one VID, PID and serial's,
    /// and a profile that presents a plain HID device can share
    /// those.</summary>
    internal static bool ClearXinputhid(string instanceId)
    {
        try
        {
            using var node = Registry.LocalMachine.OpenSubKey(EnumPath(instanceId), writable: true);
            if (node == null) return false;
            if (node.GetValue("UpperFilters") is string[] filters)
            {
                var rest = filters.Where(f => !string.Equals(f, Service, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (rest.Length == 0) node.DeleteValue("UpperFilters", throwOnMissingValue: false);
                else if (rest.Length != filters.Length) node.SetValue("UpperFilters", rest, RegistryValueKind.MultiString);
            }
            if (DriverKeyPath(node) is { } path)
            {
                using var driver = Registry.LocalMachine.OpenSubKey(path, writable: true);
                if (driver?.GetValue("DevicePropertyFlags") is int flags && flags == 1)
                {
                    driver.DeleteValue("DevicePropertyFlags", throwOnMissingValue: false);
                    if (driver.GetValue("ConfigFlags") is int config && config == 1) driver.DeleteValue("ConfigFlags", throwOnMissingValue: false);
                }
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (DeviceOrchestrator.DiagEnabled) DeviceOrchestrator.LogDiag($"usbip node stamp {instanceId}: xinputhid clear refused: {e.Message}");
            return false;
        }
    }

    /// <summary>Puts <paramref name="marker"/> first in the node's
    /// compatible IDs when it is not there. True when it is first
    /// after.</summary>
    internal static bool EnsureMarker(string instanceId, string marker)
    {
        try
        {
            using var node = Registry.LocalMachine.OpenSubKey(EnumPath(instanceId), writable: true);
            if (node == null) return false;
            var ids = node.GetValue("CompatibleIDs") as string[] ?? Array.Empty<string>();
            if (ids.Length > 0 && string.Equals(ids[0], marker, StringComparison.OrdinalIgnoreCase)) return true;
            var next = new List<string>(ids.Length + 1) { marker };
            next.AddRange(ids.Where(i => !IsMarker(i)));
            node.SetValue("CompatibleIDs", next.ToArray(), RegistryValueKind.MultiString);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (DeviceOrchestrator.DiagEnabled) DeviceOrchestrator.LogDiag($"usbip node stamp {instanceId}: marker write refused: {e.Message}");
            return false;
        }
    }

    /// <summary>Takes a marker off a node an earlier device of a Bluetooth
    /// identity left on it, for a device of a profile that is none.</summary>
    internal static void ClearMarker(string instanceId)
    {
        try
        {
            using var node = Registry.LocalMachine.OpenSubKey(EnumPath(instanceId), writable: true);
            if (node?.GetValue("CompatibleIDs") is not string[] ids || !ids.Any(IsMarker)) return;
            node.SetValue("CompatibleIDs", ids.Where(i => !IsMarker(i)).ToArray(), RegistryValueKind.MultiString);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    private static bool IsMarker(string id) =>
        string.Equals(id, ClassicMarker, StringComparison.OrdinalIgnoreCase) || string.Equals(id, LeMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when Windows has the xinputhid service, which a node's
    /// <c>UpperFilters</c> names. Windows registers it when xinputhid.inf
    /// first installs on a device.</summary>
    internal static bool ServiceRegistered()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Service);
            return key?.GetValue("ImagePath") != null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
}
