using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace HIDMaestro.Internal;

/// <summary>
/// Enumerate connected HID-class devices and read their
/// attributes/strings/preparsed-data via user-mode SetupAPI and
/// HidD_* P/Invokes. Does not create any virtual devices; read-only
/// inspection of what's physically attached.
/// </summary>
internal static partial class HidDeviceEnumerator
{
    // ── SetupAPI ────────────────────────────────────────────────

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr hDevInfo);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(IntPtr hDevInfo, IntPtr devInfo, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetailW(IntPtr hDevInfo, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    private const uint DIGCF_PRESENT         = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    // ── HidD_* ──────────────────────────────────────────────────

    [LibraryImport("hid.dll")]
    private static partial void HidD_GetHidGuid(out Guid guid);
    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr data);
    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_FreePreparsedData(IntPtr data);
    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetAttributes(IntPtr hidDeviceObject, ref HIDD_ATTRIBUTES attr);
    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetProductString(IntPtr hidDeviceObject, byte[] buffer, uint bufferLength);
    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetManufacturerString(IntPtr hidDeviceObject, byte[] buffer, uint bufferLength);
    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetSerialNumberString(IntPtr hidDeviceObject, byte[] buffer, uint bufferLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    // ── HidP_GetCaps (for top-level Usage/UsagePage) ────────────

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS caps);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    // ── kernel32 ────────────────────────────────────────────────

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    private static readonly IntPtr INVALID_HANDLE = new(-1);
    private const uint FILE_SHARE_READ_WRITE = 0x3;
    private const uint OPEN_EXISTING = 3;

    // ── Public entry ────────────────────────────────────────────

    /// <summary>Raw record for one connected HID device interface.</summary>
    public sealed class RawHidDevice
    {
        public string DevicePath = "";
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
        public string? ProductString;
        public string? ManufacturerString;
        public string? SerialNumberString;
        public ushort TopLevelUsagePage;
        public ushort TopLevelUsage;
        public ushort InputReportByteLength;
        public byte[] ReconstructedDescriptor = Array.Empty<byte>();
    }

    /// <summary>Enumerate every HID-class device interface currently present.
    /// For each, reads attributes, strings, top-level Usage/UsagePage, and
    /// reconstructs the HID report descriptor from preparsed data.</summary>
    public static IReadOnlyList<RawHidDevice> Enumerate()
    {
        var results = new List<RawHidDevice>();
        Guid hidGuid;
        HidD_GetHidGuid(out hidGuid);

        IntPtr dis = SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (dis == INVALID_HANDLE)
            return results;

        try
        {
            var ifd = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; i < 10000; i++)
            {
                if (!SetupDiEnumDeviceInterfaces(dis, IntPtr.Zero, ref hidGuid, i, ref ifd))
                    break;

                if (TryReadDevice(dis, ref ifd, ref hidGuid, out var dev))
                    results.Add(dev);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(dis);
        }
        return results;
    }

    private static bool TryReadDevice(IntPtr dis, ref SP_DEVICE_INTERFACE_DATA ifd, ref Guid hidGuid, out RawHidDevice dev)
    {
        dev = new RawHidDevice();

        // Get required size for the interface detail struct.
        uint req = 0;
        SetupDiGetDeviceInterfaceDetailW(dis, ref ifd, IntPtr.Zero, 0, out req, IntPtr.Zero);
        if (req == 0) return false;

        IntPtr detail = Marshal.AllocHGlobal((int)req);
        try
        {
            // cbSize field: on x64 it's 8 (sizeof int plus alignment for the Unicode[] start).
            Marshal.WriteInt32(detail, 8);
            if (!SetupDiGetDeviceInterfaceDetailW(dis, ref ifd, detail, req, out req, IntPtr.Zero))
                return false;

            // Device path starts at offset 4 (after cbSize).
            dev.DevicePath = Marshal.PtrToStringUni(IntPtr.Add(detail, 4)) ?? "";
        }
        finally { Marshal.FreeHGlobal(detail); }

        if (string.IsNullOrEmpty(dev.DevicePath)) return false;

        IntPtr h = CreateFileW(dev.DevicePath, 0 /* no access needed for HidD */, FILE_SHARE_READ_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID_HANDLE) return false;

        try
        {
            var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (!HidD_GetAttributes(h, ref attr)) return false;
            dev.VendorId = attr.VendorID;
            dev.ProductId = attr.ProductID;
            dev.VersionNumber = attr.VersionNumber;

            byte[] strBuf = new byte[512];
            if (HidD_GetProductString(h, strBuf, (uint)strBuf.Length))
                dev.ProductString = Trim(System.Text.Encoding.Unicode.GetString(strBuf));
            if (HidD_GetManufacturerString(h, strBuf, (uint)strBuf.Length))
                dev.ManufacturerString = Trim(System.Text.Encoding.Unicode.GetString(strBuf));
            if (HidD_GetSerialNumberString(h, strBuf, (uint)strBuf.Length))
                dev.SerialNumberString = Trim(System.Text.Encoding.Unicode.GetString(strBuf));

            if (HidD_GetPreparsedData(h, out IntPtr pp) && pp != IntPtr.Zero)
            {
                try
                {
                    var caps = new HIDP_CAPS();
                    HidP_GetCaps(pp, ref caps);
                    dev.TopLevelUsagePage = caps.UsagePage;
                    dev.TopLevelUsage = caps.Usage;
                    dev.InputReportByteLength = caps.InputReportByteLength;

                    try
                    {
                        dev.ReconstructedDescriptor = HidDescriptorReconstructor.Reconstruct(pp);
                    }
                    catch
                    {
                        // Reconstruction can fail for exotic preparsed blobs (e.g. non-standard
                        // layouts). Leave the descriptor empty; caller sees a zero-length array.
                        dev.ReconstructedDescriptor = Array.Empty<byte>();
                    }
                }
                finally { HidD_FreePreparsedData(pp); }
            }

            return true;
        }
        finally { CloseHandle(h); }
    }

    private static string Trim(string s)
    {
        int n = s.IndexOf('\0');
        return n >= 0 ? s.Substring(0, n) : s;
    }

    // ── The bus ─────────────────────────────────────────────────

    /// <summary>A device's bus as a profile states it: <c>connection</c>,
    /// and for a Bluetooth device <c>bluetoothTransport</c> and
    /// <c>bluetoothVendorIdSource</c>.</summary>
    public readonly record struct Bus(string Connection, string? BluetoothTransport, string? BluetoothVendorIdSource)
    {
        public static readonly Bus Usb = new("usb", null, null);
    }

    /// <summary>The bus of the HID device at <paramref name="devicePath"/>,
    /// read as hidapi's hid_internal_detect_bus_type and SDL's copy of it
    /// read it (hidapi windows/hid.c:630-705 at c3509c1, SDL
    /// src/hidapi/windows/hid.c:741-816 at 804cf65): the interface's instance ID, that devnode's parent, and
    /// the parent's compatible IDs (<see cref="BusFromIds"/>). A device
    /// whose parent cannot be read is USB, the connection the extractor
    /// gives every device it does not find on Bluetooth.</summary>
    public static Bus BusOf(string devicePath)
    {
        var instanceKey = DEVPKEY_Device_InstanceId;
        string? instanceId = MultiSz(InterfaceProperty(devicePath, ref instanceKey)).FirstOrDefault();
        if (instanceId == null
            || CM_Locate_DevNodeW(out uint dev, instanceId, 0) != CR_SUCCESS
            || CM_Get_Parent(out uint parent, dev, 0) != CR_SUCCESS)
            return Bus.Usb;
        var compatibleKey = DEVPKEY_Device_CompatibleIds;
        var hardwareKey = DEVPKEY_Device_HardwareIds;
        return BusFromIds(MultiSz(DevnodeProperty(parent, ref compatibleKey)), MultiSz(DevnodeProperty(parent, ref hardwareKey)));
    }

    /// <summary>hidapi's test, in its order: the first of the parent's
    /// compatible IDs that holds USB, BTHENUM or BTHLEDEVICE, upper-cased,
    /// names the bus, Bluetooth LE for the last. PNP0C50 (I2C), PNP0C51
    /// (SPI) and none of them read as USB, since no profile tells those
    /// apart. For Bluetooth, the parent's hardware IDs give the vendor ID
    /// source, the digits before the VID that a real device carries and
    /// <see cref="PresentationPlan"/> writes the same way: 0001 before the
    /// four VID digits after <c>_VID&amp;</c> (classic), or 01 after
    /// <c>_Dev_VID&amp;</c> (LE), is the Bluetooth SIG's, and any other
    /// source the USB-IF's.</summary>
    internal static Bus BusFromIds(IReadOnlyList<string> compatibleIds, IReadOnlyList<string> hardwareIds)
    {
        foreach (string id in compatibleIds)
        {
            string up = id.ToUpperInvariant();
            if (up.Contains("USB", StringComparison.Ordinal)) return Bus.Usb;
            bool classic = up.Contains("BTHENUM", StringComparison.Ordinal);
            if (classic || up.Contains("BTHLEDEVICE", StringComparison.Ordinal))
                return new Bus("bluetooth", classic ? "classic" : "le", VendorIdSource(hardwareIds, classic ? 4 : 2));
            if (up.Contains("PNP0C50", StringComparison.Ordinal) || up.Contains("PNP0C51", StringComparison.Ordinal)) return Bus.Usb;
        }
        return Bus.Usb;
    }

    /// <summary>"bluetooth" when the first hardware ID with <c>VID&amp;</c>
    /// has a source of <paramref name="digits"/> hex digits equal to 1
    /// before its four VID digits, else null, the USB-IF source a profile
    /// defaults to.</summary>
    private static string? VendorIdSource(IReadOnlyList<string> hardwareIds, int digits)
    {
        foreach (string id in hardwareIds)
        {
            int at = id.IndexOf("VID&", StringComparison.OrdinalIgnoreCase);
            if (at < 0 || at + 4 + digits + 4 > id.Length) continue;
            if (!int.TryParse(id.AsSpan(at + 4, digits), System.Globalization.NumberStyles.HexNumber, null, out int source)) continue;
            return source == 1 ? "bluetooth" : null;
        }
        return null;
    }

    private static string[] MultiSz(byte[]? buf) => buf == null
        ? Array.Empty<string>()
        : System.Text.Encoding.Unicode.GetString(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static byte[]? InterfaceProperty(string path, ref DEVPROPKEY key)
    {
        uint size = 0;
        CM_Get_Device_Interface_PropertyW(path, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buf = new byte[size];
        return CM_Get_Device_Interface_PropertyW(path, ref key, out _, buf, ref size, 0) == CR_SUCCESS ? buf : null;
    }

    private static byte[]? DevnodeProperty(uint dev, ref DEVPROPKEY key)
    {
        uint size = 0;
        CM_Get_DevNode_PropertyW(dev, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buf = new byte[size];
        return CM_Get_DevNode_PropertyW(dev, ref key, out _, buf, ref size, 0) == CR_SUCCESS ? buf : null;
    }

    /// <summary>DEVPROPKEY (devpropdef.h): the property set and the
    /// property's number in it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint propertyId;
    }

    // devpkey.h (Windows SDK 10.0.26100.0), lines 46, 47 and 86.
    private static readonly DEVPROPKEY DEVPKEY_Device_InstanceId =
        new() { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), propertyId = 256 };
    private static readonly DEVPROPKEY DEVPKEY_Device_HardwareIds =
        new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), propertyId = 3 };
    private static readonly DEVPROPKEY DEVPKEY_Device_CompatibleIds =
        new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), propertyId = 4 };

    private const uint CR_SUCCESS = 0;

    [LibraryImport("CfgMgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_PropertyW(string pszDeviceInterface, ref DEVPROPKEY propertyKey,
        out uint propertyType, byte[]? propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    [LibraryImport("CfgMgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY propertyKey,
        out uint propertyType, byte[]? propertyBuffer, ref uint propertyBufferSize, uint ulFlags);
}
