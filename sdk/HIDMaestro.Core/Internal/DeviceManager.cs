using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HIDMaestro.Internal;

/// <summary>
/// Event-driven PnP device management using CM_Register_Notification.
/// No sleeps, no polling: waits on OS callbacks for actual system state changes.
/// </summary>
public static partial class DeviceManager
{
    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

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

    internal delegate uint CM_NOTIFY_CALLBACK(IntPtr hNotify, IntPtr Context, uint Action, IntPtr EventData, uint EventDataSize);

    const uint CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0;
    const uint CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE = 2;

    const uint CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES = 0x00000002;

    const uint CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL = 0;
    const uint CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL = 1;
    const uint CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED = 7;
    const uint CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED = 9;

    const uint CR_SUCCESS = 0;
    const uint CM_LOCATE_DEVNODE_PHANTOM = 0x00000001;
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

    /// <summary>The one callback cfgmgr32 calls (<see cref="Dispatch"/>).
    /// An exception must not unwind into cfgmgr32, and one left unhandled
    /// in an UnmanagedCallersOnly method ends the process, so none
    /// escapes.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    static uint NotifyDispatch(IntPtr hNotify, IntPtr context, uint action, IntPtr eventData, uint eventDataSize) =>
        Dispatch(hNotify, context, action, eventData, eventDataSize);

    /// <summary>Counts the event, shows an instance event to
    /// <see cref="InstanceEventTap"/>, and hands it to the handler whose
    /// GCHandle is the registration's context. The tap runs in a try of
    /// its own, so a tap that throws costs the handler nothing. Nothing
    /// escapes. pnp_notify_check's offline part calls it directly.</summary>
    internal static uint Dispatch(IntPtr hNotify, IntPtr context, uint action, IntPtr eventData, uint eventDataSize)
    {
        Interlocked.Increment(ref s_notificationsDelivered);
        // An interface filter delivers only the interface actions, arrival
        // and removal (0 and 1). Every instance action is 2 or higher.
        if (action <= CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL) Interlocked.Increment(ref s_interfaceNotificationsDelivered);
        if (InstanceEventTap is { } tap && action >= CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED
            && action <= CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED)
        {
            try { tap(hNotify, action, InstanceIdOf(eventData, eventDataSize)); }
            catch { }
        }
        try
        {
            if (GCHandle.FromIntPtr(context).Target is CM_NOTIFY_CALLBACK handler)
                return handler(hNotify, context, action, eventData, eventDataSize);
        }
        catch { }
        return 0; // ERROR_SUCCESS
    }

    private static long s_notificationsDelivered;

    /// <summary>Sees every device instance event (enumerated, started,
    /// removed) any registration of this process receives, with the
    /// registration that received it and the instance id, as cfgmgr32
    /// delivers it. Opens no registration of its own. pnp_notify_check sets
    /// it to time a HID child's own REMOVED against its parent's through the
    /// all-instances registration its held-open wait keeps, which
    /// <see cref="AllInstancesRegistrationTap"/> names. Null outside
    /// it.</summary>
    internal static Action<IntPtr, uint, string>? InstanceEventTap;

    /// <summary>Sees each registration <see cref="RegisterAllInstanceEvents"/>
    /// makes, on the thread that asked for it: its handle, or zero when
    /// Windows refused it. pnp_notify_check sets it to learn which
    /// registration its held-open wait keeps, and takes its timing and the
    /// timing's controls from that registration's events alone. Null
    /// outside it.</summary>
    internal static Action<IntPtr>? AllInstancesRegistrationTap;

    /// <summary>The instance id of an instance event. CM_NOTIFY_EVENT_DATA
    /// is FilterType and Reserved, then a union at offset 8, which an
    /// instance event fills with the id. Reads no further than
    /// <paramref name="size"/>.</summary>
    internal static string InstanceIdOf(IntPtr data, uint size)
    {
        int chars = size > 8 ? (int)(size - 8) / 2 : 0;
        if (data == IntPtr.Zero || chars == 0) return "";
        string s = Marshal.PtrToStringUni(data + 8, chars);
        int end = s.IndexOf('\0');
        return end >= 0 ? s.Substring(0, end) : s;
    }

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

    private static long s_removalsOnNotification, s_removalsWithParent, s_removalsWithoutNotification, s_removalsNotGone,
                        s_removalsUnregistered;

    /// <summary>Removal tests of a devnode in the tree at their start,
    /// counted once each when the watch ends, by how they ended: on the
    /// DEVICEINSTANCEREMOVED notification, on its parent's (a child removed
    /// while its parent's watch was open, which opens no registration of
    /// its own, <see cref="SubtreeChildren"/>), gone without one past its
    /// bound, not gone, or with the notification's registration refused. A
    /// removal made in steps on one devnode is tested on one watch from its
    /// first step to its last (<see cref="StepwiseRemoval"/>), so it counts
    /// once, by how its last step ended, as present when its first step
    /// started.
    /// The change that requires the notification rests on Windows sending
    /// it for every removal the SDK makes, and these counts are where a run
    /// shows it. pnp_notify_check reads them.</summary>
    internal static long RemovalsOnNotification => Interlocked.Read(ref s_removalsOnNotification);
    /// <inheritdoc cref="RemovalsOnNotification"/>
    internal static long RemovalsWithParent => Interlocked.Read(ref s_removalsWithParent);
    /// <inheritdoc cref="RemovalsOnNotification"/>
    internal static long RemovalsWithoutNotification => Interlocked.Read(ref s_removalsWithoutNotification);
    /// <inheritdoc cref="RemovalsOnNotification"/>
    internal static long RemovalsNotGone => Interlocked.Read(ref s_removalsNotGone);
    /// <inheritdoc cref="RemovalsOnNotification"/>
    internal static long RemovalsUnregistered => Interlocked.Read(ref s_removalsUnregistered);

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
        IntPtr registration = CM_Register_Notification(ref filter, context, &NotifyDispatch, out IntPtr h) == CR_SUCCESS ? h : IntPtr.Zero;
        AllInstancesRegistrationTap?.Invoke(registration);
        return registration;
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
    public static bool WaitForDeviceInterface(string instanceId, Guid interfaceGuid, int timeoutMs = 10000) =>
        WaitForDeviceInterface(instanceId, interfaceGuid, timeoutMs, null);

    /// <summary>The same wait, which also ends when <paramref name="failed"/>,
    /// re-checked every 25 ms, says the device's start failed. False then,
    /// as on a timeout.</summary>
    public static bool WaitForDeviceInterface(string instanceId, Guid interfaceGuid, int timeoutMs, Func<bool>? failed)
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
        // A failed start publishes no interface, so with a failure test the
        // wait re-checks every 25 ms, the orchestrator's poll, and ends there.
        bool present = false;
        WithNotification(cb => RegisterInterfaceEvents(interfaceGuid, cb), callback,
            () => WaitUntil(() => (present = Volatile.Read(ref arrived) || HasInterface(instanceId, interfaceGuid)) || failed?.Invoke() == true,
                            signal, timeoutMs, failed != null ? 25 : RecheckMs));
        return present;
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
    public static bool WaitForHidChild(string parentInstanceId, int timeoutMs = 5000) =>
        WaitForHidChild(parentInstanceId, timeoutMs, null);

