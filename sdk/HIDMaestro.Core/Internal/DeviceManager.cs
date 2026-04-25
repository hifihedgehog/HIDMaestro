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
    const uint DN_STARTED = 0x00000008;

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
    public static bool RemoveDevice(string instanceId, int timeoutMs = 5000, bool fast = false, bool forceFallbacks = false)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CR_SUCCESS &&
            CM_Locate_DevNodeW(out devInst, instanceId, 1) != CR_SUCCESS)
            return true;

        // In fast mode (used by cleanup paths), fire-and-forget: issue
        // DIF_REMOVE for all children and the parent without waiting for
        // each removal to complete. PnP processes them asynchronously.
        // This avoids 500ms+ of WaitForDeviceRemoval per device (~24
        // calls during a 6-controller teardown = 12+ seconds of waiting
        // for events that won't fire until WUDFHost releases handles).
        // Any devices that survive as phantoms get caught by the ghost
        // cleanup at the start of the next SetupController run.

        // Step 1: Find and remove all children first
        var childIds = GetAllChildDeviceIds(devInst);
        foreach (string childId in childIds)
        {
            if (fast)
            {
                // Fast mode: CM_Disable + CM_Uninstall directly. Avoids the
                // SetupDiRemoveDevice → IRP_MN_QUERY_REMOVE → kernel-veto
                // timeout (~1s per device) that dominates cleanup time when
                // the UMDF host still holds handles after a force-kill.
                if (CM_Locate_DevNodeW(out uint ci, childId, 0) == CR_SUCCESS
                 || CM_Locate_DevNodeW(out ci, childId, 1) == CR_SUCCESS)
                {
                    CM_Disable_DevNode(ci, 0);
                    CM_Uninstall_DevNode(ci, 0);
                }
            }
            else
            {
                DifRemoveDevice(childId);
                WaitForDeviceRemoval(childId, 2000);
            }
        }

        // Step 2: Remove the parent
        bool goneAfterDif;
        if (fast)
        {
            CM_Disable_DevNode(devInst, 0);
            CM_Uninstall_DevNode(devInst, 0);
            goneAfterDif = false; // don't wait, don't confirm
        }
        else if (instanceId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase))
        {
            // SwDevice teardown via the documented lifetime-downgrade path.
            // DIF_REMOVE on a SwDeviceLifetimeParentPresent device is
            // transient on Win11 26200 — the kernel re-enumerates the
            // devnode within 5-30s because HTREE\ROOT\0 (the parent) is
            // always present. SwDeviceCreate-reconnect + lifetime=Handle
            // + SwDeviceClose is the only way to actually terminate the
            // lifetime contract. Verified empirically 2026-04-25 against
            // a resurrected BT SwDevice: device transitioned to PHANTOM
            // and stayed PHANTOM for 20+ seconds (vs DIF_REMOVE+pnputil-
            // /force which let it resurrect within 10s).
            var swSw = System.Diagnostics.Stopwatch.StartNew();
            int hr = SwdDeviceFactory.Remove(instanceId);
            goneAfterDif = CM_Locate_DevNodeW(out _, instanceId, 0) != CR_SUCCESS;
            DeviceOrchestrator.LogDiag($"      SwdDeviceFactory.Remove({instanceId}) hr=0x{hr:X8} present={!goneAfterDif} after {swSw.ElapsedMilliseconds}ms");
        }
        else
        {
            DifRemoveDevice(instanceId);
            var waitSw = System.Diagnostics.Stopwatch.StartNew();
            goneAfterDif = WaitForDeviceRemoval(instanceId, timeoutMs);
            DeviceOrchestrator.LogDiag($"      WaitForDeviceRemoval({instanceId}, {timeoutMs}ms) returned {goneAfterDif} after {waitSw.ElapsedMilliseconds}ms");
        }

        // Step 3: pnputil + devcon fallbacks. Normally gated on !fast (skipped
        // during bulk teardown to avoid 14× pnputil process spawn). But when
        // caller passes forceFallbacks=true — e.g. the pre-install sweep in
        // HMContext.InstallDriver — we MUST unstick any device still bound
        // to our old INF, because pnputil /delete-driver refuses to remove a
        // package while any device holds it (error "One or more devices are
        // presently installed using the specified INF"). The failure mode
        // that cost hours: without this, the old DriverStore package + its
        // stale DLL survive every reinstall, pnputil /add-driver reports
        // "Driver package added successfully. (Needed repairing)" and
        // restores the OLD bytes from internal cache, so the fresh v1.1.5
        // self-heal binary literally never loads and input keeps hanging.
        // pnputil and devcon both silently no-op on SWD\HIDMAESTRO\* phantoms
        // left behind by SwDeviceLifetimeParentPresent — verified empirically
        // 2026-04-25 (24s wall time for 11 phantoms, 10 still present after).
        // Skipping the fallback for SWD\ entries cuts startup cleanup from
        // tens of seconds to milliseconds when prior-session phantoms exist.
        if ((!fast || forceFallbacks) && !goneAfterDif &&
            (instanceId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) ||
             instanceId.StartsWith("HID\\",  StringComparison.OrdinalIgnoreCase)))
        {
            bool stillPhantom = CM_Locate_DevNodeW(out _, instanceId, 1) == CR_SUCCESS;
            if (stillPhantom)
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
                        // Async read + Kill-on-timeout — matches the pattern in
                        // DeviceOrchestrator.RunProcess. Sync ReadToEnd blocks
                        // when pnputil hangs (observed during 5+ minute startup
                        // freeze on a force-killed prior session's SWD orphan
                        // that pnputil couldn't shift), preventing the
                        // WaitForExit timeout from ever firing. Background
                        // pipe drain plus an explicit Kill makes the 5-second
                        // budget real.
                        var outTask = proc.StandardOutput.ReadToEndAsync();
                        var errTask = proc.StandardError.ReadToEndAsync();
                        if (!proc.WaitForExit(5000))
                        {
                            try { proc.Kill(entireProcessTree: true); } catch { }
                            proc.WaitForExit(2000);
                        }
                        try { outTask.GetAwaiter().GetResult(); } catch { }
                        try { errTask.GetAwaiter().GetResult(); } catch { }
                    }
                }
                catch { }
            }

            // Step 3.5: devcon fallback — per feedback-devcon-for-cleanup, the
            // CM/SetupDi APIs sometimes leave a phantom that pnputil also can't
            // shift (observed during this session on ROOT\HIDMAESTRO\0000 and
            // ROOT\VID_045E...). `devcon remove @<id>` bypasses both paths and
            // tears the node out cleanly in most of those cases. Only run if
            // devcon is locatable — it ships with the WDK, not the OS, so
            // users without the WDK silently skip this step.
            bool stillPhantom2 = CM_Locate_DevNodeW(out _, instanceId, 1) == CR_SUCCESS;
            if (stillPhantom2)
            {
                string? devcon = TryLocateDevcon();
                if (devcon != null)
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = devcon,
                            Arguments = $"remove \"@{instanceId}\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };
                        using var proc = System.Diagnostics.Process.Start(psi);
                        if (proc != null)
                        {
                            // Same async-read + Kill-on-timeout shape as the
                            // pnputil block above. devcon hangs on phantom
                            // SWD orphans that the kernel won't release until
                            // reboot — without Kill the entire cleanup loop
                            // hung indefinitely waiting on ReadToEnd.
                            var outTask = proc.StandardOutput.ReadToEndAsync();
                            var errTask = proc.StandardError.ReadToEndAsync();
                            if (!proc.WaitForExit(5000))
                            {
                                try { proc.Kill(entireProcessTree: true); } catch { }
                                proc.WaitForExit(2000);
                            }
                            try { outTask.GetAwaiter().GetResult(); } catch { }
                            try { errTask.GetAwaiter().GetResult(); } catch { }
                        }
                    }
                    catch { }
                }
            }
        }

        // Step 4: Final check
        bool removed = CM_Locate_DevNodeW(out _, instanceId, 0) != CR_SUCCESS
                    && CM_Locate_DevNodeW(out _, instanceId, 1) != CR_SUCCESS;
        return removed;
    }

    /// <summary>Locate devcon.exe under the installed WDK. Returns the first
    /// match under C:\Program Files (x86)\Windows Kits\10\Tools\*\x64\, or
    /// null if no WDK Tools directory is found. Result is cached for the
    /// lifetime of the process — a missing devcon won't re-probe on every
    /// fallback invocation.</summary>
    private static string? s_devconPath;
    private static bool s_devconProbed;
    private static string? TryLocateDevcon()
    {
        if (s_devconProbed) return s_devconPath;
        s_devconProbed = true;
        try
        {
            string root = @"C:\Program Files (x86)\Windows Kits\10\Tools";
            if (!System.IO.Directory.Exists(root)) return null;
            foreach (var dir in System.IO.Directory.EnumerateDirectories(root))
            {
                string candidate = System.IO.Path.Combine(dir, "x64", "devcon.exe");
                if (System.IO.File.Exists(candidate))
                {
                    s_devconPath = candidate;
                    return candidate;
                }
            }
        }
        catch { }
        return null;
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
    /// Returns true if the device (or its HID child) is in DN_STARTED state,
    /// meaning its driver is fully bound and the device is functional.
    /// Used by FinalizeNames to poll for PnP readiness instead of fixed sleeps.
    /// </summary>
    public static bool IsDeviceStarted(string instanceId)
    {
        // Check the device itself first
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CR_SUCCESS)
            return false;
        if (CM_Get_DevNode_Status(out uint status, out _, devInst, 0) == CR_SUCCESS
            && (status & DN_STARTED) != 0)
            return true;

        // Also check the HID child (if any) — for virtual controllers the parent
        // may be started but the HID child (where xinputhid binds) is what matters.
        string? hidChild = GetHidChildId(instanceId);
        if (hidChild == null) return false;
        if (CM_Locate_DevNodeW(out uint childInst, hidChild, 0) != CR_SUCCESS)
            return false;
        return CM_Get_DevNode_Status(out uint childStatus, out _, childInst, 0) == CR_SUCCESS
            && (childStatus & DN_STARTED) != 0;
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
    /// Returns all HID child device instance IDs of the given parent (one per
    /// top-level HID collection the parent advertises). The enumeration must
    /// run BEFORE the parent is torn down — once the parent goes to phantom
    /// state, CM_Get_Child / CM_Get_Sibling on it stop working. Callers that
    /// need to explicitly remove HID children alongside their parent (to
    /// avoid the "half-dead PDO" state where the child survives an async
    /// parent-removal cascade) should call this first, save the list, then
    /// remove parent + each captured child.
    /// Returns an empty list if the parent cannot be located or has no children.
    /// </summary>
    public static List<string> GetAllHidChildIds(string parentInstanceId)
    {
        if (CM_Locate_DevNodeW(out uint parentInst, parentInstanceId, 0) != CR_SUCCESS)
            return new List<string>();
        return GetAllChildDeviceIds(parentInst);
    }

    /// <summary>
    /// Scans HKLM\SYSTEM\CurrentControlSet\Enum\HID for orphaned HM HID
    /// children whose parent (ROOT\ or SWD\) no longer exists. Removes each
    /// via DIF_REMOVE. Returns the number of orphans removed.
    ///
    /// HM ownership is identified by the HardwareIDs of the HID instance —
    /// every HM HID child carries "HID\HIDMaestro" + "HID\HIDMaestroGamepad"
    /// entries from the INF. That's contractual and stable across:
    ///   - the v1.1.20 ROOT\ -> SWD\ enumerator migration
    ///   - VID-spoofing Xbox profiles vs HIDMAESTRO-prefix non-Xbox profiles
    /// — so it correctly catches every flavor of HM child without name-
    /// pattern brittleness.
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
                using var deviceKey = hidKey.OpenSubKey(deviceName);
                if (deviceKey == null)
                    continue;

                foreach (string instanceName in deviceKey.GetSubKeyNames())
                {
                    string hidInstanceId = $@"HID\{deviceName}\{instanceName}";

                    // Confirm this HID child belongs to HIDMaestro via its
                    // HardwareIDs. Hardcoded VID/IG_ string matching (the
                    // pre-fix approach) missed Sony/Nintendo profiles and
                    // post-v1.1.20 HIDMAESTRO-prefix children entirely.
                    using var instKey = deviceKey.OpenSubKey(instanceName);
                    if (instKey == null)
                        continue;
                    var hwIds = instKey.GetValue("HardwareID") as string[];
                    bool isHmChild = hwIds != null && Array.Exists(hwIds, id =>
                        id.IndexOf("HIDMaestro", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!isHmChild)
                        continue;

                    // Read the parent instance ID
                    string? parentId = GetRegistryParentId(enumHidPath, deviceName, instanceName);
                    if (parentId == null)
                        continue;

                    // Accept ROOT\ (legacy pre-v1.1.20) and SWD\ (current).
                    // The pre-fix code only accepted ROOT\, so post-SWD-
                    // migration orphans were never cleaned up — every live
                    // profile swap leaked an HID child until reboot.
                    bool parentIsHmEnum =
                        parentId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) ||
                        parentId.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase);
                    if (!parentIsHmEnum)
                        continue;

                    // If parent can be located (live or phantom), it's not
                    // orphaned.
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
