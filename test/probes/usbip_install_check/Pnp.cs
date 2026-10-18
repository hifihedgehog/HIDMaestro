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
