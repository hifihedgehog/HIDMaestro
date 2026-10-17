using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace HIDMaestro.Internal;

/// <summary>
/// Which driver a machine runs when several programs that embed HIDMaestro,
/// of several versions, share it.
///
/// <list type="bullet">
/// <item>No driver installed: install this SDK's.</item>
/// <item>The same payload: nothing, the fast path, outside any lock.</item>
/// <item>A newer driver: never downgraded. This SDK runs on it.</item>
/// <item>An older driver with nothing on it: upgrade.</item>
/// <item>An older driver that controllers of running programs (or of
/// programs whose state cannot be known) run on: kept, and this SDK runs on
/// it when it is <see cref="MinimumDriverVersion"/>, 2.0.0, or later, the
/// first driver of protocol 4. Otherwise
/// <see cref="HMDriverVersionException"/> names what holds it, and a
/// create meets the same rule at its deploy gate
/// (<see cref="EnsureForCreate"/>), before any PnP work.</item>
/// </list>
///
/// <para>Whether a create may run on such a driver anyway is the owner's
/// choice between the design note's two alternatives, decision (c):
/// <see cref="Coexistence"/>.</para>
///
/// <para>Every deploy of this driver, every WinUSB package add, every
/// cleanup that removes the packages, and every change to the note of
/// WinUSB packages a cleanup left (RegistryLedger) holds the machine-wide
/// mutex <see cref="MutexObject"/> for the length of one call. It lives in
/// <see cref="AdminNamespace"/>, so a program that is not elevated can
/// neither take the name first nor hold the lock. The holder re-reads the
/// installed state after acquiring it, so of two programs calling
/// InstallDriver at once, one deploys and the other finds the result
/// current. The USB/IP transport's install and repair hold a lock of their
/// own (<see cref="Usbip.UsbipDriverInstaller.TransportMutexObject"/>), so a
/// UMDF2 create never waits for them.</para>
/// </summary>
internal static partial class DriverDeploy
{
    /// <summary>The deploy mutex's name in <see cref="AdminNamespace"/>.</summary>
    internal const string MutexObject = "Deploy";

    /// <summary>The oldest driver this SDK runs on: 2.0.0, the first of
    /// protocol 4, the version contract under which a driver reads a
    /// controller's v2.0.0 values (decision (c)).</summary>
    internal static readonly Version MinimumDriverVersion = new(2, 0, 0);

    /// <summary>What a create does on a driver older than
    /// <see cref="MinimumDriverVersion"/> that controllers of other programs
    /// hold: the owner's choice between the design note's alternatives A
    /// and B of decision (c), and the one place it is made.</summary>
    internal enum CoexistenceRule
    {
        /// <summary>Alternative A: every UMDF2 create throws
        /// <see cref="HMDriverVersionException"/>, whatever the profile.
        /// No v2.0.0 controller runs on a driver older than 2.0.0, and the
        /// post-start check requires protocol 4 on every create.</summary>
        RefuseEveryCreate,

        /// <summary>Alternative B: a create of a profile that passes
        /// <see cref="ControllerProfile.RunsOnOlderDriver"/> runs on that
        /// driver, and the post-start check accepts a driver that writes no
        /// version section for it. Every other create throws.</summary>
        AllowModelFreeProfiles,
    }

    /// <summary>The coexistence rule in force. The owner has not chosen
    /// yet: this is alternative A, and initializing it to
    /// <see cref="CoexistenceRule.AllowModelFreeProfiles"/> is the whole
    /// switch to B. A field, so a check runs the gate under both.</summary>
    internal static CoexistenceRule Coexistence = CoexistenceRule.RefuseEveryCreate;

    /// <summary>The driver store, the census, the deploy lock and the
    /// deploy steps, behind one object a check can replace to give the
    /// policy a machine of its own.</summary>
    internal class DeployMachine
    {
        internal virtual IDisposable Lock() => new Held(Acquire());
        internal virtual List<DriverStamp> Stamps(string infName) => InstalledStamps(infName);
        internal virtual DriverStamp Embedded(string infName) => EmbeddedStamp(infName);
        internal virtual OwnerCensus Census() => OwnerCensus.Take();
        internal virtual bool FastPath() => DriverBuilder.WillTakeFastPath();
        internal virtual void MarkConfirmed() => PnputilHelper.MarkInstalledConfirmed();
        internal virtual string? ManifestHash() => DriverBuilder.InstalledManifestHash();
        internal virtual bool StoreHasBothPackages() => DriverBuilder.DriverStoreHasBothPackages();
        internal virtual void Deploy() => DriverBuilder.DeployLocked();
        internal virtual void AddPackages(IReadOnlyCollection<string> infs) => DriverBuilder.AddPackagesLocked(infs);
        internal virtual void RecordPayload() => DriverBuilder.RecordInstalledPayload();
        internal virtual void PruneStaging() => ProtectedStaging.PruneOldVersions();

