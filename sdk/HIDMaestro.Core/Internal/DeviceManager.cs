using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>
/// Event-driven PnP device management using CM_Register_Notification.
/// No sleeps, no polling — waits on OS callbacks for actual system state changes.
/// </summary>
public static class DeviceManager
{
    // ── CfgMgr32 P/Invoke ──

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    static extern uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    static extern uint CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    static extern uint CM_Uninstall_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Register_Notification(ref CM_NOTIFY_FILTER pFilter, IntPtr pContext, CM_NOTIFY_CALLBACK pCallback, out IntPtr pNotifyContext);

    [DllImport("CfgMgr32.dll")]
    static extern uint CM_Unregister_Notification(IntPtr NotifyContext);

    [DllImport("CfgMgr32.dll")]
    static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_ID_Size(out uint pulLen, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_IDW(uint dnDevInst, char[] Buffer, uint BufferLen, uint ulFlags);

    delegate uint CM_NOTIFY_CALLBACK(IntPtr hNotify, IntPtr Context, uint Action, ref CM_NOTIFY_EVENT_DATA EventData, uint EventDataSize);

    const uint CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0;
    const uint CM_NOTIFY_FILTER_TYPE_DEVICEHANDLE = 1;
    const uint CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE = 2;

    const uint CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL = 0;
    const uint CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL = 1;
    const uint CM_NOTIFY_ACTION_DEVICEQUERYREMOVE = 2;
    const uint CM_NOTIFY_ACTION_DEVICEQUERYREMOVEFAILED = 3;
    const uint CM_NOTIFY_ACTION_DEVICEREMOVEPENDING = 4;
    const uint CM_NOTIFY_ACTION_DEVICEREMOVECOMPLETE = 5;
    const uint CM_NOTIFY_ACTION_DEVICECUSTOMEVENT = 6;
    const uint CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED = 7;
    const uint CM_NOTIFY_ACTION_DEVICEINSTANCESTARTED = 8;
    const uint CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED = 9;

    const uint CR_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CM_NOTIFY_FILTER
    {
        public uint cbSize;
        public uint Flags;
        public uint FilterType;
        public uint Reserved;
        // Union: for DEVICEINTERFACE, this is the ClassGuid
        public Guid ClassGuid;
        // Padding for the union (device instance string or handle)
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 200)]
        public string? FilterData;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CM_NOTIFY_EVENT_DATA
    {
        public uint FilterType;
        public uint Reserved;
        // Union depends on FilterType
        public Guid ClassGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 200)]
        public string? SymbolicLink;
    }

    static readonly Guid HID_INTERFACE_GUID = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    static readonly Guid XUSB_INTERFACE_GUID = new("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");

    /// <summary>
    /// Waits for a specific device interface GUID to appear on the given device.
    /// Uses CM_Register_Notification — no polling, no sleeping.
    /// </summary>
    public static bool WaitForDeviceInterface(string instanceId, Guid interfaceGuid, int timeoutMs = 10000)
    {
        using var readyEvent = new ManualResetEventSlim(false);

        // Check if already present
        // (CM_Locate + check interfaces via registry)
        if (HasInterface(instanceId, interfaceGuid))
            return true;

        var filter = new CM_NOTIFY_FILTER
        {
            cbSize = (uint)Marshal.SizeOf<CM_NOTIFY_FILTER>(),
            FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
            ClassGuid = interfaceGuid
        };

        string instHash = instanceId.Replace('\\', '#').ToLowerInvariant();

        CM_NOTIFY_CALLBACK callback = (IntPtr hNotify, IntPtr context, uint action, ref CM_NOTIFY_EVENT_DATA data, uint size) =>
        {
            if (action == CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL)
            {
                string? symLink = data.SymbolicLink;
                if (symLink != null && symLink.ToLowerInvariant().Contains(instHash))
                    readyEvent.Set();
            }
            return 0u;
        };

        var callbackHandle = GCHandle.Alloc(callback);
        try
        {
            if (CM_Register_Notification(ref filter, IntPtr.Zero, callback, out IntPtr notifyCtx) != CR_SUCCESS)
                return false;

            try
            {
                // Re-check after registration (race window)
                if (HasInterface(instanceId, interfaceGuid))
                    return true;
                return readyEvent.Wait(timeoutMs);
            }
            finally
            {
                CM_Unregister_Notification(notifyCtx);
            }
        }
        finally
        {
            callbackHandle.Free();
        }
    }

    static bool HasInterface(string instanceId, Guid interfaceGuid)
    {
        // Quick check via registry
        string regPath = $@"SYSTEM\CurrentControlSet\Control\DeviceClasses\{{{interfaceGuid}}}";
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(regPath);
            if (key == null) return false;
            string hash = instanceId.Replace('\\', '#').ToLowerInvariant();
            return key.GetSubKeyNames().Any(s => s.ToLowerInvariant().Contains(hash));
        }
        catch { return false; }
    }

    /// <summary>
    /// Waits for a HID device interface to appear for the given device instance ID.
    /// Returns true when the HID child is ready, false on timeout.
    /// </summary>
    public static bool WaitForHidChild(string parentInstanceId, int timeoutMs = 5000)
    {
        using var readyEvent = new ManualResetEventSlim(false);

        // Check if already present
        if (CM_Locate_DevNodeW(out uint parentInst, parentInstanceId, 0) == CR_SUCCESS)
        {
            if (CM_Get_Child(out uint _, parentInst, 0) == CR_SUCCESS)
                return true; // Already has a child
        }

        // Register for HID interface arrival
        var filter = new CM_NOTIFY_FILTER
        {
            cbSize = (uint)Marshal.SizeOf<CM_NOTIFY_FILTER>(),
            FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
            ClassGuid = HID_INTERFACE_GUID
        };

        CM_NOTIFY_CALLBACK callback = (IntPtr hNotify, IntPtr context, uint action, ref CM_NOTIFY_EVENT_DATA data, uint size) =>
        {
            if (action == CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL)
            {
                string? symLink = data.SymbolicLink;
                if (symLink != null)
                {
                    string parentHash = parentInstanceId.Replace('\\', '#').ToLowerInvariant();
                    if (symLink.ToLowerInvariant().Contains(parentHash))
                        readyEvent.Set();
                }
            }
            return 0u;
        };

        var callbackHandle = GCHandle.Alloc(callback);
        try
        {
            if (CM_Register_Notification(ref filter, IntPtr.Zero, callback, out IntPtr notifyCtx) != CR_SUCCESS)
                return false;

            try
            {
                // Check again after registration (race condition window)
                uint childInst;
                if (CM_Locate_DevNodeW(out parentInst, parentInstanceId, 0) == CR_SUCCESS
                    && CM_Get_Child(out childInst, parentInst, 0) == CR_SUCCESS)
                    return true;

                return readyEvent.Wait(timeoutMs);
            }
            finally
            {
                CM_Unregister_Notification(notifyCtx);
            }
        }
        finally
        {
            callbackHandle.Free();
        }
    }

    /// <summary>
    /// Waits for a device instance to be fully removed.
    /// Returns true when gone, false on timeout.
    /// </summary>
    public static bool WaitForDeviceRemoval(string instanceId, int timeoutMs = 5000)
    {
        // Check if already gone
        if (CM_Locate_DevNodeW(out uint _, instanceId, 0) != CR_SUCCESS)
            return true;

        using var goneEvent = new ManualResetEventSlim(false);

        var filter = new CM_NOTIFY_FILTER
        {
            cbSize = (uint)Marshal.SizeOf<CM_NOTIFY_FILTER>(),
            FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE,
            FilterData = instanceId
        };

        CM_NOTIFY_CALLBACK callback = (IntPtr hNotify, IntPtr context, uint action, ref CM_NOTIFY_EVENT_DATA data, uint size) =>
        {
            if (action == CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED)
                goneEvent.Set();
            return 0u;
        };

        var callbackHandle = GCHandle.Alloc(callback);
        try
        {
            if (CM_Register_Notification(ref filter, IntPtr.Zero, callback, out IntPtr notifyCtx) != CR_SUCCESS)
                return false;

            try
            {
                // Check again after registration
                uint devInst2;
                if (CM_Locate_DevNodeW(out devInst2, instanceId, 0) != CR_SUCCESS)
                    return true;

                return goneEvent.Wait(timeoutMs);
            }
            finally
            {
                CM_Unregister_Notification(notifyCtx);
            }
        }
        finally
        {
            callbackHandle.Free();
        }
    }

    // ── SetupAPI P/Invoke for DIF_REMOVE ──

    [DllImport("SetupAPI.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW")]
    static extern IntPtr SetupDiGetClassDevsByRef(ref Guid ClassGuid, uint Enumerator,
        IntPtr hwndParent, uint Flags);

    [DllImport("SetupAPI.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiOpenDeviceInfoW(IntPtr DeviceInfoSet, string DeviceInstanceId,
        IntPtr hwndParent, uint OpenFlags, IntPtr DeviceInfoData);

    [DllImport("SetupAPI.dll", SetLastError = true, EntryPoint = "SetupDiCallClassInstaller")]
    static extern bool SetupDiCallClassInstaller(int InstallFunction, IntPtr DeviceInfoSet, IntPtr DeviceInfoData);

    [DllImport("SetupAPI.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("SetupAPI.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, IntPtr DeviceInfoData);

    [DllImport("SetupAPI.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInstanceIdW(IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
        [Out] char[] DeviceInstanceId, uint DeviceInstanceIdSize, out uint RequiredSize);

    [DllImport("SetupAPI.dll", SetLastError = true)]
    static extern bool SetupDiRemoveDevice(IntPtr DeviceInfoSet, IntPtr DeviceInfoData);

    const uint DIGCF_ALLCLASSES = 0x04;
    const int DIF_REMOVE = 0x05;

    /// <summary>
    /// Removes ALL ghost devices matching the given instance ID prefixes.
    /// Uses SetupDiRemoveDevice (direct API, not DIF_REMOVE) which works on ghost/phantom devices
    /// that DIF_REMOVE and pnputil fail to remove. Enumerates ALL devices (including non-present)
    /// via SetupDiGetClassDevs(DIGCF_ALLCLASSES) and removes those with InstallState=false.
    /// </summary>
    public static int RemoveGhostDevices(string[] instanceIdPrefixes)
    {
        int removed = 0;
        // GhostBuster pattern: Guid.Empty by ref + DIGCF_ALLCLASSES enumerates ALL devices
        // including phantoms. Ghost = CM_Get_DevNode_Status fails. Remove via SetupDiRemoveDevice
        // on the SAME DevInfoSet handle (critical — separate handles don't work).
        Guid nullGuid = Guid.Empty;
        IntPtr dis = SetupDiGetClassDevsByRef(ref nullGuid, 0, IntPtr.Zero, DIGCF_ALLCLASSES);
        if (dis == new IntPtr(-1)) return 0;

        try
        {
            int devInfoSize = IntPtr.Size == 8 ? 32 : 28;
            byte[] devInfoBuf = new byte[devInfoSize];
            var devInfoHandle = System.Runtime.InteropServices.GCHandle.Alloc(devInfoBuf, System.Runtime.InteropServices.GCHandleType.Pinned);

            try
            {
                uint index = 0;
                while (true)
                {
                    BitConverter.GetBytes(devInfoSize).CopyTo(devInfoBuf, 0);
                    if (!SetupDiEnumDeviceInfo(dis, index, devInfoHandle.AddrOfPinnedObject()))
                        break;

                    char[] idBuf = new char[512];
                    if (SetupDiGetDeviceInstanceIdW(dis, devInfoHandle.AddrOfPinnedObject(), idBuf, (uint)idBuf.Length, out uint idLen))
                    {
                        string instanceId = new string(idBuf, 0, (int)idLen - 1);

                        bool isOurs = false;
                        foreach (string prefix in instanceIdPrefixes)
                        {
                            if (instanceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            { isOurs = true; break; }
                        }

                        if (isOurs)
                        {
                            // GhostBuster ghost detection: CM_Get_DevNode_Status failure = ghost
                            // SP_DEVINFO_DATA layout: cbSize(4) + ClassGuid(16) + DevInst(4) + Reserved(ptr)
                            uint devInst = BitConverter.ToUInt32(devInfoBuf, 20);
                            bool isGhost = CM_Get_DevNode_Status(out _, out _,
                                devInst, 0) != CR_SUCCESS;

                            if (isGhost)
                            {
                                if (SetupDiRemoveDevice(dis, devInfoHandle.AddrOfPinnedObject()))
                                {
                                    System.Console.Error.WriteLine($"    Ghost removed: {instanceId}");
                                    removed++;
                                }
                            }
                        }
                    }

                    index++;
                }
            }
            finally
            {
                devInfoHandle.Free();
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(dis);
        }

        return removed;
    }

    /// <summary>
    /// Removes a device and all its children, waits for removal to complete.
    /// Enumerates HID children first and removes them individually via DIF_REMOVE,
    /// then removes the parent. This prevents ghost HID children from surviving.
    /// </summary>
    public static bool RemoveDevice(string instanceId, int timeoutMs = 5000)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CR_SUCCESS &&
            CM_Locate_DevNodeW(out devInst, instanceId, 1) != CR_SUCCESS)
            return true;

        // Step 1: Find and remove all children first
        var childIds = GetAllChildDeviceIds(devInst);
        foreach (string childId in childIds)
        {
            DifRemoveDevice(childId);
            WaitForDeviceRemoval(childId, 2000);
        }

        // Step 2: Try DIF_REMOVE first (proper PnP removal)
        DifRemoveDevice(instanceId);
        WaitForDeviceRemoval(instanceId, timeoutMs);

        // Step 3: pnputil as second pass — always try for ROOT devices
        // DIF_REMOVE often reports success but leaves phantom devices in the Enum registry.
        // pnputil /remove-device is more thorough for ghost cleanup.
        if (instanceId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) ||
            instanceId.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = $"/remove-device \"{instanceId}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    proc.StandardOutput.ReadToEnd();
                    proc.StandardError.ReadToEnd();
                    proc.WaitForExit(5000);
                }
            }
            catch { }
        }

        // Step 4: Final check
        bool removed = CM_Locate_DevNodeW(out _, instanceId, 0) != CR_SUCCESS
                    && CM_Locate_DevNodeW(out _, instanceId, 1) != CR_SUCCESS;
        return removed;
    }

    /// <summary>
    /// Gets all child device instance IDs for a given devnode (CM_Get_Child + CM_Get_Sibling loop).
    /// </summary>
    static List<string> GetAllChildDeviceIds(uint parentInst)
    {
        var children = new List<string>();
        if (CM_Get_Child(out uint childInst, parentInst, 0) != CR_SUCCESS)
            return children;

        do
        {
            string? childId = GetDeviceId(childInst);
            if (childId != null)
                children.Add(childId);
        }
        while (CM_Get_Sibling(out childInst, childInst, 0) == CR_SUCCESS);

        return children;
    }

    /// <summary>
    /// Gets the device instance ID string for a given devnode.
    /// </summary>
    static string? GetDeviceId(uint devInst)
    {
        if (CM_Get_Device_ID_Size(out uint idLen, devInst, 0) != CR_SUCCESS)
            return null;
        var buffer = new char[idLen + 1];
        if (CM_Get_Device_IDW(devInst, buffer, (uint)buffer.Length, 0) != CR_SUCCESS)
            return null;
        return new string(buffer, 0, (int)idLen);
    }

    /// <summary>
    /// Removes a single device via SetupDiRemoveDevice (same as devcon remove).
    /// This is more thorough than DIF_REMOVE — it bypasses class installers and
    /// directly removes the device node, preventing ghost entries on UMDF devices.
    /// Falls back to CM_Disable + CM_Uninstall if SetupAPI fails.
    /// </summary>
    static void DifRemoveDevice(string instanceId)
    {
        uint devInst = 0;
        bool located = CM_Locate_DevNodeW(out devInst, instanceId, 0) == CR_SUCCESS
                    || CM_Locate_DevNodeW(out devInst, instanceId, 1) == CR_SUCCESS;

        // Use Guid.Empty + DIGCF_ALLCLASSES to enumerate ALL devices including phantoms.
        // This is critical — a device-class-specific DevInfoSet won't find phantoms.
        Guid nullGuid = Guid.Empty;
        IntPtr dis = SetupDiGetClassDevsByRef(ref nullGuid, 0, IntPtr.Zero, DIGCF_ALLCLASSES);
        if (dis == new IntPtr(-1))
        {
            if (located)
            {
                CM_Disable_DevNode(devInst, 0);
                CM_Uninstall_DevNode(devInst, 0);
            }
            return;
        }

        try
        {
            int devInfoSize = IntPtr.Size == 8 ? 32 : 28;
            byte[] devInfoBuf = new byte[devInfoSize];
            BitConverter.GetBytes(devInfoSize).CopyTo(devInfoBuf, 0);
            var devInfoHandle = GCHandle.Alloc(devInfoBuf, GCHandleType.Pinned);

            try
            {
                if (!SetupDiOpenDeviceInfoW(dis, instanceId, IntPtr.Zero, 0, devInfoHandle.AddrOfPinnedObject()))
                {
                    if (located)
                    {
                        CM_Disable_DevNode(devInst, 0);
                        CM_Uninstall_DevNode(devInst, 0);
                    }
                    return;
                }

                // SetupDiRemoveDevice — direct removal like devcon.exe.
                // Unlike DIF_REMOVE (class installer), this fully removes the device
                // node and registry entries, preventing ghosts on UMDF devices.
                if (!SetupDiRemoveDevice(dis, devInfoHandle.AddrOfPinnedObject()))
                {
                    // Fallback to DIF_REMOVE if direct removal fails
                    SetupDiCallClassInstaller(DIF_REMOVE, dis, devInfoHandle.AddrOfPinnedObject());
                }
            }
            finally
            {
                devInfoHandle.Free();
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(dis);
        }
    }

    [DllImport("CfgMgr32.dll")]
    static extern uint CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    static extern uint CM_Enable_DevNode(uint dnDevInst, uint ulFlags);

    /// <summary>
    /// Restarts a device by disabling and re-enabling it. No process spawning.
    /// </summary>
    public static bool RestartDevice(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CR_SUCCESS)
            return false;
        CM_Disable_DevNode(devInst, 0);
        // Wait for driver to unload before re-enabling
        WaitForDeviceRemoval(instanceId, 5000);
        // Re-locate (may have become phantom after disable)
        CM_Locate_DevNodeW(out devInst, instanceId, 1); // phantom flag
        CM_Enable_DevNode(devInst, 0);
        return true;
    }

    /// <summary>
    /// Finds the HID child device instance ID for a given parent.
    /// Returns null if no child found.
    /// </summary>
    public static string? GetHidChildId(string parentInstanceId)
    {
        if (CM_Locate_DevNodeW(out uint parentInst, parentInstanceId, 0) != CR_SUCCESS)
            return null;

        if (CM_Get_Child(out uint childInst, parentInst, 0) != CR_SUCCESS)
            return null;

        if (CM_Get_Device_ID_Size(out uint idLen, childInst, 0) != CR_SUCCESS)
            return null;

        var buffer = new char[idLen + 1];
        if (CM_Get_Device_IDW(childInst, buffer, (uint)buffer.Length, 0) != CR_SUCCESS)
            return null;

        return new string(buffer, 0, (int)idLen);
    }

    /// <summary>
    /// Scans HKLM\SYSTEM\CurrentControlSet\Enum\HID for orphaned HID children
    /// matching VID_045E*IG_00* or HIDCLASS entries whose ROOT parent no longer exists.
    /// Removes each orphan via DIF_REMOVE.
    /// Returns the number of orphans removed.
    /// </summary>
    public static int RemoveOrphanHidChildren()
    {
        int removed = 0;
        const string enumHidPath = @"SYSTEM\CurrentControlSet\Enum\HID";

        try
        {
            using var hidKey = Registry.LocalMachine.OpenSubKey(enumHidPath);
            if (hidKey == null)
                return 0;

            foreach (string deviceName in hidKey.GetSubKeyNames())
            {
                // Match VID_045E*IG_00* or HIDCLASS entries
                string upper = deviceName.ToUpperInvariant();
                bool isMatch = (upper.Contains("VID_045E") && upper.Contains("IG_00"))
                            || upper.Contains("HIDCLASS");
                if (!isMatch)
                    continue;

                using var deviceKey = hidKey.OpenSubKey(deviceName);
                if (deviceKey == null)
                    continue;

                foreach (string instanceName in deviceKey.GetSubKeyNames())
                {
                    string hidInstanceId = $@"HID\{deviceName}\{instanceName}";

                    // Read the parent instance ID from the registry
                    string? parentId = GetRegistryParentId(enumHidPath, deviceName, instanceName);
                    if (parentId == null)
                        continue;

                    // Check if the parent ROOT device still exists (live or phantom)
                    if (!parentId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // If parent can be located, it's not an orphan
                    if (CM_Locate_DevNodeW(out uint _, parentId, 0) == CR_SUCCESS)
                        continue;
                    if (CM_Locate_DevNodeW(out uint _, parentId, 1) == CR_SUCCESS)
                        continue;

                    // Parent is gone — remove the orphan
                    Console.WriteLine($"  Removing orphan HID child: {hidInstanceId}");
                    DifRemoveDevice(hidInstanceId);
                    removed++;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: orphan scan error: {ex.Message}");
        }

        return removed;
    }

    /// <summary>
    /// Reads the ParentIdPrefix or reconstructs the parent instance ID for a HID child
    /// from the registry.
    /// </summary>
    static string? GetRegistryParentId(string enumHidPath, string deviceName, string instanceName)
    {
        try
        {
            string regPath = $@"{enumHidPath}\{deviceName}\{instanceName}";
            using var instKey = Registry.LocalMachine.OpenSubKey(regPath);
            if (instKey == null)
                return null;

            // The HID child stores its parent in the "ParentIdPrefix" or we can
            // derive it from the device ID structure. The HID\VID_XXXX&PID_XXXX&IG_XX\<hash>
            // format encodes the parent info. Check for explicit parent reference first.

            // Try reading the device's actual devnode to get the parent via CM API
            if (CM_Locate_DevNodeW(out uint childInst, $@"HID\{deviceName}\{instanceName}", 0) == CR_SUCCESS
                || CM_Locate_DevNodeW(out childInst, $@"HID\{deviceName}\{instanceName}", 1) == CR_SUCCESS)
            {
                if (CM_Get_Parent(out uint parentInst, childInst, 0) == CR_SUCCESS)
                {
                    string? parentId = GetDeviceId(parentInst);
                    if (parentId != null)
                        return parentId;
                }
            }

            // Fallback: scan ROOT\HIDMAESTRO and ROOT\HIDMAESTROIG entries
            // to see if any match via ParentIdPrefix
            foreach (string rootClass in new[] { "HIDMAESTRO", "HIDMAESTROIG" })
            {
                string rootEnumPath = $@"SYSTEM\CurrentControlSet\Enum\ROOT\{rootClass}";
                using var rootKey = Registry.LocalMachine.OpenSubKey(rootEnumPath);
                if (rootKey == null)
                    continue;

                foreach (string rootInst in rootKey.GetSubKeyNames())
                {
                    string rootInstanceId = $@"ROOT\{rootClass}\{rootInst}";
                    // If the parent no longer exists at all (not even phantom), it's orphaned
                    if (CM_Locate_DevNodeW(out uint _, rootInstanceId, 0) != CR_SUCCESS
                        && CM_Locate_DevNodeW(out uint _, rootInstanceId, 1) != CR_SUCCESS)
                    {
                        // Check if this root device's ParentIdPrefix matches the HID child's instance
                        using var rootInstKey = rootKey.OpenSubKey(rootInst);
                        string? pip = rootInstKey?.GetValue("ParentIdPrefix") as string;
                        if (pip != null && instanceName.ToUpperInvariant().Contains(pip.ToUpperInvariant()))
                            return rootInstanceId;
                    }
                }
            }
        }
        catch { }

        return null;
    }
}
