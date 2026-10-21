using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using HIDMaestro.Internal.Usbip;
using Microsoft.Win32;

namespace HIDMaestro.Internal.Uninstall;

/// <summary>
/// <see cref="HMUninstall"/>'s work. A preview lists every item
/// (<see cref="Scan"/>). A run refuses while another HIDMaestro program
/// runs, lists, acts in the order below, lists again, and reports each item
/// by what its step said or, where the step said nothing, by whether the
/// second list still holds it.
///
/// <list type="number">
/// <item>Devices: the phantom pass, complete and synchronous, then
/// <see cref="DeviceOrchestrator.RemoveEverythingForUninstall"/> (devnodes,
/// USB/IP imports, interface registrations, the registry ledger's restores
/// of shared keys), then the phantom pass again for the records the software
/// devices' removal leaves, the ghost XUSB entries and the interface
/// entries.</item>
/// <item>OEM-name overrides, each target put back only while it holds the
/// label HIDMaestro wrote.</item>
/// <item>DeviceOverrides keys, under the inventory's conditions.</item>
/// <item>The OpenVR registration, in every profile's path registry.</item>
/// <item><see cref="UsbipRemoval.Remove"/>.</item>
/// <item>The driver packages, with SetupUninstallOEMInf and no
/// SUOI_FORCEDELETE.</item>
/// <item>The WUDF service keys whose driver folder is gone.</item>
/// <item>HIDMaestro's registry roots.</item>
/// <item>The signing certificate, once no package whose provider is
/// HIDMaestro is left.</item>
/// <item>Files and folders, from a trusted folder down without following a
/// link (SafeDelete).</item>
/// </list>
///
/// <para>The run takes no owner claim (<see cref="MachineClaims.NoNewClaim"/>),
/// constructs no HMContext and installs nothing. It holds
/// <see cref="OwnerSweep.CreateGate"/> for writing from its first look to its
/// last step, so no create of the calling process and no phantom cleanup of
/// it runs alongside, and a machine-wide mutex of its own, so two removals
/// never run at once. Another program's first claim, cleanup, override, VR
/// registration or transport install waits for that mutex
/// (<see cref="RemovalGate"/>), so a program that starts during the run waits
/// for its end. The censuses of running programs before the devices, before
/// the overrides and under the deploy lock are the backstop.</para>
/// </summary>
internal static partial class Uninstaller
{
    internal const string KindDevnode = "devnode";
    internal const string KindInterface = "interface registration";
    internal const string KindSharedKey = "shared registry key";
    internal const string KindSharedValue = "shared registry value";
    internal const string KindWinUsb = "WinUSB binding";
    internal const string KindOemOverride = "OEM name override";
    internal const string KindDeviceOverride = "DeviceOverrides key";
    internal const string KindOpenVr = "OpenVR registration";
    internal const string KindPackage = "driver package";
    internal const string KindWudf = "WUDF service key";
    internal const string KindRegistryKey = "registry key";
    internal const string KindCertificate = "certificate";
    internal const string KindFolder = "folder";
    internal const string KindServiceSetting = "service setting";
    internal const string KindStep = "removal step";

    internal const string UninstallMutexObject = "Uninstall";
    private const string DeviceOverridesRoot = @"SYSTEM\CurrentControlSet\Control\DeviceOverrides";
    private const string WudfServicesRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\WUDF\Services";
    private const string DriverPackagesRoot = @"SYSTEM\DriverDatabase\DriverPackages";
    private const string ProfileListRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private const string PackagesLeftKey = @"SOFTWARE\HIDMaestroPackagesLeft";

    /// <summary>HIDMaestro's registry roots under HKLM, in the order a run
    /// deletes them.</summary>
    private static readonly string[] RegistryRoots =
    {
        @"SOFTWARE\HIDMaestro",
        MachineClaims.RecordsRoot,
        RegistryLedger.Root,
        PackagesLeftKey,
        @"SOFTWARE\HIDMaestroOemOverrides",
    };

    /// <summary>The original INF names of HIDMaestro's driver packages,
    /// beside every hmwinusb_*.inf: the two this release installs, the XUSB
    /// shim v1.1.19 to v1.1.21 installed (hidmaestro_xusbshim_class.inf,
    /// git show v1.1.20:driver/hidmaestro_xusbshim_class.inf), and the shim's
    /// earlier name.</summary>
    private static readonly string[] DriverInfNames =
        { "hidmaestro.inf", "hidmaestro_xusb.inf", "hidmaestro_xusbshim_class.inf", "hidmaestro_xusbshim.inf" };

    /// <summary>The UMDF service names HIDMaestro's INFs give, whose keys
    /// Windows makes under WUDF\Services: HMXusbShim is the shim's
    /// (hidmaestro_xusbshim_class.inf at v1.1.20, UmdfService).</summary>
    private static readonly string[] WudfServiceNames = { "HIDMaestro", "HMXInput", "HIDMaestroCompanion", "HMXusbShim" };

    // ── Entry points ─────────────────────────────────────────────────────

    internal static HMUninstallReport Preview(HMUninstallOptions options)
    {
        var entries = new List<UninstallEntry>();
        List<HMRunningProgram> programs;
        try { programs = OtherPrograms(); }
        catch (Exception ex)
        {
            programs = new List<HMRunningProgram>();
            entries.Add(new UninstallEntry(KindStep, "running programs", UninstallOutcome.Failed,
                $"could not be read: {ex.GetType().Name}: {ex.Message}"));
        }
        entries.AddRange(Scan());
        entries.AddRange(Safely("usbip-win2", () => UsbipRemoval.Remove(preview: true, includeUnproven: options.IncludeUsbip)));
        // A preview that could not read part of the machine is not one a
        // removal can be judged by.
        var status = programs.Count > 0 ? HMUninstallStatus.Blocked
            : entries.Any(e => e.Outcome == UninstallOutcome.Failed) ? HMUninstallStatus.Failed
            : HMUninstallStatus.Previewed;
        return new HMUninstallReport(true, status, programs, ToPublic(entries));
    }

    internal static HMUninstallReport Run(HMUninstallOptions options)
    {
        if (!Environment.IsPrivilegedProcess)
            throw new UnauthorizedAccessException("Removing HIDMaestro needs administrator rights. Run the program elevated.");
        // No lock of this process is held across this wait, so a preview in
        // the same process never waits behind another removal. The mutex is
        // owned by a thread, so it orders two removals in one process too.
        // The mutex is also the gate a program's first claim, deploy or
        // create waits at (RemovalGate), so a program that starts during the
        // removal waits for its end. A thread of this process waiting there
        // stops waiting once RemovingHere is set, so this process's own
        // create in flight, which holds CreateGate's read side, finishes and
        // lets the write side below in.
        using var machine = AcquireUninstallLock();
        RemovalGate.RemovingHere = true;
        try
        {
            OwnerSweep.CreateGate.EnterWriteLock();
            MachineClaims.NoNewClaim = true;
            try { return RunGated(options); }
            finally
            {
                MachineClaims.NoNewClaim = false;
                OwnerSweep.CreateGate.ExitWriteLock();
            }
        }
        finally
        {
            RemovalGate.RemovingHere = false;
            machine.ReleaseMutex();
        }
    }

