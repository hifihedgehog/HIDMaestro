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
/// <item>An older driver, either INF, that controllers of running programs
/// (or of programs whose state cannot be known) run on: this SDK's packages
/// are added to the driver store beside it, with no device updated and no
/// package removed. Every device this SDK creates then binds its driver,
/// the newest in the store, and theirs keep the driver they loaded until
/// they restart. Nothing is refused for a holder: the design note's
/// decision (c), "the V2 controllers take precedence over and supersede
/// the V1". A create meets the same rule at its deploy gate
/// (<see cref="EnsureForCreate"/>), before any PnP work.</item>
/// </list>
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
    /// controller's v2.0.0 values (decision (c)). Every create installs its
    /// own driver over an older one, so a started device that does not
    /// serve protocol 4 is a newer driver that dropped it, or a mis-stamped
    /// one.</summary>
    internal static readonly Version MinimumDriverVersion = new(2, 0, 0);

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

        /// <summary>The deploy lock held: releasing it releases the mutex
        /// and closes the handle Acquire opened for this hold.</summary>
        private sealed class Held(Mutex mutex) : IDisposable
        {
            public void Dispose()
            {
                try { mutex.ReleaseMutex(); }
                finally { mutex.Dispose(); }
            }
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

    /// <summary>This build's INFs whose newest package in the driver store
    /// is older than this build's, or which have none: a 1.x release, an
    /// older 2.x build, or a half-failed install that left one INF behind
    /// the other.</summary>
    internal static List<string> OlderThanBuild() =>
        DriverBuilder.PackageInfs.Where(inf => Machine.Stamps(inf) is var s
            && (s.Count == 0 || s.Max().CompareTo(Machine.Embedded(inf)) < 0)).ToList();

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
    /// stamp: a build made before v2.0.0 stamped its two INFs at two
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
    /// this call installs, so it does not hold the installed one.</summary>
    internal static HMDriverInstallOutcome Run(string? creatingToken = null)
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

            // An INF whose newest package is older than this build's, or
            // which has none. This build's package of each goes in.
            var older = OlderThanBuild();

            if (installed == null || installed.Value.CompareTo(embedded) < 0)
            {
                // No main package, or an older one. With nothing on the
                // driver, a full deploy, which removes the old packages. With
                // devices on it, this build's packages are added beside the
                // old ones (AddPackagesLocked: no /install, so no device is
                // updated and nothing asks for a restart, and no package is
                // removed). The store's newest package ranks first for every
                // device this SDK creates (Windows selects the most recent
                // DriverVer date among equally ranked drivers), and the
                // devices on the old package keep it until they restart.
                bool present = DriverBuilder.PackageInfs.Any(inf => Machine.Stamps(inf).Count > 0);
                if (!held || !present)
                {
                    Machine.Deploy();
                    return installed == null ? HMDriverInstallOutcome.Installed : HMDriverInstallOutcome.Upgraded;
                }
                Machine.AddPackages(older);
                Machine.RecordPayload();
                return installed == null ? HMDriverInstallOutcome.Installed : HMDriverInstallOutcome.Upgraded;
            }

            if (installed.Value.CompareTo(embedded) > 0)
            {
                // The main package is newer and stays: a newer driver is
                // never replaced. This build's package of an XUSB INF older
                // than it, or absent, is added beside it, held or not.
                if (older.Count == 0) return HMDriverInstallOutcome.KeptNewer;
                Machine.AddPackages(older);
                return HMDriverInstallOutcome.Installed;
            }

            // The same build. A deploy cut off between its two adds, a
            // package of it removed since, or an older XUSB package beside
            // it leaves the set incomplete, and the fast path needs both.
            // Adding what is missing touches nothing installed. Otherwise
            // only the record of the install is missing, as after
            // SOFTWARE\HIDMaestro was deleted: it is restored rather than
            // reinstalled under running devices.
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
    /// installed-payload record changed since this process last saw this
    /// build's packages in the store, or is missing, the driver store is
    /// read again, and the policy above runs when the newest package of
    /// either INF is older than this build's or one has none. A deploy of
    /// another program therefore either finished before this create's PnP
    /// work or saw its entry, and a create installs its own driver over an
    /// older one as InstallDriver does, whoever runs on the older one. The
    /// record is confirmed only when neither INF is older than this build,
    /// so an older driver, whose program writes the same record, never
    /// takes the fast return.
    /// <paramref name="identityToken"/> is the create's own identity, whose
    /// entry the policy does not count as a holder. Returns true when this
    /// call deployed.</summary>
    internal static bool EnsureForCreate(string identityToken)
    {
        using var deployLock = Machine.Lock();
        string? recorded = Machine.ManifestHash();
        if (recorded != null && string.Equals(recorded, Volatile.Read(ref s_confirmedManifest), StringComparison.OrdinalIgnoreCase))
            return false;
        bool deployed = false;
        if (!Machine.StoreHasBothPackages() || OlderThanBuild().Count > 0)
        {
            var outcome = Run(identityToken);
            deployed = outcome is HMDriverInstallOutcome.Installed or HMDriverInstallOutcome.Upgraded;
            if (!Machine.StoreHasBothPackages())
                throw new InvalidOperationException(
                    $"The HIDMaestro driver is not in the driver store, and a deploy for this create ended with {outcome}.");
        }
        if (OlderThanBuild().Count == 0)
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
}
