using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>
/// Who owns each HIDMaestro device on the machine right now. The sweeps act
/// on this and on nothing else.
///
/// <para>A present device belongs to the record that lists its identity
/// token or its transport index, a live record before a dead one. A record
/// is live while its owner claim is held (<see cref="MachineClaims"/>). A
/// dead record names a device only while no running program holds the
/// device's transport index or identity: an index or identity outlives the
/// program that had it, and a program started since, of any version, may
/// have taken it. A present device no record names was made by a program
/// before v1.13.0, or belongs to one started since:</para>
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

    /// <summary>Read the records and the present devices. Costs a read of
    /// the Enum keys' names under ROOT and SWD, a presence check per
    /// HIDMaestro devnode, and a registry read per present one.</summary>
    public static OwnerCensus Take()
    {
        var census = new OwnerCensus();
        census.Records.AddRange(MachineClaims.ReadAll());

        var byToken = new Dictionary<string, MachineClaims.OwnerRecord>(StringComparer.OrdinalIgnoreCase);
        var byIndex = new Dictionary<int, MachineClaims.OwnerRecord>();
        foreach (var r in census.Records)
            foreach (var c in r.Controllers)
            {
                if (!byToken.TryGetValue(c.Token, out var t) || (r.Live && !t.Live)) byToken[c.Token] = r;
                if (c.TransportIndex >= 0 && (!byIndex.TryGetValue(c.TransportIndex, out var i) || (r.Live && !i.Live)))
                    byIndex[c.TransportIndex] = r;
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
            if (rec is { Live: false } && HeldByLiveProgram(key, g.Index)) rec = null;
            Owner owner = rec != null
                ? rec.Self ? Owner.Self : rec.Live ? Owner.Live : Owner.Dead
                : ByLegacySigns(g.Index);
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

    /// <summary>The owner of one present devnode, by the same rules as
    /// <see cref="Take"/>.</summary>
    public static Owner Classify(string instanceId)
    {
        string name = instanceId.Substring(instanceId.LastIndexOf('\\') + 1);
        int index = ReadControllerIndex(instanceId);
        bool token = DeviceIdentity.IsIdentityToken(name);
        bool dead = false;
        foreach (var r in MachineClaims.ReadAll())
        {
            bool listed = r.Controllers.Any(c =>
                string.Equals(c.Token, name, StringComparison.OrdinalIgnoreCase)
                || (index >= 0 && c.TransportIndex == index && !token));
            if (!listed) continue;
            if (r.Live) return r.Self ? Owner.Self : Owner.Live;
            dead = true;
        }
        if (dead && !HeldByLiveProgram(token ? name : "#", index)) return Owner.Dead;
        return ByLegacySigns(index);
    }

    /// <summary>Whether a running program holds a transport index or an
    /// identity token now: its v1.12.0 owner pipe, which every release from
    /// v1.12.0 holds for a live controller, or this release's index or
    /// identity claim.</summary>
    private static bool HeldByLiveProgram(string key, int index)
    {
        if (index >= 0
            && (MachineClaims.IsHeld(MachineClaims.LegacyOwnerPipeName(index))
                || MachineClaims.IsHeld(MachineClaims.IndexPipeName(index))))
            return true;
        return !key.StartsWith('#') && MachineClaims.IsHeld(MachineClaims.IdentityPipeName(key));
    }

    /// <summary>The owner of a device no record names, from what a program
    /// before v1.13.0 leaves: its owner pipe while it runs, and the
    /// acknowledgement section its driver keeps after a v1.12.0 program
    /// ends.</summary>
    private static Owner ByLegacySigns(int index)
    {
        if (index < 0) return Owner.Unknown;
        if (MachineClaims.IsHeld(MachineClaims.LegacyOwnerPipeName(index))) return Owner.LiveLegacy;
        if (SectionExists($@"Global\HIDMaestroInputAck{index}")) return Owner.DeadLegacy;
        return Owner.Unknown;
    }

    /// <summary>Present ROOT and SWD devnodes HIDMaestro made: the
    /// enumerators only HIDMaestro uses, and the shared ones where the
    /// HardwareID proves it (issue #28).</summary>
    internal static List<string> PresentOwnedDevnodes() => PresentOwnedDevnodes(s_openEnumRoot);

    private static readonly Func<string, RegistryKey?> s_openEnumRoot =
        root => Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{root}");

    /// <summary>The same, walking the Enum keys <paramref name="openEnumRoot"/>
    /// opens for ROOT and SWD. The probe of S84 hands it keys it made
    /// unreadable or deleted.</summary>
    internal static List<string> PresentOwnedDevnodes(Func<string, RegistryKey?> openEnumRoot)
    {
        // The device ids and their instances come from the Enum keys, which
        // hold present and absent devnodes alike, and a normal
        // CM_Locate_DevNode finds only a present one. Measured on the bench,
        // a CM_Get_Device_ID_List call with CM_GETIDLIST_FILTER_PRESENT costs
        // 150 microseconds or more even when it lists nothing, against about
        // 45 for the ROOT key's names and 5 per presence check. Past 64
        // candidate instances, absent ones piled up by programs that never
        // swept, the walk goes back to the listings, whose cost does not
        // grow with them. So does a walk that cannot read a key.
        var candidates = new List<(string Id, bool Exclusive)>();
        if (!TryWalkEnum(openEnumRoot, candidates) || candidates.Count > 64) return PresentOwnedDevnodesListed();

        var list = new List<string>();
        foreach (var (id, exclusive) in candidates)
        {
            if (!exclusive && !DeviceManager.IsHidMaestroOwned(id)) continue;
            if (CM_Locate_DevNode(out _, id, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS) list.Add(id);
        }
        return list;
    }

    /// <summary>The census's own walk of HKLM's Enum keys, for S84: the
    /// fallback gives the listings' answer, so only this tells whether the
    /// walk itself still reads its keys.</summary>
    internal static bool TryWalkEnum(List<(string Id, bool Exclusive)> candidates) =>
        TryWalkEnum(s_openEnumRoot, candidates);

    /// <summary>The instances under ROOT and SWD of the device ids
    /// HIDMaestro uses. False when a key cannot be read: one the walk may
    /// not open, or one PnP deleted after the walk listed or opened it. A
    /// null from OpenSubKey can mean a resource failure as well as a key
    /// that is gone, so it counts too, and the listings decide.</summary>
    private static bool TryWalkEnum(Func<string, RegistryKey?> openEnumRoot, List<(string Id, bool Exclusive)> candidates)
    {
        try
        {
            foreach (string root in EnumRoots)
            {
                using var enumKey = openEnumRoot(root);
                if (enumKey == null) return false;
                foreach (string device in enumKey.GetSubKeyNames())
                {
                    bool exclusive = IsExclusiveEnumerator(device);
                    if (!exclusive && !IsSharedEnumerator(device)) continue;
                    using var deviceKey = enumKey.OpenSubKey(device);
                    if (deviceKey == null) return false;
                    foreach (string instance in deviceKey.GetSubKeyNames())
                        candidates.Add(($@"{root}\{device}\{instance}", exclusive));
                }
            }
            return true;
        }
        // OpenSubKey throws SecurityException on access denied. GetSubKeyNames
        // throws IOException on a deleted key (ERROR_KEY_DELETED) and
        // UnauthorizedAccessException on access denied (RegistryKey.cs,
        // Win32Error).
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static readonly string[] EnumRoots = { "ROOT", "SWD" };

    /// <summary>A device id only HIDMaestro uses.</summary>
    private static bool IsExclusiveEnumerator(string device) =>
        device.StartsWith("HIDMAESTRO", StringComparison.OrdinalIgnoreCase)
        || device.StartsWith("HMCOMPANION", StringComparison.OrdinalIgnoreCase);

    /// <summary>A device id HIDMaestro shares with other software, whose
    /// instances count when their hardware ids name HIDMaestro.</summary>
    private static bool IsSharedEnumerator(string device) =>
        device.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
        || device.Equals("XnaComposite", StringComparison.OrdinalIgnoreCase)
        || device.Equals("HID_IG_00", StringComparison.OrdinalIgnoreCase)
        || device.Equals("HIDCLASS", StringComparison.OrdinalIgnoreCase)
        || device.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase);

    /// <summary>The same devnodes from the configuration manager's list of
    /// present devices under each root.</summary>
    internal static List<string> PresentOwnedDevnodesListed()
    {
        var list = new List<string>();
        foreach (string root in EnumRoots)
        {
            foreach (string id in PresentDeviceIds(root))
            {
                string[] parts = id.Split('\\');
                if (parts.Length != 3) continue;
                bool exclusive = IsExclusiveEnumerator(parts[1]);
                if (exclusive || (IsSharedEnumerator(parts[1]) && DeviceManager.IsHidMaestroOwned(id)))
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
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
    private const uint CR_SUCCESS = 0, CR_BUFFER_SMALL = 0x1A;
    private const uint FILE_MAP_READ = 0x0004;

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List_Size(out uint length, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List(string filter, [Out] char[] buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "OpenFileMappingW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenFileMapping(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
