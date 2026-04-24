using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>
/// Per-device property setters: friendly name, device description, bus-reported
/// device description. These are the names games and the OS show in joy.cpl,
/// Device Manager, Settings, browser pickers, etc.
///
/// <para>The HID class driver tends to override <c>FriendlyName</c> and
/// <c>DeviceDesc</c> after device install (it pulls them from the INF), so
/// these helpers always set the property via <c>CM_Set_DevNode_PropertyW</c>
/// (which goes through the kernel and beats the INF defaults) rather than
/// writing the registry directly. The Enum subkeys are also owned by
/// TrustedInstaller, so direct registry writes from elevated admin shells
/// fail with PermissionDenied — the kernel-mode property API is the only
/// reliable path.</para>
///
/// <para><b>Multi-controller filtering:</b> the per-controller variants
/// (<see cref="FixHidChildNames"/>, <see cref="ApplyFriendlyNameForController"/>)
/// take an explicit <c>controllerIndex</c> and only touch devices whose
/// <c>Device Parameters\ControllerIndex</c> registry value matches. Without
/// this filter, naming controller N can clobber controller N-1's name.</para>
/// </summary>
internal static class DeviceProperties
{
    // ── DEVPKEY constants ────────────────────────────────────────────────

    // DEVPKEY_Device_DeviceDesc = {a45c254e-df1c-4efd-8020-67d146a850e0}, 2
    private static DEVPROPKEY DEVPKEY_DeviceDesc = new()
    {
        fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
        pid = 2
    };

    // DEVPKEY_Device_FriendlyName = {a45c254e-df1c-4efd-8020-67d146a850e0}, 14
    private static DEVPROPKEY DEVPKEY_FriendlyName = new()
    {
        fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
        pid = 14
    };

    // DEVPKEY_Device_BusReportedDeviceDesc = {540b947e-8b40-45bc-a8a2-6a0b894cbda2}, 4
    private static DEVPROPKEY DEVPKEY_BusReportedDeviceDesc = new()
    {
        fmtid = new Guid(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2),
        pid = 4
    };

    private const uint DEVPROP_TYPE_STRING = 0x12;

    // ── public helpers ───────────────────────────────────────────────────

