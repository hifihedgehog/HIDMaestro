using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HIDMaestroTest;

/// <summary>
/// Test client for HIDMaestro UMDF2 virtual HID minidriver.
///
/// Configuration flow:
///   1. Write HID descriptor + VID/PID to HKLM\SOFTWARE\HIDMaestro
///   2. Restart the device node so driver re-reads config
///   3. Open the HID child device
///   4. Send raw input reports via HidD_SetOutputReport
///
/// Commands:
///   HIDMaestroTest xbox      — Xbox 360 gamepad (standard HID)
///   HIDMaestroTest ds5       — DualSense gamepad
///   HIDMaestroTest cleanup   — Remove device, driver, certs, registry
/// </summary>
class Program
{
    const string REG_PATH = @"SOFTWARE\HIDMaestro";

    // ── P/Invoke: CfgMgr32 for device property setting ──

    [DllImport("CfgMgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("CfgMgr32.dll", SetLastError = true)]
    static extern uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", SetLastError = true)]
    static extern uint CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_IDW(uint dnDevInst, char[] Buffer, uint BufferLen, uint ulFlags);

    [DllImport("CfgMgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint CM_Set_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY propertyKey,
        uint propertyType, byte[] propertyBuffer, uint propertyBufferSize, uint ulFlags);

    [DllImport("CfgMgr32.dll", SetLastError = true)]
    static extern uint CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY propertyKey,
        out uint propertyType, byte[] propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    static void SetBusReportedDeviceDesc(string instanceId, string description)
    {
        // DEVPKEY_Device_BusReportedDeviceDesc = {540b947e-8b40-45bc-a8a2-6a0b894cbda2}, 4
        var key = new DEVPROPKEY
        {
            fmtid = new Guid(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2),
            pid = 4
        };

        if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0) return;

        byte[] strBytes = Encoding.Unicode.GetBytes(description + "\0");
        // DEVPROP_TYPE_STRING = 0x12
        CM_Set_DevNode_PropertyW(devInst, ref key, 0x12, strBytes, (uint)strBytes.Length, 0);

        // Also set on HID child
        if (CM_Get_Child(out uint childInst, devInst, 0) == 0)
        {
            CM_Set_DevNode_PropertyW(childInst, ref key, 0x12, strBytes, (uint)strBytes.Length, 0);
        }
    }

    static void SetDeviceFriendlyName(string rootInstanceId, string name)
    {
        // DEVPKEY_Device_FriendlyName = {a45c254e-df1c-4efd-8020-67d146a850e0}, 14
        var fnKey = new DEVPROPKEY
        {
            fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
            pid = 14
        };
        // DEVPKEY_Device_DeviceDesc = {a45c254e-df1c-4efd-8020-67d146a850e0}, 2
        var ddKey = new DEVPROPKEY
        {
            fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
            pid = 2
        };
        byte[] strBytes = Encoding.Unicode.GetBytes(name + "\0");

        uint locResult = CM_Locate_DevNodeW(out uint devInst, rootInstanceId, 0);
        if (locResult != 0) { Console.Error.Write($"(locate={locResult}) "); return; }

        // Set both FriendlyName and DeviceDesc on root — eliminates all
        // "HIDMaestro Virtual HID Device" appearances
        CM_Set_DevNode_PropertyW(devInst, ref fnKey, 0x12, strBytes, (uint)strBytes.Length, 0);
        CM_Set_DevNode_PropertyW(devInst, ref ddKey, 0x12, strBytes, (uint)strBytes.Length, 0);

        // Set on HID child too
        if (CM_Get_Child(out uint childInst, devInst, 0) == 0)
        {
            CM_Set_DevNode_PropertyW(childInst, ref fnKey, 0x12, strBytes, (uint)strBytes.Length, 0);
            CM_Set_DevNode_PropertyW(childInst, ref ddKey, 0x12, strBytes, (uint)strBytes.Length, 0);

            // xinputhid auto-loads via PnP matching (driverPid=02FF in hardware ID).
            // Manual injection is NOT needed and causes device restart that kills
            // SDL3/WGI connections. Only inject for non-auto-matching profiles.
        }
    }

    /// <summary>
    /// Adds xinputhid as an upper filter on the HID child device, making it
    /// visible to XInput applications. This is how real Xbox controllers work:
    /// xinputhid.sys loads as an upper filter and registers with XInput.
    /// </summary>
    /// <summary>
    /// Adds xinputhid as an upper filter on the HID child device via the
    /// persistent device registry, then restarts the device so the filter loads.
    /// This makes the device visible to XInput applications.
    /// </summary>
    static string _upperFilterName = "xinputhid"; // default; set per profile

    static void InjectXInputFilter(uint childDevInst)
    {
        string filterName = _upperFilterName;

        byte[] idBuf = new byte[512];
        CM_Get_Device_IDW(childDevInst, idBuf, (uint)idBuf.Length / 2, 0);
        string instanceId = Encoding.Unicode.GetString(idBuf).TrimEnd('\0');
        if (string.IsNullOrEmpty(instanceId)) return;

        string[] parts = instanceId.Split('\\');
        if (parts.Length < 3) return;

        string regPath = $@"SYSTEM\CurrentControlSet\Enum\{parts[0]}\{parts[1]}\{parts[2]}";
        using var key = Registry.LocalMachine.OpenSubKey(regPath, writable: true);
        if (key == null) return;

        var existing = key.GetValue("UpperFilters") as string[];
        if (existing != null && existing.Any(f => f.Equals(filterName, StringComparison.OrdinalIgnoreCase)))
            return; // Already present

        var newFilters = (existing ?? Array.Empty<string>()).ToList();
        newFilters.Add(filterName);
        key.SetValue("UpperFilters", newFilters.ToArray(), RegistryValueKind.MultiString);

        // Restart the device to load the filter
        // pnputil /restart-device doesn't work well here, so we disable/enable
        RunProcess("pnputil.exe", $"/disable-device \"{instanceId}\"");
        Thread.Sleep(500);
        RunProcess("pnputil.exe", $"/enable-device \"{instanceId}\"");
        Thread.Sleep(1000);
    }

    [DllImport("CfgMgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_IDW(uint dnDevInst, byte[] buffer, uint bufferLen, uint ulFlags);

    /// <summary>
    /// Finds the current HID child of ROOT\HID_IG_00\0000, adds xinputhid as an
    /// upper filter in its persistent registry, and restarts the device.
    /// Returns true if successful.
    /// </summary>
    static bool EnableXInputOnHidChild()
    {
        // Find the active HID child instance ID
        var (_, output) = RunProcess("powershell.exe",
            "-NoProfile -Command \"Get-PnpDevice | Where-Object { $_.InstanceId -like 'HID\\HIDCLASS\\*' -and $_.Status -eq 'OK' } | Select-Object -ExpandProperty InstanceId\"");
        string instanceId = output.Trim();
        if (string.IsNullOrEmpty(instanceId) || !instanceId.StartsWith("HID\\")) return false;

        // Parse: HID\HIDCLASS\1&xxx&yyy&0000
        string[] parts = instanceId.Split('\\');
        if (parts.Length < 3) return false;

        // Open device enum registry key
        string regPath = $@"SYSTEM\CurrentControlSet\Enum\{parts[0]}\{parts[1]}\{parts[2]}";
        using var key = Registry.LocalMachine.OpenSubKey(regPath, writable: true);
        if (key == null) return false;

        // Check if xinputhid already present
        var existing = key.GetValue("UpperFilters") as string[];
        if (existing != null && existing.Any(f => f.Equals("xinputhid", StringComparison.OrdinalIgnoreCase)))
            return true; // Already configured

        // Add xinputhid
        var newFilters = (existing ?? Array.Empty<string>()).ToList();
        newFilters.Add("xinputhid");
        key.SetValue("UpperFilters", newFilters.ToArray(), RegistryValueKind.MultiString);

        // Restart the HID child to load the new filter
        RunProcess("pnputil.exe", $"/restart-device \"{instanceId}\"");
        Thread.Sleep(2000);

        return true;
    }

    // ── P/Invoke ──

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("hid.dll")]
    static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool HidD_SetOutputReport(SafeFileHandle HidDeviceObject,
        byte[] ReportBuffer, uint ReportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool HidD_SetFeature(SafeFileHandle HidDeviceObject,
        byte[] ReportBuffer, uint ReportBufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer,
        uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        byte[] lpInBuffer, uint nInBufferSize, byte[]? lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject,
        ref HIDD_ATTRIBUTES Attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool HidD_GetPreparsedData(SafeFileHandle HidDeviceObject, out IntPtr PreparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr PreparsedData, out HIDP_CAPS Capabilities);

    [DllImport("hid.dll")]
    static extern int HidP_GetValueCaps(int ReportType, [Out] byte[] ValueCaps, ref ushort ValueCapsLength, IntPtr PreparsedData);

    [DllImport("hid.dll")]
    static extern int HidP_GetButtonCaps(int ReportType, [Out] byte[] ButtonCaps, ref ushort ButtonCapsLength, IntPtr PreparsedData);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
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

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [DllImport("SetupAPI.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid ClassGuid, IntPtr Enumerator,
        IntPtr hwndParent, uint Flags);

    [DllImport("SetupAPI.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("SetupAPI.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
        ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("SetupAPI.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize, IntPtr DeviceInfoData);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    const uint DIGCF_PRESENT = 0x02;
    const uint DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;
    const uint OPEN_EXISTING = 3;
    const uint FILE_SHARE_RW = 3;

    static CancellationTokenSource _cts = new();

    static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    static int RelaunchElevated(string[] args)
    {
        string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(" ", args),
            UseShellExecute = true,
            Verb = "runas"
        };
        try
        {
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
            return proc?.ExitCode ?? 1;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("ERROR: Elevation denied. This command requires administrator privileges.");
            return 1;
        }
    }

    static readonly string[] ElevatedCommands = { "emulate", "xbox", "ds5", "cleanup", "setname", "readtest" };

    static int Main(string[] args)
    {
        Console.WriteLine("=== HIDMaestro Test Client ===\n");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _cts.Cancel(); };

        // Single-instance: kill any other HIDMaestroTest processes
        int myPid = Environment.ProcessId;
        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("HIDMaestroTest"))
        {
            if (proc.Id != myPid)
            {
                try { proc.Kill(); proc.WaitForExit(3000); } catch { }
            }
        }

        // Auto-elevate for commands that need admin
        if (args.Length > 0 && ElevatedCommands.Contains(args[0].ToLower()) && !IsElevated())
        {
            Console.WriteLine("  Requesting elevation (admin required)...\n");
            return RelaunchElevated(args);
        }

        if (args.Length == 0)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  HIDMaestroTest emulate <id>      Emulate any controller profile");
            Console.WriteLine("  HIDMaestroTest xbox              Xbox 360 gamepad (hardcoded quick test)");
            Console.WriteLine("  HIDMaestroTest ds5               DualSense gamepad (hardcoded quick test)");
            Console.WriteLine("  HIDMaestroTest list              List all controller profiles");
            Console.WriteLine("  HIDMaestroTest search <query>    Search profiles by name/vendor");
            Console.WriteLine("  HIDMaestroTest info <id>         Show profile details");
            Console.WriteLine("  HIDMaestroTest cleanup           Remove everything");
            Console.WriteLine("\nMust run elevated for config writes + device restart.");
            return 1;
        }

        return args[0].ToLower() switch
        {
            "emulate" => EmulateProfile(args.Length > 1 ? args[1] : ""),
            "xbox"    => TestXbox360(),
            "ds5"     => TestDualSense(),
            "list"    => ListProfiles(),
            "search"  => SearchProfiles(args.Length > 1 ? args[1] : ""),
            "info"    => ShowProfile(args.Length > 1 ? args[1] : ""),
            "cleanup" => RunCleanup(),
            "setname" => SetNameTest(args.Length > 1 ? args[1] : "Controller"),
            "readtest" => ReadTest(),
            "dump"     => DumpControllers(),
            "wgi"      => TestWgi(),
            _         => Error($"Unknown command: {args[0]}")
        };
    }

    static int Error(string msg) { Console.Error.WriteLine($"ERROR: {msg}"); return 1; }

    // ── Descriptor preparation ──

    /// <summary>
    /// The HIDMaestro universal gamepad descriptor: 6 axes (16-bit), 10 buttons,
    /// 1 hat switch, plus Feature Report ID 2 as the data channel.
    /// This descriptor uses explicit Report IDs throughout, so adding the
    /// feature report is always valid. VID/PID/ProductString from the profile
    /// determine the identity; this descriptor determines the report format.
    ///
    /// Games identify controllers by VID/PID and product string — not by
    /// parsing the HID descriptor layout. So we use our proven descriptor
    /// for all profiles and just swap the identity.
    /// </summary>
    // Verified byte-for-byte from driver.h G_DefaultReportDescriptor (116 bytes)
    static readonly byte[] UniversalDescriptor = new byte[] {
        0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0x85, 0x01, 0x09, 0x30, 0x09, 0x31, 0x15, 0x00, 0x27, 0xFF,
        0xFF, 0x00, 0x00, 0x75, 0x10, 0x95, 0x02, 0x81, 0x02, 0x09, 0x33, 0x09, 0x34, 0x81, 0x02, 0x09,
        0x32, 0x09, 0x35, 0x81, 0x02, 0x05, 0x09, 0x19, 0x01, 0x29, 0x0A, 0x15, 0x00, 0x25, 0x01, 0x75,
        0x01, 0x95, 0x0A, 0x81, 0x02, 0x75, 0x06, 0x95, 0x01, 0x81, 0x01, 0x05, 0x01, 0x09, 0x39, 0x15,
        0x01, 0x25, 0x08, 0x35, 0x00, 0x46, 0x3B, 0x01, 0x66, 0x14, 0x00, 0x75, 0x04, 0x95, 0x01, 0x81,
        0x42, 0x75, 0x04, 0x95, 0x01, 0x15, 0x00, 0x25, 0x00, 0x35, 0x00, 0x45, 0x00, 0x65, 0x00, 0x81,
        0x03, 0x85, 0x02, 0x06, 0x00, 0xFF, 0x09, 0x01, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08, 0x95,
        0x0E, 0xB1, 0x02, 0xC0,
    };

    /// <summary>
    /// Adds a vendor Feature Report (ID 0xFE, 64 bytes) to a profile descriptor
    /// so we can inject input via HidD_SetFeature. Only adds if the descriptor
    /// has Report IDs but no existing Feature Report with ID 0xFE.
    /// </summary>
    /// <summary>
    /// Parse HID descriptor to compute input report byte length for Report ID 1.
    /// Returns total bytes including the Report ID byte.
    /// </summary>
    static int ComputeInputReportByteLength(byte[] desc)
    {
        int totalBits = 0;
        int reportSize = 0;
        int reportCount = 0;
        int currentReportId = 0;
        int firstInputReportId = 0;
        bool hasReportIds = false;

        for (int i = 0; i < desc.Length; )
        {
            byte prefix = desc[i];
            int bSize = prefix & 0x03;
            if (bSize == 3) bSize = 4;
            int bType = (prefix >> 2) & 0x03;
            int bTag = (prefix >> 4) & 0x0F;

            int value = 0;
            if (i + bSize < desc.Length)
            {
                for (int j = 0; j < bSize; j++)
                    value |= desc[i + 1 + j] << (8 * j);
            }

            if (bType == 1) // Global
            {
                if (bTag == 7) reportSize = value;    // Report Size
                if (bTag == 9) reportCount = value;   // Report Count
                if (bTag == 8) { currentReportId = value; if (!hasReportIds) firstInputReportId = value; hasReportIds = true; } // Report ID
            }
            else if (bType == 0) // Main
            {
                if (bTag == 8) // Input
                {
                    // Count bits for the first report ID encountered (or all if no IDs)
                    if (!hasReportIds || currentReportId == firstInputReportId)
                        totalBits += reportSize * reportCount;
                }
            }

            i += 1 + bSize;
        }

        int totalBytes = (totalBits + 7) / 8;
        return hasReportIds ? totalBytes + 1 : totalBytes; // +1 for Report ID byte
    }

    /// <summary>
    /// Adds a vendor Output Report (ID 0x02) to inject input data via HidD_SetOutputReport.
    /// Output Reports are NOT enumerated as axes by Chrome's Gamepad API (unlike Feature Reports).
    /// Sized to match the input report data exactly (15 bytes for Xbox).
    /// </summary>
    static byte[] AddOutputReport(byte[] desc)
    {
        // Check if already has our output report
        for (int i = 0; i < desc.Length - 1; i++)
            if (desc[i] == 0x85 && desc[i+1] == 0x02) return desc;

        bool hasIds = false;
        for (int i = 0; i < desc.Length - 1; i++)
            if (desc[i] == 0x85) { hasIds = true; break; }
        if (!hasIds) return desc;

        // Compute input report data size to match our output report
        int inputBytes = ComputeInputReportByteLength(desc);
        int dataBytes = inputBytes > 1 ? inputBytes - 1 : 15; // Subtract Report ID byte

        // Output Report: ID 0x02, exactly inputDataSize vendor bytes
        byte[] output = {
            0x85, 0x02,                             // Report ID (2)
            0x06, 0x00, 0xFF,                       // Usage Page (Vendor)
            0x09, 0x02,                             // Usage (0x02)
            0x15, 0x00, 0x26, 0xFF, 0x00,           // Logical 0-255
            0x75, 0x08,                             // Report Size (8)
            0x95, (byte)dataBytes,                  // Report Count = input data size
            0x91, 0x02                              // OUTPUT (Data, Var, Abs)
        };

        // Insert before the LAST 0xC0
        int lastC0 = desc.Length - 1;
        while (lastC0 >= 0 && desc[lastC0] != 0xC0) lastC0--;
        if (lastC0 < 0) return desc;

        var result = new byte[desc.Length + output.Length];
        Array.Copy(desc, 0, result, 0, lastC0);
        Array.Copy(output, 0, result, lastC0, output.Length);
        result[lastC0 + output.Length] = 0xC0;
        return result;
    }

    static byte[] AddFeatureReport(byte[] desc)
    {
        // Check if already has our feature report (ID 0x02)
        for (int i = 0; i < desc.Length - 1; i++)
            if (desc[i] == 0x85 && desc[i+1] == 0x02) return desc;

        // Check if descriptor has Report IDs (required — can't mix ID/no-ID)
        bool hasIds = false;
        for (int i = 0; i < desc.Length - 1; i++)
            if (desc[i] == 0x85) { hasIds = true; break; }
        if (!hasIds) return desc; // Can't add Feature Report to no-ID descriptor

        // Use an Output Report (not Feature Report) as the data channel.
        // Feature Reports create phantom axes in Chrome's Gamepad API.
        // Output Report ID 0x02 with vendor data — Chrome ignores Output items.
        byte[] feature = {
            0x85, 0x02, 0x06, 0x00, 0xFF, 0x09, 0x01,
            0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08,
            0x95, 0x40, 0x91, 0x02  // 0x91 = Output (not 0xB1 = Feature)
        };

        // Insert before the LAST 0xC0 (outermost End Collection)
        int lastC0 = desc.Length - 1;
        while (lastC0 >= 0 && desc[lastC0] != 0xC0) lastC0--;
        if (lastC0 < 0) return desc;

        var result = new byte[desc.Length + feature.Length];
        Array.Copy(desc, 0, result, 0, lastC0);
        Array.Copy(feature, 0, result, lastC0, feature.Length);
        result[lastC0 + feature.Length] = 0xC0; // Re-add End Collection
        return result;
    }

    // ── Registry config ──

    static void WriteConfig(byte[] descriptor, ushort vid, ushort pid, ushort ver = 0x0100, string? productString = null, string? deviceDescription = null, int inputReportByteLength = 0)
    {
        using var key = Registry.LocalMachine.CreateSubKey(REG_PATH);
        // Debug: log what we're writing
        Console.Write($"  [descriptor: {descriptor.Length}B, bytes[15]=0x{descriptor[15]:X2}, bytes[16]=0x{descriptor[16]:X2}] ");
        key.SetValue("ReportDescriptor", descriptor, RegistryValueKind.Binary);
        key.SetValue("VendorId", (int)vid, RegistryValueKind.DWord);
        key.SetValue("ProductId", (int)pid, RegistryValueKind.DWord);
        key.SetValue("VersionNumber", (int)ver, RegistryValueKind.DWord);
        if (productString != null)
            key.SetValue("ProductString", productString, RegistryValueKind.String);
        if (inputReportByteLength > 0)
            key.SetValue("InputReportByteLength", inputReportByteLength, RegistryValueKind.DWord);
        // DeviceDescription = what Device Manager / FriendlyName shows
        string displayName = deviceDescription ?? productString ?? "HIDMaestro Controller";
        key.SetValue("DeviceDescription", displayName, RegistryValueKind.String);

        // Write OEMName to the Joystick\OEM registry — joy.cpl reads from here.
        // This is indexed by VID/PID, so a real controller reconnecting will overwrite.
        string oemKey = $@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{vid:X4}&PID_{pid:X4}";
        using var oemReg = Registry.CurrentUser.CreateSubKey(oemKey);
        oemReg?.SetValue("OEMName", displayName, RegistryValueKind.String);
    }

    // ── Process runner ──

    static readonly string RepoRoot = @"C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro";
    static readonly string ScriptsDir = Path.Combine(RepoRoot, "scripts");
    static readonly string BuildDir = Path.Combine(RepoRoot, "build");

    static (int exitCode, string output) RunProcess(string fileName, string args, int timeoutMs = 30_000, bool showOutput = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName, Arguments = args,
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(timeoutMs);
        string combined = stdout + stderr;
        if (showOutput && !string.IsNullOrWhiteSpace(combined))
            Console.WriteLine(combined.TrimEnd());
        return (proc.ExitCode, combined);
    }

    static void RunPowerShell(string script, bool showOutput = false)
    {
        RunProcess("powershell.exe",
            $"-ExecutionPolicy Bypass -File \"{Path.Combine(ScriptsDir, script)}\"",
            showOutput: showOutput);
    }

    // ── Full build + sign + install pipeline ──

    // ── Device Setup (all-in-code, no external scripts) ──

    /// <summary>
    /// Ensures GameInputSvc is running and set to Automatic start.
    /// Required for WGI to discover our device (standard browser mapping).
    /// </summary>
    static void EnsureGameInputService()
    {
        try
        {
            var (_, status) = RunProcess("sc.exe", "query GameInputSvc");
            if (!status.Contains("RUNNING"))
            {
                RunProcess("sc.exe", "config GameInputSvc start= auto");
                RunProcess("sc.exe", "start GameInputSvc");
            }
        }
        catch { /* Service may not exist on older Windows */ }
    }

    /// <summary>
    /// Writes the GameInput registry for WGI Gamepad promotion.
    /// Key format: HKLM\...\GameInput\Devices\{VID4}{PID4}00010005\Gamepad
    /// Axis/button indices match WGI RawGameController enumeration order.
    /// </summary>
    static void WriteGameInputRegistry(ushort vid, ushort pid, ControllerProfile? profile = null)
    {
        string deviceKey = $@"SYSTEM\CurrentControlSet\Control\GameInput\Devices\{vid:X4}{pid:X4}00010005";
        // Delete existing key to ensure clean state
        try { Registry.LocalMachine.DeleteSubKeyTree(deviceKey, false); } catch { }
        using var root = Registry.LocalMachine.CreateSubKey(deviceKey);

        string gpPath = $@"{deviceKey}\Gamepad";
        string[] subs = { "Menu","View","A","B","X","Y","LeftShoulder","RightShoulder",
            "LeftThumbstickButton","RightThumbstickButton",
            "DPadUp","DPadDown","DPadLeft","DPadRight",
            "LeftTrigger","RightTrigger",
            "LeftThumbstickX","LeftThumbstickY","RightThumbstickX","RightThumbstickY" };
        foreach (var sub in subs)
            Registry.LocalMachine.CreateSubKey($@"{gpPath}\{sub}");

        void SetAxis(string name, int index, bool invert = false)
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{gpPath}\{name}", true)!;
            k.SetValue("AxisIndex", index, RegistryValueKind.DWord);
            if (invert) k.SetValue("Invert", 1, RegistryValueKind.DWord);
        }
        void SetButton(string name, int index)
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{gpPath}\{name}", true)!;
            k.SetValue("ButtonIndex", index, RegistryValueKind.DWord);
        }
        void SetDPad(string name, string position)
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{gpPath}\{name}", true)!;
            k.SetValue("SwitchIndex", 0, RegistryValueKind.DWord);
            k.SetValue("SwitchPosition", position, RegistryValueKind.String);
            k.SetValue("IncludeAdjacent", 1, RegistryValueKind.DWord);
        }

        bool combinedTriggers = profile?.HasCombinedTriggers == true;

        // Axes: WGI RawGameController order matches descriptor axis order
        SetAxis("LeftThumbstickX", 0);
        SetAxis("LeftThumbstickY", 1, invert: true);
        SetAxis("RightThumbstickX", 2);
        SetAxis("RightThumbstickY", 3, invert: true);

        if (combinedTriggers)
        {
            // Xbox 360: 5 axes (LX, LY, RX, RY, Z-combined). Map both triggers to axis 4.
            SetAxis("LeftTrigger", 4);
            SetAxis("RightTrigger", 4);
        }
        else
        {
            // GIP/separate: 6 axes (LX, LY, RX, RY, LT, RT)
            SetAxis("LeftTrigger", 4);
            SetAxis("RightTrigger", 5);
        }

        // Buttons
        SetButton("A", 0); SetButton("B", 1); SetButton("X", 2); SetButton("Y", 3);
        SetButton("LeftShoulder", 4); SetButton("RightShoulder", 5);
        SetButton("View", 6); SetButton("Menu", 7);
        SetButton("LeftThumbstickButton", 8); SetButton("RightThumbstickButton", 9);

        // DPad from hat switch
        SetDPad("DPadUp", "Up"); SetDPad("DPadDown", "Down");
        SetDPad("DPadLeft", "Left"); SetDPad("DPadRight", "Right");
    }

    /// <summary>
    /// Ensures the shared file exists with open ACLs for the driver timer.
    /// </summary>
    static void EnsureSharedFile()
    {
        string dir = @"C:\ProgramData\HIDMaestro";
        Directory.CreateDirectory(dir);
        string filePath = Path.Combine(dir, "input.bin");
        if (!File.Exists(filePath))
        {
            File.WriteAllBytes(filePath, new byte[72]);
        }
        // Grant Everyone full access so WUDFHost (LocalService) can read
        RunProcess("icacls.exe", $"\"{dir}\" /grant Everyone:(OI)(CI)F /T", timeoutMs: 5000);
    }

    /// <summary>
    /// Removes ghost HID_IG_00 devices that are in Error state.
    /// Does NOT remove working devices.
    /// </summary>
    static void CleanupGhostDevices()
    {
        // Remove ALL HIDMaestro devices (any enumerator, any state, any PID).
        // This prevents stale devices from previous profiles contaminating the current one.
        // NEVER touches ROOT\SYSTEM (ViGEmBus, HidHide).
        string[] prefixes = {
            @"ROOT\VID_", @"ROOT\HID_IG_00", @"ROOT\HIDCLASS",
            @"ROOT\XnaComposite"
        };
        foreach (var prefix in prefixes)
        {
            for (int idx = 0; idx < 10; idx++)
            {
                string instId = $@"{prefix}\{idx:D4}";
                if (prefix.StartsWith(@"ROOT\VID_"))
                {
                    // VID_ prefix: scan for any VID pattern
                    // Use CM_Locate to check existence
                    break; // handled below
                }
                if (CM_Locate_DevNodeW(out uint _, instId, 0) == 0)
                {
                    RunProcess("pnputil.exe", $"/remove-device \"{instId}\" /subtree", timeoutMs: 5000);
                }
            }
        }
        // VID_*&IG_00 and VID_*&PID_* patterns — scan registry for ROOT\VID_ enumerators
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT");
            if (enumKey != null)
            {
                foreach (var subName in enumKey.GetSubKeyNames())
                {
                    if (subName.StartsWith("VID_", StringComparison.OrdinalIgnoreCase))
                    {
                        // Found a VID_ enumerator — remove all device instances under it
                        using var vidKey = enumKey.OpenSubKey(subName);
                        if (vidKey != null)
                        {
                            foreach (var instName in vidKey.GetSubKeyNames())
                            {
                                string instId = $@"ROOT\{subName}\{instName}";
                                RunProcess("pnputil.exe", $"/remove-device \"{instId}\" /subtree", timeoutMs: 5000);
                            }
                        }
                    }
                }
            }
        }
        catch { }

        // Clean ghost XUSB interface registrations only.
        // XUSB ghosts cause dead XInput slots. WinExInput must be PRESERVED
        // for browser STANDARD GAMEPAD detection (xinputhid registers it).
        string[] interfaceGuids = {
            @"{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}", // XUSB only
        };
        foreach (var guid in interfaceGuids)
        {
            try
            {
                using var classKey = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Control\DeviceClasses\{guid}", writable: true);
                if (classKey == null) continue;
                foreach (var subName in classKey.GetSubKeyNames())
                {
                    // Clean our devices but NEVER ViGEmBus/HidHide (ROOT#SYSTEM#0000, #0001)
                    bool isOurDevice = subName.Contains("ROOT#VID_") || subName.Contains("ROOT#HIDCLASS") ||
                        subName.Contains("ROOT#HID_IG");
                    // For ROOT#SYSTEM, only clean entries beyond index 1 (0000=ViGEmBus, 0001=HidHide)
                    if (!isOurDevice && subName.Contains("ROOT#SYSTEM#"))
                    {
                        // Check if it's index >= 2
                        var match = System.Text.RegularExpressions.Regex.Match(subName, @"ROOT#SYSTEM#(\d+)");
                        if (match.Success && int.Parse(match.Groups[1].Value) >= 2)
                            isOurDevice = true;
                    }
                    if (isOurDevice)
                    {
                        try { classKey.DeleteSubKeyTree(subName); } catch { }
                    }
                }
            }
            catch { }
        }
    }

    // NOTE: NEVER kill WUDFHost processes. Killing WUDFHost breaks real BT controllers
    // (Code 43 error). Use pnputil /remove-device + recreate instead.

    /// <summary>
    /// Overrides xinputhid's device name on all HID child devices.
    /// xinputhid sets names like "Xbox Wireless Controller" from its INF.
    /// We override with the profile's deviceDescription via direct registry write.
    /// </summary>
    static void FixHidChildNames(string name)
    {
        // Fix device names on ALL HIDMaestro devices (HID_IG_00 and HIDCLASS)
        RunProcess("powershell.exe",
            $"-Command \"Get-PnpDevice -Class HIDClass -Status OK -EA SilentlyContinue | Where-Object {{ $_.InstanceId -match 'HID_IG_00|HIDCLASS' -and $_.InstanceId -match 'ROOT|HID' }} | ForEach-Object {{ $p = 'HKLM:\\SYSTEM\\CurrentControlSet\\Enum\\' + $_.InstanceId; Set-ItemProperty $p -Name FriendlyName -Value '{name}' -Type String -Force -EA SilentlyContinue; Set-ItemProperty $p -Name DeviceDesc -Value '{name}' -Type String -Force -EA SilentlyContinue }}\"",
            timeoutMs: 10_000);
    }

    /// <summary>
    /// Creates a device node with the correct enumerator and hardware ID.
    /// xinputhid profiles use HID_IG_00 enumerator (triggers xinputhid upper filter).
    /// Direct HID profiles use HIDClass enumerator (standard HID access).
    /// </summary>
    static bool CreateDeviceNode(ControllerProfile profile, string infPath)
    {
        string vid = $"{profile.VendorId:X4}";
        string pid = $"{profile.ProductId:X4}";
        // driverPid: use alternate PID in hardware ID for driver matching (e.g. xinputhid).
        // Apps still see the real PID via HID attributes.
        string hwPid = profile.DriverPid != null
            ? $"{Convert.ToUInt16(profile.DriverPid, 16):X4}" : pid;
        string desc = profile.ProductString;
        string enumerator, hwId;
        Guid classGuid;
        if (profile.UsesUpperFilter)
        {
            // Enumerator must contain &IG_ so the HID child's device path
            // includes "&IG_" — HIDAPI skips devices with "&IG_" in the path,
            // preventing SDL3 from seeing the main device (companion handles SDL3).
            enumerator = $"VID_{vid}&PID_{hwPid}&IG_00";
            hwId = $"root\\VID_{vid}&PID_{hwPid}&IG_00";
            classGuid = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da"); // HIDClass
        }
        else if (profile.VendorId == 0x045E)
        {
            // Xbox without upper filter: use IG_00 so Chrome filters from RawInput
            enumerator = "HID_IG_00";
            hwId = $"root\\VID_{vid}&PID_{pid}&IG_00";
            classGuid = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da"); // HIDClass
        }
        else
        {
            // Standard HID device (non-Xbox, no upper filter)
            enumerator = "HIDClass";
            hwId = $"root\\VID_{vid}&PID_{pid}";
            classGuid = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da"); // HIDClass
        }
        string hwMulti = $"{hwId}\0root\\HIDMaestro\0\0";
        IntPtr dis = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (dis == new IntPtr(-1)) return false;

        try
        {
            var devInfo = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            // Reuse SP_DEVICE_INTERFACE_DATA layout for DEVINFO_DATA (same first field)
            byte[] devInfoBuf = new byte[32]; // SP_DEVINFO_DATA
            int devInfoSize = IntPtr.Size == 8 ? 32 : 28;
            BitConverter.GetBytes(devInfoSize).CopyTo(devInfoBuf, 0);
            var devInfoHandle = System.Runtime.InteropServices.GCHandle.Alloc(devInfoBuf, GCHandleType.Pinned);

            if (!SetupDiCreateDeviceInfoW_Raw(dis, enumerator, ref classGuid, desc, IntPtr.Zero, 1, devInfoHandle.AddrOfPinnedObject()))
            {
                devInfoHandle.Free();
                return false;
            }

            byte[] hwBytes = Encoding.Unicode.GetBytes(hwMulti);
            if (!SetupDiSetDeviceRegistryPropertyW_Raw(dis, devInfoHandle.AddrOfPinnedObject(), 1, hwBytes, (uint)hwBytes.Length))
            {
                devInfoHandle.Free();
                return false;
            }

            // Set compatible IDs — USB\MS_COMP_XUSB10 helps WGI identify Xbox controllers
            if (!profile.UsesUpperFilter && profile.VendorId == 0x045E)
            {
                string compatMulti = "USB\\MS_COMP_XUSB10\0USB\\Class_FF&SubClass_5D&Prot_01\0USB\\Class_FF&SubClass_5D\0\0";
                byte[] compatBytes = Encoding.Unicode.GetBytes(compatMulti);
                SetupDiSetDeviceRegistryPropertyW_Raw(dis, devInfoHandle.AddrOfPinnedObject(), 2, compatBytes, (uint)compatBytes.Length);
            }

            // DIF_REGISTERDEVICE = 0x19
            if (!SetupDiCallClassInstaller_Raw(0x19, dis, devInfoHandle.AddrOfPinnedObject()))
            {
                devInfoHandle.Free();
                return false;
            }

            devInfoHandle.Free();

            // Install driver via UpdateDriverForPlugAndPlayDevicesW
            string driverHwId = hwId;
            UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, driverHwId, infPath, 0, out bool _);

            // XnaComposite devices need an explicit restart to load the driver
            if (!profile.UsesUpperFilter)
            {
                string instId = $@"ROOT\XNACOMPOSITE\0000";
                RunProcess("pnputil.exe", $"/restart-device \"{instId}\"");
            }

            // Wait for device to enumerate
            Thread.Sleep(3000);

            // Fix name after xinputhid loads
            string displayName = profile.DeviceDescription ?? profile.ProductString;
            FixHidChildNames(displayName);

            return true;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(dis);
        }
    }

