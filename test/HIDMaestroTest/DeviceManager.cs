using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace HIDMaestroTest;

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
    static extern uint CM_Query_And_Remove_SubTreeW(uint dnDevInst, IntPtr pVetoType, IntPtr pszVetoName, uint ulNameLength, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Register_Notification(ref CM_NOTIFY_FILTER pFilter, IntPtr pContext, CM_NOTIFY_CALLBACK pCallback, out IntPtr pNotifyContext);

    [DllImport("CfgMgr32.dll")]
    static extern uint CM_Unregister_Notification(IntPtr NotifyContext);

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

    /// <summary>
    /// Removes a device and all its children, waits for removal to complete.
    /// Returns true if removed successfully.
    /// </summary>
    public static bool RemoveDevice(string instanceId, int timeoutMs = 5000)
    {
        uint devInst;
        if (CM_Locate_DevNodeW(out devInst, instanceId, 0) != CR_SUCCESS)
            return true; // Already gone

        uint result = CM_Query_And_Remove_SubTreeW(devInst, IntPtr.Zero, IntPtr.Zero, 0, 0);
        if (result != CR_SUCCESS)
            return false;

        return WaitForDeviceRemoval(instanceId, timeoutMs);
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
        Thread.Sleep(100); // Brief pause for PnP to process
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
}
