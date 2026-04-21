using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>
/// Per-controller virtual device node creation. This is the heart of HIDMaestro
/// — it builds a root-enumerated PnP device of the right HID class with the
/// right hardware ID, writes the per-instance ControllerIndex, installs the
/// driver against it, and waits for the HID class to layer on top before
/// returning.
///
/// <para>Three enumerator paths exist, picked from the profile:</para>
/// <list type="bullet">
/// <item><b>xinputhid path</b> (Xbox Series controllers): hardware ID has
/// <c>&amp;IG_00</c> + <c>DriverPid</c> override that matches xinputhid's INF.
/// xinputhid auto-binds and presents the device as XInput.</item>
/// <item><b>Xbox legacy path</b> (Xbox 360 wired etc): hardware ID has
/// <c>&amp;IG_00</c> for HIDAPI/SDL3 filtering plus <c>USB\MS_COMP_XUSB10</c>
/// compatible IDs so WGI recognizes it as a gamepad. The XUSB companion
/// is created separately.</item>
/// <item><b>Plain HID path</b> (DualSense, generic gamepads): standard
/// HIDClass enumerator. No upper filters, no IG_, just our HID minidriver.</item>
/// </list>
///
/// <para>Critical race fixes baked in:</para>
/// <list type="bullet">
/// <item><b>ControllerIndex registry write:</b> the driver reads this from
/// <c>Device Parameters\ControllerIndex</c> at startup to know which
/// per-instance shared section to attach to.</item>
/// <item><b>HID-child wait:</b> after driver install, we wait for the HID
/// interface to actually arrive before returning, otherwise sequential
/// controller creation races and PnP arrival order doesn't match creation
/// order.</item>
/// </list>
///
/// <para><b>ContainerID note:</b> root-enumerated virtual devices get the
/// <c>{00000000-0000-0000-FFFF-FFFFFFFFFFFF}</c> null-sentinel container
/// on Win11 26200. Attempts to set a real ContainerID from user mode
/// (CM API, registry writes to <c>Enum\&lt;instId&gt;\ContainerID</c>, INF
/// directives) are all refused or silently ignored by PnP. The documented
/// workaround (DeviceOverrides with Removable=1) also doesn't trigger
/// container generation for root devices without a location path on this
/// build. UMDF2 cannot act as a bus driver, so there's no user-mode path
/// to provide a real ContainerID. Consumers that dedupe on ContainerID
/// will treat our virtuals as individually-containerless devices.</para>
/// </summary>
internal static class DeviceNodeCreator
{
    /// <summary>HID class GUID — every virtual controller is created in this class.</summary>
    private static readonly Guid HIDClassGuid =
        new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");

    /// <summary>Result of creating a device node — instance ID + the in-process device-info handle.
    /// On success the caller can pass the instanceId to e.g. <c>WaitForHidChild</c> for downstream waits.
    /// On failure InstanceId is null.</summary>
    public readonly struct Result
    {
        public readonly bool Success;
        public readonly string? InstanceId;
        public Result(bool success, string? instanceId) { Success = success; InstanceId = instanceId; }
    }

    /// <summary>Create a virtual device node for the given profile and assign it the
    /// given controller index. After this returns successfully:
    /// <list type="bullet">
    /// <item>The device exists at <c>ROOT\&lt;enumerator&gt;\&lt;instId&gt;</c></item>
    /// <item>Its <c>Device Parameters\ControllerIndex</c> = <paramref name="controllerIndex"/></item>
    /// <item>The driver is installed and bound</item>
    /// <item>The HID child interface has arrived (so the next per-controller
    ///       setup can start without racing PnP)</item>
    /// </list>
    /// </summary>
    public static Result CreateDeviceNode(ControllerProfile profile, string infPath, int controllerIndex)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        string vid = $"{profile.VendorId:X4}";
        string pid = $"{profile.ProductId:X4}";