    /// <summary>The same wait, which also ends when <paramref name="failed"/>,
    /// re-checked every 25 ms, says the device's start failed.</summary>
    public static bool WaitForHidChild(string parentInstanceId, int timeoutMs, Func<bool>? failed)
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
        // A failed start enumerates nothing, so with a failure test the wait
        // re-checks every 25 ms, the orchestrator's poll, and ends there.
        bool child = false;
        WithNotification(RegisterAllInstanceEvents, callback,
            () => WaitUntil(() => (child = HasChild(parentInstanceId)) || failed?.Invoke() == true, signal, timeoutMs,
                            failed != null ? 25 : RecheckMs));
        return child;
    }

    /// <summary>
    /// Waits for a device instance to leave the device tree by the removal
    /// test (<see cref="RemovalComplete"/>): for a devnode present at the
    /// call, until its DEVICEINSTANCEREMOVED notification, or the
    /// notification's own bound once the devnode has left. Its records are
    /// not waited for. Returns true when gone, false on timeout.
    /// </summary>
    public static bool WaitForDeviceRemoval(string instanceId, int timeoutMs = 5000) =>
        WaitRemovalComplete(instanceId, uninstalls: false, TimeoutScale.Apply(timeoutMs));

    // ── The removal test and the registration gate ──

    const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;

    /// <summary>Stand-ins for the PnP calls the removal test and the
    /// registration gate make, in the hook style of
    /// <see cref="DeviceOrchestrator.BeforeInputMapping"/>. <see cref="LocateHook"/>
    /// answers CM_Locate_DevNodeW (instance id and flags to a CONFIGRET),
    /// <see cref="RemovedHook"/> registers for an instance's
    /// DEVICEINSTANCEREMOVED notification (the instance id and the action to
    /// run when it is delivered, to a registration to dispose, or null when
    /// it is refused), and <see cref="InstallQueueHook"/> answers
    /// CMP_WaitNoPendingInstallEvents (a timeout to a WAIT_ code).
    /// pnp_notify_check's offline part sets them to feed a recorded sequence.
    /// Null outside that test.</summary>
    internal static Func<string, uint, uint>? LocateHook;
    /// <inheritdoc cref="LocateHook"/>
    internal static Func<string, Action, IDisposable?>? RemovedHook;
    /// <inheritdoc cref="LocateHook"/>
    internal static Func<uint, uint>? InstallQueueHook;
    /// <summary>Stands in for the machine-wide PnP lock
    /// (<see cref="AcquirePnpLock"/>) behind the in-process gate: taking it
    /// returns what releases it. Set by the probes that run the SDK's PnP
    /// paths with no device, null outside them.</summary>
    internal static Func<IDisposable>? PnpLockHook;
    /// <summary>Stands in for a bounded wait for the machine-wide PnP lock
    /// behind the in-process gate, ahead of <see cref="PnpLockHook"/>: the
    /// timeout in ms to what releases it, or null when it was not free in
    /// time. pnp_notify_check's offline part sets it with
    /// <see cref="PnpLockHook"/>. Null outside it.</summary>
    internal static Func<int, IDisposable?>? PnpLockTryHook;
    /// <summary>Stands in for the PnP work RemoveDeviceLocked does itself,
    /// beside <see cref="LocateHook"/> and <see cref="RemovedHook"/>: the
    /// children of a devnode, each removal call, a software device's close
    /// and the pnputil and devcon fallbacks, which never start while it is
    /// set. With it set, RemoveDeviceLocked locates through
    /// <see cref="LocateHook"/>, so pnp_notify_check's offline part runs the
    /// removal's own order on recorded sequences. Null outside it.</summary>
    internal static RemovalCalls? RemovalCallsHook;

    /// <summary>The calls <see cref="RemovalCallsHook"/> answers, each by
    /// instance id.</summary>
    internal sealed class RemovalCalls
    {
        /// <summary>A devnode's children, as GetAllChildDeviceIds lists
        /// them.</summary>
        public Func<string, IReadOnlyList<string>> Children = _ => Array.Empty<string>();
        /// <summary>SetupDiRemoveDevice (DifRemoveDevice).</summary>
        public Func<string, bool> Remove = _ => true;
        /// <summary>CM_Disable_DevNode and CM_Uninstall_DevNode
        /// (DisableAndUninstall).</summary>
        public Func<string, bool> Uninstall = _ => true;
        /// <summary>A software device's close (SwdDeviceFactory.Remove),
        /// as its HRESULT.</summary>
        public Func<string, int> Close = _ => 0;
        /// <summary>The pnputil and devcon runs.</summary>
        public Action<string> Fallbacks = _ => { };
    }

    static uint Locate(string instanceId, uint flags)
    {
        if (LocateHook is { } hook) return hook(instanceId, flags);
        if (RemovalLocate is { } phantoms)
        {
            // The seam stands for a phantom: never in the tree, its record
            // known while the seam says so. A seam that throws counts as
            // finding the id.
            if (flags == CM_LOCATE_DEVNODE_NORMAL) return CR_NO_SUCH_DEVNODE;
            bool found;
            try { found = phantoms(instanceId); }
            catch { found = true; }
            return found ? CR_SUCCESS : CR_NO_SUCH_DEVNODE;
        }
        return CM_Locate_DevNodeW(out _, instanceId, flags);
    }

    const uint CR_NO_SUCH_DEVNODE = 0x0000000D;

    /// <summary>What a removal wait has seen of one instance id.</summary>
    internal readonly struct RemovalObservation
    {
        /// <summary>The devnode was in the device tree when the wait had
        /// registered for its REMOVED notification.</summary>
        public readonly bool PresentAtStart;
        /// <summary>Windows accepted that registration.</summary>
        public readonly bool Registered;
        /// <summary>The DEVICEINSTANCEREMOVED notification has arrived.</summary>
        public readonly bool RemovedDelivered;
        /// <summary>A CM_LOCATE_DEVNODE_NORMAL locate finds it.</summary>
        public readonly bool InTree;
        /// <summary>A CM_LOCATE_DEVNODE_PHANTOM locate finds it: in the tree,
        /// or a nonpresent device whose records remain.</summary>
        public readonly bool Record;

        public RemovalObservation(bool presentAtStart, bool registered, bool removedDelivered, bool inTree, bool record)
        {
            PresentAtStart = presentAtStart;
            Registered = registered;
            RemovedDelivered = removedDelivered;
            InTree = inTree;
            Record = record;
        }
    }

    /// <summary>What a removal test found.</summary>
    internal enum RemovalOutcome
    {
        /// <summary>Not decided yet: the wait goes on.</summary>
        Pending,
        /// <summary>Gone, and the REMOVED notification arrived or had nothing
        /// to prove.</summary>
        Removed,
        /// <summary>Gone from the tree, and from the registry for an
        /// uninstall, but the REMOVED notification did not arrive within
        /// <see cref="RemovedBoundMs"/>.</summary>
        GoneWithoutRemoved,
        /// <summary>The devnode, or a record an uninstall should have
        /// deleted, was still there when the budget ended.</summary>
        NotGone,
    }

    /// <summary>How long a removal test waits for the REMOVED notification
    /// once the devnode has left the tree and, for an uninstall, its record
    /// is gone. The same 2 s RemoveDevice has given a child's removal since
    /// before v2.0.0. In the bench's diag log of 2026-10-14 and 10-15 the
    /// wait after a software device's close ended within 471 ms of the start
    /// of the close in 688 of 688 removals, the hmswd.exe run included. It
    /// applies to a devnode removed directly. A child removed while a ROOT
    /// or HID parent's watch is open is not given it
    /// (<see cref="SubtreeChildren"/>): in the Windows Sandbox runs of
    /// 2026-10-16 no such HID child's notification came within the bound,
    /// and the hypothesis is that its bus driver keeps its PDO until the
    /// parent goes (<see cref="SubtreeChildComplete"/>). A software parent's
    /// survivor, in the tree at its call and not proven by the parent's
    /// notification, is held for it with no registration once it is
    /// gone.</summary>
    internal const int RemovedBoundMs = 2000;

    /// <summary>How long a removal whose call finishes its work before it
    /// returns gets to show it: a child, a fast removal, an orphan, a
    /// phantom record, a fallback. SetupDiRemoveDevice deletes the registry
    /// state before it returns, and the setupapi logs of 2026-10-14 put
    /// Windows' device-deleted event (Kernel-PnP 420) inside setupapi's own
    /// section for that removal. Also 2 s, the child budget of old.</summary>
    internal const int QuickRemovalMs = 2000;

    /// <summary>The outcome of a removal by what <paramref name="o"/> has
    /// seen. Each signal decides only what it proves.
    ///
    /// <para>A failed CM_LOCATE_DEVNODE_NORMAL locate proves the device is
    /// not "currently configured in the device tree" (CM_Locate_DevNodeW on
    /// Microsoft Learn), and a failed CM_LOCATE_DEVNODE_PHANTOM locate that
    /// it is not a "nonpresent device" either: its records are gone, which an
    /// uninstall must achieve (SetupDiRemoveDevice deletes "all traces of the
    /// device instance" from the registry, CM_Uninstall_DevNode "removes all
    /// persistent state associated with a device instance"). Until both
    /// hold, as the removal requires, the removal is not done, and at the end
    /// of its budget it is NotGone. A removal that keeps the record, as
    /// SwDeviceClose does, needs only the first.</para>
    ///
    /// <para>DEVICEINSTANCEREMOVED is "sent when a device instance that meets
    /// your filter criteria is no longer present" (CM_NOTIFY_ACTION). It is
    /// the signal that Windows has delivered the devnode's removal, and it is
    /// waited for when the devnode was in the tree when the watch registered:
    /// until v2.0.0 a failed NORMAL locate stood in for it, and on the bench
    /// it ended 2343 of 2343 waits for a ROOT parent at 0 ms. A devnode that
    /// is gone but whose notification has not come within
    /// <see cref="RemovedBoundMs"/> is GoneWithoutRemoved: gone by every
    /// signal that can be checked, and counted and logged, never refused. It
    /// decides nothing for a devnode already out of the tree at the start,
    /// or when Windows refused the registration.</para>
    ///
    /// <para>CM_LOCATE_DEVNODE_CANCELREMOVE is never used: "If the device is
    /// in the process of being removed, the function cancels the removal of
    /// the device."</para></summary>
    internal static RemovalOutcome RemovalComplete(in RemovalObservation o, bool uninstalls, bool removedOverdue)
    {
        if (o.InTree || (uninstalls && o.Record)) return RemovalOutcome.Pending;
        if (o.RemovedDelivered || !o.PresentAtStart || !o.Registered) return RemovalOutcome.Removed;
        return removedOverdue ? RemovalOutcome.GoneWithoutRemoved : RemovalOutcome.Pending;
    }

    /// <summary>The outcome of the removal of a child removed while its
    /// parent's watch was open, decided once the parent's own removal test
    /// has ended (<see cref="SubtreeChildren"/>).
    /// <paramref name="parentRemoved"/>: the parent's DEVICEINSTANCEREMOVED
    /// notification arrived after the child's removal call began. The child
    /// has no registration of its own, so its own notification is not
    /// watched: the parent's watch is the one registration open.
    ///
    /// <para>The orderly case. A bus driver given IRP_MN_REMOVE_DEVICE for
    /// a child it still reports "Retains the PDO for the device until the
    /// device has been physically removed" ("Removing a Device in a Bus
    /// Driver"), and DEVICEINSTANCEREMOVED is "sent when a device instance
    /// ... is no longer present" (CM_NOTIFY_ACTION). When the parent goes,
    /// "If the bus driver handled a previous IRP_MN_REMOVE_DEVICE request
    /// for the device, and there has been no subsequent
    /// IRP_MN_SURPRISE_REMOVAL request, then the bus driver deletes the
    /// child PDO. In this case, the PnP manager ensures that any function
    /// and filter drivers have been removed from the child device ...
    /// before it sends a remove IRP to the parent bus device. The child PDO
    /// might still be present, so the bus driver must delete the child PDO
    /// before it removes the bus device." ("Removing a Device in a Function
    /// Driver", step 1). The parent's notification says the parent is no
    /// longer present, so it comes after that remove IRP, inside which the
    /// child's PDO was deleted. That last step is inference: no page orders
    /// DEVICEINSTANCEREMOVED against the remove IRP, and the I/O system
    /// frees a deleted PDO only when its reference count reaches zero.</para>
    ///
    /// <para>The exception, on the same page: "If the bus driver handled a
    /// previous IRP_MN_SURPRISE_REMOVAL request for the child device, but
    /// the driver has not yet received the subsequent IRP_MN_REMOVE_DEVICE
    /// request, the bus driver leaves the child PDO intact. At some later
    /// time, when all handles to the child device are closed, the PnP
    /// manager will send the remove IRP for the child device". A HID child
    /// surprise removed with a handle still open can keep its PDO past the
    /// parent's notification, and this test counts it removed with its
    /// parent once the locates no longer find it. No signal the SDK has
    /// shows that case, as none did before this test.</para>
    ///
    /// <para>Without the parent's notification the child is gone without
    /// one, at once, since the parent's test has already had its bound. The
    /// locates decide as in <see cref="RemovalComplete"/>: in the tree, or
    /// its record still there after an uninstall, is pending, and the
    /// caller's budget ends it as not gone. A child out of the tree at its
    /// call has no removal the parent's notification could prove, and its
    /// record decides.</para></summary>
    internal static RemovalOutcome SubtreeChildComplete(in RemovalObservation o, bool uninstalls, bool parentRemoved)
    {
        if (o.InTree || (uninstalls && o.Record)) return RemovalOutcome.Pending;
        if (!o.PresentAtStart || parentRemoved) return RemovalOutcome.Removed;
        return RemovalOutcome.GoneWithoutRemoved;
    }

    /// <summary>How the parent of a subtree child ended, for the child's
    /// test and its diag line (<see cref="SubtreeChildren.Settle"/>).</summary>
    internal enum ParentEnd
    {
        /// <summary>Its REMOVED notification arrived after the child's call
        /// began: the proof the child's test takes.</summary>
        RemovedAfterCall,
        /// <summary>Its last REMOVED notification arrived before the child's
        /// call began, so it says nothing of this child.</summary>
        RemovedBeforeCall,
        /// <summary>It left with no REMOVED notification.</summary>
        GoneWithoutRemoved,
        /// <summary>It was still there when its test ended.</summary>
        NotGone,
    }

    /// <summary>One instance's removal test. For a devnode in the tree it
    /// registers for the instance's DEVICEINSTANCEREMOVED notification and
    /// then looks at the devnode again, so a removal that ends between the
    /// two still reaches the callback. A devnode out of the tree has no
    /// removal a notification could prove, and gets no registration: Windows
    /// refuses one for an id with no devnode anyway (CR_FAILURE, measured
    /// 2026-10-15), and every registration that works is one more listener
    /// during a removal.</summary>
    internal sealed class RemovalWatch : IDisposable
    {
        private readonly string _id;
        private readonly AutoResetEvent _signal = new(false);
        private GCHandle _keep;
        private IntPtr _registration;
        private IDisposable? _hooked;
        private int _removed;
        // When the latest REMOVED notification arrived, in Stopwatch ticks,
        // 0 before. A child's test compares its parent's with the moment
        // the child's removal call began (SubtreeChildren).
        private long _removedTicks;
        // A child of a subtree: no registration of its own, and a test run
        // after its parent's (WaitAfterParent). The diag line names how it
        // ended, with how its parent ended.
        private bool _subtreeChild;
        private ParentEnd _parentEnd;
        // How long a software parent's survivor was held once gone, -1 when
        // it was not (SubtreeChildren.Settle).
        private long _heldMs = -1;
        // The last observation a wait made, for the diag line of a removal
        // that ended not gone.
        private RemovalObservation _lastSeen;
        private bool _disposed;
        // When the devnode was first seen gone (and, for an uninstall, its
        // record), in Stopwatch ticks. The REMOVED bound runs from there, so
        // a second wait on a watch whose bound has passed ends at once.
        private long _goneAt;
        private RemovalOutcome _last = RemovalOutcome.Pending;

        internal bool PresentAtStart { get; private set; }
        internal bool Registered { get; }

        private RemovalWatch(string instanceId, bool register)
        {
            _id = instanceId;
            if (!register) return;
            if (RemovedHook is { } hook)
            {
                _hooked = hook(instanceId, OnRemoved);
                Registered = _hooked != null;
                return;
            }
            CM_NOTIFY_CALLBACK callback = (hNotify, context, action, data, size) =>
            {
                if (action == CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED) OnRemoved();
                return 0u;
            };
            _keep = GCHandle.Alloc(callback);
            _registration = RegisterInstanceEvents(instanceId, GCHandle.ToIntPtr(_keep));
            Registered = _registration != IntPtr.Zero;
        }

        /// <summary>For a devnode in the device tree, register for its
        /// REMOVED notification, then note whether it is still there.</summary>
        internal static RemovalWatch Start(string instanceId)
        {
            bool inTree = Locate(instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS;
            var watch = new RemovalWatch(instanceId, register: inTree);
            try { watch.PresentAtStart = inTree && Locate(instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS; }
            catch
            {
                watch.Dispose();
                throw;
            }
            return watch;
        }

        /// <summary>A subtree child's watch: no REMOVED registration, and
        /// whether the child is in the tree as its removal call is about to
        /// begin. Its parent's watch is the one registration open
        /// (SubtreeChildren).</summary>
        internal static RemovalWatch StartUnregistered(string instanceId)
        {
            var watch = new RemovalWatch(instanceId, register: false) { _subtreeChild = true };
            try { watch.PresentAtStart = Locate(instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS; }
            catch
            {
                watch.Dispose();
                throw;
            }
            return watch;
        }

        /// <summary>When the latest REMOVED notification arrived, in
        /// Stopwatch ticks, or 0. A devnode that leaves, comes back and leaves
        /// again is notified each time.</summary>
        internal long RemovedTicks => Interlocked.Read(ref _removedTicks);

        /// <summary>Wait up to <paramref name="timeoutMs"/> (scaled) for a
        /// REMOVED notification that arrives at or after
        /// <paramref name="since"/> (Stopwatch ticks), and say whether one
        /// did.</summary>
        internal bool WaitRemovedSince(long since, int timeoutMs)
        {
            long freq = System.Diagnostics.Stopwatch.Frequency;
            long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + Math.Max(0, TimeoutScale.Apply(timeoutMs)) * freq / 1000;
            while (true)
            {
                if (RemovedTicks >= since && RemovedTicks != 0) return true;
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (deadline <= now) return false;
                _signal.WaitOne((int)Math.Clamp((deadline - now) * 1000 / freq + 1, 1, RecheckMs));
            }
        }

        private void OnRemoved()
        {
            Interlocked.Exchange(ref _removedTicks, System.Diagnostics.Stopwatch.GetTimestamp());
            Volatile.Write(ref _removed, 1);
            try { _signal.Set(); }
            catch (ObjectDisposedException) { }
        }

        internal RemovalObservation Observe() => new(
            PresentAtStart, Registered, Volatile.Read(ref _removed) != 0,
            inTree: Locate(_id, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS,
            record: Locate(_id, CM_LOCATE_DEVNODE_PHANTOM) == CR_SUCCESS);

        /// <summary>Waits for an outcome of <see cref="RemovalComplete"/>,
        /// checking at once, on the notification, and every
        /// <see cref="RecheckMs"/>. The devnode, and the record for an
        /// uninstall, get <paramref name="timeoutMs"/> (scaled) to go, and
        /// the notification <paramref name="removedMs"/> (scaled) from the
        /// moment they are gone.</summary>
        internal RemovalOutcome Wait(bool uninstalls, int timeoutMs, int removedMs = RemovedBoundMs) =>
            WaitMs(uninstalls, TimeoutScale.Apply(timeoutMs), TimeoutScale.Apply(removedMs), withinBudget: false);

        /// <summary>As <see cref="Wait"/>, within <paramref name="budgetMs"/>
        /// already scaled, and with the notification's bound inside that
        /// budget: a devnode gone when the budget ends is gone, with or
        /// without its notification.</summary>
        internal RemovalOutcome WaitWithin(bool uninstalls, int budgetMs) =>
            WaitMs(uninstalls, budgetMs, TimeoutScale.Apply(RemovedBoundMs), withinBudget: true);

        private RemovalOutcome WaitMs(bool uninstalls, int timeoutMs, int removedMs, bool withinBudget)
        {
            long freq = System.Diagnostics.Stopwatch.Frequency;
            long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + Math.Max(0, timeoutMs) * freq / 1000;
            long removedTicks = Math.Max(0, removedMs) * freq / 1000;
            while (true)
            {
                var o = _lastSeen = Observe();
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                bool gone = !o.InTree && !(uninstalls && o.Record);
                if (gone && _goneAt == 0) _goneAt = now;
                var r = RemovalComplete(o, uninstalls, removedOverdue: gone && now - _goneAt >= removedTicks);
                if (r != RemovalOutcome.Pending) return _last = r;
                long until = gone ? _goneAt + removedTicks : deadline;
                if (gone && withinBudget) until = Math.Min(until, deadline);
                if (until <= now) return _last = gone ? RemovalOutcome.GoneWithoutRemoved : RemovalOutcome.NotGone;
                _signal.WaitOne((int)Math.Clamp((until - now) * 1000 / freq + 1, 1, RecheckMs));
            }
        }

        /// <summary>The test of a subtree child once its parent's own test
        /// has ended (<see cref="SubtreeChildComplete"/>), proven by the
        /// parent's notification when <paramref name="parentEnd"/> is
        /// <see cref="ParentEnd.RemovedAfterCall"/>. Only a devnode still in
        /// the tree, or a record an uninstall should have deleted, waits, for
        /// <paramref name="budgetMs"/> (scaled), looking every
        /// <see cref="RecheckMs"/>.</summary>
        internal RemovalOutcome WaitAfterParent(bool uninstalls, int budgetMs, ParentEnd parentEnd)
        {
            _parentEnd = parentEnd;
            long freq = System.Diagnostics.Stopwatch.Frequency;
            long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + Math.Max(0, TimeoutScale.Apply(budgetMs)) * freq / 1000;
            while (true)
            {
                var o = _lastSeen = Observe();
                var r = SubtreeChildComplete(o, uninstalls, parentRemoved: parentEnd == ParentEnd.RemovedAfterCall);
                if (r != RemovalOutcome.Pending) return _last = r;
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (deadline <= now) return _last = RemovalOutcome.NotGone;
                _signal.WaitOne((int)Math.Clamp((deadline - now) * 1000 / freq + 1, 1, RecheckMs));
            }
        }

        /// <summary>The diag line of a subtree child's test: how it ended,
        /// with how its parent ended, and how long it was held once gone
        /// when it was (<paramref name="heldMs"/>, negative when not). Null
        /// for a test that has not ended.</summary>
        internal static string? SubtreeChildLine(string id, RemovalOutcome last, bool presentAtStart, ParentEnd parentEnd, bool inTree,
                                                 long heldMs = -1)
        {
            if (last == RemovalOutcome.Pending) return null;
            if (!presentAtStart) return $"      {id} was out of the tree at its removal call, its record decided: {last}";
            string held = heldMs >= 0 ? $", held {heldMs} ms under the lock after it left" : "";
            return last switch
            {
                RemovalOutcome.Removed => $"      {id} left with its parent's subtree, on the parent's REMOVED notification{held}",
                RemovalOutcome.GoneWithoutRemoved => parentEnd switch
                {
                    ParentEnd.RemovedBeforeCall => $"      {id} left the tree, but its parent's REMOVED notification came before its removal call{held}",
                    ParentEnd.NotGone => $"      {id} left the tree, its parent not gone{held}",
                    _ => $"      {id} left the tree, with no REMOVED notification for its parent{held}",
                },
                _ => $"      {id} not gone after its parent's removal: "
                     + (inTree ? "still in the tree" : "out of the tree, its record left"),
            };
        }

        /// <summary>Notes how long a software parent's survivor was held
        /// once gone, for its diag line (<see cref="SubtreeChildren.Settle"/>).</summary>
        internal void Held(long ms) => _heldMs = ms;

        /// <summary>Counts the test, writes to the diag log how a subtree
        /// child's test ended and when any other ended without its
        /// notification, and closes the registration.
        /// CM_Unregister_Notification "waits for pending callbacks to finish"
        /// (Microsoft Learn), so the callback's handle is freed only after
        /// it.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_subtreeChild)
            {
                // No registration: proven by the parent's notification, gone
                // without one, or not gone.
                if (PresentAtStart && _last == RemovalOutcome.Removed) Interlocked.Increment(ref s_removalsWithParent);
                else if (PresentAtStart && _last == RemovalOutcome.GoneWithoutRemoved) Interlocked.Increment(ref s_removalsWithoutNotification);
                else if (PresentAtStart && _last == RemovalOutcome.NotGone) Interlocked.Increment(ref s_removalsNotGone);
                if (SubtreeChildLine(_id, _last, PresentAtStart, _parentEnd, _lastSeen.InTree, _heldMs) is { } line)
                    DeviceOrchestrator.LogDiag(line);
            }
            else if (PresentAtStart && _last != RemovalOutcome.Pending)
            {
                if (!Registered) Interlocked.Increment(ref s_removalsUnregistered);
                else if (_last == RemovalOutcome.Removed) Interlocked.Increment(ref s_removalsOnNotification);
                else if (_last == RemovalOutcome.GoneWithoutRemoved) Interlocked.Increment(ref s_removalsWithoutNotification);
                if (_last == RemovalOutcome.NotGone) Interlocked.Increment(ref s_removalsNotGone);
                if (_last == RemovalOutcome.GoneWithoutRemoved)
                    DeviceOrchestrator.LogDiag($"      {_id} left the tree with no REMOVED notification within its bound");
            }
            if (_registration != IntPtr.Zero) CM_Unregister_Notification(_registration);
            _hooked?.Dispose();
            if (_keep.IsAllocated) _keep.Free();
            _signal.Dispose();
        }
    }

    /// <summary>One devnode's removal in steps (a close, then a disable and
    /// uninstall, then the pnputil and devcon fallbacks), tested on one
    /// watch from the first step's call to the last step's test. The watch
    /// keeps its REMOVED registration between steps, so a notification
    /// delivered between two of them is seen, and the removal counts once,
    /// by how its last step ended, as present when its first step started.
    /// A watch per step missed both: a devnode that left between two steps
    /// got a later watch that was not present at its start, which counts
    /// nothing, and a step that ended not gone counted as the removal's end
    /// although a later step removed the devnode.</summary>
    internal sealed class StepwiseRemoval : IDisposable
    {
        private readonly string _id;
        private RemovalWatch? _watch;

        internal StepwiseRemoval(string instanceId) => _id = instanceId;

        /// <summary>When the devnode's latest REMOVED notification arrived on
        /// the open watch, in Stopwatch ticks, or 0.</summary>
        internal long RemovedTicks => _watch?.RemovedTicks ?? 0;

        /// <summary>A watch is open, registered for the REMOVED notification
        /// of a devnode that was in the tree when it started.</summary>
        internal bool Watching => _watch is { Registered: true, PresentAtStart: true };

        /// <summary>Wait on the open watch for a REMOVED notification at or
        /// after <paramref name="since"/>, up to <paramref name="timeoutMs"/>
        /// (scaled). False with no watch open.</summary>
        internal bool WaitRemovedSince(long since, int timeoutMs) => _watch?.WaitRemovedSince(since, timeoutMs) ?? false;

        /// <summary>Start the watch, unless a step already did, before a
        /// step's call.</summary>
        internal void Begin() => _watch ??= RemovalWatch.Start(_id);

        /// <summary>One step: the watch, then <paramref name="call"/>, then
        /// the removal test within <paramref name="budgetMs"/>, or a single
        /// look when the call failed.</summary>
        internal RemovalOutcome Step(Func<bool> call, bool uninstalls, int budgetMs)
        {
            Begin();
            bool issued = call();
            return Wait(uninstalls, issued ? budgetMs : 0);
        }

        /// <summary>The removal test of the step whose call just ran.</summary>
        internal RemovalOutcome Wait(bool uninstalls, int budgetMs) =>
            (_watch ?? throw new InvalidOperationException("No step has begun.")).Wait(uninstalls, budgetMs);

        /// <summary>End the watch after a step that removed the devnode, so
        /// its registration is not open during work on other devnodes. A
        /// later step starts a watch of its own.</summary>
        internal void Finish()
        {
            _watch?.Dispose();
            _watch = null;
        }

        public void Dispose() => Finish();
    }

    /// <summary>The children of one removal, each removed by its own call.
    /// While the parent's watch is open (<see cref="StepwiseRemoval.Watching"/>:
    /// registered, with the parent in the tree when it started), a child goes
    /// with the parent's subtree: it opens no registration of its own, so
    /// the parent's is the one open, and its test runs after the parent's
    /// (<see cref="Settle"/>), on the parent's REMOVED notification
    /// (<see cref="SubtreeChildComplete"/>). The parent's watch decides both
    /// at once: a child is deferred only while that watch is open, and the
    /// same watch sees the parent leave, whenever and however it leaves.
    /// With no parent watch open, a child is removed directly and tested at
    /// its call on its own registration, with the REMOVED bound, as before:
    /// a child whose parent was out of the tree when its watch started, or
    /// whose parent's registration Windows refused. A software parent's
    /// close keeps its watch open through its survivors' removals, so they
    /// open none while the close's cascade may still be removing them.
    /// When Windows refused the close's registration there is no watch to
    /// keep, and each survivor registers its own at its call while the
    /// cascade may still run, one at a time. A
    /// survivor in the tree at its call that the parent's notification does
    /// not prove is held under the lock for <see cref="RemovedBoundMs"/>
    /// from the moment it is seen gone, with no registration, as long as
    /// a child removed directly waits for its own notification. The
    /// parent's notification ends the hold. One REMOVED registration at
    /// most is open at a time (78e2848). Every test ends before the removal
    /// returns, under the PnP lock the removal holds. Settle discards the
    /// children's outcomes, as the test after each child's call did:
    /// RemoveDevice answers for the parent.</summary>
    internal sealed class SubtreeChildren : IDisposable
    {
        private readonly StepwiseRemoval _parent;
        private readonly bool _holdForCascade;
        private readonly List<(RemovalWatch Watch, long CalledAt, bool Issued)> _deferred = new();

        /// <param name="parent">The parent's removal, whose watch decides
        /// the deferral and gives the proof.</param>
        /// <param name="holdForCascade">A software parent: its close starts a
        /// cascade that runs on its own, and its children are called after
        /// it, so a survivor's own notification would come after its call.
        /// Each survivor in the tree at its call that the parent's
        /// notification has not proven is held, once gone, for up to
        /// <see cref="RemovedBoundMs"/>, as long as a child removed directly
        /// waits for its own notification. The parent's notification
        /// after the survivor's call, on the one registration open, ends the
        /// hold and proves it. A ROOT or HID parent's children are called
        /// before the parent's own removal and get no hold.</param>
        internal SubtreeChildren(StepwiseRemoval parent, bool holdForCascade)
        {
            _parent = parent;
            _holdForCascade = holdForCascade;
        }

        /// <summary>Remove one child: its watch, then <paramref name="call"/>.
        /// Returns its outcome when it was tested at its call, or null when
        /// its test waits for the parent's. A call that throws closes the
        /// child's watch, registration included, and rethrows.</summary>
        internal RemovalOutcome? Remove(string childId, Func<bool> call)
        {
            bool withParent = _parent.Watching;
            var watch = withParent ? RemovalWatch.StartUnregistered(childId) : RemovalWatch.Start(childId);
            long calledAt = System.Diagnostics.Stopwatch.GetTimestamp();
            bool issued;
            try { issued = call(); }
            catch
            {
                watch.Dispose();
                throw;
            }
            if (withParent)
            {
                _deferred.Add((watch, calledAt, issued));
                return null;
            }
            using (watch) return watch.Wait(uninstalls: true, issued ? QuickRemovalMs : 0);
        }

        /// <summary>Test every child that waits for its parent, once the
        /// parent's test has ended with <paramref name="parentOutcome"/>. A
        /// child is proven by the parent's latest REMOVED notification when it
        /// arrived after the child's call began. A child whose call failed
        /// gets one look, as a failed call does everywhere.</summary>
        internal void Settle(RemovalOutcome parentOutcome)
        {
            foreach (var (watch, calledAt, issued) in _deferred)
            {
                ParentEnd End()
                {
                    long ticks = _parent.RemovedTicks;
                    return ticks != 0 && ticks >= calledAt ? ParentEnd.RemovedAfterCall
                         : ticks != 0 ? ParentEnd.RemovedBeforeCall
                         : parentOutcome == RemovalOutcome.NotGone ? ParentEnd.NotGone
                         : ParentEnd.GoneWithoutRemoved;
                }
                using (watch)
                {
                    var outcome = watch.WaitAfterParent(uninstalls: true, issued ? QuickRemovalMs : 0, End());
                    // A software parent's survivor, in the tree at its call
                    // and gone with no proof: held for the bound its own test
                    // once waited on its own notification, which no
                    // registration hears now, then looked at once more.
                    if (_holdForCascade && outcome == RemovalOutcome.GoneWithoutRemoved)
                    {
                        var held = System.Diagnostics.Stopwatch.StartNew();
                        _parent.WaitRemovedSince(calledAt, RemovedBoundMs);
                        watch.Held(held.ElapsedMilliseconds);
                        watch.WaitAfterParent(uninstalls: true, 0, End());
                    }
                }
            }
            _deferred.Clear();
        }

        /// <summary>A removal that ended before <see cref="Settle"/> (a
        /// throw) closes its children's watches untested.</summary>
        public void Dispose()
        {
            foreach (var d in _deferred) d.Watch.Dispose();
            _deferred.Clear();
        }
    }

    /// <summary>What the registration gate decided.</summary>
    internal enum RegistrationGate
    {
        /// <summary>Register now.</summary>
        Allowed,
        /// <summary>Register now. Windows still had device installs pending
        /// when the gate's bounded wait for them ended.</summary>
        AllowedInstallsPending,
        /// <summary>Do not register.</summary>
        Refused,
    }

    /// <summary>How long the gate waits for Windows to have no device
    /// install pending: the bound vJoy's installer gives the same wait before
    /// it installs on a devnode it registered (vJoyInstallLib.cpp, cmdUpdate,
    /// CMP_WaitNoPendingInstallEvents(30000)).</summary>
    internal const int InstallQueueWaitMs = 30_000;

    /// <summary>What of a devnode's prior life the registration gate needs
    /// gone before the id is registered again.</summary>
    internal enum RegistrationKind
    {
        /// <summary>A ROOT devnode (SetupDiCreateDeviceInfoW,
        /// DIF_REGISTERDEVICE): the prior life out of the tree and its record
        /// gone. SetupDiCreateDeviceInfoW refuses an id whose record is still
        /// there.</summary>
        Root,
        /// <summary>A software device (SwDeviceCreate): the prior life out of
        /// the tree. Its record stays: SwDeviceClose keeps it and
        /// SwDeviceCreate takes it over.</summary>
        Software,
    }

    /// <summary>How many times a gate goes round again because something it
    /// had removed was back when it took the lock.</summary>
    const int GateRounds = 3;

    /// <summary>The gate an instance id passes before the SDK registers it.
    /// It returns holding the PnP lock (<see cref="HoldPnpLock"/>), which the
    /// caller releases after the registration, or null to refuse it.
    ///
    /// <para>Outside the lock, everything that takes time: rivals of its
    /// ParentIdPrefix go (<paramref name="clearRivals"/> with true), a prior
    /// life that a NORMAL locate, or for a ROOT id a PHANTOM locate, still
    /// finds goes through <paramref name="removePriorLife"/>, which returns
    /// its removal's outcome or null to refuse, and Windows' install queue
    /// gets up to <paramref name="installWaitMs"/> to empty
    /// (<see cref="AwaitInstallQueue"/>). Each removal takes the lock for
    /// itself and has its own test, which is the one wait for it: NotGone
    /// refuses, as SetupDiCreateDeviceInfoW would refuse a record still
    /// there, and GoneWithoutRemoved goes ahead, as a pending install does
    /// when its wait ends.</para>
    ///
    /// <para>Under the lock nothing is removed and nothing is waited for: a
    /// rival (<paramref name="clearRivals"/> with false) or a prior life back
    /// since sends the gate round again, up to <see cref="GateRounds"/>, and
    /// so does a pending install while the install wait has time left, which
    /// CMP_WaitNoPendingInstallEvents with 0 reports at once ("tests whether
    /// there are pending installation events and returns immediately",
    /// Learn). So a registration holds the lock for its checks and its own
    /// calls only, and another program's removal never waits behind a
    /// removal or an install this one waits for.</para>
    ///
    /// <para>The gate sees a removal of the id by any program through the
    /// id's locates. What it cannot see is a removal Windows has not
    /// finished after the devnode and its record went: a program of this
    /// release holds the lock until its removal test has an outcome, and a
    /// program before v2.0.0 takes no lock.</para></summary>
    internal static PnpLockHold? EnterRegistrationGate(string instanceId, RegistrationKind kind,
        Func<RemovalOutcome?> removePriorLife, Func<bool, bool>? clearRivals,
        out RegistrationGate decision, int installWaitMs = InstallQueueWaitMs)
    {
        long freq = System.Diagnostics.Stopwatch.Frequency;
        long installDeadline = System.Diagnostics.Stopwatch.GetTimestamp() + TimeoutScale.Apply(installWaitMs) * freq / 1000;
        // Rounded up, so the wait never ends a millisecond short of its bound.
        int MsLeft() => (int)Math.Max(0, ((installDeadline - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000 + freq - 1) / freq);

        // Rounds sent back by something that came back. A round sent back by
        // a pending install is bounded by the install wait's time instead.
        int backs = 0;
        while (true)
        {
            decision = RegistrationGate.Refused;
            if (clearRivals != null && !clearRivals(true)) return null;
            if (Locate(instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS
                || (kind == RegistrationKind.Root && Locate(instanceId, CM_LOCATE_DEVNODE_PHANTOM) == CR_SUCCESS))
            {
                var outcome = removePriorLife();
                if (outcome is not { } o) return null;
                if (o == RemovalOutcome.NotGone)
                {
                    DeviceOrchestrator.LogDiag($"      {instanceId} is still there after its removal; refusing to register it");
                    return null;
                }
            }
            var queue = AwaitInstallQueue(instanceId, MsLeft());
            if (queue == RegistrationGate.Refused) return null;

            var hold = HoldPnpLock();
            try
            {
                bool back = (clearRivals != null && !clearRivals(false))
                    || Locate(instanceId, kind == RegistrationKind.Root ? CM_LOCATE_DEVNODE_PHANTOM : CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS;
                if (back)
                {
                    hold.Dispose();
                    if (++backs < GateRounds) continue;
                    DeviceOrchestrator.LogDiag($"      {instanceId} or a rival of its prefix came back {backs} times; refusing to register it");
                    return null;
                }
                uint now = InstallQueue(0);
                if (now == WAIT_TIMEOUT && MsLeft() > 0)
                {
                    hold.Dispose();
                    continue;
                }
                if (now != WAIT_OBJECT_0 && now != WAIT_TIMEOUT)
                {
                    hold.Dispose();
                    DeviceOrchestrator.LogDiag($"      CMP_WaitNoPendingInstallEvents failed (0x{now:X8}); refusing to register {instanceId}");
                    return null;
                }
                if (now == WAIT_TIMEOUT)
                    DeviceOrchestrator.LogDiag($"      device installs still pending after {installWaitMs} ms; registering {instanceId} anyway");
                decision = now == WAIT_OBJECT_0 ? RegistrationGate.Allowed : RegistrationGate.AllowedInstallsPending;
                return hold;
            }
            catch
            {
                hold.Dispose();
                throw;
            }
        }
    }

    static uint InstallQueue(uint timeoutMs) =>
        InstallQueueHook?.Invoke(timeoutMs) ?? CMP_WaitNoPendingInstallEvents(timeoutMs);

    /// <summary>Whether Windows has a device installation pending, any
    /// device's, at this moment: CMP_WaitNoPendingInstallEvents with 0
    /// "tests whether there are pending installation events and returns
    /// immediately" (Learn). A failed wait reads as none pending, so it
    /// never holds back a verdict.</summary>
    internal static bool InstallsPending() => InstallQueue(0) == WAIT_TIMEOUT;

    /// <summary>Waits up to <paramref name="installWaitMs"/> for Windows to
    /// have no device install pending, the registration gate's wait outside
    /// the lock.
    /// CMP_WaitNoPendingInstallEvents "waits until there are no pending
    /// device installation activities for the PnP manager to perform", and
    /// "just indicates that there are no pending installation activities at
    /// the moment it is called" (Learn). An install still pending when the
    /// wait ends lets the registration go ahead, as vJoy's installer goes on
    /// after its WAIT_TIMEOUT: it can belong to any device on the machine,
    /// and refusing would turn another device's slow install into a failed
    /// create. A failed wait refuses it, as vJoy's stops (WAIT_FAILED).</summary>
    internal static RegistrationGate AwaitInstallQueue(string instanceId, int installWaitMs)
    {
        // Already scaled by the caller's deadline.
        bool real = InstallQueueHook == null;
        uint r = InstallQueue((uint)installWaitMs);
        switch (r)
        {
            case WAIT_OBJECT_0:
                return RegistrationGate.Allowed;
            case WAIT_TIMEOUT:
                return RegistrationGate.AllowedInstallsPending;
            default:
                DeviceOrchestrator.LogDiag($"      CMP_WaitNoPendingInstallEvents failed (0x{r:X8}" +
                    (real ? $", Win32 {Marshal.GetLastPInvokeError()}" : "") + $"); refusing to register {instanceId}");
                return RegistrationGate.Refused;
        }
    }

    const uint WAIT_OBJECT_0 = 0x00000000;
    const uint WAIT_TIMEOUT = 0x00000102;

    // cfgmgr32.h: #define CM_WaitNoPendingInstallEvents CMP_WaitNoPendingInstallEvents.
    // The DLL exports only the CMP_ name.
    [LibraryImport("CfgMgr32.dll", EntryPoint = "CMP_WaitNoPendingInstallEvents", SetLastError = true)]
    private static partial uint CMP_WaitNoPendingInstallEvents(uint dwTimeout);

    /// <summary>The name, in <see cref="AdminNamespace"/>, of the
    /// machine-wide lock over PnP registrations and removals.</summary>
    internal const string PnpMutexObject = "Pnp";

    /// <summary>How long a program waits for another to release the PnP
    /// lock, as long as the deploy lock's wait. The longest hold is a
    /// software device's removal: the hmswd.exe remove, up to 90 s to reach
    /// its close and 8 s for the close (SwdDeviceFactory.HelperBoundMs and
    /// HelperCloseMs), its parent's 120 s budget and 2 s for its
    /// notification, and 4 s per child, about 230 s with a few children. A
    /// ROOT or HID removal holds it for its parent's 120 s budget and 2 s
    /// for its notification, 4 s per child, and the pnputil and devcon
    /// fallbacks (up to 7 s each) with 4 s to test them, about 150 s. A
    /// registration whose software device create timed out holds it for the
    /// hmswd.exe create, up to 98 s with its close, and a removal test of up
    /// to 90 s after it, about 190 s. A full deploy holds it for each
    /// pnputil install, up to 240 s (DriverBuilder.PnputilAddMs). All of
    /// these scale alike. None nests another.</summary>
    const int PnpLockWaitMs = 600_000;

    /// <summary>Wait up to <paramref name="timeoutMs"/> for the machine-wide
    /// PnP lock, held by the calling thread until it calls
    /// <c>ReleaseMutex</c>. Null when the wait ran out. Taken only through
    /// <see cref="HoldPnpLock"/> and <see cref="TryHoldPnpLock"/>, behind
    /// this process's in-process gate.
    ///
    /// <para>Held by: each devnode registration the SDK makes, from its
    /// gate's last checks (<see cref="EnterRegistrationGate"/>) through
    /// DIF_REGISTERDEVICE (<see cref="DeviceNodeCreator.CreateDeviceNode"/>)
    /// or SwDeviceCreate (the two software companions), and each removal
    /// and uninstall the SDK makes, from its call through its removal test:
    /// <see cref="RemoveDevice"/> (with its children, its fallbacks and
    /// hmswd.exe's close), each orphan of <see cref="RemoveOrphanHidChildren"/>
    /// and each phantom of <see cref="RemoveAccumulatedHmPhantoms"/>. Also
    /// held by the usbip-win2 host controller's registration (its
    /// DIF_REGISTERDEVICE, and its DIF_REMOVE when its driver install
    /// failed), the host controller stamp's removal, and each step of a full
    /// removal's usbip-win2 part that registers, removes or writes a
    /// devnode (Usbip.UsbipRemoval). Also
    /// held by a full driver deploy's pnputil /add-driver /install, which
    /// reinstalls every present devnode the package matches, other
    /// programs' controllers and their HID children included, by the bus
    /// type stamp of every create (DeviceOrchestrator.StampBusType), which
    /// writes or deletes a property on the parent the create made, and by
    /// the ghost XUSB interface sweep from its presence check through its
    /// disable of a phantom.</para>
    ///
    /// <para>Not held by these, so not ordered against them. pnp_notify_check's
    /// source check lists each of their calls and fails on one made anywhere
    /// else outside the lock. Under the hypothesis this lock rests on (a
    /// removal of a HIDClass devnode overlapping Windows' event delivery for
    /// the same device), those in the first list touch only a controller's
    /// own devnodes, residue, or no HIDClass devnode. Those in the second
    /// are exposure the lock does not cover.</para>
    /// <list type="bullet">
    /// <item>The naming writes on a controller's own devnodes after its
    /// registration's hold (DeviceProperties). Its identity claim keeps
    /// other programs' removals off them.</item>
    /// <item>RestartDevice's CM_Disable_DevNode and CM_Enable_DevNode, on a
    /// controller's own devnodes in every other call the SDK makes. They
    /// take the devnode's children out of the tree and bring them back, and
    /// create or delete no record. RestartDevice is public, and a program's
    /// own call of it takes no lock.</item>
    /// <item>The driver package removal's pnputil /delete-driver /uninstall
    /// /force, under the deploy lock, run once the census shows no device
    /// and no other live program, so what it uninstalls is residue.</item>
    /// <item>The usbip-win2 host controller's repair. Its
    /// CM_Query_And_Remove_SubTreeW on the vhci root runs only when no host
    /// controller answers and starting it failed, when no import has a
    /// subtree, before CM_Setup_DevNode and CM_Reenumerate_DevNode bring it
    /// back.</item>
    /// <item>The host controller's hardware id stamp and a WinUSB restore's
    /// CONFIGFLAG_REINSTALL, written on a devnode that has left. Neither is
    /// a HIDClass devnode.</item>
    /// </list>
    /// <list type="bullet">
    /// <item>A USB/IP import's attach and detach, the vhci plugin and
    /// plugout requests. Plug and Play enumerates and removes the import's
    /// HIDClass children after each.</item>
    /// <item>The transport driver install and update: hmswd.exe
    /// force-driver on the host controller, under the transport lock. On a
    /// transport swap it restarts every import's subtree. On an install it
    /// runs on the devnode just registered, which has no children.</item>
    /// <item>A WinUSB bind, DiInstallDevice on the device, which takes its
    /// HID collections away, after the null driver where its driver list
    /// cannot carry the package (Windows 10). Only a persona that declares a
    /// WinUSB binding makes one.</item>
    /// <item>A registration's DiInstallDevice, which runs once its hold has
    /// ended, on the controller's own devnode: the driver store's search, or
    /// the driver the search chose before for the same ids
    /// (DeviceNodeCreator.InstallFromStoreInf). Its HID child is linked into
    /// the HIDClass list by Plug and Play after the hold, with no lock: the
    /// lock covers the parent's registration, not its children's.</item>
    /// <item>Any program built on HIDMaestro before v2.0.0.</item>
    /// </list>
    ///
    /// <para>Direct registry writes to devnode keys hold the lock where they
    /// decide on a devnode they do not own: the interface-class cleanup
    /// (DeviceOrchestrator.RemoveOwnedInterfaceEntries) and the ghost XUSB
    /// sweep. The rest are on a controller's own devnode: the orphan
    /// instance key a ROOT registration deletes and the ParentIdPrefix it
    /// writes, both inside its registration's hold, a gamepad companion's
    /// ParentIdPrefix after its registration, the ControllerIndex value in
    /// Device Parameters and the HID parent's UpperFilters. The Everything
    /// cleanup deletes every HIDMaestro instance's Device Parameters after
    /// its removals, since it removes every program's devices by
    /// design.</para>
    ///
    /// <para>One hold is one removal or one registration, never both: a
    /// registration's gate removes what it must, each removal in a hold of
    /// its own, before it takes the lock. Inside a software device's
    /// registration, a create attempt that timed out and closed the device
    /// again is waited for by the removal test before the retry, and after
    /// the last attempt before the hold ends. A thread
    /// that holds it can take it again, which none does today. An abandoned
    /// lock is taken: the gate and the removal test look at the devnode
    /// again whatever the last holder left.</para>
    ///
    /// <para>Threads of this process take it through an in-process gate
    /// first (<see cref="HoldPnpLock"/>), one at a time, so one thread at a
    /// time waits for another program's hold, and a bound on the waits
    /// counts only that wait, never this process's own holds. A wait behind
    /// this process's own hold has no bound, except once the process is
    /// exiting, when a wait behind one hold is bounded by the full size of
    /// the take's bound (<see cref="TakePnpLock"/>).</para>
    ///
    /// <para>Lock order: the deploy lock, then this one, then the staging
    /// lock. A full deploy takes this lock for its pnputil install with the
    /// deploy lock held. Under this lock the SDK stages hmswd.exe
    /// (SwdDeviceFactory), which takes the staging lock, and tries identity
    /// claims, which never wait, and takes no other machine-wide lock. The
    /// transport lock comes before it: the host controller's registration
    /// and its rollback take this lock with the transport lock held, and no
    /// holder of this lock waits for the transport lock. The WinUSB devnode
    /// and follow-up locks and the registry ledger's lock are never held
    /// with it: none of their holders registers, removes or reinstalls a
    /// devnode through the SDK. In
    /// process, <see cref="OwnerSweep.CreateGate"/> and a controller's
    /// teardown gate come before it.</para></summary>
    /// <exception cref="UnauthorizedAccessException">The process is not
    /// elevated.</exception>
    internal static Mutex? AcquirePnpLock(int timeoutMs)
    {
        var m = new Mutex(false, AdminNamespace.Name(PnpMutexObject));
        try
        {
            if (!m.WaitOne(Math.Max(0, timeoutMs)))
            {
                m.Dispose();
                return null;
            }
        }
        catch (AbandonedMutexException) { }
        catch
        {
            m.Dispose();
            throw;
        }
        return m;
    }

    /// <summary>A hold of the PnP lock: the in-process gate and the
    /// machine-wide lock. Disposing it releases both, once, so a caller can
    /// let it go early and leave the end of the scope to a no-op. Disposed
    /// on the thread that took it, as the mutex requires: on another thread
    /// the gate's Monitor.Exit throws and the gate stays held for good. No
    /// SDK path does that.</summary>
    internal sealed class PnpLockHold : IDisposable
    {
        private IDisposable? _machine;
        internal PnpLockHold(IDisposable machine) => _machine = machine;

        public void Dispose()
        {
            if (_machine is not { } machine) return;
            _machine = null;
            try { machine.Dispose(); }
            finally { Monitor.Exit(s_pnpGate); }
        }
    }

    /// <summary>The machine-wide lock's mutex as a scope.</summary>
    private sealed class MutexHold : IDisposable
    {
        private readonly Mutex _mutex;
        internal MutexHold(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    /// <summary>The in-process gate in front of the machine-wide lock.
    /// Threads of this process queue on it, and only its holder waits for
    /// the machine-wide lock. Re-entrant, as the lock is. A wait here behind
    /// this process's own hold counts against no bound: the holder's work
    /// under the lock ends within its own removal or registration
    /// bounds.</summary>
    private static readonly object s_pnpGate = new();

    /// <summary>True when the calling thread holds the PnP lock's in-process
    /// gate, so a take of the lock from another thread would wait for
    /// it.</summary>
    internal static bool PnpGateHeldHere => Monitor.IsEntered(s_pnpGate);

    /// <summary>How often a thread queued on the gate looks at its bound.</summary>
    private const int GateSliceMs = 10;

    /// <summary>How many times a thread has entered the gate, so a thread
    /// queued behind it can tell one hold from the next.</summary>
    private static long s_gateHolds;

    private static readonly object s_otherWaitLock = new();
    private static long s_otherWaitTicks;
    private static long s_otherWaitSince;

    /// <summary>The process's clock of waits for another program: the ticks
    /// the gate's holder has spent waiting for the machine-wide lock, the
    /// wait in progress included. One thread waits for it at a time, so the
    /// clock runs no faster than the wall clock however many threads
    /// queue.</summary>
    internal static long OtherProgramWaitTicks() => OtherProgramWaitTicks(out _);

    /// <inheritdoc cref="OtherProgramWaitTicks()"/>
    /// <param name="waitingTicks">How long the wait in progress has lasted,
    /// 0 when none is.</param>
    private static long OtherProgramWaitTicks(out long waitingTicks)
    {
        lock (s_otherWaitLock)
        {
            waitingTicks = s_otherWaitSince != 0 ? System.Diagnostics.Stopwatch.GetTimestamp() - s_otherWaitSince : 0;
            return s_otherWaitTicks + waitingTicks;
        }
    }

    /// <summary>The machine-wide lock, or a probe's stand-in, waited for up
    /// to <paramref name="timeoutMs"/> on the process's clock of waits for
    /// another program. Called by the gate's holder only.</summary>
    private static IDisposable? WaitMachineLock(int timeoutMs)
    {
        lock (s_otherWaitLock) s_otherWaitSince = Math.Max(1, System.Diagnostics.Stopwatch.GetTimestamp());
        try
        {
            if (PnpLockTryHook is { } tryHook) return tryHook(timeoutMs);
            if (PnpLockHook is { } hook) return hook();
            return AcquirePnpLock(timeoutMs) is { } m ? new MutexHold(m) : null;
        }
        finally
        {
            lock (s_otherWaitLock)
            {
                s_otherWaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - s_otherWaitSince;
                s_otherWaitSince = 0;
            }
        }
    }

    /// <summary>Take the gate, then the machine-wide lock.
    /// <paramref name="leftTicks"/> gives what is left of the caller's bound
    /// once this take has seen so many ticks of the process waiting for
    /// another program, and <paramref name="boundTicks"/> is that bound's
    /// full size. The take waits behind this process's own holds as long as
    /// they last outside the exit, and gives up once waiting for another
    /// program has used up what is left: at the gate, while the holder in
    /// front waits for one, or at the machine-wide lock. A take whose bound
    /// is spent still gets the lock when it is free at its turn, during the
    /// exit too as long as no hold in front of it outlasts the full bound.
    /// <paramref name="seenTicks"/> is that waiting. Null when it gave
    /// up.
    ///
    /// <para>Once the process is exiting (<see cref="ProcessExitSignal"/>),
    /// a wait behind one hold of this process's own is bounded too, by the
    /// bound's full size, and not charged to it: a removal or a registration
    /// holds the lock through synchronous SetupAPI and CfgMgr calls
    /// (DIF_REMOVE, SetupDiRemoveDevice, CM_Uninstall_DevNode,
    /// DIF_REGISTERDEVICE, CM_Disable_DevNode) that take no timeout, so no
    /// bound of the removal itself caps the hold, and one stuck call would
    /// otherwise keep the exit waiting for as long as it lasts. The wait is
    /// measured from the hold in front, less the part that holder spends
    /// waiting for another program, which the bound counts already, so a
    /// queue of this process's removals, each shorter than the full bound,
    /// is waited out whole, whatever is left of the bound. A single hold of
    /// this process's own that outlasts the full bound refuses the take
    /// queued behind it, whether stuck or a removal that slow: with the
    /// exit's 10 s, an xinputhid parent's removal, whose filter unload can
    /// take that long (DeviceOrchestrator.TeardownController). A take this
    /// bound refuses counts as refused (<see cref="HoldPnpLock"/>), so the
    /// record keeps its controller for the next sweep. Outside the exit, a
    /// wait behind this process's own hold has no bound.</para></summary>
    private static PnpLockHold? TakePnpLock(Func<long, long> leftTicks, long boundTicks, out long seenTicks)
    {
        long freq = System.Diagnostics.Stopwatch.Frequency;
        long from = OtherProgramWaitTicks();
        long holdSeen = Volatile.Read(ref s_gateHolds);
        long behindSince = System.Diagnostics.Stopwatch.GetTimestamp(), seenAtBehind = 0;
        bool gated = false;
        try
        {
            while (!(gated = Monitor.TryEnter(s_pnpGate, GateSliceMs)))
            {
                // Only while the holder in front waits for another program:
                // a wait that has lasted a millisecond is a hold, not a take
                // of a free lock. Its own work is waited out. A holder that
                // is descheduled for over a millisecond between starting its
                // wait and WaitOne returning on a free lock reads as another
                // program's hold here: a queued take whose bound is spent and
                // that polls in that moment gives up although the lock comes
                // free at once, and the clock counts that time as another
                // program's. Small, and only near a spent bound.
                long seen = OtherProgramWaitTicks(out long waiting) - from;
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                long hold = Volatile.Read(ref s_gateHolds);
                if (hold != holdSeen)
                {
                    holdSeen = hold;
                    behindSince = now;
                    seenAtBehind = seen;
                }
                // The wait behind this hold, less the part the holder spent
                // waiting for another program, which the clock counts.
                long ownBehind = now - behindSince - (seen - seenAtBehind);
                if ((waiting >= freq / 1000 && leftTicks(seen) <= 0)
                    || (ProcessExitSignal.Raised && ownBehind >= boundTicks))
                {
                    seenTicks = seen;
                    return null;
                }
            }
            Interlocked.Increment(ref s_gateHolds);
            long left = Math.Max(0, leftTicks(OtherProgramWaitTicks() - from));
            // Rounded up, so the wait never ends a millisecond short of its bound.
            var machine = WaitMachineLock((int)Math.Min(int.MaxValue, (left * 1000 + freq - 1) / freq));
            seenTicks = OtherProgramWaitTicks() - from;
            if (machine == null) return null;
            gated = false;   // released with the hold
            return new PnpLockHold(machine);
        }
        finally
        {
            if (gated) Monitor.Exit(s_pnpGate);
        }
    }

    /// <summary>Take the PnP lock as a scope (<see cref="PnpLockHold"/>).
    /// Waits for another program's hold within the bound of this thread's
    /// scope (<see cref="BoundPnpLockWaits"/>), or once the process is
    /// exiting within the exit's budget, and throws
    /// <see cref="PnpLockBusyException"/> when it runs out. Otherwise waits
    /// for another program for up to ten minutes, then throws the same.
    /// Waits behind this process's own holds are never counted. Once the
    /// process is exiting a wait behind one such hold is bounded by the
    /// bound's full size (<see cref="TakePnpLock"/>), and outside the exit
    /// it has no bound.</summary>
    internal static PnpLockHold HoldPnpLock()
    {
        if ((t_lockBudget ?? ExitLockBudget()) is { } budget)
        {
            var hold = TakePnpLock(budget.LeftTicks, budget.Ticks, out long seen);
            budget.Charge(seen);
            if (hold != null) return hold;
            t_lockTakesRefused++;
            throw new PnpLockBusyException(
                "HIDMaestro's machine-wide PnP lock was held past this exit's bound on its waits for it.");
        }
        // A take refused here is not counted (t_lockTakesRefused), so a
        // dispose this ten-minute timeout cuts short gives its lease back,
        // and its devices stay present without the record entry that named
        // them.
        long wait = (long)TimeoutScale.Apply(PnpLockWaitMs) * System.Diagnostics.Stopwatch.Frequency / 1000;
        return TakePnpLock(seen => wait - seen, wait, out _)
               ?? throw new PnpLockBusyException(
                   "Another program held HIDMaestro's machine-wide PnP lock for ten minutes.");
    }

    /// <summary>A take of the PnP lock found another program holding it past
    /// the take's bound: the thread's scope (<see cref="BoundPnpLockWaits"/>),
    /// the process's exit, or ten minutes. An InvalidOperationException, as
    /// the ten-minute timeout was before these bounds, so a sweep that
    /// catches a device's failure and goes on can tell a lock that does not
    /// come, which stops it, from that one device's failure.</summary>
    internal sealed class PnpLockBusyException : InvalidOperationException
    {
        internal PnpLockBusyException(string message) : base(message) { }
    }

    /// <summary>A bound on the time spent waiting for another program's hold
    /// of the PnP lock. Only those waits are charged, never the work done
    /// while the lock is held nor a wait behind this process's own hold, so a
    /// long removal of this process's own leaves the next take its share. A
    /// thread's budget is charged with what each of its takes saw. The
    /// exit's budget, which every thread shares, is charged by the process's
    /// clock of such waits since it was made, so threads that wait together
    /// spend it once.</summary>
    internal sealed class LockWaitBudget
    {
        private readonly long _ticks;
        private readonly long _sharedFrom;
        private long _spentTicks;

        /// <summary>The bound's full size.</summary>
        internal long Ticks => _ticks;

        internal LockWaitBudget(int budgetMs, bool shared = false)
        {
            _ticks = (long)Math.Max(0, budgetMs) * System.Diagnostics.Stopwatch.Frequency / 1000;
            _sharedFrom = shared ? OtherProgramWaitTicks() : -1;
        }

        /// <summary>What is left once the take in progress has seen
        /// <paramref name="seenTicks"/> of waiting for another program.</summary>
        internal long LeftTicks(long seenTicks) => _sharedFrom >= 0
            ? _ticks - (OtherProgramWaitTicks() - _sharedFrom)
            : _ticks - Interlocked.Read(ref _spentTicks) - seenTicks;

        /// <summary>Charge a thread's budget with what a finished take saw.
        /// The shared budget follows the clock instead.</summary>
        internal void Charge(long seenTicks)
        {
            if (_sharedFrom < 0) Interlocked.Add(ref _spentTicks, Math.Max(0, seenTicks));
        }
    }

    /// <summary>The bound of this thread's takes, set by
    /// <see cref="BoundPnpLockWaits"/>, or null.</summary>
    [ThreadStatic] private static LockWaitBudget? t_lockBudget;

    /// <summary>Takes of the PnP lock this thread made that the bound
    /// refused. A dispose compares it before and after its teardown.</summary>
    [ThreadStatic] private static long t_lockTakesRefused;

    /// <inheritdoc cref="t_lockTakesRefused"/>
    internal static long LockTakesRefusedHere => t_lockTakesRefused;

    private static LockWaitBudget? s_exitLockBudget;

    /// <summary>Once the process has begun to exit (<see cref="ProcessExitSignal"/>),
    /// one budget of the exit handler's size (OwnerSweep.ExitWaitMs) for every
    /// take on a thread with no bound of its own: a program that disposes or
    /// cleans up from its own exit handler waits for another program's hold
    /// as long as the SDK's own exit does, and no longer. Its removals queue
    /// behind each other on the in-process gate uncounted, and the budget
    /// follows the process's clock of waits for another program, so several
    /// threads waiting at once spend it once. The signal also
    /// holds once a load context other than the default that loaded the SDK
    /// has begun to unload, and the budget then bounds that SDK's takes the
    /// same way. Null before.</summary>
    private static LockWaitBudget? ExitLockBudget()
    {
        if (!ProcessExitSignal.Raised) return null;
        return Volatile.Read(ref s_exitLockBudget)
               ?? Interlocked.CompareExchange(ref s_exitLockBudget,
                      new LockWaitBudget(TimeoutScale.Apply(OwnerSweep.ExitWaitMs), shared: true), null)
               ?? s_exitLockBudget!;
    }

    /// <summary>Bound the time this thread spends waiting for another
    /// program's hold of the PnP lock in <see cref="HoldPnpLock"/> until the
    /// scope ends, all its takes together, to <paramref name="budgetMs"/>
    /// (already scaled). Only those waits count, not the work done under the
    /// lock nor a wait behind this process's own hold. A take that does not
    /// get the lock in what is left throws <see cref="PnpLockBusyException"/>.
    /// The exit handler sets it, so another program's hold of the lock
    /// cannot keep the process in its exit. Dispose the scope on the thread
    /// that made it.</summary>
    internal static IDisposable BoundPnpLockWaits(int budgetMs)
    {
        var previous = t_lockBudget;
        t_lockBudget = new LockWaitBudget(budgetMs);
        return new LockWaitBound(previous);
    }

    private sealed class LockWaitBound : IDisposable
    {
        private readonly LockWaitBudget? _previous;
        private bool _done;
        internal LockWaitBound(LockWaitBudget? previous) => _previous = previous;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            t_lockBudget = _previous;
        }
    }

    /// <summary><see cref="HoldPnpLock"/> that gives up once it has waited
    /// <paramref name="timeoutMs"/> (already scaled) for another program's
    /// hold, and returns null. Waits behind this process's own holds are not
    /// counted, and once the process is exiting a wait behind one such hold
    /// is bounded by <paramref name="timeoutMs"/> (<see cref="TakePnpLock"/>). <paramref name="waitedTicks"/> is the waiting it counted. An
    /// abandoned lock is taken, as AcquirePnpLock takes it.</summary>
    internal static PnpLockHold? TryHoldPnpLock(int timeoutMs, out long waitedTicks)
    {
        long bound = (long)Math.Max(0, timeoutMs) * System.Diagnostics.Stopwatch.Frequency / 1000;
        return TakePnpLock(seen => bound - seen, bound, out waitedTicks);
    }

    // ── SetupAPI P/Invoke for DIF_REMOVE ──

    [LibraryImport("SetupAPI.dll", SetLastError = true)]
    private static partial IntPtr SetupDiCreateDeviceInfoList(IntPtr ClassGuid, IntPtr hwndParent);

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
    /// <paramref name="afterUninstall"/> runs under the phantom's claim
    /// after each successful uninstall and says whether Windows has finished
    /// with the id. Once <paramref name="stop"/> answers true, no further
    /// phantom is uninstalled. <paramref name="holdLock"/> takes the PnP lock
    /// for each uninstall, null when its budget ran out first, and the
    /// phantom is then left for the next run. Without it the lock is waited
    /// for as any removal waits. A phantom <paramref name="include"/> answers
    /// false for is left: a full removal leaves usbip-win2's host controller
    /// records to its usbip-win2 part.
    /// </summary>
    internal static int RemoveAccumulatedHmPhantoms(Func<string, bool>? afterUninstall = null, Func<bool>? stop = null,
                                                    Func<PnpLockHold?>? holdLock = null, Func<string, bool>? include = null)
    {
        int removed = 0;
        var source = PhantomSource;
        if (source != null)
        {
            foreach (var (id, uninstall) in source())
            {
                if (stop?.Invoke() == true) break;
                if (IsHmInstance(id) && include?.Invoke(id) != false && RemovePhantom(id, uninstall, afterUninstall, holdLock)) removed++;
            }
            return removed;
        }

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

                    if (!IsHmInstance(instanceId) || include?.Invoke(instanceId) == false) continue;
                    if (stop?.Invoke() == true) break;
                    IntPtr data = devInfoHandle.AddrOfPinnedObject();
                    if (RemovePhantom(instanceId, () => SetupDiRemoveDevice(dis, data), afterUninstall, holdLock))
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

    /// <summary>Whether the devnode is in the device tree.</summary>
    internal static bool IsPresent(string instanceId) => CM_Locate_DevNodeW(out _, instanceId, 0) == CR_SUCCESS;

    /// <summary>Every devnode of any class, present or not, that the phantom
    /// pass counts as HIDMaestro's (<see cref="IsHmInstance"/>), with whether
    /// it is in the device tree. The same SetupDi walk as
    /// <see cref="RemoveAccumulatedHmPhantoms"/>, reading only. A full
    /// removal lists these before and after it acts.</summary>
    internal static List<(string Id, bool Present)> ListHmInstances()
    {
        var list = new List<(string, bool)>();
        Guid nullGuid = Guid.Empty;
        IntPtr dis = SetupDiGetClassDevsByRef(ref nullGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_ALLCLASSES);
        if (dis == new IntPtr(-1)) return list;
        try
        {
            int devInfoSize = IntPtr.Size == 8 ? 32 : 28;
            byte[] devInfoBuf = new byte[devInfoSize];
            var devInfoHandle = System.Runtime.InteropServices.GCHandle.Alloc(
                devInfoBuf, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                char[] idBuf = new char[512];
                for (uint index = 0; ; index++)
                {
                    BitConverter.GetBytes(devInfoSize).CopyTo(devInfoBuf, 0);
                    if (!SetupDiEnumDeviceInfo(dis, index, devInfoHandle.AddrOfPinnedObject())) break;
                    if (!SetupDiGetDeviceInstanceIdW(dis, devInfoHandle.AddrOfPinnedObject(),
                            idBuf, (uint)idBuf.Length, out uint idLen))
                        continue;
                    string instanceId = new string(idBuf, 0, (int)idLen - 1);
                    if (!IsHmInstance(instanceId)) continue;
                    list.Add((instanceId, CM_Locate_DevNodeW(out _, instanceId, 0) == CR_SUCCESS));
                }
            }
            finally { devInfoHandle.Free(); }
        }
        finally { SetupDiDestroyDeviceInfoList(dis); }
        return list;
    }

    /// <summary>Phantoms with no device behind them, each id with the call
    /// that stands for its SetupDiRemoveDevice. Coexistence probe
    /// `phantomgate` sets it through reflection to run the phantom cleanup's
    /// real path, claim, uninstall and wait, with no device. Null outside
    /// the probe.</summary>
    internal static Func<IEnumerable<(string Id, Func<bool> Uninstall)>>? PhantomSource;

    // Issue #28 (v1.3.16): the prior VID_045E&PID_028E /
    // VID_BEEF&PID_F000 substring checks matched real Xbox 360 wired
    // controllers and PadForge Custom devices regardless of enumerator
    //: so a real unplugged Xbox 360's phantom record qualified. The
    // new test scopes pure-name matches to the HIDMAESTRO* /
    // HMCOMPANION* enumerator forms (which are HM-exclusive by
    // construction), and otherwise routes through IsHidMaestroOwned to
    // prove ownership via the devnode's HardwareID list.
    private static bool IsHmInstance(string iid)
    {
        if (iid.IndexOf("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) >= 0
            || iid.IndexOf("HMCOMPANION", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        return IsHidMaestroOwned(iid);
    }

    /// <summary>Uninstall one HIDMaestro phantom under the PnP lock and its
    /// owner's claim, and run <paramref name="afterUninstall"/> inside both
    /// once the uninstall succeeded, or the removal test when there is no
    /// callback. True when it was uninstalled.</summary>
    private static bool RemovePhantom(string instanceId, Func<bool> uninstall, Func<string, bool>? afterUninstall,
                                      Func<PnpLockHold?>? holdLock)
    {
        // Skip PRESENT devnodes. Only sweep PHANTOMs to guarantee
        // we never disturb a live controller.
        if (CM_Locate_DevNodeW(out _, instanceId, 0) == CR_SUCCESS)
            return false;

        // Every uninstall of a HIDMaestro devnode holds the PnP lock through
        // its removal test, so this pass cannot free a devnode while another
        // program registers one: the uninstall pass that ran just before
        // both crashing registrations of 2026-10-14 and 10-15. The cleanup
        // waits for the lock within its own budget (holdLock). A phantom
        // back in the tree since the check above is left alone.
        using var pnp = holdLock != null ? holdLock() : HoldPnpLock();
        if (pnp == null || CM_Locate_DevNodeW(out _, instanceId, 0) == CR_SUCCESS)
            return false;

        // The claim of the identity the phantom belongs to is held across
        // the uninstall. A create of this release takes that claim before
        // it registers a devnode, so it cannot register this id during the
        // uninstall. That covers Windows' removal only if the call has
        // finished it when it returns, which is not verified. A phantom
        // whose claim another program holds is that program's create in
        // flight, and is left. The test above does not find a devnode in
        // the process of being removed, so one with no claim to take (a
        // name older than identity tokens, a HID child whose parent cannot
        // be found) still reaches the uninstall. The claim holds off only
        // this release's creates: a v1.12.0 program takes none, so one that
        // starts on a default token's index once its owner pipe has closed
        // can register the id during the uninstall and the wait after it.
        SafeFileHandle? claim = ClaimPhantomOwner(instanceId, out bool heldElsewhere);
        if (heldElsewhere) return false;
        bool settled = true;
        try
        {
            // The one uninstall the phantom cleanup makes. The lines around
            // it let a log show a process that ended inside it, and the
            // thread's Windows id lets a probe find the thread waiting in it.
            DeviceOrchestrator.LogDiag($"    phantom uninstall begins on thread {GetCurrentThreadId()}: {instanceId}");
            bool uninstalled = uninstall();
            DeviceOrchestrator.LogDiag($"    phantom uninstall returned {uninstalled}: {instanceId}");
            if (!uninstalled) return false;
            // Still under the claim, so no create of this release registers
            // the id until the callback returns. A phantom is out of the tree,
            // so its record decides and no REMOVED registration is opened.
            settled = afterUninstall?.Invoke(instanceId)
                      ?? WaitRemovalComplete(instanceId, uninstalls: true, TimeoutScale.Apply(QuickRemovalMs));
            return true;
        }
        finally { ReleaseOwnerClaim(claim, settled); }
    }

    /// <summary>Let go of the identity claim held across a removal.
    /// An id Windows still knows when the process is exiting keeps its claim
    /// until the process ends: releasing it earlier would let another
    /// program register the id while it may still be being removed, during
    /// the rest of this process's exit.</summary>
    private static void ReleaseOwnerClaim(SafeFileHandle? claim, bool settled)
    {
        if (claim != null && !settled && KeepUnsettledClaims)
            lock (s_keptClaims) s_keptClaims.Add(claim);
        else
            claim?.Dispose();
    }

    /// <summary>Set by the exit handler before it joins the phantom
    /// cleanup: from then on the claim of a phantom whose removal was not
    /// confirmed stays with the process until it ends.</summary>
    internal static volatile bool KeepUnsettledClaims;

    private static readonly List<SafeFileHandle> s_keptClaims = new();

    /// <summary>Answers, in place of the PHANTOM locate, whether Windows
    /// still knows a phantom, for the removal test (<see cref="Locate"/>):
    /// a phantom is never in the tree. Coexistence probe `phantomgate` sets
    /// it through reflection, with <see cref="PhantomSource"/>, to run the
    /// cleanup's gate, its wait and the exit join with no device. Null
    /// outside the probe.</summary>
    internal static Func<string, bool>? RemovalLocate;

    /// <summary>How often <see cref="WaitRemovalComplete"/> looks when no
    /// notification wakes it.</summary>
    internal const int RemovalPollMs = RecheckMs;

    /// <summary>Wait until a removal the SDK started on
    /// <paramref name="instanceId"/> has finished, or
    /// <paramref name="budgetMs"/> (already scaled) passes, and say whether
    /// it finished. With a budget of 0 it looks once. The one test of a
    /// finished removal for the sweeps, the exit handler and the phantom
    /// cleanup: the removal test (<see cref="RemovalComplete"/>), started
    /// at the call. A devnode in the tree then is finished on its
    /// DEVICEINSTANCEREMOVED notification or, at the end of the budget,
    /// once a NORMAL locate fails. An uninstall
    /// (<paramref name="uninstalls"/> true: a ROOT or HID id removed by
    /// SetupDiRemoveDevice or CM_Uninstall_DevNode) is finished only once a
    /// PHANTOM locate fails as well. A software device closed by
    /// SwDeviceClose keeps its record, so pass false for an SWD id. A locate
    /// the probe's seam answers by throwing counts as finding the id.</summary>
    internal static bool WaitRemovalComplete(string instanceId, bool uninstalls, int budgetMs)
    {
        using var watch = RemovalWatch.Start(instanceId);
        return watch.WaitWithin(uninstalls, budgetMs) != RemovalOutcome.NotGone;
    }

    /// <summary>Whether an id names a software device, whose record
    /// SwDeviceClose keeps: the <c>uninstalls</c> argument of
    /// <see cref="WaitRemovalComplete"/> is false for these and true for
    /// the ROOT and HID ids the SDK removes.</summary>
    internal static bool KeepsRecord(string instanceId) =>
        instanceId.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The identity claim of the controller a phantom belongs to:
    /// its own token, or for a HID child its parent's. Null with
    /// <paramref name="heldElsewhere"/> false for a phantom older than
    /// identity tokens, which no create of this release registers again.
    /// A v1.12.0 program takes no claim, so a default token's phantom
    /// counts as held while a v1.12.0 program holds that index's owner
    /// pipe.</summary>
    private static SafeFileHandle? ClaimPhantomOwner(string instanceId, out bool heldElsewhere)
    {
        heldElsewhere = false;
        string? token = IdentityTokenOf(instanceId);
        if (token == null && instanceId.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = instanceId.Split('\\');
            string? parent = parts.Length == 3 ? GetRegistryParentId(@"SYSTEM\CurrentControlSet\Enum\HID", parts[1], parts[2]) : null;
            if (parent != null) token = IdentityTokenOf(parent);
        }
        if (token == null) return null;
        if (token.Length == DeviceIdentity.TokenPrefix.Length + 4
            && int.TryParse(token.AsSpan(DeviceIdentity.TokenPrefix.Length), System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out int index)
            && MachineClaims.IsHeld(MachineClaims.LegacyOwnerPipeName(index)))
        {
            heldElsewhere = true;
            return null;
        }
        SafeFileHandle? claim = MachineClaims.TryClaim(MachineClaims.IdentityPipeName(token));
        heldElsewhere = claim == null;
        return claim;
    }

    /// <summary>The identity token an instance id ends in, or null for a
    /// name older than identity tokens.</summary>
    private static string? IdentityTokenOf(string instanceId)
    {
        string name = instanceId.Substring(instanceId.LastIndexOf('\\') + 1);
        return DeviceIdentity.IsIdentityToken(name) ? name : null;
    }

    /// <summary>The instance ids of a present devnode's direct children,
    /// none when it is not present.</summary>
    internal static List<string> ChildIdsOf(string instanceId) =>
        CM_Locate_DevNodeW(out uint devInst, instanceId, 0) == CR_SUCCESS ? GetAllChildDeviceIds(devInst) : new List<string>();

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

    /// <summary>Where the rival scan reads the device tree and what it
    /// does to a rival: the registry and PnP, or for
    /// identity_derivation_check (S58) a recorded tree it sets through
    /// <see cref="RivalSourceHook"/>.</summary>
    internal interface IRivalSource
    {
        /// <summary>The enumerator keys under Enum\<paramref name="enumRoot"/>.</summary>
        string[] Enumerators(string enumRoot);
        /// <summary>The instance keys under Enum\<paramref name="enumRoot"/>\<paramref name="enumerator"/>.</summary>
        string[] Instances(string enumRoot, string enumerator);
        /// <summary>The instance key's ParentIdPrefix value, or null.</summary>
        string? ParentIdPrefix(string instanceId);
        /// <summary>The devnode is in the device tree.</summary>
        bool Live(string instanceId);
        /// <summary>The devnode carries a HIDMaestro hardware ID (<see cref="IsHidMaestroOwned"/>).</summary>
        bool Owned(string instanceId);
        OwnerCensus.Owner Classify(string instanceId);
        /// <summary>Removes the devnode, as <see cref="RemoveDevice"/> does.</summary>
        void Remove(string instanceId);
        /// <summary>The instance key is still in the registry.</summary>
        bool RecordExists(string instanceId);
        /// <summary>Writes the existing instance key's ParentIdPrefix, as
        /// <see cref="DeviceIdentity.ApplyParentIdPrefix"/> does without
        /// creating the key. True when the key now holds it.</summary>
        bool SetParentIdPrefix(string instanceId, string prefix);
    }

    /// <summary>Stands in for the registry and PnP in the rival scan. Set by
    /// identity_derivation_check's offline part, null outside it.</summary>
    internal static IRivalSource? RivalSourceHook;

    sealed class SystemRivalSource : IRivalSource
    {
        internal static readonly SystemRivalSource Instance = new();

        static string[] SubKeys(string path)
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(path);
                return k?.GetSubKeyNames() ?? Array.Empty<string>();
            }
            catch { return Array.Empty<string>(); }
        }

        public string[] Enumerators(string enumRoot) => SubKeys($@"SYSTEM\CurrentControlSet\Enum\{enumRoot}");
        public string[] Instances(string enumRoot, string enumerator) => SubKeys($@"SYSTEM\CurrentControlSet\Enum\{enumRoot}\{enumerator}");

        public string? ParentIdPrefix(string instanceId)
        {
            try
            {
                using var ik = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
                return ik?.GetValue("ParentIdPrefix") as string;
            }
            catch { return null; }
        }

        public bool Live(string instanceId) => CM_Locate_DevNodeW(out _, instanceId, 0) == CR_SUCCESS;
        public bool Owned(string instanceId) => IsHidMaestroOwned(instanceId);
        public OwnerCensus.Owner Classify(string instanceId) => OwnerCensus.Classify(instanceId);
        // An SWD rival whose helper cannot be staged throws here, and the
        // create fails with the reason instead of returning no device.
        public void Remove(string instanceId) => RemoveDevice(instanceId, timeoutMs: 120_000, forceFallbacks: true);

        public bool RecordExists(string instanceId)
        {
            try { using var ik = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}"); return ik != null; }
            catch { return true; }
        }

        public bool SetParentIdPrefix(string instanceId, string prefix)
        {
            DeviceIdentity.ApplyParentIdPrefix(instanceId, prefix, createKey: false);
            return string.Equals(ParentIdPrefix(instanceId), prefix, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Clears every other parent whose HID child would take the device
    /// instance path this create's child takes, and says whether the way is
    /// clear. With <paramref name="remove"/> false it only checks, and a
    /// rival it would remove makes the way not clear: the registration
    /// gate's check under the PnP lock.
    ///
    /// <para>hidclass names a HID child <c>HID\&lt;segment&gt;\&lt;prefix&gt;&amp;&lt;collection&gt;</c>:
    /// the segment is the parent's enumerator, the second field of its
    /// device ID (<c>ROOT\HIDClass\HM_0000</c> gives <c>HID\HIDCLASS\...</c>),
    /// and the prefix is the parent's ParentIdPrefix. Two parents whose
    /// children share that path make PnP report a duplicate PDO, bugcheck
    /// 0xCA. Two parents under different enumerators give their children
    /// different paths whatever their prefixes. So a rival is a parent
    /// under <paramref name="enumerator"/>, in either root, whose prefix is
    /// one this create's child can arrive under: <paramref name="prefix"/>,
    /// and with <paramref name="rewriteOwnRecord"/> the value the create's
    /// own retained record still holds when it cannot be rewritten first.
    /// SwDeviceCreate starts the child under the record's value before the
    /// SDK can write it, so an SWD create passes true and the record is
    /// rewritten before the scan. A ROOT record is re-enumerated at every
    /// boot, live or not, so it counts either way. An SWD record only
    /// enumerates while its creator holds it, so only a live one
    /// counts.</para>
    ///
    /// <para>The scan walks every enumerator HIDMaestro creates parents
    /// under, ROOT (<see cref="PresentationPlan.IsRootParentEnumerator"/>)
    /// and SWD (<see cref="PresentationPlan.IsSwdParentEnumerator"/>), beside
    /// <paramref name="enumerator"/>. Under ROOT a parent with
    /// <paramref name="token"/> under another enumerator is the same
    /// controller left where an older presentation put it, and is a rival
    /// whatever its prefix. The instance of <paramref name="token"/> under
    /// <paramref name="enumerator"/> is the create's own prior life, which
    /// the registration gate removes first.</para>
    ///
    /// <para>A rival that is not HIDMaestro's is never touched, and the
    /// create is refused. A present rival whose owner runs, here or in
    /// another program, or cannot be known, is refused with
    /// <see cref="HMResourceInUseException"/>. Any other rival is
    /// removed. False means a rival is still there and the caller must not
    /// create.</para>
    /// </summary>
    internal static bool ClearParentIdPrefixRivals(string enumRoot, string enumerator, string token, string prefix,
                                                   bool remove = true, bool rewriteOwnRecord = false)
    {
        var src = RivalSourceHook ?? SystemRivalSource.Instance;
        static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        bool root = Same(enumRoot, "ROOT");

        var childPrefixes = new List<string> { prefix };
        string own = $@"{enumRoot}\{enumerator}\{token}";
        if (rewriteOwnRecord && src.RecordExists(own) && src.ParentIdPrefix(own) is { } kept && !Same(kept, prefix)
            && !src.SetParentIdPrefix(own, prefix))
        {
            DeviceOrchestrator.LogDiag($"      {own} keeps ParentIdPrefix {kept}, and its child arrives under it first");
            childPrefixes.Add(kept);
        }

        bool clear = true;
        foreach (string r in new[] { "ROOT", "SWD" })
        {
            bool rRoot = Same(r, "ROOT");
            var enumerators = src.Enumerators(r).Where(e => Same(e, enumerator)
                || (rRoot ? PresentationPlan.IsRootParentEnumerator(e) : PresentationPlan.IsSwdParentEnumerator(e))).ToList();
            if (Same(r, enumRoot) && !enumerators.Exists(e => Same(e, enumerator)))
                enumerators.Add(enumerator);
            foreach (string e in enumerators)
            {
                bool sameSegment = Same(e, enumerator);
                foreach (string name in src.Instances(r, e))
                {
                    bool sameToken = Same(name, token);
                    if (sameSegment && sameToken && Same(r, enumRoot)) continue;
                    string rival = $@"{r}\{e}\{name}";
                    string? held = src.ParentIdPrefix(rival);
                    bool sameChildPath = sameSegment && held != null && childPrefixes.Exists(p => Same(p, held));
                    bool leftover = root && rRoot && sameToken && !sameSegment;
                    if (!sameChildPath && !leftover) continue;
                    string why = sameChildPath
                        ? $"would give its HID child the path HID\\{e.ToUpperInvariant()}\\{held}&..., this controller's child's"
                        : $"is this controller's parent left under {e}";

                    bool live = src.Live(rival);
                    if (!rRoot && !live) continue;

                    if (!src.Owned(rival))
                    {
                        DeviceOrchestrator.LogDiag($"      {rival} {why} and is not HIDMaestro-owned; refusing to create beside it");
                        clear = false;
                        continue;
                    }

                    // A present rival is removed only when its owner is
                    // provably gone. One that belongs to a running program,
                    // here or elsewhere, or to a program whose state cannot
                    // be known, stays, and this create is refused instead:
                    // either way no two children ever share a path.
                    if (live)
                    {
                        var owner = src.Classify(rival);
                        if (owner is not (OwnerCensus.Owner.Dead or OwnerCensus.Owner.DeadLegacy))
                        {
                            DeviceOrchestrator.LogDiag($"      {rival} {why} and its owner is {owner}; refusing to create beside it");
                            string who = owner switch
                            {
                                OwnerCensus.Owner.Unknown =>
                                    "a program built on HIDMaestro older than 1.12.0, which may still be running. " +
                                    "If it is not, remove the device with HMContext.RemoveAllVirtualControllers(HMCleanupScope.Everything, preserveInstall: true)",
                                OwnerCensus.Owner.LiveLegacy => "a running program built on HIDMaestro 1.12.0",
                                _ => "a running program",
                            };
                            throw new HMResourceInUseException(
                                $"The device {rival} {why}, and it belongs to {who}. " +
                                "Two HID children on one device instance path stop Windows from booting, so this create is refused.",
                                $"ParentIdPrefix {held ?? prefix}", null);
                        }
                    }

                    if (!remove)
                    {
                        DeviceOrchestrator.LogDiag($"      {rival} {why} again");
                        clear = false;
                        continue;
                    }
                    DeviceOrchestrator.LogDiag($"      {rival} {why}; removing it before create");
                    src.Remove(rival);

                    bool stillThere = rRoot ? src.RecordExists(rival) : src.Live(rival);
                    if (stillThere)
                    {
                        DeviceOrchestrator.LogDiag($"      {rival} survived removal; refusing to create beside it");
                        clear = false;
                    }
                }
            }
        }
        return clear;
    }

    /// <summary>
    /// Removes a device and all its children, waits for removal to complete.
    /// Enumerates HID children first and removes them individually via DIF_REMOVE,
    /// then removes the parent. This prevents ghost HID children from surviving.
    /// </summary>
    /// <exception cref="InvalidOperationException">An SWD parent's removal
    /// needs hmswd.exe, and it could not be staged or held
    /// (<see cref="SwdDeviceFactory.Remove"/>). Thrown under the PnP lock
    /// before any wait for the removal, with the device left in
    /// place.</exception>
    /// <exception cref="PnpLockBusyException">Another program held the PnP
    /// lock past this take's bound (<see cref="HoldPnpLock"/>). Nothing was
    /// removed.</exception>
    public static bool RemoveDevice(string instanceId, int timeoutMs = 5000, bool fast = false, bool forceFallbacks = false)
    {
        if (Locate(instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS &&
            Locate(instanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
            return true;

        using var pnp = HoldPnpLock();
        RemoveDeviceLocked(instanceId, timeoutMs, fast, forceFallbacks, out bool removed);
        return removed;
    }

    /// <summary>Removes a prior life of an id about to be registered again,
    /// as <see cref="RemoveDevice"/> with its fallbacks, and returns the
    /// outcome of the parent's removal test for the registration gate.</summary>
    /// <summary>Asks Windows to remove a device and the devices below it the
    /// way Safely Remove Hardware asks, under the PnP lock
    /// (CM_Query_And_Remove_SubTreeW). Every program that watches the device
    /// for a removal query closes its handles before the removal: Windows'
    /// input stack does for a keyboard or mouse, and a vendor's service that
    /// opens its own devices does too, such as Logitech's lighting service.
    /// True when the device has left the tree, or was out of it. False, with
    /// <paramref name="veto"/>, when a program refused, as one that holds a
    /// HID child open without watching it does (Steam with a PlayStation
    /// pad). A refusal changes nothing: no record is touched, where a
    /// refused SetupDiRemoveDevice has had its class installer delete the
    /// child's driver before it queried.</summary>
    internal static bool QueryRemoveSubtree(string instanceId, out string veto)
    {
        veto = "";
        using var pnp = HoldPnpLock();
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
            return true;
        IntPtr name = Marshal.AllocHGlobal(VetoNameChars * sizeof(char));
        try
        {
            Marshal.Copy(new byte[VetoNameChars * sizeof(char)], 0, name, VetoNameChars * sizeof(char));
            int cr = CM_Query_And_Remove_SubTreeW(devInst, out uint vetoType, name, VetoNameChars,
                CM_REMOVE_UI_NOT_OK | CM_REMOVE_NO_RESTART);
            if (cr == 0) return true;
            veto = $"CR {cr}, veto type {vetoType}, {Marshal.PtrToStringUni(name)}";
            return false;
        }
        finally { Marshal.FreeHGlobal(name); }
    }

    // cfgmgr32.h: CM_REMOVE_UI_NOT_OK and CM_REMOVE_NO_RESTART, and a veto
    // name of MAX_PATH characters.
    private const uint CM_REMOVE_UI_NOT_OK = 0x00000001, CM_REMOVE_NO_RESTART = 0x00000002;
    private const int VetoNameChars = 260;

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Query_And_Remove_SubTreeW(uint dnAncestor, out uint pVetoType, IntPtr pszVetoName, uint ulNameLength, uint ulFlags);

    internal static RemovalOutcome RemovePriorLife(string instanceId, int timeoutMs)
    {
        if (CM_Locate_DevNodeW(out _, instanceId, 0) != CR_SUCCESS &&
            CM_Locate_DevNodeW(out _, instanceId, 1) != CR_SUCCESS)
            return RemovalOutcome.Removed;
        using var pnp = HoldPnpLock();
        return RemoveDeviceLocked(instanceId, timeoutMs, fast: false, forceFallbacks: true, out _);
    }

    /// <summary>The budget a parent's removal test gets: the caller's for
    /// a SetupDiRemoveDevice or SwDeviceClose removal, which can wait on open
    /// handles, and <see cref="QuickRemovalMs"/> at most for a fast one,
    /// whatever budget its caller passed.</summary>
    internal static int ParentRemovalBudget(bool fast, int timeoutMs) =>
        fast ? Math.Min(timeoutMs, QuickRemovalMs) : timeoutMs;

    static RemovalOutcome RemoveDeviceLocked(string instanceId, int timeoutMs, bool fast, bool forceFallbacks, out bool removed)
    {
        removed = true;
        // With the hook set every locate goes through LocateHook, and the
        // devnode handles below are never used.
        var calls = RemovalCallsHook;
        uint Find(string id, uint flags) => calls != null ? Locate(id, flags) : CM_Locate_DevNodeW(out _, id, flags);
        uint devInst = 0;
        bool known = calls != null
            ? Find(instanceId, 0) == CR_SUCCESS || Find(instanceId, 1) == CR_SUCCESS
            : CM_Locate_DevNodeW(out devInst, instanceId, 0) == CR_SUCCESS || CM_Locate_DevNodeW(out devInst, instanceId, 1) == CR_SUCCESS;
        if (!known)
            return RemovalOutcome.Removed;

        // In fast mode (used by cleanup paths), each devnode goes by
        // CM_Disable + CM_Uninstall instead of SetupDiRemoveDevice, and the
        // pnputil and devcon fallbacks run only when the caller forces
        // them. Every removal, fast or not, has a removal test
        // (RemovalComplete), each with the budget its kind needs and no
        // more: the caller's timeoutMs for a parent that SetupDiRemoveDevice
        // or SwDeviceClose removes, which can wait on open handles, and
        // QuickRemovalMs for a call that finishes its work before it
        // returns, a fast removal of any caller among them. Until v2.0.0
        // fast mode waited for nothing, so the next registration could
        // follow a removal Windows was still processing. Each watch starts
        // right before its devnode's removal call, so the REMOVED
        // notification of a devnode that leaves during the call is not
        // missed. The parent's steps share one watch (StepwiseRemoval), from
        // its first removal call through the fallbacks' test. A ROOT or HID
        // parent starts that watch before the first child's call, and a
        // software parent's close starts it and keeps it through the children
        // its cascade may still be removing. While it is open the children
        // go with the parent's subtree (SubtreeChildren): they open no
        // registration, and their tests run after the parent's, on the
        // parent's REMOVED notification, a software parent's survivor held
        // for it once gone. With no parent watch open (a parent out of the
        // tree when its watch started, or one whose registration Windows
        // refused, a close's included), a child is removed directly on its
        // own registration. One registration at most is open at a time on
        // every path.
        int parentBudget = ParentRemovalBudget(fast, timeoutMs);
        using var parent = new StepwiseRemoval(instanceId);
        var parentOutcome = RemovalOutcome.Pending;
        var closeOutcome = RemovalOutcome.Removed;

        bool isSwdParent = instanceId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase);
        using var subtree = new SubtreeChildren(parent, holdForCascade: isSwdParent);
        // Whether the pnputil and devcon fallbacks (step 3) take over a
        // parent that step 2 leaves not gone.
        bool fallbacks = (!fast || forceFallbacks) &&
            (instanceId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) ||
             instanceId.StartsWith("HID\\",  StringComparison.OrdinalIgnoreCase));

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
        IReadOnlyList<string> childIds = calls?.Children(instanceId) ?? GetAllChildDeviceIds(devInst);
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
        bool isLive = Find(instanceId, 0) == CR_SUCCESS;
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
            int hrFast = 0;
            closeOutcome = parent.Step(() => { hrFast = calls?.Close(instanceId) ?? SwdDeviceFactory.Remove(instanceId); return true; },
                                       uninstalls: false, parentBudget);
            DeviceOrchestrator.LogDiag(
                $"      SwdDeviceFactory.Remove({instanceId}) hr=0x{hrFast:X8} " +
                $"(fast mode): {closeOutcome} in {swFastSw.ElapsedMilliseconds}ms");
            // Step 2 disables and uninstalls the devnode whatever the close
            // did, on the close's watch. It stays open through the children's
            // removals, so they open no registration of their own while the
            // close's cascade may still be removing them (SubtreeChildren).
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
            int hr = 0;
            // Block until the kernel actually fires the device-removed
            // notification: or the timeout expires. Use the caller's
            // timeoutMs budget so callers explicitly choose how long to
            // wait. Live-swap callers pass 120_000 ms (the full BT
            // xinputhid cascade budget); cleanup paths may pass less.
            // SwDeviceClose leaves the instance's record, so the record is
            // not waited for. The close's watch stays open through the
            // children's removals, so a survivor opens no registration of its
            // own while the cascade may still be removing it, and is held for
            // the parent's REMOVED instead (SubtreeChildren). When Windows
            // refused the close's registration, each survivor registers its
            // own at its call, one at a time.
            parentOutcome = parent.Step(() => { hr = calls?.Close(instanceId) ?? SwdDeviceFactory.Remove(instanceId); return true; },
                                        uninstalls: false, timeoutMs);
            goneAfterDif = parentOutcome != RemovalOutcome.NotGone;
            DeviceOrchestrator.LogDiag($"      SwdDeviceFactory.Remove({instanceId}) hr=0x{hr:X8} confirmed_offline={goneAfterDif} after {swSw.ElapsedMilliseconds}ms");
        }

        // Step 1: Find and remove all children. For SWD parents this
        // typically becomes a no-op because the SwD close already
        // cascade-removed them. A ROOT or HID parent's watch starts first,
        // so it sees the parent leave from before the first child's call.
        if (!isSwdParent) parent.Begin();
        foreach (string childId in childIds)
        {
            // Skip if the SwD-parent close already cascaded this child away.
            if (isSwdParent && !fast
                && Find(childId, 0) != CR_SUCCESS
                && Find(childId, 1) != CR_SUCCESS)
                continue;

            if (fast)
            {
                // Fast mode: CM_Disable + CM_Uninstall directly. Avoids the
                // SetupDiRemoveDevice → IRP_MN_QUERY_REMOVE → kernel-veto
                // timeout (~1s per device) that dominates cleanup time when
                // the UMDF host still holds handles after a force-kill. A
                // keyboard refuses the disable and takes SetupDiRemoveDevice
                // instead (DisableAndUninstall), before its parent is tried.
                uint ci = 0;
                if (calls != null
                    ? Find(childId, 0) == CR_SUCCESS || Find(childId, 1) == CR_SUCCESS
                    : CM_Locate_DevNodeW(out ci, childId, 0) == CR_SUCCESS || CM_Locate_DevNodeW(out ci, childId, 1) == CR_SUCCESS)
                    subtree.Remove(childId, () => calls?.Uninstall(childId) ?? DisableAndUninstall(ci, childId));
            }
            else
            {
                subtree.Remove(childId, () => calls?.Remove(childId) ?? DifRemoveDevice(childId));
            }
        }

        // Step 2: Remove the parent (for non-SWD; SWD already done above)
        // A removal call that failed gets no budget: its test decides at
        // once, and the fallbacks below take over. A software device's record
        // is not waited for: SwDeviceClose keeps it, and no source shows that
        // CM_Uninstall_DevNode deletes it.
        if (fast)
        {
            var waitSw = System.Diagnostics.Stopwatch.StartNew();
            parentOutcome = Then(closeOutcome, parent.Step(() => calls?.Uninstall(instanceId) ?? DisableAndUninstall(devInst, instanceId),
                                                           uninstalls: !isSwdParent, parentBudget));
            goneAfterDif = parentOutcome != RemovalOutcome.NotGone;
            DeviceOrchestrator.LogDiag($"      fast removal of {instanceId}: {parentOutcome} after {waitSw.ElapsedMilliseconds}ms");
        }
        else if (isSwdParent)
        {
            // Already removed at the top of the function. Re-check in case
            // a survivor sweep above kicked the parent into a transient
            // state.
            if (Find(instanceId, 0) == CR_SUCCESS)
                parentOutcome = RemovalOutcome.NotGone;
            else if (parentOutcome == RemovalOutcome.Pending)
                parentOutcome = RemovalOutcome.Removed;
            goneAfterDif = parentOutcome != RemovalOutcome.NotGone;
        }
        else
        {
            var waitSw = System.Diagnostics.Stopwatch.StartNew();
            parentOutcome = parent.Step(() => calls?.Remove(instanceId) ?? DifRemoveDevice(instanceId), uninstalls: true, parentBudget);
            goneAfterDif = parentOutcome != RemovalOutcome.NotGone;
            DeviceOrchestrator.LogDiag($"      WaitForDeviceRemoval({instanceId}, {timeoutMs}ms): {parentOutcome} after {waitSw.ElapsedMilliseconds}ms");
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
        if (fallbacks && !goneAfterDif)
        {
            // The parent's watch from step 2 goes on.
            parent.Begin();
            // On the hook no process starts: the hook stands in for both runs.
            calls?.Fallbacks(instanceId);
            bool stillPhantom = calls == null && CM_Locate_DevNodeW(out _, instanceId, 1) == CR_SUCCESS;
            if (stillPhantom)
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        // From System32, never a pnputil.exe in the
                        // application's or the current directory.
                        FileName = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe"),
                        Arguments = $"/remove-device \"{instanceId}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    ProtectedStaging.ConfineChildTemp(psi);
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
                        PnputilHelper.Drain(outTask, errTask);
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
            bool stillPhantom2 = calls == null && CM_Locate_DevNodeW(out _, instanceId, 1) == CR_SUCCESS;
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
                        ProtectedStaging.ConfineChildTemp(psi);
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
                            PnputilHelper.Drain(outTask, errTask);
                        }
                    }
                    catch { }
                }
            }

            // The fallbacks are removals too, tested on the parent's watch
            // before the PnP lock is let go. The test runs even when the
            // devnode went before pnputil was needed: its outcome is the
            // removal's.
            parentOutcome = parent.Wait(uninstalls: true, QuickRemovalMs);
        }

        // The children removed while the parent's watch was open went with
        // its subtree. Their tests run now, under the same PnP lock, on the
        // parent's REMOVED notification.
        subtree.Settle(parentOutcome);

        // Step 4: Final check
        removed = Find(instanceId, 0) != CR_SUCCESS && Find(instanceId, 1) != CR_SUCCESS;
        return parentOutcome;
    }

    /// <summary>The outcome of a removal made in two steps on one devnode:
    /// not gone when the last step left it, and otherwise gone without its
    /// REMOVED notification when either step saw it go without one. An
    /// earlier step that left it in the tree says nothing once the last
    /// step has removed it.</summary>
    internal static RemovalOutcome Then(RemovalOutcome earlier, RemovalOutcome last) =>
        last == RemovalOutcome.NotGone || earlier != RemovalOutcome.GoneWithoutRemoved ? last : earlier;

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
    /// before the parent, so a keyboard child goes first, and its removal
    /// test waits it out before the parent is tried. False when the removal
    /// call failed.
    ///
    /// Measured 2026-10-13 on the root of a keyboard whose consumer had
    /// been killed: CM_Disable_DevNode returned CR_NOT_DISABLEABLE and
    /// SetupDiRemoveDevice failed with ERROR_NOT_DISABLEABLE. The same call
    /// on its keyboard child succeeded, the root then reported disableable,
    /// and SetupDiRemoveDevice removed it. The two mice the same consumer
    /// held reported disableable throughout.
    /// </summary>
    static bool DisableAndUninstall(uint devInst, string instanceId)
    {
        if (CM_Disable_DevNode(devInst, 0) != CR_NOT_DISABLEABLE)
            return CM_Uninstall_DevNode(devInst, 0) == CR_SUCCESS;
        return DifRemoveDevice(instanceId);
    }

    /// <summary>
    /// Removes a single device via SetupDiRemoveDevice (same as devcon remove).
    /// This is more thorough than DIF_REMOVE: it bypasses class installers and
    /// directly removes the device node, preventing ghost entries on UMDF devices.
    /// Falls back to CM_Disable + CM_Uninstall if SetupAPI fails. False when
    /// its removal failed.
    /// </summary>
    static bool DifRemoveDevice(string instanceId)
    {
        uint devInst = 0;
        bool located = CM_Locate_DevNodeW(out devInst, instanceId, 0) == CR_SUCCESS
                    || CM_Locate_DevNodeW(out devInst, instanceId, 1) == CR_SUCCESS;

        // An empty set with no setup class, into which SetupDiOpenDeviceInfo
        // opens the one device by its instance id, present or not: a set of
        // one class would refuse a device of another. A set of every device
        // of every class (SetupDiGetClassDevs with DIGCF_ALLCLASSES), which
        // this call built before, took 8 to 9 ms on the bench machine on
        // 2026-10-19, once for each device a removal takes. The same day an
        // empty set opened five devnodes that were not present, of the
        // AudioEndpoint, HIDClass and USB classes. Nefarius'
        // PnPDevice.InstallCustomDriver and nefcon's DeviceRestart open a
        // device the same way.
        IntPtr dis = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (dis == new IntPtr(-1))
        {
            if (!located) return false;
            CM_Disable_DevNode(devInst, 0);
            return CM_Uninstall_DevNode(devInst, 0) == CR_SUCCESS;
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
                    if (!located) return false;
                    CM_Disable_DevNode(devInst, 0);
                    return CM_Uninstall_DevNode(devInst, 0) == CR_SUCCESS;
                }

                // SetupDiRemoveDevice: direct removal like devcon.exe.
                // Unlike DIF_REMOVE (class installer), this fully removes the device
                // node and registry entries, preventing ghosts on UMDF devices.
                // Fallback to DIF_REMOVE if direct removal fails.
                return SetupDiRemoveDevice(dis, devInfoHandle.AddrOfPinnedObject())
                    || SetupDiCallClassInstaller(DIF_REMOVE, dis, devInfoHandle.AddrOfPinnedObject());
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

    /// <summary>The ParentIdPrefix a ROOT parent under
    /// <paramref name="enumerator"/> gives its HID child: the identity's
    /// own, <paramref name="basePrefix"/>, unless a record of a child on
    /// that path is out of the tree with no Driver value and names
    /// HIDMaestro in its hardware ids. A vetoed removal left such records
    /// before v2.0.0 (SetupDiRemoveDevice deletes the driver before it
    /// queries the removal, and "Setting needs reboot"). On 2026-10-19
    /// every later child on that path was configured with a null driver
    /// and stopped with problem 19, and no user-mode call removed the
    /// record or installed a driver on it: SetupDiRemoveDevice and
    /// CM_Uninstall_DevNode failed with CR_NO_SUCH_DEVNODE, DiInstallDevice
    /// could not read the hardware ids, and CONFIGFLAG_REINSTALL was cleared
    /// unused. The child is then given the next prefix
    /// (<see cref="DeviceIdentity.AlternatePrefix"/>) whose path holds no
    /// such record, a fresh record Windows installs as a new device. All
    /// broken leaves the identity's own. The child path is
    /// HID\&lt;enumerator&gt;\&lt;prefix&gt;&amp;&lt;collection&gt;, as
    /// ClearParentIdPrefixRivals reads it.</summary>
    internal static string ChildPrefixFor(string enumerator, string basePrefix)
    {
        for (int n = 0; n <= DeviceIdentity.PrefixAlternates; n++)
        {
            string prefix = n == 0 ? basePrefix : DeviceIdentity.AlternatePrefix(basePrefix, n);
            if (!(DriverlessChildHook ?? DriverlessChild)(enumerator, prefix))
            {
                if (n > 0)
                    DeviceOrchestrator.LogDiag($"      the HID child record under {basePrefix} has no driver, so this child takes {prefix}");
                return prefix;
            }
        }
        return basePrefix;
    }

    /// <summary>Stands in for <see cref="DriverlessChild"/> in the offline
    /// checks. Null outside them.</summary>
    internal static Func<string, string, bool>? DriverlessChildHook;

    /// <summary>A record HID\&lt;enumerator&gt;\&lt;prefix&gt;&amp;N that is
    /// not in the tree, has no Driver value and names HIDMaestro in its
    /// hardware ids.</summary>
    static bool DriverlessChild(string enumerator, string prefix)
    {
        try
        {
            using var segment = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\HID\{enumerator}");
            if (segment == null) return false;
            foreach (string name in segment.GetSubKeyNames())
            {
                if (!name.StartsWith(prefix + "&", StringComparison.OrdinalIgnoreCase)) continue;
                using var record = segment.OpenSubKey(name);
                if (record == null || !string.IsNullOrEmpty(record.GetValue("Driver") as string)) continue;
                if (record.GetValue("HardwareID") is not string[] ids
                    || !Array.Exists(ids, id => id.Contains("HIDMaestro", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (Locate($@"HID\{enumerator}\{name}", CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return true;
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        return false;
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
    public static int RemoveOrphanHidChildren(ICollection<string>? removedIds = null)
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

                    // Parent is gone: remove the orphan silently. Caller
                    // gets the count for whatever observability it wants.
                    if (!RemoveOrphanHidChild(hidInstanceId, parentId, () => DifRemoveDevice(hidInstanceId)))
                        continue;
                    removedIds?.Add(hidInstanceId);
                    removed++;
                }
            }
        }
        catch (Exception ex) when (ex is not PnpLockBusyException)
        {
            // Defensive: a single bad HID child node shouldn't abort the
            // overall sweep. Caller still gets the removed count. A lock
            // take past its thread's bound goes to the caller, which keeps
            // its record for the next sweep.
        }

        return removed;
    }

    /// <summary>Remove one HID child whose recorded parent Windows no
    /// longer knows, in the tree or as a record, with
    /// <paramref name="remove"/>, under the PnP lock and the claim of the
    /// identity its parent belongs to, and test the removal. True when it
    /// was removed, false when it was left.
    ///
    /// <para>The parent is looked for again under the lock. The wait for the
    /// lock can be a whole registration of the same identity: the child's
    /// instance id stays the same across lives (issue #60 fixes both the
    /// parent's id and the child's path), so a parent registered during the
    /// wait would get its HID child back under this id while the sweep
    /// removed it. As <see cref="RemovePhantom"/> does, the child is also
    /// left while another program holds its identity's claim: a create of
    /// this release takes it before its gate.</para>
    ///
    /// <para>A claim this process holds is no other program's create, and
    /// the child is removed under it: the lease of the controller whose
    /// teardown runs this sweep (issue #9's surviving child), a sweep's
    /// claims of the groups it removes, the leases still open at exit. A
    /// create of this process cannot hold the identity of a controller being
    /// disposed, and its registration waits for the lock held here.</para></summary>
    internal static bool RemoveOrphanHidChild(string hidInstanceId, string parentId, Func<bool> remove)
    {
        if (ParentKnown(parentId)) return false;
        using var pnp = HoldPnpLock();
        if (ParentKnown(parentId)) return false;
        SafeFileHandle? claim = ClaimPhantomOwner(parentId, out bool heldElsewhere);
        if (heldElsewhere && !(IdentityTokenOf(parentId) is string token
                               && MachineClaims.HeldHere(MachineClaims.IdentityPipeName(token))))
            return false;
        var outcome = RemovalOutcome.Pending;
        try
        {
            using var watch = RemovalWatch.Start(hidInstanceId);
            outcome = watch.Wait(uninstalls: true, remove() ? QuickRemovalMs : 0);
            return true;
        }
        finally { ReleaseOwnerClaim(claim, settled: outcome != RemovalOutcome.NotGone); }

        static bool ParentKnown(string id) =>
            Locate(id, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS || Locate(id, CM_LOCATE_DEVNODE_PHANTOM) == CR_SUCCESS;
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