    [DllImport("SetupAPI.dll", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);

    [DllImport("SetupAPI.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiCreateDeviceInfoW")]
    static extern bool SetupDiCreateDeviceInfoW_Raw(IntPtr DeviceInfoSet, string DeviceName,
        ref Guid ClassGuid, string DeviceDescription, IntPtr hwndParent, int CreationFlags, IntPtr DeviceInfoData);

    [DllImport("SetupAPI.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiSetDeviceRegistryPropertyW")]
    static extern bool SetupDiSetDeviceRegistryPropertyW_Raw(IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
        int Property, byte[] PropertyBuffer, uint PropertyBufferSize);

    [DllImport("SetupAPI.dll", SetLastError = true, EntryPoint = "SetupDiCallClassInstaller")]
    static extern bool SetupDiCallClassInstaller_Raw(int InstallFunction, IntPtr DeviceInfoSet, IntPtr DeviceInfoData);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string HardwareId,
        string FullInfPath, int InstallFlags, out bool RebootRequired);

    static bool IsDriverInStore()
    {
        var (_, output) = RunProcess("pnputil.exe", "/enum-drivers");
        return output.Contains("hidmaestro.inf");
    }

    static bool EnsureDriverInstalled(ControllerProfile? profile = null)
    {
        string dllPath = Path.Combine(BuildDir, "HIDMaestro.dll");
        string driverSource = Path.Combine(RepoRoot, "driver", "driver.c");
        string driverHeader = Path.Combine(RepoRoot, "driver", "driver.h");

        bool needsBuild = !File.Exists(dllPath) ||
            File.GetLastWriteTime(driverSource) > File.GetLastWriteTime(dllPath) ||
            File.GetLastWriteTime(driverHeader) > File.GetLastWriteTime(dllPath);

        bool driverInstalled = IsDriverInStore();

        if (needsBuild || !driverInstalled)
        {
            // Full deploy: build + sign + install + create node
            string skipArg = needsBuild ? "" : "-SkipBuild";
            var (_, output) = RunProcess("powershell.exe",
                $"-ExecutionPolicy Bypass -File \"{Path.Combine(ScriptsDir, "full_deploy.ps1")}\" {skipArg}",
                timeoutMs: 120_000, showOutput: true);
            return output.Contains("Deploy Complete");
        }

        // Driver is installed — restart device to pick up new registry config.
        // Find existing device — only restart if needed
        Console.Write("  Checking device... ");
        string? rootInstId = null;
        // Search enumerator types (skip HIDClass — gamepad companion lives there)
        // Include VID_*&IG_00 enumerators for xinputhid profiles
        foreach (string enumer in new[] { "HID_IG_00", "XnaComposite",
            "VID_045E&PID_02FF&IG_00", "VID_045E&PID_0B13&IG_00" })
        {
            for (int idx = 0; idx < 10; idx++)
            {
                string candidate = $@"ROOT\{enumer}\{idx:D4}";
                if (CM_Locate_DevNodeW(out uint _, candidate, 0) == 0)
                { rootInstId = candidate; break; }
            }
            if (rootInstId != null) break;
        }
        string infPath = Path.Combine(BuildDir, "hidmaestro.inf");
        if (rootInstId != null)
        {
            // Remove and recreate to force fresh WUDFHost with latest DLL
            RunProcess("pnputil.exe", $"/remove-device \"{rootInstId}\" /subtree");
            Thread.Sleep(2000);
            if (profile != null)
                CreateDeviceNode(profile, infPath);
            else
                RunPowerShell("create_node.ps1"); // fallback
            Thread.Sleep(3000);
            Console.WriteLine("OK");
        }
        else
        {
            // Device doesn't exist — create it
            Console.Write("creating... ");
            if (profile != null)
                CreateDeviceNode(profile, infPath);
            else
                RunPowerShell("create_node.ps1"); // fallback
            Thread.Sleep(3000);
            Console.WriteLine("OK");
        }

        // Set device name on root AND HID child
        Console.Write("  Setting device name... ");
        string dispName = (string?)Registry.LocalMachine.OpenSubKey(REG_PATH)?.GetValue("DeviceDescription") ?? "Controller";
        if (rootInstId != null)
        {
            SetBusReportedDeviceDesc(rootInstId, dispName);
            SetDeviceFriendlyName(rootInstId, dispName);
        }
        // Find and rename the HID child (xinputhid sets it to "Xbox Wireless Controller")
        if (CM_Locate_DevNodeW(out uint rootInst, @"ROOT\HID_IG_00\0000", 0) == 0)
        {
            if (CM_Get_Child(out uint childInst, rootInst, 0) == 0)
            {
                // Walk children to find the HID child
                uint inst = childInst;
                do
                {
                    // Set name on every child
                    var friendlyKey = new DEVPROPKEY
                    {
                        fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
                        pid = 14
                    };
                    var busDescKey = new DEVPROPKEY
                    {
                        fmtid = new Guid(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2),
                        pid = 4
                    };
                    byte[] nameBytes = Encoding.Unicode.GetBytes(dispName + "\0");
                    CM_Set_DevNode_PropertyW(inst, ref friendlyKey, 0x12, nameBytes, (uint)nameBytes.Length, 0);
                    CM_Set_DevNode_PropertyW(inst, ref busDescKey, 0x12, nameBytes, (uint)nameBytes.Length, 0);
                } while (CM_Get_Sibling(out inst, inst, 0) == 0);
            }
        }
        Console.WriteLine("OK");

        return true;
    }