        // driverPid: alternate PID in the hardware ID used for driver matching only
        // (e.g. xinputhid's INF matches PID 0x02FF). Apps still see the real PID via
        // HID attributes — the driver returns it from IOCTL_HID_GET_DEVICE_ATTRIBUTES.
        string hwPid = profile.DriverPid != null
            ? $"{Convert.ToUInt16(profile.DriverPid, 16):X4}" : pid;

        string desc = profile.ProductString;
        string enumerator, hwId;

        if (profile.UsesUpperFilter)
        {
            // xinputhid path. The &IG_00 in the path is what causes HIDAPI/SDL3 to
            // skip the device (HIDAPI's GAMECONTROLLER blocklist), forcing them to
            // use XInput instead — which is what we want for Xbox Series.
            enumerator = $"VID_{vid}&PID_{hwPid}&IG_00";
            hwId = $"root\\VID_{vid}&PID_{hwPid}&IG_00";
        }
        else if (profile.VendorId == 0x045E)
        {
            // Xbox legacy path (e.g. Xbox 360 wired). Same &IG_00 filtering plus
            // FunctionMode=1 in the registry enables WinExInput on the main device
            // so WGI fires GamepadAdded for it.
            enumerator = $"VID_{vid}&PID_{pid}&IG_00";
            hwId = $"root\\VID_{vid}&PID_{pid}&IG_00";
        }
        else
        {
            // Standard HID device — DualSense, generic third-party gamepads, etc.
            // No upper filter, plain HID class.
            enumerator = "HIDClass";
            hwId = $"root\\VID_{vid}&PID_{pid}";
        }
        string hwMulti = $"{hwId}\0root\\HIDMaestro\0\0";

        Guid classGuid = HIDClassGuid;
        IntPtr dis = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (dis == new IntPtr(-1)) return new Result(false, null);

