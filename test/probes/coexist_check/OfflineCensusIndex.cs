// OF13: devnodes whose transport index the census cannot read.
//
// A v1.12.0 program's device whose driver install failed reads index -1 in
// the census while its program runs, measured in Windows Sandbox. The census
// then cannot tie the device to its owner pipe. The checks run the census's
// own grouping and classification (OwnerCensus.Build) and its own opener of
// Device Parameters (OwnerCensus.DeviceParametersOpener) over scratch keys
// under HKCU, with no device and no elevation:
//
//   The read. A DWORD value reads as itself. A key without the value, a
//   value of another type and a missing key read -1 with their reason. A key
//   that refuses this process's read, made so with a deny entry, reads -1,
//   unreadable. A default identity's token (HM_0042) whose value cannot be
//   read gives the index it names.
//   The read also refuses a keyed token whose hex digits make a small
//   number, and reads HM_0064 as 64.
//   The floor. A device of a closed program whose index cannot be read
//   leaves ControllerLease's floor at 0. With a v1.12.0 program seen running
//   on an index nothing else accounts for, the floor is 64, and a live
//   record's index, this release's own, is not counted (a dead record's
//   is). A registry ledger holder whose process no live record names raises
//   it on an index this release also holds. This release's own holder, a
//   holder on index 64 and a holder whose process the records read after
//   the holders name raise nothing. A keyless leftover keeps only its own
//   index from use. With every index read, nothing is looked for. The
//   holders are read, then the records, then the holders again, and only a
//   holder both reads have counts.
//   The holder test opens the holder's process, closes it again, and reads
//   the process list only for one that refuses the open, once for both of
//   the census's holder reads (OwnerCensus.HolderReads). A process's first
//   read of the list makes one kernel query.
//   The exit (OwnerSweep.ExitRecordMayGo) keeps the record while a hold of
//   its own process stands, with controllers listed or none, and while a
//   device is still in the device tree.
//
// "offline census" runs only these.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

internal static partial class Program
{
    const BindingFlags AnyStaticMember = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>OF14. While a v1.12.0 program has a device present the
    /// floor is 64, and an identity key is the create's all the same:
    /// index:1, which that program gives its own controller on index 1, is
    /// index 1's default identity, as the program asked (decision (c), the
    /// newer release takes precedence). At 454ecb22 the lease refused the
    /// key with HMResourceInUseException.</summary>
    static void OfflineKeyBelowFloor()
    {
        Assembly sdk = typeof(HIDMaestro.HMContext).Assembly;
        Type? census = sdk.GetType("HIDMaestro.Internal.OwnerCensus");
        Type? lease = sdk.GetType("HIDMaestro.Internal.ControllerLease");
        MethodInfo? floor = lease?.GetMethod("Floor", AnyStaticMember);
        MethodInfo? keyed = lease?.GetMethod("KeyedIdentity", AnyStaticMember);
        Type? group = census?.GetNestedType("DeviceGroup", BindingFlags.NonPublic | BindingFlags.Public);
        Type? owner = census?.GetNestedType("Owner", BindingFlags.NonPublic | BindingFlags.Public);
        Check("OF14: ControllerLease.KeyedIdentity, its floor and the census's DeviceGroup exist",
              census != null && floor != null && keyed != null && group != null && owner != null);
        if (census == null || floor == null || keyed == null || group == null || owner == null) return;
        object c = Activator.CreateInstance(census, nonPublic: true)!;
        ((IList)census.GetProperty("Devices")!.GetValue(c)!).Add(Activator.CreateInstance(group, "HM_0000", 0,
            new List<string> { @"ROOT\HIDClass\HM_0000" }, Enum.Parse(owner, "LiveLegacy"), null)!);
        int f = (int)floor.Invoke(null, new[] { c })!;
        Check("OF14: a v1.12.0 program's present device sets the floor at 64", f == 64, $"{f}");
        foreach (var (key, index) in new[] { ("index:1", 1), ("index:0", 0), ("index:70", 70) })
        {
            object? id = null;
            string error = "";
            try { id = keyed.Invoke(null, new object[] { key, 0, c }); }
            catch (TargetInvocationException t) { error = $"{t.InnerException!.GetType().Name}: {t.InnerException.Message}"; }
            bool isDefault = id != null && (bool)id.GetType().GetProperty("IsDefault")!.GetValue(id)!;
            int at = id == null ? -1 : (int)id.GetType().GetProperty("Index")!.GetValue(id)!;
            Check($"OF14: with the floor at 64, the key {key} is index {index}'s default identity, not refused",
                  isDefault && at == index, id == null ? error : $"default {isDefault}, index {at}");
        }
    }