    /// <summary>Set the bus-reported device description on the given device
    /// instance and (if present) its first HID child.</summary>
    public static void SetBusReportedDeviceDesc(string instanceId, string description)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0) return;

        byte[] strBytes = Encoding.Unicode.GetBytes(description + "\0");
        CM_Set_DevNode_PropertyW(devInst, ref DEVPKEY_BusReportedDeviceDesc,
            DEVPROP_TYPE_STRING, strBytes, (uint)strBytes.Length, 0);

        if (CM_Get_Child(out uint childInst, devInst, 0) == 0)
        {
            CM_Set_DevNode_PropertyW(childInst, ref DEVPKEY_BusReportedDeviceDesc,
                DEVPROP_TYPE_STRING, strBytes, (uint)strBytes.Length, 0);
        }
    }

    /// <summary>Set FriendlyName + DeviceDesc on the given root device instance
    /// and on its first HID child. Returns false if the device couldn't be
    /// located.</summary>
    public static bool SetDeviceFriendlyName(string rootInstanceId, string name)
    {
        if (CM_Locate_DevNodeW(out uint devInst, rootInstanceId, 0) != 0)
            return false;

        byte[] strBytes = Encoding.Unicode.GetBytes(name + "\0");

        CM_Set_DevNode_PropertyW(devInst, ref DEVPKEY_FriendlyName,
            DEVPROP_TYPE_STRING, strBytes, (uint)strBytes.Length, 0);
        CM_Set_DevNode_PropertyW(devInst, ref DEVPKEY_DeviceDesc,
            DEVPROP_TYPE_STRING, strBytes, (uint)strBytes.Length, 0);

        if (CM_Get_Child(out uint childInst, devInst, 0) == 0)
        {
            CM_Set_DevNode_PropertyW(childInst, ref DEVPKEY_FriendlyName,
                DEVPROP_TYPE_STRING, strBytes, (uint)strBytes.Length, 0);
            CM_Set_DevNode_PropertyW(childInst, ref DEVPKEY_DeviceDesc,
                DEVPROP_TYPE_STRING, strBytes, (uint)strBytes.Length, 0);
        }
        return true;
    }

    /// <summary>Sets FriendlyName + DeviceDesc on every <c>ROOT\VID_*</c>
    /// HIDMaestro root device whose <c>Device Parameters\ControllerIndex</c>
    /// matches <paramref name="controllerIndex"/>, plus the device's first HID
    /// child. With <paramref name="controllerIndex"/> = -1 (legacy single-
    /// controller behavior) updates ALL HIDMaestro VID_ devices regardless
    /// of index — used only by paths that don't need filtering.
    ///
    /// <para>Multi-controller setups MUST pass an explicit index, otherwise
    /// naming controller N will clobber controller N-1's name.</para>
    /// </summary>
    public static void FixHidChildNames(string name, int controllerIndex = -1)
    {
        byte[] strBytes = Encoding.Unicode.GetBytes(name + "\0");

        // Sweep BOTH SWD\ (post-slot-1-skip-fix) and ROOT\ (legacy) enumerator
        // roots — multi-controller setups after the migration have HIDMAESTRO
        // and gamepad companions under SWD\ while older paths may still sit
        // under ROOT\.
        foreach (var enumRoot in new[] { "SWD", "ROOT" })
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{enumRoot}");
            if (enumKey == null) continue;

            foreach (var sub in enumKey.GetSubKeyNames())
            {
                // Accept the pre-SWD `VID_xxx&PID_yyy&IG_00` enumerator (ROOT
                // path) and any HIDMAESTRO-prefixed SWD enumerator (current
                // gamepad companion form: HIDMAESTRO_VID_<vid>_PID_<pid>&IG_00).
                bool isVidForm = sub.StartsWith("VID_", StringComparison.OrdinalIgnoreCase);
                bool isSwdForm = sub.StartsWith("HIDMAESTRO", StringComparison.OrdinalIgnoreCase);
                if (!isVidForm && !isSwdForm) continue;

                using var subKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{enumRoot}\{sub}");
                if (subKey == null) continue;

                foreach (var inst in subKey.GetSubKeyNames())
                {
                    if (controllerIndex >= 0)
                    {
                        using var dpKey = Registry.LocalMachine.OpenSubKey(
                            $@"SYSTEM\CurrentControlSet\Enum\{enumRoot}\{sub}\{inst}\Device Parameters");
                        if (dpKey == null) continue;
                        var ci = dpKey.GetValue("ControllerIndex");
                        // Treat missing as index 0 so single-controller setup still works
                        int actual = ci is int v ? v : 0;
                        if (actual != controllerIndex) continue;
                    }

                    string devId = $@"{enumRoot}\{sub}\{inst}";
                    if (CM_Locate_DevNodeW(out uint devInst, devId, 0) == 0)
                    {
                        CM_Set_DevNode_PropertyW(devInst, ref DEVPKEY_FriendlyName,
                            DEVPROP_TYPE_STRING, strBytes, (uint)strBytes.Length, 0);
                        CM_Set_DevNode_PropertyW(devInst, ref DEVPKEY_DeviceDesc,
                            DEVPROP_TYPE_STRING, strBytes, (uint)strBytes.Length, 0);
                        if (CM_Get_Child(out uint childInst, devInst, 0) == 0)
                        {
                            CM_Set_DevNode_PropertyW(childInst, ref DEVPKEY_FriendlyName,
                                DEVPROP_TYPE_STRING, strBytes, (uint)strBytes.Length, 0);
                            CM_Set_DevNode_PropertyW(childInst, ref DEVPKEY_DeviceDesc,
                                DEVPROP_TYPE_STRING, strBytes, (uint)strBytes.Length, 0);
                        }
                    }
                }
            }
        }
        catch { /* registry races during PnP transitions are non-fatal */ }
    }

    /// <summary>Final-pass friendly-name application for a specific
    /// controllerIndex. Used by the orchestration after Phase 1 to overcome
    /// a race where per-controller renames during setup get clobbered by
    /// PnP driver-bind on the first controller. Iterates ALL ROOT\* device
    /// classes (<c>VID_*&amp;IG_00</c>, <c>HIDMAESTRO</c>, <c>HIDClass</c>)
    /// and matches by <c>Device Parameters\ControllerIndex</c>, applying
    /// FriendlyName + DeviceDesc + BusReportedDeviceDesc to the root device
    /// and every HID child/grandchild.</summary>
    public static void ApplyFriendlyNameForController(int controllerIndex, string name)
    {
        byte[] strBytes = Encoding.Unicode.GetBytes(name + "\0");

        void Apply(uint inst)
        {
            CM_Set_DevNode_PropertyW(inst, ref DEVPKEY_FriendlyName,
                DEVPROP_TYPE_STRING, strBytes, (uint)strBytes.Length, 0);
            CM_Set_DevNode_PropertyW(inst, ref DEVPKEY_DeviceDesc,
                DEVPROP_TYPE_STRING, strBytes, (uint)strBytes.Length, 0);
            CM_Set_DevNode_PropertyW(inst, ref DEVPKEY_BusReportedDeviceDesc,
                DEVPROP_TYPE_STRING, strBytes, (uint)strBytes.Length, 0);
        }

        // Sweep both SWD\ (new) and ROOT\ (legacy) enumerator roots.
        foreach (var enumRoot in new[] { "SWD", "ROOT" })
        try
        {
            using var rootEnum = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{enumRoot}");
            if (rootEnum == null) continue;

            foreach (var sub in rootEnum.GetSubKeyNames())
            {
                bool isOurClass = sub.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
                               || sub.Equals("HIDMAESTRO", StringComparison.OrdinalIgnoreCase)
                               || sub.Equals("HIDCLASS", StringComparison.OrdinalIgnoreCase)
                               || sub.StartsWith("HIDMAESTRO", StringComparison.OrdinalIgnoreCase);
                if (!isOurClass) continue;

                using var subKey = rootEnum.OpenSubKey(sub);
                if (subKey == null) continue;

                foreach (var inst in subKey.GetSubKeyNames())
                {
                    using var dpKey = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Enum\{enumRoot}\{sub}\{inst}\Device Parameters");
                    if (dpKey == null) continue;
                    var ci = dpKey.GetValue("ControllerIndex");
                    int actual = ci is int v ? v : -1;
                    if (actual != controllerIndex) continue;

                    string instId = $@"{enumRoot}\{sub}\{inst}";
                    if (CM_Locate_DevNodeW(out uint devInst, instId, 0) != 0) continue;

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
        catch { /* registry races during PnP transitions are non-fatal */ }
    }

    // ── private P/Invokes ────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("CfgMgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("CfgMgr32.dll", SetLastError = true)]
    private static extern uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", SetLastError = true)]
    private static extern uint CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Set_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY propertyKey,
        uint propertyType, byte[] propertyBuffer, uint propertyBufferSize, uint ulFlags);
}
