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
/// it when it is protocol 2 (v1.12.0) or later. Otherwise
/// <see cref="HMDriverVersionException"/> names what holds it.</item>
/// </list>
///
/// <para>Every deploy, of this driver or of the USB/IP transport, holds the
/// machine-wide mutex <see cref="MutexName"/> for the length of one call.
/// The holder re-reads the installed state after acquiring it, so of two
/// programs calling InstallDriver at once, one deploys and the other finds
/// the result current.</para>
/// </summary>
internal static partial class DriverDeploy
{
    internal const string MutexName = @"Global\HIDMaestro.Deploy";

    /// <summary>The oldest driver this SDK runs on: protocol 2, the v1.12.0
    /// layouts, including the InputAck section keyboards and mice need.</summary>
    internal static readonly Version MinimumDriverVersion = new(1, 12, 0);

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
        string repo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
        DriverStamp? best = null;
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(repo, "hidmaestro.inf_*"))
            {
                string inf = Path.Combine(dir, "hidmaestro.inf");
                if (!File.Exists(inf)) continue;
                var stamp = ParseDriverVer(File.ReadAllText(inf));
                if (stamp is { } s && (best == null || s.CompareTo(best.Value) > 0)) best = s;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return best;
    }

    /// <summary>This SDK's own driver stamp, from its embedded INF.</summary>
    internal static DriverStamp EmbeddedDriver()
    {
        using var stream = typeof(DriverDeploy).Assembly.GetManifestResourceStream(DriverBuilder.NativePrefix + "hidmaestro.inf")
            ?? throw new InvalidOperationException("The embedded hidmaestro.inf is missing from HIDMaestro.Core.dll.");
        using var reader = new StreamReader(stream);
        return ParseDriverVer(reader.ReadToEnd())
            ?? throw new InvalidOperationException("The embedded hidmaestro.inf has no DriverVer line.");
    }

    /// <summary>Acquire the deploy mutex. An abandoned mutex is acquired,
    /// and the caller re-reads the installed state before acting.</summary>
    internal static Mutex Acquire()
    {
        var m = new Mutex(false, MutexName);
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

    /// <summary>Apply the policy. Returns what was done.</summary>
    internal static HMDriverInstallOutcome Run()
    {
        if (DriverBuilder.WillTakeFastPath())
        {
            PnputilHelper.MarkInstalledConfirmed();
            return HMDriverInstallOutcome.AlreadyCurrent;
        }

        using var mutex = Acquire();
        try
        {
            if (DriverBuilder.WillTakeFastPath())
            {
                PnputilHelper.MarkInstalledConfirmed();
                return HMDriverInstallOutcome.AlreadyCurrent;
            }

            DriverStamp embedded = EmbeddedDriver();
            DriverStamp? installed = InstalledDriver();
            DeviceOrchestrator.LogDiag($"    deploy: embedded {embedded} ({embedded.Date:yyyy-MM-dd}), installed {(installed?.ToString() ?? "none")}");

            if (installed == null)
            {
                DriverBuilder.DeployLocked();
                return HMDriverInstallOutcome.Installed;
            }

            int cmp = installed.Value.CompareTo(embedded);
            if (cmp > 0)
                return HMDriverInstallOutcome.KeptNewer;
            if (cmp == 0)
            {
                // The same build is installed and only the record of it is
                // missing, as after SOFTWARE\HIDMaestro was deleted. Restore
                // the record rather than reinstall under running devices.
                DriverBuilder.RecordInstalledPayload();
                return HMDriverInstallOutcome.AlreadyCurrent;
            }

            // Older. The reclaim before this removed what dead programs
            // left, so whatever is present now belongs to a running program
            // or to one whose state cannot be known. A running program's
            // controller whose devices are not present yet holds the driver
            // too: its record entry comes before its PnP work, and its
            // create waits for this lock before that work (EnsureForCreate).
            var census = OwnerCensus.Take();
            var holders = census.Devices
                .Where(d => d.Owner is not (OwnerCensus.Owner.Dead or OwnerCensus.Owner.DeadLegacy))
                .ToList();
            var pending = census.Records
                .Where(r => r.Live)
                .SelectMany(r => r.Controllers.Where(c =>
                        !string.Equals(c.Backend, "usbip", StringComparison.OrdinalIgnoreCase)
                        && !holders.Any(h => string.Equals(h.Key, c.Token, StringComparison.OrdinalIgnoreCase)))
                    .Select(c => (Record: r, Controller: c)))
                .ToList();
            if (holders.Count == 0 && pending.Count == 0)
            {
                DriverBuilder.DeployLocked();
                return HMDriverInstallOutcome.Upgraded;
            }
            if (installed.Value.Release >= MinimumDriverVersion)
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
            try { ProtectedStaging.PruneOldVersions(); } catch { }
            mutex.ReleaseMutex();
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
    /// packages, or is missing, the driver store is read again, and a
    /// missing driver is deployed by the policy above. A deploy of another
    /// program therefore either finished before this create's PnP work or
    /// saw its entry. Returns true when this call deployed.</summary>
    internal static bool EnsureForCreate()
    {
        using var mutex = Acquire();
        try
        {
            string? recorded = DriverBuilder.InstalledManifestHash();
            if (recorded != null && string.Equals(recorded, Volatile.Read(ref s_confirmedManifest), StringComparison.OrdinalIgnoreCase))
                return false;
            bool deployed = false;
            if (!DriverBuilder.DriverStoreHasBothPackages())
            {
                var outcome = Run();
                deployed = outcome is HMDriverInstallOutcome.Installed or HMDriverInstallOutcome.Upgraded;
                if (!DriverBuilder.DriverStoreHasBothPackages())
                    throw new InvalidOperationException(
                        $"The HIDMaestro driver is not in the driver store, and a deploy for this create ended with {outcome}.");
            }
            Volatile.Write(ref s_confirmedManifest, DriverBuilder.InstalledManifestHash());
            return deployed;
        }
        finally { mutex.ReleaseMutex(); }
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
                OwnerCensus.Owner.LiveLegacy => "a running program built on HIDMaestro 1.12.0",
                _ => "a program built on HIDMaestro older than 1.12.0, which may still be running",
            };
            list.Add($"{node} ({who})");
        }
        return list;
    }
}
