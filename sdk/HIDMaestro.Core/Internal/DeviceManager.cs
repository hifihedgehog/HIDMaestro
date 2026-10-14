using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>
/// Event-driven PnP device management using CM_Register_Notification.
/// No sleeps, no polling: waits on OS callbacks for actual system state changes.
/// </summary>
public static partial class DeviceManager
{
    // ── CfgMgr32 P/Invoke ──

    [LibraryImport("CfgMgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Uninstall_DevNode(uint dnDevInst, uint ulFlags);

    // The callback is a plain function pointer to NotifyDispatch. Each wait's
    // own handler travels in the context argument as a GCHandle.
    [LibraryImport("CfgMgr32.dll")]
    private static unsafe partial uint CM_Register_Notification(ref CM_NOTIFY_FILTER_INTERFACE pFilter, IntPtr pContext,
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, uint, uint> pCallback, out IntPtr pNotifyContext);

    [LibraryImport("CfgMgr32.dll")]
    private static unsafe partial uint CM_Register_Notification(ref CM_NOTIFY_FILTER_INSTANCE pFilter, IntPtr pContext,
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, uint, uint> pCallback, out IntPtr pNotifyContext);

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Unregister_Notification(IntPtr NotifyContext);

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Get_Device_ID_Size(out uint pulLen, uint dnDevInst, uint ulFlags);

    [LibraryImport("CfgMgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_IDW(uint dnDevInst, char[] Buffer, uint BufferLen, uint ulFlags);

    delegate uint CM_NOTIFY_CALLBACK(IntPtr hNotify, IntPtr Context, uint Action, IntPtr EventData, uint EventDataSize);

    const uint CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0;
    const uint CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE = 2;

    const uint CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES = 0x00000002;

    const uint CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL = 0;
    const uint CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL = 1;
    const uint CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED = 7;
    const uint CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED = 9;

    const uint CR_SUCCESS = 0;
    const uint CR_NOT_DISABLEABLE = 0x00000028;
    const uint DN_STARTED = 0x00000008;

    // cfgmgr32.h: CM_NOTIFY_FILTER is four DWORDs and then a union at offset
    // 16 holding a GUID (interface filters), a HANDLE, or WCHAR
    // InstanceId[MAX_DEVICE_ID_LEN] with MAX_DEVICE_ID_LEN 200 (instance
    // filters): 416 bytes. CM_Register_Notification refuses any other
    // cbSize with CR_INVALID_DATA. One struct used to carry the GUID and then
    // the instance ID after it, 432 bytes, so every registration failed and
    // each wait below returned at once. Measured 2026-10-13: CR 0x1F for that
    // layout, CR_SUCCESS for these, for both filter kinds.
    [StructLayout(LayoutKind.Sequential, Size = 416)]
    struct CM_NOTIFY_FILTER_INTERFACE
    {
        public uint cbSize;
        public uint Flags;
        public uint FilterType;
        public uint Reserved;
        public Guid ClassGuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct CM_NOTIFY_FILTER_INSTANCE
    {
        public uint cbSize;
        public uint Flags;
        public uint FilterType;
        public uint Reserved;
        public fixed char InstanceId[200];   // WCHAR InstanceId[MAX_DEVICE_ID_LEN]
    }

    /// <summary>Copy <paramref name="id"/> into the filter's instance ID: at
    /// most 199 characters and a terminator, as the ByValTStr marshalling
    /// this field had before did.</summary>
    static unsafe void SetInstanceId(ref CM_NOTIFY_FILTER_INSTANCE filter, string id)
    {
        int n = Math.Min(id.Length, 199);
        fixed (char* p = filter.InstanceId)
        {
            id.AsSpan(0, n).CopyTo(new Span<char>(p, 200));
            p[n] = '\0';
        }
    }

    /// <summary>The one callback cfgmgr32 calls. It hands the event to the
    /// handler whose GCHandle is the registration's context. An exception
    /// must not unwind into cfgmgr32, and one left unhandled in an
    /// UnmanagedCallersOnly method ends the process, so none escapes.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    static uint NotifyDispatch(IntPtr hNotify, IntPtr context, uint action, IntPtr eventData, uint eventDataSize)
    {
        Interlocked.Increment(ref s_notificationsDelivered);
        // An interface filter delivers only the interface actions, arrival
        // and removal (0 and 1). Every instance action is 2 or higher.
        if (action <= CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL) Interlocked.Increment(ref s_interfaceNotificationsDelivered);
        try
        {
            if (GCHandle.FromIntPtr(context).Target is CM_NOTIFY_CALLBACK handler)
                return handler(hNotify, context, action, eventData, eventDataSize);
        }
        catch { }
        return 0; // ERROR_SUCCESS
    }

    private static long s_notificationsDelivered;

    /// <summary>PnP notifications delivered to a wait since the process
    /// started. A wait whose registration Windows refuses still finishes on
    /// its re-checks, only later, so this count is the evidence that the
    /// notifications themselves arrive. pnp_notify_check reads it.</summary>
    internal static long NotificationsDelivered => Interlocked.Read(ref s_notificationsDelivered);

    private static long s_interfaceNotificationsDelivered;

    /// <summary>The part of <see cref="NotificationsDelivered"/> that came
    /// through a device-interface filter: interface arrivals and removals.
    /// An instance filter never delivers these, so a count above zero shows
    /// the interface filter itself works. pnp_notify_check reads it.</summary>
    internal static long InterfaceNotificationsDelivered => Interlocked.Read(ref s_interfaceNotificationsDelivered);

    /// <summary>The symbolic link of an interface event. CM_NOTIFY_EVENT_DATA
    /// is FilterType and Reserved, then a union at offset 8, and an interface
    /// event puts its class GUID there and the link after it, at offset 24.
    /// Reads no further than <paramref name="size"/>.</summary>
    static string? EventSymbolicLink(IntPtr data, uint size)
    {
        if (data == IntPtr.Zero || size <= 24) return null;
        string s = Marshal.PtrToStringUni(data + 24, (int)(size - 24) / 2);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s.Substring(0, nul) : s;
    }

    /// <summary>A handle no one sets, for waits that only re-check.</summary>
    static readonly WaitHandle NeverSignaled = new ManualResetEvent(false);

    /// <summary>The devnode is present and started.</summary>
    static bool IsStarted(string instanceId) =>
        CM_Locate_DevNodeW(out uint d, instanceId, 0) == CR_SUCCESS
        && CM_Get_DevNode_Status(out uint status, out _, d, 0) == CR_SUCCESS
        && (status & DN_STARTED) != 0;

    /// <summary>How often a wait re-checks its condition between
    /// notifications, so an event that is missed, or that the callback
    /// cannot match, costs at most this long.</summary>
    const int RecheckMs = 50;

    /// <summary>Waits until <paramref name="done"/> holds, checking at once,
    /// again whenever <paramref name="signal"/> is set, and every
    /// <paramref name="recheckMs"/> besides. False when the scaled budget runs
    /// out first.</summary>
    static bool WaitUntil(Func<bool> done, WaitHandle signal, int timeoutMs, int recheckMs = RecheckMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int budget = TimeoutScale.Apply(timeoutMs);
        while (true)
        {
            if (done()) return true;
            long left = budget - sw.ElapsedMilliseconds;
            if (left <= 0) return false;
            signal.WaitOne((int)Math.Min(left, recheckMs));
        }
    }

    /// <summary>Registers the handler whose GCHandle is <paramref name="context"/>
    /// for interface events of one class. Zero when Windows refuses, and the
    /// caller's wait then runs on its re-checks alone.</summary>
    static unsafe IntPtr RegisterInterfaceEvents(Guid interfaceClass, IntPtr context)
    {
        var filter = new CM_NOTIFY_FILTER_INTERFACE
        {
            cbSize = (uint)Marshal.SizeOf<CM_NOTIFY_FILTER_INTERFACE>(),
            FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
            ClassGuid = interfaceClass,
        };
        return CM_Register_Notification(ref filter, context, &NotifyDispatch, out IntPtr h) == CR_SUCCESS ? h : IntPtr.Zero;
    }

    /// <summary>Registers the handler whose GCHandle is <paramref name="context"/>
    /// for the events of one device instance. Zero when Windows refuses.</summary>
    static unsafe IntPtr RegisterInstanceEvents(string instanceId, IntPtr context)
    {
        var filter = new CM_NOTIFY_FILTER_INSTANCE
        {
            cbSize = (uint)Marshal.SizeOf<CM_NOTIFY_FILTER_INSTANCE>(),
            FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE,
        };
        SetInstanceId(ref filter, instanceId);
        return CM_Register_Notification(ref filter, context, &NotifyDispatch, out IntPtr h) == CR_SUCCESS ? h : IntPtr.Zero;
    }

    /// <summary>Registers the handler whose GCHandle is <paramref name="context"/>
    /// for the events of every device instance
    /// (CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES, with an empty instance
    /// ID). Zero when Windows refuses.</summary>
    static unsafe IntPtr RegisterAllInstanceEvents(IntPtr context)
    {
        var filter = new CM_NOTIFY_FILTER_INSTANCE
        {
            cbSize = (uint)Marshal.SizeOf<CM_NOTIFY_FILTER_INSTANCE>(),
            Flags = CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES,
            FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE,
        };
        return CM_Register_Notification(ref filter, context, &NotifyDispatch, out IntPtr h) == CR_SUCCESS ? h : IntPtr.Zero;
    }

    /// <summary>Runs <paramref name="wait"/> with <paramref name="callback"/>
    /// registered through <paramref name="register"/>, which receives the
    /// callback's GCHandle as the registration context. The handle keeps the
    /// callback alive and is freed only after CM_Unregister_Notification,
    /// which waits for callbacks in flight to finish.</summary>
    static bool WithNotification(Func<IntPtr, IntPtr> register, CM_NOTIFY_CALLBACK callback, Func<bool> wait)
    {
        var keep = GCHandle.Alloc(callback);
        IntPtr h = IntPtr.Zero;
        try
        {
            h = register(GCHandle.ToIntPtr(keep));
            return wait();
        }
        finally
        {
            if (h != IntPtr.Zero) CM_Unregister_Notification(h);
            keep.Free();
        }
    }

    /// <summary>
    /// Waits for a specific device interface GUID to appear on the given device.
    /// Uses CM_Register_Notification: no polling, no sleeping.
    /// </summary>
    public static bool WaitForDeviceInterface(string instanceId, Guid interfaceGuid, int timeoutMs = 10000)
    {
        // Check if already present
        // (CM_Locate + check interfaces via registry)
        if (HasInterface(instanceId, interfaceGuid))
            return true;

        string instHash = instanceId.Replace('\\', '#').ToLowerInvariant();
        bool arrived = false;
        using var signal = new AutoResetEvent(false);
        CM_NOTIFY_CALLBACK callback = (hNotify, context, action, data, size) =>
        {
            if (action == CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL
                && EventSymbolicLink(data, size)?.ToLowerInvariant().Contains(instHash) == true)
            {
                Volatile.Write(ref arrived, true);
                signal.Set();
            }
            return 0u;
        };
        return WithNotification(cb => RegisterInterfaceEvents(interfaceGuid, cb), callback,
            () => WaitUntil(() => Volatile.Read(ref arrived) || HasInterface(instanceId, interfaceGuid), signal, timeoutMs));
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
        static bool HasChild(string parent) =>
            CM_Locate_DevNodeW(out uint p, parent, 0) == CR_SUCCESS && CM_Get_Child(out uint _, p, 0) == CR_SUCCESS;
        if (HasChild(parentInstanceId))
            return true;

        // Wakes on every devnode Windows enumerates and re-checks the parent,
        // so the wait ends as the child appears. A HID interface arrives
        // only after the child has started, and its link names the child,
        // not the parent. Waking on those cost 30 to 50 ms per plain HID
        // create (measured 2026-10-13, DualSense and G502 medians).
        using var signal = new AutoResetEvent(false);
        CM_NOTIFY_CALLBACK callback = (hNotify, context, action, data, size) =>
        {
            if (action == CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED) signal.Set();
            return 0u;
        };
        return WithNotification(RegisterAllInstanceEvents, callback,
            () => WaitUntil(() => HasChild(parentInstanceId), signal, timeoutMs));
    }

    /// <summary>
    /// Waits for a device instance to be fully removed.
    /// Returns true when gone, false on timeout.
    /// </summary>
    public static bool WaitForDeviceRemoval(string instanceId, int timeoutMs = 5000)
    {
        static bool Gone(string id) => CM_Locate_DevNodeW(out uint _, id, 0) != CR_SUCCESS;
        if (Gone(instanceId))
            return true;

        bool removed = false;
        using var signal = new AutoResetEvent(false);
        CM_NOTIFY_CALLBACK callback = (hNotify, context, action, data, size) =>
        {
            if (action == CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED)
            {
                Volatile.Write(ref removed, true);
                signal.Set();
            }
            return 0u;
        };
        return WithNotification(cb => RegisterInstanceEvents(instanceId, cb), callback,
            () => WaitUntil(() => Volatile.Read(ref removed) || Gone(instanceId), signal, timeoutMs));
    }

    // ── SetupAPI P/Invoke for DIF_REMOVE ──

    [LibraryImport("SetupAPI.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static partial IntPtr SetupDiGetClassDevsByRef(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

    [LibraryImport("SetupAPI.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiOpenDeviceInfoW(IntPtr DeviceInfoSet, string DeviceInstanceId, IntPtr hwndParent, uint OpenFlags, IntPtr DeviceInfoData);

    [LibraryImport("SetupAPI.dll", EntryPoint = "SetupDiCallClassInstaller", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiCallClassInstaller(int InstallFunction, IntPtr DeviceInfoSet, IntPtr DeviceInfoData);

    [LibraryImport("SetupAPI.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [LibraryImport("SetupAPI.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, IntPtr DeviceInfoData);

    [LibraryImport("SetupAPI.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInstanceIdW(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, [Out] char[] DeviceInstanceId, uint DeviceInstanceIdSize, out uint RequiredSize);

    [LibraryImport("SetupAPI.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiRemoveDevice(IntPtr DeviceInfoSet, IntPtr DeviceInfoData);

    const uint DIGCF_ALLCLASSES = 0x04;
    const int DIF_REMOVE = 0x05;

    /// <summary>
    /// Sweep accumulated HIDMaestro phantom devnodes from the Windows PnP
    /// registry cache. v1.1.30's per-call unique SwD instance suffix
    /// guarantees a fresh devnode every create, but Windows preserves a
    /// per-instance registry hive entry indefinitely after teardown.
    /// Across many sessions these accumulate as "hidden devices" in
    /// Device Manager. They are functionally harmless (no XInput slot,
    /// no joy.cpl/WGI/RawInput/SDL3 visibility, only surfaced by
    /// "Show Hidden Devices") but visually clutter Device Manager for
    /// power users running many sessions per boot.
    ///
    /// SetupDiRemoveDevice via DIGCF_ALLCLASSES enumerator is the
    /// documented path that actually deletes the registry hive cache
    /// for non-present devnodes (verified empirically 2026-04-25:
    /// 28/28 phantoms removed in 492ms, zero failures, PRESENT
    /// devnodes correctly skipped).
    ///
    /// Skips PRESENT devnodes: only touches PHANTOM/non-loaded ones.
    /// Safe to call while a HIDMaestro session is live; will not disturb
    /// any current controller. Returns the number of phantoms removed.
    /// </summary>
    internal static int RemoveAccumulatedHmPhantoms()
    {
        // Issue #28 (v1.3.16): the prior VID_045E&PID_028E /
        // VID_BEEF&PID_F000 substring checks matched real Xbox 360 wired
        // controllers and PadForge Custom devices regardless of enumerator
        //: so a real unplugged Xbox 360's phantom record qualified. The
        // new test scopes pure-name matches to the HIDMAESTRO* /
        // HMCOMPANION* enumerator forms (which are HM-exclusive by
        // construction), and otherwise routes through IsHidMaestroOwned to
        // prove ownership via the devnode's HardwareID list.
        bool IsHmInstance(string iid)
        {
            if (iid.IndexOf("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) >= 0
                || iid.IndexOf("HMCOMPANION", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return IsHidMaestroOwned(iid);
        }

        int removed = 0;
        Guid nullGuid = Guid.Empty;
        IntPtr dis = SetupDiGetClassDevsByRef(ref nullGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_ALLCLASSES);
        if (dis == new IntPtr(-1)) return 0;

        try
        {
            int devInfoSize = IntPtr.Size == 8 ? 32 : 28;
            byte[] devInfoBuf = new byte[devInfoSize];
            var devInfoHandle = System.Runtime.InteropServices.GCHandle.Alloc(
                devInfoBuf, System.Runtime.InteropServices.GCHandleType.Pinned);

            try
            {
                uint index = 0;
                char[] idBuf = new char[512];
                while (true)
                {
                    BitConverter.GetBytes(devInfoSize).CopyTo(devInfoBuf, 0);
                    if (!SetupDiEnumDeviceInfo(dis, index, devInfoHandle.AddrOfPinnedObject()))
                        break;
                    index++;

                    if (!SetupDiGetDeviceInstanceIdW(dis, devInfoHandle.AddrOfPinnedObject(),
                            idBuf, (uint)idBuf.Length, out uint idLen))
                        continue;
                    string instanceId = new string(idBuf, 0, (int)idLen - 1);

                    if (!IsHmInstance(instanceId)) continue;

                    // Skip PRESENT devnodes. Only sweep PHANTOMs to guarantee
                    // we never disturb a live controller.
                    if (CM_Locate_DevNodeW(out _, instanceId, 0) == CR_SUCCESS)
                        continue;

                    // A phantom whose identity another program holds is that
                    // program's create in flight: its record exists before
                    // its devnode starts.
                    string instName = instanceId.Substring(instanceId.LastIndexOf('\\') + 1);
                    if (DeviceIdentity.IsIdentityToken(instName)
                        && MachineClaims.IsHeld(MachineClaims.IdentityPipeName(instName)))
                        continue;

                    if (SetupDiRemoveDevice(dis, devInfoHandle.AddrOfPinnedObject()))
                        removed++;
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

        if (removed > 0)
            DeviceOrchestrator.LogDiag($"    RemoveAccumulatedHmPhantoms: removed {removed} HIDMaestro phantom(s)");
        return removed;
    }

    /// <summary>
    /// Returns true when the PnP devnode at <paramref name="instanceId"/> carries
    /// a HIDMaestro-signature HardwareID: i.e. one of `root\HIDMaestro`,
    /// `root\HIDMaestroGamepad`, `root\HIDMaestroXUSB` (or any future
    /// `*HIDMaestro*` literal). Every HM-created devnode writes one of those
    /// strings into its HardwareID multi-sz at SetupDi-create time (plain HID
    /// at DeviceNodeCreator.cs:121, gamepad companion at
    /// DeviceOrchestrator.cs:889-894, XUSB at DeviceOrchestrator.cs:1019-1020),
    /// so the presence of "HIDMaestro" in HardwareID is the ownership proof
    /// every sweep needs before disabling / uninstalling / renaming /
    /// property-writing a node.
    ///
    /// <para>Issue #28 (v1.3.16): without this guard the
    /// <c>CleanupGhostDevices</c> / <c>SetBusTypeGuidUsb</c> /
    /// <c>RemoveAllVirtualControllers</c> / <c>DisableGhostXusbInterfaces</c>
    /// sweeps selected by instance-path pattern alone and ran their
    /// destructive operations against whatever matched: disabling a
    /// coexisting vJoy root HIDClass device, mutating ViGEmBus / HidHide
    /// SYSTEM nodes, or removing third-party root-enumerated VID_ devices
    /// the user never intended HIDMaestro to touch.</para>
    ///
    /// <para>Returns false on missing devnode, missing HardwareID value,
    /// registry-access exception, or no "HIDMaestro" substring match.
    /// Conservative-by-default: when in doubt, do NOT claim ownership.</para>
    /// </summary>
    internal static bool IsHidMaestroOwned(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return false;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
            if (k == null) return false;
            var hwIds = k.GetValue("HardwareID") as string[];
            if (hwIds == null) return false;
            return Array.Exists(hwIds, id => id != null &&
                id.IndexOf("HIDMaestro", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        catch { return false; }
    }

    /// <summary>
    /// Clears every other parent under <paramref name="enumRoot"/>\<paramref name="enumerator"/>
    /// that holds <paramref name="prefix"/> as its ParentIdPrefix, and says
    /// whether the way is clear. Two parents of one enumerator with one
    /// prefix make hidclass build the same HID child id twice, which PnP
    /// answers with bugcheck 0xCA (duplicate PDO). A ROOT record is
    /// re-enumerated at every boot, live or not, so it is removed either
    /// way and a boot loop is what is at stake. An SWD record only
    /// enumerates while its creator holds it, so only a live one counts.
    /// False means a rival is still there and the caller must not create.
    /// </summary>
    internal static bool ClearParentIdPrefixRivals(string enumRoot, string enumerator, string token, string prefix)
    {
        string enumPath = $@"SYSTEM\CurrentControlSet\Enum\{enumRoot}\{enumerator}";
        string[] names;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(enumPath);
            names = k?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch { names = Array.Empty<string>(); }

        bool root = string.Equals(enumRoot, "ROOT", StringComparison.OrdinalIgnoreCase);
        bool clear = true;
        foreach (string name in names)
        {
            if (string.Equals(name, token, StringComparison.OrdinalIgnoreCase)) continue;
            string? held;
            try
            {
                using var ik = Registry.LocalMachine.OpenSubKey($@"{enumPath}\{name}");
                held = ik?.GetValue("ParentIdPrefix") as string;
            }
            catch { continue; }
            if (!string.Equals(held, prefix, StringComparison.OrdinalIgnoreCase)) continue;

            string rival = $@"{enumRoot}\{enumerator}\{name}";
            bool live = CM_Locate_DevNodeW(out _, rival, 0) == CR_SUCCESS;
            if (!root && !live) continue;

            if (!IsHidMaestroOwned(rival))
            {
                DeviceOrchestrator.LogDiag($"      {rival} holds ParentIdPrefix {prefix} and is not HIDMaestro-owned; refusing to create beside it");
                clear = false;
                continue;
            }

            // A present rival is removed only when its owner is provably
            // gone. One that belongs to a running program, here or
            // elsewhere, or to a program whose state cannot be known,
            // stays, and this create is refused instead: either way there
            // is never a second parent on one prefix.
            if (live)
            {
                var owner = OwnerCensus.Classify(rival);
                if (owner is not (OwnerCensus.Owner.Dead or OwnerCensus.Owner.DeadLegacy))
                {
                    DeviceOrchestrator.LogDiag($"      {rival} holds ParentIdPrefix {prefix} and its owner is {owner}; refusing to create beside it");
                    string who = owner switch
                    {
                        OwnerCensus.Owner.Unknown =>
                            "a program built on HIDMaestro older than 1.12.0, which may still be running. " +
                            "If it is not, remove the device with HMContext.RemoveAllVirtualControllers(HMCleanupScope.Everything, preserveInstall: true)",
                        OwnerCensus.Owner.LiveLegacy => "a running program built on HIDMaestro 1.12.0",
                        _ => "a running program",
                    };
                    throw new HMResourceInUseException(
                        $"The device {rival} holds the ParentIdPrefix {prefix} this controller's identity derives, and it belongs to {who}. " +
                        "Two devices on one prefix stop Windows from booting, so this create is refused.",
                        $"ParentIdPrefix {prefix}", null);
                }
            }

            DeviceOrchestrator.LogDiag($"      {rival} holds ParentIdPrefix {prefix}; removing it before create");
            RemoveDevice(rival, timeoutMs: 120_000, forceFallbacks: true);

            bool stillThere;
            if (root)
            {
                try { using var ik = Registry.LocalMachine.OpenSubKey($@"{enumPath}\{name}"); stillThere = ik != null; }
                catch { stillThere = true; }
            }
            else stillThere = CM_Locate_DevNodeW(out _, rival, 0) == CR_SUCCESS;

            if (stillThere)
            {
                DeviceOrchestrator.LogDiag($"      {rival} survived removal; refusing to create a second parent on {prefix}");
                clear = false;
            }
        }
        return clear;
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

        bool isSwdParent = instanceId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase);

        // For SWD parents, close the SwDevice handle FIRST, then mop up
        // surviving children. HID children of an SwD parent cannot fully
        // unwind until the parent's HSWDEVICE handle releases its kernel
        // refcount (DIF_REMOVE on the child completes the handle/PDO
        // detach but the queries can't drain while the parent still
        // holds the lifetime lock). The pre-fix child-first order paid
        // 2000ms per HID child on WaitForDeviceRemoval (timed out, child
        // didn't actually go), then the parent close took 55ms and
        // cascaded the children automatically. Net: ~5.4s per SWD
        // teardown for one HID child, scaling worse with more children.
        // Post-fix: parent close first (~55ms), child-survivor sweep is
        // a fast no-op for the typical case where Windows already
        // cascade-removed them with the SwD parent.
        var childIds = GetAllChildDeviceIds(devInst);
        bool goneAfterDif = false;

        // For SWD parents that are PHANTOM-only (registry residue, no live
        // devnode), skip the hmswd.exe roundtrip: there's no live device
        // to terminate. Each helper spawn costs ~50-75 ms with no useful
        // work; over a 5-cycle Xbox 360 wired probe the sweep accumulates
        // 4 phantoms by cycle 5, adding ~250 ms to that teardown for
        // nothing. Detect via CM_Locate_DevNodeW NORMAL (mode 0) returning
        // failure while PHANTOM (mode 1) succeeds: the early-bail at the
        // top of this function only fires when BOTH fail, so we still got
        // here for phantom-only entries.
        bool isLive = CM_Locate_DevNodeW(out _, instanceId, 0) == CR_SUCCESS;
        if (isSwdParent && !fast && !isLive)
        {
            DeviceOrchestrator.LogDiag($"      SwdDeviceFactory.Remove({instanceId}) SKIPPED (phantom-only registry residue)");
            goneAfterDif = true;
        }
        else if (isSwdParent && fast && isLive)
        {
            // Force-close recovery fix (2026-07-21): fast mode must still
            // use the SwD lifetime terminator. The CM_Disable/CM_Uninstall
            // pair below is documented (above) as transient-and-slow for a
            // SwDeviceLifetimeParentPresent device whose orphaned WUDFHost
            // is still up: the devnode lingered 6-17 s per device in the
            // force-close recovery measurements, and the sequential sweep
            // in RemoveAllVirtualControllers summed to ~40 s for a
            // 360-wired + series-bt pair. SwdDeviceFactory.Remove is the
            // empirically-verified terminator (reconnect + lifetime=Handle
            // + SwDeviceClose, see the !fast branch below): the handle
            // close starts the SAME kernel cascade a graceful consumer
            // close does (~1-2 s for all devices, per PadForge's normal
            // exit). Fast semantics are preserved: no WaitForDeviceRemoval
            // here; the cascade completes asynchronously and Step 2's CM
            // sweep below remains as belt-and-braces for survivors.
            var swFastSw = System.Diagnostics.Stopwatch.StartNew();
            int hrFast = SwdDeviceFactory.Remove(instanceId);
            DeviceOrchestrator.LogDiag(
                $"      SwdDeviceFactory.Remove({instanceId}) hr=0x{hrFast:X8} " +
                $"(fast mode, no removal wait) in {swFastSw.ElapsedMilliseconds}ms");
        }
        else if (isSwdParent && !fast)
        {
            // SwDevice teardown via the documented lifetime-downgrade path.
            // DIF_REMOVE on a SwDeviceLifetimeParentPresent device is
            // transient on Win11 26200: the kernel re-enumerates the
            // devnode within 5-30s because HTREE\ROOT\0 (the parent) is
            // always present. SwDeviceCreate-reconnect + lifetime=Handle
            // + SwDeviceClose is the only way to actually terminate the
            // lifetime contract. Verified empirically 2026-04-25 against
            // a resurrected BT SwDevice: device transitioned to PHANTOM
            // and stayed PHANTOM for 20+ seconds (vs DIF_REMOVE+pnputil-
            // /force which let it resurrect within 10s).
            //
            // SwdDeviceFactory.Remove returns when the HSWDEVICE handle
            // closes: NOT when the kernel has finished propagating the
            // devnode removal through the PnP tree. We must block on a
            // CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED event for the
            // instance to guarantee the device is fully offline before
            // proceeding to children/cleanup. CM_Locate_DevNodeW
            // immediately after Remove() commonly returns "still present"
            // (verified in the 09:21:18 diag log: present=True after
            // 56 ms hr=0x0).
            var swSw = System.Diagnostics.Stopwatch.StartNew();
            int hr = SwdDeviceFactory.Remove(instanceId);
            // Block until the kernel actually fires the device-removed
            // notification: or the timeout expires. Use the caller's
            // timeoutMs budget so callers explicitly choose how long to
            // wait. Live-swap callers pass 120_000 ms (the full BT
            // xinputhid cascade budget); cleanup paths may pass less.
            goneAfterDif = WaitForDeviceRemoval(instanceId, timeoutMs);
            DeviceOrchestrator.LogDiag($"      SwdDeviceFactory.Remove({instanceId}) hr=0x{hr:X8} confirmed_offline={goneAfterDif} after {swSw.ElapsedMilliseconds}ms");
        }

        // Step 1: Find and remove all children. For SWD parents this
        // typically becomes a no-op because the SwD close already
        // cascade-removed them.
        foreach (string childId in childIds)
        {
            // Skip if the SwD-parent close already cascaded this child away.
            if (isSwdParent && !fast
                && CM_Locate_DevNodeW(out _, childId, 0) != CR_SUCCESS
                && CM_Locate_DevNodeW(out _, childId, 1) != CR_SUCCESS)
                continue;

            if (fast)
            {
                // Fast mode: CM_Disable + CM_Uninstall directly. Avoids the
                // SetupDiRemoveDevice → IRP_MN_QUERY_REMOVE → kernel-veto
                // timeout (~1s per device) that dominates cleanup time when
                // the UMDF host still holds handles after a force-kill. A
                // keyboard refuses the disable and takes SetupDiRemoveDevice
                // instead (DisableAndUninstall), before its parent is tried.
                if (CM_Locate_DevNodeW(out uint ci, childId, 0) == CR_SUCCESS
                 || CM_Locate_DevNodeW(out ci, childId, 1) == CR_SUCCESS)
                    DisableAndUninstall(ci, childId, waitMs: 2000);
            }
            else
            {
                DifRemoveDevice(childId);
                WaitForDeviceRemoval(childId, 2000);
            }
        }

        // Step 2: Remove the parent (for non-SWD; SWD already done above)
        if (fast)
        {
            DisableAndUninstall(devInst, instanceId, waitMs: 0);
            goneAfterDif = false; // don't wait, don't confirm
        }
        else if (isSwdParent)
        {
            // Already removed at the top of the function. Re-check in case
            // a survivor sweep above kicked the parent into a transient
            // state.
            goneAfterDif = CM_Locate_DevNodeW(out _, instanceId, 0) != CR_SUCCESS;
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
        // caller passes forceFallbacks=true: e.g. the pre-install sweep in
        // HMContext.InstallDriver: we MUST unstick any device still bound
        // to our old INF, because pnputil /delete-driver refuses to remove a
        // package while any device holds it (error "One or more devices are
        // presently installed using the specified INF"). The failure mode
        // that cost hours: without this, the old DriverStore package + its
        // stale DLL survive every reinstall, pnputil /add-driver reports
        // "Driver package added successfully. (Needed repairing)" and
        // restores the OLD bytes from internal cache, so the fresh v1.1.5
        // self-heal binary literally never loads and input keeps hanging.
        // pnputil and devcon both silently no-op on SWD\HIDMAESTRO\* phantoms
        // left behind by SwDeviceLifetimeParentPresent: verified empirically
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
                        // Async read + Kill-on-timeout: matches the pattern in
                        // DeviceOrchestrator.RunProcess. Sync ReadToEnd blocks
                        // when pnputil hangs (observed during 5+ minute startup
                        // freeze on a force-killed prior session's SWD orphan
                        // that pnputil couldn't shift), preventing the
                        // WaitForExit timeout from ever firing. Background
                        // pipe drain plus an explicit Kill makes the 5-second
                        // budget real.
                        var outTask = proc.StandardOutput.ReadToEndAsync();
                        var errTask = proc.StandardError.ReadToEndAsync();
                        if (!proc.WaitForExit(TimeoutScale.Apply(5000)))
                        {
                            try { proc.Kill(entireProcessTree: true); } catch { }
                            proc.WaitForExit(TimeoutScale.Apply(2000));
                        }
                        try { outTask.GetAwaiter().GetResult(); } catch { }
                        try { errTask.GetAwaiter().GetResult(); } catch { }
                    }
                }
                catch { }
            }

            // Step 3.5: devcon fallback: per feedback-devcon-for-cleanup, the
            // CM/SetupDi APIs sometimes leave a phantom that pnputil also can't
            // shift (observed during this session on ROOT\HIDMAESTRO\0000 and
            // ROOT\VID_045E...). `devcon remove @<id>` bypasses both paths and
            // tears the node out cleanly in most of those cases. Only run if
            // devcon is locatable: it ships with the WDK, not the OS, so
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
                            // reboot: without Kill the entire cleanup loop
                            // hung indefinitely waiting on ReadToEnd.
                            // T38-2: scaled budget so Atom-class hardware
                            // doesn't get a 5 s timeout while everything else
                            // is 50 s. Was the only un-scaled WaitForExit
                            // budget in DeviceManager.
                            var outTask = proc.StandardOutput.ReadToEndAsync();
                            var errTask = proc.StandardError.ReadToEndAsync();
                            if (!proc.WaitForExit(TimeoutScale.Apply(5000)))
                            {
                                try { proc.Kill(entireProcessTree: true); } catch { }
                                proc.WaitForExit(TimeoutScale.Apply(2000));
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
    /// match under C:\Program Files (x86)\Windows Kits\10\Tools\*\&lt;arch&gt;\,
    /// where arch is the one this machine runs, or null if no WDK Tools
    /// directory is found. Result is cached for the lifetime of the
    /// process: a missing devcon won't re-probe on every fallback
    /// invocation.</summary>
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
            string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                          == System.Runtime.InteropServices.Architecture.Arm64
                ? "arm64"
                : "x64";
            foreach (var dir in System.IO.Directory.EnumerateDirectories(root))
            {
                string candidate = System.IO.Path.Combine(dir, arch, "devcon.exe");
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
    /// Gets the device instance ID string for a given devnode. T33-2: uses
    /// the same thread-local reusable buffer as GetHidChildId so per-call
    /// char[] allocations on enumeration paths (GetAllChildDeviceIds,
    /// RemoveOrphanHidChildren) don't generate GC pressure on slow hardware.
    /// </summary>
    static string? GetDeviceId(uint devInst)
    {
        if (CM_Get_Device_ID_Size(out uint idLen, devInst, 0) != CR_SUCCESS)
            return null;
        var buffer = t_devIdBuffer;
        if (buffer == null || buffer.Length < idLen + 1)
            buffer = t_devIdBuffer = new char[Math.Max(512, (int)idLen + 1)];
        if (CM_Get_Device_IDW(devInst, buffer, (uint)buffer.Length, 0) != CR_SUCCESS)
            return null;
        return new string(buffer, 0, (int)idLen);
    }

    /// <summary>
    /// Fast-mode removal of one devnode: CM_Disable_DevNode, then
    /// CM_Uninstall_DevNode. Windows will not disable a keyboard, and while
    /// one is up it will not disable the device above it either, so pnputil
    /// and devcon fail on that root too (ERROR_NOT_DISABLEABLE).
    /// SetupDiRemoveDevice still removes the keyboard itself, and the root
    /// can be disabled once the keyboard is gone, so a devnode that refuses
    /// the disable is removed that way. RemoveDevice takes the children
    /// before the parent, so a keyboard child goes first, and
    /// <paramref name="waitMs"/> waits it out before the parent is tried.
    ///
    /// Measured 2026-10-13 on the root of a keyboard whose consumer had
    /// been killed: CM_Disable_DevNode returned CR_NOT_DISABLEABLE and
    /// SetupDiRemoveDevice failed with ERROR_NOT_DISABLEABLE. The same call
    /// on its keyboard child succeeded, the root then reported disableable,
    /// and SetupDiRemoveDevice removed it. The two mice the same consumer
    /// held reported disableable throughout.
    /// </summary>
    static void DisableAndUninstall(uint devInst, string instanceId, int waitMs)
    {
        if (CM_Disable_DevNode(devInst, 0) != CR_NOT_DISABLEABLE)
        {
            CM_Uninstall_DevNode(devInst, 0);
            return;
        }
        DifRemoveDevice(instanceId);
        if (waitMs > 0)
            WaitForDeviceRemoval(instanceId, waitMs);
    }

    /// <summary>
    /// Removes a single device via SetupDiRemoveDevice (same as devcon remove).
    /// This is more thorough than DIF_REMOVE: it bypasses class installers and
    /// directly removes the device node, preventing ghost entries on UMDF devices.
    /// Falls back to CM_Disable + CM_Uninstall if SetupAPI fails.
    /// </summary>
    static void DifRemoveDevice(string instanceId)
    {
        uint devInst = 0;
        bool located = CM_Locate_DevNodeW(out devInst, instanceId, 0) == CR_SUCCESS
                    || CM_Locate_DevNodeW(out devInst, instanceId, 1) == CR_SUCCESS;

        // Use Guid.Empty + DIGCF_ALLCLASSES to enumerate ALL devices including phantoms.
        // This is critical: a device-class-specific DevInfoSet won't find phantoms.
        Guid nullGuid = Guid.Empty;
        IntPtr dis = SetupDiGetClassDevsByRef(ref nullGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_ALLCLASSES);
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

                // SetupDiRemoveDevice: direct removal like devcon.exe.
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

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Enable_DevNode(uint dnDevInst, uint ulFlags);

    /// <summary>
    /// Restarts a device by disabling and re-enabling it. No process spawning.
    /// </summary>
    public static bool RestartDevice(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CR_SUCCESS)
            return false;
        // A disable stops the stack and leaves the devnode in place, so no
        // removal event follows it. Wait until the device has left the
        // started state, checked at once and every 2 ms, capped at 5 s. A
        // refused disable has nothing to wait for.
        if (CM_Disable_DevNode(devInst, 0) == CR_SUCCESS)
            WaitUntil(() => !IsStarted(instanceId), NeverSignaled, 5000, recheckMs: 2);
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

        // Also check the HID child (if any): for virtual controllers the parent
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
    // T29-2: thread-local reusable char buffer. WaitForHidChild polls this
    // at 25 ms cadence until the HID child appears; on a cold-start setup
    // that's 4-8 calls per controller, each allocating a fresh char[]. Per-
    // thread reuse eliminates the GC pressure (esp. relevant on Atom-class
    // hardware where every alloc avoided is a cycle saved). Sized at 512
    // chars: covers every realistic device-instance ID length while staying
    // small enough that the per-thread allocation overhead is one-time.
    [ThreadStatic]
    private static char[]? t_devIdBuffer;

    public static string? GetHidChildId(string parentInstanceId)
    {
        if (CM_Locate_DevNodeW(out uint parentInst, parentInstanceId, 0) != CR_SUCCESS)
            return null;

        if (CM_Get_Child(out uint childInst, parentInst, 0) != CR_SUCCESS)
            return null;

        if (CM_Get_Device_ID_Size(out uint idLen, childInst, 0) != CR_SUCCESS)
            return null;

        var buffer = t_devIdBuffer;
        if (buffer == null || buffer.Length < idLen + 1)
            buffer = t_devIdBuffer = new char[Math.Max(512, (int)idLen + 1)];
        if (CM_Get_Device_IDW(childInst, buffer, (uint)buffer.Length, 0) != CR_SUCCESS)
            return null;

        return new string(buffer, 0, (int)idLen);
    }

    /// <summary>
    /// Returns all HID child device instance IDs of the given parent (one per
    /// top-level HID collection the parent advertises). The enumeration must
    /// run BEFORE the parent is torn down: once the parent goes to phantom
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
    /// HM ownership is identified by the HardwareIDs of the HID instance
    /// every HM HID child carries "HID\HIDMaestro" + "HID\HIDMaestroGamepad"
    /// entries from the INF. That's contractual and stable across:
    ///   - the v1.1.20 ROOT\ -> SWD\ enumerator migration
    ///   - VID-spoofing Xbox profiles vs HIDMAESTRO-prefix non-Xbox profiles
    ///: so it correctly catches every flavor of HM child without name-
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
                    // migration orphans were never cleaned up: every live
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

                    // Parent is gone: remove the orphan silently. Caller
                    // gets the count for whatever observability it wants.
                    DifRemoveDevice(hidInstanceId);
                    removed++;
                }
            }
        }
        catch
        {
            // Defensive: a single bad HID child node shouldn't abort the
            // overall sweep. Caller still gets the removed count.
        }

        return removed;
    }

    /// <summary>
    /// Reads the ParentIdPrefix or reconstructs the parent instance ID for a HID child
    /// from the registry.
    /// </summary>
    internal static string? GetRegistryParentId(string enumHidPath, string deviceName, string instanceName)
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
