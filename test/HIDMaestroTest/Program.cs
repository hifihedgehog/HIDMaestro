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

            // Add xinputhid as upper filter so the device appears in XInput
            InjectXInputFilter(childInst);
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
    static void InjectXInputFilter(uint childDevInst)
    {
        // Get the device instance ID from the devnode
        // Use CM_Get_Device_IDW to get the instance path
        byte[] idBuf = new byte[512];
        CM_Get_Device_IDW(childDevInst, idBuf, (uint)idBuf.Length / 2, 0);
        string instanceId = Encoding.Unicode.GetString(idBuf).TrimEnd('\0');
        if (string.IsNullOrEmpty(instanceId)) return;

        // Parse instance ID: HID\HIDCLASS\1&xxx&yyy&0000
        string[] parts = instanceId.Split('\\');
        if (parts.Length < 3) return;

        // Open the device's enum registry key
        string regPath = $@"SYSTEM\CurrentControlSet\Enum\{parts[0]}\{parts[1]}\{parts[2]}";
        using var key = Registry.LocalMachine.OpenSubKey(regPath, writable: true);
        if (key == null) return;

        // Read existing UpperFilters
        var existing = key.GetValue("UpperFilters") as string[];
        if (existing != null && existing.Any(f => f.Equals("xinputhid", StringComparison.OrdinalIgnoreCase)))
            return; // Already has xinputhid

        // Add xinputhid
        var newFilters = (existing ?? Array.Empty<string>()).ToList();
        newFilters.Add("xinputhid");
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
                if (bTag == 8) { currentReportId = value; hasReportIds = true; } // Report ID
            }
            else if (bType == 0) // Main
            {
                if (bTag == 8) // Input
                {
                    if (!hasReportIds || currentReportId == 1)
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

        // NOTE: Do NOT write OEMName to the Joystick\OEM registry key.
        // That key is indexed by VID/PID and affects ALL devices with the same
        // VID/PID, including real physical controllers. The device name is set
        // via CM_Set_DevNode_PropertyW on our specific device node instead.
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

    static bool IsDriverInStore()
    {
        var (_, output) = RunProcess("pnputil.exe", "/enum-drivers");
        return output.Contains("hidmaestro.inf");
    }

    static bool EnsureDriverInstalled()
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
        Console.Write("  Restarting device... ");
        // Find our root device dynamically
        string? rootInstId = null;
        for (int idx = 0; idx < 10; idx++)
        {
            string candidate = $@"ROOT\HID_IG_00\{idx:D4}";
            if (CM_Locate_DevNodeW(out uint _, candidate, 0) == 0)
            { rootInstId = candidate; break; }
        }
        if (rootInstId != null)
            RunProcess("pnputil.exe", $"/restart-device \"{rootInstId}\"");
        Thread.Sleep(3000);
        Console.WriteLine("OK");

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

        // Remove existing nodes
        RunProcess("pnputil.exe", "/remove-device \"ROOT\\HIDCLASS\\0000\" /subtree");
        RunProcess("pnputil.exe", "/remove-device \"ROOT\\HIDCLASS\\0001\" /subtree");
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

        // Step 1: Write descriptor to registry FIRST
        int inputReportLen = ComputeInputReportByteLength(descriptor);
        Console.Write($"  Writing profile to registry (InputReport={inputReportLen}B)... ");
        WriteConfig(descriptor, profile.VendorId, profile.ProductId,
            productString: profile.ProductString,
            deviceDescription: profile.DeviceDescription,
            inputReportByteLength: inputReportLen);
        Console.WriteLine("OK");

        // Step 2: Build, sign, install driver + create device node
        // The driver will read the registry config we just wrote
        if (!EnsureDriverInstalled())
            return Error("Driver build/install failed. Run elevated.");

        // Step 3: Wait for HID child + xinputhid to fully load, then fix name
        Thread.Sleep(3000); // Wait for HID child + xinputhid to enumerate
        // xinputhid overrides the HID child name to "Xbox Wireless Controller"
        // We override it back to match the profile's deviceDescription
        Console.Write("  Fixing device name... ");
        {
            string fixName = profile.DeviceDescription ?? profile.ProductString;
            // Find our root device dynamically (instance ID increments on recreate)
            bool nameSet = false;
            for (int idx = 0; idx < 10; idx++)
            {
                string rootId = $@"ROOT\HID_IG_00\{idx:D4}";
                if (CM_Locate_DevNodeW(out uint rootI, rootId, 0) == 0)
                {
                    if (CM_Get_Child(out uint childI, rootI, 0) == 0)
                    {
                        var fKey = new DEVPROPKEY { fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), pid = 14 };
                        var bKey = new DEVPROPKEY { fmtid = new Guid(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2), pid = 4 };
                        byte[] nb = Encoding.Unicode.GetBytes(fixName + "\0");
                        // Set on root
                        CM_Set_DevNode_PropertyW(rootI, ref fKey, 0x12, nb, (uint)nb.Length, 0);
                        CM_Set_DevNode_PropertyW(rootI, ref bKey, 0x12, nb, (uint)nb.Length, 0);
                        // Set on all children via both CM property AND direct registry
                        // (xinputhid's cached value overrides CM property, but registry persists)
                        uint curInst = childI;
                        do {
                            CM_Set_DevNode_PropertyW(curInst, ref fKey, 0x12, nb, (uint)nb.Length, 0);
                            CM_Set_DevNode_PropertyW(curInst, ref bKey, 0x12, nb, (uint)nb.Length, 0);
                            // Direct registry write — survives xinputhid's name override
                            char[] instIdBuf = new char[256];
                            uint instIdLen = (uint)instIdBuf.Length;
                            if (CM_Get_Device_IDW(curInst, instIdBuf, instIdLen, 0) == 0)
                            {
                                string childInstId = new string(instIdBuf).TrimEnd('\0');
                                using var childKey = Registry.LocalMachine.OpenSubKey(
                                    $@"SYSTEM\CurrentControlSet\Enum\{childInstId}", true);
                                if (childKey != null)
                                    childKey.SetValue("FriendlyName", fixName, RegistryValueKind.String);
                            }
                        } while (CM_Get_Sibling(out curInst, curInst, 0) == 0);
                        nameSet = true;
                    }
                }
            }
            Console.WriteLine(nameSet ? "OK" : "FAILED (device not found)");
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

        // Open the XUSB device for XInput input injection
        Console.Write("  Opening XUSB device... ");
        SafeFileHandle? xh = null;
        {
            HidD_GetHidGuid(out Guid _); // just to init
            var xusbGuid = new Guid("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");
            IntPtr dis = SetupDiGetClassDevsW(ref xusbGuid, IntPtr.Zero, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (dis != new IntPtr(-1))
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (SetupDiEnumDeviceInterfaces(dis, IntPtr.Zero, ref xusbGuid, 0, ref did))
                {
                    SetupDiGetDeviceInterfaceDetailW(dis, ref did, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
                    IntPtr detail = Marshal.AllocHGlobal((int)reqSize);
                    Marshal.WriteInt32(detail, 8);
                    if (SetupDiGetDeviceInterfaceDetailW(dis, ref did, detail, reqSize, out _, IntPtr.Zero))
                    {
                        string xpath = Marshal.PtrToStringUni(detail + 4)!;
                        xh = CreateFileW(xpath, GENERIC_READ | GENERIC_WRITE,
                            FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                        if (xh.IsInvalid) xh = null;
                    }
                    Marshal.FreeHGlobal(detail);
                }
                SetupDiDestroyDeviceInfoList(dis);
            }
        }
        Console.WriteLine(xh != null ? "OK" : "not found (XInput won't get live data)");

        // Parse the descriptor to build a generic input report packer
        // Parse the ORIGINAL profile descriptor (before Feature Report injection)
        var reportBuilder = HidReportBuilder.Parse(profileDesc ?? descriptor);
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
            sharedFile = new FileStream(sharedFilePath, FileMode.Create, FileAccess.ReadWrite,
                FileShare.ReadWrite, 72, FileOptions.WriteThrough);
            // Pre-allocate 72 bytes
            sharedFile.SetLength(72);
            sharedFile.Flush();
            Console.WriteLine("  Shared file: OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Shared file: FAILED ({ex.Message})");
        }

        // XUSB input: piggybacked on GET_STATE
        byte[] xusbInput = new byte[17];
        xusbInput[0] = 0x01; xusbInput[1] = 0x01; xusbInput[2] = 0x00;
        byte[] xusbOutput = new byte[29];

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

            byte[] inputReport = reportBuilder.BuildReport(
                leftX: lxNorm, leftY: lyNorm,
                rightX: 0.5, rightY: 0.5,
                leftTrigger: ltVal, rightTrigger: rtVal,
                hatValue: 0, buttonMask: btnMask);

            // Write input data to shared file (driver reads via timer)
            if (sharedFile != null)
            {
                int dataStart = reportBuilder.InputReportId != 0 ? 1 : 0;
                int dataLen = Math.Min(inputReport.Length - dataStart, 64);
                sharedMemSeqNo++;
                sharedFile.Seek(0, SeekOrigin.Begin);
                // Layout: SeqNo(4) + DataSize(4) + Data(64) = 72 bytes
                sharedFile.Write(BitConverter.GetBytes(sharedMemSeqNo));
                sharedFile.Write(BitConverter.GetBytes(dataLen));
                sharedFile.Write(inputReport, dataStart, dataLen);
                sharedFile.Flush();
            }
            bool ok = true;

            // Send to XUSB device (XInput) — piggyback on GET_STATE
            if (xh != null)
            {
                int copyOfs = reportBuilder.InputReportId != 0 ? 1 : 0;
                int copyLen = Math.Min(14, inputReport.Length - copyOfs);
                Array.Copy(inputReport, copyOfs, xusbInput, 3, Math.Min(copyLen, xusbInput.Length - 3));
                DeviceIoControl(xh, 0x8000E00C, xusbInput, (uint)xusbInput.Length,
                    xusbOutput, (uint)xusbOutput.Length, out _, IntPtr.Zero);
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
            if (count % 100 == 0)
                Console.Write($"\r  Reports: {count}  t={t:F1}s  ");

            Thread.Sleep(4); // ~250 Hz
        }

        Console.WriteLine($"\n\n  Sent {count} reports.");
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
