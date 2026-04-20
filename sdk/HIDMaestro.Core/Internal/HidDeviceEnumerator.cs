using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HIDMaestro.Internal;

/// <summary>
/// Enumerate connected HID-class devices and read their
/// attributes/strings/preparsed-data via user-mode SetupAPI and
/// HidD_* P/Invokes. Does not create any virtual devices; read-only
/// inspection of what's physically attached.
/// </summary>
internal static class HidDeviceEnumerator
{
    // ── SetupAPI ────────────────────────────────────────────────

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr hDevInfo);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr hDevInfo, IntPtr devInfo, ref Guid interfaceClassGuid,
        uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr hDevInfo, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
        out uint requiredSize, IntPtr deviceInfoData);

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

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr data);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetAttributes(IntPtr hidDeviceObject, ref HIDD_ATTRIBUTES attr);
    [DllImport("hid.dll")] private static extern bool HidD_GetProductString(IntPtr hidDeviceObject, byte[] buffer, uint bufferLength);
    [DllImport("hid.dll")] private static extern bool HidD_GetManufacturerString(IntPtr hidDeviceObject, byte[] buffer, uint bufferLength);
    [DllImport("hid.dll")] private static extern bool HidD_GetSerialNumberString(IntPtr hidDeviceObject, byte[] buffer, uint bufferLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    // ── HidP_GetCaps (for top-level Usage/UsagePage) ────────────

    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS caps);

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

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

    /// <summary>Enumerate and return only devices matching a VID:PID.</summary>
    public static RawHidDevice? FindByVidPid(ushort vid, ushort pid)
    {
        foreach (var d in Enumerate())
        {
            if (d.VendorId == vid && d.ProductId == pid)
                return d;
        }
        return null;
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
                    var caps = new HIDP_CAPS { Reserved = new ushort[17] };
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
}
