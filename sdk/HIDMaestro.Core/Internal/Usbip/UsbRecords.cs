using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace HIDMaestro.Internal.Usbip;

/// <summary>The device records a USB/IP persona leaves after it unplugs.
///
/// <para>A device on usbip-win2's host controller is enumerated by the hub
/// driver as a real one is, so its records stay when it leaves: the USB
/// instance, <c>USB\VID_vvvv&amp;PID_pppp\&lt;serial&gt;</c>, and every record
/// below it (a composite's interface records, the HID collections, audio
/// endpoints). A persona carries no HIDMaestro token anywhere, so no sweep
/// can tell its records from a real device's by looking at them. The create
/// therefore writes the instance id into its owner record before the attach,
/// and, when Windows holds no record of that id then, into the ledger of
/// records HIDMaestro caused (<see cref="LedgerRoot"/>), which outlives the
/// program. Only a record so named is removed, never one found by a
/// pattern, and never one that was there before the create.</para>
///
/// <para>A dispose leaves the records, so the identity's next life has the
/// same USB instance and the same HID child, whose instance id Windows
/// builds from a prefix it keeps in the USB instance's record: a record
/// removed and made again gets a new prefix, and the HID interface path
/// changes with it (measured on the Celeron and the Surface,
/// 2026-11-18). The full removal (RemoveAllVirtualControllers with
/// Everything) and the uninstall remove every record the ledger names whose
/// device has left.</para>
///
/// <para>The records below the instance are found by the parent Windows
/// keeps for each devnode, present or not (DEVPKEY_Device_Parent, which
/// CM_Get_DevNode_Property answers for a non-present devnode with the parent
/// it last had). Each is removed with SetupDiRemoveDevice on the record
/// itself, the deepest first, and only while it and every record below the
/// instance are out of the device tree. Nothing is asked of the host
/// controller, its root hub or any other device on it.</para></summary>
internal static partial class UsbRecords
{
    /// <summary>The owner record's value that names the USB instance of a
    /// controller's device.</summary>
    internal const string InstanceValue = "UsbInstanceId";

    /// <summary>The ledger of USB instances whose records HIDMaestro caused:
    /// one subkey per instance, its value <c>InstanceId</c> the id. Outside
    /// the registry ledger's root, which its full release deletes.</summary>
    internal const string LedgerRoot = @"SOFTWARE\HIDMaestroUsbRecords";
    private const string V_InstanceId = "InstanceId";

    /// <summary>How long a removal waits, before any scale, for the device
    /// to leave the device tree after its unplug.</summary>
    internal const int LeaveWaitMs = 5_000;

    internal enum Outcome
    {
        /// <summary>Every record of the instance is gone.</summary>
        Removed,
        /// <summary>Windows had no record of the instance.</summary>
        Absent,
        /// <summary>The instance or a record below it is in the device tree,
        /// and nothing was removed.</summary>
        Present,
        /// <summary>A removal failed or the id is not a USB instance. What
        /// was not removed stays.</summary>
        Failed,
    }

    /// <summary>One removal's result: the outcome and the records it
    /// removed, deepest first.</summary>
    internal readonly record struct Result(Outcome Outcome, IReadOnlyList<string> Removed, string Detail)
    {
        /// <summary>True when nothing of the instance is left: the ledger
        /// entry that names it may go.</summary>
        public bool Settled => Outcome is Outcome.Removed or Outcome.Absent;
    }

    /// <summary>True when Windows keeps a record of the instance, present or
    /// not.</summary>
    internal static bool Exists(string instanceId) =>
        CM_Locate_DevNodeW(out _, instanceId, CM_LOCATE_DEVNODE_PHANTOM) == CR_SUCCESS;

    /// <summary>True when the instance is in the device tree.</summary>
    internal static bool Present(string instanceId) =>
        CM_Locate_DevNodeW(out _, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS;

    /// <summary>The instance names Windows keeps under
    /// <c>Enum\USB\VID_vvvv&amp;PID_pppp</c>, read before an attach so a
    /// device Windows names by its port can be told apart from the records
    /// that were there before (<see cref="CreatedBy"/>).</summary>
    internal static HashSet<string> Known(ushort vendorId, ushort productId)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\USB\VID_{vendorId:X4}&PID_{productId:X4}");
            if (k != null) set.UnionWith(k.GetSubKeyNames());
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        return set;
    }

    /// <summary>True when <paramref name="instanceId"/> is not among the
    /// instance names <paramref name="known"/> listed before the attach.</summary>
    internal static bool CreatedBy(string instanceId, HashSet<string> known) =>
        !known.Contains(instanceId.Substring(instanceId.LastIndexOf('\\') + 1));

    /// <summary>Record the USB instance a controller's device takes: in its
    /// owner entry, and in the ledger when the create caused its record.
    /// Written before the attach for a device Windows names by its serial,
    /// so a crash after the attach leaves the record named.</summary>
    internal static void Note(string? token, string instanceId, bool created)
    {
        if (token != null) MachineClaims.SetControllerValue(token, InstanceValue, instanceId);
        if (created) NoteCaused(instanceId);
    }