        try
        {
            // SP_DEVINFO_DATA: 32 bytes on x64 (cbSize at offset 0)
            byte[] devInfoBuf = new byte[32];
            int devInfoSize = IntPtr.Size == 8 ? 32 : 28;
            BitConverter.GetBytes(devInfoSize).CopyTo(devInfoBuf, 0);
            var devInfoHandle = GCHandle.Alloc(devInfoBuf, GCHandleType.Pinned);

            try
            {
                if (!SetupDiCreateDeviceInfoW(dis, enumerator, ref classGuid, desc,
                        IntPtr.Zero, 1 /*DICD_GENERATE_ID*/, devInfoHandle.AddrOfPinnedObject()))
                    return new Result(false, null);

                byte[] hwBytes = Encoding.Unicode.GetBytes(hwMulti);
                if (!SetupDiSetDeviceRegistryPropertyW(dis, devInfoHandle.AddrOfPinnedObject(),
                        1 /*SPDRP_HARDWAREID*/, hwBytes, (uint)hwBytes.Length))
                    return new Result(false, null);

                // USB\MS_COMP_XUSB10 helps WGI/GameInputSvc identify Xbox controllers.
                // Only relevant for non-xinputhid Xbox profiles.
                if (!profile.UsesUpperFilter && profile.VendorId == 0x045E)
                {
                    string compatMulti = "USB\\MS_COMP_XUSB10\0USB\\Class_FF&SubClass_5D&Prot_01\0USB\\Class_FF&SubClass_5D\0\0";
                    byte[] compatBytes = Encoding.Unicode.GetBytes(compatMulti);
                    SetupDiSetDeviceRegistryPropertyW(dis, devInfoHandle.AddrOfPinnedObject(),
                        2 /*SPDRP_COMPATIBLEIDS*/, compatBytes, (uint)compatBytes.Length);
                }

                // DeviceOverrides marks the device as Removable BEFORE registration.
                // This tells PnP to generate a unique ContainerId per instance instead
                // of merging all our ROOT devices into one container in Settings.
                try
                {
                    string overrideHwId = hwId.Replace('\\', '#');
                    string overridePath = $@"SYSTEM\CurrentControlSet\Control\DeviceOverrides\{overrideHwId}\*";
                    using var overrideKey = Registry.LocalMachine.CreateSubKey(overridePath);
                    overrideKey.SetValue("Removable", 1, RegistryValueKind.DWord);
                }
                catch { /* non-fatal — ContainerID step below catches the consequences */ }

                // DIF_REGISTERDEVICE actually creates the PnP node. This is admin-only
                // (SeLoadDriverPrivilege) — failures here mean the consumer process
                // isn't elevated.
                if (!SetupDiCallClassInstaller(0x19 /*DIF_REGISTERDEVICE*/, dis,
                        devInfoHandle.AddrOfPinnedObject()))
                    return new Result(false, null);
            }
            finally
            {
                devInfoHandle.Free();
            }

            // ── post-creation: assign ControllerIndex + ContainerID ──────────
            //
            // PnP picks an instance index that may NOT match controllerIndex (ghosts
            // from previous runs offset numbering). Find OUR newly-created device
            // by looking for a live device with no ControllerIndex yet.
            try
            {
                using var enumKey = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Enum\ROOT\{enumerator}");
                if (enumKey != null)
                {
                    foreach (var inst in enumKey.GetSubKeyNames())
                    {
                        string instId = $@"ROOT\{enumerator}\{inst}";
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

            // (Removed 2026-04-21: post-registration registry write to
            // Enum\<instId>\ContainerID. PnP caches ContainerID at first
            // enumeration and ignores subsequent registry writes to this
            // path — stable MS behavior across Windows 10/11. The write
            // ran every deploy but never took effect; verified empirically
            // during issue #8 investigation. The DeviceOverrides write
            // above is retained because its effect on older Windows
            // versions has not been verified.)

            // Install our driver against the new device's hardware ID.
            UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hwId, infPath, 0, out _);

            // XnaComposite (legacy XInput path) needs an explicit restart to load.
            if (!profile.UsesUpperFilter)
            {
                DeviceManager.RestartDevice(@"ROOT\XNACOMPOSITE\0000");
            }

            // Wait for the HID interface to arrive — this is what the Phase 1 loop
            // races against. The enumerator MUST match the one we created the device
            // under (xinputhid uses the &IG_00 form, plain HID uses HIDClass).
            string devEnumer;
            if (profile.UsesUpperFilter)
                devEnumer = $"VID_{profile.VendorId:X4}&PID_{(profile.DriverPid != null ? Convert.ToUInt16(profile.DriverPid, 16) : profile.ProductId):X4}&IG_00";
            else if (profile.VendorId == 0x045E)
                devEnumer = $"VID_{profile.VendorId:X4}&PID_{profile.ProductId:X4}&IG_00";
            else
                devEnumer = "HIDClass";

            string? devInstId = null;
            try
            {
                using var ek = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Enum\ROOT\{devEnumer}");
                if (ek != null)
                {
                    foreach (var inst in ek.GetSubKeyNames())
                    {
                        string cand = $@"ROOT\{devEnumer}\{inst}";
                        if (CM_Locate_DevNodeW(out uint _, cand, 0) != 0) continue;
                        using var dp = ek.OpenSubKey($@"{inst}\Device Parameters");
                        if (dp?.GetValue("ControllerIndex") is int ci && ci == controllerIndex)
                        {
                            devInstId = cand;
                            break;
                        }
                    }
                }
            }
            catch { }

            if (devInstId != null)
            {
                DeviceManager.WaitForHidChild(devInstId);
            }

            // Apply the friendly name to the new device + its HID children, filtered
            // by controllerIndex so multi-controller setups don't clobber each other.
            string displayName = profile.DeviceDescription ?? profile.ProductString;
            DeviceProperties.FixHidChildNames(displayName, controllerIndex);

            return new Result(true, devInstId);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(dis);
        }
    }

    // ── private P/Invokes ────────────────────────────────────────────────

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

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string HardwareId,
        string FullInfPath, int InstallFlags, out bool RebootRequired);

    [DllImport("CfgMgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);
}
