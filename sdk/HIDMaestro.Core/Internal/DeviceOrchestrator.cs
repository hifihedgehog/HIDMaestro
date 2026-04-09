using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>
/// Per-controller setup orchestrator. Owns the full sequence of steps that
/// turns a profile into a live virtual HID device. Handles all three profile
/// paths: xinputhid companion-only, non-xinputhid Xbox (XUSB companion), and
/// plain HID.
/// </summary>
internal static class DeviceOrchestrator
{
    private const string REG_BASE = @"SOFTWARE\HIDMaestro";
    private static string RegPathForIndex(int index) =>
        $@"{REG_BASE}\Controller{index}";

    private static bool s_ghostsCleaned;

    // ════════════════════════════════════════════════════════════════════
    //  P/Invoke: SetupAPI
    // ════════════════════════════════════════════════════════════════════

    [DllImport("SetupAPI.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);

    [DllImport("SetupAPI.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiCreateDeviceInfoW")]
    private static extern bool SetupDiCreateDeviceInfoW(IntPtr DeviceInfoSet, string DeviceName,
        ref Guid ClassGuid, string DeviceDescription, IntPtr hwndParent, int CreationFlags, IntPtr DeviceInfoData);

    [DllImport("SetupAPI.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiSetDeviceRegistryPropertyW")]
    private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
        int Property, byte[] PropertyBuffer, uint PropertyBufferSize);

    [DllImport("SetupAPI.dll", SetLastError = true, EntryPoint = "SetupDiCallClassInstaller")]
    private static extern bool SetupDiCallClassInstaller(int InstallFunction, IntPtr DeviceInfoSet, IntPtr DeviceInfoData);

    [DllImport("SetupAPI.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    // ════════════════════════════════════════════════════════════════════
    //  P/Invoke: CfgMgr32
    // ════════════════════════════════════════════════════════════════════

    [DllImport("CfgMgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("CfgMgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Set_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY propertyKey,
        uint propertyType, byte[] propertyBuffer, uint propertyBufferSize, uint ulFlags);

    [DllImport("CfgMgr32.dll", SetLastError = true)]
    private static extern uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    private static extern uint CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    private const uint CM_LOCATE_DEVNODE_PHANTOM = 0x00000001;
    private const uint DN_HAS_PROBLEM = 0x00000400;
    private const uint DN_STARTED = 0x00000008;

    // ════════════════════════════════════════════════════════════════════
    //  Well-known GUIDs
    // ════════════════════════════════════════════════════════════════════

    private static readonly Guid HidClassGuid = new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
    private static readonly Guid SystemClassGuid = new("4D36E97D-E325-11CE-BFC1-08002BE10318");
    private static readonly Guid XusbInterfaceGuid = new("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");

    // ════════════════════════════════════════════════════════════════════
    //  Process helper (silent — no Console output)
    // ════════════════════════════════════════════════════════════════════

    private static (int exitCode, string output) RunProcess(string fileName, string args, int timeoutMs = 30_000)
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
        return (proc.ExitCode, stdout + stderr);
    }

    // ════════════════════════════════════════════════════════════════════
    //  CleanupGhostDevices
    // ════════════════════════════════════════════════════════════════════

    private static void CleanupGhostDevices()
    {
        // Remove known non-VID_ enumerators
        string[] fixedPrefixes = { @"ROOT\HID_IG_00", @"ROOT\HIDCLASS", @"ROOT\XnaComposite" };
        foreach (var prefix in fixedPrefixes)
        {
            for (int idx = 0; idx < 10; idx++)
            {
                string instId = $@"{prefix}\{idx:D4}";
                if (CM_Locate_DevNodeW(out _, instId, 0) == 0)
                    DeviceManager.RemoveDevice(instId);
            }
        }

        // VID_* enumerators under ROOT
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT");
            if (enumKey != null)
            {
                foreach (var subName in enumKey.GetSubKeyNames())
                {
                    if (!subName.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)) continue;
                    using var vidKey = enumKey.OpenSubKey(subName);
                    if (vidKey == null) continue;
                    foreach (var instName in vidKey.GetSubKeyNames())
                    {
                        string instId = $@"ROOT\{subName}\{instName}";
                        DeviceManager.RemoveDevice(instId);
                    }
                }
            }
        }
        catch { }

        // Clean ghost XUSB and WinExInput interface registrations
        string[] interfaceGuids = {
            @"{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}",
            @"{6c53d5fd-6480-440f-b618-476750c5e1a6}",
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
                    bool isOurDevice = subName.Contains("ROOT#VID_") || subName.Contains("ROOT#HIDCLASS") ||
                        subName.Contains("ROOT#HID_IG");
                    if (!isOurDevice && subName.Contains("ROOT#SYSTEM#"))
                    {
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

    // ════════════════════════════════════════════════════════════════════
    //  DisableGhostXusbInterfaces
    // ════════════════════════════════════════════════════════════════════

    private static void DisableGhostXusbInterfaces()
    {
        try
        {
            using var xusbKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}");
            if (xusbKey == null) return;

            foreach (var subName in xusbKey.GetSubKeyNames())
            {
                if (subName.Contains("ROOT#", StringComparison.OrdinalIgnoreCase)) continue;
                if (!subName.Contains("HID#", StringComparison.OrdinalIgnoreCase)) continue;

                string devPath = subName;
                int guidStart = devPath.LastIndexOf("#{");
                if (guidStart < 0) continue;
                devPath = devPath.Substring(0, guidStart);
                devPath = devPath.Replace("##?#", "");
                devPath = devPath.Replace('#', '\\');

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

                bool isGhost = false;
                uint devInst = 0;
                if (CM_Locate_DevNodeW(out devInst, devPath, 0) != 0)
                {
                    if (CM_Locate_DevNodeW(out devInst, devPath, CM_LOCATE_DEVNODE_PHANTOM) == 0)
                        isGhost = true;
                    else
                        continue;
                }
                else
                {
                    if (CM_Get_DevNode_Status(out uint status, out _, devInst, 0) == 0)
                    {
                        if ((status & DN_HAS_PROBLEM) != 0 || (status & DN_STARTED) == 0)
                            isGhost = true;
                        else
                            continue;
                    }
                    else
                        isGhost = true;
                }
                if (!isGhost) continue;

                CM_Disable_DevNode(devInst, 0);

                // Clean XUSB DeviceClasses entry
                try
                {
                    string regPath = @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}";
                    using var classKey = Registry.LocalMachine.OpenSubKey(regPath, writable: true);
                    if (classKey != null)
                    {
                        try { classKey.DeleteSubKeyTree(subName); }
                        catch
                        {
                            try
                            {
                                string fullRegPath = $@"HKLM\{regPath}\{subName}";
                                RunProcess("reg.exe", $"delete \"{fullRegPath}\" /f", timeoutMs: 3000);
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                // Clean WinExInput entry
                try
                {
                    string winExPath = @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{6c53d5fd-6480-440f-b618-476750c5e1a6}";
                    using var winExKey = Registry.LocalMachine.OpenSubKey(winExPath, writable: true);
                    if (winExKey != null)
                    {
                        string devPathHash = devPath.Replace('\\', '#');
                        foreach (var weSub in winExKey.GetSubKeyNames())
                        {
                            if (weSub.Contains(devPathHash, StringComparison.OrdinalIgnoreCase))
                            {
                                try { winExKey.DeleteSubKeyTree(weSub); }
                                catch
                                {
                                    try
                                    {
                                        string fullRegPath = $@"HKLM\{winExPath}\{weSub}";
                                        RunProcess("reg.exe", $"delete \"{fullRegPath}\" /f", timeoutMs: 3000);
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
        catch { }
    }

    // ════════════════════════════════════════════════════════════════════
    //  EnsureGameInputService
    // ════════════════════════════════════════════════════════════════════

    private static void EnsureGameInputService()
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
        catch { }
    }

    // ════════════════════════════════════════════════════════════════════
    //  WriteGameInputRegistry
    // ════════════════════════════════════════════════════════════════════

    private static void WriteGameInputRegistry(ControllerProfile profile)
    {
        string deviceKey = $@"SYSTEM\CurrentControlSet\Control\GameInput\Devices\{profile.VendorId:X4}{profile.ProductId:X4}00010005";
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

        bool combinedTriggers = profile.HasCombinedTriggers;

        SetAxis("LeftThumbstickX", 0);
        SetAxis("LeftThumbstickY", 1, invert: true);
        SetAxis("RightThumbstickX", 2);
        SetAxis("RightThumbstickY", 3, invert: true);

        if (combinedTriggers)
        {
            SetAxis("LeftTrigger", 5);
            SetAxis("RightTrigger", 6);
        }
        else
        {
            SetAxis("LeftTrigger", 4);
            SetAxis("RightTrigger", 5);
        }

        SetButton("A", 0); SetButton("B", 1); SetButton("X", 2); SetButton("Y", 3);
        SetButton("LeftShoulder", 4); SetButton("RightShoulder", 5);
        SetButton("View", 6); SetButton("Menu", 7);
        SetButton("LeftThumbstickButton", 8); SetButton("RightThumbstickButton", 9);

        SetDPad("DPadUp", "Up"); SetDPad("DPadDown", "Down");
        SetDPad("DPadLeft", "Left"); SetDPad("DPadRight", "Right");
    }

    // ════════════════════════════════════════════════════════════════════
    //  WriteInstanceConfig
    // ════════════════════════════════════════════════════════════════════

    private static void WriteInstanceConfig(int controllerIndex, ControllerProfile profile)
    {
        byte[] descriptor = profile.GetDescriptorBytes()!;
        int inputReportLen = (profile.InputReportSize ?? 0) > 0
            ? profile.InputReportSize!.Value
            : ComputeInputReportByteLength(descriptor);

        // FunctionMode=1 for non-xinputhid Xbox (XUSB on main device),
        // FunctionMode=0 for everything else.
        bool funcMode = profile.VendorId == 0x045E && !profile.UsesUpperFilter;

        string instancePath = RegPathForIndex(controllerIndex);
        using var key = Registry.LocalMachine.CreateSubKey(instancePath);
        string instanceSuffix = controllerIndex > 0 ? $"\\{controllerIndex:D4}" : "\\0000";
        key.SetValue("DeviceInstanceId",
            $@"ROOT\VID_{profile.VendorId:X4}&PID_{profile.ProductId:X4}&IG_00{instanceSuffix}",
            RegistryValueKind.String);
        key.SetValue("FunctionMode", funcMode ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("ReportDescriptor", descriptor, RegistryValueKind.Binary);
        key.SetValue("VendorId", (int)profile.VendorId, RegistryValueKind.DWord);
        key.SetValue("ProductId", (int)profile.ProductId, RegistryValueKind.DWord);
        key.SetValue("VersionNumber", 0x0100, RegistryValueKind.DWord);
        if (profile.ProductString != null)
            key.SetValue("ProductString", profile.ProductString, RegistryValueKind.String);
        if (inputReportLen > 0)
            key.SetValue("InputReportByteLength", inputReportLen, RegistryValueKind.DWord);

        string displayName = profile.DeviceDescription ?? profile.ProductString ?? "HIDMaestro Controller";
        key.SetValue("DeviceDescription", displayName, RegistryValueKind.String);

        // Joystick OEM display name (joy.cpl)
        string oemKeyPath = $@"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{profile.VendorId:X4}&PID_{profile.ProductId:X4}";
        try
        {
            using var oem = Registry.LocalMachine.CreateSubKey(oemKeyPath);
            oem?.SetValue("OEMName", displayName, RegistryValueKind.String);
            oem?.DeleteValue("OEMData", false);
            try { Registry.LocalMachine.DeleteSubKeyTree($@"{oemKeyPath}\Axes", throwOnMissingSubKey: false); } catch { }
        }
        catch { }
    }

    // ════════════════════════════════════════════════════════════════════
    //  CreateGamepadCompanion (xinputhid path)
    // ════════════════════════════════════════════════════════════════════

    private static string? CreateGamepadCompanion(int controllerIndex, ControllerProfile profile)
    {
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
                    if (CM_Locate_DevNodeW(out _, candidate, 0) != 0) continue;
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
            // 2. Create a new companion device
            var hidGuid = HidClassGuid;
            IntPtr dis = SetupDiCreateDeviceInfoList(ref hidGuid, IntPtr.Zero);
            if (dis != new IntPtr(-1))
            {
                byte[] diBuf = new byte[32];
                BitConverter.GetBytes(IntPtr.Size == 8 ? 32 : 28).CopyTo(diBuf, 0);
                var diHandle = GCHandle.Alloc(diBuf, GCHandleType.Pinned);
                string gpHw = $"root\\VID_{gpVid}&PID_{hwPid}&IG_00\0root\\HIDMaestroGamepad\0root\\HIDMaestro\0\0";
                byte[] gpHwBytes = Encoding.Unicode.GetBytes(gpHw);
                if (SetupDiCreateDeviceInfoW(dis, gpEnumerator, ref hidGuid,
                    "HIDMaestro Gamepad", IntPtr.Zero, 1, diHandle.AddrOfPinnedObject()))
                {
                    SetupDiSetDeviceRegistryPropertyW(dis, diHandle.AddrOfPinnedObject(), 1, gpHwBytes, (uint)gpHwBytes.Length);

                    string gpCompatBase = $"root\\HIDMaestroGamepad\0root\\HIDMaestro\0\0";
                    if (profile.Connection == "bluetooth")
                        gpCompatBase = $"BTHLEDEVICE\\{{00001812-0000-1000-8000-00805f9b34fb}}_Dev_VID&02{gpVid}_PID&{gpPid}\0" + gpCompatBase;
                    byte[] gpCompatBytes = Encoding.Unicode.GetBytes(gpCompatBase);
                    SetupDiSetDeviceRegistryPropertyW(dis, diHandle.AddrOfPinnedObject(), 2, gpCompatBytes, (uint)gpCompatBytes.Length);

                    SetupDiCallClassInstaller(0x19, dis, diHandle.AddrOfPinnedObject()); // DIF_REGISTERDEVICE
                    SetupDiCallClassInstaller(0x02, dis, diHandle.AddrOfPinnedObject()); // DIF_INSTALLDEVICE
                }
                diHandle.Free();
                SetupDiDestroyDeviceInfoList(dis);
            }

            // 3. Find the freshly-created instance (live, no ControllerIndex yet)
            try
            {
                using var gpEnum = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Enum\ROOT\{gpEnumerator}");
                if (gpEnum != null)
                {
                    foreach (var inst in gpEnum.GetSubKeyNames())
                    {
                        string candidate = $@"ROOT\{gpEnumerator}\{inst}";
                        if (CM_Locate_DevNodeW(out _, candidate, 0) != 0) continue;
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

        // 4. Restart so it picks up latest descriptor + VID/PID
        if (gpInstId != null)
        {
            DeviceManager.RestartDevice(gpInstId);
            Thread.Sleep(2000);
        }

        return gpInstId;
    }

    // ════════════════════════════════════════════════════════════════════
    //  CreateXusbCompanion (non-xinputhid Xbox path)
    // ════════════════════════════════════════════════════════════════════

    private static string? CreateXusbCompanion(int controllerIndex, ControllerProfile profile)
    {
        // Find live companion for this controllerIndex
        string? xusbInstId = null;
        try
        {
            using var hmEnum = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT\HMCOMPANION");
            if (hmEnum != null)
            {
                foreach (var inst in hmEnum.GetSubKeyNames())
                {
                    string candidate = $@"ROOT\HMCOMPANION\{inst}";
                    if (CM_Locate_DevNodeW(out _, candidate, 0) != 0) continue;
                    using var dp = hmEnum.OpenSubKey($@"{inst}\Device Parameters");
                    if (dp?.GetValue("ControllerIndex") is int ci && ci == controllerIndex)
                    { xusbInstId = candidate; break; }
                }
            }
        }
        catch { }

        bool xusbExists = xusbInstId != null;
        if (!xusbExists)
        {
            var sysGuid = SystemClassGuid;
            IntPtr dis = SetupDiCreateDeviceInfoList(ref sysGuid, IntPtr.Zero);
            if (dis != new IntPtr(-1))
            {
                byte[] diBuf = new byte[32];
                BitConverter.GetBytes(IntPtr.Size == 8 ? 32 : 28).CopyTo(diBuf, 0);
                var diHandle = GCHandle.Alloc(diBuf, GCHandleType.Pinned);
                string xusbHw = "root\\HIDMaestroXUSB\0\0";
                byte[] xusbHwBytes = Encoding.Unicode.GetBytes(xusbHw);
                string companionDesc = profile.DeviceDescription ?? profile.ProductString ?? "Controller";
                if (SetupDiCreateDeviceInfoW(dis, "HMCompanion", ref sysGuid,
                    companionDesc, IntPtr.Zero, 1, diHandle.AddrOfPinnedObject()))
                {
                    SetupDiSetDeviceRegistryPropertyW(dis, diHandle.AddrOfPinnedObject(), 1, xusbHwBytes, (uint)xusbHwBytes.Length);
                    SetupDiCallClassInstaller(0x19, dis, diHandle.AddrOfPinnedObject()); // DIF_REGISTERDEVICE
                }
                diHandle.Free();
                SetupDiDestroyDeviceInfoList(dis);

                // Find the actual instance PnP created (live, no ControllerIndex yet)
                try
                {
                    using var hmEnum = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT\HMCOMPANION");
                    if (hmEnum != null)
                    {
                        foreach (var inst in hmEnum.GetSubKeyNames())
                        {
                            string candidate = $@"ROOT\HMCOMPANION\{inst}";
                            if (CM_Locate_DevNodeW(out _, candidate, 0) != 0) continue;
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
                }
                catch { }

                // Trigger driver binding with pnputil restart loop
                if (xusbInstId == null) xusbInstId = $@"ROOT\HMCOMPANION\{controllerIndex:D4}";
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    RunProcess("pnputil.exe", $"/restart-device \"{xusbInstId}\"", timeoutMs: 5000);
                    if (DeviceManager.WaitForDeviceInterface(xusbInstId, XusbInterfaceGuid, timeoutMs: 5000))
                        break;
                    DeviceManager.RestartDevice(xusbInstId);
                }
            }
        }
        else
        {
            DeviceManager.RestartDevice(xusbInstId!);
        }

        // Set companion name to match the controller profile
        if (xusbInstId != null)
        {
            string companionName = profile.DeviceDescription ?? profile.ProductString ?? "Controller";
            DeviceProperties.SetBusReportedDeviceDesc(xusbInstId, companionName);
            DeviceProperties.SetDeviceFriendlyName(xusbInstId, companionName);
        }

        return xusbInstId;
    }

    // ════════════════════════════════════════════════════════════════════
    //  SetBusTypeGuidUsb
    // ════════════════════════════════════════════════════════════════════

    private static void SetBusTypeGuidUsb()
    {
        var busTypeKey = new DEVPROPKEY
        {
            fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
            pid = 21 // DEVPKEY_Device_BusTypeGuid
        };
        byte[] usbBusGuid = new Guid("9d7debbc-c85d-11d1-9eb4-006008c3a19a").ToByteArray();

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
                    if (CM_Get_Child(out uint childInst, devInst, 0) == 0)
                    {
                        CM_Set_DevNode_PropertyW(childInst, ref busTypeKey, 0x0D,
                            usbBusGuid, (uint)usbBusGuid.Length, 0);
                    }
                }
            }
        }

        // Also scan VID_* enumerators dynamically
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT");
            if (enumKey != null)
            {
                foreach (var sub in enumKey.GetSubKeyNames())
                {
                    if (!sub.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)) continue;
                    using var vidKey = enumKey.OpenSubKey(sub);
                    if (vidKey == null) continue;
                    foreach (var inst in vidKey.GetSubKeyNames())
                    {
                        string devId = $@"ROOT\{sub}\{inst}";
                        if (CM_Locate_DevNodeW(out uint devInst, devId, 0) == 0)
                        {
                            CM_Set_DevNode_PropertyW(devInst, ref busTypeKey, 0x0D,
                                usbBusGuid, (uint)usbBusGuid.Length, 0);
                            if (CM_Get_Child(out uint childInst, devInst, 0) == 0)
                            {
                                CM_Set_DevNode_PropertyW(childInst, ref busTypeKey, 0x0D,
                                    usbBusGuid, (uint)usbBusGuid.Length, 0);
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }

    // ════════════════════════════════════════════════════════════════════
    //  SetupController — full orchestration
    // ════════════════════════════════════════════════════════════════════

    public static string? SetupController(
        int controllerIndex, ControllerProfile profile, string infPath)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (controllerIndex < 0) throw new ArgumentOutOfRangeException(nameof(controllerIndex));
        if (!profile.HasDescriptor)
            throw new ArgumentException(
                $"Profile '{profile.Id}' has no HID descriptor.", nameof(profile));

        // ── Step 0: pre-flight environment ───────────────────────────────
        SharedMemoryIO.EnsureInputMapping(controllerIndex);
        try { SharedMemoryIO.EnsureOutputMapping(controllerIndex); } catch { }

        // Once-per-session cleanup
        if (!s_ghostsCleaned)
        {
            CleanupGhostDevices();
            DisableGhostXusbInterfaces();

            // Clean HIDMaestro SYSTEM companions (never ViGEmBus/HidHide)
            for (int idx = 0; idx < 10; idx++)
            {
                string sysId = $@"ROOT\SYSTEM\{idx:D4}";
                if (CM_Locate_DevNodeW(out _, sysId, 0) == 0)
                {
                    try
                    {
                        using var key = Registry.LocalMachine.OpenSubKey(
                            $@"SYSTEM\CurrentControlSet\Enum\{sysId}");
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

            s_ghostsCleaned = true;
        }

        EnsureGameInputService();
        WriteGameInputRegistry(profile);

        // ── Step 1: per-instance registry config ────────────────────────
        WriteInstanceConfig(controllerIndex, profile);

        // ── Step 2: ensure driver is in the store ────────────────────────
        if (DriverBuilder.NeedsBuild() || !DriverBuilder.IsDriverInstalled())
        {
            if (!DriverBuilder.FullDeploy(rebuild: DriverBuilder.NeedsBuild()))
                throw new InvalidOperationException(
                    "Driver build/install failed. Run elevated and check logs.");
        }

        // ── Step 3: create device(s) ────────────────────────────────────
        string? mainInstanceId = null;
        string? companionId = null;

        if (profile.UsesUpperFilter)
        {
            // xinputhid path: companion-only (no main HID device)
            companionId = CreateGamepadCompanion(controllerIndex, profile);
        }
        else if (!profile.CompanionOnly)
        {
            // Plain HID or non-xinputhid Xbox: create main device node
            var result = DeviceNodeCreator.CreateDeviceNode(profile, infPath, controllerIndex);
            if (!result.Success || result.InstanceId == null)
                throw new InvalidOperationException(
                    $"DeviceNodeCreator.CreateDeviceNode failed for profile " +
                    $"'{profile.Id}' at index {controllerIndex}.");
            mainInstanceId = result.InstanceId;
        }

        // ── Step 4: wait for HID child + name finalization ───────────────
        Thread.Sleep(3000);
        string displayName = profile.DeviceDescription ?? profile.ProductString ?? "Controller";
        DeviceProperties.FixHidChildNames(displayName, controllerIndex);

        // Set names on root device — locate via Device Parameters\ControllerIndex
        try
        {
            using var rootEnum = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT");
            if (rootEnum != null)
            {
                bool found = false;
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
                        DeviceProperties.SetBusReportedDeviceDesc(rootId, displayName);
                        DeviceProperties.SetDeviceFriendlyName(rootId, displayName);
                        found = true; break;
                    }
                    if (found) break;
                }
            }
        }
        catch { }

        // Final name fix after xinputhid grandchild has fully appeared
        Thread.Sleep(2000);
        DeviceProperties.FixHidChildNames(displayName, controllerIndex);

        // ── Step 5: bus type + companions ─────────────────────────────────
        SetBusTypeGuidUsb();

        // Non-xinputhid Xbox: create XUSB companion for XInput
        if (profile.VendorId == 0x045E && !profile.UsesUpperFilter)
        {
            string? xusbId = CreateXusbCompanion(controllerIndex, profile);
            // For CompanionOnly profiles, the XUSB companion IS the device
            if (profile.CompanionOnly && companionId == null)
                companionId = xusbId;
        }

        // ── Step 6: final friendly name ──────────────────────────────────
        DeviceProperties.ApplyFriendlyNameForController(controllerIndex, displayName);

        // Return main instance ID, or companion ID for companion-only profiles
        return mainInstanceId ?? companionId;
    }

    // ════════════════════════════════════════════════════════════════════
    //  TeardownController
    // ════════════════════════════════════════════════════════════════════

    public static void TeardownController(int controllerIndex, string? instanceId)
    {
        try { SharedMemoryIO.DestroyController(controllerIndex); } catch { }

        if (!string.IsNullOrEmpty(instanceId))
        {
            try { DeviceManager.RemoveDevice(instanceId!); } catch { }
        }

        // Scan and remove companions by ControllerIndex
        string[] companionRoots = { "HMCOMPANION" };
        foreach (var root in companionRoots)
        {
            try
            {
                using var hmEnum = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Enum\ROOT\{root}");
                if (hmEnum == null) continue;
                foreach (var inst in hmEnum.GetSubKeyNames())
                {
                    string candidate = $@"ROOT\{root}\{inst}";
                    using var dp = hmEnum.OpenSubKey($@"{inst}\Device Parameters");
                    if (dp?.GetValue("ControllerIndex") is int ci && ci == controllerIndex)
                    {
                        try { DeviceManager.RemoveDevice(candidate); } catch { }
                    }
                }
            }
            catch { }
        }

        // Also scan VID_*&IG_00 companions (xinputhid gamepad companions)
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT");
            if (enumKey != null)
            {
                foreach (var sub in enumKey.GetSubKeyNames())
                {
                    if (!sub.Contains("&IG_00", StringComparison.OrdinalIgnoreCase)) continue;
                    using var vidKey = enumKey.OpenSubKey(sub);
                    if (vidKey == null) continue;
                    foreach (var instName in vidKey.GetSubKeyNames())
                    {
                        string candidate = $@"ROOT\{sub}\{instName}";
                        using var dp = vidKey.OpenSubKey($@"{instName}\Device Parameters");
                        if (dp?.GetValue("ControllerIndex") is int ci && ci == controllerIndex)
                        {
                            try { DeviceManager.RemoveDevice(candidate); } catch { }
                        }
                    }
                }
            }
        }
        catch { }

        // Clear the per-instance registry config
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(RegPathForIndex(controllerIndex), throwOnMissingSubKey: false);
        }
        catch { }
    }

    // ════════════════════════════════════════════════════════════════════
    //  ComputeInputReportByteLength
    // ════════════════════════════════════════════════════════════════════

    private static int ComputeInputReportByteLength(byte[] desc)
    {
        int reportSize = 0, reportCount = 0;
        long inputBits = 0;
        int colDepth = 0;
        for (int i = 0; i < desc.Length;)
        {
            byte prefix = desc[i];
            int bSize = prefix & 0x03;
            if (bSize == 3) bSize = 4;
            int bType = (prefix >> 2) & 0x03;
            int bTag = (prefix >> 4) & 0x0F;

            int dataValue = 0;
            for (int b = 0; b < bSize && i + 1 + b < desc.Length; b++)
                dataValue |= desc[i + 1 + b] << (b * 8);

            if (bType == 1 && bTag == 7) reportSize = dataValue;
            else if (bType == 1 && bTag == 9) reportCount = dataValue;
            else if (bType == 0 && bTag == 8)
                inputBits += (long)reportSize * reportCount;
            else if (bType == 0 && bTag == 10) colDepth++;
            else if (bType == 0 && bTag == 12)
            {
                colDepth--;
                if (colDepth == 0) break;
            }

            i += 1 + bSize;
        }
        return (int)((inputBits + 7) / 8);
    }
}