    /// <summary>A registry key name for an instance id, which carries
    /// backslashes a key name cannot.</summary>
    private static string EntryName(string instanceId) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(instanceId.ToUpperInvariant())))[..16];

    /// <summary>Add an instance to the ledger of records HIDMaestro
    /// caused.</summary>
    internal static void NoteCaused(string instanceId)
    {
        try
        {
            using var e = Registry.LocalMachine.CreateSubKey($@"{LedgerRoot}\{EntryName(instanceId)}");
            e.SetValue(V_InstanceId, instanceId, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            DeviceOrchestrator.LogDiag($"    usb records: {instanceId} was not noted: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The instances the ledger names.</summary>
    internal static List<string> Caused()
    {
        var list = new List<string>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(LedgerRoot);
            if (root == null) return list;
            foreach (string n in root.GetSubKeyNames())
            {
                using var e = root.OpenSubKey(n);
                if (e?.GetValue(V_InstanceId) is string id) list.Add(id);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        return list;
    }

    /// <summary>Drop an instance from the ledger, and the ledger's key once
    /// it names none.</summary>
    private static void Forget(string instanceId)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(LedgerRoot, writable: true);
            if (root == null) return;
            root.DeleteSubKeyTree(EntryName(instanceId), throwOnMissingSubKey: false);
            bool empty = root.SubKeyCount == 0 && root.ValueCount == 0;
            root.Close();
            if (empty) Registry.LocalMachine.DeleteSubKey(LedgerRoot, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            DeviceOrchestrator.LogDiag($"    usb records: {instanceId} stays in the ledger: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Remove the records of every instance the ledger names whose
    /// device has left, waiting up to <paramref name="leaveWaitMs"/> (already
    /// scaled) for each to leave, and drop each settled one from the ledger.
    /// One read of the machine's records serves them all. Returns how many
    /// stay. The full removal calls it once every import is detached.</summary>
    internal static int RemoveCaused(int leaveWaitMs)
    {
        var ids = Caused();
        if (ids.Count == 0) return 0;
        int left = 0;
        Tree? tree = null;
        foreach (string id in ids)
        {
            Result r;
            try
            {
                tree ??= Tree.Read();
                r = Remove(id, leaveWaitMs, tree);
            }
            catch (Exception e) { r = new Result(Outcome.Failed, Array.Empty<string>(), $"{e.GetType().Name}: {e.Message}"); }
            DeviceOrchestrator.LogDiag($"    usb records of {id}: {r.Outcome}, {r.Detail}");
            if (r.Settled) Forget(id);
            else left++;
        }
        return left;
    }

    /// <summary>Remove the records of a USB instance the ledger names, once
    /// the device has left the device tree, waiting up to
    /// <paramref name="leaveWaitMs"/> (already scaled) for it to leave. Under
    /// the PnP lock every devnode removal of the SDK takes.</summary>
    internal static Result Remove(string instanceId, int leaveWaitMs) => Remove(instanceId, leaveWaitMs, null);

    /// <summary>The same with the machine's records read once by the caller
    /// (<see cref="Tree.Read"/>), for a removal of many instances in a row.
    /// Presence is read live for each record all the same.</summary>
    internal static Result Remove(string instanceId, int leaveWaitMs, Tree? tree)
    {
        var removed = new List<string>();
        if (!IsUsbInstance(instanceId))
            return new Result(Outcome.Failed, removed, $"{instanceId} is not a USB device instance");
        var until = Stopwatch.StartNew();
        while (Present(instanceId) && until.ElapsedMilliseconds < leaveWaitMs) Thread.Sleep(25);
        if (Present(instanceId)) return new Result(Outcome.Present, removed, $"{instanceId} is in the device tree");
        if (!Exists(instanceId)) return new Result(Outcome.Absent, removed, $"{instanceId} has no record");

        // Each record below the instance must be out of the tree too: a
        // composite's interfaces and collections leave with it, and one that
        // has not yet left is waited for within the same bound.
        List<string> subtree = (tree ?? Tree.Read()).Subtree(instanceId);
        string? present;
        while ((present = subtree.FirstOrDefault(Present)) != null && until.ElapsedMilliseconds < leaveWaitMs) Thread.Sleep(25);
        if (present != null) return new Result(Outcome.Present, removed, $"{present}, below {instanceId}, is in the device tree");

        using var pnp = DeviceManager.HoldPnpLock();
        // The walk is read again under the lock: a device that came back
        // since stops the removal before anything goes.
        if (tree == null) subtree = Tree.Read().Subtree(instanceId);
        present = subtree.FirstOrDefault(Present);
        if (present != null) return new Result(Outcome.Present, removed, $"{present} is in the device tree again");
        // Deepest first: the walk lists each record after its parent.
        for (int i = subtree.Count - 1; i >= 0; i--)
        {
            string id = subtree[i];
            if (!Exists(id)) continue;
            if (!RemoveRecord(id, out int error) || Exists(id))
                return new Result(Outcome.Failed, removed, $"the record of {id} was not removed: error {error}");
            removed.Add(id);
        }
        DeviceOrchestrator.LogDiag($"    usb records: removed {removed.Count} under {instanceId}");
        return new Result(Outcome.Removed, removed, $"{removed.Count} record(s)");
    }

    /// <summary><c>USB\&lt;device id&gt;\&lt;instance&gt;</c>, the form the hub
    /// driver gives a device on a port. A removal of any other id is
    /// refused.</summary>
    internal static bool IsUsbInstance(string instanceId)
    {
        string[] parts = instanceId.Split('\\');
        return parts.Length == 3 && parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase)
            && parts[1].StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
            && !parts[1].StartsWith("ROOT_HUB", StringComparison.OrdinalIgnoreCase)
            && parts[2].Length > 0;
    }

    /// <summary>Every record of the machine by the parent Windows keeps for
    /// it: one walk of the device id list, which holds present and
    /// non-present devnodes alike.</summary>
    internal sealed class Tree
    {
        private readonly Dictionary<string, List<string>> _children = new(StringComparer.OrdinalIgnoreCase);

        internal static Tree Read()
        {
            var t = new Tree();
            foreach (string id in AllDeviceIds())
            {
                if (ParentOf(id) is not { } parent) continue;
                if (!t._children.TryGetValue(parent, out var list)) t._children[parent] = list = new List<string>();
                list.Add(id);
            }
            return t;
        }

        /// <summary>The instance and every record below it, each after its
        /// parent.</summary>
        internal List<string> Subtree(string instanceId) =>
            SubtreeOf(instanceId, p => _children.TryGetValue(p, out var l) ? l : Array.Empty<string>());
    }

    /// <summary>The breadth-first order <see cref="Subtree"/> returns, from
    /// a map of each record to its children, which
    /// registry_ownership_check's offline part feeds by hand. A record seen
    /// twice is listed once.</summary>
    internal static List<string> SubtreeOf(string root, Func<string, IReadOnlyList<string>> childrenOf)
    {
        var order = new List<string> { root };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        for (int i = 0; i < order.Count; i++)
            foreach (string c in childrenOf(order[i]))
                if (seen.Add(c)) order.Add(c);
        return order;
    }

    /// <summary>The parent Windows keeps for a devnode, present or not, or
    /// null when it has none to give.</summary>
    internal static string? ParentOf(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint dev, instanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS) return null;
        var key = DEVPKEY_Device_Parent;
        uint size = 0;
        uint cr = CM_Get_DevNode_PropertyW(dev, ref key, out uint type, null, ref size, 0);
        if (cr != CR_BUFFER_SMALL || size < 2) return null;
        var buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(dev, ref key, out type, buffer, ref size, 0) != CR_SUCCESS || type != DEVPROP_TYPE_STRING)
            return null;
        string s = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
        return s.Length > 0 ? s : null;
    }

    /// <summary>Every device instance id the PnP manager knows, present or
    /// not (CM_Get_Device_ID_List with no filter).</summary>
    internal static List<string> AllDeviceIds()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint len, null, CM_GETIDLIST_FILTER_NONE) != CR_SUCCESS) break;
            var buffer = new char[len];
            uint cr = CM_Get_Device_ID_ListW(null, buffer, len, CM_GETIDLIST_FILTER_NONE);
            if (cr == CR_BUFFER_SMALL) continue;
            if (cr != CR_SUCCESS) break;
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        return new List<string>();
    }

    /// <summary>SetupDiRemoveDevice on one record, opened by its id in a set
    /// of no class, which opens a non-present devnode of any class.</summary>
    private static bool RemoveRecord(string instanceId, out int error)
    {
        error = 0;
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) { error = Marshal.GetLastPInvokeError(); return false; }
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            bool ok = SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data)
                && SetupDiRemoveDevice(set, ref data);
            if (!ok) error = Marshal.GetLastPInvokeError();
            return ok;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private const uint CR_SUCCESS = 0x00;
    private const uint CR_BUFFER_SMALL = 0x1A;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0x0;
    private const uint CM_LOCATE_DEVNODE_PHANTOM = 0x1;
    private const uint CM_GETIDLIST_FILTER_NONE = 0x0;
    private const uint DEVPROP_TYPE_STRING = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    // devpkey.h: {4340a6c5-93fa-4706-972c-7b648008a5a7}, 8, a string.
    private static readonly DEVPROPKEY DEVPKEY_Device_Parent = new()
    {
        fmtid = new Guid(0x4340a6c5, 0x93fa, 0x4706, 0x97, 0x2c, 0x7b, 0x64, 0x80, 0x08, 0xa5, 0xa7),
        pid = 8,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static partial uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type,
        [Out] byte[]? buffer, ref uint size, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint length, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiOpenDeviceInfoW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr hwndParent, uint flags,
        ref SP_DEVINFO_DATA data);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiRemoveDevice(IntPtr set, ref SP_DEVINFO_DATA data);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
