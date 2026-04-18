using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.IO;

// Open our virtual's HID device file and call HidD_SetOutputReport with
// a test byte sequence. If our driver's IOCTL_UMDF_HID_SET_OUTPUT_REPORT
// handler fires, [HID-SETOUT] appears in xusbshim_log.txt. If driver's
// IOCTL_HID_WRITE_REPORT (via WriteFile) fires, [HID-WRITE] appears.
// Tests both HID output paths to confirm instrumentation is live.

internal static class P
{
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetOutputReport(SafeFileHandle HidDeviceObject,
        byte[] ReportBuffer, uint ReportBufferLength);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator,
        IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet,
        IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData,
        uint DeviceInterfaceDetailDataSize, out uint RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        [MarshalAs(UnmanagedType.LPStruct)] SP_DEVICE_INTERFACE_DETAIL_DATA DeviceInterfaceDetailData,
        uint DeviceInterfaceDetailDataSize, out uint RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("hid.dll")]
    static extern void HidD_GetHidGuid(out Guid HidGuid);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    class SP_DEVICE_INTERFACE_DETAIL_DATA {
        public uint cbSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string DevicePath = "";
    }

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject, out HIDD_ATTRIBUTES Attributes);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    const uint DIGCF_PRESENT = 0x00000002;
    const uint DIGCF_DEVICEINTERFACE = 0x00000010;

    static void Main()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr devInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1)) {
            Console.WriteLine("SetupDiGetClassDevs failed");
            return;
        }
        try
        {
            uint i = 0;
            while (true)
            {
                var did = new SP_DEVICE_INTERFACE_DATA();
                did.cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();
                if (!SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, i, ref did))
                    break;
                i++;

                var detail = new SP_DEVICE_INTERFACE_DETAIL_DATA {
                    cbSize = IntPtr.Size == 8 ? 8u : 6u
                };
                if (!SetupDiGetDeviceInterfaceDetail(devInfo, ref did, detail, 256, out _, IntPtr.Zero))
                    continue;

                string path = detail.DevicePath;
                Console.WriteLine($"  iface[{i-1}]: {path}");
                // Filter for our virtual's HID child (VID 045E PID 028E IG_00)
                if (!path.Contains("vid_045e&pid_028e&ig_00", StringComparison.OrdinalIgnoreCase)) continue;

                Console.WriteLine($"Opening: {path}");
                var handle = CreateFile(path, 0x40000000 /* GENERIC_WRITE */, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid) {
                    Console.WriteLine($"  CreateFile failed: {Marshal.GetLastWin32Error()}");
                    continue;
                }
                HidD_GetAttributes(handle, out var attr);
                Console.WriteLine($"  VID=0x{attr.VendorID:X4} PID=0x{attr.ProductID:X4}");

                // Send a test output report. ReportID 0, 4 bytes of test payload.
                byte[] report = new byte[] { 0x00, 0xAA, 0xBB, 0xCC, 0xDD };
                bool ok = HidD_SetOutputReport(handle, report, (uint)report.Length);
                Console.WriteLine($"  HidD_SetOutputReport({report.Length}B) = {ok}, err={Marshal.GetLastWin32Error()}");

                // Also try WriteFile via a stream
                try {
                    uint written = 0;
                    bool wf = WriteFile(handle, report, (uint)report.Length, out written, IntPtr.Zero);
                    Console.WriteLine($"  WriteFile({report.Length}B) = {wf}, wrote={written}, err={Marshal.GetLastWin32Error()}");
                } catch (Exception ex) {
                    Console.WriteLine($"  WriteFile failed: {ex.Message}");
                }

                handle.Close();
                break;
            }
            Console.WriteLine("Done. Check xusbshim_log.txt for [HID-SETOUT] and [HID-WRITE] entries.");
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);
}
