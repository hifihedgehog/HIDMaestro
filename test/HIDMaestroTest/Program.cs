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
    /// Finds the current HID child of ROOT\HIDCLASS\0000, adds xinputhid as an
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

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject,
        ref HIDD_ATTRIBUTES Attributes);

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

    static readonly string[] ElevatedCommands = { "emulate", "xbox", "ds5", "cleanup", "setname" };

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
    static readonly byte[] UniversalDescriptor = {
        // EXACT COPY of G_DefaultReportDescriptor from driver.h
        // This descriptor is PROVEN working (Xbox 360 PoC).
        0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0x85, 0x01,
        0x09, 0x30, 0x09, 0x31, 0x15, 0x00, 0x27, 0xFF,
        0xFF, 0x00, 0x00, 0x75, 0x10, 0x95, 0x02, 0x81,
        0x02, 0x09, 0x33, 0x09, 0x34, 0x81, 0x02, 0x09,
        0x32, 0x09, 0x35, 0x81, 0x02, 0x05, 0x09, 0x19,
        0x01, 0x29, 0x0A, 0x15, 0x00, 0x25, 0x01, 0x75,
        0x01, 0x95, 0x0A, 0x81, 0x02, 0x75, 0x06, 0x95,
        0x01, 0x81, 0x01, 0x05, 0x01, 0x09, 0x39, 0x15,
        0x01, 0x25, 0x08, 0x35, 0x00, 0x46, 0x3B, 0x01,
        0x66, 0x14, 0x00, 0x75, 0x04, 0x95, 0x01, 0x81,
        0x42, 0x75, 0x04, 0x95, 0x01, 0x15, 0x00, 0x25,
        0x00, 0x35, 0x00, 0x45, 0x00, 0x65, 0x00, 0x81,
        0x03, 0x85, 0x02, 0x06, 0x00, 0xFF, 0x09, 0x01,
        0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08, 0x95,
        0x0E, 0xB1, 0x02, 0xC0
    };

    // ── Registry config ──

    static void WriteConfig(byte[] descriptor, ushort vid, ushort pid, ushort ver = 0x0100, string? productString = null, string? deviceDescription = null)
    {
        using var key = Registry.LocalMachine.CreateSubKey(REG_PATH);
        key.SetValue("ReportDescriptor", descriptor, RegistryValueKind.Binary);
        key.SetValue("VendorId", (int)vid, RegistryValueKind.DWord);
        key.SetValue("ProductId", (int)pid, RegistryValueKind.DWord);
        key.SetValue("VersionNumber", (int)ver, RegistryValueKind.DWord);
        if (productString != null)
            key.SetValue("ProductString", productString, RegistryValueKind.String);
        // DeviceDescription = what Device Manager / FriendlyName shows
        string displayName = deviceDescription ?? productString ?? "HIDMaestro Controller";
        key.SetValue("DeviceDescription", displayName, RegistryValueKind.String);

        // Register OEM joystick name so joy.cpl/DirectInput shows the correct name.
        // DirectInput reads OEMName from BOTH HKLM and HKCU — HKCU takes priority.
        {
            string oemSubKey = $@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{vid:X4}&PID_{pid:X4}";
            byte[] oemData = { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

            // Write to HKLM
            using (var oem = Registry.LocalMachine.CreateSubKey(oemSubKey))
            {
                oem.SetValue("OEMName", displayName, RegistryValueKind.String);
                oem.SetValue("OEMData", oemData, RegistryValueKind.Binary);
            }

            // Write to HKCU (takes priority for DirectInput/joy.cpl)
            using (var oem = Registry.CurrentUser.CreateSubKey(oemSubKey))
            {
                oem.SetValue("OEMName", displayName, RegistryValueKind.String);
                oem.SetValue("OEMData", oemData, RegistryValueKind.Binary);
            }
        }
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

        // Driver is installed — just remove and recreate device node
        // (EvtDeviceAdd re-reads registry config on creation)
        Console.Write("  Recreating device node... ");
        RunProcess("pnputil.exe", "/remove-device \"ROOT\\HIDCLASS\\0000\" /subtree");
        RunProcess("pnputil.exe", "/remove-device \"ROOT\\HIDCLASS\\0001\" /subtree");
        // Clean up ghost HID children from previous sessions
        RunPowerShell("cleanup_ghosts.ps1");
        Thread.Sleep(1000);
        RunPowerShell("create_node.ps1");
        Thread.Sleep(3000);
        Console.WriteLine("OK");

        // Set device name properties so Device Manager / joy.cpl shows correct name
        Console.Write("  Setting device name... ");
        string dispName = (string?)Registry.LocalMachine.OpenSubKey(REG_PATH)?.GetValue("DeviceDescription") ?? "Controller";
        SetBusReportedDeviceDesc(@"ROOT\HIDCLASS\0000", dispName);
        SetDeviceFriendlyName(@"ROOT\HIDCLASS\0000", dispName);
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
                        // Check for our device by VID/PID match OR by HIDCLASS path (our driver)
                        bool isOurs = (attrs.VendorID == targetVid && attrs.ProductID == targetPid)
                            || path.Contains("HIDCLASS", StringComparison.OrdinalIgnoreCase);

                        if (isOurs)
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
        // Determine if this is an Xbox profile that needs XInput support
        bool isXbox = profile.Vendor == "Microsoft" && profile.Type == "gamepad";
        bool useNativeDescriptor = isXbox && profile.HasDescriptor;

        // For Xbox XInput: use the native GIP descriptor so xinputhid.sys accepts it
        // For everything else: use the universal descriptor with Feature Report data channel
        byte[] descriptor = useNativeDescriptor ? profile.GetDescriptorBytes()! : UniversalDescriptor;
        Console.WriteLine($"  Descriptor: {descriptor.Length} bytes ({(useNativeDescriptor ? "native GIP" : "universal")})\n");

        // Step 1: Write descriptor to registry FIRST
        Console.Write("  Writing profile to registry... ");
        WriteConfig(descriptor, profile.VendorId, profile.ProductId,
            productString: profile.ProductString,
            deviceDescription: profile.DeviceDescription);
        Console.WriteLine("OK");

        // Step 2: Build, sign, install driver + create device node
        // The driver will read the registry config we just wrote
        if (!EnsureDriverInstalled())
            return Error("Driver build/install failed. Run elevated.");

        // Step 3: Open the HID child device
        Console.Write("  Opening HID device... ");
        Thread.Sleep(2000); // Wait for HID child to enumerate
        using var h = OpenHidDevice(profile.VendorId, profile.ProductId);
        if (h == null)
        {
            Console.WriteLine("FAILED");
            Console.WriteLine("  Device not found. Check Device Manager.");
            return 1;
        }

        // Step 4: For Xbox profiles, inject xinputhid upper filter for XInput support
        if (profile.Vendor == "Microsoft" && profile.Type == "gamepad")
        {
            Console.Write("  Enabling XInput... ");
            bool xinputOk = EnableXInputOnHidChild();
            Console.WriteLine(xinputOk ? "OK" : "SKIPPED");
            if (xinputOk)
            {
                // Reopen device after restart
                h.Dispose();
                Thread.Sleep(2000);
                var h2 = OpenHidDevice(profile.VendorId, profile.ProductId);
                if (h2 != null)
                {
                    // Swap handle — can't reassign 'using var' so we'll use h2 below
                    // Actually we need to restructure... for now just proceed
                }
            }
        }

        Console.WriteLine("\n  Sending input. Open joy.cpl to watch.");
        Console.WriteLine("  Ctrl+C to stop.\n");

        // For Xbox native: use WriteFile (IOCTL_HID_WRITE_REPORT -> driver treats as input)
        // For universal: use HidD_SetFeature (Report ID 2 -> driver builds input)
        bool useWriteFile = useNativeDescriptor;
        int reportSize = useWriteFile ? 16 : 14; // GIP input = 16 bytes, universal feature = 14
        byte[] report = new byte[reportSize + (useWriteFile ? 0 : 1)]; // +1 for feature report ID
        if (!useWriteFile) report[0] = 0x02; // Feature Report ID 2

        var sw = Stopwatch.StartNew();
        int count = 0;

        while (!_cts.Token.IsCancellationRequested)
        {
            double t = sw.Elapsed.TotalSeconds;
            double angle = t * Math.PI * 2 * 0.5;

            // Pack gamepad input
            int dataOfs = useWriteFile ? 0 : 1; // skip feature report ID byte
            ushort lx = (ushort)(32768 + (int)(30000 * Math.Sin(angle)));
            ushort ly = (ushort)(32768 + (int)(30000 * Math.Cos(angle)));
            report[dataOfs + 0] = (byte)(lx & 0xFF); report[dataOfs + 1] = (byte)(lx >> 8);
            report[dataOfs + 2] = (byte)(ly & 0xFF); report[dataOfs + 3] = (byte)(ly >> 8);

            bool ok;
            if (useWriteFile)
            {
                ok = WriteFile(h, report, (uint)report.Length, out _, IntPtr.Zero);
            }
            else
            {
                ok = HidD_SetFeature(h, report, (uint)report.Length);
            }

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (count == 0)
                    Console.Error.WriteLine($"  Send failed: {err} (0x{err:X})");
                if (count > 3) break;
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
        SetDeviceFriendlyName(@"ROOT\HIDCLASS\0000", name);
        Console.WriteLine();

        // Set BusReportedDeviceDesc on root
        Console.Write("  Setting BusReportedDeviceDesc on root... ");
        SetBusReportedDeviceDesc(@"ROOT\HIDCLASS\0000", name);
        Console.WriteLine();

        // Verify
        uint locResult = CM_Locate_DevNodeW(out uint devInst, @"ROOT\HIDCLASS\0000", 0);
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
