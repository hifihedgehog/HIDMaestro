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
/// token, or, for a device older than identity tokens, its transport index,
/// a live record before a dead one. A record
/// is live while its owner claim is held (<see cref="MachineClaims"/>). A
/// dead record names a device only while no running program holds the
/// device's transport index or identity: an index or identity outlives the
/// program that had it, and a program started since, of any version, may
/// have taken it. A present device no record names was made by a program
/// before v1.13.0, or belongs to one started since. A program on this
/// release takes its claims before its record entry, and the census reads
/// the records before it lists the devices, so a device whose index or
/// identity claim is held counts as a running program's whether a record
/// names it or not. Otherwise:</para>
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
    /// token (or by index for devices older than identity tokens).
    /// <see cref="IndexRead"/> says why <see cref="ControllerIndex"/> is -1
    /// when it is.</summary>
    internal sealed record DeviceGroup(string Key, int ControllerIndex, List<string> InstanceIds, Owner Owner,
        MachineClaims.OwnerRecord? Record)
    {
        public IndexRead IndexRead { get; init; } = IndexRead.Read;
    }

    /// <summary>What <see cref="ReadControllerIndex(string, Func{string, RegistryKey?}, out IndexRead)"/>
    /// found in a devnode's Device Parameters: the value, a key without a
    /// DWORD ControllerIndex, no key, or a key this process may not open or
    /// read. Token: the value could not be read, and the index came from the
    /// devnode's name, a default identity's token (HM_&lt;index&gt;, every
    /// release from v1.8.0), which names its index by construction.</summary>
    internal enum IndexRead { Read, NoValue, NoKey, Unreadable, Token }

    public List<MachineClaims.OwnerRecord> Records { get; } = new();
    public List<DeviceGroup> Devices { get; } = new();

    /// <summary>Transport indices of USB/IP imports in the SDK's port range
    /// whose server port no v1.13.0 record names: a program built on an
    /// older SDK whose composite has no devnode of its own to record the
    /// index. The bus id <c>1-&lt;N+1&gt;</c> carries it.</summary>
    public HashSet<int> ForeignImportIndices { get; } = new();

    /// <summary>A present device no record names has no readable index,
    /// and a v1.12.0 program runs on an index below
    /// <see cref="ControllerLease.LegacyClearance"/>. Either a registry
    /// ledger holder on that index names a running process no live record
    /// names (<see cref="RegistryLedger.LiveHolders"/>), on any index, one
    /// this release also holds included, or, on an index no live record and
    /// no present device's index accounts for, its owner pipe is held beside
    /// its input section and an acknowledgement section without the
    /// driver's ended marker, the evidence <see cref="LegacyPipeLive"/>
    /// takes when no driver reports on the pipe. That program's device can
    /// be the one whose index cannot be read. Tested only while such a
    /// device is present.</summary>
    public bool UnplacedLegacyProgram { get; private set; }

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
        // Both holder reads share one read of the process list, needed only
        // for a holder's process that refuses the open.
        var processList = RegistryLedger.ReadOnce(RegistryLedger.ProcessCreateTimes);
        var census = Build(PresentOwnedDevnodes(), s_openDeviceParameters, MachineClaims.ReadAll(), LegacyProgramOn,
            () => RegistryLedger.LiveHolders(Registry.LocalMachine, RegistryLedger.Root, processList), MachineClaims.ReadAll);

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

    /// <summary><see cref="Take"/>'s records and device groups from what it
    /// is handed: the present devnodes, the opener of their Device
    /// Parameters, the owner records, the test of whether a v1.12.0 program
    /// runs on a transport index (<see cref="LegacyProgramOn"/>), the ledger
    /// holders whose process runs (<see cref="RegistryLedger.LiveHolders"/>),
    /// read before and after the records, and that read of the records. The coexistence probe's
    /// OF13 hands it devnodes, keys, holders and records of its own.</summary>
    internal static OwnerCensus Build(IEnumerable<string> presentIds, Func<string, RegistryKey?> openDeviceParameters,
        IEnumerable<MachineClaims.OwnerRecord> records, Func<int, bool> legacyProgramOn,
        Func<IEnumerable<(int Pid, int Index)>> ledgerHolders, Func<IEnumerable<MachineClaims.OwnerRecord>> recordsNow)
    {
        var census = new OwnerCensus();
        census.Records.AddRange(records);

        var byToken = new Dictionary<string, MachineClaims.OwnerRecord>(StringComparer.OrdinalIgnoreCase);
        var byIndex = new Dictionary<int, MachineClaims.OwnerRecord>();
        foreach (var r in census.Records)
            foreach (var c in r.Controllers)
            {
                if (!byToken.TryGetValue(c.Token, out var t) || (r.Live && !t.Live)) byToken[c.Token] = r;
                if (c.TransportIndex >= 0 && (!byIndex.TryGetValue(c.TransportIndex, out var i) || (r.Live && !i.Live)))
                    byIndex[c.TransportIndex] = r;
            }

        var groups = new Dictionary<string, (int Index, IndexRead Read, List<string> Ids)>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in presentIds)
        {
            string name = id.Substring(id.LastIndexOf('\\') + 1);
            int index = CensusIndex(id, name, openDeviceParameters, out IndexRead read);
            string key = DeviceIdentity.IsIdentityToken(name) ? name : $"#index:{index}";
            if (!groups.TryGetValue(key, out var g)) g = (index, read, new List<string>());
            if (g.Index < 0) (g.Index, g.Read) = (index, read);
            g.Ids.Add(id);
            groups[key] = g;
        }

        foreach (var (key, g) in groups)
        {
            // A device with an identity token belongs to the record that
            // lists the token, and one older than identity tokens to the
            // record that lists its index, as in Classify. A token no record
            // lists is another program's, a v1.12.0 program's or an older
            // one's, even when it shares a recorded index: a running v1.12.0
            // program takes indices without looking at other programs.
            MachineClaims.OwnerRecord? rec = null;
            if (!key.StartsWith('#')) byToken.TryGetValue(key, out rec);
            else if (g.Index >= 0) byIndex.TryGetValue(g.Index, out rec);
            Owner owner;
            if (rec is { Live: true }) owner = rec.Self ? Owner.Self : Owner.Live;
            else
            {
                owner = WithoutLiveRecord(key, g.Index, dead: rec != null, () => g.Ids);
                if (owner != Owner.Dead) rec = null;
            }
            if (g.Read != IndexRead.Read)
                DeviceOrchestrator.LogDiag($"    transport index {g.Index} for {string.Join(", ", g.Ids)}: {Describe(g.Read)}, read as {owner}");
            census.Devices.Add(new DeviceGroup(key, g.Index, g.Ids, owner, rec) { IndexRead = g.Read });
        }

        // A device whose index cannot be read cannot be tied to its owner
        // pipe. Whether its program runs is read from the indices a v1.12.0
        // program reaches that nothing else accounts for. A present device's
        // own index was judged with its group, through its driver's report
        // where there is one, and this release's own controllers hold the
        // same pipe name on the indices their live records list.
        if (census.Devices.Any(d => d.Owner == Owner.Unknown && d.ControllerIndex < 0))
        {
            // A v1.12.0 program holds a ledger entry for each controller
            // that is not a keyboard or a mouse, written before its devnode,
            // in the same form as this release's. Its process is in no live
            // record, which tells it from this release's program even on an
            // index both hold, where every named object carries the same
            // name. The holders are read, then the records, then the holders
            // again, and a holder counts only when both reads have it. This
            // release writes its record before any holder, so a program of
            // this release whose holder the first read has is in the
            // records. Its exit deletes its record only after its holders
            // are gone (OwnerSweep.ExitRecordMayGo), so one whose
            // record the census missed has no holder in the second read.
            var first = ledgerHolders().Where(h => h.Index >= 0 && h.Index < ControllerLease.LegacyClearance).ToHashSet();
            if (first.Count > 0)
            {
                var livePids = new HashSet<int>(recordsNow().Where(r => r.Live).Select(r => r.Pid));
                var holders = ledgerHolders().Where(first.Contains).ToList();
                foreach (var h in holders)
                {
                    if (livePids.Contains(h.Pid)) continue;
                    census.UnplacedLegacyProgram = true;
                    DeviceOrchestrator.LogDiag($"    a v1.12.0 program, process {h.Pid}, holds transport index {h.Index} in the registry ledger, beside a device whose index cannot be read");
                    return census;
                }
            }

            var accounted = new HashSet<int>();
            foreach (var d in census.Devices) if (d.ControllerIndex >= 0) accounted.Add(d.ControllerIndex);
            foreach (var r in census.Records)
                if (r.Live) foreach (var c in r.Controllers) accounted.Add(c.TransportIndex);
            for (int n = 0; n < ControllerLease.LegacyClearance; n++)
            {
                if (accounted.Contains(n) || !legacyProgramOn(n)) continue;
                census.UnplacedLegacyProgram = true;
                DeviceOrchestrator.LogDiag($"    a v1.12.0 program runs on transport index {n}, beside a device whose index cannot be read");
                break;
            }
        }
        return census;
    }

    /// <summary>Whether a v1.12.0 program runs on <paramref name="index"/>,
    /// read where no driver reports on its owner pipe: the pipe held, the
    /// input section a running program creates before it, and an
    /// acknowledgement section without the driver's ended marker, as in
    /// <see cref="LegacyPipeLive"/>. A program that is not elevated can make
    /// the pipe name but neither section.</summary>
    private static bool LegacyProgramOn(int index) =>
        MachineClaims.IsHeld(MachineClaims.LegacyOwnerPipeName(index))
        && SectionExists($@"Global\HIDMaestroInput{index}")
        && AckWatchEnded(index) == false;

    /// <summary>The owner of one present devnode, by the same rules as
    /// <see cref="Take"/>.</summary>
    public static Owner Classify(string instanceId)
    {
        string name = instanceId.Substring(instanceId.LastIndexOf('\\') + 1);
        int index = CensusIndex(instanceId, name, s_openDeviceParameters, out _);
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
        return WithoutLiveRecord(token ? name : "#", index, dead, members);
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
                : !DeviceIdentity.IsIdentityToken(n) && CensusIndex(id, n, s_openDeviceParameters, out _) == index;
            if (same) ids.Add(id);
        }
        if (!ids.Contains(instanceId, StringComparer.OrdinalIgnoreCase)) ids.Add(instanceId);
        return ids;
    }

    /// <summary>The owner of a device no live record names, with
    /// <paramref name="dead"/> true when a dead record names it. A running
    /// program holds the device's index or identity now through its v1.12.0
    /// owner pipe, which every release from v1.12.0 holds for a live
    /// controller (<see cref="LegacyPipeLive"/>), or through this release's
    /// index or identity claim, which a create takes before its record
    /// entry and a sweep holds while it removes the device. Otherwise the
    /// dead record decides, and without one what a program before v1.13.0
    /// leaves: the acknowledgement section its driver keeps after a v1.12.0
    /// program ends.</summary>
    private static Owner WithoutLiveRecord(string key, int index, bool dead, Func<IReadOnlyList<string>> members)
    {
        if (index >= 0 && LegacyPipeLive(index, members)) return Owner.LiveLegacy;
        if ((index >= 0 && MachineClaims.IsHeld(MachineClaims.IndexPipeName(index)))
            || (!key.StartsWith('#') && MachineClaims.IsHeld(MachineClaims.IdentityPipeName(key))))
            return Owner.Live;
        if (dead) return Owner.Dead;
        if (index >= 0 && SectionExists($@"Global\HIDMaestroInputAck{index}")) return Owner.DeadLegacy;
        return Owner.Unknown;
    }

    /// <summary><see cref="LegacyPipeLive"/> for a group a sweep is about to
    /// act on: whether a running v1.12.0 program holds the index now.</summary>
    internal static bool LegacyOwnerRuns(int index, IReadOnlyList<string> instanceIds) =>
        index >= 0 && LegacyPipeLive(index, () => instanceIds);

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
        // so the marker is read after the value it explains, never before:
        // a marker read first could be clear only because the watch ended
        // in between. Each read stands for the values read before it, so
        // it is never reused for a value read later.
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
                if (AckWatchEnded(index) == false) continue;
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
        bool? ended = AckWatchEnded(index);
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

    /// <summary>How many transport indices the legacy-table check probes
    /// for an input section. v1.11.0 takes the lowest index its own context
    /// has free. A create of this release needs no probe: its record entry,
    /// written before its sections, accounts for its index.</summary>
    private const int SectionProbeLimit = 128;

    /// <summary>The GameInput key names (<see cref="GameInputMapping.KeyName"/>)
    /// that a device or a create in flight may read through, or null when a
    /// create in flight of unknown identity means every table must stay.
    ///
    /// <para>A key is in use when a present devnode's hardware ids carry its
    /// VID and PID, or any <c>Controller&lt;N&gt;</c> configuration names
    /// them, present device or not. The configuration's VID and PID can
    /// differ from the devnode's for an xinputhid persona whose driver PID
    /// is its own (DeviceNodeCreator). v1.11.0 writes the table first in its
    /// create, after its input section and before its configuration
    /// (DeviceOrchestrator.SetupController steps 0 and 1 at v1.11.0). So an
    /// input section whose index has no configuration and no present device
    /// is a create whose identity cannot be read yet, and so is a
    /// configuration that names no VID and PID yet.</para>
    ///
    /// <para>A section another program explains is no such create: an
    /// index a live record lists (this release writes the entry before the
    /// sections, and a composite on USB/IP writes no configuration), a
    /// USB/IP import no record names (a composite of an older program), and
    /// an index whose <c>Global\HIDMaestroInputAck&lt;N&gt;</c> exists. An
    /// SDK of v1.12.0 or later creates that section beside its input
    /// section and v1.11.0 never does, and from a desktop session only a
    /// process holding SeCreateGlobalPrivilege, an elevated program or a
    /// service, can create a section under Global\, so a program that is not
    /// elevated cannot make a v1.11.0 create look explained. The v1.12.0
    /// owner pipe is no such sign: any program can create that name. None of these
    /// hides a v1.11.0 create except one on another program's index, which
    /// v1.11.0 breaks anyway.</para></summary>
    internal static HashSet<string>? GameInputKeysInUse(OwnerCensus census)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accounted = new HashSet<int>(census.ForeignImportIndices);
        foreach (var r in census.Records)
            if (r.Live)
                foreach (var c in r.Controllers)
                    if (c.TransportIndex >= 0) accounted.Add(c.TransportIndex);
        foreach (var g in census.Devices)
        {
            if (g.ControllerIndex >= 0) accounted.Add(g.ControllerIndex);
            foreach (string id in g.InstanceIds)
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{id}");
                    if (k?.GetValue("HardwareID") is string[] hwids)
                        foreach (string hwid in hwids)
                            if (TryParseVidPid(hwid, out ushort vid, out ushort pid))
                                keys.Add(GameInputMapping.KeyName(vid, pid));
                }
                catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestro");
            foreach (string name in root?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                const string lead = "Controller";
                if (!name.StartsWith(lead, StringComparison.OrdinalIgnoreCase)
                    || !int.TryParse(name.AsSpan(lead.Length), System.Globalization.NumberStyles.None,
                                     System.Globalization.CultureInfo.InvariantCulture, out int n))
                    continue;
                using var c = root!.OpenSubKey(name);
                if (c == null) continue;   // deleted since the listing
                accounted.Add(n);
                if (c.GetValue("VendorId") is int vid && c.GetValue("ProductId") is int pid)
                    keys.Add(GameInputMapping.KeyName((ushort)vid, (ushort)pid));
                else if (c.GetValue("DeviceInstanceId") is string instance && TryParseVidPid(instance, out ushort v, out ushort p))
                    keys.Add(GameInputMapping.KeyName(v, p));
                else
                {
                    DeviceOrchestrator.LogDiag($"    legacy GameInput tables kept: {name} names no VID and PID yet");
                    return null;
                }
            }
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
        for (int n = 0; n < SectionProbeLimit; n++)
        {
            if (accounted.Contains(n) || !SectionPresent($@"Global\HIDMaestroInput{n}")) continue;
            if (SectionPresent($@"Global\HIDMaestroInputAck{n}")) continue;
            DeviceOrchestrator.LogDiag($"    legacy GameInput tables kept: transport index {n} has an input section nothing else explains, so a create of unknown identity may be in flight");
            return null;
        }
        return keys;
    }

    /// <summary>Whether a named section exists, counting one this process
    /// may not open.</summary>
    private static bool SectionPresent(string name)
    {
        IntPtr h = OpenFileMapping(FILE_MAP_READ, false, name);
        if (h != IntPtr.Zero) { CloseHandle(h); return true; }
        return Marshal.GetLastPInvokeError() == ERROR_ACCESS_DENIED;
    }

    /// <summary>The VID and PID of a hardware id that carries
    /// <c>VID_xxxx&amp;PID_xxxx</c>.</summary>
    private static bool TryParseVidPid(string hwid, out ushort vid, out ushort pid)
    {
        vid = pid = 0;
        int v = hwid.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        if (v < 0 || v + 17 > hwid.Length) return false;
        ReadOnlySpan<char> s = hwid.AsSpan(v);
        return s.Slice(8, 5).Equals("&PID_", StringComparison.OrdinalIgnoreCase)
            && ushort.TryParse(s.Slice(4, 4), System.Globalization.NumberStyles.HexNumber, null, out vid)
            && ushort.TryParse(s.Slice(13, 4), System.Globalization.NumberStyles.HexNumber, null, out pid);
    }

    internal static int ReadControllerIndex(string instanceId) => ReadControllerIndex(instanceId, s_openDeviceParameters, out _);

    private static readonly Func<string, RegistryKey?> s_openDeviceParameters =
        DeviceParametersOpener(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Enum");

    /// <summary>The census's opener of a devnode's Device Parameters under
    /// <paramref name="enumPath"/> of <paramref name="hive"/>: HKLM's Enum
    /// for the census, a scratch key for the coexistence probe's OF13.</summary>
    internal static Func<string, RegistryKey?> DeviceParametersOpener(RegistryKey hive, string enumPath) =>
        id => hive.OpenSubKey($@"{enumPath}\{id}\Device Parameters");

    /// <summary>The index the census gives a devnode: its ControllerIndex
    /// value, or, when that cannot be read, the index its name carries as a
    /// default identity's token, which a keyless controller of every release
    /// from v1.8.0 has. A keyed devnode whose value cannot be read has none
    /// (-1).</summary>
    private static int CensusIndex(string instanceId, string name, Func<string, RegistryKey?> openDeviceParameters, out IndexRead read)
    {
        int index = ReadControllerIndex(instanceId, openDeviceParameters, out read);
        if (index < 0 && DeviceIdentity.TryParseDefaultToken(name, out int fromToken))
        {
            read = IndexRead.Token;
            return fromToken;
        }
        return index;
    }

    /// <summary>A devnode's Device Parameters\ControllerIndex, which every
    /// SDK writes at create, or -1 with the reason in
    /// <paramref name="read"/>. <paramref name="openDeviceParameters"/>
    /// opens the key: the census's opener, or one the coexistence probe's
    /// OF13 hands it. A v1.12.0 program's devnode whose driver install
    /// failed (its staging had no catalog) has read -1 in the census while
    /// its program ran, measured in Windows Sandbox, though its create had
    /// written the value.</summary>
    internal static int ReadControllerIndex(string instanceId, Func<string, RegistryKey?> openDeviceParameters, out IndexRead read)
    {
        try
        {
            using var dp = openDeviceParameters(instanceId);
            if (dp == null) { read = IndexRead.NoKey; return -1; }
            if (dp.GetValue("ControllerIndex") is int ci) { read = IndexRead.Read; return ci; }
            read = IndexRead.NoValue;
            return -1;
        }
        // OpenSubKey throws SecurityException on a key it may not open, and
        // GetValue IOException on a key deleted under it. Any failure reads
        // as no index, as before this reason was kept.
        catch (Exception)
        {
            read = IndexRead.Unreadable;
            return -1;
        }
    }

    private static string Describe(IndexRead read) => read switch
    {
        IndexRead.NoValue => "Device Parameters has no DWORD ControllerIndex",
        IndexRead.NoKey => "the devnode has no Device Parameters key",
        IndexRead.Unreadable => "Device Parameters cannot be read",
        IndexRead.Token => "Device Parameters has no readable ControllerIndex, and the index came from the default identity's token",
        _ => "read",
    };

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
