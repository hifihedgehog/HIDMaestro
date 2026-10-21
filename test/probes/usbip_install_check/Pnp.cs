// What Windows did to the composite's devnodes while the probe read it: the
// chain from the HID interface up to the host controller, each node's last
// arrival and removal, whether device installation was pending, and the
// Kernel-PnP configuration events. Read directly, so the record does not
// rest on the SDK it is checking.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

internal static class Pnp
{
    const uint CR_SUCCESS = 0, CM_LOCATE_DEVNODE_PHANTOM = 1, DEVPROP_TYPE_FILETIME = 0x10, DEVPROP_TYPE_STRING = 0x12;
    const uint WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 0x102;

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    // devpkey.h (Windows SDK 10.0.26100.0): DEVPKEY_Device_InstanceId is
    // {78c34fc8-...},256, DEVPKEY_Device_LastArrivalDate {83da6326-...},102
    // and DEVPKEY_Device_LastRemovalDate {83da6326-...},103.
    static readonly DEVPROPKEY InstanceIdKey = new() { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };
    static readonly DEVPROPKEY LastArrivalKey = new() { fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"), pid = 102 };
    static readonly DEVPROPKEY LastRemovalKey = new() { fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"), pid = 103 };

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_PropertyW(string path, ref DEVPROPKEY key, out uint type, byte[]? buffer, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Locate_DevNodeW(out uint devInst, string? id, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_IDW(uint devInst, char[] buffer, int length, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[]? buffer, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
    // cfgmgr32.h defines CM_WaitNoPendingInstallEvents as this export.
    [DllImport("cfgmgr32.dll")]
    static extern uint CMP_WaitNoPendingInstallEvents(uint timeoutMs);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_ID_ListW(string? filter, char[] buffer, uint length, uint flags);

    // devpkey.h: DEVPKEY_Device_HardwareIds {a45c254e-...},3, a
    // DEVPROP_TYPE_STRING_LIST (devpropdef.h, 0x2012). cfgmgr32.h:
    // CM_GETIDLIST_FILTER_ENUMERATOR 1, CR_BUFFER_SMALL 0x1A.
    static readonly DEVPROPKEY HardwareIdsKey = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 3 };
    const uint DEVPROP_TYPE_STRING_LIST = 0x2012, CM_GETIDLIST_FILTER_ENUMERATOR = 1, CR_BUFFER_SMALL = 0x1A;

    /// <summary>The root-enumerated devnodes, present or not, with
    /// <paramref name="hardwareId"/> among their hardware IDs: what
    /// <c>pnputil /enum-devices /deviceid</c> lists on Windows 11, read on
    /// every Windows version (Windows 10's pnputil has no /deviceid). -1
    /// when the device list cannot be read.</summary>
    internal static int WithHardwareId(string hardwareId)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint length, "ROOT", CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS) return -1;
            var list = new char[length];
            uint cr = CM_Get_Device_ID_ListW("ROOT", list, length, CM_GETIDLIST_FILTER_ENUMERATOR);
            // A devnode that arrived between the two calls: read again.
            if (cr == CR_BUFFER_SMALL) continue;
            if (cr != CR_SUCCESS) return -1;
            int count = 0;
            foreach (string id in new string(list).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (CM_Locate_DevNodeW(out uint dev, id, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS) continue;
                var key = HardwareIdsKey;
                uint size = 0;
                CM_Get_DevNode_PropertyW(dev, ref key, out _, null, ref size, 0);
                if (size == 0) continue;
                var buf = new byte[size];
                if (CM_Get_DevNode_PropertyW(dev, ref key, out uint type, buf, ref size, 0) != CR_SUCCESS || type != DEVPROP_TYPE_STRING_LIST) continue;
                if (Encoding.Unicode.GetString(buf, 0, (int)size).Split('\0').Any(h => h.Equals(hardwareId, StringComparison.OrdinalIgnoreCase)))
                    count++;
            }
            return count;
        }
        return -1;
    }

    /// <summary>The root-enumerated devnodes with <paramref name="hardwareId"/>
    /// among their hardware IDs, present or not, each with all its hardware
    /// IDs in order.</summary>
    internal static List<(string Id, bool Present, string[] HardwareIds)> RootDevnodes(string hardwareId)
    {
        var found = new List<(string, bool, string[])>();
        foreach (string id in IdList("ROOT"))
        {
            string[] ids = HardwareIds(id);
            if (ids.Any(h => h.Equals(hardwareId, StringComparison.OrdinalIgnoreCase)))
                found.Add((id, CM_Locate_DevNodeW(out _, id, 0) == CR_SUCCESS, ids));
        }
        return found;
    }

    /// <summary>Every device instance id under an enumerator, present or
    /// not, or every one with a null enumerator.</summary>
    internal static List<string> IdList(string? enumerator)
    {
        uint flags = enumerator == null ? 0 : CM_GETIDLIST_FILTER_ENUMERATOR;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint length, enumerator, flags) != CR_SUCCESS) break;
            var list = new char[length];
            uint cr = CM_Get_Device_ID_ListW(enumerator, list, length, flags);
            if (cr == CR_BUFFER_SMALL) continue;
            if (cr != CR_SUCCESS) break;
            return new string(list).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        return new List<string>();
    }