    static void OfflineCensusIndex()
    {
        Assembly sdk = typeof(HIDMaestro.HMContext).Assembly;
        Type? census = sdk.GetType("HIDMaestro.Internal.OwnerCensus");
        Type? record = sdk.GetType("HIDMaestro.Internal.MachineClaims")?.GetNestedType("OwnerRecord", BindingFlags.NonPublic | BindingFlags.Public);
        MethodInfo? floor = sdk.GetType("HIDMaestro.Internal.ControllerLease")?.GetMethod("Floor", AnyStaticMember);
        MethodInfo? build = census?.GetMethod("Build", AnyStaticMember);
        MethodInfo? opener = census?.GetMethod("DeviceParametersOpener", AnyStaticMember);
        MethodInfo? inUse = census?.GetMethod("IndicesInUse", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Type? group = census?.GetNestedType("DeviceGroup", BindingFlags.NonPublic | BindingFlags.Public);
        Type? owner = census?.GetNestedType("Owner", BindingFlags.NonPublic | BindingFlags.Public);
        Check("OF13: OwnerCensus, its DeviceGroup and ControllerLease's floor exist",
              census != null && group != null && owner != null && floor != null);
        if (census == null || group == null || owner == null || floor == null) return;

        // The rule alone, on a census built by hand, which every release
        // since the floor can be handed: a device of unknown state whose
        // index cannot be read, and no v1.12.0 program found running.
        {
            object c = Activator.CreateInstance(census, nonPublic: true)!;
            ((IList)census.GetProperty("Devices")!.GetValue(c)!).Add(Activator.CreateInstance(group, "HM_A1B2C3D4E5F60718", -1,
                new List<string> { @"ROOT\HIDClass\HM_A1B2C3D4E5F60718" }, Enum.Parse(owner, "Unknown"), null)!);
            int f = (int)floor.Invoke(null, new[] { c })!;
            Check("OF13: on a census built by hand, a device of unknown state whose index cannot be read, with no v1.12.0 program found running, sets no floor",
                  f == 0, $"{f}");
        }

        Check("OF13: OwnerCensus.Build, reading ledger holders, DeviceParametersOpener and IndicesInUse exist",
              record != null && build != null && build.GetParameters().Length == 6 && opener != null && inUse != null,
              build == null ? "no Build" : $"Build takes {build.GetParameters().Length} parameters");
        if (record == null || build == null || build.GetParameters().Length != 6 || opener == null || inUse == null) return;

        const string root = @"Software\HIDMaestroCoexistOF13";
        const string enumPath = root + @"\Enum";
        const string keyless = @"ROOT\HIDClass\HM_0042";
        const string unreadable = @"ROOT\HIDClass\HM_A1B2C3D4E5F60718";
        const string withValue = @"ROOT\HIDClass\HM_1234567890ABCDEF";
        const string noKey = @"ROOT\HIDClass\HM_FEDCBA9876543210";
        const string stringValue = @"ROOT\HIDClass\HM_00AA00AA00AA00AA";
        // A keyed token whose sixteen hex digits make a small number, 42, and
        // a keyless token past the floor, HM_0064.
        const string keyedHex = @"ROOT\HIDClass\HM_000000000000002A";
        const string keyless64 = @"ROOT\HIDClass\HM_0064";
        string Params(string id) => $@"{enumPath}\{id}\Device Parameters";
        var me = WindowsIdentity.GetCurrent().User!;
        var deny = new RegistryAccessRule(me, RegistryRights.ReadKey, AccessControlType.Deny);
        try
        {
            using (Registry.CurrentUser.CreateSubKey(Params(keyless))) { }
            using (Registry.CurrentUser.CreateSubKey(Params(keyedHex))) { }
            using (Registry.CurrentUser.CreateSubKey(Params(keyless64))) { }
            using (var k = Registry.CurrentUser.CreateSubKey(Params(withValue))) k.SetValue("ControllerIndex", 7, RegistryValueKind.DWord);
            using (var k = Registry.CurrentUser.CreateSubKey(Params(stringValue))) k.SetValue("ControllerIndex", "7", RegistryValueKind.String);
            using (Registry.CurrentUser.CreateSubKey($@"{enumPath}\{noKey}")) { }
            using (var k = Registry.CurrentUser.CreateSubKey(Params(unreadable)))
            {
                k.SetValue("ControllerIndex", 9, RegistryValueKind.DWord);
                var acl = k.GetAccessControl();
                acl.AddAccessRule(deny);
                k.SetAccessControl(acl);
            }
            string control;
            try { using var k = Registry.CurrentUser.OpenSubKey(Params(unreadable)); control = "it opened"; }
            catch (Exception e) { control = e.GetType().FullName!; }
            Check("OF13: control: the denied key throws SecurityException on a plain open", control == "System.Security.SecurityException", control);

            var open = opener.Invoke(null, new object[] { Registry.CurrentUser, enumPath })!;
            Array noRecords = Array.CreateInstance(record, 0);
            // The census reads the records again after the ledger holders,
            // and is handed both reads here.
            Delegate RecordsRead(Array a)
            {
                Type sequence = typeof(IEnumerable<>).MakeGenericType(record);
                return System.Linq.Expressions.Expression.Lambda(typeof(Func<>).MakeGenericType(sequence),
                    System.Linq.Expressions.Expression.Constant(a, sequence)).Compile();
            }
            object BuildWith(Array records, Func<int, bool> legacyOn, (int Pid, int Index)[] holders, Array? recordsAfter, params string[] ids) =>
                build.Invoke(null, new[] { (object)ids, open, records, legacyOn,
                    (Func<IEnumerable<(int Pid, int Index)>>)(() => holders), RecordsRead(recordsAfter ?? records) })!;
            // The same with the two reads as calls whose answers depend on
            // what was called before, as on a machine where another program
            // writes or deletes its record and holders between them.
            Delegate RecordsCall(Func<Array> read)
            {
                Type sequence = typeof(IEnumerable<>).MakeGenericType(record);
                return System.Linq.Expressions.Expression.Lambda(typeof(Func<>).MakeGenericType(sequence),
                    System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Invoke(
                        System.Linq.Expressions.Expression.Constant(read)), sequence)).Compile();
            }
            object BuildCalls(Func<(int Pid, int Index)[]> holders, Func<Array> recordsAfter, params string[] ids) =>
                build.Invoke(null, new[] { (object)ids, open, noRecords, (Func<int, bool>)(_ => false),
                    (Func<IEnumerable<(int Pid, int Index)>>)(() => holders()), RecordsCall(recordsAfter) })!;
            object Build(Func<int, bool> legacyOn, params string[] ids) => BuildWith(noRecords, legacyOn, Array.Empty<(int, int)>(), null, ids);
            // One record of this release's, its owner live or not, listing a
            // controller on transport index 3, owned by process pid.
            Array Records(bool live, int? pid = null)
            {
                Type entry = sdk.GetType("HIDMaestro.Internal.MachineClaims")!.GetNestedType("ControllerEntry", BindingFlags.NonPublic | BindingFlags.Public)!;
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entry))!;
                list.Add(Activator.CreateInstance(entry, "HM_0003", 3, "umdf2", null, null)!);
                Array a = Array.CreateInstance(record, 1);
                a.SetValue(Activator.CreateInstance(record, "of13-owner", pid ?? Environment.ProcessId, null, 0L, "1.13.0", 0, live, false, list, null)!, 0);
                return a;
            }
            IList Devices(object c) => (IList)census.GetProperty("Devices")!.GetValue(c)!;
            string Show(object c) => string.Join(" ", Devices(c).Cast<object>().Select(g =>
                $"{Prop(g, "Key")}|{Prop(g, "ControllerIndex")}/{Prop(g, "IndexRead")}|{Prop(g, "Owner")}"));
            object? Group(object c, string id) => Devices(c).Cast<object>()
                .FirstOrDefault(g => id.EndsWith(Prop(g, "Key") ?? "?", StringComparison.OrdinalIgnoreCase));
            string Of(object c, string id) => Group(c, id) is { } g ? $"{Prop(g, "ControllerIndex")}/{Prop(g, "IndexRead")}/{Prop(g, "Owner")}" : "no group";
            int Floor(object c) => (int)floor.Invoke(null, new[] { c })!;
            HashSet<int> InUse(object c) => (HashSet<int>)inUse.Invoke(c, null)!;

