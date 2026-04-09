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
using HIDMaestro.Internal;

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

    [DllImport("CfgMgr32.dll")]
    static extern uint CM_Get_Device_ID_Size(out uint pulLen, uint dnDevInst, uint ulFlags);

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

        // Output-passthrough sender runs SIDE-BY-SIDE with an active emulate
        // session — it must NOT kill the running test app, must NOT clean up
        // devices on exit, and does not need elevation.
        bool isRumbleTest = args.Length > 0 && args[0].Equals("rumbletest", StringComparison.OrdinalIgnoreCase);

        if (!isRumbleTest)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { RemoveAllHIDMaestroDevices(); } catch { } };

            // Safety net: clean up devices if the process exits unexpectedly
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { SharedMemoryIO.Cleanup(); } catch { }
            };
        }

        if (!isRumbleTest)
        {
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
        }

        // Clean up any leftover devices from previous sessions (now elevated).
        // Skip for rumbletest — it runs side-by-side with an emulate session
        // and must not touch the devices that session is driving.
        if (!isRumbleTest)
        {
            RemoveAllHIDMaestroDevices();
            DeviceManager.RemoveOrphanHidChildren();
            CleanStaleXusbInterfaces();
        }

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
            "rumbletest" => RumbleTest(args.Skip(1).ToArray()),
            "sdk-demo"   => SdkDemo(args.Skip(1).ToArray()),
            _         => Error($"Unknown command: {args[0]}")
        };
    }

    /// <summary>Minimal SDK consumer demo — exercises HMContext → CreateController → SubmitState.
    /// Creates 1 DualSense, feeds a left-stick circle for 5s, then cleans up.</summary>
    static int SdkDemo(string[] args)
    {
        string profileId = args.Length > 0 ? args[0] : "dualsense";
        Console.WriteLine("=== HIDMaestro SDK Demo ===\n");

        using var ctx = new HIDMaestro.HMContext();
        int loaded = ctx.LoadProfilesFromDirectory(GetProfilesDir());
        Console.WriteLine($"  Loaded {loaded} profiles");

        Console.Write("  Installing driver... ");
        ctx.InstallDriver();
        Console.WriteLine("OK");

        var profile = ctx.GetProfile(profileId);
        if (profile == null) return Error($"Profile '{profileId}' not found");

        Console.Write($"  Creating controller ({profile.Name})... ");
        using var ctrl = ctx.CreateController(profile);
        Console.WriteLine("OK");

        Console.WriteLine("  Sending input (left stick circle, 5s)...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int frame = 0;
        while (sw.ElapsedMilliseconds < 5000)
        {
            double t = sw.Elapsed.TotalSeconds * 2 * Math.PI;
            var state = new HIDMaestro.HMGamepadState
            {
                LeftStickX = (float)Math.Cos(t),
                LeftStickY = (float)Math.Sin(t),
            };
            ctrl.SubmitState(in state);
            frame++;
            Thread.Sleep(4);
        }
        Console.WriteLine($"  Sent {frame} frames ({frame * 1000.0 / sw.ElapsedMilliseconds:F0} Hz)");

        Console.Write("  Disposing... ");
        ctrl.Dispose();
        ctx.Dispose();
        Console.WriteLine("OK\n=== Demo complete ===");
        return 0;
    }

    static int Error(string msg) { Console.Error.WriteLine($"ERROR: {msg}"); return 1; }

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
        try
        {
            using var jsRoot = Registry.CurrentUser.OpenSubKey(
                @"System\CurrentControlSet\Control\MediaResources\Joystick", writable: true);
            if (jsRoot != null)
                foreach (var sub in jsRoot.GetSubKeyNames())
                {
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
                        wudf.WaitForExit(10000);
                }
                catch { }
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

                        bool isGhost = false;
                        if (CM_Locate_DevNodeW(out uint devInst, devPath, 0) != 0)
                        {
                            isGhost = true;
                        }
                        else
                        {
                            const uint DN_HAS_PROBLEM = 0x00000400;
                            const uint DN_STARTED = 0x00000008;
                            if (CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) == 0)
                            {
                                if ((status & DN_HAS_PROBLEM) != 0 || (status & DN_STARTED) == 0)
                                    isGhost = true;
                            }
                        }
                        if (!isGhost) continue;
                    }
                    else
                    {
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

    // NOTE: NEVER kill WUDFHost processes. Killing WUDFHost breaks real BT controllers
    // (Code 43 error). Use pnputil /remove-device + recreate instead.

    // ── Shared memory IPC ──
    // P/Invokes still used by rumbletest checkout and ad-hoc enumeration.
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
        uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")]
    static extern bool CloseHandleNative(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "OpenFileMappingW", CharSet = CharSet.Unicode)]
    static extern IntPtr OpenFileMappingNative(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    /* ========================================================================
     * OUTPUT PASSTHROUGH READER
     *
     * The driver and companion publish captured rumble / haptics / FFB / LED
     * commands into Global\HIDMaestroOutput<N>. We open the section read-only
     * and poll for SeqNo changes. Each new packet is decoded by (Source, ReportId)
     * and printed.
     * ====================================================================== */

    const int SHARED_OUTPUT_SIZE = SharedMemoryIO.SHARED_OUTPUT_SIZE;
    const byte OUT_SOURCE_HID_OUTPUT  = SharedMemoryIO.OUT_SOURCE_HID_OUTPUT;
    const byte OUT_SOURCE_HID_FEATURE = SharedMemoryIO.OUT_SOURCE_HID_FEATURE;
    const byte OUT_SOURCE_XINPUT      = SharedMemoryIO.OUT_SOURCE_XINPUT;

    /// <summary>Polls the output mapping for the given controller and prints
    /// each new packet. Runs on a background thread until the cancellation
    /// token fires.</summary>
    static void RunOutputReader(int controllerIndex, ControllerProfile profile, CancellationToken ct)
    {
        IntPtr view;
        try { view = SharedMemoryIO.EnsureOutputMapping(controllerIndex); }
        catch (Exception ex)
        {
            Console.WriteLine($"  [out{controllerIndex}] reader failed to open mapping: {ex.Message}");
            return;
        }

        uint lastSeq = 0;
        byte[] data = new byte[256];
        string label = profile.DeviceDescription ?? profile.ProductString ?? "Controller";

        // Initial sample so we don't fire on whatever's already there.
        lastSeq = (uint)Marshal.ReadInt32(view, 0);

        while (!ct.IsCancellationRequested)
        {
            uint seq1 = (uint)Marshal.ReadInt32(view, 0);
            if (seq1 == lastSeq) { Thread.Sleep(4); continue; }

            // Seqlock read: sample, copy, sample, retry on mismatch.
            byte source = 0, reportId = 0;
            ushort size = 0;
            int retries = 4;
            uint seq2;
            do
            {
                source = Marshal.ReadByte(view, 4);
                reportId = Marshal.ReadByte(view, 5);
                size = (ushort)Marshal.ReadInt16(view, 6);
                if (size > 256) size = 256;
                for (int i = 0; i < size; i++)
                    data[i] = Marshal.ReadByte(view, 8 + i);
                Thread.MemoryBarrier();
                seq2 = (uint)Marshal.ReadInt32(view, 0);
                if (seq1 == seq2) break;
                seq1 = seq2;
            } while (--retries > 0);

            lastSeq = seq2;
            DecodeAndPrintOutput(controllerIndex, label, profile, source, reportId, data, size);
        }
    }

    /// <summary>Profile-aware decode for human-readable display.</summary>
    static void DecodeAndPrintOutput(int idx, string label, ControllerProfile profile,
                                      byte source, byte reportId, byte[] data, int size)
    {
        var sb = new StringBuilder();
        sb.Append($"  [out{idx} {label}] ");

        switch (source)
        {
            case OUT_SOURCE_XINPUT:
            {
                int lo = -1, hi = -1;
                if (size >= 5) { lo = data[2]; hi = data[3]; }
                else if (size >= 4) { lo = data[0] | (data[1] << 8); hi = data[2] | (data[3] << 8); }
                if (lo >= 0)
                    sb.Append($"XInput rumble: lo={lo} hi={hi}  ({size} bytes)");
                else
                    sb.Append($"XInput rumble: {size} bytes  {ToHex(data, size)}");
                break;
            }
            case OUT_SOURCE_HID_FEATURE:
                sb.Append($"HID Feature reportId=0x{reportId:X2} size={size}  {ToHex(data, Math.Min(size, 32))}");
                break;
            case OUT_SOURCE_HID_OUTPUT:
            default:
            {
                if (TryDecodeDualSenseOutput(profile, reportId, data, size, sb)) break;
                if (TryDecodeXboxRumbleHidOutput(profile, reportId, data, size, sb)) break;
                sb.Append($"HID Output reportId=0x{reportId:X2} size={size}  {ToHex(data, Math.Min(size, 32))}");
                break;
            }
        }

        Console.WriteLine(sb.ToString());
    }

    static bool TryDecodeDualSenseOutput(ControllerProfile p, byte reportId, byte[] d, int size, StringBuilder sb)
    {
        if (p == null) return false;
        bool isSony = (p.VendorId == 0x054C);
        if (!isSony) return false;
        if (reportId != 0x02 && reportId != 0x05 && reportId != 0x31) return false;

        if (size < 4)
        {
            sb.Append($"Sony reportId=0x{reportId:X2} short ({size} bytes)");
            return true;
        }

        int rumbleR = d[2];
        int rumbleL = d[3];
        sb.Append($"Sony rumble  L={rumbleL,3}  R={rumbleR,3}  reportId=0x{reportId:X2}  size={size}");

        if (size >= 33)
        {
            byte rTrigMode = d[10];
            byte lTrigMode = d[22];
            if (rTrigMode != 0 || lTrigMode != 0)
                sb.Append($"  triggerL=mode{lTrigMode:X2} triggerR=mode{rTrigMode:X2}");
        }

        if (size >= 47)
        {
            byte r = d[44], g = d[45], b = d[46];
            if ((r | g | b) != 0)
                sb.Append($"  LED=#{r:X2}{g:X2}{b:X2}");
        }
        return true;
    }

    static bool TryDecodeXboxRumbleHidOutput(ControllerProfile p, byte reportId, byte[] d, int size, StringBuilder sb)
    {
        if (p == null) return false;
        if (p.VendorId != 0x045E) return false;
        if (reportId != 0x03 && reportId != 0x00) return false;
        if (size < 4) return false;

        sb.Append($"Xbox HID rumble reportId=0x{reportId:X2} size={size}  ");
        sb.Append($"enable=0x{d[0]:X2} ltrig={d[1],3} rtrig={d[2],3} lmot={d[3],3}");
        if (size >= 5) sb.Append($" rmot={d[4],3}");
        return true;
    }

    static string ToHex(byte[] data, int size)
    {
        var s = new StringBuilder(size * 3);
        for (int i = 0; i < size; i++)
        {
            if (i > 0) s.Append(' ');
            s.Append(data[i].ToString("X2"));
        }
        return s.ToString();
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

        // Phase 1: Set up all controllers SEQUENTIALLY (no races).
        var phase1Db = ProfileDatabase.Load(GetProfilesDir());
        for (int i = 0; i < currentIds.Length; i++)
        {
            Console.WriteLine($"\n  --- Controller {i}: {currentIds[i]} ---");
            var phase1Profile = phase1Db.GetById(currentIds[i]);
            bool isXboxProfile = ProfileTakesXInputSlot(phase1Profile);
            int slotsBefore = isXboxProfile ? CountConnectedXInputSlots() : -1;
            bool xinputFull = isXboxProfile && slotsBefore >= 4;

            EmulateProfile(currentIds[i], controllerIndex: i, setupOnly: true);

            if (xinputFull)
            {
                Console.WriteLine($"  XInput is full ({slotsBefore}/4) — controller {i} will be visible via DI/HIDAPI/Browser only.");
            }
            else if (slotsBefore >= 0)
            {
                Console.Write($"  Waiting for XInput slot... ");
                var sw = Stopwatch.StartNew();
                int slotsAfter = slotsBefore;
                while (sw.ElapsedMilliseconds < 15000)
                {
                    slotsAfter = CountConnectedXInputSlots();
                    if (slotsAfter > slotsBefore) break;
                    Thread.Sleep(100);
                }
                if (slotsAfter > slotsBefore)
                    Console.WriteLine($"slot claimed in {sw.ElapsedMilliseconds}ms ({slotsBefore}→{slotsAfter})");
                else
                    Console.WriteLine($"TIMEOUT after {sw.ElapsedMilliseconds}ms — slot order may be non-deterministic");
            }
        }

        // Phase 1.5: Re-apply friendly names AFTER all setup is complete.
        Console.Write("\n  Finalizing device names... ");
        Thread.Sleep(2000);
        {
            var nameDb = ProfileDatabase.Load(GetProfilesDir());
            for (int i = 0; i < currentIds.Length; i++)
            {
                var p = nameDb.GetById(currentIds[i]);
                if (p == null) continue;
                string name = p.DeviceDescription ?? p.ProductString ?? "Controller";
                DeviceProperties.ApplyFriendlyNameForController(i, name);
            }
        }
        Console.WriteLine("OK");

        // Phase 2: Run input loops concurrently. inputLoopOnly=true skips ALL device
        // setup and only opens handles + runs the input loop.
        Console.WriteLine($"\n  All controllers ready. Starting input loops...\n");
        var threads = new List<Thread>();
        var outputThreads = new List<Thread>();
        var phase2Db = ProfileDatabase.Load(GetProfilesDir());
        for (int i = 0; i < currentIds.Length; i++)
        {
            int idx = i;
            string pid = currentIds[i];
            var thread = new Thread(() =>
            {
                EmulateProfile(pid, controllerIndex: idx, inputLoopOnly: true);
            }) { IsBackground = true, Name = $"Controller_{idx}" };
            thread.Start();
            threads.Add(thread);

            var profile = phase2Db.GetById(pid);
            if (profile != null)
            {
                var outThread = new Thread(() =>
                {
                    RunOutputReader(idx, profile, _cts.Token);
                }) { IsBackground = true, Name = $"Output_{idx}" };
                outThread.Start();
                outputThreads.Add(outThread);
            }
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
            descriptor = profileDesc;
            Console.WriteLine($"  Descriptor: {descriptor.Length} bytes (native)\n");
        }
        else
        {
            descriptor = new byte[] {
                0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0x85, 0x01, 0x09, 0x30, 0x09, 0x31, 0x15, 0x00, 0x27, 0xFF,
                0xFF, 0x00, 0x00, 0x75, 0x10, 0x95, 0x02, 0x81, 0x02, 0x09, 0x33, 0x09, 0x34, 0x81, 0x02, 0x09,
                0x32, 0x09, 0x35, 0x81, 0x02, 0x05, 0x09, 0x19, 0x01, 0x29, 0x0A, 0x15, 0x00, 0x25, 0x01, 0x75,
                0x01, 0x95, 0x0A, 0x81, 0x02, 0x75, 0x06, 0x95, 0x01, 0x81, 0x01, 0x05, 0x01, 0x09, 0x39, 0x15,
                0x01, 0x25, 0x08, 0x35, 0x00, 0x46, 0x3B, 0x01, 0x66, 0x14, 0x00, 0x75, 0x04, 0x95, 0x01, 0x81,
                0x42, 0x75, 0x04, 0x95, 0x01, 0x15, 0x00, 0x25, 0x00, 0x35, 0x00, 0x45, 0x00, 0x65, 0x00, 0x81,
                0x03, 0x85, 0x02, 0x06, 0x00, 0xFF, 0x09, 0x01, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08, 0x95,
                0x0E, 0xB1, 0x02, 0xC0,
            };
            Console.WriteLine($"  Descriptor: {descriptor.Length} bytes (universal)\n");
        }

        // Phase 2 (inputLoopOnly): skip ALL device setup — devices already exist from Phase 1.
        SafeFileHandle? h = null;
        SafeFileHandle? xh = null;
        ushort hidFeatureReportLen = 0;
        ushort hidInputReportLen = 0;

        if (!inputLoopOnly)
        {
            // Full setup via SDK orchestrator
            Console.Write("  Setting up controller via SDK... ");
            string infPath = Path.Combine(BuildDir, "hidmaestro.inf");
            DeviceOrchestrator.SetupController(controllerIndex, profile, infPath);
            Console.WriteLine($"OK ({setupSw.ElapsedMilliseconds}ms)");
        } // end if (!inputLoopOnly)

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
        byte[] gipDescBytes = Convert.FromHexString("05010905a101a10009300931150027ffff0000950275108102c0a10009330934150027ffff0000950275108102c005010932150026ff039501750a81021500250075069501810305010935150026ff039501750a81021500250075069501810305091901290a950a750181021500250075069501810305010939150125083500463b0166140075049501814275049501150025003500450065008103a102050f0997150025017504950191021500250091030970150025647508950491020950660110550e26ff009501910209a7910265005500097c9102c005010980a10009851500250195017501810215002500750795018103c005060920150026ff00750895018102c0");
        var gipBuilder = HidReportBuilder.Parse(gipDescBytes);
        Console.WriteLine("  GIP builder layout:");
        gipBuilder.PrintLayout();
        Console.WriteLine($"    RightTrigger: {(gipBuilder.RightTrigger != null ? $"bit {gipBuilder.RightTrigger.BitOffset}, {gipBuilder.RightTrigger.BitSize}b" : "NULL!")}");
        byte[] testGip = gipBuilder.BuildReport(rightTrigger: 0.8);
        Console.WriteLine($"    Test RT=0.8: bytes[10-11] = 0x{testGip[10]:X2}{testGip[11]:X2} = {(testGip[10] | (testGip[11] << 8)) & 0x3FF}");

        // BLE descriptor for report builder
        byte[] bleDesc = descriptor;
        var reportBuilder = profile.UsesUpperFilter
            ? HidReportBuilder.Parse(bleDesc)
            : HidReportBuilder.Parse(profileDesc ?? descriptor);
        reportBuilder.PrintLayout();

        setupSw.Stop();
        Console.WriteLine($"\n  Setup complete in {setupSw.ElapsedMilliseconds}ms");

        if (setupOnly) { h?.Dispose(); return 0; } // Multi-controller: setup done, input loop runs separately

        Console.WriteLine("  Sending input. Type a profile ID to switch, 'quit' to exit.\n");
        timeBeginPeriod(1); // Enable 1ms timer resolution for 1000 Hz loop

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

        // Shared memory IPC
        int ctrlIndex = controllerIndex;
        IntPtr sharedView = IntPtr.Zero;
        uint sharedMemSeqNo = 0;
        try
        {
            sharedView = SharedMemoryIO.EnsureInputMapping(ctrlIndex);
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
            double speed = 0.5 + ctrlIndex * 0.3;
            double direction = (ctrlIndex % 2 == 0) ? 1.0 : -1.0;
            double phase = ctrlIndex * Math.PI / 2;
            double angle = t * Math.PI * 2 * speed * direction + phase;

            double lxNorm = 0.5 + 0.46 * Math.Sin(angle);
            double lyNorm = 0.5 + 0.46 * Math.Cos(angle);
            uint btnMask = ((int)t % 2 == 0) ? 0x01u : 0x00u;

            double triggerPhase = (t % 15.0);
            double ltVal, rtVal;
            if (profile.HasCombinedTriggers)
            {
                if (triggerPhase < 3.0)
                    ltVal = 0.5 - 0.5 * (triggerPhase / 3.0);
                else if (triggerPhase < 6.0)
                    ltVal = 0.5 * ((triggerPhase - 3.0) / 3.0);
                else if (triggerPhase < 9.0)
                    ltVal = 0.5 + 0.5 * ((triggerPhase - 6.0) / 3.0);
                else if (triggerPhase < 12.0)
                    ltVal = 1.0 - 0.5 * ((triggerPhase - 9.0) / 3.0);
                else
                    ltVal = 0.5;
                rtVal = 0.0;
            }
            else
            {
                if (triggerPhase < 3.0)
                    { ltVal = triggerPhase / 3.0; rtVal = 0.0; }
                else if (triggerPhase < 6.0)
                    { ltVal = 1.0 - (triggerPhase - 3.0) / 3.0; rtVal = 0.0; }
                else if (triggerPhase < 9.0)
                    { ltVal = 0.0; rtVal = (triggerPhase - 6.0) / 3.0; }
                else if (triggerPhase < 12.0)
                    { ltVal = (triggerPhase - 9.0) / 3.0; rtVal = 1.0; }
                else
                    { ltVal = 1.0 - (triggerPhase - 12.0) / 3.0; rtVal = 1.0 - (triggerPhase - 12.0) / 3.0; }
            }

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

            if (sharedView != IntPtr.Zero)
            {
                int dataStart = reportBuilder.InputReportId != 0 ? 1 : 0;
                int dataLen = Math.Min(inputReport.Length - dataStart, 64);

                byte[] gipData = new byte[14];
                BitConverter.GetBytes((ushort)(lxNorm * 65535)).CopyTo(gipData, 0);
                BitConverter.GetBytes((ushort)(lyNorm * 65535)).CopyTo(gipData, 2);
                BitConverter.GetBytes((ushort)(0.5 * 65535)).CopyTo(gipData, 4);
                BitConverter.GetBytes((ushort)(0.5 * 65535)).CopyTo(gipData, 6);
                BitConverter.GetBytes((ushort)(sepLt * 1023)).CopyTo(gipData, 8);
                BitConverter.GetBytes((ushort)(sepRt * 1023)).CopyTo(gipData, 10);
                byte btnLow = 0;
                if ((btnMask & 0x001) != 0) btnLow |= 0x01;
                if ((btnMask & 0x002) != 0) btnLow |= 0x02;
                if ((btnMask & 0x004) != 0) btnLow |= 0x04;
                if ((btnMask & 0x008) != 0) btnLow |= 0x08;
                if ((btnMask & 0x010) != 0) btnLow |= 0x10;
                if ((btnMask & 0x020) != 0) btnLow |= 0x20;
                if ((btnMask & 0x100) != 0) btnLow |= 0x40;
                if ((btnMask & 0x200) != 0) btnLow |= 0x80;
                gipData[12] = btnLow;
                byte btnHigh = 0;
                if ((btnMask & 0x040) != 0) btnHigh |= 0x01;
                if ((btnMask & 0x080) != 0) btnHigh |= 0x02;
                gipData[13] = btnHigh;

                byte[] nativeData = new byte[dataLen];
                Array.Copy(inputReport, dataStart, nativeData, 0, dataLen);

                SharedMemoryIO.WriteInputFrame(sharedView, ref sharedMemSeqNo, nativeData, dataLen, gipData);
            }
            bool ok = true;

            // Send GIP-format data to XUSB companion for XInput
            if (xh != null)
            {
                byte[] xusbIn = new byte[17];
                xusbIn[0] = 0x01; xusbIn[1] = 0x01; xusbIn[2] = 0x00;
                ushort xLx = (ushort)(lxNorm * 65535);
                ushort xLy = (ushort)(lyNorm * 65535);
                ushort xRx = (ushort)(0.5 * 65535);
                ushort xRy = (ushort)(0.5 * 65535);
                ushort xLt = (ushort)(sepLt * 1023);
                ushort xRt = (ushort)(sepRt * 1023);
                BitConverter.GetBytes(xLx).CopyTo(xusbIn, 3);
                BitConverter.GetBytes(xLy).CopyTo(xusbIn, 5);
                BitConverter.GetBytes(xRx).CopyTo(xusbIn, 7);
                BitConverter.GetBytes(xRy).CopyTo(xusbIn, 9);
                BitConverter.GetBytes(xLt).CopyTo(xusbIn, 11);
                BitConverter.GetBytes(xRt).CopyTo(xusbIn, 13);
                byte btnLow = 0;
                if ((btnMask & 0x01) != 0) btnLow |= 0x01;
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
                if (failCount > 100) break;
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

            Thread.Sleep(1);
        }

        Console.WriteLine($"\n\n  Sent {count} reports.");

        // Restore HidHide state on exit
        {
            string hidHideCli = @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe";
            if (File.Exists(hidHideCli))
                RunProcess(hidHideCli, "--cloak-off --inv-off", timeoutMs: 3000);
        }

        Console.Write("\n  Cleaning up devices... ");
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
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "profiles");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
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

        Console.Write("  Setting FriendlyName on root... ");
        DeviceProperties.SetDeviceFriendlyName(@"ROOT\HID_IG_00\0000", name);
        Console.WriteLine();

        Console.Write("  Setting BusReportedDeviceDesc on root... ");
        DeviceProperties.SetBusReportedDeviceDesc(@"ROOT\HID_IG_00\0000", name);
        Console.WriteLine();

        uint locResult = CM_Locate_DevNodeW(out uint devInst, @"ROOT\HID_IG_00\0000", 0);
        Console.WriteLine($"  Locate root: result={locResult} inst={devInst}");

        if (locResult == 0)
        {
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

        Console.WriteLine("\nPumping messages for 5s...");
        var sw2 = Stopwatch.StartNew();
        while (sw2.ElapsedMilliseconds < 5000)
        {
            while (PeekMessageW(out MSG msg, IntPtr.Zero, 0, 0, 1))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            Thread.Sleep(50);
        }

        Console.WriteLine($"\nFinal: RawControllers={found.Count + existing.Count} Gamepads={foundGamepads.Count + existingGamepads.Count}");
        return 0;
    }

    // ── Rumble / output passthrough sender (verification only) ──

    [DllImport("XInput1_4.dll", EntryPoint = "XInputSetState")]
    static extern uint XInputSetState(uint dwUserIndex, ref XINPUT_VIBRATION pVibration);

    [DllImport("XInput1_4.dll", EntryPoint = "XInputGetState")]
    static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_VIBRATION
    {
        public ushort wLeftMotorSpeed;
        public ushort wRightMotorSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    static int CountConnectedXInputSlots()
    {
        int count = 0;
        for (uint slot = 0; slot < 4; slot++)
        {
            if (XInputGetState(slot, out _) == 0) count++;
        }
        return count;
    }

    static bool ProfileTakesXInputSlot(ControllerProfile? p) =>
        p != null && p.VendorId == 0x045E;

    static void PumpMessagesFor(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            while (PeekMessageW(out MSG msg, IntPtr.Zero, 0, 0, 1))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            Thread.Sleep(10);
        }
    }

    static int RumbleTest(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  rumbletest xinput <slot>                    — pulse XInput rumble on slot");
            Console.WriteLine("  rumbletest hid <vid_hex> <pid_hex> <reportId_hex> <hex bytes...>");
            Console.WriteLine("Examples:");
            Console.WriteLine("  rumbletest xinput 0");
            Console.WriteLine("  rumbletest hid 054C 0CE6 02 FF 80 80 00 00 00 00 00 00 00");
            return 1;
        }

        if (args[0] == "xinput")
        {
            uint slot = args.Length > 1 ? uint.Parse(args[1]) : 0;
            Console.WriteLine($"XInputSetState slot={slot}: pulsing rumble for 1.5s...");
            var v = new XINPUT_VIBRATION { wLeftMotorSpeed = 0xC000, wRightMotorSpeed = 0x4000 };
            uint r = XInputSetState(slot, ref v);
            Console.WriteLine($"  → return: 0x{r:X8} ({(r == 0 ? "ERROR_SUCCESS" : "ERROR")})");
            Thread.Sleep(1500);
            v = new XINPUT_VIBRATION { wLeftMotorSpeed = 0, wRightMotorSpeed = 0 };
            XInputSetState(slot, ref v);
            Console.WriteLine("  Stopped.");
            return r == 0 ? 0 : 2;
        }

        if (args[0] == "hidorder")
        {
            ushort vid = args.Length > 1 ? ushort.Parse(args[1], System.Globalization.NumberStyles.HexNumber) : (ushort)0x054C;
            ushort pid = args.Length > 2 ? ushort.Parse(args[2], System.Globalization.NumberStyles.HexNumber) : (ushort)0x0CE6;

            Console.WriteLine($"-- HID enumeration order (HidDevice.GetDeviceSelector / FindAllAsync) for VID=0x{vid:X4} PID=0x{pid:X4} --\n");

            string selector = Windows.Devices.HumanInterfaceDevice.HidDevice.GetDeviceSelector(0x0001, 0x0005, vid, pid);
            var devices = Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector).GetAwaiter().GetResult();

            int n = 0;
            foreach (var info in devices)
            {
                Console.WriteLine($"  [{n++}] id: {info.Id}");
                var h = CreateFileW(info.Id, 0, 0x03, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (!h.IsInvalid)
                {
                    try
                    {
                        byte[] buf = new byte[512];
                        string serial = HidD_GetSerialNumberString(h, buf, (uint)buf.Length)
                            ? UnicodeBufToString(buf) : "(none)";
                        Console.WriteLine($"      serial: {serial}");
                    }
                    finally { h.Dispose(); }
                }
                else
                {
                    Console.WriteLine($"      (could not open to read serial — busy or denied)");
                }
            }
            Console.WriteLine($"\nTotal: {n} device(s)");
            return 0;
        }

        if (args[0] == "wgi")
        {
            Console.WriteLine("-- WGI enumeration order --\n");
            var seen = new List<Windows.Gaming.Input.RawGameController>();
            Windows.Gaming.Input.RawGameController.RawGameControllerAdded += (_, rc) => seen.Add(rc);

            _ = Windows.Gaming.Input.RawGameController.RawGameControllers;

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000)
            {
                while (PeekMessageW(out MSG msg, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
                Thread.Sleep(50);
            }

            var listed = Windows.Gaming.Input.RawGameController.RawGameControllers;
            var combined = new List<Windows.Gaming.Input.RawGameController>(seen);
            foreach (var rc in listed)
                if (!combined.Any(x => x.NonRoamableId == rc.NonRoamableId))
                    combined.Add(rc);

            int n = 0;
            foreach (var rc in combined)
            {
                Console.WriteLine($"  [{n++}] {rc.DisplayName}");
                Console.WriteLine($"      VID=0x{rc.HardwareVendorId:X4} PID=0x{rc.HardwareProductId:X4}");
                Console.WriteLine($"      NonRoamableId={rc.NonRoamableId}");
                Console.WriteLine($"      Axes={rc.AxisCount} Buttons={rc.ButtonCount} Switches={rc.SwitchCount}");
            }
            Console.WriteLine($"\nTotal: {n} controller(s)");
            return 0;
        }

        if (args[0] == "serials")
        {
            if (args.Length < 3)
            {
                Console.WriteLine("usage: rumbletest serials <vid_hex> <pid_hex>");
                Console.WriteLine("example: rumbletest serials 054C 0CE6");
                return 1;
            }
            ushort vid = ushort.Parse(args[1], System.Globalization.NumberStyles.HexNumber);
            ushort pid = ushort.Parse(args[2], System.Globalization.NumberStyles.HexNumber);

            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr devs = SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                0x10 | 0x02);
            if (devs == new IntPtr(-1)) { Console.WriteLine("SetupDiGetClassDevs failed"); return 2; }

            int found = 0;
            try
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();
                uint i = 0;
                while (SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref hidGuid, i++, ref ifData))
                {
                    SetupDiGetDeviceInterfaceDetailW(devs, ref ifData, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
                    if (needed == 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        Marshal.WriteInt32(detail, 0, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetailW(devs, ref ifData, detail, needed, out _, IntPtr.Zero))
                            continue;
                        string? path = Marshal.PtrToStringUni(detail + 4);
                        if (path == null) continue;

                        var h = CreateFileW(path, 0, 0x03, IntPtr.Zero, 3, 0, IntPtr.Zero);
                        if (h.IsInvalid) continue;
                        try
                        {
                            var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                            if (!HidD_GetAttributes(h, ref attrs)) continue;
                            if (attrs.VendorID != vid || attrs.ProductID != pid) continue;

                            found++;
                            byte[] buf = new byte[512];
                            string serial = HidD_GetSerialNumberString(h, buf, (uint)buf.Length)
                                ? UnicodeBufToString(buf) : "(none)";
                            string product = HidD_GetProductString(h, buf, (uint)buf.Length)
                                ? UnicodeBufToString(buf) : "(none)";
                            string manuf = HidD_GetManufacturerString(h, buf, (uint)buf.Length)
                                ? UnicodeBufToString(buf) : "(none)";

                            Console.WriteLine($"#{found}");
                            Console.WriteLine($"  path:    {path}");
                            Console.WriteLine($"  vid/pid: 0x{attrs.VendorID:X4}/0x{attrs.ProductID:X4}  ver=0x{attrs.VersionNumber:X4}");
                            Console.WriteLine($"  manuf:   {manuf}");
                            Console.WriteLine($"  product: {product}");
                            Console.WriteLine($"  serial:  {serial}");
                            Console.WriteLine();
                        }
                        finally { h.Dispose(); }
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(devs); }

            Console.WriteLine($"Total {found} HID device(s) matching VID=0x{vid:X4} PID=0x{pid:X4}");
            return 0;
        }

        if (args[0] == "checkout")
        {
            int idx = args.Length > 1 ? int.Parse(args[1]) : 0;
            string mn = $@"Global\HIDMaestroOutput{idx}";
            const uint FILE_MAP_READ = 0x0004;
            IntPtr h = OpenFileMappingNative(FILE_MAP_READ, false, mn);
            if (h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Console.WriteLine($"OpenFileMapping('{mn}') FAILED  err={err}");
                return 2;
            }
            IntPtr v = MapViewOfFile(h, FILE_MAP_READ, 0, 0, (UIntPtr)SHARED_OUTPUT_SIZE);
            if (v == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Console.WriteLine($"MapViewOfFile FAILED  err={err}");
                CloseHandleNative(h);
                return 3;
            }
            uint seq = (uint)Marshal.ReadInt32(v, 0);
            byte src = Marshal.ReadByte(v, 4);
            byte rid = Marshal.ReadByte(v, 5);
            ushort sz = (ushort)Marshal.ReadInt16(v, 6);
            Console.WriteLine($"  {mn}:  SeqNo={seq}  Source={src}  ReportId=0x{rid:X2}  DataSize={sz}");
            if (sz > 0 && sz <= 256)
            {
                byte[] data = new byte[Math.Min((int)sz, 32)];
                for (int i = 0; i < data.Length; i++) data[i] = Marshal.ReadByte(v, 8 + i);
                Console.WriteLine($"  Data: {string.Join(" ", data.Select(b => b.ToString("X2")))}");
            }
            UnmapViewOfFile(v);
            CloseHandleNative(h);
            return 0;
        }

        if (args[0] == "hid" && args.Length >= 4)
        {
            ushort vid = ushort.Parse(args[1], System.Globalization.NumberStyles.HexNumber);
            ushort pid = ushort.Parse(args[2], System.Globalization.NumberStyles.HexNumber);
            byte reportId = byte.Parse(args[3], System.Globalization.NumberStyles.HexNumber);
            var payload = new List<byte> { reportId };
            for (int i = 4; i < args.Length; i++)
                payload.Add(byte.Parse(args[i], System.Globalization.NumberStyles.HexNumber));

            Console.WriteLine($"HID write VID=0x{vid:X4} PID=0x{pid:X4} reportId=0x{reportId:X2} ({payload.Count} bytes)");
            var path = FindHidDevicePath(vid, pid);
            if (path == null) { Console.WriteLine("  ERROR: device not found"); return 2; }
            Console.WriteLine($"  Path: {path}");

            using var h = OpenHidDevice(path);
            if (h == null || h.IsInvalid) { Console.WriteLine("  ERROR: open failed"); return 3; }

            byte[] buf = payload.ToArray();
            bool ok = HidD_SetOutputReport(h, buf, (uint)buf.Length);
            Console.WriteLine($"  HidD_SetOutputReport → {ok}");
            return ok ? 0 : 4;
        }

        Console.WriteLine($"Unknown rumbletest mode: {args[0]}");
        return 1;
    }

    static string? FindHidDevicePath(ushort vid, ushort pid)
    {
        Guid hidGuid;
        HidD_GetHidGuid(out hidGuid);
        IntPtr devs = SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            0x10 | 0x02);
        if (devs == new IntPtr(-1)) return null;

        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA();
            ifData.cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();
            uint i = 0;
            while (SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref hidGuid, i++, ref ifData))
            {
                SetupDiGetDeviceInterfaceDetailW(devs, ref ifData, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
                if (needed == 0) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)needed);
                try
                {
                    Marshal.WriteInt32(detail, 0, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(devs, ref ifData, detail, needed, out _, IntPtr.Zero))
                        continue;
                    string? path = Marshal.PtrToStringUni(detail + 4);
                    if (path == null) continue;

                    var h = CreateFileW(path, 0, 0x03, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (h.IsInvalid) continue;

                    var attrs = new HIDD_ATTRIBUTES();
                    attrs.Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>();
                    bool ok = HidD_GetAttributes(h, ref attrs);
                    h.Dispose();
                    if (ok && attrs.VendorID == vid && attrs.ProductID == pid)
                        return path;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devs); }
        return null;
    }


    static SafeFileHandle? OpenHidDevice(string path)
    {
        const uint GENERIC_READ = 0x80000000;
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 0x00000001;
        const uint FILE_SHARE_WRITE = 0x00000002;
        const uint OPEN_EXISTING = 3;
        var handle = CreateFileW(path, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        return handle;
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

            if (HidD_GetPreparsedData(handle, out IntPtr ppd))
            {
                HidP_GetCaps(ppd, out HIDP_CAPS caps);
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

                    if (caps.NumberInputValueCaps > 0)
                    {
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
                    if (caps.NumberInputButtonCaps > 0)
                    {
                        ushort numBtns = caps.NumberInputButtonCaps;
                        byte[] btnBuf = new byte[72 * numBtns];
                        HidP_GetButtonCaps(0, btnBuf, ref numBtns, ppd);
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

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool HidD_GetSerialNumberString(SafeFileHandle HidDeviceObject, byte[] Buffer, uint BufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool HidD_GetManufacturerString(SafeFileHandle HidDeviceObject, byte[] Buffer, uint BufferLength);

    static string UnicodeBufToString(byte[] buf)
    {
        int n = 0;
        while (n + 1 < buf.Length && (buf[n] != 0 || buf[n + 1] != 0)) n += 2;
        return System.Text.Encoding.Unicode.GetString(buf, 0, n);
    }

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