    // ── Device restart (remove + recreate) ──

    static void RestartDevice()
    {
        Console.Write("  Restarting device node... ");

        // Remove existing nodes (but NOT HIDCLASS — gamepad companion lives there)
        // RunProcess("pnputil.exe", "/remove-device \"ROOT\\HIDCLASS\\0000\" /subtree");
        // RunProcess("pnputil.exe", "/remove-device \"ROOT\\HIDCLASS\\0001\" /subtree");
        Thread.Sleep(1000);

        // Recreate via the create_node.ps1 script
        RunPowerShell("create_node.ps1");

        Console.WriteLine("OK");
        Thread.Sleep(3000); // Wait for HID child to enumerate
    }

    // ── Find and open HID child device ──

    static SafeFileHandle? OpenHidDevice(ushort targetVid, ushort targetPid)
    {
        HidD_GetHidGuid(out Guid hidGuid);

        IntPtr dis = SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (dis == new IntPtr(-1)) return null;

        try
        {
            for (uint idx = 0; idx < 128; idx++)
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(dis, IntPtr.Zero, ref hidGuid, idx, ref did))
                    break;

                SetupDiGetDeviceInterfaceDetailW(dis, ref did, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);

                IntPtr detail = Marshal.AllocHGlobal((int)reqSize);
                try
                {
                    Marshal.WriteInt32(detail, 8);
                    if (!SetupDiGetDeviceInterfaceDetailW(dis, ref did, detail, reqSize, out _, IntPtr.Zero))
                        continue;

                    string path = Marshal.PtrToStringUni(detail + 4)!;

                    var handle = CreateFileW(path, GENERIC_READ | GENERIC_WRITE,
                        FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (handle.IsInvalid) continue;

                    var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (HidD_GetAttributes(handle, ref attrs))
                    {
                        // Prefer our HIDMaestro device (HIDCLASS or IG_ in path) over real hardware
                        bool isOurDevice = path.Contains("HIDCLASS", StringComparison.OrdinalIgnoreCase)
                            || (path.Contains("IG_", StringComparison.OrdinalIgnoreCase)
                                && path.Contains("root", StringComparison.OrdinalIgnoreCase));
                        if (isOurDevice)
                        {
                            Console.WriteLine($"  Found: VID={attrs.VendorID:X4} PID={attrs.ProductID:X4} @ {path}");
                            return handle;
                        }
                        if (attrs.VendorID == targetVid && attrs.ProductID == targetPid)
                        {
                            // Real hardware — skip
                            handle.Dispose();
                            continue;
                        }
                    }

                    handle.Dispose();
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(dis);
        }

        return null;
    }

    // ── Emulate any profile ──

    static int EmulateProfile(string profileId)
    {
        if (string.IsNullOrEmpty(profileId))
            return Error("Usage: HIDMaestroTest emulate <profile-id>\n  Use 'list' to see available profiles.");

        var db = ProfileDatabase.Load(GetProfilesDir());
        var profile = db.GetById(profileId);
        if (profile == null)
        {
            // Try fuzzy search
            var matches = db.Search(profileId).ToList();
            if (matches.Count == 1)
                profile = matches[0];
            else if (matches.Count > 1)
            {
                Console.Error.WriteLine($"Multiple matches for '{profileId}':");
                foreach (var m in matches.Take(10))
                    Console.Error.WriteLine($"  {m.Id,-35} {m.Name}");
                return 1;
            }
            else
                return Error($"Profile '{profileId}' not found. Use 'list' to see profiles.");
        }

        if (!profile.HasDescriptor)
            return Error($"Profile '{profile.Id}' has no HID descriptor. Cannot emulate.\n  This profile needs a descriptor captured from physical hardware.");

        Console.WriteLine($"-- Emulating: {profile.Name} --\n");
        Console.WriteLine($"  Profile:  {profile.Id}");
        Console.WriteLine($"  VID:PID:  0x{profile.VendorId:X4}:0x{profile.ProductId:X4}");
        Console.WriteLine($"  Product:  {profile.ProductString}");
        // Use profile descriptor if it has Report IDs (browser-compatible).
        // Descriptors without Report IDs (like GIP 262-byte) cause HID validation failure.
        byte[] descriptor;
        bool useNativeHID = false;
        byte[]? profileDesc = profile.HasDescriptor ? profile.GetDescriptorBytes() : null;
        bool descHasReportIds = false;
        if (profileDesc != null)
            for (int i = 0; i < profileDesc.Length - 1; i++)
                if (profileDesc[i] == 0x85) { descHasReportIds = true; break; }

        if (profileDesc != null)
        {
            // Use the profile descriptor as-is. Data injection uses shared file,
            // so no output/feature report injection needed.
            descriptor = profileDesc;
            useNativeHID = true;
            Console.WriteLine($"  Descriptor: {descriptor.Length} bytes (native)\n");
        }
        else
        {
            descriptor = UniversalDescriptor;
            Console.WriteLine($"  Descriptor: {descriptor.Length} bytes (universal)\n");
        }

        // Step 0: Pre-flight setup (all in code, no external scripts)
        Console.Write("  Setting up environment... ");
        _upperFilterName = profile.UpperFilterName ?? "none";
        EnsureGameInputService();
        EnsureSharedFile();
        CleanupGhostDevices();
        // Also clean XUSB companions (only HIDMaestro, NEVER ViGEmBus/HidHide)
        for (int idx = 0; idx < 10; idx++)
        {
            string sysId = $@"ROOT\SYSTEM\{idx:D4}";
            if (CM_Locate_DevNodeW(out uint _, sysId, 0) == 0)
            {
                // Check device identity via registry
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{sysId}");
                    if (key != null)
                    {
                        string? fn = key.GetValue("FriendlyName") as string;
                        string? dd = key.GetValue("DeviceDesc") as string;
                        string?[] hwIds = key.GetValue("HardwareID") as string[] ?? Array.Empty<string>();
                        bool isOurs = (fn != null && fn.Contains("HIDMaestro")) ||
                                      (dd != null && dd.Contains("HIDMaestro")) ||
                                      hwIds.Any(h => h != null && h.Contains("HIDMaestro"));
                        if (isOurs)
                            RunProcess("pnputil.exe", $"/remove-device \"{sysId}\"", timeoutMs: 5000);
                    }
                }
                catch { }
            }
        }
        // GameInput registry sets the Gamepad flag so SDL3 uses GetGamepadState (live data)
        // instead of GetControllerAxisState (zeros). Security fix (GA for Everyone) enables
        // non-elevated GameInput reading.
        WriteGameInputRegistry(profile.VendorId, profile.ProductId, profile);
        Console.WriteLine("OK");

        // Step 1: Write descriptor to registry
        int inputReportLen = (profile.InputReportSize ?? 0) > 0
            ? profile.InputReportSize!.Value
            : ComputeInputReportByteLength(descriptor);
        Console.Write($"  Writing profile to registry (InputReport={inputReportLen}B)... ");
        // For xinputhid profiles, set HID attributes VID/PID to 0 to prevent
        // GameInput from claiming as Gamepad. SDL3 falls through to XInput.
        // Real identity comes from hardware ID, OEMName, and WinExInput companion.
        // Use a non-gamepad Microsoft VID/PID in HID attributes to prevent GameInput
        // from claiming as Gamepad while keeping xinputhid working.
        // xinputhid checks hardware ID (PID 02FF), not HID attributes.
        // 045E:0001 = Microsoft mouse (not a gamepad in GameInput's database)
        // PID 0001 for xinputhid profiles: prevents RawInput zero-data entry.
        // xinputhid blocks RawInput ReadFile (by design for Xbox controllers).
        // PID 0001 isn't in SDL3's controller DB → RawInput entry is generic.
        // XInput provides live data as separate device.
        // TODO: Identity fix requires kernel bus driver for real transport.
        // Write BLE descriptor + real VID/PID (companion reads this at startup)
        // BLE descriptor has Report ID 0x01 which HIDAPI can parse correctly
        // Companion descriptor: use the profile's native descriptor directly.
        // For xinputhid profiles, prepend Report ID 0x01 to make mshidumdf happy.
        // The companion is hidden from HIDAPI (&IG_), so SDL3 sees it through
        // RawInput/Windows backend which maps by VID/PID, not descriptor parsing.
        // DirectInput reads this descriptor directly — must match real hardware
        // (correct axis count, button count, trigger mode).
        // Companion descriptor: inject Report ID 0x01 into the profile's native descriptor.
        // This preserves the profile's exact axis count, button count, and trigger mode.
        // The companion is hidden from Chrome RawInput (&IG_), so Chrome uses XInput
        // for triggers (separate LT/RT). DirectInput sees the native layout.
        byte[] bleDesc;
        int bleReportLen;
        if (profile.UsesUpperFilter)
        {
            var descList = new List<byte>(descriptor);
            // Insert "85 01" (Report ID 1) after the first Application Collection (A1 01)
            for (int di = 0; di < descList.Count - 1; di++)
            {
                if (descList[di] == 0xA1 && descList[di + 1] == 0x01)
                {
                    descList.Insert(di + 2, 0x01);
                    descList.Insert(di + 2, 0x85);
                    break;
                }
            }
            bleDesc = descList.ToArray();
            bleReportLen = inputReportLen + 1; // +1 for Report ID byte
        }
        else
        {
            bleDesc = descriptor;
            bleReportLen = inputReportLen;
        }
        WriteConfig(bleDesc, profile.VendorId, profile.ProductId,
            productString: profile.ProductString,
            deviceDescription: profile.DeviceDescription,
            inputReportByteLength: bleReportLen);
        Console.WriteLine("OK");

        // Step 1.5: Create Gamepad companion with BLE descriptor (HIDAPI-compatible)
        // Companion reads VID/PID + descriptor from registry at startup.
        // Write BLE descriptor first, create companion, then overwrite for main device.
        if (profile.UsesUpperFilter)
        {
            Console.Write("  Creating Gamepad companion... ");
            bool gpExists = false;
            string gpVidSearch = $"{profile.VendorId:X4}";
            string gpPidSearch = $"{profile.ProductId:X4}";
            // Search both old (HIDCLASS) and new (VID_*&IG_00) enumerators
            foreach (string pfx in new[] { "HIDCLASS", $"VID_{gpVidSearch}&PID_{gpPidSearch}&IG_00" })
            {
                for (int idx = 0; idx < 10; idx++)
                {
                    string candidate = $@"ROOT\{pfx}\{idx:D4}";
                    if (CM_Locate_DevNodeW(out uint _, candidate, 0) == 0)
                    {
                        var (_, info) = RunProcess("pnputil.exe", $"/enum-devices /instanceid \"{candidate}\"");
                        if (info.Contains("Gamepad") || info.Contains("HIDMaestroGamepad"))
                        { gpExists = true; break; }
                    }
                }
                if (gpExists) break;
            }
            if (!gpExists)
            {
                var hidGuid = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
                IntPtr dis3 = SetupDiCreateDeviceInfoList(ref hidGuid, IntPtr.Zero);
                if (dis3 != new IntPtr(-1))
                {
                    byte[] diBuf3 = new byte[32];
                    BitConverter.GetBytes(IntPtr.Size == 8 ? 32 : 28).CopyTo(diBuf3, 0);
                    var diHandle3 = System.Runtime.InteropServices.GCHandle.Alloc(diBuf3, GCHandleType.Pinned);
                    string gpVid = $"{profile.VendorId:X4}";
                    string gpPid = $"{profile.ProductId:X4}";
                    // Use driverPid (02FF) in enumerator so xinputhid matches and provides
                    // live XInput data. xinputhid's XInput→DI mapping suppresses raw HID → 5 axes.
                    // Real PID comes from HID attributes (set by WriteConfig).
                    string hwPid = profile.DriverPid != null
                        ? $"{Convert.ToUInt16(profile.DriverPid, 16):X4}" : gpPid;
                    string gpEnumerator = $"VID_{gpVid}&PID_{hwPid}&IG_00";
                    string gpHw = $"root\\VID_{gpVid}&PID_{hwPid}&IG_00\0root\\HIDMaestroGamepad\0root\\HIDMaestro\0\0";
                    byte[] gpHwBytes = Encoding.Unicode.GetBytes(gpHw);
                    if (SetupDiCreateDeviceInfoW_Raw(dis3, gpEnumerator, ref hidGuid,
                        "HIDMaestro Gamepad", IntPtr.Zero, 1, diHandle3.AddrOfPinnedObject()))
                    {
                        SetupDiSetDeviceRegistryPropertyW_Raw(dis3, diHandle3.AddrOfPinnedObject(), 1, gpHwBytes, (uint)gpHwBytes.Length);
                        // Set CompatibleIDs — only add BTHLEDEVICE for Bluetooth profiles.
                        // USB profiles should NOT spoof BT (causes SDL3 misidentification).
                        string gpCompatBase = $"root\\HIDMaestroGamepad\0root\\HIDMaestro\0\0";
                        if (profile.Connection == "bluetooth")
                            gpCompatBase = $"BTHLEDEVICE\\{{00001812-0000-1000-8000-00805f9b34fb}}_Dev_VID&02{gpVid}_PID&{gpPid}\0" + gpCompatBase;
                        byte[] gpCompatBytes = Encoding.Unicode.GetBytes(gpCompatBase);
                        SetupDiSetDeviceRegistryPropertyW_Raw(dis3, diHandle3.AddrOfPinnedObject(), 2, gpCompatBytes, (uint)gpCompatBytes.Length);
                        SetupDiCallClassInstaller_Raw(0x19, dis3, diHandle3.AddrOfPinnedObject());
                    }
                    diHandle3.Free();
                    SetupDiDestroyDeviceInfoList(dis3);
                }
            }
            // Restart companion to load with real VID/PID
            string hwPidSearch = profile.DriverPid != null
                ? $"{Convert.ToUInt16(profile.DriverPid, 16):X4}" : gpPidSearch;
            foreach (string pfx2 in new[] { $"VID_{gpVidSearch}&PID_{hwPidSearch}&IG_00", $"VID_{gpVidSearch}&PID_{gpPidSearch}&IG_00", "HIDCLASS" })
            {
                bool found = false;
                for (int idx = 0; idx < 10; idx++)
                {
                    string candidate = $@"ROOT\{pfx2}\{idx:D4}";
                    if (CM_Locate_DevNodeW(out uint _, candidate, 0) == 0)
                    {
                        RunProcess("pnputil.exe", $"/restart-device \"{candidate}\"", timeoutMs: 5000);
                        Thread.Sleep(2000);
                        found = true; break;
                    }
                }
                if (found) break;
            }
            Console.WriteLine("OK");

            // Registry keeps real VID/PID (companion reads it)
        }

        // Step 2: Build, sign, install driver + create main device node
        // For xinputhid profiles: companion-only approach.
        // Companion provides SDL3 + DirectInput + browser.
        // XInput comes from XUSB companion (separate device).
        // No main device with xinputhid — it blocks DirectInput data flow for virtual devices.
        if (!profile.UsesUpperFilter)
        {
            if (!EnsureDriverInstalled(profile))
                return Error("Driver build/install failed. Run elevated.");
        }
        else
        {
            Console.WriteLine("  Companion-only mode (no xinputhid main device)");
        }

        // Step 3: Wait for HID child + xinputhid, then fix device name
        Thread.Sleep(3000);
        Console.Write("  Fixing device name... ");
        string displayName = profile.DeviceDescription ?? profile.ProductString;
        FixHidChildNames(displayName);
        // Also set on root device (search both enumerator types)
        foreach (string enumer in new[] { "HID_IG_00", "HIDClass", "XnaComposite",
            "VID_045E&PID_02FF&IG_00", "VID_045E&PID_0B13&IG_00" })
        {
            for (int idx = 0; idx < 10; idx++)
            {
                string rootId = $@"ROOT\{enumer}\{idx:D4}";
                if (CM_Locate_DevNodeW(out uint rootI, rootId, 0) == 0)
                {
                    SetBusReportedDeviceDesc(rootId, displayName);
                    SetDeviceFriendlyName(rootId, displayName);
                    goto nameSetDone;
                }
            }
        }
        nameSetDone:
        // Final name fix after xinputhid grandchild has fully appeared
        Thread.Sleep(2000);
        FixHidChildNames(displayName);

        // Set BusTypeGuid to USB on ROOT device and HID children
        // GameInput uses BusTypeGuid to determine how to read HID reports
        {
            var busTypeKey = new DEVPROPKEY
            {
                fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
                pid = 21 // DEVPKEY_Device_BusTypeGuid
            };
            byte[] usbBusGuid = new Guid("9d7debbc-c85d-11d1-9eb4-006008c3a19a").ToByteArray();
            // DEVPROP_TYPE_GUID = 0x0D
            foreach (string enumer in new[] { "HID_IG_00", "HIDClass", "XnaComposite",
            "VID_045E&PID_02FF&IG_00", "VID_045E&PID_0B13&IG_00" })
            {
                for (int idx = 0; idx < 10; idx++)
                {
                    string devId = $@"ROOT\{enumer}\{idx:D4}";
                    if (CM_Locate_DevNodeW(out uint devInst, devId, 0) == 0)
                    {
                        CM_Set_DevNode_PropertyW(devInst, ref busTypeKey, 0x0D,
                            usbBusGuid, (uint)usbBusGuid.Length, 0);
                        // Also set on HID child
                        if (CM_Get_Child(out uint childInst, devInst, 0) == 0)
                        {
                            CM_Set_DevNode_PropertyW(childInst, ref busTypeKey, 0x0D,
                                usbBusGuid, (uint)usbBusGuid.Length, 0);
                        }
                    }
                }
            }
        }
        Console.WriteLine("OK");

        // Step 3.5: Create XUSB/WinExInput companion for browser detection.
        // For xinputhid profiles, xinputhid provides XInput (via driverPid 02FF).
        // Companion provides WinExInput for WGI GamepadAdded (browser STANDARD GAMEPAD).
        if (profile.VendorId == 0x045E)
        {
            Console.Write("  Creating XUSB companion... ");
            // Ensure XUSB driver is in the store
            string xusbInf = Path.Combine(BuildDir, "hidmaestro_xusb.inf");
            if (File.Exists(xusbInf))
                RunProcess("pnputil.exe", $"/add-driver \"{xusbInf}\" /install", timeoutMs: 10000);
            // Check if already exists
            bool xusbExists = false;
            for (int idx = 0; idx < 10; idx++)
            {
                string candidate = $@"ROOT\SYSTEM\{idx:D4}";
                if (CM_Locate_DevNodeW(out uint _, candidate, 0) == 0)
                {
                    var (_, info) = RunProcess("pnputil.exe", $"/enum-devices /instanceid \"{candidate}\"");
                    if (info.Contains("XInput") || info.Contains("HIDMaestro"))
                    {
                        RunProcess("pnputil.exe", $"/restart-device \"{candidate}\"");
                        xusbExists = true;
                        break;
                    }
                }
            }
            if (!xusbExists)
            {
                var sysGuid = new Guid("4D36E97D-E325-11CE-BFC1-08002BE10318");
                IntPtr dis2 = SetupDiCreateDeviceInfoList(ref sysGuid, IntPtr.Zero);
                if (dis2 != new IntPtr(-1))
                {
                    byte[] diBuf = new byte[32];
                    BitConverter.GetBytes(IntPtr.Size == 8 ? 32 : 28).CopyTo(diBuf, 0);
                    var diHandle = System.Runtime.InteropServices.GCHandle.Alloc(diBuf, GCHandleType.Pinned);
                    string xusbHw = "root\\HIDMaestroXUSB\0\0";
                    byte[] xusbHwBytes = Encoding.Unicode.GetBytes(xusbHw);
                    if (SetupDiCreateDeviceInfoW_Raw(dis2, "System", ref sysGuid,
                        "HIDMaestro XInput Companion", IntPtr.Zero, 1, diHandle.AddrOfPinnedObject()))
                    {
                        SetupDiSetDeviceRegistryPropertyW_Raw(dis2, diHandle.AddrOfPinnedObject(), 1, xusbHwBytes, (uint)xusbHwBytes.Length);
                        SetupDiCallClassInstaller_Raw(0x19, dis2, diHandle.AddrOfPinnedObject());
                    }
                    diHandle.Free();
                    SetupDiDestroyDeviceInfoList(dis2);
                    Thread.Sleep(3000);
                }
            }
            // Restart XUSB companion to trigger driver binding
            // (DIF_REGISTERDEVICE alone doesn't install the driver)
            for (int idx = 0; idx < 10; idx++)
            {
                string candidate = $@"ROOT\SYSTEM\{idx:D4}";
                if (CM_Locate_DevNodeW(out uint _, candidate, 0) == 0)
                {
                    var (_, info) = RunProcess("pnputil.exe", $"/enum-devices /instanceid \"{candidate}\"");
                    if (info.Contains("XInput") || info.Contains("HIDMaestro"))
                    {
                        RunProcess("pnputil.exe", $"/restart-device \"{candidate}\"", timeoutMs: 5000);
                        Thread.Sleep(3000);
                        break;
                    }
                }
            }
            Console.WriteLine("OK");
        }

        // (Gamepad companion already created in Step 1.5)

        // Step 3.6: Create ViGEmBus Xbox 360 target for GameInput/SDL3
        // GameInput can't read from UMDF2 virtual HID devices (HID bus type).
        // ViGEmBus creates PDOs with USB bus type that GameInput CAN read.
        SafeFileHandle? vigemHandle = null;
        uint vigemSerial = 0;
        if (false) // ViGEmBus DISABLED - security fix makes GameInput work directly
        {
            Console.Write("  Creating ViGEmBus target... ");
            try
            {
                // Open ViGEmBus device
                var vigemGuid = new Guid("96E42B22-F5E9-42F8-B043-ED0F932F014F");
                IntPtr vDis = SetupDiGetClassDevsW(ref vigemGuid, IntPtr.Zero, IntPtr.Zero,
                    DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
                if (vDis != new IntPtr(-1))
                {
                    var vDid = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                    if (SetupDiEnumDeviceInterfaces(vDis, IntPtr.Zero, ref vigemGuid, 0, ref vDid))
                    {
                        SetupDiGetDeviceInterfaceDetailW(vDis, ref vDid, IntPtr.Zero, 0, out uint vReq, IntPtr.Zero);
                        IntPtr vDetail = Marshal.AllocHGlobal((int)vReq);
                        Marshal.WriteInt32(vDetail, 8);
                        if (SetupDiGetDeviceInterfaceDetailW(vDis, ref vDid, vDetail, vReq, out _, IntPtr.Zero))
                        {
                            string vPath = Marshal.PtrToStringUni(vDetail + 4)!;
                            vigemHandle = CreateFileW(vPath, GENERIC_READ | GENERIC_WRITE,
                                FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                            if (vigemHandle.IsInvalid) vigemHandle = null;
                        }
                        Marshal.FreeHGlobal(vDetail);
                    }
                    SetupDiDestroyDeviceInfoList(vDis);
                }

                if (vigemHandle != null)
                {
                    // Check version
                    byte[] verBuf = new byte[8]; // VIGEM_CHECK_VERSION: Size(4) + Version(4)
                    BitConverter.GetBytes(8).CopyTo(verBuf, 0);
                    BitConverter.GetBytes(0x0001).CopyTo(verBuf, 4);
                    DeviceIoControl(vigemHandle, 0x002AA00C, verBuf, (uint)verBuf.Length,
                        null, 0, out _, IntPtr.Zero);

                    // Plugin Xbox 360 target
                    vigemSerial = 1;
                    byte[] plugBuf = new byte[16]; // Size(4) + SerialNo(4) + TargetType(4) + VID(2) + PID(2)
                    BitConverter.GetBytes(16).CopyTo(plugBuf, 0);
                    BitConverter.GetBytes(vigemSerial).CopyTo(plugBuf, 4);
                    BitConverter.GetBytes(2).CopyTo(plugBuf, 8); // DualShock4Wired = 2
                    BitConverter.GetBytes((ushort)0x045E).CopyTo(plugBuf, 12); // VID
                    BitConverter.GetBytes((ushort)0xFFFE).CopyTo(plugBuf, 14); // PID FFFE (our INF matches)
                    bool plugOk = DeviceIoControl(vigemHandle, 0x002AA004, plugBuf, (uint)plugBuf.Length,
                        null, 0, out _, IntPtr.Zero);
                    if (plugOk)
                    {
                        // Wait for device to be ready
                        byte[] waitBuf = new byte[8]; // Size(4) + SerialNo(4)
                        BitConverter.GetBytes(8).CopyTo(waitBuf, 0);
                        BitConverter.GetBytes(vigemSerial).CopyTo(waitBuf, 4);
                        DeviceIoControl(vigemHandle, 0x002AA010, waitBuf, (uint)waitBuf.Length,
                            null, 0, out _, IntPtr.Zero);
                        Thread.Sleep(1000);
                        Console.WriteLine("OK");
                    }
                    else
                    {
                        Console.WriteLine($"FAILED (err={Marshal.GetLastWin32Error()})");
                        vigemSerial = 0;
                    }
                }
                else
                {
                    Console.WriteLine("ViGEmBus not found");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}");
            }
        }

        // Step 4: Open the HID child device
        Console.Write("  Opening HID device... ");
        using var h = OpenHidDevice(profile.VendorId, profile.ProductId);
        if (h == null)
        {
            Console.WriteLine("SKIPPED (xinputhid blocks direct access — XInput-only mode)");
        }

        // Query HID caps for report sizes
        ushort hidFeatureReportLen = 0;
        ushort hidInputReportLen = 0;
        if (h != null && HidD_GetPreparsedData(h, out IntPtr ppd))
        {
            HidP_GetCaps(ppd, out HIDP_CAPS caps);
            hidInputReportLen = caps.InputReportByteLength;
            hidFeatureReportLen = caps.FeatureReportByteLength;
            Console.WriteLine($"  HID Caps: Input={caps.InputReportByteLength}B  Output={caps.OutputReportByteLength}B  Feature={caps.FeatureReportByteLength}B");
            Console.WriteLine($"            Buttons: In={caps.NumberInputButtonCaps} Out={caps.NumberOutputButtonCaps} Feat={caps.NumberFeatureButtonCaps}");
            Console.WriteLine($"            Values:  In={caps.NumberInputValueCaps} Out={caps.NumberOutputValueCaps} Feat={caps.NumberFeatureValueCaps}");
            HidD_FreePreparsedData(ppd);
        }
        else if (h == null)
        {
            Console.WriteLine("  HID Caps: N/A (XInput-only mode)");
        }

        // Open XUSB companion for direct XInput data injection (driverMode=hid only)
        SafeFileHandle? xh = null;
        if (profile.UsesUpperFilter)
        {
            Console.WriteLine($"  XInput: via {profile.UpperFilterName} upper filter");
        }
        else if (profile.VendorId == 0x045E)
        {
            Console.Write("  Opening XUSB interface... ");
            var xusbGuid = new Guid("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");
            for (int retry = 0; retry < 3 && xh == null; retry++)
            {
                if (retry > 0) Thread.Sleep(2000);
                IntPtr xDis = SetupDiGetClassDevsW(ref xusbGuid, IntPtr.Zero, IntPtr.Zero,
                    DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
                if (xDis != new IntPtr(-1))
                {
                    var xDid = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                    for (uint xi = 0; xi < 10; xi++)
                    {
                        if (!SetupDiEnumDeviceInterfaces(xDis, IntPtr.Zero, ref xusbGuid, xi, ref xDid))
                            break;
                        SetupDiGetDeviceInterfaceDetailW(xDis, ref xDid, IntPtr.Zero, 0, out uint xReq, IntPtr.Zero);
                        IntPtr xDetail = Marshal.AllocHGlobal((int)xReq);
                        Marshal.WriteInt32(xDetail, 8);
                        if (SetupDiGetDeviceInterfaceDetailW(xDis, ref xDid, xDetail, xReq, out _, IntPtr.Zero))
                        {
                            string xPath = Marshal.PtrToStringUni(xDetail + 4)!;
                            // Open our device's XUSB interface (ROOT#HID_IG_00 or ROOT#SYSTEM)
                            if (xPath.Contains("root#", StringComparison.OrdinalIgnoreCase))
                            {
                                xh = CreateFileW(xPath, GENERIC_READ | GENERIC_WRITE,
                                    FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                                if (xh.IsInvalid) xh = null;
                            }
                        }
                        Marshal.FreeHGlobal(xDetail);
                    }
                    SetupDiDestroyDeviceInfoList(xDis);
                }
            }
            Console.WriteLine(xh != null ? "OK" : "not found");
        }

        // Parse the descriptor to build a generic input report packer
        // Parse the ORIGINAL profile descriptor (before Feature Report injection)
        // GIP builder: used for XUSB GET_STATE data AND as report builder for xinputhid profiles
        byte[] gipDescBytes = Convert.FromHexString("05010905a101a10009300931150027ffff0000950275108102c0a10009330934150027ffff0000950275108102c005010932150026ff039501750a81021500250075069501810305010935150026ff039501750a81021500250075069501810305091901290a950a750181021500250075069501810305010939150125083500463b0166140075049501814275049501150025003500450065008103a102050f0997150025017504950191021500250091030970150025647508950491020950660110550e26ff009501910209a7910265005500097c9102c005010980a10009851500250195017501810215002500750795018103c005060920150026ff00750895018102c0");
        var gipBuilder = HidReportBuilder.Parse(gipDescBytes);
        Console.WriteLine("  GIP builder layout:");
        gipBuilder.PrintLayout();
        Console.WriteLine($"    RightTrigger: {(gipBuilder.RightTrigger != null ? $"bit {gipBuilder.RightTrigger.BitOffset}, {gipBuilder.RightTrigger.BitSize}b" : "NULL!")}");
        // Quick test: build with RT=0.8 and check bytes 10-11
        byte[] testGip = gipBuilder.BuildReport(rightTrigger: 0.8);
        Console.WriteLine($"    Test RT=0.8: bytes[10-11] = 0x{testGip[10]:X2}{testGip[11]:X2} = {(testGip[10] | (testGip[11] << 8)) & 0x3FF}");

        // For xinputhid profiles with companion, use BLE descriptor builder (Report ID 0x01)
        // For other profiles, parse the profile descriptor directly
        var reportBuilder = profile.UsesUpperFilter
            ? HidReportBuilder.Parse(bleDesc)
            : HidReportBuilder.Parse(profileDesc ?? descriptor);
        reportBuilder.PrintLayout();

        Console.WriteLine("\n  Sending input via SetFeature + XInput. Ctrl+C to stop.\n");

        // Feature report: Report ID 0x02 + enough data for the input report
        // Output Report buffer
        int reportBufSize = reportBuilder.InputReportByteSize;
        if (h != null)
        {
            if (HidD_GetPreparsedData(h, out IntPtr ppd2))
            {
                HidP_GetCaps(ppd2, out HIDP_CAPS caps2);
                if (caps2.OutputReportByteLength > 0)
                    reportBufSize = caps2.OutputReportByteLength;
                HidD_FreePreparsedData(ppd2);
            }
        }
        Console.WriteLine($"  Report buffer: {reportBufSize} bytes");

        // Shared memory for data injection (bypasses xinputhid/HidHide upper filters)
        // Shared file for data injection (driver reads via timer)
        // Using a temp file instead of Global\ shared memory to avoid privilege issues
        string sharedFilePath = @"C:\ProgramData\HIDMaestro\input.bin";
        Directory.CreateDirectory(Path.GetDirectoryName(sharedFilePath)!);
        IntPtr sharedMemPtr = IntPtr.Zero;
        int sharedMemSeqNo = 0;
        FileStream? sharedFile = null;
        try
        {
            sharedFile = new FileStream(sharedFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.ReadWrite, 72, FileOptions.WriteThrough);
            // Pre-allocate 72 bytes
            sharedFile.SetLength(86); // SeqNo(4) + DataSize(4) + Data(64) + GipData(14)
            sharedFile.Flush();
            Console.WriteLine("  Shared file: OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Shared file: FAILED ({ex.Message})");
        }

        var sw = Stopwatch.StartNew();
        int count = 0;
        int failCount = 0;

        while (!_cts.Token.IsCancellationRequested)
        {
            double t = sw.Elapsed.TotalSeconds;
            double angle = t * Math.PI * 2 * 0.5;

            // Pack gamepad input — data starts after the Report ID byte
            // Generic descriptor-driven input packing (works for ANY gamepad descriptor)
            double lxNorm = 0.5 + 0.46 * Math.Sin(angle);  // Left stick circles
            double lyNorm = 0.5 + 0.46 * Math.Cos(angle);
            uint btnMask = ((int)t % 2 == 0) ? 0x01u : 0x00u; // Toggle button A

            // Triggers: cycle through sweep pattern (15s cycle)
            // Phase 0-3s:  LT pulls 0→full, RT idle
            // Phase 3-6s:  LT releases full→0, RT idle
            // Phase 6-9s:  RT pulls 0→full, LT idle
            // Phase 9-12s: Both pull together 0→full
            // Phase 12-15s: Both release full→0
            double triggerPhase = (t % 15.0);
            double ltVal, rtVal;
            if (profile.HasCombinedTriggers)
            {
                // Combined Z: 0.5=center, 0.0=LT full, 1.0=RT full
                // Both full = cancel out = center (0.5)
                if (triggerPhase < 3.0)
                    ltVal = 0.5 - 0.5 * (triggerPhase / 3.0);         // center → LT full
                else if (triggerPhase < 6.0)
                    ltVal = 0.5 * ((triggerPhase - 3.0) / 3.0);       // LT full → center
                else if (triggerPhase < 9.0)
                    ltVal = 0.5 + 0.5 * ((triggerPhase - 6.0) / 3.0); // center → RT full
                else if (triggerPhase < 12.0)
                    ltVal = 1.0 - 0.5 * ((triggerPhase - 9.0) / 3.0); // RT full → center
                else
                    ltVal = 0.5; // both full = cancel = center
                rtVal = 0.0;
            }
            else
            {
                // Separate: each 0.0=released, 1.0=full
                if (triggerPhase < 3.0)
                    { ltVal = triggerPhase / 3.0; rtVal = 0.0; }
                else if (triggerPhase < 6.0)
                    { ltVal = 1.0 - (triggerPhase - 3.0) / 3.0; rtVal = 0.0; }
                else if (triggerPhase < 9.0)
                    { ltVal = 0.0; rtVal = (triggerPhase - 6.0) / 3.0; }
                else if (triggerPhase < 12.0)
                    { ltVal = (triggerPhase - 9.0) / 3.0; rtVal = 1.0; }  // Both engage
                else
                    { ltVal = 1.0 - (triggerPhase - 12.0) / 3.0; rtVal = 1.0 - (triggerPhase - 12.0) / 3.0; } // Both release
            }

            // Separate trigger values: sequential ramp pattern
            // Phase 0-3: LT up, RT=0. Phase 3-6: LT down, RT=0.
            // Phase 6-9: LT=0, RT up. Phase 9-12: LT up, RT holds.
            // Phase 12-15: both ramp down together.
            double sepLt, sepRt;
            if (triggerPhase < 3.0)
                { sepLt = triggerPhase / 3.0; sepRt = 0.0; }
            else if (triggerPhase < 6.0)
                { sepLt = 1.0 - (triggerPhase - 3.0) / 3.0; sepRt = 0.0; }
            else if (triggerPhase < 9.0)
                { sepLt = 0.0; sepRt = (triggerPhase - 6.0) / 3.0; }
            else if (triggerPhase < 12.0)
                { sepLt = (triggerPhase - 9.0) / 3.0; sepRt = 1.0; }
            else
                { sepLt = 1.0 - (triggerPhase - 12.0) / 3.0; sepRt = 1.0 - (triggerPhase - 12.0) / 3.0; }

            byte[] inputReport = reportBuilder.BuildReport(
                leftX: lxNorm, leftY: lyNorm,
                rightX: 0.5, rightY: 0.5,
                leftTrigger: sepLt, rightTrigger: sepRt,
                hatValue: 0, buttonMask: btnMask);

            // Write to shared file: SeqNo(4) + DataSize(4) + Data(64) + GipData(14) = 86 bytes
            // Data = native HID report (for DirectInput via HID READ_REPORT)
            // GipData = GIP format (for XInput via XUSB GET_STATE)
            if (sharedFile != null)
            {
                // Native HID report (from descriptor's format)
                int dataStart = reportBuilder.InputReportId != 0 ? 1 : 0;
                int dataLen = Math.Min(inputReport.Length - dataStart, 64);

                // GIP data: build using GIP descriptor parser (identical to xinputhid path)
                // Debug: log once to verify separate triggers
                if (sharedMemSeqNo == 200)
                    File.WriteAllText(@"C:\ProgramData\HIDMaestro\gip_debug.txt",
                        $"sepLt={sepLt:F3} sepRt={sepRt:F3} gipBuilder.LT={gipBuilder.LeftTrigger?.BitOffset},{gipBuilder.LeftTrigger?.BitSize} RT={gipBuilder.RightTrigger?.BitOffset},{gipBuilder.RightTrigger?.BitSize}\n");
                byte[] gipReport = gipBuilder.BuildReport(
                    leftX: lxNorm, leftY: lyNorm,
                    rightX: 0.5, rightY: 0.5,
                    leftTrigger: sepLt, rightTrigger: sepRt,
                    hatValue: 0, buttonMask: btnMask);
                byte[] gipData = new byte[14];
                Array.Copy(gipReport, 0, gipData, 0, Math.Min(14, gipReport.Length));

                // Debug: log trigger values once
                if (sharedMemSeqNo == 100)
                {
                    string dbg = $"sepLt={sepLt:F3} sepRt={sepRt:F3} gipReport.Len={gipReport.Length} " +
                        $"bytes[8-11]={gipReport[8]:X2}{gipReport[9]:X2}{gipReport[10]:X2}{gipReport[11]:X2} " +
                        $"gipData[8-11]={gipData[8]:X2}{gipData[9]:X2}{gipData[10]:X2}{gipData[11]:X2}\n" +
                        $"RightTrigger={gipBuilder.RightTrigger?.BitOffset},{gipBuilder.RightTrigger?.BitSize}\n";
                    File.WriteAllText(@"C:\ProgramData\HIDMaestro\trigger_debug.txt", dbg);
                }

                sharedMemSeqNo++;
                sharedFile.Seek(0, SeekOrigin.Begin);
                sharedFile.Write(BitConverter.GetBytes(sharedMemSeqNo));  // 4 bytes
                sharedFile.Write(BitConverter.GetBytes(dataLen));          // 4 bytes
                byte[] padded = new byte[64];
                Array.Copy(inputReport, dataStart, padded, 0, dataLen);
                sharedFile.Write(padded);                                  // 64 bytes
                sharedFile.Write(gipData);                                 // 14 bytes
                sharedFile.Flush();
            }
            bool ok = true;

            // Send GIP-format data to XUSB companion for XInput
            if (xh != null)
            {
                byte[] xusbIn = new byte[17];
                xusbIn[0] = 0x01; xusbIn[1] = 0x01; xusbIn[2] = 0x00; // GET_STATE header
                // GIP layout: LX(2) LY(2) RX(2) RY(2) LT(2) RT(2) BtnLow(1) BtnHigh(1)
                ushort xLx = (ushort)(lxNorm * 65535);
                ushort xLy = (ushort)(lyNorm * 65535);
                ushort xRx = (ushort)(0.5 * 65535);
                ushort xRy = (ushort)(0.5 * 65535);
                ushort xLt = (ushort)(ltVal * 1023);  // 10-bit trigger
                ushort xRt = (ushort)(rtVal * 1023);
                BitConverter.GetBytes(xLx).CopyTo(xusbIn, 3);
                BitConverter.GetBytes(xLy).CopyTo(xusbIn, 5);
                BitConverter.GetBytes(xRx).CopyTo(xusbIn, 7);
                BitConverter.GetBytes(xRy).CopyTo(xusbIn, 9);
                BitConverter.GetBytes(xLt).CopyTo(xusbIn, 11);
                BitConverter.GetBytes(xRt).CopyTo(xusbIn, 13);
                byte btnLow = 0;
                if ((btnMask & 0x01) != 0) btnLow |= 0x01; // A
                xusbIn[15] = btnLow;
                xusbIn[16] = 0;
                byte[] xusbOut = new byte[29];
                DeviceIoControl(xh, 0x8000E00C, xusbIn, (uint)xusbIn.Length,
                    xusbOut, (uint)xusbOut.Length, out _, IntPtr.Zero);
            }

            // Submit report to ViGEmBus target for GameInput/SDL3
            if (vigemHandle != null && vigemSerial > 0)
            {
                // DS4_SUBMIT_REPORT: Size(4) + SerialNo(4) + DS4_REPORT(9) = 17 bytes
                // DS4_REPORT: bThumbLX(1) + bThumbLY(1) + bThumbRX(1) + bThumbRY(1) +
                //             wButtons(2) + bSpecial(1) + bTriggerL(1) + bTriggerR(1)
                byte[] vrBuf = new byte[17];
                BitConverter.GetBytes(17).CopyTo(vrBuf, 0);
                BitConverter.GetBytes(vigemSerial).CopyTo(vrBuf, 4);
                // Sticks: 8-bit unsigned (0=left/up, 128=center, 255=right/down)
                vrBuf[8] = (byte)(lxNorm * 255);      // LX
                vrBuf[9] = (byte)(lyNorm * 255);       // LY
                vrBuf[10] = 128;                        // RX center
                vrBuf[11] = 128;                        // RY center
                // Buttons: lower 4 bits = D-pad (8=none), upper bits = face buttons
                ushort ds4Buttons = 0x08; // D-pad none
                if ((btnMask & 0x01) != 0) ds4Buttons |= 0x20; // Cross
                BitConverter.GetBytes(ds4Buttons).CopyTo(vrBuf, 12);
                vrBuf[14] = 0; // bSpecial
                vrBuf[15] = (byte)(sepLt * 255); // LT
                vrBuf[16] = (byte)(sepRt * 255); // RT
                DeviceIoControl(vigemHandle, 0x002AA80C, vrBuf, (uint)vrBuf.Length,
                    null, 0, out _, IntPtr.Zero);
            }

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                failCount++;
                if (failCount == 1)
                    Console.Error.WriteLine($"  HID send failed: {err} (0x{err:X})");
                if (failCount > 100) break; // Only exit after 100 consecutive failures
            }
            else
            {
                failCount = 0;
            }

            count++;
            if (count % 500 == 1)
            {
                byte[] gr = gipBuilder.BuildReport(leftX: lxNorm, leftY: lyNorm, rightX: 0.5, rightY: 0.5, leftTrigger: sepLt, rightTrigger: sepRt, hatValue: 0, buttonMask: btnMask);
                ushort gLx2 = BitConverter.ToUInt16(gr, 0);
                ushort gLy2 = BitConverter.ToUInt16(gr, 2);
                short xLx = (short)((int)gLx2 - 32768);
                short xLy = (short)((int)gLy2 - 32768);
                Console.Write($"\r  #{count} gip[0-3]: {gLx2:X4} {gLy2:X4}  xi: {xLx} {xLy}  lx={lxNorm:F3} ly={lyNorm:F3}  ");
            }

            Thread.Sleep(4); // ~250 Hz
        }

        Console.WriteLine($"\n\n  Sent {count} reports.");

        // Unplug ViGEmBus target
        if (vigemHandle != null && vigemSerial > 0)
        {
            byte[] unplugBuf = new byte[8]; // Size(4) + SerialNo(4)
            BitConverter.GetBytes(8).CopyTo(unplugBuf, 0);
            BitConverter.GetBytes(vigemSerial).CopyTo(unplugBuf, 4);
            DeviceIoControl(vigemHandle, 0x002AA008, unplugBuf, (uint)unplugBuf.Length,
                null, 0, out _, IntPtr.Zero);
            vigemHandle.Dispose();
        }

        return 0;
    }

    // ── Xbox 360 HID Gamepad ──

    static int TestXbox360()
    {
        Console.WriteLine("-- Xbox 360 Gamepad Emulation --\n");

        // Standard HID gamepad descriptor: 6 axes (16-bit), 10 buttons, 1 hat
        // This produces an exact gamepad in joy.cpl
        byte[] descriptor = {
            0x05, 0x01,                     // Usage Page (Generic Desktop)
            0x09, 0x05,                     // Usage (Game Pad)
            0xA1, 0x01,                     // Collection (Application)
            0x85, 0x01,                     //   Report ID (1)
            // Left stick X, Y
            0x09, 0x30,                     //   Usage (X)
            0x09, 0x31,                     //   Usage (Y)
            0x15, 0x00,                     //   Logical Minimum (0)
            0x27, 0xFF, 0xFF, 0x00, 0x00,   //   Logical Maximum (65535)
            0x75, 0x10,                     //   Report Size (16)
            0x95, 0x02,                     //   Report Count (2)
            0x81, 0x02,                     //   INPUT (Data, Var, Abs)
            // Right stick X, Y
            0x09, 0x33,                     //   Usage (Rx)
            0x09, 0x34,                     //   Usage (Ry)
            0x81, 0x02,                     //   INPUT (Data, Var, Abs)
            // Triggers
            0x09, 0x32,                     //   Usage (Z)
            0x09, 0x35,                     //   Usage (Rz)
            0x81, 0x02,                     //   INPUT (Data, Var, Abs)
            // Buttons 1-10
            0x05, 0x09,                     //   Usage Page (Button)
            0x19, 0x01,                     //   Usage Minimum (1)
            0x29, 0x0A,                     //   Usage Maximum (10)
            0x15, 0x00,                     //   Logical Minimum (0)
            0x25, 0x01,                     //   Logical Maximum (1)
            0x75, 0x01,                     //   Report Size (1)
            0x95, 0x0A,                     //   Report Count (10)
            0x81, 0x02,                     //   INPUT (Data, Var, Abs)
            // 6-bit padding
            0x75, 0x06,                     //   Report Size (6)
            0x95, 0x01,                     //   Report Count (1)
            0x81, 0x01,                     //   INPUT (Cnst)
            // Hat switch
            0x05, 0x01,                     //   Usage Page (Generic Desktop)
            0x09, 0x39,                     //   Usage (Hat switch)
            0x15, 0x01,                     //   Logical Minimum (1)
            0x25, 0x08,                     //   Logical Maximum (8)
            0x35, 0x00,                     //   Physical Minimum (0)
            0x46, 0x3B, 0x01,               //   Physical Maximum (315)
            0x66, 0x14, 0x00,               //   Unit (Degrees)
            0x75, 0x04,                     //   Report Size (4)
            0x95, 0x01,                     //   Report Count (1)
            0x81, 0x42,                     //   INPUT (Data, Var, Abs, Null)
            // 4-bit padding
            0x75, 0x04, 0x95, 0x01,
            0x15, 0x00, 0x25, 0x00,
            0x35, 0x00, 0x45, 0x00, 0x65, 0x00,
            0x81, 0x03,                     //   INPUT (Cnst, Var)
            0xC0                            // End Collection
        };

        ushort vid = 0x045E; // Microsoft
        ushort pid = 0x028E; // Xbox 360 Controller

        // Descriptor is hardcoded in driver — just open the existing device
        Console.Write("  Opening HID device... ");
        using var h = OpenHidDevice(vid, pid);
        if (h == null)
        {
            Console.WriteLine("FAILED");
            Console.WriteLine("  Device not found. Check Device Manager.");
            return 1;
        }

        // Feature report: Report ID 2 + 14 bytes of input data
        // The driver takes these bytes, prepends Report ID 1, and delivers as input
        Console.WriteLine("\n  Sending input via feature reports. Open joy.cpl to watch.");
        Console.WriteLine("  Ctrl+C to stop.\n");

        byte[] report = new byte[15]; // Report ID 2 + 14 bytes data
        report[0] = 0x02; // Feature Report ID

        var sw = Stopwatch.StartNew();
        int count = 0;

        while (!_cts.Token.IsCancellationRequested)
        {
            double t = sw.Elapsed.TotalSeconds;
            double angle = t * Math.PI * 2 * 0.5;

            // Left stick circle
            ushort lx = (ushort)(32768 + (int)(30000 * Math.Sin(angle)));
            ushort ly = (ushort)(32768 + (int)(30000 * Math.Cos(angle)));
            ushort rx = 32768, ry = 32768;
            ushort lt = 0, rt = 0;

            // Pack axes into feature data (starts at [1], report[0] is feature ID)
            report[1] = (byte)(lx & 0xFF); report[2] = (byte)(lx >> 8);
            report[3] = (byte)(ly & 0xFF); report[4] = (byte)(ly >> 8);
            report[5] = (byte)(rx & 0xFF); report[6] = (byte)(rx >> 8);
            report[7] = (byte)(ry & 0xFF); report[8] = (byte)(ry >> 8);
            report[9] = (byte)(lt & 0xFF); report[10] = (byte)(lt >> 8);
            report[11] = (byte)(rt & 0xFF); report[12] = (byte)(rt >> 8);

            // Buttons: press A every other second
            int btns = ((int)t % 2 == 0) ? 0x01 : 0x00;
            report[13] = (byte)(btns & 0xFF);

            // Hat: centered (0 = null)
            report[14] = 0;

            if (!HidD_SetFeature(h, report, (uint)report.Length))
            {
                int err = Marshal.GetLastWin32Error();
                if (count == 0)
                    Console.Error.WriteLine($"  HidD_SetFeature failed: {err} (0x{err:X})");
                if (count > 3) break;
            }

            count++;
            if (count % 100 == 0)
                Console.Write($"\r  Reports: {count}  LX={lx,5} LY={ly,5}  ");

            Thread.Sleep(4); // ~250 Hz
        }

        Console.WriteLine($"\n\n  Sent {count} reports.");
        return 0;
    }

    // ── DualSense ──

    static int TestDualSense()
    {
        Console.WriteLine("-- DualSense Emulation --\n");

        byte[] descriptor = {
            0x05, 0x01,                     // Usage Page (Generic Desktop)
            0x09, 0x05,                     // Usage (Game Pad)
            0xA1, 0x01,                     // Collection (Application)
            0x85, 0x01,                     //   Report ID (1)
            // 4 axes uint8 (LX, LY, RX, RY)
            0x09, 0x30, 0x09, 0x31, 0x09, 0x33, 0x09, 0x34,
            0x15, 0x00, 0x26, 0xFF, 0x00,
            0x75, 0x08, 0x95, 0x04, 0x81, 0x02,
            // 2 triggers uint8 (L2, R2)
            0x09, 0x32, 0x09, 0x35,
            0x95, 0x02, 0x81, 0x02,
            // 14 buttons
            0x05, 0x09, 0x19, 0x01, 0x29, 0x0E,
            0x15, 0x00, 0x25, 0x01,
            0x75, 0x01, 0x95, 0x0E, 0x81, 0x02,
            // 2-bit pad
            0x75, 0x02, 0x95, 0x01, 0x81, 0x01,
            // Hat
            0x05, 0x01, 0x09, 0x39,
            0x15, 0x00, 0x25, 0x07,
            0x35, 0x00, 0x46, 0x3B, 0x01, 0x65, 0x14,
            0x75, 0x04, 0x95, 0x01, 0x81, 0x42,
            // 4-bit pad
            0x75, 0x04, 0x95, 0x01,
            0x15, 0x00, 0x25, 0x00, 0x35, 0x00, 0x45, 0x00, 0x65, 0x00,
            0x81, 0x03,
            0xC0
        };

        ushort vid = 0x054C; // Sony
        ushort pid = 0x0CE6; // DualSense

        Console.Write("  Writing config to registry... ");
        WriteConfig(descriptor, vid, pid);
        Console.WriteLine("OK");

        RestartDevice();

        Console.Write("  Opening HID device... ");
        using var h = OpenHidDevice(vid, pid);
        if (h == null)
        {
            Console.WriteLine("FAILED");
            return 1;
        }

        Console.WriteLine("\n  Sending input reports. Open joy.cpl to watch.");
        Console.WriteLine("  Ctrl+C to stop.\n");

        // Report ID + 4 axes + 2 triggers + 2 bytes (buttons + hat) = 9 bytes
        byte[] report = new byte[9];
        report[0] = 0x01;

        var sw = Stopwatch.StartNew();
        int count = 0;

        while (!_cts.Token.IsCancellationRequested)
        {
            double t = sw.Elapsed.TotalSeconds;
            double angle = t * Math.PI * 2 * 0.5;

            report[1] = (byte)(128 + (int)(120 * Math.Sin(angle)));  // LX
            report[2] = (byte)(128 + (int)(120 * Math.Cos(angle)));  // LY
            report[3] = 128; // RX
            report[4] = 128; // RY
            report[5] = 0;   // L2
            report[6] = 0;   // R2
            report[7] = (byte)(((int)t % 2 == 0) ? 0x01 : 0x00); // Cross btn
            report[8] = 0x08; // Hat centered

            if (!WriteFile(h, report, (uint)report.Length, out uint written, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                if (count == 0)
                    Console.Error.WriteLine($"  WriteFile failed: {err} (0x{err:X}), written={written}");
                if (count > 3) break; // Allow a few retries
            }

            count++;
            if (count % 100 == 0)
                Console.Write($"\r  Reports: {count}  LX={report[1],3} LY={report[2],3}  ");

            Thread.Sleep(4);
        }

        Console.WriteLine($"\n\n  Sent {count} reports.");
        return 0;
    }

    // ── Profile database commands ──

    static string GetProfilesDir()
    {
        // Walk up from exe to find profiles/ directory
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "profiles");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        // Fallback to known path
        return @"C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\profiles";
    }

    static int ListProfiles()
    {
        Console.WriteLine("-- Controller Profile Database --");
        var db = ProfileDatabase.Load(GetProfilesDir());
        db.PrintAll();
        return 0;
    }

    static int SearchProfiles(string query)
    {
        if (string.IsNullOrEmpty(query))
            return Error("Usage: HIDMaestroTest search <query>");

        var db = ProfileDatabase.Load(GetProfilesDir());
        var results = db.Search(query).ToList();

        Console.WriteLine($"-- Search results for \"{query}\" ({results.Count} found) --\n");
        foreach (var p in results)
            Console.WriteLine($"  {p.Id,-35} {p.Name,-45} {p.VendorId:X4}:{p.ProductId:X4}");

        if (results.Count == 0)
            Console.WriteLine("  No profiles found.");
        return 0;
    }

    static int ShowProfile(string id)
    {
        if (string.IsNullOrEmpty(id))
            return Error("Usage: HIDMaestroTest info <profile-id>");

        var db = ProfileDatabase.Load(GetProfilesDir());
        var p = db.GetById(id);

        if (p == null)
        {
            Console.Error.WriteLine($"Profile '{id}' not found. Use 'list' to see all profiles.");
            return 1;
        }

        Console.WriteLine($"-- Profile: {p.Name} --\n");
        Console.WriteLine($"  ID:             {p.Id}");
        Console.WriteLine($"  Vendor:         {p.Vendor}");
        Console.WriteLine($"  VID:            0x{p.VendorId:X4}");
        Console.WriteLine($"  PID:            0x{p.ProductId:X4}");
        Console.WriteLine($"  Product String: {p.ProductString}");
        Console.WriteLine($"  Manufacturer:   {p.ManufacturerString ?? "(unknown)"}");
        Console.WriteLine($"  Type:           {p.Type}");
        Console.WriteLine($"  Connection:     {p.Connection}");
        Console.WriteLine($"  Descriptor:     {(p.HasDescriptor ? $"{p.GetDescriptorBytes()!.Length} bytes" : "NOT CAPTURED")}");
        Console.WriteLine($"  Input Report:   {(p.InputReportSize.HasValue ? $"{p.InputReportSize} bytes" : "unknown")}");
        if (!string.IsNullOrEmpty(p.Notes))
            Console.WriteLine($"\n  Notes: {p.Notes}");
        return 0;
    }

    // ── SetName test ──

    static int SetNameTest(string name)
    {
        Console.WriteLine($"Setting device name to: \"{name}\"\n");

        // Set FriendlyName on root
        Console.Write("  Setting FriendlyName on root... ");
        SetDeviceFriendlyName(@"ROOT\HID_IG_00\0000", name);
        Console.WriteLine();

        // Set BusReportedDeviceDesc on root
        Console.Write("  Setting BusReportedDeviceDesc on root... ");
        SetBusReportedDeviceDesc(@"ROOT\HID_IG_00\0000", name);
        Console.WriteLine();

        // Verify
        uint locResult = CM_Locate_DevNodeW(out uint devInst, @"ROOT\HID_IG_00\0000", 0);
        Console.WriteLine($"  Locate root: result={locResult} inst={devInst}");

        if (locResult == 0)
        {
            // Try reading back FriendlyName
            var fnKey = new DEVPROPKEY
            {
                fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
                pid = 14
            };
            byte[] readBuf = new byte[512];
            uint readSz = 512;
            uint readType;

            uint getResult = CM_Get_DevNode_PropertyW(devInst, ref fnKey, out readType, readBuf, ref readSz, 0);
            string readVal = readSz > 0 ? Encoding.Unicode.GetString(readBuf, 0, (int)readSz).TrimEnd('\0') : "(empty)";
            Console.WriteLine($"  Read FriendlyName: result={getResult} type={readType} val=\"{readVal}\"");
        }

        return 0;
    }

    // ── Read Test ──

    static int ReadTest()
    {
        Console.WriteLine("-- HID Read Test --\n");
        ushort vid = 0x045E, pid = 0x0B13;

        using var h = OpenHidDevice(vid, pid);
        if (h == null) return Error("Device not found");

        if (HidD_GetPreparsedData(h, out IntPtr ppd))
        {
            HidP_GetCaps(ppd, out HIDP_CAPS caps);
            Console.WriteLine($"  Input={caps.InputReportByteLength}B  Feature={caps.FeatureReportByteLength}B");
            HidD_FreePreparsedData(ppd);
        }

        // Try to read one input report (blocking, will wait for data)
        Console.Write("  Reading input report (waiting for data)... ");
        byte[] buf = new byte[17];
        bool ok = ReadFile(h, buf, (uint)buf.Length, out uint bytesRead, IntPtr.Zero);
        if (ok)
        {
            Console.WriteLine($"OK ({bytesRead} bytes)");
            Console.Write("  Data: ");
            for (int i = 0; i < (int)bytesRead; i++)
                Console.Write($"{buf[i]:X2} ");
            Console.WriteLine();

            // Parse axes
            if (bytesRead >= 9)
            {
                ushort lx = (ushort)(buf[1] | (buf[2] << 8));
                ushort ly = (ushort)(buf[3] | (buf[4] << 8));
                ushort rx = (ushort)(buf[5] | (buf[6] << 8));
                ushort ry = (ushort)(buf[7] | (buf[8] << 8));
                Console.WriteLine($"  LX={lx} LY={ly} RX={rx} RY={ry}");
            }
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"FAILED (error {err} / 0x{err:X})");
        }
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer,
        uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    // ── WGI Test ──

    static int TestWgi()
    {
        Console.WriteLine("-- Windows.Gaming.Input Test --\n");

        // WGI needs event-based discovery. Register for events, then pump messages.
        var found = new List<Windows.Gaming.Input.RawGameController>();
        var foundGamepads = new List<Windows.Gaming.Input.Gamepad>();

        Windows.Gaming.Input.RawGameController.RawGameControllerAdded += (_, rc) =>
        {
            found.Add(rc);
            Console.WriteLine($"  [EVENT] RawController added: {rc.DisplayName} VID=0x{rc.HardwareVendorId:X4} PID=0x{rc.HardwareProductId:X4}");
            Console.WriteLine($"    Axes={rc.AxisCount} Buttons={rc.ButtonCount} Switches={rc.SwitchCount}");
            var asGamepad = Windows.Gaming.Input.Gamepad.FromGameController(rc);
            Console.WriteLine($"    IsGamepad: {asGamepad != null}");
        };

        Windows.Gaming.Input.Gamepad.GamepadAdded += (_, gp) =>
        {
            foundGamepads.Add(gp);
            Console.WriteLine($"  [EVENT] Gamepad added (standard mapping!)");
        };

        // Check existing devices
        var existing = Windows.Gaming.Input.RawGameController.RawGameControllers;
        Console.WriteLine($"Existing RawControllers: {existing.Count}");
        foreach (var rc in existing)
        {
            Console.WriteLine($"  {rc.DisplayName} VID=0x{rc.HardwareVendorId:X4} PID=0x{rc.HardwareProductId:X4}");
            Console.WriteLine($"    Axes={rc.AxisCount} Buttons={rc.ButtonCount} Switches={rc.SwitchCount}");
            var asGamepad = Windows.Gaming.Input.Gamepad.FromGameController(rc);
            Console.WriteLine($"    IsGamepad: {asGamepad != null}");
        }

        var existingGamepads = Windows.Gaming.Input.Gamepad.Gamepads;
        Console.WriteLine($"Existing Gamepads: {existingGamepads.Count}");

        // WGI needs a Win32 message pump for device notifications
        Console.WriteLine("\nPumping messages for 5s...");
        var sw2 = Stopwatch.StartNew();
        while (sw2.ElapsedMilliseconds < 5000)
        {
            // Win32 PeekMessage/DispatchMessage pump
            while (PeekMessageW(out MSG msg, IntPtr.Zero, 0, 0, 1)) // PM_REMOVE
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            Thread.Sleep(50);
        }

        Console.WriteLine($"\nFinal: RawControllers={found.Count + existing.Count} Gamepads={foundGamepads.Count + existingGamepads.Count}");
        return 0;
    }

    // ── Dump Controllers ──

    static int DumpControllers()
    {
        Console.WriteLine("-- HID Controller Dump --\n");
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr dis = SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (dis == new IntPtr(-1)) return Error("SetupDi failed");

        for (uint idx = 0; idx < 256; idx++)
        {
            var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            if (!SetupDiEnumDeviceInterfaces(dis, IntPtr.Zero, ref hidGuid, idx, ref did)) break;

            SetupDiGetDeviceInterfaceDetailW(dis, ref did, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
            IntPtr detail = Marshal.AllocHGlobal((int)reqSize);
            Marshal.WriteInt32(detail, 8);
            if (!SetupDiGetDeviceInterfaceDetailW(dis, ref did, detail, reqSize, out _, IntPtr.Zero))
            { Marshal.FreeHGlobal(detail); continue; }
            string path = Marshal.PtrToStringUni(detail + 4)!;
            Marshal.FreeHGlobal(detail);

            var handle = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid) continue;

            var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (!HidD_GetAttributes(handle, ref attrs)) { handle.Dispose(); continue; }

            // Show game controllers (skip keyboards, mice, etc.)
            if (HidD_GetPreparsedData(handle, out IntPtr ppd))
            {
                HidP_GetCaps(ppd, out HIDP_CAPS caps);
                // Usage Page 1 (Generic Desktop), Usage 4/5 (Joystick/GamePad)
                if (caps.UsagePage == 0x01 && (caps.Usage == 0x04 || caps.Usage == 0x05))
                {
                    byte[] prodBuf = new byte[256];
                    HidD_GetProductString(handle, prodBuf, 256);
                    string prod = System.Text.Encoding.Unicode.GetString(prodBuf).TrimEnd('\0');
                    Console.WriteLine($"=== {prod} ===");
                    Console.WriteLine($"  VID=0x{attrs.VendorID:X4} PID=0x{attrs.ProductID:X4} Ver=0x{attrs.VersionNumber:X4}");
                    Console.WriteLine($"  Input={caps.InputReportByteLength}B Output={caps.OutputReportByteLength}B Feature={caps.FeatureReportByteLength}B");
                    Console.WriteLine($"  Buttons: In={caps.NumberInputButtonCaps} Out={caps.NumberOutputButtonCaps}");
                    Console.WriteLine($"  Values:  In={caps.NumberInputValueCaps} Out={caps.NumberOutputValueCaps} Feat={caps.NumberFeatureValueCaps}");
                    Console.WriteLine($"  Path: {path}");

                    // Dump value caps (axes)
                    if (caps.NumberInputValueCaps > 0)
                    {
                        // HIDP_VALUE_CAPS: UsagePage+0, ReportID+2, IsRange+12, BitSize+18, ReportCount+20,
                        // LogicalMin+40, LogicalMax+44, NotRange.Usage+56
                        ushort numVals = caps.NumberInputValueCaps;
                        byte[] valBuf = new byte[72 * numVals];
                        HidP_GetValueCaps(0, valBuf, ref numVals, ppd);
                        Console.WriteLine($"  Input Values ({numVals}):");
                        for (int v = 0; v < numVals; v++)
                        {
                            int o = v * 72;
                            ushort usagePage = BitConverter.ToUInt16(valBuf, o + 0);
                            byte reportId = valBuf[o + 2];
                            bool isRange = valBuf[o + 12] != 0;
                            ushort bitSize = BitConverter.ToUInt16(valBuf, o + 18);
                            ushort reportCount = BitConverter.ToUInt16(valBuf, o + 20);
                            int logMin = BitConverter.ToInt32(valBuf, o + 40);
                            int logMax = BitConverter.ToInt32(valBuf, o + 44);
                            ushort usage = BitConverter.ToUInt16(valBuf, o + 56);
                            Console.WriteLine($"    [{v}] Page=0x{usagePage:X4} Usage=0x{usage:X4} Bits={bitSize} Range=[{logMin}..{logMax}] ID={reportId}");
                        }
                    }
                    if (caps.NumberFeatureValueCaps > 0)
                    {
                        ushort numFeatVals = caps.NumberFeatureValueCaps;
                        byte[] featValBuf = new byte[72 * numFeatVals];
                        HidP_GetValueCaps(2, featValBuf, ref numFeatVals, ppd);
                        Console.WriteLine($"  Feature Values ({numFeatVals}):");
                        for (int v = 0; v < numFeatVals; v++)
                        {
                            int o = v * 72;
                            ushort usagePage = BitConverter.ToUInt16(featValBuf, o + 0);
                            ushort usage = BitConverter.ToUInt16(featValBuf, o + 56);
                            ushort bitSize = BitConverter.ToUInt16(featValBuf, o + 18);
                            Console.WriteLine($"    [{v}] Page=0x{usagePage:X4} Usage=0x{usage:X4} Bits={bitSize}");
                        }
                    }
                    // Dump button caps
                    if (caps.NumberInputButtonCaps > 0)
                    {
                        ushort numBtns = caps.NumberInputButtonCaps;
                        byte[] btnBuf = new byte[72 * numBtns];
                        HidP_GetButtonCaps(0, btnBuf, ref numBtns, ppd); // 0 = HidP_Input
                        Console.WriteLine($"  Input Buttons ({numBtns}):");
                        for (int b = 0; b < numBtns; b++)
                        {
                            int o = b * 72;
                            ushort usagePage = BitConverter.ToUInt16(btnBuf, o + 0);
                            bool isRange = btnBuf[o + 6] != 0;
                            if (isRange)
                            {
                                ushort usageMin = BitConverter.ToUInt16(btnBuf, o + 16);
                                ushort usageMax = BitConverter.ToUInt16(btnBuf, o + 18);
                                Console.WriteLine($"    [{b}] Page=0x{usagePage:X4} Buttons {usageMin}-{usageMax}");
                            }
                            else
                            {
                                ushort usage = BitConverter.ToUInt16(btnBuf, o + 16);
                                Console.WriteLine($"    [{b}] Page=0x{usagePage:X4} Button {usage}");
                            }
                        }
                    }
                    Console.WriteLine();
                }
                HidD_FreePreparsedData(ppd);
            }
            handle.Dispose();
        }
        SetupDiDestroyDeviceInfoList(dis);
        return 0;
    }

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool HidD_GetProductString(SafeFileHandle HidDeviceObject, byte[] Buffer, uint BufferLength);

    // Security for shared memory
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool InitializeSecurityDescriptor(byte[] pSecurityDescriptor, uint dwRevision);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetSecurityDescriptorDacl(byte[] pSecurityDescriptor, bool bDaclPresent, IntPtr pDacl, bool bDaclDefaulted);

    // Shared memory for driver data injection
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr lpAttributes,
        uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
        uint dwFileOffsetHigh, uint dwFileOffsetLow, uint dwNumberOfBytesToMap);

    // Win32 message pump for WGI
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    // ── Cleanup ──

    static int RunCleanup()
    {
        Console.WriteLine("Running cleanup script...\n");
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -File \"{Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "cleanup.ps1")}\"",
            UseShellExecute = false
        };

        // Try direct path
        string scriptPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "cleanup.ps1"));
        if (!File.Exists(scriptPath))
        {
            scriptPath = @"C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\scripts\cleanup.ps1";
        }

        Console.WriteLine($"  Script: {scriptPath}");
        psi.Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\"";

        using var proc = Process.Start(psi);
        proc?.WaitForExit(30_000);
        return 0;
    }
}