        private sealed class Held(Mutex mutex) : IDisposable
        {
            public void Dispose() => mutex.ReleaseMutex();
        }
    }

    /// <summary>The machine the policy acts on.</summary>
    internal static DeployMachine Machine = new();

    /// <summary>A driver package's version stamp, from its INF's
    /// <c>DriverVer</c> line: <c>MM/dd/yyyy,major.minor.patch.build</c>.</summary>
    internal readonly record struct DriverStamp(DateTime Date, Version Version) : IComparable<DriverStamp>
    {
        /// <summary>Release version first, then the build date, then the
        /// build number. A release is newer than another by its version
        /// even when the other was built later.</summary>
        public int CompareTo(DriverStamp other)
        {
            var a = new Version(Version.Major, Version.Minor, Math.Max(0, Version.Build));
            var b = new Version(other.Version.Major, other.Version.Minor, Math.Max(0, other.Version.Build));
            int c = a.CompareTo(b);
            if (c != 0) return c;
            c = Date.CompareTo(other.Date);
            if (c != 0) return c;
            return Math.Max(0, Version.Revision).CompareTo(Math.Max(0, other.Version.Revision));
        }

        public Version Release => new(Version.Major, Version.Minor, Math.Max(0, Version.Build));
        public override string ToString() => Version.ToString();
    }

    [GeneratedRegex(@"(?m)^\s*DriverVer\s*=\s*(\d{1,2})/(\d{1,2})/(\d{4})\s*,\s*(\d+)\.(\d+)\.(\d+)\.(\d+)")]
    private static partial Regex DriverVerLine();

