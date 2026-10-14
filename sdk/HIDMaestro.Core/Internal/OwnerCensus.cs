using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>
/// Who owns each HIDMaestro device on the machine right now. The sweeps act
/// on this and on nothing else.
///
/// <para>A present device belongs to the record that lists its identity
/// token or its transport index. A record is live while its owner claim is
/// held (<see cref="MachineClaims"/>). A present device no record lists was
/// made by a program before v1.13.0:</para>
/// <list type="bullet">
/// <item>its index's v1.12.0 owner pipe <c>HIDMaestroOwner&lt;N&gt;</c> is
/// held: a live v1.12.0 program;</item>
/// <item>no owner pipe, but the section <c>Global\HIDMaestroInputAck&lt;N&gt;</c>
/// exists: a v1.12.0 program that has exited. Only a v1.12.0 or later SDK
/// creates that section, the driver keeps it open while the device exists,
/// and the program's owner pipe closed when the program ended;</item>
/// <item>neither: a program older than v1.12.0, which leaves no sign of
/// whether it still runs. Unknown, and never removed on a guess.</item>
/// </list>
/// </summary>
internal sealed partial class OwnerCensus
{
    internal enum Owner { Self, Live, Dead, LiveLegacy, DeadLegacy, Unknown }

    /// <summary>One controller's present devnodes, grouped by identity
    /// token (or by index for devices older than identity tokens).</summary>
    internal sealed record DeviceGroup(string Key, int ControllerIndex, List<string> InstanceIds, Owner Owner,
        MachineClaims.OwnerRecord? Record);

    public List<MachineClaims.OwnerRecord> Records { get; } = new();
    public List<DeviceGroup> Devices { get; } = new();

    /// <summary>Transport indices of USB/IP imports in the SDK's port range
    /// whose server port no v1.13.0 record names: a program built on an
    /// older SDK whose composite has no devnode of its own to record the
    /// index. The bus id <c>1-&lt;N+1&gt;</c> carries it.</summary>
    public HashSet<int> ForeignImportIndices { get; } = new();

    public IEnumerable<MachineClaims.OwnerRecord> DeadRecords => Records.Where(r => !r.Live);
    public bool AnyUnknown => Devices.Any(d => d.Owner == Owner.Unknown);

    /// <summary>Nothing to reclaim: no dead record and no present device
    /// of a dead program.</summary>
    public bool NothingToReclaim =>
        !Records.Any(r => !r.Live) && !Devices.Any(d => d.Owner is Owner.Dead or Owner.DeadLegacy);

    /// <summary>Transport indices a present device or a live record uses,
    /// whoever owns it.</summary>
    public HashSet<int> IndicesInUse()
    {
        var set = new HashSet<int>(ForeignImportIndices);
        foreach (var d in Devices) if (d.ControllerIndex >= 0) set.Add(d.ControllerIndex);
        foreach (var r in Records)
            if (r.Live) foreach (var c in r.Controllers) if (c.TransportIndex >= 0) set.Add(c.TransportIndex);
        return set;
    }

    /// <summary>Read the records and the present devices. Costs one
    /// CM_Get_Device_ID_List call per enumerator root and a registry read
    /// per HIDMaestro devnode, with no walk of phantom records.</summary>
    public static OwnerCensus Take()
    {
        var census = new OwnerCensus();
        census.Records.AddRange(MachineClaims.ReadAll());

        var byToken = new Dictionary<string, MachineClaims.OwnerRecord>(StringComparer.OrdinalIgnoreCase);
        var byIndex = new Dictionary<int, MachineClaims.OwnerRecord>();
        foreach (var r in census.Records)
            foreach (var c in r.Controllers)
            {
                byToken[c.Token] = r;
                if (c.TransportIndex >= 0 && (r.Live || !byIndex.ContainsKey(c.TransportIndex))) byIndex[c.TransportIndex] = r;
            }

        var groups = new Dictionary<string, (int Index, List<string> Ids)>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in PresentOwnedDevnodes())
        {
            string name = id.Substring(id.LastIndexOf('\\') + 1);
            int index = ReadControllerIndex(id);
            string key = DeviceIdentity.IsIdentityToken(name) ? name : $"#index:{index}";
            if (!groups.TryGetValue(key, out var g)) g = (index, new List<string>());
            if (g.Index < 0) g.Index = index;
            g.Ids.Add(id);
            groups[key] = g;
        }

