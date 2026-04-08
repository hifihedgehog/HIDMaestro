using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
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

    /// <summary>Returns the per-instance registry path for a controller index.</summary>
    static string RegPathForIndex(int index) => $@"SOFTWARE\HIDMaestro\Controller{index}";

    // ── P/Invoke: CfgMgr32 for device property setting ──

    [DllImport("CfgMgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("CfgMgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint CM_Register_Device_InterfaceW(uint dnDevInst, ref Guid InterfaceClassGuid, string? pszReference, IntPtr pszDeviceInterface, ref uint pulLength, uint ulFlags);

    [DllImport("CfgMgr32.dll", SetLastError = true)]
    static extern uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", SetLastError = true)]
    static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

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

    [DllImport("CfgMgr32.dll")]
    static extern uint CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

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

    static void SetDeviceDescription(string instanceId, string description)
    {
        // CM_Set_DevNode_PropertyW for DeviceDesc is overridden by INF string reference.
        // Write directly to the Enum registry key instead.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{instanceId}", writable: true);
            if (key != null)
            {
                key.SetValue("DeviceDesc", description, RegistryValueKind.String);
                key.SetValue("FriendlyName", description, RegistryValueKind.String);
            }
            // Also set on HID child
            if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) == 0
                && CM_Get_Child(out uint childInst, devInst, 0) == 0)
            {
                uint idLen = 0;
                CM_Get_Device_ID_Size(out idLen, childInst, 0);
                var buf = new char[idLen + 1];
                CM_Get_Device_IDW(childInst, buf, (uint)buf.Length, 0);
                string childId = new string(buf, 0, (int)idLen);
                using var childKey = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Enum\{childId}", writable: true);
                if (childKey != null)
                {
                    childKey.SetValue("DeviceDesc", description, RegistryValueKind.String);
                    childKey.SetValue("FriendlyName", description, RegistryValueKind.String);
                }
            }
        }
        catch { }
    }

    [DllImport("CfgMgr32.dll")]
    static extern uint CM_Get_Device_ID_Size(out uint pulLen, uint dnDevInst, uint ulFlags);

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
    /// Adds HMBtnFix UMDF2 filter on the HID child to patch xinputhid's 16-button
    /// descriptor to 10 buttons. Configures WUDFRd service parameters.
    /// </summary>
    static void AddBtnFixFilter(string hidChildInstanceId)
    {
        string[] parts = hidChildInstanceId.Split('\\');
        if (parts.Length < 3) return;
        string enumPath = $@"SYSTEM\CurrentControlSet\Enum\{parts[0]}\{parts[1]}\{parts[2]}";

        // Find HMBtnFix.dll in the driver store
        string? btnFixDll = null;
        string storePath = @"C:\Windows\System32\DriverStore\FileRepository";
        foreach (var dir in Directory.GetDirectories(storePath, "hidmaestro_btnfix*"))
        {
            string candidate = Path.Combine(dir, "HMBtnFix.dll");
            if (File.Exists(candidate)) { btnFixDll = candidate; break; }
        }
        if (btnFixDll == null)
        {
            // Try build directory
            btnFixDll = Path.Combine(BuildDir, "HMBtnFix.dll");
            if (!File.Exists(btnFixDll)) { Console.WriteLine("  BtnFix: DLL not found"); return; }
        }

        // 1. Create UMDF service entry
        using (var svcKey = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\HMBtnFix"))
        {
            svcKey.SetValue("DisplayName", "HIDMaestro Button Fix", RegistryValueKind.String);
            svcKey.SetValue("Type", 1, RegistryValueKind.DWord);
            svcKey.SetValue("Start", 3, RegistryValueKind.DWord);
            svcKey.SetValue("ErrorControl", 1, RegistryValueKind.DWord);
        }
        using (var wdfKey = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\HMBtnFix\Parameters\Wdf"))
        {
            wdfKey.SetValue("WdfMajorVersion", 2, RegistryValueKind.DWord);
            wdfKey.SetValue("WdfMinorVersion", 15, RegistryValueKind.DWord);
        }

        // 2. Configure WUDFRd on the device's parameters
        using (var dpKey = Registry.LocalMachine.CreateSubKey($@"{enumPath}\Device Parameters\WudfRd"))
        {
            // UMDF service definition
            using (var svcDef = Registry.LocalMachine.CreateSubKey($@"{enumPath}\Device Parameters\WudfRd\UmdfService\HMBtnFix"))
            {
                svcDef.SetValue("UmdfLibraryVersion", "2.15.0", RegistryValueKind.String);
                svcDef.SetValue("ServiceBinary", btnFixDll, RegistryValueKind.String);
            }
            dpKey.SetValue("UmdfServiceOrder", new string[] { "HMBtnFix" }, RegistryValueKind.MultiString);
            dpKey.SetValue("UmdfKernelModeClientPolicy", 1, RegistryValueKind.DWord); // AllowKernelModeClients
            dpKey.SetValue("UmdfFileObjectPolicy", 2, RegistryValueKind.DWord); // AllowNullAndUnknownFileObjects
            dpKey.SetValue("UmdfMethodNeitherAction", 1, RegistryValueKind.DWord); // Copy
            dpKey.SetValue("UmdfFsContextUsePolicy", 1, RegistryValueKind.DWord); // CanUseFsContext2
        }

        // 3. Add WUDFRd to UpperFilters
        using (var key = Registry.LocalMachine.OpenSubKey(enumPath, writable: true))
        {
            if (key == null) return;
            var existing = key.GetValue("UpperFilters") as string[];
            if (existing != null && existing.Contains("WUDFRd")) return; // Already present
            var newFilters = (existing ?? Array.Empty<string>()).ToList();
            newFilters.Add("WUDFRd"); // Add after xinputhid
            key.SetValue("UpperFilters", newFilters.ToArray(), RegistryValueKind.MultiString);
        }

        Console.Write($"  BtnFix filter configured on {hidChildInstanceId}... ");
    }

    // ── P/Invoke ──

    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")]
    static extern uint timeEndPeriod(uint uPeriod);

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
    static volatile bool _ghostsCleanedThisSession = false;
    static volatile string? _switchToProfile = null;

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


    static int Main(string[] args)
    {
        Console.WriteLine("=== HIDMaestro Test Client ===\n");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { RemoveAllHIDMaestroDevices(); } catch { } };

        // Safety net: clean up devices if the process exits unexpectedly
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { CleanupGhostDevices(); } catch { }
            try { CleanupSharedMappings(); } catch { }
        };

            // Single-instance: kill any other HIDMaestroTest processes
        int myPid = Environment.ProcessId;
        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("HIDMaestroTest"))
        {
            if (proc.Id != myPid)
            {
                try { proc.Kill(); proc.WaitForExit(3000); } catch { }
            }
        }
        // Elevate immediately — everything we do needs admin
        if (!IsElevated())
        {
            Console.WriteLine("  Requesting elevation (admin required)...\n");
            return RelaunchElevated(args);
        }

        // Clean up any leftover devices from previous sessions (now elevated)
        RemoveAllHIDMaestroDevices();
        DeviceManager.RemoveOrphanHidChildren();
        CleanStaleXusbInterfaces();
        DisableGhostXusbInterfaces();

        if (args.Length == 0)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  HIDMaestroTest emulate <id>      Emulate any controller profile");
            Console.WriteLine("  HIDMaestroTest list              List all controller profiles");
            Console.WriteLine("  HIDMaestroTest search <query>    Search profiles by name/vendor");
            Console.WriteLine("  HIDMaestroTest info <id>         Show profile details");
            Console.WriteLine("  HIDMaestroTest cleanup           Remove everything");
            Console.WriteLine("\nMust run elevated for config writes + device restart.");
            return 1;
        }

        return args[0].ToLower() switch
        {
            "emulate" => EmulateWithSwitching(args.Skip(1).ToArray()),
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

    static void WriteConfig(byte[] descriptor, ushort vid, ushort pid, ushort ver = 0x0100, string? productString = null, string? deviceDescription = null, int inputReportByteLength = 0, bool functionMode = false, int controllerIndex = 0)
    {
        // Write to per-instance key AND legacy global key (for backward compat)
        string instanceRegPath = RegPathForIndex(controllerIndex);
        using var key = Registry.LocalMachine.CreateSubKey(instanceRegPath);
        // Also write to legacy global key for single-instance fallback
        using var legacyKey = Registry.LocalMachine.CreateSubKey(REG_PATH);
        string instanceSuffix = controllerIndex > 0 ? $"\\{controllerIndex:D4}" : "\\0000";
        key.SetValue("DeviceInstanceId", $@"ROOT\VID_{vid:X4}&PID_{pid:X4}&IG_00{instanceSuffix}", RegistryValueKind.String);
        key.SetValue("FunctionMode", functionMode ? 1 : 0, RegistryValueKind.DWord);
        // Copy to legacy key for index 0
        if (controllerIndex == 0)
        {
            legacyKey.SetValue("DeviceInstanceId", key.GetValue("DeviceInstanceId")!, RegistryValueKind.String);
            legacyKey.SetValue("FunctionMode", functionMode ? 1 : 0, RegistryValueKind.DWord);
        }
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

        // Write OEMName to Joystick\OEM registry (display name in joy.cpl).
        string oemKeyPath = $@"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{vid:X4}&PID_{pid:X4}";
        try
        {
            using var oemLm = Registry.LocalMachine.CreateSubKey(oemKeyPath);
            oemLm?.SetValue("OEMName", displayName, RegistryValueKind.String);
            // Remove any stale OEMData/Axes entries from previous versions
            oemLm?.DeleteValue("OEMData", false);
            try { Registry.LocalMachine.DeleteSubKeyTree($@"{oemKeyPath}\Axes", false); } catch { }
        }
        catch { }
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

    /// <summary>Removes ALL HIDMaestro virtual devices, companions, and interface registries.</summary>
    static void RemoveAllHIDMaestroDevices()
    {
        // Remove ALL HIDMaestro devices by scanning ROOT enumerators in the registry.
        // Enumerators we always own: VID_*, XnaComposite, HMCompanion, HID_IG_00
        // Shared enumerators (HIDCLASS, SYSTEM): verify hardware ID contains "HIDMaestro"
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT");
            if (enumKey != null)
            {
                foreach (var sub in enumKey.GetSubKeyNames())
                {
                    bool alwaysOurs = sub.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
                        || sub.Equals("XnaComposite", StringComparison.OrdinalIgnoreCase)
                        || sub.Equals("HMCompanion", StringComparison.OrdinalIgnoreCase)
                        || sub.Equals("HID_IG_00", StringComparison.OrdinalIgnoreCase);

                    bool shared = sub.Equals("HIDCLASS", StringComparison.OrdinalIgnoreCase)
                        || sub.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase);

                    if (!alwaysOurs && !shared) continue;

                    using var subKey = enumKey.OpenSubKey(sub);
                    if (subKey == null) continue;

                    foreach (var inst in subKey.GetSubKeyNames())
                    {
                        string instId = $@"ROOT\{sub}\{inst}";

                        if (alwaysOurs)
                        {
                            bool removed = DeviceManager.RemoveDevice(instId, timeoutMs: 5000);
                            Console.Error.WriteLine($"    Cleanup: {instId} → {(removed ? "removed" : "FAILED")}");
                            continue;
                        }

                        // Shared: only remove if hardware ID contains "HIDMaestro"
                        try
                        {
                            using var devKey = subKey.OpenSubKey(inst);
                            var hwIds = devKey?.GetValue("HardwareID") as string[];
                            if (hwIds?.Any(h => h.Contains("HIDMaestro", StringComparison.OrdinalIgnoreCase)) == true)
                                DeviceManager.RemoveDevice(instId, timeoutMs: 3000);
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }

        // Also remove orphaned HID children (survive parent removal as "Unknown" devices)
        try
        {
            using var hidEnum = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\HID");
            if (hidEnum != null)
            {
                foreach (var sub in hidEnum.GetSubKeyNames())
                {
                    // Our HID children have enumerator names like VID_045E&PID_028E&IG_00,
                    // HIDCLASS, or HID_IG_00
                    bool couldBeOurs = sub.StartsWith("VID_045E", StringComparison.OrdinalIgnoreCase)
                        || sub.Equals("HIDCLASS", StringComparison.OrdinalIgnoreCase)
                        || sub.StartsWith("HID_IG", StringComparison.OrdinalIgnoreCase);
                    if (!couldBeOurs) continue;

                    using var childEnum = hidEnum.OpenSubKey(sub);
                    if (childEnum == null) continue;
                    foreach (var inst in childEnum.GetSubKeyNames())
                    {
                        string childId = $@"HID\{sub}\{inst}";
                        // Only remove if the device is Unknown/Error (orphaned) or has HIDMaestro in parent
                        if (CM_Locate_DevNodeW(out uint childInst, childId, 0) == 0)
                        {
                            // Check if parent still exists
                            bool parentGone = CM_Get_Parent(out uint parentInst, childInst, 0) != 0;
                            if (parentGone)
                                DeviceManager.RemoveDevice(childId, timeoutMs: 3000);
                        }
                        else if (CM_Locate_DevNodeW(out childInst, childId, 1) == 0) // phantom
                        {
                            DeviceManager.RemoveDevice(childId, timeoutMs: 3000);
                        }
                    }
                }
            }
        }
        catch { }

        // Clean Device Parameters under our enumerators (removes stale ControllerIndex,
        // interface registrations, etc.) but leave the PnP-owned instance keys intact.
        {
            string[] ourEnumerators = { "VID_", "XnaComposite", "HMCompanion", "HID_IG_00" };
            try
            {
                using var enumRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT");
                if (enumRoot != null)
                {
                    foreach (var sub in enumRoot.GetSubKeyNames())
                    {
                        bool ours = ourEnumerators.Any(e => sub.StartsWith(e, StringComparison.OrdinalIgnoreCase)
                            || sub.Equals(e, StringComparison.OrdinalIgnoreCase));
                        if (!ours) continue;
                        using var subKey = enumRoot.OpenSubKey(sub);
                        if (subKey == null) continue;
                        foreach (var inst in subKey.GetSubKeyNames())
                        {
                            // Delete Device Parameters (our data) via reg.exe (PnP ACLs)
                            string dpPath = $@"SYSTEM\CurrentControlSet\Enum\ROOT\{sub}\{inst}\Device Parameters";
                            RunProcess("reg.exe", $"delete \"HKLM\\{dpPath}\" /f", timeoutMs: 3000);
                        }
                    }
                }
            }
            catch { }
        }

        // Clean interface registries (XUSB + WinExInput)
        foreach (var guid in new[] { "{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}", "{6c53d5fd-6480-440f-b618-476750c5e1a6}" })
        {
            try
            {
                using var classKey = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Control\DeviceClasses\{guid}", writable: true);
                if (classKey != null)
                    foreach (var sub in classKey.GetSubKeyNames())
                        if (sub.Contains("ROOT#"))
                            try { classKey.DeleteSubKeyTree(sub); } catch { }
            }
            catch { }
        }

        // Clean joy.cpl joystick cache (OEM entries + slot assignments)
        // Delete ALL our OEM entries (VID_045E&PID_* and VID_054C&PID_*) from Joystick\OEM.
        // Over many test runs, dozens of different PIDs accumulate and fill all 16 joystick slots.
        string[] oemPrefixes = { "VID_045E&PID_", "VID_054C&PID_", "VID_0000&PID_" };
        string oemRelPath = @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var oemKey = root.OpenSubKey(oemRelPath, writable: true);
                if (oemKey != null)
                    foreach (var sub in oemKey.GetSubKeyNames())
                        if (oemPrefixes.Any(p => sub.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                            try { oemKey.DeleteSubKeyTree(sub, false); } catch { }
            }
            catch { }
        }
        // Remove ALL joystick slot assignments so DirectInput reassigns fresh slots.
        // Each slot has JoystickNOEMName + JoystickNConfiguration values (N = 1..16).
        // Deleting all of them resets the entire slot table; Windows rebuilds on next enumeration.
        // We must clean BOTH CurrentJoystickSettings AND per-VID/PID JoystickSettings subkeys.
        try
        {
            using var jsRoot = Registry.CurrentUser.OpenSubKey(
                @"System\CurrentControlSet\Control\MediaResources\Joystick", writable: true);
            if (jsRoot != null)
                foreach (var sub in jsRoot.GetSubKeyNames())
                {
                    // Clean CurrentJoystickSettings (the active slot table).
                    try
                    {
                        using var settings = jsRoot.OpenSubKey($@"{sub}\CurrentJoystickSettings", writable: true);
                        if (settings != null)
                            foreach (var name in settings.GetValueNames())
                            {
                                if (name.StartsWith("Joystick", StringComparison.OrdinalIgnoreCase) &&
                                    (name.Contains("OEMName", StringComparison.OrdinalIgnoreCase) ||
                                     name.Contains("Configuration", StringComparison.OrdinalIgnoreCase)))
                                    settings.DeleteValue(name, false);
                            }
                    }
                    catch { }
                    // Clean per-VID/PID JoystickSettings subkeys (cached slot history).
                    // DirectInput reads these to restore previous slot assignments; stale entries
                    // cause new devices to land at high slot numbers (e.g. Joy11 instead of Joy1).
                    try
                    {
                        using var jsSettings = jsRoot.OpenSubKey($@"{sub}\JoystickSettings", writable: true);
                        if (jsSettings != null)
                            foreach (var vidPid in jsSettings.GetSubKeyNames())
                            {
                                try
                                {
                                    using var vidPidKey = jsSettings.OpenSubKey(vidPid, writable: true);
                                    if (vidPidKey == null) continue;
                                    foreach (var name in vidPidKey.GetValueNames())
                                    {
                                        if (name.StartsWith("Joystick", StringComparison.OrdinalIgnoreCase) &&
                                            (name.Contains("OEMName", StringComparison.OrdinalIgnoreCase) ||
                                             name.Contains("Configuration", StringComparison.OrdinalIgnoreCase)))
                                            vidPidKey.DeleteValue(name, false);
                                    }
                                }
                                catch { }
                            }
                    }
                    catch { }
                }
        }
        catch { }

        // Wait for WUDFHost processes to release our DLLs.
        // After device removal, WUDFHost may still hold DLLs in memory.
        // New devices would reuse the cached DLL instead of loading from the store.
        // Wait for any WUDFHost hosting our DLLs to exit before installing new packages.
        try
        {
            string[] ourDlls = { "HIDMaestro.dll", "HMXInput.dll", "HIDMaestroCompanion.dll" };
            foreach (var wudf in Process.GetProcessesByName("WUDFHost"))
            {
                try
                {
                    bool hostsOurs = false;
                    foreach (ProcessModule mod in wudf.Modules)
                    {
                        if (ourDlls.Any(d => mod.ModuleName.Equals(d, StringComparison.OrdinalIgnoreCase)))
                        { hostsOurs = true; break; }
                    }
                    if (hostsOurs)
                        wudf.WaitForExit(10000); // Wait for process to exit after device removal
                }
                catch { } // Access denied for other users' WUDFHost — skip
            }
        }
        catch { }

        // Remove driver packages from store
        try
        {
            var (_, drivers) = RunProcess("pnputil.exe", "/enum-drivers", timeoutMs: 10_000);
            string? currentOem = null;
            foreach (var line in drivers.Split('\n'))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"(oem\d+\.inf)");
                if (match.Success) currentOem = match.Groups[1].Value;
                if (currentOem != null && line.Contains("HIDMaestro", StringComparison.OrdinalIgnoreCase))
                {
                    RunProcess("pnputil.exe", $"/delete-driver {currentOem} /force", timeoutMs: 5000);
                    currentOem = null;
                }
                if (string.IsNullOrWhiteSpace(line)) currentOem = null;
            }
        }
        catch { }

        // Clear registry config (stale values like XusbNeeded cause duplicate XInput slots)
        try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\HIDMaestro", false); } catch { }
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
            // Combined Z for DI (axis 4) + hidden Vx/Vy for WGI separate triggers (axes 5/6).
            // DI ignores velocity usages (Vx=0x40, Vy=0x41) → 5 axes.
            // GameInput sees all HID axes → Vx at index 5, Vy at index 6.
            SetAxis("LeftTrigger", 5);
            SetAxis("RightTrigger", 6);
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

    // ── Shared memory IPC (replaces high-frequency file writes) ──
    // Per-controller named section in Global\ namespace, pagefile-backed.
    // Layout matches HIDMAESTRO_SHARED_INPUT in driver.h: 86 bytes.
    //   [0..3]   ULONG SeqNo (volatile, seqlock — odd = mid-write)
    //   [4..7]   ULONG DataSize
    //   [8..71]  UCHAR Data[64]
    //   [72..85] UCHAR GipData[14]

    const int SHARED_INPUT_SIZE = 86;
    static readonly Dictionary<int, IntPtr> _mappingHandles = new();
    static readonly Dictionary<int, IntPtr> _mappingViews = new();

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public uint nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr lpAttributes,
        uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
        uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string StringSecurityDescriptor, uint StringSDRevision,
        out IntPtr SecurityDescriptor, IntPtr SecurityDescriptorSize);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Returns the view pointer for the given controller's shared mapping.
    /// Creates the mapping if it doesn't exist yet. Returned pointer is valid for
    /// the lifetime of the process (until cleanup at exit).</summary>
    static IntPtr EnsureSharedMapping(int controllerIndex)
    {
        if (_mappingViews.TryGetValue(controllerIndex, out IntPtr existing))
            return existing;

        string name = $@"Global\HIDMaestroInput{controllerIndex}";

        // SDDL: SYSTEM full, Admins full, LocalService read, World read.
        // LocalService is what WUDFHost runs as for UMDF2 reflector.
        const string sddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;LS)(A;;GR;;;WD)";
        IntPtr sd = IntPtr.Zero;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out sd, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception();

        SECURITY_ATTRIBUTES sa = new()
        {
            nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = sd,
            bInheritHandle = 0,
        };
        IntPtr saPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_ATTRIBUTES>());
        Marshal.StructureToPtr(sa, saPtr, false);

        IntPtr hMap;
        try
        {
            hMap = CreateFileMappingW(new IntPtr(-1), saPtr,
                0x04 /* PAGE_READWRITE */, 0, SHARED_INPUT_SIZE, name);
        }
        finally
        {
            Marshal.FreeHGlobal(saPtr);
            LocalFree(sd);
        }

        if (hMap == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();

        IntPtr view = MapViewOfFile(hMap, 0x04 /* FILE_MAP_WRITE */ | 0x02 /* FILE_MAP_READ */,
            0, 0, (UIntPtr)SHARED_INPUT_SIZE);
        if (view == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            CloseHandleNative(hMap);
            throw new System.ComponentModel.Win32Exception(err);
        }

        // Zero-init (CreateFileMapping pages start zero, but be explicit).
        // Importantly, set SeqNo to 0 — the driver detects "no change" on equal seq.
        for (int i = 0; i < SHARED_INPUT_SIZE; i++)
            Marshal.WriteByte(view, i, 0);

        _mappingHandles[controllerIndex] = hMap;
        _mappingViews[controllerIndex] = view;
        return view;
    }

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")]
    static extern bool CloseHandleNative(IntPtr hObject);

    /// <summary>Atomically publishes a new shared-input snapshot using a seqlock.
    /// Single-writer (input loop) → many-readers (driver + companion) is safe lock-free.</summary>
    static void WriteSharedInput(IntPtr view, ref uint seqNo, byte[] data, int dataLen, byte[] gipData)
    {
        // 1. Mark write in progress (odd seqNo)
        uint pending = seqNo + 1;
        Marshal.WriteInt32(view, 0, (int)pending);
        Thread.MemoryBarrier();

        // 2. Write payload (DataSize, Data, GipData)
        Marshal.WriteInt32(view, 4, dataLen);
        for (int i = 0; i < 64; i++)
            Marshal.WriteByte(view, 8 + i, i < dataLen ? data[i] : (byte)0);
        for (int i = 0; i < 14; i++)
            Marshal.WriteByte(view, 72 + i, gipData[i]);

        // 3. Mark write complete (even seqNo)
        Thread.MemoryBarrier();
        seqNo = pending + 1;
        Marshal.WriteInt32(view, 0, (int)seqNo);
    }

    /// <summary>Releases all mappings on process exit.</summary>
    static void CleanupSharedMappings()
    {
        foreach (var view in _mappingViews.Values)
            if (view != IntPtr.Zero) UnmapViewOfFile(view);
        _mappingViews.Clear();
        foreach (var h in _mappingHandles.Values)
            if (h != IntPtr.Zero) CloseHandleNative(h);
        _mappingHandles.Clear();
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
                    DeviceManager.RemoveDevice(instId);
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
                                DeviceManager.RemoveDevice(instId);
                            }
                        }
                    }
                }
            }
        }
        catch { }

        // Clean ALL ghost interface registrations — XUSB and WinExInput.
        // Both cause duplicate XInput slots if left behind.
        string[] interfaceGuids = {
            @"{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}", // XUSB
            @"{6c53d5fd-6480-440f-b618-476750c5e1a6}", // WinExInput
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

    /// <summary>
    /// Scans the XUSB and WinExInput DeviceClasses registry for HMCOMPANION entries
    /// whose backing device no longer exists (ghost/phantom gone). Deletes the stale
    /// DeviceClasses subkey so Windows stops reporting the interface as PRESENT,
    /// freeing the XInput slot.
    /// </summary>
    static int CleanStaleXusbInterfaces()
    {
        int cleaned = 0;
        string[] interfaceGuids = {
            "{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}", // XUSB
            "{6c53d5fd-6480-440f-b618-476750c5e1a6}", // WinExInput
        };

        foreach (var guid in interfaceGuids)
        {
            try
            {
                string regPath = $@"SYSTEM\CurrentControlSet\Control\DeviceClasses\{guid}";
                using var classKey = Registry.LocalMachine.OpenSubKey(regPath, writable: true);
                if (classKey == null) continue;

                foreach (var subName in classKey.GetSubKeyNames())
                {
                    // Extract the device instance ID from the subkey name.
                    // Format: ##?#ROOT#HMCOMPANION#0010#{ec87f1e3-...}
                    // or:     ##?#HID#{00001812-...}&Dev&VID_045e&...&IG_00#...#{ec87f1e3-...}
                    string devPath = subName;
                    int guidStart = devPath.LastIndexOf("#{");
                    if (guidStart >= 0)
                        devPath = devPath.Substring(0, guidStart);
                    devPath = devPath.Replace("##?#", "");
                    devPath = devPath.Replace('#', '\\');

                    bool isHmCompanion = subName.Contains("HMCOMPANION", StringComparison.OrdinalIgnoreCase);
                    bool isHidXinput = subName.Contains("HID\\", StringComparison.OrdinalIgnoreCase)
                                    || subName.Contains("HID#", StringComparison.OrdinalIgnoreCase);

                    // For HMCOMPANION: remove if the device no longer exists
                    if (isHmCompanion)
                    {
                        bool alive = CM_Locate_DevNodeW(out uint _, devPath, 0) == 0;
                        if (alive) continue;
                    }
                    // For non-ROOT HID entries (e.g. BT controller's xinputhid children):
                    // remove if the device has a problem (disconnected/off) or has xinputhid
                    // as upper filter while the controller is off.
                    else if (isHidXinput && !subName.Contains("ROOT#", StringComparison.OrdinalIgnoreCase))
                    {
                        // Check if xinputhid is the upper filter (confirms Xbox HID child)
                        bool hasXinputHid = false;
                        try
                        {
                            using var devKey = Registry.LocalMachine.OpenSubKey(
                                $@"SYSTEM\CurrentControlSet\Enum\{devPath}");
                            if (devKey != null)
                            {
                                var upperFilters = devKey.GetValue("UpperFilters") as string[];
                                hasXinputHid = upperFilters != null && upperFilters.Any(
                                    f => f.Equals("xinputhid", StringComparison.OrdinalIgnoreCase));
                            }
                        }
                        catch { }
                        if (!hasXinputHid) continue;

                        // Check if the device node is actually started and working.
                        // If not present or has a problem, it's a ghost occupying a slot.
                        bool isGhost = false;
                        if (CM_Locate_DevNodeW(out uint devInst, devPath, 0) != 0)
                        {
                            // Device not even present — definitely stale
                            isGhost = true;
                        }
                        else
                        {
                            // Device is present — check status. If it has a problem
                            // (DN_HAS_PROBLEM) or is not started, the controller is off.
                            const uint DN_HAS_PROBLEM = 0x00000400;
                            const uint DN_STARTED = 0x00000008;
                            if (CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) == 0)
                            {
                                // Ghost if device has a problem OR is not started
                                if ((status & DN_HAS_PROBLEM) != 0 || (status & DN_STARTED) == 0)
                                    isGhost = true;
                            }
                        }
                        if (!isGhost) continue;
                    }
                    else
                    {
                        // Skip entries we don't recognize
                        continue;
                    }

                    // Device is stale/ghost — nuke the interface registry entry
                    try
                    {
                        classKey.DeleteSubKeyTree(subName);
                        Console.WriteLine($"  Cleaned stale XUSB interface: {devPath}");
                        cleaned++;
                    }
                    catch (Exception ex)
                    {
                        // Registry ACLs may block — try via reg.exe as fallback
                        try
                        {
                            string fullRegPath = $@"HKLM\{regPath}\{subName}";
                            RunProcess("reg.exe", $"delete \"{fullRegPath}\" /f", timeoutMs: 3000);
                            Console.WriteLine($"  Cleaned stale XUSB interface (reg.exe): {devPath}");
                            cleaned++;
                        }
                        catch
                        {
                            Console.WriteLine($"  Warning: could not clean stale interface {devPath}: {ex.Message}");
                        }
                    }
                }
            }
            catch { }
        }

        return cleaned;
    }

    /// <summary>
    /// Disables XUSB interfaces from real BT controllers that are paired but turned off.
    /// When a BT Xbox controller is paired, Windows keeps its HID children present with
    /// xinputhid loaded. xinputhid registers XUSB interfaces on those children, consuming
    /// XInput slots even though the controller is off. This pushes HIDMaestro virtual
    /// controllers to higher slot numbers (e.g. 2,3 instead of 0,1).
    ///
    /// Fix: disable the HID child device nodes of non-ROOT Xbox controllers.
    /// This unloads xinputhid and deactivates their XUSB interfaces, freeing slots 0+.
    /// When the real BT controller connects, the BT stack re-enumerates fresh children.
    ///
    /// IMPORTANT: Only call at startup, NOT at process exit. Disabling at exit would
    /// break a real controller the user turned on during the session.
    /// </summary>
    static void DisableGhostXusbInterfaces()
    {
        try
        {
            // Strategy: find non-ROOT HID devices with xinputhid upper filter that have
            // XUSB interfaces registered. These are BT Xbox controller HID children that
            // occupy XInput slots even when the controller is off.
            // We disable the devnode AND clean the XUSB registry entries.
            using var xusbKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}");
            if (xusbKey == null) return;

            foreach (var subName in xusbKey.GetSubKeyNames())
            {
                // Only target non-ROOT HID devices (real controller's xinputhid children)
                if (subName.Contains("ROOT#", StringComparison.OrdinalIgnoreCase)) continue;
                if (!subName.Contains("HID#", StringComparison.OrdinalIgnoreCase)) continue;

                // Extract device instance ID from the registry key name
                string devPath = subName;
                int guidStart = devPath.LastIndexOf("#{");
                if (guidStart < 0) continue;
                devPath = devPath.Substring(0, guidStart); // remove #{guid}
                devPath = devPath.Replace("##?#", "");     // remove prefix
                devPath = devPath.Replace('#', '\\');

                // Verify this has xinputhid as upper filter (confirms it's an Xbox HID child)
                bool hasXinputHid = false;
                try
                {
                    using var devKey = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Enum\{devPath}");
                    if (devKey == null) continue;
                    var upperFilters = devKey.GetValue("UpperFilters") as string[];
                    hasXinputHid = upperFilters != null && upperFilters.Any(
                        f => f.Equals("xinputhid", StringComparison.OrdinalIgnoreCase));
                }
                catch { continue; }
                if (!hasXinputHid) continue;

                // Check device status — we only want to disable ghosts (off controllers)
                bool isGhost = false;
                uint devInst = 0;
                // Try locate with NORMAL flag first (present devices only)
                if (CM_Locate_DevNodeW(out devInst, devPath, 0) != 0)
                {
                    // Not present at all — try phantom (paired but removed)
                    const uint CM_LOCATE_DEVNODE_PHANTOM = 0x00000001;
                    if (CM_Locate_DevNodeW(out devInst, devPath, CM_LOCATE_DEVNODE_PHANTOM) == 0)
                        isGhost = true;
                    else
                        continue; // Can't even find it as phantom — skip
                }
                else
                {
                    // Device is present — check if it's actually started (controller on)
                    const uint DN_HAS_PROBLEM = 0x00000400;
                    const uint DN_STARTED = 0x00000008;
                    if (CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) == 0)
                    {
                        if ((status & DN_HAS_PROBLEM) != 0 || (status & DN_STARTED) == 0)
                            isGhost = true;
                        else
                            continue; // Device is started and healthy — real active controller
                    }
                    else
                    {
                        isGhost = true; // Can't get status — treat as ghost
                    }
                }

                if (!isGhost) continue;

                Console.WriteLine($"  Found ghost XUSB interface: {devPath}");

                // Approach 1: Disable the device node to unload xinputhid
                uint result = CM_Disable_DevNode(devInst, 0);
                if (result == 0)
                    Console.WriteLine($"    Disabled device node OK");
                else
                    Console.WriteLine($"    CM_Disable_DevNode returned 0x{result:X8}");

                // Approach 2: Also clean the XUSB DeviceClasses registry entry.
                // This ensures the XInput slot is freed even if CM_Disable_DevNode
                // didn't fully deactivate the interface.
                try
                {
                    string regPath = @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}";
                    using var classKey = Registry.LocalMachine.OpenSubKey(regPath, writable: true);
                    if (classKey != null)
                    {
                        try
                        {
                            classKey.DeleteSubKeyTree(subName);
                            Console.WriteLine($"    Deleted XUSB registry entry");
                        }
                        catch
                        {
                            // Try reg.exe fallback
                            try
                            {
                                string fullRegPath = $@"HKLM\{regPath}\{subName}";
                                RunProcess("reg.exe", $"delete \"{fullRegPath}\" /f", timeoutMs: 3000);
                                Console.WriteLine($"    Deleted XUSB registry entry (reg.exe)");
                            }
                            catch (Exception ex2)
                            {
                                Console.WriteLine($"    Warning: could not delete XUSB entry: {ex2.Message}");
                            }
                        }
                    }
                }
                catch { }

                // Also clean the WinExInput interface for this device
                try
                {
                    string winExPath = @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{6c53d5fd-6480-440f-b618-476750c5e1a6}";
                    using var winExKey = Registry.LocalMachine.OpenSubKey(winExPath, writable: true);
                    if (winExKey != null)
                    {
                        // Find matching entry for this device
                        string devPathHash = devPath.Replace('\\', '#');
                        foreach (var weSub in winExKey.GetSubKeyNames())
                        {
                            if (weSub.Contains(devPathHash, StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    winExKey.DeleteSubKeyTree(weSub);
                                    Console.WriteLine($"    Deleted WinExInput registry entry");
                                }
                                catch
                                {
                                    try
                                    {
                                        string fullRegPath = $@"HKLM\{winExPath}\{weSub}";
                                        RunProcess("reg.exe", $"delete \"{fullRegPath}\" /f", timeoutMs: 3000);
                                        Console.WriteLine($"    Deleted WinExInput registry entry (reg.exe)");
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: DisableGhostXusbInterfaces error: {ex.Message}");
        }
    }

    // NOTE: NEVER kill WUDFHost processes. Killing WUDFHost breaks real BT controllers
    // (Code 43 error). Use pnputil /remove-device + recreate instead.

    /// <summary>
    /// Overrides xinputhid's device name on all HID child devices.
    /// xinputhid sets names like "Xbox Wireless Controller" from its INF.
    /// We override with the profile's deviceDescription via direct registry write.
    /// </summary>
    /// <summary>
    /// Final-pass friendly-name application for a specific controllerIndex.
    /// Used by EmulateWithSwitching after Phase 1 to overcome a race where
    /// per-controller renames during setup get clobbered by PnP driver-bind
    /// on the first controller. Iterates ALL ROOT\* device classes
    /// (VID_*&IG_00, HMCOMPANION, HIDClass) and matches by Device Parameters\
    /// ControllerIndex.
    /// </summary>
    static void ApplyFriendlyNameForController(int controllerIndex, string name)
    {
        var fnKey = new DEVPROPKEY
        {
            fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
            pid = 14
        };
        var ddKey = new DEVPROPKEY
        {
            fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
            pid = 2
        };
        var brddKey = new DEVPROPKEY
        {
            fmtid = new Guid(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2),
            pid = 4
        };
        byte[] strBytes = Encoding.Unicode.GetBytes(name + "\0");

        void Apply(uint inst)
        {
            CM_Set_DevNode_PropertyW(inst, ref fnKey,   0x12, strBytes, (uint)strBytes.Length, 0);
            CM_Set_DevNode_PropertyW(inst, ref ddKey,   0x12, strBytes, (uint)strBytes.Length, 0);
            CM_Set_DevNode_PropertyW(inst, ref brddKey, 0x12, strBytes, (uint)strBytes.Length, 0);
        }

        try
        {
            using var rootEnum = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT");
            if (rootEnum == null) return;
            foreach (var sub in rootEnum.GetSubKeyNames())
            {
                // Match HIDMaestro device classes only — never touch VHF/HidHide etc.
                bool isOurClass = sub.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
                               || sub.Equals("HMCOMPANION", StringComparison.OrdinalIgnoreCase)
                               || sub.Equals("HIDCLASS", StringComparison.OrdinalIgnoreCase);
                if (!isOurClass) continue;

                using var subKey = rootEnum.OpenSubKey(sub);
                if (subKey == null) continue;
                foreach (var inst in subKey.GetSubKeyNames())
                {
                    using var dpKey = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Enum\ROOT\{sub}\{inst}\Device Parameters");
                    if (dpKey == null) continue;
                    var ci = dpKey.GetValue("ControllerIndex");
                    int actual = ci is int v ? v : -1;
                    if (actual != controllerIndex) continue;

                    string instId = $@"ROOT\{sub}\{inst}";
                    if (CM_Locate_DevNodeW(out uint devInst, instId, 0) != 0) continue;

                    // Root device
                    Apply(devInst);

                    // Walk children (HID grandchildren etc.) and apply to all
                    if (CM_Get_Child(out uint child, devInst, 0) == 0)
                    {
                        uint cur = child;
                        do { Apply(cur); }
                        while (CM_Get_Sibling(out cur, cur, 0) == 0);
                    }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Sets the FriendlyName and DeviceDesc on the HIDMaestro root device(s)
    /// belonging to a specific controllerIndex (matched via Device Parameters
    /// registry value), and on their HID children. With controllerIndex=-1
    /// (legacy behavior) updates ALL HIDMaestro VID_ devices — used only by
    /// single-controller paths that don't need filtering.
    ///
    /// Multi-controller MUST pass the explicit index to avoid one controller's
    /// setup overwriting an earlier controller's friendly name.
    /// </summary>
    static void FixHidChildNames(string name, int controllerIndex = -1)
    {
        var fnKey = new DEVPROPKEY
        {
            fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
            pid = 14 // DEVPKEY_Device_FriendlyName
        };
        var ddKey = new DEVPROPKEY
        {
            fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
            pid = 2 // DEVPKEY_Device_DeviceDesc
        };
        byte[] strBytes = Encoding.Unicode.GetBytes(name + "\0");

        // Find ROOT\VID_* devices, optionally filtered by Device Parameters\ControllerIndex
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT");
            if (enumKey == null) return;
            foreach (var sub in enumKey.GetSubKeyNames())
            {
                if (!sub.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)) continue;
                using var subKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\ROOT\{sub}");
                if (subKey == null) continue;
                foreach (var inst in subKey.GetSubKeyNames())
                {
                    // If a controllerIndex was requested, skip devices that don't match.
                    if (controllerIndex >= 0)
                    {
                        using var dpKey = Registry.LocalMachine.OpenSubKey(
                            $@"SYSTEM\CurrentControlSet\Enum\ROOT\{sub}\{inst}\Device Parameters");
                        if (dpKey == null) continue;
                        var ci = dpKey.GetValue("ControllerIndex");
                        // Treat missing as index 0 so single-controller setup still works
                        int actual = ci is int v ? v : 0;
                        if (actual != controllerIndex) continue;
                    }

                    string devId = $@"ROOT\{sub}\{inst}";
                    if (CM_Locate_DevNodeW(out uint devInst, devId, 0) == 0)
                    {
                        CM_Set_DevNode_PropertyW(devInst, ref fnKey, 0x12, strBytes, (uint)strBytes.Length, 0);
                        CM_Set_DevNode_PropertyW(devInst, ref ddKey, 0x12, strBytes, (uint)strBytes.Length, 0);
                        if (CM_Get_Child(out uint childInst, devInst, 0) == 0)
                        {
                            CM_Set_DevNode_PropertyW(childInst, ref fnKey, 0x12, strBytes, (uint)strBytes.Length, 0);
                            CM_Set_DevNode_PropertyW(childInst, ref ddKey, 0x12, strBytes, (uint)strBytes.Length, 0);
                        }
                    }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Creates a device node with the correct enumerator and hardware ID.
    /// xinputhid profiles use HID_IG_00 enumerator (triggers xinputhid upper filter).
    /// Direct HID profiles use HIDClass enumerator (standard HID access).
    /// </summary>
    static bool CreateDeviceNode(ControllerProfile profile, string infPath, int controllerIndex = 0)
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
            // Xbox HID mode: &IG_ for Chrome/SDL3 filtering + FunctionMode enables WinExInput
            enumerator = $"VID_{vid}&PID_{pid}&IG_00";
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

            // Write DeviceOverrides registry key BEFORE device registration.
            // This tells PnP the device is removable, generating a unique ContainerId
            // per instance instead of merging all ROOT devices into one container.
            try
            {
                // Escape backslash in hwId for the registry key name:
                // DeviceOverrides uses the hardware ID as the subkey name with # replacing \
                string overrideHwId = hwId.Replace('\\', '#');
                string overridePath = $@"SYSTEM\CurrentControlSet\Control\DeviceOverrides\{overrideHwId}\*";
                using var overrideKey = Registry.LocalMachine.CreateSubKey(overridePath);
                overrideKey.SetValue("Removable", 1, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Console.Write($"(DeviceOverrides: {ex.Message}) ");
            }

            // DIF_REGISTERDEVICE = 0x19
            if (!SetupDiCallClassInstaller_Raw(0x19, dis, devInfoHandle.AddrOfPinnedObject()))
            {
                devInfoHandle.Free();
                return false;
            }

            devInfoHandle.Free();

            // Write ControllerIndex to device's HW key (driver reads this at startup)
            // Write ControllerIndex to the device we just created.
            // PnP assigns instance indices that may not match controllerIndex (ghosts
            // from previous runs offset numbering). Find our device: it's a LIVE device
            // (CM_Locate succeeds) without ControllerIndex in Device Parameters.
            try
            {
                using var enumKey = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Enum\ROOT\{enumerator}");
                if (enumKey != null)
                {
                    foreach (var inst in enumKey.GetSubKeyNames())
                    {
                        string instId = $@"ROOT\{enumerator}\{inst}";
                        // Must be a live device, not a ghost
                        if (CM_Locate_DevNodeW(out uint _, instId, 0) != 0) continue;
                        string dpPath = $@"SYSTEM\CurrentControlSet\Enum\{instId}\Device Parameters";
                        using var dpKey = Registry.LocalMachine.CreateSubKey(dpPath);
                        var existing = dpKey.GetValue("ControllerIndex");
                        if (existing == null)
                        {
                            dpKey.SetValue("ControllerIndex", controllerIndex, RegistryValueKind.DWord);
                            break;
                        }
                    }
                }
            }
            catch { }

            // Write a unique ContainerID directly to the Enum key BEFORE the driver
            // starts.  PnP honours a pre-existing ContainerID on first enumeration,
            // giving each virtual device its own container in Device Manager / Settings.
            // The GUID is deterministic per controllerIndex so it survives restarts.
            try
            {
                using var enumKey2 = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Enum\ROOT\{enumerator}");
                if (enumKey2 != null)
                {
                    foreach (var inst in enumKey2.GetSubKeyNames())
                    {
                        string instId2 = $@"ROOT\{enumerator}\{inst}";
                        if (CM_Locate_DevNodeW(out uint _, instId2, 0) != 0) continue;
                        string dpPath2 = $@"SYSTEM\CurrentControlSet\Enum\{instId2}\Device Parameters";
                        using var dpKey2 = Registry.LocalMachine.OpenSubKey(dpPath2);
                        var ci = dpKey2?.GetValue("ControllerIndex");
                        if (ci is int ciVal && ciVal == controllerIndex)
                        {
                            // Build deterministic GUID: {48494430-4D41-4553-5452-4F00XXYY00ZZ}
                            // where ZZ = controllerIndex, keeping it unique per instance.
                            string containerGuid = $"{{48494430-4D41-4553-5452-4F00000000{controllerIndex:X2}}}";
                            string enumRegPath = $@"SYSTEM\CurrentControlSet\Enum\{instId2}";
                            using var devKey = Registry.LocalMachine.OpenSubKey(enumRegPath, writable: true);
                            devKey?.SetValue("ContainerID", containerGuid, RegistryValueKind.String);
                            Console.Write($"(ContainerID={containerGuid}) ");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Write($"(ContainerID write: {ex.Message}) ");
            }

            // Install driver via UpdateDriverForPlugAndPlayDevicesW
            string driverHwId = hwId;
            UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, driverHwId, infPath, 0, out bool _);

            // XnaComposite devices need an explicit restart to load the driver
            if (!profile.UsesUpperFilter)
            {
                string instId = $@"ROOT\XNACOMPOSITE\0000";
                DeviceManager.RestartDevice(instId);
            }

            // Wait for HID child to enumerate (event-driven, no sleep).
            // Find OUR device instance by ControllerIndex (PnP index may differ from controllerIndex).
            string devEnumer = profile.UsesUpperFilter
                ? $"VID_{profile.VendorId:X4}&PID_{(profile.DriverPid != null ? Convert.ToUInt16(profile.DriverPid, 16) : profile.ProductId):X4}&IG_00"
                : $"VID_{profile.VendorId:X4}&PID_{profile.ProductId:X4}&IG_00";
            string? devInstId = null;
            try
            {
                using var ek = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\ROOT\{devEnumer}");
                if (ek != null)
                    foreach (var inst in ek.GetSubKeyNames())
                    {
                        string cand = $@"ROOT\{devEnumer}\{inst}";
                        if (CM_Locate_DevNodeW(out uint _, cand, 0) != 0) continue;
                        using var dp = ek.OpenSubKey($@"{inst}\Device Parameters");
                        if (dp?.GetValue("ControllerIndex") is int ci && ci == controllerIndex)
                        { devInstId = cand; break; }
                    }
            }
            catch { }
            if (devInstId != null)
            {
                if (!DeviceManager.WaitForHidChild(devInstId))
                    Console.Write("(HID child timeout) ");
            }

            // Fix name after xinputhid loads — filter by controllerIndex so
            // multi-controller doesn't overwrite a sibling controller's name
            string displayName = profile.DeviceDescription ?? profile.ProductString;
            FixHidChildNames(displayName, controllerIndex);

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

    static bool EnsureDriverInstalled(ControllerProfile? profile = null, int controllerIndex = 0)
    {
        {
            bool needsBuild = DriverBuilder.NeedsBuild();
            bool driverInstalled = DriverBuilder.IsDriverInstalled();

            if (needsBuild || !driverInstalled)
            {
                Console.WriteLine("  Driver deploy needed...");
                if (!DriverBuilder.FullDeploy(rebuild: needsBuild))
                    return false;
            }
        }

        // companionOnly: skip main HID device — XUSB companion provides DI + XInput + browser.
        // Real Xbox 360 uses xusb22.sys (no HID at all). DI reads from XInput (5 axes).
        string? rootInstId = null;
        if (profile?.CompanionOnly == true)
        {
            Console.WriteLine("  Companion-only mode (no main HID device)");
        }
        else
        {
            // Find existing device for THIS controller index (by ControllerIndex in registry).
            Console.Write("  Checking device... ");
            {
                string[] enumerators = new[] { "HID_IG_00", "XnaComposite",
                    $"VID_{profile?.VendorId:X4}&PID_{profile?.ProductId:X4}&IG_00",
                    "VID_045E&PID_02FF&IG_00", "VID_045E&PID_0B13&IG_00",
                    "VID_045E&PID_028E&IG_00" };
                foreach (string enumer in enumerators)
                {
                    try
                    {
                        using var ek = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\ROOT\{enumer}");
                        if (ek == null) continue;
                        foreach (var inst in ek.GetSubKeyNames())
                        {
                            string candidate = $@"ROOT\{enumer}\{inst}";
                            if (CM_Locate_DevNodeW(out uint _, candidate, 0) != 0) continue;
                            using var dp = ek.OpenSubKey($@"{inst}\Device Parameters");
                            var ci = dp?.GetValue("ControllerIndex");
                            // Claim device if it has our index, OR if it has no index (unclaimed, for index 0)
                            if ((ci is int ciVal && ciVal == controllerIndex) ||
                                (ci == null && controllerIndex == 0))
                            { rootInstId = candidate; break; }
                        }
                    }
                    catch { }
                    if (rootInstId != null) break;
                }
            }
            string infPath = Path.Combine(BuildDir, "hidmaestro.inf");
            if (rootInstId != null)
            {
                // Device already exists with correct ControllerIndex — use it as-is.
                // Don't restart/remove — that destroys xinputhid binding and HID children.
                Console.WriteLine("OK");
            }
            else
            {
                // Device doesn't exist — create it
                Console.Write("creating... ");
                if (profile != null)
                    CreateDeviceNode(profile, infPath, controllerIndex);
                else
                    RunPowerShell("create_node.ps1"); // fallback
                Thread.Sleep(3000);
                Console.WriteLine("OK");
            }
        }

        // Set device name on root AND HID child
        Console.Write("  Setting device name... ");
        string dispName = (string?)Registry.LocalMachine.OpenSubKey(RegPathForIndex(controllerIndex))?.GetValue("DeviceDescription")
            ?? (string?)Registry.LocalMachine.OpenSubKey(REG_PATH)?.GetValue("DeviceDescription")
            ?? "Controller";

        // If rootInstId wasn't captured from the existing-device claim above
        // (i.e. CreateDeviceNode just made a new one), find OUR new instance
        // by ControllerIndex BEFORE renaming. Without this, the rename runs
        // against null and the root device gets stuck with the INF default
        // name "Game Controller".
        uint rootInst = 0;
        if (rootInstId == null)
        {
            string? enumer = profile?.UsesUpperFilter == true
                ? $"VID_{profile.VendorId:X4}&PID_{(profile.DriverPid != null ? Convert.ToUInt16(profile.DriverPid, 16) : profile.ProductId):X4}&IG_00"
                : $"VID_{profile?.VendorId:X4}&PID_{profile?.ProductId:X4}&IG_00";
            try
            {
                using var enumKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\ROOT\{enumer}");
                if (enumKey != null)
                    foreach (var inst in enumKey.GetSubKeyNames())
                    {
                        string candidate = $@"ROOT\{enumer}\{inst}";
                        if (CM_Locate_DevNodeW(out uint ci, candidate, 0) != 0) continue;
                        using var dp = enumKey.OpenSubKey($@"{inst}\Device Parameters");
                        if (dp?.GetValue("ControllerIndex") is int idx && idx == controllerIndex)
                        { rootInst = ci; rootInstId = candidate; break; }
                    }
            }
            catch { }
        }
        else
        {
            CM_Locate_DevNodeW(out rootInst, rootInstId, 0);
        }

        if (rootInstId != null)
        {
            SetDeviceDescription(rootInstId, dispName);
            SetBusReportedDeviceDesc(rootInstId, dispName);
            SetDeviceFriendlyName(rootInstId, dispName);
        }

        if (rootInst != 0)
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
                        // Match by VID/PID AND verify it's our root-enumerated device
                        if (attrs.VendorID == targetVid && attrs.ProductID == targetPid
                            && path.Contains("root", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"  Found: VID={attrs.VendorID:X4} PID={attrs.ProductID:X4} @ {path}");
                            return handle;
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

    // ── Live profile switching ──

    // Per-controller switch request: index → new profile ID
    static volatile string?[] _switchRequests = new string?[4];

    static int EmulateWithSwitching(string[] profileIds)
    {
        if (profileIds.Length == 0)
            return Error("Usage: HIDMaestroTest emulate <profile-id> [profile-id ...]\n  Use 'list' to see available profiles.");

        // Backward compat: single profile
        if (profileIds.Length == 1)
            return EmulateWithSwitchingSingle(profileIds[0]);

        // Multi-controller: install drivers once, then run each profile on its own thread.
        Console.WriteLine($"\n  Multi-controller mode: {profileIds.Length} controllers\n");

        string[] currentIds = (string[])profileIds.Clone();

        // Install drivers before any controller threads start
        {
            bool needsBuild = DriverBuilder.NeedsBuild();
            bool driverInstalled = DriverBuilder.IsDriverInstalled();
            if (needsBuild || !driverInstalled)
            {
                Console.Write("  Installing drivers... ");
                if (!DriverBuilder.FullDeploy(rebuild: needsBuild))
                    return Error("Driver build/install failed. Run elevated.");
                Console.WriteLine("OK");
            }
        }

        // Console input thread: "quit" or "<index> <profile-id>"
        var consoleThread = new Thread(() =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    string? line = Console.ReadLine();
                    if (line == null) break;
                    line = line.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                        line.Equals("exit", StringComparison.OrdinalIgnoreCase))
                    {
                        _cts.Cancel();
                        break;
                    }
                }
                catch { break; }
            }
        }) { IsBackground = true, Name = "ConsoleInput" };
        consoleThread.Start();

        // Phase 1: Set up all controllers SEQUENTIALLY (no races)
        for (int i = 0; i < currentIds.Length && i < 4; i++)
        {
            Console.WriteLine($"\n  --- Controller {i}: {currentIds[i]} ---");
            EmulateProfile(currentIds[i], controllerIndex: i, setupOnly: true);
        }

        // Phase 1.5: Re-apply friendly names AFTER all setup is complete.
        // The per-controller rename inside EmulateProfile races with PnP
        // driver-bind for the FIRST controller — its CM_Set_DevNode_PropertyW
        // calls happen before the new device's binding settles, so the
        // FriendlyName/DeviceDesc writes get lost. Re-applying here, after
        // all PnP activity is done, makes the writes stick reliably.
        Console.Write("\n  Finalizing device names... ");
        Thread.Sleep(2000);
        {
            var nameDb = ProfileDatabase.Load(GetProfilesDir());
            for (int i = 0; i < currentIds.Length && i < 4; i++)
            {
                var p = nameDb.GetById(currentIds[i]);
                if (p == null) continue;
                string name = p.DeviceDescription ?? p.ProductString ?? "Controller";
                ApplyFriendlyNameForController(i, name);
            }
        }
        Console.WriteLine("OK");

        // Phase 2: Run input loops concurrently. inputLoopOnly=true skips ALL device
        // setup (Steps 0-3.6) and only opens handles + runs the input loop.
        Console.WriteLine($"\n  All controllers ready. Starting input loops...\n");
        var threads = new List<Thread>();
        for (int i = 0; i < currentIds.Length && i < 4; i++)
        {
            int idx = i;
            string pid = currentIds[i];
            var thread = new Thread(() =>
            {
                EmulateProfile(pid, controllerIndex: idx, inputLoopOnly: true);
            }) { IsBackground = true, Name = $"Controller_{idx}" };
            thread.Start();
            threads.Add(thread);
        }

        // Wait for quit
        try { _cts.Token.WaitHandle.WaitOne(); } catch { }

        foreach (var t in threads)
            t.Join(5000);

        Console.Write("\n  Cleaning up... ");
        RemoveAllHIDMaestroDevices();
        Console.WriteLine("OK");

        return 0;
    }

    /// <summary>Single-controller emulate with live profile switching (original behavior).</summary>
    static int EmulateWithSwitchingSingle(string initialProfileId)
    {
        string currentProfileId = initialProfileId;

        var inputThread = new Thread(() =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    string? line = Console.ReadLine();
                    if (line == null) break;
                    line = line.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                        line.Equals("exit", StringComparison.OrdinalIgnoreCase))
                    {
                        _cts.Cancel();
                        break;
                    }
                    _switchToProfile = line;
                }
                catch { break; }
            }
        }) { IsBackground = true, Name = "ProfileSwitchInput" };
        inputThread.Start();

        while (!_cts.Token.IsCancellationRequested)
        {
            _switchToProfile = null;

            int result = EmulateProfile(currentProfileId);

            string? nextProfile = _switchToProfile;
            if (nextProfile != null && !_cts.Token.IsCancellationRequested)
            {
                var db = ProfileDatabase.Load(GetProfilesDir());
                var next = db.GetById(nextProfile) ?? db.Search(nextProfile).FirstOrDefault();
                if (next != null && next.HasDescriptor)
                {
                    Console.WriteLine($"\n  === Switching to {next.Name} ===");
                    currentProfileId = next.Id;
                    continue;
                }
                else
                {
                    Console.WriteLine($"\n  Profile '{nextProfile}' not found or has no descriptor. Still running {currentProfileId}.");
                    _switchToProfile = null;
                    continue;
                }
            }
            break;
        }

        Console.Write("\n  Cleaning up... ");
        RemoveAllHIDMaestroDevices();
        Console.WriteLine("OK");

        return 0;
    }

    // ── Emulate any profile ──

    static int EmulateProfile(string profileId, int controllerIndex = 0, bool setupOnly = false, bool inputLoopOnly = false)
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

        var setupSw = Stopwatch.StartNew();
        Console.WriteLine($"-- Emulating: {profile.Name} --\n");
        Console.WriteLine($"  Profile:  {profile.Id}");
        Console.WriteLine($"  VID:PID:  0x{profile.VendorId:X4}:0x{profile.ProductId:X4}");
        Console.WriteLine($"  Product:  {profile.ProductString}");
        byte[] descriptor;
        byte[]? profileDesc = profile.HasDescriptor ? profile.GetDescriptorBytes() : null;

        if (profileDesc != null)
        {
            // Use the profile descriptor as-is. Data injection uses shared file,
            // so no output/feature report injection needed.
            descriptor = profileDesc;
            Console.WriteLine($"  Descriptor: {descriptor.Length} bytes (native)\n");
        }
        else
        {
            descriptor = UniversalDescriptor;
            Console.WriteLine($"  Descriptor: {descriptor.Length} bytes (universal)\n");
        }

        // Compute input report length (pure computation from profile, no device ops)
        int inputReportLen = (profile.InputReportSize ?? 0) > 0
            ? profile.InputReportSize!.Value
            : ComputeInputReportByteLength(descriptor);

        // Compute BLE descriptor (pure computation, no device ops)
        byte[] bleDesc;
        int bleReportLen;
        if (profile.UsesUpperFilter)
        {
            bleDesc = descriptor;
            bleReportLen = inputReportLen;
        }
        else
        {
            bleDesc = descriptor;
            bleReportLen = inputReportLen;
        }

        // Phase 2 (inputLoopOnly): skip ALL device setup — devices already exist from Phase 1.
        // Only need to parse descriptors, open handles, and run the input loop.
        SafeFileHandle? h = null;
        SafeFileHandle? xh = null;
        ushort hidFeatureReportLen = 0;
        ushort hidInputReportLen = 0;

        if (!inputLoopOnly)
        {

        // Step 0: Pre-flight setup (all in code, no external scripts)
        Console.Write("  Setting up environment... ");
        EnsureGameInputService();
        EnsureSharedMapping(controllerIndex);
        if (controllerIndex == 0 && !_ghostsCleanedThisSession)
        {
            CleanupGhostDevices();
            DisableGhostXusbInterfaces();
            _ghostsCleanedThisSession = true;
        }
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
                            DeviceManager.RemoveDevice(sysId);
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
        Console.Write($"  Writing profile to registry (InputReport={inputReportLen}B)... ");
        // === PID 0001 TRICK (critical for xinputhid profiles) ===
        //
        // HID attributes PID is set to 0x0001 (Microsoft mouse), NOT the real PID.
        // xinputhid matches by HARDWARE ID (PID 02FF), not HID attributes — unaffected.
        //
        // Why: GameInput auto-claims Xbox VID/PID as Gamepad but returns ZEROS for
        // virtual (ROOT-enumerated) devices. PID 0001 isn't in GameInput's gamepad
        // database, so GameInput skips it entirely.
        //
        // SDL3 detection chain with PID 0001:
        //   HIDAPI: skips (device path has &IG_)
        //   GameInput: skips (PID 0001 not a gamepad)
        //   RawInput: skips for gaming (PID 0001 generic)
        //   XInput: CLAIMS IT → reads from xinputhid's slot → correct identity + live data
        //
        // Result: SDL3/PadForge shows "Xbox Series X Controller" via XInput backend.
        // Python hidapi showing "no devices" is CORRECT — HIDAPI is not the path.
        // Write BLE descriptor + real VID/PID (companion reads this at startup)
        // BLE descriptor has Report ID 0x01 which HIDAPI can parse correctly
        // Companion descriptor: use the profile's native descriptor directly.
        // For xinputhid profiles, prepend Report ID 0x01 to make mshidumdf happy.
        // The companion is hidden from HIDAPI (&IG_), so SDL3 sees it through
        // RawInput/Windows backend which maps by VID/PID, not descriptor parsing.
        // DirectInput reads this descriptor directly — must match real hardware
        // (correct axis count, button count, trigger mode).
        // === Vx/Vy VELOCITY TRICK (Xbox 360 descriptor) ===
        //
        // Xbox 360 profiles use Vx (0x40) and Vy (0x41) velocity usages in the HID
        // descriptor to carry separate trigger values. This solves the DI-vs-browser
        // trigger dilemma:
        //   - DirectInput ignores velocity usages → sees 5 axes (X, Y, Z, Rx, Ry)
        //   - GameInput/WGI reads velocity as extra axes → browser gets separate LT/RT
        //   - Combined Z axis gives DI the classic Xbox 360 behavior
        //
        // The descriptor is in the profile JSON — no code manipulation needed.
        // (bleDesc/bleReportLen already computed above)
        // FunctionMode for Xbox HID profiles: skip filter mode so XUSB registers on
        // the HID device itself. DI sees XUSB → uses XInput mapping (5 axes, 10 buttons).
        bool funcMode = profile.VendorId == 0x045E && !profile.UsesUpperFilter;
        WriteConfig(bleDesc, profile.VendorId, profile.ProductId,
            productString: profile.ProductString,
            deviceDescription: profile.DeviceDescription,
            inputReportByteLength: bleReportLen,
            functionMode: funcMode,
            controllerIndex: controllerIndex);
        Console.WriteLine("OK");

        // Ensure driver packages are in the store BEFORE creating any devices.
        // Devices created without a matching driver enter a bad state requiring reboot.
        {
            bool needsBuild = DriverBuilder.NeedsBuild();
            bool driverInstalled = DriverBuilder.IsDriverInstalled();
            if (needsBuild || !driverInstalled)
            {
                Console.Write("  Installing drivers... ");
                if (!DriverBuilder.FullDeploy(rebuild: needsBuild))
                    return Error("Driver build/install failed. Run elevated.");
                Console.WriteLine("OK");
            }
        }

        // Step 1.5: Create Gamepad companion for SDL3 HIDAPI identity.
        // The companion provides correct VID/PID + BTHLEDEVICE bus type so SDL3's
        // HIDAPI backend claims it and shows the correct controller name.
        // Multi-controller: each controllerIndex gets its OWN companion instance.
        // We claim an existing companion only if its Device Parameters\ControllerIndex
        // matches ours; otherwise we create a fresh device and write ControllerIndex
        // to identify it. This mirrors the CreateDeviceNode pattern for non-xinputhid.
        if (profile.UsesUpperFilter)
        {
            Console.Write($"  Creating Gamepad companion {controllerIndex}... ");
            string gpVid = $"{profile.VendorId:X4}";
            string gpPid = $"{profile.ProductId:X4}";
            string hwPid = profile.DriverPid != null
                ? $"{Convert.ToUInt16(profile.DriverPid, 16):X4}" : gpPid;
            string gpEnumerator = $"VID_{gpVid}&PID_{hwPid}&IG_00";

            // 1. Look for an existing companion already claimed by THIS controllerIndex
            string? gpInstId = null;
            try
            {
                using var gpEnum = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Enum\ROOT\{gpEnumerator}");
                if (gpEnum != null)
                {
                    foreach (var inst in gpEnum.GetSubKeyNames())
                    {
                        string candidate = $@"ROOT\{gpEnumerator}\{inst}";
                        if (CM_Locate_DevNodeW(out uint _, candidate, 0) != 0) continue;
                        using var dp = gpEnum.OpenSubKey($@"{inst}\Device Parameters");
                        var ci = dp?.GetValue("ControllerIndex");
                        if (ci is int civ && civ == controllerIndex)
                        { gpInstId = candidate; break; }
                    }
                }
            }
            catch { }

            if (gpInstId == null)
            {
                // 2. No existing match — create a new companion device.
                var hidGuid = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
                IntPtr dis3 = SetupDiCreateDeviceInfoList(ref hidGuid, IntPtr.Zero);
                if (dis3 != new IntPtr(-1))
                {
                    byte[] diBuf3 = new byte[32];
                    BitConverter.GetBytes(IntPtr.Size == 8 ? 32 : 28).CopyTo(diBuf3, 0);
                    var diHandle3 = System.Runtime.InteropServices.GCHandle.Alloc(diBuf3, GCHandleType.Pinned);
                    string gpHw = $"root\\VID_{gpVid}&PID_{hwPid}&IG_00\0root\\HIDMaestroGamepad\0root\\HIDMaestro\0\0";
                    byte[] gpHwBytes = Encoding.Unicode.GetBytes(gpHw);
                    if (SetupDiCreateDeviceInfoW_Raw(dis3, gpEnumerator, ref hidGuid,
                        "HIDMaestro Gamepad", IntPtr.Zero, 1, diHandle3.AddrOfPinnedObject()))
                    {
                        SetupDiSetDeviceRegistryPropertyW_Raw(dis3, diHandle3.AddrOfPinnedObject(), 1, gpHwBytes, (uint)gpHwBytes.Length);
                        // CompatibleIDs — only spoof BTHLEDEVICE for Bluetooth profiles
                        string gpCompatBase = $"root\\HIDMaestroGamepad\0root\\HIDMaestro\0\0";
                        if (profile.Connection == "bluetooth")
                            gpCompatBase = $"BTHLEDEVICE\\{{00001812-0000-1000-8000-00805f9b34fb}}_Dev_VID&02{gpVid}_PID&{gpPid}\0" + gpCompatBase;
                        byte[] gpCompatBytes = Encoding.Unicode.GetBytes(gpCompatBase);
                        SetupDiSetDeviceRegistryPropertyW_Raw(dis3, diHandle3.AddrOfPinnedObject(), 2, gpCompatBytes, (uint)gpCompatBytes.Length);
                        SetupDiCallClassInstaller_Raw(0x19, dis3, diHandle3.AddrOfPinnedObject()); // DIF_REGISTERDEVICE
                        SetupDiCallClassInstaller_Raw(0x02, dis3, diHandle3.AddrOfPinnedObject()); // DIF_INSTALLDEVICE
                    }
                    diHandle3.Free();
                    SetupDiDestroyDeviceInfoList(dis3);
                }

                // 3. Find the freshly-created instance: live device with no
                //    ControllerIndex yet. Same trick CreateDeviceNode uses.
                try
                {
                    using var gpEnum = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Enum\ROOT\{gpEnumerator}");
                    if (gpEnum != null)
                    {
                        foreach (var inst in gpEnum.GetSubKeyNames())
                        {
                            string candidate = $@"ROOT\{gpEnumerator}\{inst}";
                            if (CM_Locate_DevNodeW(out uint _, candidate, 0) != 0) continue;
                            string dpPath = $@"SYSTEM\CurrentControlSet\Enum\{candidate}\Device Parameters";
                            using var dpKey = Registry.LocalMachine.CreateSubKey(dpPath);
                            if (dpKey.GetValue("ControllerIndex") == null)
                            {
                                dpKey.SetValue("ControllerIndex", controllerIndex, RegistryValueKind.DWord);
                                gpInstId = candidate;
                                break;
                            }
                        }
                    }
                }
                catch { }
            }

            // 4. Restart THIS controller's companion so it picks up the latest
            //    descriptor + VID/PID from the per-instance registry config.
            if (gpInstId != null)
            {
                DeviceManager.RestartDevice(gpInstId);
                Thread.Sleep(2000);
            }
            Console.WriteLine("OK");

            // Registry keeps real VID/PID (companion reads it)
        }

        // Step 2: Build, sign, install driver + create main device node
        if (!profile.UsesUpperFilter)
        {
            if (!EnsureDriverInstalled(profile, controllerIndex))
                return Error("Driver build/install failed. Run elevated.");
        }
        else
        {
            Console.WriteLine("  Companion-only mode (no xinputhid main device)");
        }

        // Step 3: Wait for HID child + xinputhid, then fix device name.
        // Pass controllerIndex so we ONLY rename our own controller's devices
        // — never overwrite a sibling controller's friendly name in multi mode.
        Thread.Sleep(3000);
        Console.Write("  Fixing device name... ");
        string displayName = profile.DeviceDescription ?? profile.ProductString;
        FixHidChildNames(displayName, controllerIndex);
        // Also set on root device — locate it via Device Parameters\ControllerIndex
        // so multi-controller doesn't grab the wrong root.
        {
            bool found = false;
            try
            {
                using var rootEnum = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT");
                if (rootEnum != null)
                {
                    foreach (var sub in rootEnum.GetSubKeyNames())
                    {
                        if (!sub.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)) continue;
                        using var subKey = rootEnum.OpenSubKey(sub);
                        if (subKey == null) continue;
                        foreach (var inst in subKey.GetSubKeyNames())
                        {
                            using var dpKey = Registry.LocalMachine.OpenSubKey(
                                $@"SYSTEM\CurrentControlSet\Enum\ROOT\{sub}\{inst}\Device Parameters");
                            int actual = (dpKey?.GetValue("ControllerIndex") is int v) ? v : 0;
                            if (actual != controllerIndex) continue;
                            string rootId = $@"ROOT\{sub}\{inst}";
                            SetBusReportedDeviceDesc(rootId, displayName);
                            SetDeviceFriendlyName(rootId, displayName);
                            found = true; break;
                        }
                        if (found) break;
                    }
                }
            }
            catch { }
        }
        // Final name fix after xinputhid grandchild has fully appeared
        Thread.Sleep(2000);
        FixHidChildNames(displayName, controllerIndex);

        // Xbox 360 xinputhid: BtnFix filter (DISABLED — needs proper INF installation)
        if (false && profile.UsesXinputhid && profile.ProductId == 0x028E)
        {
            // Find the xinputhid HID child (HID\VID_045E&PID_02FF&IG_00\...)
            var hidChild = System.Diagnostics.Process.GetCurrentProcess(); // placeholder
            string? hidChildId = null;
            using (var hidEnum = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\HID"))
            {
                if (hidEnum != null)
                {
                    foreach (var sub in hidEnum.GetSubKeyNames())
                    {
                        if (sub.Contains("VID_045E") && sub.Contains("PID_02FF") && sub.Contains("IG_00"))
                        {
                            using var devKey = hidEnum.OpenSubKey(sub);
                            if (devKey != null)
                            {
                                foreach (var inst in devKey.GetSubKeyNames())
                                {
                                    hidChildId = $@"HID\{sub}\{inst}";
                                    break;
                                }
                            }
                            if (hidChildId != null) break;
                        }
                    }
                }
            }
            if (hidChildId != null)
            {
                Console.Write("  Adding BtnFix filter... ");
                try
                {
                    AddBtnFixFilter(hidChildId);
                    // Restart HID child for filter to load
                    DeviceManager.RestartDevice(hidChildId);
                    Thread.Sleep(3000);
                    Console.WriteLine("OK");
                }
                catch (Exception ex) { Console.WriteLine($"FAILED: {ex.Message}"); }
            }
        }

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

        // Step 3.5: Create XUSB companion for XInput — non-xinputhid Xbox profiles ONLY.
        // For xinputhid profiles (Xbox Series BT, etc.), xinputhid auto-loads on the
        // HID child of the gamepad companion and provides XInput itself. Creating an
        // XUSB companion in that case produces a duplicate XInput slot.
        if (profile.VendorId == 0x045E && !profile.UsesUpperFilter)
        {
            Console.Write($"  Creating XUSB companion {controllerIndex}... ");
            // Each controller gets its own XUSB companion for independent XInput slots.
            // Check if THIS controller's companion already exists
            // Find live companion for this controllerIndex
            string? xusbInstId = null;
            try
            {
                using var hmEnum = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT\HMCOMPANION");
                if (hmEnum != null)
                    foreach (var inst in hmEnum.GetSubKeyNames())
                    {
                        string candidate = $@"ROOT\HMCOMPANION\{inst}";
                        if (CM_Locate_DevNodeW(out uint _, candidate, 0) != 0) continue;
                        using var dp = hmEnum.OpenSubKey($@"{inst}\Device Parameters");
                        if (dp?.GetValue("ControllerIndex") is int ci && ci == controllerIndex)
                        { xusbInstId = candidate; break; }
                    }
            }
            catch { }
            bool xusbExists = xusbInstId != null;
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
                    string companionDesc = profile.DeviceDescription ?? profile.ProductString ?? "Controller";
                    if (SetupDiCreateDeviceInfoW_Raw(dis2, "HMCompanion", ref sysGuid,
                        companionDesc, IntPtr.Zero, 1, diHandle.AddrOfPinnedObject()))
                    {
                        SetupDiSetDeviceRegistryPropertyW_Raw(dis2, diHandle.AddrOfPinnedObject(), 1, xusbHwBytes, (uint)xusbHwBytes.Length);
                        SetupDiCallClassInstaller_Raw(0x19, dis2, diHandle.AddrOfPinnedObject());
                    }
                    diHandle.Free();
                    SetupDiDestroyDeviceInfoList(dis2);
                    // Find the actual instance PnP created (live, no ControllerIndex yet)
                    try
                    {
                        using var hmEnum = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT\HMCOMPANION");
                        if (hmEnum != null)
                            foreach (var inst in hmEnum.GetSubKeyNames())
                            {
                                string candidate = $@"ROOT\HMCOMPANION\{inst}";
                                if (CM_Locate_DevNodeW(out uint _, candidate, 0) != 0) continue;
                                string dpPath = $@"SYSTEM\CurrentControlSet\Enum\{candidate}\Device Parameters";
                                using var dpKey = Registry.LocalMachine.CreateSubKey(dpPath);
                                if (dpKey.GetValue("ControllerIndex") == null)
                                {
                                    dpKey.SetValue("ControllerIndex", controllerIndex, RegistryValueKind.DWord);
                                    xusbInstId = candidate;
                                    break;
                                }
                            }
                    }
                    catch { }
                    // Trigger driver binding and wait for XUSB interface.
                    if (xusbInstId == null) xusbInstId = $@"ROOT\HMCOMPANION\{controllerIndex:D4}";
                    var xusbGuid = new Guid("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        RunProcess("pnputil.exe", $"/restart-device \"{xusbInstId}\"", timeoutMs: 5000);
                        if (DeviceManager.WaitForDeviceInterface(xusbInstId, xusbGuid, timeoutMs: 5000))
                            break;
                        // Error state — restart device to retry
                        DeviceManager.RestartDevice(xusbInstId);
                    }
                }
            }
            else
            {
                DeviceManager.RestartDevice(xusbInstId);
            }
            // Set companion name to match the controller profile
            string companionName = profile.DeviceDescription ?? profile.ProductString ?? "Controller";
            SetBusReportedDeviceDesc(xusbInstId, companionName);
            SetDeviceFriendlyName(xusbInstId, companionName);
            Console.WriteLine("OK");
        }

        // (Gamepad companion already created in Step 1.5)

        } // end if (!inputLoopOnly) — skip device setup for Phase 2

        // Step 4: Open the HID child device (runs for both setup and inputLoopOnly)
        Console.Write("  Opening HID device... ");
        h = OpenHidDevice(profile.VendorId, profile.ProductId);
        if (h == null)
        {
            Console.WriteLine("SKIPPED (xinputhid blocks direct access — XInput-only mode)");
        }

        // Query HID caps for report sizes
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
        if (profile.UsesUpperFilter)
        {
            Console.WriteLine($"  XInput: via {profile.UpperFilterName} upper filter");
        }
        else if (profile.VendorId == 0x045E)
        {
            Console.Write("  Opening XUSB interface... ");
            var xusbGuid = new Guid("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");
            for (int retry = 0; retry < 30 && xh == null; retry++)
            {
                if (retry > 0) Thread.Sleep(500);
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

        setupSw.Stop();
        Console.WriteLine($"\n  Setup complete in {setupSw.ElapsedMilliseconds}ms");

        if (setupOnly) { h?.Dispose(); return 0; } // Multi-controller: setup done, input loop runs separately

        Console.WriteLine("  Sending input. Type a profile ID to switch, 'quit' to exit.\n");
        timeBeginPeriod(1); // Enable 1ms timer resolution for 1000 Hz loop

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

        // Shared memory IPC: pagefile-backed named section in Global\ namespace.
        // Driver opens by name. RAM-only — no disk I/O.
        int ctrlIndex = controllerIndex;
        IntPtr sharedView = IntPtr.Zero;
        uint sharedMemSeqNo = 0;
        try
        {
            sharedView = EnsureSharedMapping(ctrlIndex);
            Console.WriteLine($"  Shared memory: OK (Global\\HIDMaestroInput{ctrlIndex})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Shared memory: FAILED ({ex.Message})");
        }

        var sw = Stopwatch.StartNew();
        int count = 0;
        int failCount = 0;

        while (!_cts.Token.IsCancellationRequested && _switchToProfile == null)
        {
            double t = sw.Elapsed.TotalSeconds;
            // Each controller MUST have visually distinct movement across ALL APIs
            // for testing. Speed, direction, and phase all vary by index.
            // DO NOT MODIFY this to fix a single-API bug — the problem is elsewhere.
            double speed = 0.5 + ctrlIndex * 0.3;                       // 0.5, 0.8, 1.1, 1.4 Hz
            double direction = (ctrlIndex % 2 == 0) ? 1.0 : -1.0;      // even=CW, odd=CCW
            double phase = ctrlIndex * Math.PI / 2;                     // 0°, 90°, 180°, 270°
            double angle = t * Math.PI * 2 * speed * direction + phase;

            double lxNorm = 0.5 + 0.46 * Math.Sin(angle);
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

            // Publish to shared memory section: 86 bytes via seqlock.
            // Data = native HID report (for DirectInput via HID READ_REPORT)
            // GipData = GIP format (for XInput via XUSB GET_STATE)
            if (sharedView != IntPtr.Zero)
            {
                int dataStart = reportBuilder.InputReportId != 0 ? 1 : 0;
                int dataLen = Math.Min(inputReport.Length - dataStart, 64);

                // Build GipData with exact layout the companion expects:
                // [0..1]  LX (16-bit unsigned)
                // [2..3]  LY (16-bit unsigned)
                // [4..5]  RX (16-bit unsigned)
                // [6..7]  RY (16-bit unsigned)
                // [8..9]  LT (10-bit in low bits)
                // [10..11] RT (10-bit in low bits)
                // [12]    btnLow (A=0x01, B=0x02, X=0x04, Y=0x08, LB=0x10, RB=0x20, LS=0x40, RS=0x80)
                // [13]    btnHigh (Back=0x01, Start=0x02, hat in bits 2-5)
                byte[] gipData = new byte[14];
                BitConverter.GetBytes((ushort)(lxNorm * 65535)).CopyTo(gipData, 0);
                BitConverter.GetBytes((ushort)(lyNorm * 65535)).CopyTo(gipData, 2);
                BitConverter.GetBytes((ushort)(0.5 * 65535)).CopyTo(gipData, 4);
                BitConverter.GetBytes((ushort)(0.5 * 65535)).CopyTo(gipData, 6);
                BitConverter.GetBytes((ushort)(sepLt * 1023)).CopyTo(gipData, 8);
                BitConverter.GetBytes((ushort)(sepRt * 1023)).CopyTo(gipData, 10);
                byte btnLow = 0;
                if ((btnMask & 0x001) != 0) btnLow |= 0x01; // A
                if ((btnMask & 0x002) != 0) btnLow |= 0x02; // B
                if ((btnMask & 0x004) != 0) btnLow |= 0x04; // X
                if ((btnMask & 0x008) != 0) btnLow |= 0x08; // Y
                if ((btnMask & 0x010) != 0) btnLow |= 0x10; // LB
                if ((btnMask & 0x020) != 0) btnLow |= 0x20; // RB
                if ((btnMask & 0x100) != 0) btnLow |= 0x40; // LS (button 9)
                if ((btnMask & 0x200) != 0) btnLow |= 0x80; // RS (button 10)
                gipData[12] = btnLow;
                byte btnHigh = 0;
                if ((btnMask & 0x040) != 0) btnHigh |= 0x01; // Back (button 7)
                if ((btnMask & 0x080) != 0) btnHigh |= 0x02; // Start (button 8)
                gipData[13] = btnHigh;

                // Native HID payload extracted from inputReport (skip Report ID byte if present)
                byte[] nativeData = new byte[dataLen];
                Array.Copy(inputReport, dataStart, nativeData, 0, dataLen);

                // Atomic seqlock publish — single-writer, lock-free, no disk I/O
                WriteSharedInput(sharedView, ref sharedMemSeqNo, nativeData, dataLen, gipData);
            }
            bool ok = true;

            // Send GIP-format data to XUSB companion for XInput
            // Uses SEPARATE trigger values (sepLt/sepRt), not combined ltVal.
            if (xh != null)
            {
                byte[] xusbIn = new byte[17];
                xusbIn[0] = 0x01; xusbIn[1] = 0x01; xusbIn[2] = 0x00; // GET_STATE header
                // GIP layout: LX(2) LY(2) RX(2) RY(2) LT(2) RT(2) BtnLow(1) BtnHigh(1)
                ushort xLx = (ushort)(lxNorm * 65535);
                ushort xLy = (ushort)(lyNorm * 65535);
                ushort xRx = (ushort)(0.5 * 65535);
                ushort xRy = (ushort)(0.5 * 65535);
                ushort xLt = (ushort)(sepLt * 1023);  // 10-bit separate trigger
                ushort xRt = (ushort)(sepRt * 1023);
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

            Thread.Sleep(1); // ~1000 Hz with timeBeginPeriod(1)
        }

        Console.WriteLine($"\n\n  Sent {count} reports.");

        // Restore HidHide state on exit
        {
            string hidHideCli = @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe";
            if (File.Exists(hidHideCli))
                RunProcess(hidHideCli, "--cloak-off --inv-off", timeoutMs: 3000);
        }

        Console.Write("\n  Cleaning up devices... ");
        // Fast cleanup using CM APIs — no pnputil process spawning
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT");
            if (enumKey != null)
                foreach (var sub in enumKey.GetSubKeyNames())
                {
                    if (sub.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
                        || sub.Equals("HMCompanion", StringComparison.OrdinalIgnoreCase)
                        || sub.Equals("XnaComposite", StringComparison.OrdinalIgnoreCase))
                        foreach (var inst in Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\ROOT\{sub}")?.GetSubKeyNames() ?? Array.Empty<string>())
                            DeviceManager.RemoveDevice($@"ROOT\{sub}\{inst}", timeoutMs: 3000);
                }
        }
        catch { }
        Console.WriteLine("OK");

        h?.Dispose();
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