    internal static DriverStamp? ParseDriverVer(string infText)
    {
        var m = DriverVerLine().Match(infText);
        if (!m.Success) return null;
        int I(int g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        try
        {
            return new DriverStamp(new DateTime(I(3), I(1), I(2)), new Version(I(4), I(5), I(6), I(7)));
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>The newest HIDMaestro driver package in the driver store,
    /// or null when none is installed.</summary>
    internal static DriverStamp? InstalledDriver()
    {
        var stamps = Machine.Stamps("hidmaestro.inf");
        return stamps.Count == 0 ? null : stamps.Max();
    }

    /// <summary>True when either INF's newest package in the driver store
    /// is older than <see cref="MinimumDriverVersion"/>, or one has none.
    /// Both count: a half-failed install can leave a 2.0.0
    /// <c>hidmaestro.inf</c> beside an older <c>hidmaestro_xusb.inf</c>.</summary>
    internal static bool StoreBelowFloor() =>
        DriverBuilder.PackageInfs.Any(inf => Machine.Stamps(inf) is var s && (s.Count == 0 || s.Max().Release < MinimumDriverVersion));

    /// <summary>The stamps of every package of one of the two INFs in the
    /// driver store, read from the INF in each package's FileRepository
    /// directory.</summary>
    internal static List<DriverStamp> InstalledStamps(string infName)
    {
        string repo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
        var stamps = new List<DriverStamp>();
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(repo, infName + "_*"))
            {
                string inf = Path.Combine(dir, infName);
                if (!File.Exists(inf)) continue;
                if (ParseDriverVer(File.ReadAllText(inf)) is { } s) stamps.Add(s);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return stamps;
    }

    /// <summary>This build's INFs that have no package of this build's stamp
    /// in the driver store. Each INF is compared with its own embedded
    /// stamp: a build made before v1.13.0 stamped its two INFs at two
    /// moments, and when those fell in two minutes the XUSB package never
    /// carried the driver INF's stamp, so it read as missing after every
    /// add.</summary>
    internal static List<string> MissingOfBuild() =>
        DriverBuilder.PackageInfs.Where(inf => !Machine.Stamps(inf).Any(s => s.CompareTo(Machine.Embedded(inf)) == 0)).ToList();

    /// <summary>This SDK's own driver stamp, from its embedded
    /// <c>hidmaestro.inf</c>.</summary>
    internal static DriverStamp EmbeddedDriver() => Machine.Embedded("hidmaestro.inf");

    /// <summary>The stamp of one of this SDK's embedded INFs.</summary>
    internal static DriverStamp EmbeddedStamp(string infName)
    {
        using var stream = typeof(DriverDeploy).Assembly.GetManifestResourceStream(DriverBuilder.NativePrefix + infName)
            ?? throw new InvalidOperationException($"The embedded {infName} is missing from HIDMaestro.Core.dll.");
        using var reader = new StreamReader(stream);
        return ParseDriverVer(reader.ReadToEnd())
            ?? throw new InvalidOperationException($"The embedded {infName} has no DriverVer line.");
    }

    /// <summary>Acquire the deploy mutex. An abandoned mutex is acquired,
    /// and the caller re-reads the installed state before acting.</summary>
    /// <exception cref="UnauthorizedAccessException">The process is not
    /// elevated.</exception>
    internal static Mutex Acquire()
    {
        var m = new Mutex(false, AdminNamespace.Name(MutexObject));
        try
        {
            if (!m.WaitOne(TimeoutScale.Apply(600_000)))
            {
                m.Dispose();
                throw new InvalidOperationException(
                    "Another program's HIDMaestro driver deploy held the machine-wide deploy lock for ten minutes.");
            }
        }
        catch (AbandonedMutexException) { }
        return m;
    }

    /// <summary>Apply the policy. Returns what was done.
    /// <paramref name="creatingToken"/> is the identity of the create whose
    /// deploy gate calls this (<see cref="EnsureForCreate"/>). That create's
    /// own record entry is written before the gate, and it needs the driver
    /// this call installs, so it does not hold the installed one.
    /// <paramref name="allowHeldOlder"/> keeps a held driver older than
    /// <see cref="MinimumDriverVersion"/> rather than refusing it, for a
    /// create alternative B lets run there.</summary>
    internal static HMDriverInstallOutcome Run(string? creatingToken = null, bool allowHeldOlder = false)
    {
        if (Machine.FastPath())
        {
            Machine.MarkConfirmed();
            return HMDriverInstallOutcome.AlreadyCurrent;
        }

        using var deployLock = Machine.Lock();
        try
        {
            if (Machine.FastPath())
            {
                Machine.MarkConfirmed();
                return HMDriverInstallOutcome.AlreadyCurrent;
            }

            DriverStamp embedded = EmbeddedDriver();
            DriverStamp? installed = InstalledDriver();
            DeviceOrchestrator.LogDiag($"    deploy: embedded {embedded} ({embedded.Date:yyyy-MM-dd}), installed {(installed?.ToString() ?? "none")}");

            // Whatever is present now belongs to a running program or to one
            // whose state cannot be known: the reclaim before this removed
            // what dead programs left. A running program's controller whose
            // devices are not present yet holds the driver too: its record
            // entry comes before its PnP work, and its create waits for this
            // lock before that work (EnsureForCreate). The create calling
            // this through its gate is the exception: it waits for this
            // call's result, so it holds nothing yet.
            var census = Machine.Census();
            var holders = census.Devices
                .Where(d => d.Owner is not (OwnerCensus.Owner.Dead or OwnerCensus.Owner.DeadLegacy))
                .ToList();
            var pending = census.Records
                .Where(r => r.Live)
                .SelectMany(r => r.Controllers.Where(c =>
                        !string.Equals(c.Backend, "usbip", StringComparison.OrdinalIgnoreCase)
                        && !(r.Self && creatingToken != null && string.Equals(c.Token, creatingToken, StringComparison.OrdinalIgnoreCase))
                        && !holders.Any(h => string.Equals(h.Key, c.Token, StringComparison.OrdinalIgnoreCase)))
                    .Select(c => (Record: r, Controller: c)))
                .ToList();
            bool held = holders.Count > 0 || pending.Count > 0;

            if (installed == null)
            {
                // No main package. With nothing on the driver, a full deploy.
                // With devices on a lone XUSB package, only what this build
                // lacks is added: a full deploy would remove that package and
                // its hosts under them.
                if (held && DriverBuilder.PackageInfs.Any(inf => Machine.Stamps(inf).Count > 0))
                    Machine.AddPackages(MissingOfBuild());
                else
                    Machine.Deploy();
                return HMDriverInstallOutcome.Installed;
            }

            int cmp = installed.Value.CompareTo(embedded);
            if (cmp > 0)
                return HMDriverInstallOutcome.KeptNewer;
            if (cmp == 0)
            {
                // The same build. A deploy cut off between its two adds, or
                // a package of it removed since, leaves the set incomplete,
                // and the fast path needs both. Adding what is missing touches
                // nothing installed. Otherwise only the record of the install
                // is missing, as after SOFTWARE\HIDMaestro was deleted: it is
                // restored rather than reinstalled under running devices.
                var missing = MissingOfBuild();
                if (missing.Count > 0)
                {
                    Machine.AddPackages(missing);
                    Machine.RecordPayload();
                    return HMDriverInstallOutcome.Installed;
                }
                Machine.RecordPayload();
                return HMDriverInstallOutcome.AlreadyCurrent;
            }

            // Older.
            if (!held)
            {
                Machine.Deploy();
                return HMDriverInstallOutcome.Upgraded;
            }
            if (installed.Value.Release >= MinimumDriverVersion || allowHeldOlder)
                return HMDriverInstallOutcome.KeptInUse;
            var blockers = Describe(holders);
            foreach (var (r, c) in pending)
                blockers.Add($"identity {c.Token} on transport index {c.TransportIndex}, being created ({(r.Self ? "this program" : MachineClaims.Describe(r))})");
            throw new HMDriverVersionException(installed.Value.Release, MinimumDriverVersion, blockers);
        }
        finally
        {
            // Under the mutex, drop staging directories of versions no live
            // owner names. Best effort: a directory still in use by another
            // program's version is kept because its owner record names it.
            try { Machine.PruneStaging(); } catch { }
        }
    }

    // The installed-payload record this process last saw with both
    // packages in the store. Every deploy and package removal rewrites or
    // deletes it, so a create finds it unchanged only while the store is as
    // it was.
    private static string? s_confirmedManifest;

    /// <summary>The deploy gate of a create, run after the controller's
    /// record entry and before its PnP work. Under the deploy lock: when the
    /// installed-payload record changed since this process last saw the
    /// packages at <see cref="MinimumDriverVersion"/> or later, or is
    /// missing, the driver store is read again, and the policy above runs
    /// when it lacks a package of either INF or the newest package of
    /// either is older than the floor. A deploy of another program
    /// therefore either finished before this create's PnP work or saw its
    /// entry, and a create meets the same rule on an older driver that
    /// InstallDriver does. The record is confirmed only when both packages
    /// meet the floor, so a driver older than 2.0.0, whose program writes
    /// the same record, never takes the fast return.
    /// <paramref name="identityToken"/> is the create's own identity, whose
    /// entry the policy does not count as a holder.
    /// <paramref name="runsOnOlderDriver"/> is the profile's
    /// <see cref="ControllerProfile.RunsOnOlderDriver"/>, which
    /// alternative B reads. Returns true when this call deployed.</summary>
    internal static bool EnsureForCreate(string identityToken, bool runsOnOlderDriver)
    {
        using var deployLock = Machine.Lock();
        string? recorded = Machine.ManifestHash();
        if (recorded != null && string.Equals(recorded, Volatile.Read(ref s_confirmedManifest), StringComparison.OrdinalIgnoreCase))
            return false;
        bool deployed = false;
        if (!Machine.StoreHasBothPackages() || StoreBelowFloor())
        {
            var outcome = Run(identityToken,
                allowHeldOlder: Coexistence == CoexistenceRule.AllowModelFreeProfiles && runsOnOlderDriver);
            deployed = outcome is HMDriverInstallOutcome.Installed or HMDriverInstallOutcome.Upgraded;
            if (!Machine.StoreHasBothPackages())
                throw new InvalidOperationException(
                    $"The HIDMaestro driver is not in the driver store, and a deploy for this create ended with {outcome}.");
        }
        if (!StoreBelowFloor())
            Volatile.Write(ref s_confirmedManifest, Machine.ManifestHash());
        return deployed;
    }

    /// <summary>Forgets the confirmed record, as a new process starts. For
    /// a check that runs the gate on machines of its own.</summary>
    internal static void ForgetConfirmed() => Volatile.Write(ref s_confirmedManifest, null);

    /// <summary>True when the driver packages may go: no HIDMaestro device
    /// is present, no other program that embeds HIDMaestro runs, and this
    /// one has no controller. Otherwise <paramref name="holders"/> says what
    /// keeps them. The caller holds the deploy lock.</summary>
    internal static bool PackagesUnused(out string holders)
    {
        var census = OwnerCensus.Take();
        int others = census.Records.Count(r => r.Live && !r.Self);
        bool ownHeld = census.Records.Any(r => r.Self && r.Controllers.Count > 0);
        holders = $"{census.Devices.Count} device group(s), {others} other running program(s)"
                + (ownHeld ? ", and this program's own controllers" : "");
        return census.Devices.Count == 0 && others == 0 && !ownHeld;
    }

    private static List<string> Describe(List<OwnerCensus.DeviceGroup> holders)
    {
        var list = new List<string>();
        foreach (var g in holders)
        {
            string node = g.InstanceIds.OrderBy(id => id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault() ?? g.Key;
            string who = g.Owner switch
            {
                OwnerCensus.Owner.Self => "this program",
                OwnerCensus.Owner.Live when g.Record != null => MachineClaims.Describe(g.Record),
                // Claimed now by a program on this release that no record
                // names yet: a create in flight, or a sweep removing it.
                OwnerCensus.Owner.Live => "a running program",
                OwnerCensus.Owner.LiveLegacy => "a running program built on HIDMaestro 1.12.0",
                _ => "a program built on HIDMaestro older than 1.12.0, which may still be running",
            };
            list.Add($"{node} ({who})");
        }
        return list;
    }
}