        foreach (var (key, g) in groups)
        {
            MachineClaims.OwnerRecord? rec = null;
            if (!key.StartsWith('#')) byToken.TryGetValue(key, out rec);
            if (rec == null && g.Index >= 0) byIndex.TryGetValue(g.Index, out rec);
            Owner owner;
            if (rec != null) owner = rec.Self ? Owner.Self : rec.Live ? Owner.Live : Owner.Dead;
            else if (g.Index < 0) owner = Owner.Unknown;
            else if (MachineClaims.IsHeld(MachineClaims.LegacyOwnerPipeName(g.Index))) owner = Owner.LiveLegacy;
            else if (SectionExists($@"Global\HIDMaestroInputAck{g.Index}")) owner = Owner.DeadLegacy;
            else owner = Owner.Unknown;
            census.Devices.Add(new DeviceGroup(key, g.Index, g.Ids, owner, rec));
        }

        var recordedPorts = new HashSet<int>();
        foreach (var r in census.Records)
            if (r.UsbipPort > 0) recordedPorts.Add(r.UsbipPort);
        foreach (var row in Usbip.VhciClient.GetImportedDevices())
        {
            if (row.Host != "127.0.0.1" || !int.TryParse(row.Service, out int port)) continue;
            if (port < Usbip.UsbipServer.BasePort || port >= Usbip.UsbipServer.BasePort + Usbip.UsbipServer.PortRange) continue;
            if (recordedPorts.Contains(port)) continue;
            if (row.BusId.StartsWith("1-", StringComparison.Ordinal) && int.TryParse(row.BusId.AsSpan(2), out int dev) && dev >= 1)
                census.ForeignImportIndices.Add(dev - 1);
        }
        return census;
    }

    /// <summary>Present ROOT and SWD devnodes HIDMaestro made: the
    /// enumerators only HIDMaestro uses, and the shared ones where the
    /// HardwareID proves it (issue #28).</summary>
    internal static List<string> PresentOwnedDevnodes()
    {
        var list = new List<string>();
        foreach (string root in new[] { "ROOT", "SWD" })
        {
            foreach (string id in PresentDeviceIds(root))
            {
                string[] parts = id.Split('\\');
                if (parts.Length != 3) continue;
                string enumerator = parts[1];
                bool exclusive = enumerator.StartsWith("HIDMAESTRO", StringComparison.OrdinalIgnoreCase)
                    || enumerator.StartsWith("HMCOMPANION", StringComparison.OrdinalIgnoreCase);
                bool shared = enumerator.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
                    || enumerator.Equals("XnaComposite", StringComparison.OrdinalIgnoreCase)
                    || enumerator.Equals("HID_IG_00", StringComparison.OrdinalIgnoreCase)
                    || enumerator.Equals("HIDCLASS", StringComparison.OrdinalIgnoreCase)
                    || enumerator.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase);
                if (exclusive || (shared && DeviceManager.IsHidMaestroOwned(id)))
                    list.Add(id);
            }
        }
        return list;
    }

    internal static int ReadControllerIndex(string instanceId)
    {
        try
        {
            using var dp = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters");
            return dp?.GetValue("ControllerIndex") is int ci ? ci : -1;
        }
        catch { return -1; }
    }

    /// <summary>Present device instance ids under one enumerator root.</summary>
    internal static List<string> PresentDeviceIds(string enumeratorRoot)
    {
        const uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (CM_Get_Device_ID_List_Size(out uint len, enumeratorRoot, flags) != CR_SUCCESS || len <= 1)
                return new List<string>();
            var buf = new char[len];
            uint cr = CM_Get_Device_ID_List(enumeratorRoot, buf, len, flags);
            if (cr == CR_BUFFER_SMALL) continue;   // a device arrived between the two calls
            if (cr != CR_SUCCESS) return new List<string>();
            return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        return new List<string>();
    }

    internal static bool SectionExists(string name)
    {
        IntPtr h = OpenFileMapping(FILE_MAP_READ, false, name);
        if (h == IntPtr.Zero) return false;
        CloseHandle(h);
        return true;
    }

    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
    private const uint CR_SUCCESS = 0, CR_BUFFER_SMALL = 0x1A;
    private const uint FILE_MAP_READ = 0x0004;

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List_Size(out uint length, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List(string filter, [Out] char[] buffer, uint length, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "OpenFileMappingW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenFileMapping(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