            // The read, through the census's grouping, every device of a
            // closed program: no v1.12.0 program runs.
            object all = Build(_ => false, keyless, unreadable, withValue, noKey, stringValue);
            Check("OF13: a DWORD ControllerIndex reads as its value", Of(all, withValue) == "7/Read/Unknown", Show(all));
            Check("OF13: a value of another type reads -1, no value", Of(all, stringValue) == "-1/NoValue/Unknown", Show(all));
            Check("OF13: a missing Device Parameters key reads -1, no key", Of(all, noKey) == "-1/NoKey/Unknown", Show(all));
            Check("OF13: a key that refuses the read reads -1, unreadable", Of(all, unreadable) == "-1/Unreadable/Unknown", Show(all));
            Check("OF13: a default identity's token gives the index it names when the value is missing", Of(all, keyless) == "42/Token/Unknown", Show(all));
            object tokens = Build(_ => false, keyedHex, keyless64);
            Check("OF13: a keyed token whose hex digits make 42 gives no index", Of(tokens, keyedHex) == "-1/NoValue/Unknown", Show(tokens));
            Check("OF13: a default identity's token past the floor, HM_0064, gives 64", Of(tokens, keyless64) == "64/Token/Unknown", Show(tokens));

            // The floor.
            object dead = Build(_ => false, unreadable);
            Check("OF13: a closed program's device whose index cannot be read leaves the floor at 0", Floor(dead) == 0, $"{Floor(dead)}, {Show(dead)}");
            object live = Build(n => n == 3, unreadable);
            Check("OF13: with a v1.12.0 program running on index 3, which nothing else accounts for, the floor is 64",
                  Floor(live) == 64 && Prop(live, "UnplacedLegacyProgram") == "True", $"{Floor(live)}, {Show(live)}");
            object own = Build(_ => false, keyless);
            var used = InUse(own);
            Check("OF13: a keyless leftover keeps only its own index from use, and no floor",
                  used.SetEquals(new[] { 42 }) && Floor(own) == 0, $"in use {string.Join(",", used)}, floor {Floor(own)}");
            // This release's own controllers hold the same pipe and section
            // names a v1.12.0 program holds, so the index a live record lists
            // reads as one to the test. It is this release's, not counted.
            object own3 = BuildWith(Records(live: true), n => n == 3, Array.Empty<(int, int)>(), null, unreadable);
            Check("OF13: a live record's index, whose pipe and sections are this release's own, raises no floor",
                  Floor(own3) == 0 && Prop(own3, "UnplacedLegacyProgram") == "False", $"{Floor(own3)}, {Show(own3)}");
            object dead3 = BuildWith(Records(live: false), n => n == 3, Array.Empty<(int, int)>(), null, unreadable);
            Check("OF13: control: a dead record's index excludes nothing, and the program found there raises the floor",
                  Floor(dead3) == 64, $"{Floor(dead3)}, {Show(dead3)}");