    /// <summary>A devnode's hardware IDs, present or not.</summary>
    internal static string[] HardwareIds(string id)
    {
        if (CM_Locate_DevNodeW(out uint dev, id, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS) return Array.Empty<string>();
        var key = HardwareIdsKey;
        uint size = 0;
        CM_Get_DevNode_PropertyW(dev, ref key, out _, null, ref size, 0);
        if (size == 0) return Array.Empty<string>();
        var buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(dev, ref key, out uint type, buf, ref size, 0) != CR_SUCCESS || type != DEVPROP_TYPE_STRING_LIST)
            return Array.Empty<string>();
        return Encoding.Unicode.GetString(buf, 0, (int)size).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    // devpkey.h: DEVPKEY_Device_DriverInfPath {a8b865dd-...},5 and
    // DEVPKEY_Device_Parent {4340a6c5-...},8, both DEVPROP_TYPE_STRING.
    static readonly DEVPROPKEY DriverInfPathKey = new() { fmtid = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), pid = 5 };
    static readonly DEVPROPKEY ParentKey = new() { fmtid = new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"), pid = 8 };

    /// <summary>The published INF a devnode's driver came from, or null.</summary>
    internal static string? DriverInf(string id) => StringProperty(id, DriverInfPathKey);

    /// <summary>A devnode's parent, present or not, or null.</summary>
    internal static string? Parent(string id) => StringProperty(id, ParentKey);

    /// <summary>True when Windows holds a record of the devnode, present or
    /// not.</summary>
    internal static bool Known(string id) => CM_Locate_DevNodeW(out _, id, CM_LOCATE_DEVNODE_PHANTOM) == CR_SUCCESS;

    /// <summary>True when the devnode is in the tree.</summary>
    internal static bool Present(string id) => CM_Locate_DevNodeW(out _, id, 0) == CR_SUCCESS;

    static string? StringProperty(string id, DEVPROPKEY key)
    {
        if (CM_Locate_DevNodeW(out uint dev, id, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS) return null;
        uint size = 0;
        CM_Get_DevNode_PropertyW(dev, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(dev, ref key, out uint type, buf, ref size, 0) != CR_SUCCESS || type != DEVPROP_TYPE_STRING) return null;
        return Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
    }

    /// <summary>The devnode of a device interface, then each parent up to the
    /// host controller, a root-enumerated node, which ends the list.</summary>
    internal static List<string> Chain(string interfacePath)
    {
        var chain = new List<string>();
        var key = InstanceIdKey;
        uint size = 0;
        CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, null, ref size, 0);
        if (size == 0) return chain;
        var buf = new byte[size];
        if (CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out uint type, buf, ref size, 0) != CR_SUCCESS || type != DEVPROP_TYPE_STRING)
            return chain;
        string id = Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
        if (CM_Locate_DevNodeW(out uint dev, id, 0) != CR_SUCCESS) return chain;
        for (int depth = 0; depth < 8; depth++)
        {
            var name = new char[512];
            if (CM_Get_Device_IDW(dev, name, name.Length, 0) != CR_SUCCESS) break;
            string nodeId = new string(name).TrimEnd('\0');
            chain.Add(nodeId);
            if (nodeId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)) break;
            if (CM_Get_Parent(out uint parent, dev, 0) != CR_SUCCESS) break;
            dev = parent;
        }
        return chain;
    }

    /// <summary>One node as it stands: present or not, its status and
    /// problem, its last arrival and its last removal.</summary>
    internal readonly record struct Node(string Id, bool Present, uint Status, uint Problem, DateTime? Arrived, DateTime? Removed)
    {
        public override string ToString() =>
            $"{(Present ? $"present, status 0x{Status:X8} problem {Problem}" : "not present")}, " +
            $"arrived {Stamp(Arrived)}, removed {Stamp(Removed)}";
    }

    internal static Node Read(string id)
    {
        bool present = CM_Locate_DevNodeW(out uint dev, id, 0) == CR_SUCCESS;
        if (!present && CM_Locate_DevNodeW(out dev, id, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
            return new Node(id, false, 0, 0, null, null);
        uint status = 0, problem = 0;
        if (present) CM_Get_DevNode_Status(out status, out problem, dev, 0);
        return new Node(id, present, status, problem, FileTime(dev, LastArrivalKey), FileTime(dev, LastRemovalKey));
    }

    static DateTime? FileTime(uint dev, DEVPROPKEY key)
    {
        uint size = 8;
        var buf = new byte[8];
        if (CM_Get_DevNode_PropertyW(dev, ref key, out uint type, buf, ref size, 0) != CR_SUCCESS || type != DEVPROP_TYPE_FILETIME) return null;
        return DateTime.FromFileTimeUtc(BitConverter.ToInt64(buf, 0));
    }

    /// <summary>Whether the PnP manager has device installation pending,
    /// waiting at most <paramref name="timeoutMs"/> for it to have none
    /// (Microsoft Learn, CM_WaitNoPendingInstallEvents).</summary>
    internal static string InstallState(uint timeoutMs) => CMP_WaitNoPendingInstallEvents(timeoutMs) switch
    {
        WAIT_OBJECT_0 => "none pending",
        WAIT_TIMEOUT => "pending",
        uint other => $"unknown (0x{other:X})",
    };

    internal static string Stamp(DateTime? t) => t is DateTime v ? v.ToString("HH:mm:ss.fff") + "Z" : "never";

    /// <summary>The Kernel-PnP configuration events since
    /// <paramref name="sinceUtc"/> that name a device whose id holds one of
    /// <paramref name="match"/>: when, the event id, what happened, and the
    /// driver lines. Read with wevtutil, which opens no window.</summary>
    internal static List<string> Events(DateTime sinceUtc, IReadOnlyCollection<string> match,
                                        Func<string, int, string[], (int Exit, string Output)> run)
    {
        var found = new List<string>();
        var r = run("wevtutil.exe", 60_000, new[]
        {
            "qe", "Microsoft-Windows-Kernel-PnP/Configuration",
            $"/q:*[System[TimeCreated[@SystemTime>='{sinceUtc:yyyy-MM-ddTHH:mm:ss.fffZ}']]]", "/f:text",
        });
        if (r.Exit != 0) { found.Add($"wevtutil exited {r.Exit}: {r.Output.Trim()}"); return found; }

        // Each event is a block from "Event[n]" on: a Date line, an Event ID
        // line, and after "Description:" the sentence naming the device, then
        // its Driver and Status lines.
        string date = "", eventId = "", what = "";
        var extra = new List<string>();
        bool inDescription = false;
        void Flush()
        {
            if (what.Length > 0 && match.Any(m => what.Contains(m, StringComparison.OrdinalIgnoreCase)))
                found.Add($"{date} {eventId} {what}{(extra.Count > 0 ? " (" + string.Join(", ", extra) + ")" : "")}");
            date = eventId = what = "";
            extra.Clear();
            inDescription = false;
        }
        foreach (string raw in r.Output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string t = line.Trim();
            if (line.StartsWith("Event[", StringComparison.Ordinal)) { Flush(); continue; }
            if (!inDescription)
            {
                if (t.StartsWith("Date:", StringComparison.Ordinal)) date = t[5..].Trim();
                else if (t.StartsWith("Event ID:", StringComparison.Ordinal)) eventId = t[9..].Trim();
                else if (t.StartsWith("Description:", StringComparison.Ordinal)) inDescription = true;
                continue;
            }
            if (t.Length == 0) continue;
            if (what.Length == 0) { what = t; continue; }
            if (t.StartsWith("Driver Name:", StringComparison.Ordinal) || t.StartsWith("Driver Section:", StringComparison.Ordinal)
                || t.StartsWith("Status:", StringComparison.Ordinal) || t.StartsWith("Problem:", StringComparison.Ordinal)
                || t.StartsWith("Upper Filters:", StringComparison.Ordinal) || t.StartsWith("Device Updated:", StringComparison.Ordinal))
                extra.Add(t);
        }
        Flush();
        return found;
    }
}