    private static HMUninstallReport RunGated(HMUninstallOptions options)
    {
        List<HMRunningProgram> programs;
        try { programs = OtherPrograms(); }
        catch (Exception ex)
        {
            // Without the census no run can tell whether another program
            // would lose its controllers, so nothing is changed.
            return new HMUninstallReport(false, HMUninstallStatus.Failed, Array.Empty<HMRunningProgram>(), new[]
            {
                new HMUninstallEntry(KindStep, "running programs", HMUninstallOutcome.Failed,
                    $"could not be read, so nothing was changed: {ex.GetType().Name}: {ex.Message}"),
            });
        }
        if (programs.Count > 0)
        {
            DeviceOrchestrator.LogDiag($"uninstall: blocked by {string.Join(", ", programs)}");
            return new HMUninstallReport(false, HMUninstallStatus.Blocked, programs, Array.Empty<HMUninstallEntry>());
        }
        var clock = Stopwatch.StartNew();
        var before = Scan();
        var told = new Told();

        // Each part of the device step is a step of its own, so one that
        // throws, such as a phantom pass that waited out the PnP lock's
        // bound, does not keep the others from running.
        Step(told, "phantom records", () => PhantomPass("first"));
        // The phantom pass waits for the PnP lock with no time limit, so a
        // program can start during it. Its controllers must not go with
        // everything else, so the run stops before the devices.
        if (StartedSince(told) is { } late) return Stopped(before, told, late, clock);
        Step(told, "devices", DeviceOrchestrator.RemoveEverythingForUninstall);
        Step(told, "phantom records again", () => PhantomPass("second"));
        Step(told, "interface registrations", () =>
        {
            DeviceOrchestrator.RemoveGhostXusbInterfaces();
            DeviceOrchestrator.RemoveOwnedInterfaceEntries(phantomOnly: false);
        });
        // A program that started during the device step holds the OEM
        // overrides, the OpenVR registration and the transport it uses.
        if (StartedSince(told) is { } late2) return Stopped(before, told, late2, clock);
        Step(told, "OEM name overrides", () => ActOemOverrides(told));
        Step(told, "DeviceOverrides keys", () => ActDeviceOverrides(told));
        Step(told, "OpenVR registrations", () => ActOpenVr(told));
        // Called holding CreateGate and the removal's own lock, and neither
        // the PnP nor the transport lock, as UsbipRemoval requires.
        Step(told, "usbip-win2", () =>
        {
            foreach (var e in UsbipRemoval.Remove(preview: false, includeUnproven: options.IncludeUsbip)) told.Add(e);
        });

        // The driver, its staging and the certificate it is signed with go
        // under the deploy lock, which every deploy and every create's deploy
        // gate takes, from the last look for a running program to the last
        // staging delete. A program that starts meanwhile waits for it, and
        // installs everything again once it is let go.
        Mutex? deploy = null;
        HMUninstallReport? stopped = null;
        Step(told, "deploy lock", () => deploy = DriverDeploy.Acquire());
        try
        {
            if (deploy != null)
            {
                if (StartedSince(told) is { } late3) stopped = Stopped(before, told, late3, clock);
                else
                {
                    Step(told, "driver packages", () => ActPackages(told));
                    Step(told, "WUDF service keys", () => ActWudf(told));
                    Step(told, "registry keys", () => ActRegistryRoots(told));
                    Step(told, "certificate", () => ActCertificate(told));
                    Step(told, "files", () => ActFiles(told));
                }
            }
        }
        finally
        {
            if (deploy != null)
            {
                deploy.ReleaseMutex();
                deploy.Dispose();
            }
        }
        DeviceNodeCreator.ForgetDeviceOverrides();
        if (stopped != null) return stopped;

        var after = Scan();
        var entries = Merge(before, after, told, stoppedBecause: null);
        var status = entries.Any(e => e.Outcome == UninstallOutcome.Failed) ? HMUninstallStatus.Failed
            : entries.Any(e => e.Outcome == UninstallOutcome.RemovedAtRestart) ? HMUninstallStatus.RemovedRestartPending
            : HMUninstallStatus.Removed;
        DeviceOrchestrator.LogDiag($"uninstall: {status} in {clock.ElapsedMilliseconds} ms, {entries.Count} item(s)");
        return new HMUninstallReport(false, status, Array.Empty<HMRunningProgram>(), ToPublic(entries));
    }

    /// <summary>The programs that started since the run's first look, or
    /// null when none did. A census that fails stops the run too, as a
    /// program it could not see would lose what the next steps
    /// remove.</summary>
    private static List<HMRunningProgram>? StartedSince(Told told)
    {
        try
        {
            var programs = OtherPrograms();
            return programs.Count > 0 ? programs : null;
        }
        catch (Exception ex)
        {
            told.Add(new UninstallEntry(KindStep, "running programs", UninstallOutcome.Failed,
                $"could not be read, so the removal stopped: {ex.GetType().Name}: {ex.Message}"));
            return new List<HMRunningProgram>();
        }
    }

    /// <summary>The report of a run that stopped part way: Blocked, with the
    /// programs that started, and every item it had not reached kept with
    /// the reason. Failed when it stopped because the census failed.</summary>
    private static HMUninstallReport Stopped(List<UninstallEntry> before, Told told, List<HMRunningProgram> programs, Stopwatch clock)
    {
        string why = programs.Count > 0
            ? $"the removal stopped because a HIDMaestro program started during it ({string.Join(", ", programs)}). Run it again once that program has exited"
            : "the removal stopped because the running programs could not be read";
        var entries = Merge(before, Scan(), told, why);
        var status = programs.Count > 0 ? HMUninstallStatus.Blocked : HMUninstallStatus.Failed;
        DeviceOrchestrator.LogDiag($"uninstall: stopped, {status}, in {clock.ElapsedMilliseconds} ms");
        return new HMUninstallReport(false, status, programs, ToPublic(entries));
    }

    /// <summary>The machine-wide lock two removals share, in the admin-only
    /// namespace (<see cref="AdminNamespace"/>). It comes first and orders
    /// against nothing: the only other taker, <see cref="RemovalGate"/>,
    /// takes it holding no lock of the SDK's and lets it go after one owner
    /// claim.</summary>
    private static Mutex AcquireUninstallLock()
    {
        var m = new Mutex(false, AdminNamespace.Name(UninstallMutexObject));
        try
        {
            if (!m.WaitOne(TimeoutScale.Apply(600_000)))
            {
                m.Dispose();
                throw new InvalidOperationException("Another removal of HIDMaestro held its lock for ten minutes.");
            }
        }
        catch (AbandonedMutexException) { }
        return m;
    }

    /// <summary>What the steps of a run said about items, by kind and
    /// location.</summary>
    private sealed class Told
    {
        private readonly Dictionary<(string, string), UninstallEntry> _byKey = new(KeyComparer.Instance);
        private readonly List<(string, string)> _order = new();
        public void Add(UninstallEntry e)
        {
            var key = (e.Kind, e.Location);
            if (!_byKey.ContainsKey(key)) _order.Add(key);
            _byKey[key] = e;
        }
        public bool TryGet(string kind, string location, out UninstallEntry e) => _byKey.TryGetValue((kind, location), out e!);
        public IEnumerable<UninstallEntry> All => _order.Select(k => _byKey[k]);
    }