            // The ledger. A v1.12.0 program on index 3, which this release's
            // live program also holds, so every named object there is
            // this release's too, shows by its holder: a running process no
            // live record names. The holders handed here are those whose
            // process runs, as RegistryLedger.LiveHolders returns them.
            const int foreignPid = 2_000_000_001;
            object shared = BuildWith(Records(live: true), n => n == 3, new[] { (foreignPid, 3) }, null, unreadable);
            Check("OF13: a ledger holder on index 3 whose process no live record names raises the floor, though this release holds index 3",
                  Floor(shared) == 64 && Prop(shared, "UnplacedLegacyProgram") == "True", $"{Floor(shared)}, {Show(shared)}");
            object ownHolder = BuildWith(Records(live: true), n => n == 3, new[] { (Environment.ProcessId, 3) }, null, unreadable);
            Check("OF13: control: this release's own holder on its own index raises no floor",
                  Floor(ownHolder) == 0, $"{Floor(ownHolder)}, {Show(ownHolder)}");
            object highHolder = BuildWith(noRecords, _ => false, new[] { (foreignPid, 64) }, null, unreadable);
            Check("OF13: control: a holder on index 64, out of a v1.12.0 program's first 64, raises no floor",
                  Floor(highHolder) == 0, $"{Floor(highHolder)}, {Show(highHolder)}");
            // A program of this release writes its record before any holder,
            // so one whose holder the census read is in the records it reads
            // after the holders, though not in the read before.
            object later = BuildWith(noRecords, _ => false, new[] { (foreignPid, 5) }, Records(live: true, pid: foreignPid), unreadable);
            Check("OF13: a holder whose process is in the records read after the holders is this release's, and raises no floor",
                  Floor(later) == 0, $"{Floor(later)}, {Show(later)}");
            // A program of this release mid-create: its record is written,
            // then its holder. The census reads the holders, the records,
            // then the holders again. A census that read the records first
            // would find the holder and miss the record.
            {
                Array published = noRecords;
                int calls = 0;
                object midCreate = BuildCalls(() =>
                {
                    published = Records(live: true, pid: foreignPid);
                    calls++;
                    return new[] { (foreignPid, 5) };
                }, () => published, unreadable);
                Check("OF13: a program of this release whose record and holder are written while the census reads is not taken for a v1.12.0 one",
                      Floor(midCreate) == 0 && calls == 2, $"{Floor(midCreate)}, holders read {calls} times, {Show(midCreate)}");
            }
            // A program of this release at exit: its holders go, then its
            // record. A census that read the holders before and the records
            // after misses both, and only the second read of the holders
            // shows the holder gone.
            {
                int calls = 0;
                object exiting = BuildCalls(() => ++calls == 1 ? new[] { (foreignPid, 5) } : Array.Empty<(int, int)>(),
                    () => noRecords, unreadable);
                Check("OF13: a program of this release whose holders and then record go while the census reads is not taken for a v1.12.0 one",
                      Floor(exiting) == 0, $"{Floor(exiting)}, holders read {calls} times, {Show(exiting)}");
            }
            // The second holder read counts only what the first had: a
            // program of this release that writes its record after the
            // records read and its holder before the second holder read is
            // in neither the records nor the first read.
            {
                const int ownPid = 2_000_000_003;
                int calls = 0;
                object newcomer = BuildCalls(() => ++calls == 1 ? new[] { (ownPid, 3) } : new[] { (ownPid, 3), (foreignPid, 5) },
                    () => Records(live: true, pid: ownPid), unreadable);
                Check("OF13: a holder the second holder read has and the first does not raises no floor",
                      Floor(newcomer) == 0 && calls == 2, $"{Floor(newcomer)}, holders read {calls} times, {Show(newcomer)}");
            }
            // The records are read between the two holder reads: a record
            // the census can see only after the first holder read and
            // before the second accounts for the holder both reads have.
            {
                int calls = 0;
                object between = BuildCalls(() => { calls++; return new[] { (foreignPid, 5) }; },
                    () => calls == 1 ? Records(live: true, pid: foreignPid) : noRecords, unreadable);
                Check("OF13: the records are read after the first holder read and before the second",
                      Floor(between) == 0 && calls == 2, $"{Floor(between)}, holders read {calls} times, {Show(between)}");
            }
            object deadSamePid = BuildWith(Records(live: false, pid: foreignPid), _ => false, new[] { (foreignPid, 3) }, null, unreadable);
            Check("OF13: a dead record that names the holder's process id does not hide the v1.12.0 program now under that id",
                  Floor(deadSamePid) == 64, $"{Floor(deadSamePid)}, {Show(deadSamePid)}");
            object noDevice = BuildWith(noRecords, _ => false, new[] { (foreignPid, 3) }, null, withValue);
            Check("OF13: control: with every index read, holders are not read and no floor is set",
                  Floor(noDevice) == 0, $"{Floor(noDevice)}, {Show(noDevice)}");
            object readable = Build(_ => true, withValue);
            Check("OF13: control: with every index read, no program is looked for and no floor is set",
                  Floor(readable) == 0 && Prop(readable, "UnplacedLegacyProgram") == "False", $"{Floor(readable)}, {Show(readable)}");
        }
        finally
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Params(unreadable), RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);
                if (k != null)
                {
                    var acl = k.GetAccessControl(AccessControlSections.Access);
                    acl.RemoveAccessRule(deny);
                    k.SetAccessControl(acl);
                }
            }
            catch (Exception e) { Console.WriteLine($"    OF13: the deny entry was not removed: {e.Message}"); }
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    /// <summary>OF13's ledger: RegistryLedger.LiveHolders over a scratch
    /// ledger under HKCU and the system's process list, and the test of a
    /// holder the census and the dead-holder cleanup share
    /// (RegistryLedger.HolderAliveIn).</summary>
    static void OfflineLedgerHolders()
    {
        Assembly sdk = typeof(HIDMaestro.HMContext).Assembly;
        Type? ledger = sdk.GetType("HIDMaestro.Internal.RegistryLedger");
        MethodInfo? live = ledger?.GetMethods(AnyStaticMember).FirstOrDefault(m => m.Name == "LiveHolders" && m.GetParameters().Length == 3);
        MethodInfo? times = ledger?.GetMethod("ProcessCreateTimes", AnyStaticMember, Type.EmptyTypes);
        MethodInfo? alive = ledger?.GetMethod("HolderAliveIn", AnyStaticMember);
        Check("OF13: RegistryLedger.LiveHolders over a given ledger, ProcessCreateTimes and HolderAliveIn exist",
              live != null && times != null && alive != null);
        if (live == null || times == null || alive == null) return;

        int self = Environment.ProcessId;
        long start;
        using (var me = System.Diagnostics.Process.GetCurrentProcess()) start = me.StartTime.ToFileTimeUtc();
        var list = (Dictionary<int, long>?)times.Invoke(null, null);
        Check("OF13: the process list carries this process, with the start time a holder of it carries",
              list != null && list.TryGetValue(self, out long listed) && listed == start,
              list == null ? "no list" : list.TryGetValue(self, out long l) ? $"{l:X} against {start:X}" : "not listed");
        if (list == null) return;
        // The holder test with a read of the list that counts its calls.
        int listReads = 0;
        bool AliveWith(string holder, Dictionary<int, long>? given)
        {
            Func<Dictionary<int, long>?> read = () => { listReads++; return given; };
            object arg = alive.GetParameters()[1].ParameterType == typeof(Dictionary<int, long>) ? given! : read;
            return (bool)alive.Invoke(null, new object[] { holder, arg })!;
        }
        bool Alive(string holder) => AliveWith(holder, list);
        Check("OF13: a holder of this process with its start time is alive", Alive($"{self}-{start:X}-3"));
        Check("OF13: a holder of this process's id with another start time is gone", !Alive($"{self}-1-3"));
        // pid 4 is the System process, which a process that is not
        // elevated cannot open for its start time.
        Check("OF13: a holder of a process this one cannot open, with another start time, is gone", !Alive("4-1-3"));
        Check("OF13: a holder of a process that does not run is gone", !Alive("2000000001-1-3"));
        if (list.TryGetValue(4, out long systemStart))
            Check("OF13: a holder of a process this one cannot open, with the start time the list gives it, is alive", Alive($"4-{systemStart:X}-3"));
        Check("OF13: with no list to read, a holder of a process this one cannot open counts", AliveWith("4-1-3", null));
        // A process that has exited with exit code 259, STILL_ACTIVE's
        // value, while this one keeps its handle, so its id still opens.
        using (var exited = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit 259")
               { UseShellExecute = false, CreateNoWindow = true })!)
        {
            exited.WaitForExit();
            long exitedStart = exited.StartTime.ToFileTimeUtc();
            Check("OF13: a holder of a process that has exited with exit code 259 is gone, its handle still open here",
                  exited.ExitCode == 259 && !Alive($"{exited.Id}-{exitedStart:X}-3"), $"exit code {exited.ExitCode}");
        }
        listReads = 0;
        bool ownAlive = AliveWith($"{self}-{start:X}-3", list), ownGone = AliveWith($"{self}-1-3", list), noneGone = AliveWith("2000000001-1-3", list);
        Check("OF13: a holder whose process opens, or does not exist, is judged without reading the process list",
              ownAlive && !ownGone && !noneGone && listReads == 0, $"list read {listReads} times");
        listReads = 0;
        AliveWith("4-1-3", list);
        Check("OF13: a holder of a process that refuses the open reads the process list once", listReads == 1, $"list read {listReads} times");
        // Each holder test opens the holder's process and closes it again.
        int Handles() { using var me = System.Diagnostics.Process.GetCurrentProcess(); return me.HandleCount; }
        int handlesBefore = Handles();
        for (int i = 0; i < 500; i++) { AliveWith($"{self}-{start:X}-3", list); AliveWith($"{self}-1-3", list); }
        int handlesAfter = Handles();
        Check("OF13: a thousand holder tests of a process that opens leave no handle behind",
              handlesAfter - handlesBefore < 50, $"{handlesBefore} handles before, {handlesAfter} after");
        // A process's first read of the list fits its buffer on a machine
        // with this many processes, and so does the next.
        var sizeField = ledger!.GetField("s_processListSize", AnyStaticMember);
        var countedRead = ledger.GetMethods(AnyStaticMember).FirstOrDefault(m => m.Name == "ProcessCreateTimes" && m.GetParameters().Length == 1);
        if (sizeField != null && countedRead != null)
        {
            object saved = sizeField.GetValue(null)!;
            sizeField.SetValue(null, 0u);
            var args = new object?[] { null };
            countedRead.Invoke(null, args);
            int first = (int)args[0]!;
            countedRead.Invoke(null, args);
            int next = (int)args[0]!;
            Check("OF13: a process's first read of the process list, and its next, each make one kernel query",
                  first == 1 && next == 1, $"{first} and {next} queries, {list.Count} processes, buffer now {sizeField.GetValue(null)} bytes");
            // The check above catches a smaller first buffer only on a
            // machine with enough processes to outgrow it. This one holds the
            // first buffer to 1,500 processes at this machine's bytes per
            // process, whatever the count here (its comment promises some
            // 1,600): a 1 MB or a 2.5 MB first buffer fails it on any machine.
            uint needed = (uint)sizeField.GetValue(null)! - 64 * 1024;
            object? firstSize = ledger.GetField("FirstProcessListSize", AnyStaticMember)?.GetRawConstantValue();
            double perProcess = list.Count > 0 ? (double)needed / list.Count : 0;
            double holds = firstSize is uint fs && perProcess > 0 ? fs / perProcess : 0;
            Check("OF13: the first read's buffer holds at least 1,500 processes at this machine's bytes per process",
                  holds >= 1500, $"{firstSize ?? "no FirstProcessListSize"} bytes, {perProcess:F0} bytes a process over {list.Count}, holds {holds:F0}");
            sizeField.SetValue(null, saved);
        }
        else Check("OF13: RegistryLedger.ProcessCreateTimes(out int) and its size exist", false);

        const string root = @"Software\HIDMaestroCoexistOF13Ledger";
        string denied = $@"{root}\JoystickOem\VID_BEEF&PID_0000\Holders";
        var deny = new RegistryAccessRule(WindowsIdentity.GetCurrent().User!, RegistryRights.ReadKey, AccessControlType.Deny);
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(denied))
            {
                k.SetValue($"{self}-{start:X}-9", 1, RegistryValueKind.DWord);
                var acl = k.GetAccessControl();
                acl.AddAccessRule(deny);
                k.SetAccessControl(acl);
            }
            using (var k = Registry.CurrentUser.CreateSubKey($@"{root}\JoystickOem\VID_BEEF&PID_0001\Holders"))
            {
                k.SetValue($"{self}-{start:X}-3", 1, RegistryValueKind.DWord);
                k.SetValue($"{self}-1-4", 1, RegistryValueKind.DWord);
                k.SetValue("4-1-5", 1, RegistryValueKind.DWord);
                k.SetValue("2000000001-1-6", 1, RegistryValueKind.DWord);
                k.SetValue("not-a-holder", 1, RegistryValueKind.DWord);
            }
            using (var k = Registry.CurrentUser.CreateSubKey($@"{root}\GameInput\VID_BEEF&PID_0001\Holders"))
                k.SetValue($"{self}-{start:X}-7", 1, RegistryValueKind.DWord);
            int reads = 0;
            Func<Dictionary<int, long>?> counted = () => { reads++; return list; };
            List<string> Walk(Func<Dictionary<int, long>?> read) =>
                ((IEnumerable)live.Invoke(null, new object[] { Registry.CurrentUser, root, read })!)
                .Cast<object>().Select(h => h.ToString()!).OrderBy(h => h, StringComparer.Ordinal).ToList();
            var found = Walk(counted);
            string want = string.Join(" ", new[] { $"({self}, 3)", $"({self}, 7)" }.OrderBy(h => h, StringComparer.Ordinal));
            Check("OF13: LiveHolders lists only the holders whose process runs with their start time, in both kinds, past an entry it may not read",
                  string.Join(" ", found) == want, $"{string.Join(" ", found)}, want {want}");
            Check("OF13: LiveHolders reads the process list once, for the one holder whose process refuses the open", reads == 1, $"list read {reads} times");
            // The census's two holder reads share one read of the list.
            if (ledger!.GetMethod("ReadOnce", AnyStaticMember) is { } once)
            {
                reads = 0;
                var shared = (Func<Dictionary<int, long>?>)once.Invoke(null, new object[] { counted })!;
                Walk(shared);
                Walk(shared);
                Check("OF13: two holder reads through RegistryLedger.ReadOnce read the process list once", reads == 1, $"list read {reads} times");
            }
            else Check("OF13: RegistryLedger.ReadOnce exists", false);
            // The census's own holder reads, as OwnerCensus.Take builds them.
            if (sdk.GetType("HIDMaestro.Internal.OwnerCensus")?.GetMethod("HolderReads", AnyStaticMember) is { } holderReads)
            {
                reads = 0;
                var census = (Func<IEnumerable<(int Pid, int Index)>>)holderReads.Invoke(null, new object[] { Registry.CurrentUser, root, counted })!;
                var firstRead = census().OrderBy(h => h).ToList();
                var secondRead = census().OrderBy(h => h).ToList();
                Check("OF13: the census's two holder reads read the process list once between them",
                      reads == 1 && firstRead.SequenceEqual(secondRead), $"list read {reads} times, {firstRead.Count} and {secondRead.Count} holders");
            }
            else Check("OF13: OwnerCensus.HolderReads exists", false);
            using (var k = Registry.CurrentUser.OpenSubKey($@"{root}\JoystickOem\VID_BEEF&PID_0001\Holders", writable: true)!)
                k.DeleteValue("4-1-5");
            reads = 0;
            Walk(counted);
            Check("OF13: LiveHolders reads no process list when every holder's process opens or does not exist", reads == 0, $"list read {reads} times");
        }
        finally
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(denied, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);
                if (k != null)
                {
                    var acl = k.GetAccessControl(AccessControlSections.Access);
                    acl.RemoveAccessRule(deny);
                    k.SetAccessControl(acl);
                }
            }
            catch (Exception e) { Console.WriteLine($"    OF13: the deny entry was not removed: {e.Message}"); }
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    /// <summary>OF13's exit: OwnerSweep.ExitRecordMayGo, which decides
    /// whether an exit deletes the owner record, over a scratch ledger under
    /// HKCU, with the device removal and the release handed in.</summary>
    static void OfflineExitRecord()
    {
        Assembly sdk = typeof(HIDMaestro.HMContext).Assembly;
        MethodInfo? mayGo = sdk.GetType("HIDMaestro.Internal.OwnerSweep")?.GetMethod("ExitRecordMayGo", AnyStaticMember);
        Type? record = sdk.GetType("HIDMaestro.Internal.MachineClaims")?.GetNestedType("OwnerRecord", BindingFlags.NonPublic | BindingFlags.Public);
        Type? entry = sdk.GetType("HIDMaestro.Internal.MachineClaims")?.GetNestedType("ControllerEntry", BindingFlags.NonPublic | BindingFlags.Public);
        Check("OF13: OwnerSweep.ExitRecordMayGo over a given ledger exists", mayGo != null && record != null && entry != null);
        if (mayGo == null || record == null || entry == null) return;

        int self = Environment.ProcessId;
        long start;
        using (var me = System.Diagnostics.Process.GetCurrentProcess()) start = me.StartTime.ToFileTimeUtc();
        const string root = @"Software\HIDMaestroCoexistOF13Exit";
        string holders = $@"{root}\JoystickOem\VID_BEEF&PID_0002\Holders";
        string ownHold = $"{self}-{start:X}-0";
        // This process's record, listing a controller on index 0 or none.
        object Own(bool listsController)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entry))!;
            if (listsController) list.Add(Activator.CreateInstance(entry, "HM_0000", 0, "umdf2", null, null)!);
            return Activator.CreateInstance(record, "of13-exit", self, null, 0L, "1.13.0", 0, true, false, list, null)!;
        }
        void Ledger(params string[] names)
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
            using var k = Registry.CurrentUser.CreateSubKey(holders);
            foreach (string n in names) k.SetValue(n, 1, RegistryValueKind.DWord);
        }
        // The release a dispose would run: one that fails partway and leaves
        // the holds, or one that removes them.
        var released = new List<string>();
        Action<string[]> leaves = names => released.AddRange(names);
        Action<string[]> removes = names =>
        {
            released.AddRange(names);
            using var k = Registry.CurrentUser.OpenSubKey(holders, writable: true)!;
            foreach (string n in names) k.DeleteValue(n, throwOnMissingValue: false);
        };
        int removals = 0;
        (bool Gone, string Detail) Exit(object? own, bool devicesOut, Action<string[]> release)
        {
            released.Clear();
            removals = 0;
            Func<bool> remove = () => { removals++; return devicesOut; };
            bool gone = (bool)mayGo.Invoke(null, new object?[] { own, remove, Registry.CurrentUser, root, release })!;
            return (gone, $"record {(gone ? "deleted" : "kept")}, removal ran {removals} times, released [{string.Join(" ", released)}]");
        }

        var deny = new RegistryAccessRule(WindowsIdentity.GetCurrent().User!, RegistryRights.ReadKey, AccessControlType.Deny);
        try
        {
            // Every controller disposed, one dispose's release failed
            // partway: the record lists no controller, the hold stands.
            Ledger(ownHold, "2000000001-1-1");
            var r = Exit(Own(false), true, leaves);
            Check("OF13: an exit whose record lists no controller keeps the record while a hold of its own stands",
                  !r.Gone && released.SequenceEqual(new[] { ownHold }) && removals == 0, r.Detail);
            Ledger(ownHold, "2000000001-1-1");
            r = Exit(Own(false), true, removes);
            Check("OF13: an exit whose record lists no controller deletes the record once the release removes its hold",
                  r.Gone && released.SequenceEqual(new[] { ownHold }), r.Detail);
            Ledger("2000000001-1-1");
            r = Exit(Own(false), true, leaves);
            Check("OF13: control: an exit with no hold of its own deletes the record and releases nothing, another program's holder beside it",
                  r.Gone && released.Count == 0, r.Detail);
            Ledger(ownHold);
            r = Exit(Own(true), true, leaves);
            Check("OF13: an exit whose controllers' devices are out keeps the record while a hold of its own stands",
                  !r.Gone && removals == 1 && released.SequenceEqual(new[] { ownHold }), r.Detail);
            Ledger(ownHold);
            r = Exit(Own(true), true, removes);
            Check("OF13: an exit whose controllers' devices are out deletes the record once its hold is released",
                  r.Gone && removals == 1, r.Detail);
            Ledger();
            r = Exit(Own(true), false, removes);
            Check("OF13: an exit with a device still in the device tree keeps the record",
                  !r.Gone && removals == 1 && released.Count == 0, r.Detail);
            r = Exit(null, true, removes);
            Check("OF13: control: an exit with no record of its own deletes nothing and removes nothing",
                  !r.Gone && removals == 0 && released.Count == 0, r.Detail);
            Ledger(ownHold);
            using (var k = Registry.CurrentUser.OpenSubKey(holders, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions)!)
            {
                var acl = k.GetAccessControl();
                acl.AddAccessRule(deny);
                k.SetAccessControl(acl);
            }
            r = Exit(Own(false), true, removes);
            Check("OF13: an exit that cannot read the ledger keeps the record", !r.Gone, r.Detail);
        }
        finally
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(holders, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);
                if (k != null)
                {
                    var acl = k.GetAccessControl(AccessControlSections.Access);
                    acl.RemoveAccessRule(deny);
                    k.SetAccessControl(acl);
                }
            }
            catch (Exception e) { Console.WriteLine($"    OF13: the deny entry was not removed: {e.Message}"); }
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }
}
