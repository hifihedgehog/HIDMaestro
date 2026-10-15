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
/// held: a live v1.12.0 program. Any program can create that name, so
/// where the device's driver reports on the pipe
/// (<see cref="LegacyPipeLive"/>), a held pipe does not count once the
/// driver shows the program it watched is gone.</item>
/// <item>no such pipe, but the section <c>Global\HIDMaestroInputAck&lt;N&gt;</c>
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
            if (rec is { Live: false } && HeldByLiveProgram(key, g.Index, () => g.Ids)) rec = null;
            Owner owner = rec != null
                ? rec.Self ? Owner.Self : rec.Live ? Owner.Live : Owner.Dead
                : ByLegacySigns(g.Index, () => g.Ids);
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
        Func<IReadOnlyList<string>> members = () => GroupMembers(instanceId, name, token, index);
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
        if (dead && !HeldByLiveProgram(token ? name : "#", index, members)) return Owner.Dead;
        return ByLegacySigns(index, members);
    }

    /// <summary>The present devnodes <see cref="Take"/> groups with this
    /// one: those of its identity token, or, for a device older than
    /// identity tokens, those of its index.</summary>
    private static List<string> GroupMembers(string instanceId, string name, bool token, int index)
    {
        var ids = new List<string>();
        foreach (string id in PresentOwnedDevnodes())
        {
            string n = id.Substring(id.LastIndexOf('\\') + 1);
            bool same = token
                ? string.Equals(n, name, StringComparison.OrdinalIgnoreCase)
                : !DeviceIdentity.IsIdentityToken(n) && ReadControllerIndex(id) == index;
            if (same) ids.Add(id);
        }
        if (!ids.Contains(instanceId, StringComparer.OrdinalIgnoreCase)) ids.Add(instanceId);
        return ids;
    }

    /// <summary>Whether a running program holds a transport index or an
    /// identity token now: its v1.12.0 owner pipe, which every release from
    /// v1.12.0 holds for a live controller (<see cref="LegacyPipeLive"/>),
    /// or this release's index or identity claim.</summary>
    private static bool HeldByLiveProgram(string key, int index, Func<IReadOnlyList<string>> members)
    {
        if (index >= 0
            && (MachineClaims.IsHeld(MachineClaims.IndexPipeName(index))
                || LegacyPipeLive(index, members)))
            return true;
        return !key.StartsWith('#') && MachineClaims.IsHeld(MachineClaims.IdentityPipeName(key));
    }

    /// <summary>The owner of a device no record names, from what a program
    /// before v1.13.0 leaves: its owner pipe while it runs, and the
    /// acknowledgement section its driver keeps after a v1.12.0 program
    /// ends.</summary>
    private static Owner ByLegacySigns(int index, Func<IReadOnlyList<string>> members)
    {
        if (index < 0) return Owner.Unknown;
        if (LegacyPipeLive(index, members)) return Owner.LiveLegacy;
        if (SectionExists($@"Global\HIDMaestroInputAck{index}")) return Owner.DeadLegacy;
        return Owner.Unknown;
    }

    /// <summary>Whether the v1.12.0 owner pipe of <paramref name="index"/>
    /// shows a running program. A v1.12.0 program creates that name
    /// unprotected, so any program can create it too. The device's own
    /// driver is the pipe's client, and from v1.13.0 it records the server
    /// it is connected to (driver.c RecordOwnerConnected). A held pipe stops
    /// counting only on proof that the program the driver watched is gone:
    /// the watch has ended, or the server it named no longer runs
    /// (<see cref="CheckServer"/>). A watch connected to a server that runs
    /// keeps the pipe counting, trusted or not: a program that made the
    /// name first can leave a v1.12.0 program's watch on its own pipe, and
    /// that proves nothing about the program writing the device's sections.
    /// Where no driver recorded anything, one older than v1.13.0 or one
    /// whose watch has not connected yet, the held pipe counts only beside
    /// the index's input section and an acknowledgement section that does
    /// not carry the driver's ended marker.
    /// <paramref name="members"/> gives the device's devnodes and is called
    /// only when the pipe is held.</summary>
    private static bool LegacyPipeLive(int index, Func<IReadOnlyList<string>> members)
    {
        if (!MachineClaims.IsHeld(MachineClaims.LegacyOwnerPipeName(index))) return false;
        // The driver sets the ended marker before it writes OwnerConnected 0,
        // so the marker is read after the values, never before: a marker
        // read first could be clear only because the watch ended in between.
        bool ackRead = false;
        bool? ackEnded = null;
        bool? AckEnded()
        {
            if (!ackRead) { ackEnded = AckWatchEnded(index); ackRead = true; }
            return ackEnded;
        }
        string? gone = null;
        foreach (string id in members())
        {
            OwnerWatch? w = ReadOwnerWatch(id);
            if (w == null) continue;
            if (!w.Connected)
            {
                // The driver marks the acknowledgement section before it
                // writes OwnerConnected 0. A clear marker beside that value
                // means a program has zeroed the section since, so the
                // value describes the program before it and the rule below
                // decides.
                if (AckEnded() == false) continue;
                gone ??= "the watch ended";
                continue;
            }
            ServerCheck c = CheckServer(w);
            if (c.Alive) return true;
            gone ??= $"server {w.ProcessId} no longer runs";
        }
        if (gone != null)
        {
            DeviceOrchestrator.LogDiag($"    the v1.12.0 owner pipe of transport index {index} is not a running program's: {gone}");
            return false;
        }
        // No v1.13.0 driver reported on the pipe (an older driver, or a
        // device added again before its watch reconnected). A running
        // program creates Global\HIDMaestroInput<N> before its owner pipe,
        // and a program that is not elevated cannot create a Global\
        // section, so the section's presence is the one signal left that a
        // real program, not a squatter holding only the pipe name, is on
        // the index.
        if (!SectionExists($@"Global\HIDMaestroInput{index}"))
        {
            DeviceOrchestrator.LogDiag($"    the v1.12.0 owner pipe of transport index {index} is held with no input section: a squatter, not a running program");
            return false;
        }
        // Every SDK that makes the owner pipe makes the acknowledgement
        // section beside the input section and zeroes it. A program that is
        // not elevated can neither create it nor write it. With no section,
        // or with the ended marker set (a v1.13.0 driver saw the watch end
        // and no program has started on the index since), the pipe and the
        // input section are a squatter's hold on a dead program's objects.
        bool? ended = AckEnded();
        if (ended != false)
        {
            DeviceOrchestrator.LogDiag(ended == null
                ? $"    the v1.12.0 owner pipe of transport index {index} is held with no acknowledgement section: a squatter, not a running program"
                : $"    the v1.12.0 owner pipe of transport index {index} is held after the driver saw its watch end, and no program has started on it since");
            return false;
        }
        return true;
    }

    /// <summary>Whether the driver marked the owner watch of
    /// <paramref name="index"/> ended in Global\HIDMaestroInputAck&lt;N&gt;
    /// (SharedMemoryIO.INPUT_ACK_WATCH_ENDED), or null when that section
    /// does not exist. Everyone may read the section.</summary>
    private static bool? AckWatchEnded(int index)
    {
        IntPtr h = OpenFileMapping(FILE_MAP_READ, false, $@"Global\HIDMaestroInputAck{index}");
        if (h == IntPtr.Zero) return null;
        try
        {
            IntPtr view = MapViewOfFile(h, FILE_MAP_READ, 0, 0, (nuint)(SharedMemoryIO.INPUT_ACK_OFFSET_CAPS + 4));
            if (view == IntPtr.Zero) return null;
            try { return (Marshal.ReadInt32(view, SharedMemoryIO.INPUT_ACK_OFFSET_CAPS) & SharedMemoryIO.INPUT_ACK_WATCH_ENDED) != 0; }
            finally { UnmapViewOfFile(view); }
        }
        finally { CloseHandle(h); }
    }

    /// <summary>What a device's driver recorded about its owner watch:
    /// connected or not, while connected the server's process id and the
    /// pipe's owner SID, and when the values were last written.</summary>
    internal sealed record OwnerWatch(bool Connected, int ProcessId, string PipeOwner, DateTime WrittenUtc);

    /// <summary>The server a driver named, judged now. Alive: a process
    /// with that id runs and was created before the driver wrote the
    /// values, so it is the server the driver saw and not one that took
    /// over its id, or a process exists that this one may not open.
    /// Elevated: its token's TokenElevation, null when the token cannot be
    /// read. Trusted: alive, elevated, and an elevated token made the pipe,
    /// which the pipe's owner shows. Administrators or SYSTEM own what an
    /// elevated token makes, unless policy makes administrators own their
    /// objects themselves. Then the server's token must own it, as it does
    /// a pipe it made, and a pipe a thread of that process made while
    /// impersonating another token does not pass. Trusted is reported, not
    /// acted on (<see cref="LegacyPipeLive"/>).</summary>
    internal readonly record struct ServerCheck(bool Alive, bool? Elevated, bool Trusted);

    /// <summary>The values a device's driver writes in its own key, or null
    /// when it wrote none. A UMDF driver may write only its service's
    /// subkey of Device Parameters (WdfDeviceOpenRegistryKey,
    /// WDF_REGKEY_DEVICE_SUBKEY).</summary>
    internal static OwnerWatch? ReadOwnerWatch(string instanceId)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters\{OwnerWatchSubkey}");
            if (k?.GetValue("OwnerConnected") is not int connected) return null;
            if (connected == 0) return new OwnerWatch(false, 0, "", default);
            if (k.GetValue("OwnerProcessId") is not int pid || k.GetValue("OwnerPipeOwner") is not string owner) return null;
            // The driver writes OwnerConnected last, so the key's last write
            // is the connect.
            if (RegQueryInfoKey(k.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out long written) != 0)
                return null;
            // A value only a KEY_SET_VALUE holder (the driver host or an
            // administrator) can plant could be out of range. Treat an
            // unreadable write time as no values, never a throw. With no
            // values a held pipe counts only beside the program's input
            // section (LegacyPipeLive).
            if (FileTimeToUtc(written) is not DateTime writtenUtc) return null;
            return new OwnerWatch(true, pid, owner, writtenUtc);
        }
        // OpenSubKey throws SecurityException on access denied, GetValue
        // IOException on a deleted key and UnauthorizedAccessException on
        // access denied (RegistryKey.cs, Win32Error).
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>The service's subkey of Device Parameters: the UmdfService
    /// name in hidmaestro.inf.</summary>
    private const string OwnerWatchSubkey = "HIDMaestro";

    internal static ServerCheck CheckServer(OwnerWatch w)
    {
        bool privilegedOwner = w.PipeOwner is LocalSystemSid or AdministratorsSid;
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)w.ProcessId);
        if (process == IntPtr.Zero)
        {
            // An id no process has fails with ERROR_INVALID_PARAMETER.
            if (Marshal.GetLastPInvokeError() != ERROR_ACCESS_DENIED)
                return new ServerCheck(false, null, false);
            // A process with that id exists but denies this one a handle.
            // Read its creation time without a handle, so a process that
            // reused the id and cannot be opened does not pass as the server.
            // Where the process list cannot be read, the process counts as
            // running, as it did before this release: an unreadable list
            // must not get a running program's devices removed. Its token,
            // hence elevation, cannot be read.
            DateTime? listed = CreationTimeByScan((uint)w.ProcessId, out bool listRead);
            bool deniedAlive = !listRead || (listed is DateTime dc && dc <= w.WrittenUtc);
            return new ServerCheck(deniedAlive, null, false);
        }
        try
        {
            bool alive = GetExitCodeProcess(process, out uint code) && code == STILL_ACTIVE
                && GetProcessTimes(process, out long created, out _, out _, out _)
                && FileTimeToUtc(created) is DateTime c && c <= w.WrittenUtc;
            bool? elevated = null;
            string? tokenOwner = null;
            if (OpenProcessToken(process, TOKEN_QUERY, out IntPtr token))
            {
                try
                {
                    if (GetTokenInformation(token, TokenElevation, out int isElevated, sizeof(int), out _))
                        elevated = isElevated != 0;
                    tokenOwner = TokenOwnerSid(token);
                }
                finally { CloseHandle(token); }
            }
            bool trusted = alive && elevated == true
                && (privilegedOwner || string.Equals(tokenOwner, w.PipeOwner, StringComparison.OrdinalIgnoreCase));
            return new ServerCheck(alive, elevated, trusted);
        }
        finally { CloseHandle(process); }
    }

    /// <summary>A FILETIME in UTC, or null when it is zero or out of the
    /// range <see cref="DateTime.FromFileTimeUtc"/> accepts, so a planted or
    /// garbage value is a miss, never a throw.</summary>
    private static DateTime? FileTimeToUtc(long fileTime)
    {
        if (fileTime <= 0) return null;
        try { return DateTime.FromFileTimeUtc(fileTime); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>The creation time of the process with <paramref name="pid"/>
    /// read from the system process list, for when its handle cannot be
    /// opened. Null when no such process is listed. <paramref name="read"/>
    /// is false when the list could not be read: in a 32-bit process, whose
    /// layout the offsets below do not describe, or when the query fails.
    /// Used only on the access-denied path, which is rare, so the full-list
    /// query is acceptable there.</summary>
    private static DateTime? CreationTimeByScan(uint pid, out bool read)
    {
        read = false;
        if (IntPtr.Size != 8) return null;
        int len = 512 * 1024;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                uint status = NtQuerySystemInformation(SystemProcessInformation, buf, (uint)len, out uint needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    len = (int)Math.Max((uint)len * 2, needed + 64 * 1024);
                    continue;
                }
                if (status != 0) return null;
                read = true;
                int offset = 0;
                while (true)
                {
                    IntPtr entry = buf + offset;
                    int next = Marshal.ReadInt32(entry);                        // NextEntryOffset
                    nint entryPid = Marshal.ReadIntPtr(entry, ProcessIdOffset);  // UniqueProcessId
                    if ((uint)entryPid == pid)
                        return FileTimeToUtc(Marshal.ReadInt64(entry, CreateTimeOffset));
                    if (next == 0) return null;
                    offset += next;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return null;
    }

    /// <summary>A token's default owner SID as text, or null.</summary>
    private static string? TokenOwnerSid(IntPtr token)
    {
        GetTokenInformation(token, TokenOwner, IntPtr.Zero, 0, out int length);
        if (length <= 0) return null;
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenOwner, buffer, length, out _)) return null;
            if (!ConvertSidToStringSid(Marshal.ReadIntPtr(buffer), out IntPtr text)) return null;
            try { return Marshal.PtrToStringUni(text); }
            finally { LocalFree(text); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>A device group's owner watch in one token, for the
    /// diagnostic log and the coexistence probe: none, gone, or connected
    /// with the server and what <see cref="CheckServer"/> found.</summary>
    internal static string DescribeOwnerWatch(DeviceGroup group) => DescribeWatch(group.InstanceIds);

    private static string DescribeWatch(IReadOnlyList<string> instanceIds)
    {
        foreach (string id in instanceIds)
        {
            OwnerWatch? w = ReadOwnerWatch(id);
            if (w == null) continue;
            if (!w.Connected) return "gone";
            ServerCheck c = CheckServer(w);
            return $"connected,pid={w.ProcessId},owner={w.PipeOwner},alive={(c.Alive ? 1 : 0)}," +
                   $"elevated={(c.Elevated is bool e ? (e ? "1" : "0") : "?")},trusted={(c.Trusted ? 1 : 0)}";
        }
        return "none";
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

    private const string LocalSystemSid = "S-1-5-18", AdministratorsSid = "S-1-5-32-544";
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x0008, STILL_ACTIVE = 259;
    private const uint SystemProcessInformation = 5, STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
    // SYSTEM_PROCESS_INFORMATION field offsets in a 64-bit process (x64 and
    // ARM64 share the LLP64 layout): CreateTime at 0x20, UniqueProcessId at
    // 0x50. A 32-bit process does not read the list (CreationTimeByScan).
    private const int CreateTimeOffset = 0x20, ProcessIdOffset = 0x50;
    private const int ERROR_ACCESS_DENIED = 5, TokenOwner = 4, TokenElevation = 20;
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(IntPtr view);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertSidToStringSid(IntPtr sid, out IntPtr text);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("ntdll.dll")]
    private static partial uint NtQuerySystemInformation(uint infoClass, IntPtr info, uint length, out uint returnLength);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW")]
    private static partial int RegQueryInfoKey(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, IntPtr className, IntPtr classLength,
        IntPtr reserved, IntPtr subKeys, IntPtr maxSubKeyLength, IntPtr maxClassLength, IntPtr values, IntPtr maxValueNameLength,
        IntPtr maxValueLength, IntPtr securityDescriptorLength, out long lastWriteTime);
}