    private sealed class KeyComparer : IEqualityComparer<(string, string)>
    {
        internal static readonly KeyComparer Instance = new();
        public bool Equals((string, string) a, (string, string) b) =>
            string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string, string) k) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item1), StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item2));
    }

    /// <summary>One step. A step that throws is reported as a failed entry
    /// of its own, and the run goes on: its items are then judged by the
    /// second list.</summary>
    private static void Step(Told told, string name, Action act)
    {
        var sw = Stopwatch.StartNew();
        try { act(); }
        catch (Exception ex)
        {
            told.Add(new UninstallEntry(KindStep, name, UninstallOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}"));
        }
        DeviceOrchestrator.LogDiag($"uninstall: {name} in {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>Each item of the first list, by what its step said, or kept
    /// as the first list kept it, or removed when the second list no longer
    /// holds it, or failed when it does. Then what the steps said about items
    /// the first list did not hold, and what the second list holds that the
    /// first did not. A listing that failed stays failed. In a run that
    /// stopped part way (<paramref name="stoppedBecause"/>), an item still
    /// there is kept with that reason.</summary>
    private static List<UninstallEntry> Merge(List<UninstallEntry> before, List<UninstallEntry> after, Told told, string? stoppedBecause)
    {
        var afterKeys = new HashSet<(string, string)>(after.Select(e => (e.Kind, e.Location)), KeyComparer.Instance);
        var beforeKeys = new HashSet<(string, string)>(KeyComparer.Instance);
        var result = new List<UninstallEntry>();
        UninstallEntry Left(UninstallEntry e, string otherwise) => stoppedBecause != null
            ? e with { Outcome = UninstallOutcome.Kept, Reason = stoppedBecause }
            : e with { Outcome = UninstallOutcome.Failed, Reason = otherwise };
        foreach (var e in before)
        {
            if (!beforeKeys.Add((e.Kind, e.Location))) continue;
            if (told.TryGet(e.Kind, e.Location, out var said)) result.Add(said);
            else if (e.Outcome is UninstallOutcome.Kept or UninstallOutcome.Failed) result.Add(e);
            else if (!afterKeys.Contains((e.Kind, e.Location))) result.Add(e with { Outcome = UninstallOutcome.Removed, Reason = RemovedNote(e.Kind) });
            else result.Add(Left(e, "it is still there after the removal"));
        }
        foreach (var said in told.All)
            if (!beforeKeys.Contains((said.Kind, said.Location))) { beforeKeys.Add((said.Kind, said.Location)); result.Add(said); }
        foreach (var e in after)
        {
            if (!beforeKeys.Add((e.Kind, e.Location))) continue;
            result.Add(e.Outcome is UninstallOutcome.Kept or UninstallOutcome.Failed ? e
                : Left(e, "it appeared during the removal and is still there"));
        }
        return result;
    }

    /// <summary>The note a removed item of a shared kind carries: it was put
    /// back, not deleted.</summary>
    private static string? RemovedNote(string kind) => kind switch
    {
        KindSharedKey or KindSharedValue => "put back as it was before HIDMaestro changed it, where it still held what HIDMaestro wrote",
        KindWinUsb => "the device's earlier driver is given back at its next arrival",
        _ => null,
    };

    private static List<HMUninstallEntry> ToPublic(List<UninstallEntry> entries) =>
        entries.Select(e => new HMUninstallEntry(e.Kind, e.Location, (HMUninstallOutcome)(int)e.Outcome, e.Reason)).ToList();

    private static IEnumerable<UninstallEntry> Safely(string what, Func<IEnumerable<UninstallEntry>> list)
    {
        try { return list().ToList(); }
        catch (Exception ex)
        {
            return new[] { new UninstallEntry(KindStep, what, UninstallOutcome.Failed, $"could not be listed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    // ── Running programs ─────────────────────────────────────────────────

    /// <summary>The other HIDMaestro programs that run now. A program on
    /// 2.0.0 or later holds its owner claim beside a record
    /// (<see cref="MachineClaims"/>). A v1.12.0 program holds the owner
    /// pipe of each transport index it has a controller on, which the census
    /// tells from a squatter by its driver's report or its sections
    /// (<see cref="OwnerCensus"/>), and its process id is the one its ledger
    /// holds or its driver's owner watch names. The calling process is
    /// never listed. A program older than v1.12.0 leaves no sign.</summary>
    internal static List<HMRunningProgram> OtherPrograms()
    {
        var list = new List<HMRunningProgram>();
        int self = Environment.ProcessId;
        var census = OwnerCensus.Take();
        var pids = new HashSet<int> { self };
        foreach (var r in census.Records)
            if (r.Live && !r.Self && pids.Add(r.Pid))
                list.Add(new HMRunningProgram(r.Pid, r.Image, r.SdkVersion));

        var accounted = new HashSet<int>();
        foreach (var r in census.Records)
            if (r.Live) foreach (var c in r.Controllers) if (c.TransportIndex >= 0) accounted.Add(c.TransportIndex);
        var legacy = new HashSet<int>();
        bool claimHeld = false;
        foreach (var d in census.Devices)
        {
            if (d.Owner == OwnerCensus.Owner.LiveLegacy) legacy.Add(d.ControllerIndex);
            else if (d.Owner == OwnerCensus.Owner.Live && d.Record == null) claimHeld = true;
            if (d.ControllerIndex >= 0) accounted.Add(d.ControllerIndex);
        }
        for (int n = 0; n < ControllerLease.LegacyClearance; n++)
            if (!accounted.Contains(n) && OwnerCensus.LegacyProgramOn(n)) legacy.Add(n);
        bool anyIndex = census.UnplacedLegacyProgram;
        if (legacy.Count > 0 || anyIndex)
        {
            var legacyPids = new SortedSet<int>();
            try
            {
                foreach (var (pid, index) in RegistryLedger.LiveHolders(Registry.LocalMachine, RegistryLedger.Root, RegistryLedger.ProcessCreateTimes))
                    if (!pids.Contains(pid) && (anyIndex || legacy.Contains(index))) legacyPids.Add(pid);
            }
            catch { }
            foreach (var d in census.Devices.Where(d => d.Owner == OwnerCensus.Owner.LiveLegacy))
                foreach (string id in d.InstanceIds)
                    if (OwnerCensus.ReadOwnerWatch(id) is { Connected: true } w && !pids.Contains(w.ProcessId))
                        legacyPids.Add(w.ProcessId);
            if (legacyPids.Count == 0) list.Add(new HMRunningProgram(0, null, "1.12.0"));
            foreach (int pid in legacyPids)
            {
                pids.Add(pid);
                list.Add(new HMRunningProgram(pid, ImageOf(pid), "1.12.0"));
            }
        }
        // A program holds a device's index or identity claim with no record
        // naming the device: one in the middle of a create or a sweep.
        if (claimHeld && list.Count == 0) list.Add(new HMRunningProgram(0, null, null));
        return list;
    }

    private static string? ImageOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainModule?.FileName;
        }
        catch { return null; }
    }

    // ── The list ─────────────────────────────────────────────────────────

    /// <summary>Every item a run would act on, with WouldRemove or Kept and
    /// the reason. Reads only. usbip-win2's items come from
    /// <see cref="UsbipRemoval.Remove"/>, which the caller adds.</summary>
    internal static List<UninstallEntry> Scan()
    {
        var list = new List<UninstallEntry>();
        void Part(string what, Action<List<UninstallEntry>> scan)
        {
            try { scan(list); }
            catch (Exception ex)
            {
                list.Add(new UninstallEntry(KindStep, what, UninstallOutcome.Failed, $"could not be listed: {ex.GetType().Name}: {ex.Message}"));
            }
        }
        Part("devnodes", ScanDevnodes);
        Part("interface registrations", ScanInterfaces);
        Part("shared keys", ScanSharedKeys);
        Part("OEM name overrides", ScanOemOverrides);
        Part("DeviceOverrides keys", ScanDeviceOverrides);
        Part("OpenVR registrations", ScanOpenVr);
        Part("driver packages", ScanPackages);
        Part("WUDF service keys", ScanWudf);
        Part("registry keys", ScanRegistryRoots);
        Part("certificate", ScanCertificate);
        Part("files", ScanFiles);
        Part("GameInputSvc", ScanGameInputService);
        return list;
    }

    private static UninstallEntry Would(string kind, string location) => new(kind, location, UninstallOutcome.WouldRemove);
    private static UninstallEntry Keep(string kind, string location, string reason) => new(kind, location, UninstallOutcome.Kept, reason);

    /// <summary>Every devnode the phantom pass counts as HIDMaestro's, and
    /// the HID children the Everything removal counts as HIDMaestro's.
    /// usbip-win2's host controllers carry the SDK's ROOT\HIDMAESTRO_UDE
    /// stamp and are <see cref="UsbipRemoval"/>'s to report.</summary>
    private static void ScanDevnodes(List<UninstallEntry> list)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, _) in DeviceManager.ListHmInstances())
            if (!IsUsbipHostController(id) && seen.Add(id)) list.Add(Would(KindDevnode, id));
        foreach (string id in DeviceOrchestrator.ListOwnedHidChildren())
            if (seen.Add(id)) list.Add(Would(KindDevnode, id));
    }

    private static bool IsUsbipHostController(string instanceId)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
            return k?.GetValue("HardwareID") is string[] ids
                && ids.Any(i => i.Equals(@"ROOT\USBIP_WIN2\UDE", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static void ScanInterfaces(List<UninstallEntry> list)
    {
        foreach (string path in DeviceOrchestrator.ListOwnedInterfaceEntries())
            list.Add(Would(KindInterface, $@"HKLM\{path}"));
    }

    /// <summary>The ledger's entries, each the shared key it puts back, and
    /// the GameInput tables older releases left.</summary>
    private static void ScanSharedKeys(List<UninstallEntry> list)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var gi = Registry.LocalMachine.OpenSubKey($@"{RegistryLedger.Root}\GameInput"))
            if (gi != null)
                foreach (string name in gi.GetSubKeyNames())
                    if (seen.Add(name)) list.Add(Would(KindSharedKey, $@"HKLM\{GameInputMapping.SystemRoot}\{name}"));
        using (var tables = Registry.LocalMachine.OpenSubKey(GameInputMapping.SystemRoot))
            if (tables != null)
                foreach (string name in tables.GetSubKeyNames())
                    if (!seen.Contains(name) && GameInputMapping.IsLegacyTable(name) && seen.Add(name))
                        list.Add(Would(KindSharedKey, $@"HKLM\{GameInputMapping.SystemRoot}\{name}"));
        using (var joy = Registry.LocalMachine.OpenSubKey($@"{RegistryLedger.Root}\JoystickOem"))
            if (joy != null)
                foreach (string name in joy.GetSubKeyNames())
                    list.Add(Would(KindSharedValue, $@"HKLM\{RegistryLedger.JoystickOemRoot}\{name}\OEMName"));
        using (var winUsb = Registry.LocalMachine.OpenSubKey($@"{RegistryLedger.Root}\WinUsb"))
            if (winUsb != null)
                foreach (string name in winUsb.GetSubKeyNames())
                {
                    using var e = winUsb.OpenSubKey(name);
                    string id = e?.GetValue("InstanceId") as string ?? name;
                    list.Add(DeviceManager.IsPresent(id) ? Keep(KindWinUsb, id, WinUsbPresentReason) : Would(KindWinUsb, id));
                }
    }

    private const string WinUsbPresentReason =
        "the device is plugged in and runs on HIDMaestro's WinUSB driver, which Windows changes back only once it is unplugged. Unplug it and run the removal again";

    /// <summary>Why a WinUSB package stays when no device on it is plugged
    /// in: WinUsbBinding.RemovePackage keeps a package a devnode record
    /// still names, and never removes that record, since every real adapter
    /// of the model gets the same instance ID and one plugged in during the
    /// removal would lose its install.</summary>
    private const string WinUsbRecordReason =
        "a device record Windows keeps for the adapter still names it, and that record is shared with every real adapter of the model, so HIDMaestro does not remove it";

    private static void ScanOemOverrides(List<UninstallEntry> list)
    {
        foreach (string vidPid in OemNameOverrideStore.PendingIdentities())
            list.Add(Would(KindOemOverride, $@"HKLM\{OemNameOverrideStore.PendingRecordPath(vidPid)}"));
    }

    /// <summary>A DeviceOverrides key goes when its name is the root
    /// hardware ID of a catalog profile, in either form a ROOT parent has
    /// (<see cref="PresentationPlan"/>: root\VID_xxxx&amp;PID_yyyy, and with
    /// &amp;IG_00 for an XUSB companion), its one subkey is <c>*</c>, that
    /// subkey holds only the DWORD <c>Removable</c> = 1 HIDMaestro writes
    /// (DeviceNodeCreator), and no ROOT device without a HIDMaestro
    /// hardware ID carries that hardware ID. Other root-enumerated drivers
    /// use the same form.</summary>
    private static void ScanDeviceOverrides(List<UninstallEntry> list)
    {
        foreach (var (name, keep) in DeviceOverrideCandidates())
            list.Add(keep == null ? Would(KindDeviceOverride, $@"HKLM\{DeviceOverridesRoot}\{name}")
                                  : Keep(KindDeviceOverride, $@"HKLM\{DeviceOverridesRoot}\{name}", keep));
    }

    private static List<(string Name, string? Keep)> DeviceOverrideCandidates()
    {
        var result = new List<(string, string?)>();
        using var root = Registry.LocalMachine.OpenSubKey(DeviceOverridesRoot);
        if (root == null) return result;
        var catalog = CatalogRootHardwareIds();
        HashSet<string>? foreign = null;
        foreach (string name in root.GetSubKeyNames())
        {
            if (!catalog.Contains(name)) continue;
            using var k = root.OpenSubKey(name);
            if (k == null) continue;
            string[] subs = k.GetSubKeyNames();
            if (k.ValueCount != 0 || subs.Length != 1 || subs[0] != "*")
            {
                result.Add((name, "it holds more than the Removable value HIDMaestro writes"));
                continue;
            }
            using var star = k.OpenSubKey("*");
            string[] values = star?.GetValueNames() ?? Array.Empty<string>();
            if (star == null || star.SubKeyCount != 0 || values.Length != 1
                || !values[0].Equals("Removable", StringComparison.OrdinalIgnoreCase)
                || star.GetValueKind("Removable") != RegistryValueKind.DWord || star.GetValue("Removable") is not 1)
            {
                result.Add((name, "it holds more than the Removable value HIDMaestro writes"));
                continue;
            }
            foreign ??= ForeignRootHardwareIds();
            if (foreign.Contains(name.Replace('#', '\\')))
            {
                result.Add((name, "a root-enumerated device of another driver carries this hardware ID"));
                continue;
            }
            result.Add((name, null));
        }
        return result;
    }

    /// <summary>The DeviceOverrides key names of the catalog's ROOT parents:
    /// root#VID_xxxx&amp;PID_yyyy and root#VID_xxxx&amp;PID_yyyy&amp;IG_00 for
    /// each profile's product ID and driver product ID.</summary>
    private static HashSet<string> CatalogRootHardwareIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ProfileDatabase.LoadEmbedded().All)
        {
            try
            {
                var pids = new List<ushort> { p.ProductId };
                if (p.ResolvedDriverPid is { } driverPid) pids.Add(Convert.ToUInt16(driverPid, 16));
                foreach (ushort pid in pids)
                {
                    string id = $"root#VID_{p.VendorId:X4}&PID_{pid:X4}";
                    set.Add(id);
                    set.Add(id + "&IG_00");
                }
            }
            catch { }
        }
        return set;
    }

    /// <summary>The hardware IDs of every ROOT device record that carries no
    /// HIDMaestro hardware ID.</summary>
    private static HashSet<string> ForeignRootHardwareIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT");
        if (root == null) return set;
        foreach (string e in root.GetSubKeyNames())
        {
            try
            {
                using var ek = root.OpenSubKey(e);
                if (ek == null) continue;
                foreach (string i in ek.GetSubKeyNames())
                {
                    using var ik = ek.OpenSubKey(i);
                    if (ik?.GetValue("HardwareID") is not string[] ids) continue;
                    if (ids.Any(x => x.Contains("HIDMaestro", StringComparison.OrdinalIgnoreCase))) continue;
                    foreach (string x in ids) set.Add(x);
                }
            }
            catch { }
        }
        return set;
    }

    /// <summary>Every path registry that names HIDMaestro's OpenVR driver
    /// folder: the calling account's (<see cref="OpenVrPathRegistry.FilePath"/>)
    /// and each profile's on the machine, SYSTEM's included.</summary>
    private static void ScanOpenVr(List<UninstallEntry> list)
    {
        foreach (var f in OpenVrFiles())
            list.Add(Would(KindOpenVr, f.File));
    }

    private const string OpenVrRelative = @"AppData\Local\openvr";
    private const string OpenVrFileName = "openvrpaths.vrpath";

    /// <summary>A path registry that names HIDMaestro's driver folder: the
    /// calling account's, edited as that account, or another account's at
    /// its default place below its profile folder (<paramref name="Anchor"/>),
    /// edited through held handles from there down. Another account's
    /// VR_PATHREG_OVERRIDE is not read.</summary>
    private sealed record OpenVrFile(string File, bool Caller, string? Anchor);

    private static List<OpenVrFile> OpenVrFiles()
    {
        string target = VrDriverBuilder.ExtractRoot;
        var found = new List<OpenVrFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Consider(string? file, string? anchor)
        {
            if (string.IsNullOrEmpty(file)) return;
            string full;
            try { full = Path.GetFullPath(file); } catch { return; }
            if (!seen.Add(full)) return;
            try
            {
                if (File.Exists(full) && OpenVrPathRegistry.ExternalDriversIn(full).Contains(target, StringComparer.Ordinal))
                    found.Add(new OpenVrFile(full, anchor == null, anchor));
            }
            catch { }
        }
        Consider(OpenVrPathRegistry.FilePath, anchor: null);
        foreach (string profile in ProfileFolders())
            Consider(Path.Combine(profile, OpenVrRelative, OpenVrFileName), profile);
        return found;
    }

    /// <summary>Each account's profile folder on the machine, from the
    /// ProfileList key, SYSTEM's and the service accounts' included.</summary>
    private static List<string> ProfileFolders()
    {
        var list = new List<string>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(ProfileListRoot);
            if (root == null) return list;
            foreach (string sid in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sid);
                if (k?.GetValue("ProfileImagePath") is string p && p.Length > 0) list.Add(p);
            }
        }
        catch { }
        return list;
    }

    /// <summary>One published package in the driver store's database.</summary>
    private sealed record Package(string Published, string InfName, string Id, bool Known)
    {
        public string Location => $"{Published} ({InfName})";
        public bool WinUsb => InfName.StartsWith("hmwinusb_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every package in the driver database
    /// (HKLM\SYSTEM\DriverDatabase\DriverPackages) whose provider is
    /// HIDMaestro, each marked Known when its original INF name is one a
    /// HIDMaestro release installed. The database holds a package whose
    /// oemNN.inf file is gone too, which a listing of the INF folder
    /// misses.</summary>
    private static List<Package> HidMaestroPackages()
    {
        var list = new List<Package>();
        using var root = Registry.LocalMachine.OpenSubKey(DriverPackagesRoot);
        if (root == null) return list;
        foreach (string id in root.GetSubKeyNames())
        {
            try
            {
                using var k = root.OpenSubKey(id);
                if (k == null) continue;
                if (!string.Equals(k.GetValue("Provider") as string, "HIDMaestro", StringComparison.OrdinalIgnoreCase)) continue;
                int dot = id.IndexOf(".inf_", StringComparison.OrdinalIgnoreCase);
                string inf = k.GetValue("InfName") as string ?? (dot > 0 ? id.Substring(0, dot + 4) : id);
                string? published = k.GetValue("") as string;
                if (string.IsNullOrEmpty(published)) continue;
                bool known = DriverInfNames.Any(n => inf.Equals(n, StringComparison.OrdinalIgnoreCase))
                             || inf.StartsWith("hmwinusb_", StringComparison.OrdinalIgnoreCase);
                list.Add(new Package(published, inf, id, known));
            }
            catch { }
        }
        return list;
    }

    private const string UnknownPackageReason =
        "its provider is HIDMaestro, but no HIDMaestro release installed a package of that name, so it is left to whoever installed it";

    private static void ScanPackages(List<UninstallEntry> list)
    {
        bool winUsbPresent = WinUsbDevicePresent();
        foreach (var p in HidMaestroPackages())
            list.Add(!p.Known ? Keep(KindPackage, p.Location, UnknownPackageReason)
                : p.WinUsb && winUsbPresent ? Keep(KindPackage, p.Location, WinUsbPresentReason)
                : Would(KindPackage, p.Location));
    }

    /// <summary>Whether a device the ledger bound to HIDMaestro's WinUSB
    /// driver is plugged in.</summary>
    private static bool WinUsbDevicePresent()
    {
        using var winUsb = Registry.LocalMachine.OpenSubKey($@"{RegistryLedger.Root}\WinUsb");
        if (winUsb == null) return false;
        foreach (string name in winUsb.GetSubKeyNames())
        {
            using var e = winUsb.OpenSubKey(name);
            if (e?.GetValue("InstanceId") is string id && DeviceManager.IsPresent(id)) return true;
        }
        return false;
    }

    /// <summary>A WUDF service key goes once the driver store folder its
    /// ImagePath names is gone, and only when that folder is one of
    /// HIDMaestro's packages' (hidmaestro*.inf_*). Windows makes these keys
    /// from the INFs' UmdfService lines, and deleting the package takes
    /// their values and leaves the key with its empty Parameters and State
    /// subkeys (measured in Windows Sandbox, 2026-10-20). Such a key, under
    /// one of HIDMaestro's UMDF service names and holding no value anywhere,
    /// goes too. Windows' own
    /// mshidumdf and WUDFRd keys are never touched.</summary>
    private static void ScanWudf(List<UninstallEntry> list)
    {
        bool packageKept = PackagesTheRunKeeps().Any(p => !p.WinUsb);
        foreach (string name in WudfServiceNames)
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{WudfServicesRoot}\{name}");
            if (k == null) continue;
            string location = $@"HKLM\{WudfServicesRoot}\{name}";
            list.Add(WudfFolder(k) == null && !EmptySkeleton(k)
                ? Keep(KindWudf, location, $"its ImagePath, {k.GetValue("ImagePath") ?? "none"}, is not in a HIDMaestro driver package's folder")
                : packageKept ? Keep(KindWudf, location, WudfKeptReason)
                : Would(KindWudf, location));
        }
    }

    private const string WudfKeptReason = "a HIDMaestro driver package is still in the driver store, and its service needs the key";

    /// <summary>A key that holds no value, itself or in any subkey.</summary>
    private static bool EmptySkeleton(RegistryKey k)
    {
        if (k.ValueCount != 0) return false;
        foreach (string s in k.GetSubKeyNames())
        {
            using var c = k.OpenSubKey(s);
            if (c == null || !EmptySkeleton(c)) return false;
        }
        return true;
    }

    /// <summary>The driver store folder a WUDF service key's ImagePath
    /// names when it is one of HIDMaestro's, and null otherwise.</summary>
    private static string? WudfFolder(RegistryKey k)
    {
        if (k.GetValue("ImagePath") is not string image || image.Length == 0) return null;
        string? folder;
        try { folder = Path.GetDirectoryName(Path.GetFullPath(Environment.ExpandEnvironmentVariables(image))); }
        catch { return null; }
        if (folder == null) return null;
        string repo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
        string name = Path.GetFileName(folder);
        return string.Equals(Path.GetDirectoryName(folder), repo, StringComparison.OrdinalIgnoreCase)
               && name.StartsWith("hidmaestro", StringComparison.OrdinalIgnoreCase)
               && name.Contains(".inf_", StringComparison.OrdinalIgnoreCase)
            ? folder : null;
    }

    private static void ScanRegistryRoots(List<UninstallEntry> list)
    {
        foreach (string root in RegistryRoots)
        {
            using var k = Registry.LocalMachine.OpenSubKey(root);
            if (k == null) continue;
            if (root == RegistryLedger.Root && WinUsbDevicePresent())
                list.Add(Keep(KindRegistryKey, $@"HKLM\{root}", "it records the driver to give back to a plugged-in device on HIDMaestro's WinUSB driver. Unplug it and run the removal again"));
            else if (root == PackagesLeftKey && WinUsbDevicePresent())
                list.Add(Keep(KindRegistryKey, $@"HKLM\{root}", WinUsbPresentReason));
            else
                list.Add(Would(KindRegistryKey, $@"HKLM\{root}"));
        }
    }

    private static readonly StoreName[] CertificateStores = { StoreName.My, StoreName.Root, StoreName.TrustedPublisher };

    private static string CertificateSubject => "CN=" + DriverBuilder.CertFriendlyName;

    private static void ScanCertificate(List<UninstallEntry> list)
    {
        // A package the run will keep keeps the certificate too
        // (ActCertificate), and the preview says so.
        bool kept = PackagesTheRunKeeps().Count > 0;
        foreach (var name in CertificateStores)
        {
            using var store = new X509Store(name, StoreLocation.LocalMachine);
            try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); }
            catch (CryptographicException) { continue; }
            foreach (var cert in store.Certificates)
                using (cert)
                    if (string.Equals(cert.Subject, CertificateSubject, StringComparison.Ordinal))
                    {
                        string location = $@"LocalMachine\{name}\{cert.Thumbprint}";
                        list.Add(kept ? Keep(KindCertificate, location, CertificateKeptReason) : Would(KindCertificate, location));
                    }
        }
    }

    private const string CertificateKeptReason =
        "a driver package whose provider is HIDMaestro stays in the driver store, signed with it";

    /// <summary>The HIDMaestro packages a run leaves as they are: one under
    /// a name no HIDMaestro release used, and a WinUSB package while its
    /// device is plugged in. One a device still uses at the run's time is
    /// found only by the run.</summary>
    private static List<Package> PackagesTheRunKeeps()
    {
        bool winUsbPresent = WinUsbDevicePresent();
        return HidMaestroPackages().Where(p => !p.Known || (p.WinUsb && winUsbPresent)).ToList();
    }

    /// <summary>The folders a removal deletes: %ProgramFiles%\HIDMaestro,
    /// %ProgramData%\HIDMaestro and the folders a repair moved aside beside
    /// it, and in each account's %TEMP% and C:\Windows\Temp the folders the
    /// SDK through v1.12.0 and its test program wrote there, by their exact
    /// names. Each is listed and later deleted from a trusted anchor down,
    /// through held handles, without following a link (SafeDelete).</summary>
    private static void ScanFiles(List<UninstallEntry> list)
    {
        foreach (var t in FileTargets()) list.Add(t.Entry);
    }

    /// <summary>One folder a removal deletes or keeps: its entry, and the
    /// anchor and the path below it the delete walks. Only one under
    /// %ProgramFiles%\HIDMaestro may leave an entry in use to the next
    /// start.</summary>
    private sealed record FileTarget(UninstallEntry Entry, string Anchor, string Relative, bool InProtected);

    private static string ProgramFilesAnchor => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static string ProgramDataAnchor => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string WindowsAnchor => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static List<FileTarget> FileTargets()
    {
        var list = new List<FileTarget>();
        OwnFolder(list, ProgramFilesAnchor, "HIDMaestro", inProtected: true, name =>
            IsOrAside(name, "Staging") || IsOrAside(name, "childtemp"));
        // Staging lived here in builds made while 2.0.0 was in development
        // (ProtectedStaging at 16e65f53), before it moved to Program Files.
        OwnFolder(list, ProgramDataAnchor, "HIDMaestro", inProtected: false, name =>
            IsOrAside(name, "openvr") || IsOrAside(name, "usbip") || IsOrAside(name, "Staging"));
        var top = ListThrough(ProgramDataAnchor, "", out _);
        if (top != null)
            foreach (var e in top)
                if (e.Name.StartsWith("HIDMaestro.untrusted-", StringComparison.OrdinalIgnoreCase))
                    list.Add(new FileTarget(Would(KindFolder, Path.Combine(ProgramDataAnchor, e.Name)), ProgramDataAnchor, e.Name, false));
        foreach (var (anchor, relative) in TempFolders())
        {
            string temp = Path.Combine(anchor, relative);
            var entries = ListThrough(anchor, relative, out string? refusal);
            if (entries == null)
            {
                if (refusal != null)
                    list.Add(new FileTarget(Keep(KindFolder, temp,
                        $"HIDMaestro does not look in this %TEMP%: {refusal}. Folders HIDMaestro left there, if any, stay"), anchor, relative, false));
                continue;
            }
            foreach (var e in entries)
                if (IsLegacyTempName(e.Name))
                    list.Add(new FileTarget(Would(KindFolder, Path.Combine(temp, e.Name)), anchor, Path.Combine(relative, e.Name), false));
        }
        return list;
    }

    /// <summary>The entries of a folder, listed through a chain of held
    /// handles from <paramref name="anchor"/> down. Null when the folder is
    /// missing, or, with the reason, when the chain was refused.</summary>
    private static List<SafeDelete.Entry>? ListThrough(string anchor, string relative, out string? refusal)
    {
        using var chain = SafeDelete.OpenChain(anchor, relative, out refusal);
        if (chain == null) return null;
        try { return SafeDelete.List(chain.Last); }
        catch (Exception e)
        {
            refusal = $"{chain.Path} could not be listed: {e.Message}";
            return null;
        }
    }

    /// <summary>A folder HIDMaestro made: the whole folder when it holds only
    /// what HIDMaestro puts there, otherwise each of those and the folder
    /// kept with the reason.</summary>
    private static void OwnFolder(List<FileTarget> list, string anchor, string relative, bool inProtected, Func<string, bool> known)
    {
        string root = Path.Combine(anchor, relative);
        var children = ListThrough(anchor, relative, out string? refusal);
        if (children == null)
        {
            // A link under HIDMaestro's own name, which a program without
            // elevation can plant at %ProgramData%\HIDMaestro: it is not
            // walked, and it goes as a link (SafeDelete.Tree).
            if (refusal != null && SafeDelete.LeafIsLink(anchor, relative))
                list.Add(new FileTarget(Would(KindFolder, root), anchor, relative, inProtected));
            else if (refusal != null)
                list.Add(new FileTarget(new UninstallEntry(KindFolder, root, UninstallOutcome.Failed, refusal), anchor, relative, inProtected));
            return;
        }
        var unknown = children.Where(c => !known(c.Name)).ToList();
        if (unknown.Count == 0)
        {
            list.Add(new FileTarget(Would(KindFolder, root), anchor, relative, inProtected));
            return;
        }
        foreach (var c in children.Where(c => known(c.Name)))
            list.Add(new FileTarget(Would(KindFolder, Path.Combine(root, c.Name)), anchor, Path.Combine(relative, c.Name), inProtected));
        list.Add(new FileTarget(Keep(KindFolder, root,
            $"it also holds what HIDMaestro did not write: {string.Join(", ", unknown.Select(u => u.Name))}"), anchor, relative, inProtected));
    }

    private static bool IsOrAside(string name, string own) =>
        name.Equals(own, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(own + ".untrusted-", StringComparison.OrdinalIgnoreCase);

    /// <summary>The folders the SDK through v1.12.0 left in %TEMP%:
    /// HIDMaestro (hmswd.exe and the diagnostic logs),
    /// HIDMaestro_&lt;payload hash or GUID&gt; (the driver payload),
    /// HIDMaestro_usbip_&lt;version&gt; (the usbip-win2 installer), and the
    /// test program's HIDMaestroEmulate-&lt;GUID&gt;. Nothing else in %TEMP%
    /// is touched.</summary>
    internal static bool IsLegacyTempName(string name)
    {
        if (name.Equals("HIDMaestro", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("HIDMaestro_usbip_", StringComparison.OrdinalIgnoreCase))
        {
            string ver = name.Substring("HIDMaestro_usbip_".Length);
            return ver.Length > 0 && ver.All(c => char.IsAsciiDigit(c) || c == '.');
        }
        if (name.StartsWith("HIDMaestro_", StringComparison.OrdinalIgnoreCase))
        {
            string tail = name.Substring("HIDMaestro_".Length);
            return tail.Length >= 8 && tail.All(c => char.IsAsciiHexDigit(c) || c == '-');
        }
        if (name.StartsWith("HIDMaestroEmulate-", StringComparison.OrdinalIgnoreCase))
        {
            string tail = name.Substring("HIDMaestroEmulate-".Length);
            return tail.Length >= 8 && tail.All(c => char.IsAsciiHexDigit(c) || c == '-');
        }
        return false;
    }

    /// <summary>Each account's %TEMP% (AppData\Local\Temp below its profile
    /// folder), Windows' own Temp folder, and the calling process's %TEMP%
    /// when it lies below one of those anchors, each as an anchor and the
    /// path below it. A %TEMP% elsewhere is not reached: no anchor below
    /// which it could be walked without following a link is known for
    /// it.</summary>
    private static List<(string Anchor, string Relative)> TempFolders()
    {
        var list = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anchors = new List<string> { WindowsAnchor };
        anchors.AddRange(ProfileFolders());
        void Add(string anchor, string relative)
        {
            string full;
            try { full = Path.GetFullPath(Path.Combine(anchor, relative)).TrimEnd('\\'); } catch { return; }
            if (seen.Add(full)) list.Add((Path.GetFullPath(anchor).TrimEnd('\\'), relative));
        }
        Add(WindowsAnchor, "Temp");
        foreach (string profile in ProfileFolders()) Add(profile, @"AppData\Local\Temp");
        try
        {
            string own = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
            foreach (string a in anchors)
            {
                string anchor = Path.GetFullPath(a).TrimEnd('\\');
                if (own.StartsWith(anchor + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    Add(anchor, own.Substring(anchor.Length + 1));
                    break;
                }
            }
        }
        catch { }
        return list;
    }

    private static void ScanGameInputService(List<UninstallEntry> list)
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\GameInputSvc");
        if (k?.GetValue("Start") is 2)
            list.Add(Keep(KindServiceSetting, "GameInputSvc start type (automatic)",
                "HIDMaestro sets GameInputSvc to start automatically when it finds the service stopped, and records no earlier value, so a removal leaves it as it is"));
    }

    // ── The steps ────────────────────────────────────────────────────────

    /// <summary>The phantom pass: the background cleanup's walk over every
    /// device class (DeviceManager.RemoveAccumulatedHmPhantoms), run here on
    /// this thread to its end, with no budget, each uninstall waiting for the
    /// PnP lock as any removal waits. It runs before the devices go, and
    /// again after, for the records SwDeviceClose keeps of the software
    /// devices the Everything removal closes. usbip-win2's host controller
    /// records carry the SDK's ROOT\HIDMAESTRO_UDE stamp, and are left to
    /// UsbipRemoval, whatever IncludeUsbip says.</summary>
    private static void PhantomPass(string which)
    {
        int removed = DeviceManager.RemoveAccumulatedHmPhantoms(include: id => !IsUsbipHostController(id));
        DeviceOrchestrator.LogDiag($"uninstall: the {which} phantom pass removed {removed}");
    }

    private static void ActOemOverrides(Told told)
    {
        foreach (string vidPid in OemNameOverrideStore.PendingIdentities())
        {
            string location = $@"HKLM\{OemNameOverrideStore.PendingRecordPath(vidPid)}";
            try
            {
                var r = OemNameOverrideStore.RestoreForUninstall(vidPid);
                if (r.RecordKept)
                    told.Add(new UninstallEntry(KindOemOverride, location, UninstallOutcome.Kept, r.Reason));
                else if (r.Reason != null)
                    told.Add(new UninstallEntry(KindOemOverride, location, UninstallOutcome.Removed, r.Reason));
            }
            catch (Exception ex)
            {
                told.Add(new UninstallEntry(KindOemOverride, location, UninstallOutcome.Failed, ex.Message));
            }
        }
    }

    private static void ActDeviceOverrides(Told told)
    {
        foreach (var (name, keep) in DeviceOverrideCandidates())
        {
            if (keep != null) continue;
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(DeviceOverridesRoot, writable: true);
                root?.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
            }
            catch (Exception ex)
            {
                told.Add(new UninstallEntry(KindDeviceOverride, $@"HKLM\{DeviceOverridesRoot}\{name}", UninstallOutcome.Failed, ex.Message));
            }
        }
    }

    private static void ActOpenVr(Told told)
    {
        string target = VrDriverBuilder.ExtractRoot;
        foreach (var f in OpenVrFiles())
        {
            try
            {
                if (f.Caller) OpenVrPathRegistry.RemoveDriver(target);
                else OpenVrPathRegistry.RemoveDriverIn(f.Anchor!, OpenVrRelative, OpenVrFileName, target);
            }
            catch (Exception ex)
            {
                told.Add(new UninstallEntry(KindOpenVr, f.File, UninstallOutcome.Failed, ex.Message));
            }
        }
    }

    /// <summary>Each HIDMaestro package with SetupUninstallOEMInf and no
    /// SUOI_FORCEDELETE, the delete devcon's dp_delete makes without -f
    /// (Windows-driver-samples setup\devcon\cmds.cpp, cmdDPDelete) and pnputil
    /// /delete-driver makes without /uninstall or /force: a package a device
    /// still uses stays, and its entry says so. The devices are gone and
    /// their hosts drained by now, and one still letting go of the driver
    /// answers ERROR_INF_IN_USE_BY_DEVICES for a moment, which is tried again.
    /// The WinUSB packages go through <see cref="WinUsbBinding.RemovePackage"/>,
    /// which keeps one a devnode still uses. The caller holds the deploy
    /// lock.</summary>
    private static void ActPackages(Told told)
    {
        foreach (var p in HidMaestroPackages().Where(p => p.Known && !p.WinUsb))
        {
            int error = 0;
            bool done = false;
            for (int attempt = 0; attempt < 5 && !done; attempt++)
            {
                if (attempt > 0) Thread.Sleep(TimeoutScale.Apply(1000));
                done = SetupUninstallOEMInf(p.Published, 0, IntPtr.Zero);
                error = done ? 0 : Marshal.GetLastPInvokeError();
                if (!done && error != ERROR_INF_IN_USE_BY_DEVICES) break;
            }
            if (!done)
                told.Add(new UninstallEntry(KindPackage, p.Location, UninstallOutcome.Failed,
                    error == ERROR_INF_IN_USE_BY_DEVICES
                        ? "a device still uses it, and HIDMaestro never forces a package out from under a device"
                        : $"SetupUninstallOEMInf failed with error 0x{error:X8}"));
        }
        PnputilHelper.InvalidateInstalledCache();
        DriverDeploy.ForgetConfirmed();
        WinUsbBinding.RemovePackage();
        if (HidMaestroPackages().Any(p => p.WinUsb))
        {
            string[] noted = RegistryLedger.NotedPackages(Registry.LocalMachine);
            string reason = WinUsbDevicePresent() ? WinUsbPresentReason : WinUsbRecordReason;
            foreach (var p in HidMaestroPackages().Where(p => p.WinUsb))
                if (noted.Contains(p.InfName, StringComparer.OrdinalIgnoreCase))
                    told.Add(new UninstallEntry(KindPackage, p.Location, UninstallOutcome.Kept, reason));
        }
    }

    // SetupAPI.h: ERROR_INF_IN_USE_BY_DEVICES, APPLICATION_ERROR_MASK |
    // ERROR_SEVERITY_ERROR | 0x23D, the error devcon's dp_delete reads.
    private const int ERROR_INF_IN_USE_BY_DEVICES = unchecked((int)0xE000023D);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupUninstallOEMInfW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupUninstallOEMInf(string infFileName, uint flags, IntPtr reserved);

    /// <summary>A WUDF service key goes only once no HIDMaestro driver package
    /// but a WinUSB one is left: the key belongs to a package's UmdfService,
    /// and a package left behind still needs it.</summary>
    private static void ActWudf(Told told)
    {
        bool packageLeft = HidMaestroPackages().Any(p => !p.WinUsb);
        foreach (string name in WudfServiceNames)
        {
            string path = $@"{WudfServicesRoot}\{name}";
            string? folder;
            bool skeleton;
            using (var k = Registry.LocalMachine.OpenSubKey(path))
            {
                if (k == null) continue;
                folder = WudfFolder(k);
                skeleton = folder == null && EmptySkeleton(k);
            }
            if (folder == null && !skeleton) continue;
            if (packageLeft || (folder != null && Directory.Exists(folder)))
            {
                told.Add(new UninstallEntry(KindWudf, $@"HKLM\{path}", UninstallOutcome.Kept, WudfKeptReason));
                continue;
            }
            try { Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
            catch (Exception ex) { told.Add(new UninstallEntry(KindWudf, $@"HKLM\{path}", UninstallOutcome.Failed, ex.Message)); }
        }
    }

    private static void ActRegistryRoots(Told told)
    {
        void Fail(string root, string why) => told.Add(new UninstallEntry(KindRegistryKey, $@"HKLM\{root}", UninstallOutcome.Failed, why));

        try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\HIDMaestro", throwOnMissingSubKey: false); }
        catch (Exception ex) { Fail(@"SOFTWARE\HIDMaestro", ex.Message); }

        // This process's own record, when it is a program with a claim, and
        // every record whose program has exited. DeleteRecord drops the root
        // once it is empty.
        if (MachineClaims.HoldsOwnerClaim) MachineClaims.DeleteOwnRecord();
        foreach (var r in MachineClaims.ReadAll())
            if (!r.Live) MachineClaims.DeleteRecord(r.OwnerId);
        bool ownersEmpty;
        using (var o = Registry.LocalMachine.OpenSubKey(MachineClaims.RecordsRoot))
            ownersEmpty = o != null && o.SubKeyCount == 0 && o.ValueCount == 0;
        if (ownersEmpty)
            try { Registry.LocalMachine.DeleteSubKey(MachineClaims.RecordsRoot, throwOnMissingSubKey: false); }
            catch (Exception ex) { Fail(MachineClaims.RecordsRoot, ex.Message); }

        // The override root once its records are gone: a record kept for a
        // later run stays, and its entry says why.
        const string overrides = @"SOFTWARE\HIDMaestroOemOverrides";
        bool overridesEmpty;
        using (var o = Registry.LocalMachine.OpenSubKey(overrides))
            overridesEmpty = o != null && o.SubKeyCount == 0;
        if (overridesEmpty)
            try { Registry.LocalMachine.DeleteSubKeyTree(overrides, throwOnMissingSubKey: false); }
            catch (Exception ex) { Fail(overrides, ex.Message); }

        // The ledger goes once the Everything removal has put back every
        // shared key it records (RegistryLedger.ReleaseAll deletes it then).
        // An entry left is the only record of a key's earlier value, so a
        // ledger still holding a GameInput or joy.cpl entry stays.
        int EntriesOf(string kind)
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{RegistryLedger.Root}\{kind}");
            return k?.SubKeyCount ?? 0;
        }
        bool winUsbLeft = EntriesOf("WinUsb") > 0;
        int sharedLeft = EntriesOf("GameInput") + EntriesOf("JoystickOem");
        if (sharedLeft > 0)
            Fail(RegistryLedger.Root, $"{sharedLeft} shared key(s) it records were not put back, and it is their only record of what they held before");
        else if (!winUsbLeft)
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(RegistryLedger.Root, throwOnMissingSubKey: false); }
            catch (Exception ex) { Fail(RegistryLedger.Root, ex.Message); }
        }

        // Under the deploy lock, which the caller holds, as every change to
        // the note is (RegistryLedger.Note).
        if (!HidMaestroPackages().Any(p => p.WinUsb))
        {
            try { RegistryLedger.WriteNote(Registry.LocalMachine, null); }
            catch (Exception ex) { Fail(PackagesLeftKey, ex.Message); }
        }
        else if (Registry.LocalMachine.OpenSubKey(PackagesLeftKey) is { } note)
        {
            note.Dispose();
            told.Add(new UninstallEntry(KindRegistryKey, $@"HKLM\{PackagesLeftKey}", UninstallOutcome.Kept,
                "it names the WinUSB package that stays, so a later HIDMaestro program can remove it once nothing uses it"));
        }
        if (winUsbLeft && sharedLeft == 0)
            told.Add(new UninstallEntry(KindRegistryKey, $@"HKLM\{RegistryLedger.Root}", UninstallOutcome.Kept,
                "it records the driver to give back to a device on HIDMaestro's WinUSB driver"));
    }

    /// <summary>The certificate in LocalMachine My, Root and
    /// TrustedPublisher, once no package whose provider is HIDMaestro is
    /// left in the driver store, whatever its name: every version and every
    /// program signs with it, and a package left behind is one a later
    /// program would install without a trusted signer. The copies go first
    /// and the private key last, through the key's own handle, and only once
    /// the copy in My is gone: a copy left in My without its key would never
    /// be made again (DriverBuilder.HasTestCertificate).</summary>
    private static void ActCertificate(Told told)
    {
        if (HidMaestroPackages().Count > 0)
        {
            ScanCertificateInto(told, UninstallOutcome.Kept, CertificateKeptReason);
            return;
        }
        var keys = new List<RSA>();
        bool myRemoved = true;
        try
        {
            foreach (var name in CertificateStores)
            {
                using var store = new X509Store(name, StoreLocation.LocalMachine);
                try { store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly); }
                catch (CryptographicException) { continue; }
                foreach (var cert in store.Certificates)
                    using (cert)
                    {
                        if (!string.Equals(cert.Subject, CertificateSubject, StringComparison.Ordinal)) continue;
                        string location = $@"LocalMachine\{name}\{cert.Thumbprint}";
                        // The copies in Root and TrustedPublisher name the
                        // same key as the one in My (CreateTestCertificate
                        // adds one certificate to all three), so the key is
                        // opened once, from My, and held until the end.
                        if (name == StoreName.My && cert.HasPrivateKey)
                            try { if (cert.GetRSAPrivateKey() is { } rsa) keys.Add(rsa); }
                            catch (Exception ex) { told.Add(new UninstallEntry(KindCertificate, location, UninstallOutcome.Failed, $"its private key could not be opened: {ex.Message}")); }
                        try { store.Remove(cert); }
                        catch (Exception ex)
                        {
                            if (name == StoreName.My) myRemoved = false;
                            told.Add(new UninstallEntry(KindCertificate, location, UninstallOutcome.Failed, ex.Message));
                        }
                    }
            }
            if (myRemoved)
                foreach (var rsa in keys)
                    try { DeletePrivateKey(rsa); }
                    catch (Exception ex)
                    {
                        told.Add(new UninstallEntry(KindCertificate, "private key of " + CertificateSubject, UninstallOutcome.Failed, ex.Message));
                    }
        }
        finally
        {
            foreach (var rsa in keys) rsa.Dispose();
        }
        DriverBuilder.ForgetSignedPackage();
    }

    private static void ScanCertificateInto(Told told, UninstallOutcome outcome, string reason)
    {
        var list = new List<UninstallEntry>();
        ScanCertificate(list);
        foreach (var e in list) told.Add(e with { Outcome = outcome, Reason = reason });
    }

    /// <summary>The certificate's machine key: a CNG key is deleted through
    /// its handle, a CryptoAPI key container by clearing it with
    /// PersistKeyInCsp false.</summary>
    private static void DeletePrivateKey(RSA rsa)
    {
        switch (rsa)
        {
            case RSACng cng:
                cng.Key.Delete();
                break;
            case RSACryptoServiceProvider csp:
                csp.PersistKeyInCsp = false;
                csp.Clear();
                break;
        }
    }

    private static void ActFiles(Told told)
    {
        foreach (var t in FileTargets())
        {
            if (t.Entry.Outcome is UninstallOutcome.Kept or UninstallOutcome.Failed) { told.Add(t.Entry); continue; }
            var (outcome, reason) = SafeDelete.Tree(t.Anchor, t.Relative, t.InProtected);
            if (outcome != UninstallOutcome.Removed) told.Add(t.Entry with { Outcome = outcome, Reason = reason });
        }
        ProtectedStaging.ForgetChildTemp();
    }
}
