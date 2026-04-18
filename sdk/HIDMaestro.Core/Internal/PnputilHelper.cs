using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace HIDMaestro.Internal;

/// <summary>
/// Strict, structured wrapper around <c>pnputil /enum-drivers</c> and
/// <c>pnputil /delete-driver</c>. Replaces a previous substring-based grep
/// of pnputil output that was vulnerable to two failure modes:
///
/// <list type="bullet">
/// <item>Loose substring matching — any line containing "hidmaestro" was
/// treated as part of one of our packages. A stale or partial entry from a
/// half-failed cleanup would cause <c>IsHidMaestroDriverInstalled</c> to
/// return true even when the real package was gone, which made
/// <c>FullDeploy</c> skip its install and the next test run silently use
/// the OLD binary.</item>
/// <item>Silent delete failures — <c>/delete-driver</c> can fail with
/// "in use" if any device is still bound. The previous code didn't check
/// the exit code, so a stale entry would persist forever.</item>
/// </list>
///
/// This helper parses the output into <see cref="DriverRecord"/> values and
/// applies the strict filter <c>Provider Name == "HIDMaestro"</c> with an
/// explicit allow-list of INF original names. It also retries deletes when
/// pnputil reports the driver is in use, and verifies removal afterward.
/// </summary>
internal static class PnputilHelper
{
    /// <summary>The INF original names that belong to HIDMaestro. Used as
    /// the strict allow-list when filtering enumerated driver records.</summary>
    /// <summary>INFs we EXPECT to be installed. IsHidMaestroDriverInstalled
    /// requires all of these to be present.</summary>
    public static readonly string[] HidMaestroInfNames = new[]
    {
        "hidmaestro.inf",
        "hidmaestro_xusb.inf",
        "hidmaestro_xusbshim_class.inf",
    };

    /// <summary>INFs that RemoveAllHidMaestroPackages should clean up — includes
    /// legacy/experimental names so stale packages from prior iterations
    /// don't linger in the driver store and block reinstall.</summary>
    public static readonly string[] HidMaestroInfNamesForCleanup = new[]
    {
        "hidmaestro.inf",
        "hidmaestro_xusb.inf",
        "hidmaestro_xusbshim_class.inf",
        "hidmaestro_xusbshim.inf",  // legacy Extension variant
        "hidmaestro_btnfix.inf",    // legacy btnfix experiment
    };

    /// <summary>One enumerated driver package record (a single block of
    /// <c>pnputil /enum-drivers</c> output between blank lines).</summary>
    public sealed record DriverRecord(
        string PublishedName,
        string OriginalName,
        string ProviderName,
        string DriverVersion);

    private static string PnputilPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");

    private static (int exitCode, string output) Run(string args, int timeoutMs = 30_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PnputilPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(timeoutMs);
        return (p.ExitCode, stdout + stderr);
    }

    /// <summary>Parses the full output of <c>pnputil /enum-drivers</c> into
    /// records. Records are delimited by blank lines. Lines whose colon-prefix
    /// matches a known field are extracted; everything else is ignored. The
    /// parser is forgiving of pnputil locale variations on whitespace.</summary>
    public static List<DriverRecord> EnumerateDrivers()
    {
        var (_, output) = Run("/enum-drivers");
        var records = new List<DriverRecord>();

        string published = "", original = "", provider = "", version = "";
        void Flush()
        {
            if (!string.IsNullOrEmpty(published))
                records.Add(new DriverRecord(published, original, provider, version));
            published = original = provider = version = "";
        }

        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string key = line.Substring(0, colon).Trim();
            string val = line.Substring(colon + 1).Trim();

            if (key.Equals("Published Name", StringComparison.OrdinalIgnoreCase)) published = val;
            else if (key.Equals("Original Name", StringComparison.OrdinalIgnoreCase)) original = val;
            else if (key.Equals("Provider Name", StringComparison.OrdinalIgnoreCase)) provider = val;
            else if (key.Equals("Driver Version", StringComparison.OrdinalIgnoreCase)) version = val;
        }
        Flush();
        return records;
    }

    /// <summary>Returns only the enumerated records that belong to HIDMaestro,
    /// using a strict <c>Provider Name == "HIDMaestro"</c> filter combined
    /// with an allow-list of original INF names. This is the function callers
    /// should use rather than substring-matching pnputil output.</summary>
    public static List<DriverRecord> FindHidMaestroPackages()
    {
        return EnumerateDrivers()
            .Where(r => r.ProviderName.Equals("HIDMaestro", StringComparison.OrdinalIgnoreCase)
                     && HidMaestroInfNames.Any(n => string.Equals(r.OriginalName, n,
                                                                  StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>True iff a HIDMaestro package matching every entry in
    /// <see cref="HidMaestroInfNames"/> is currently in the driver store.
    /// Replaces the previous substring grep on enum output.</summary>
    public static bool IsHidMaestroDriverInstalled()
    {
        var pkgs = FindHidMaestroPackages();
        return HidMaestroInfNames.All(name =>
            pkgs.Any(p => string.Equals(p.OriginalName, name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Removes one driver package by published name with retry on
    /// "in use" failures. Returns true on success. <paramref name="error"/>
    /// is set to pnputil's combined stdout+stderr on failure.</summary>
    public static bool DeletePackage(string publishedName, out string error, int retries = 3)
    {
        error = "";
        for (int attempt = 0; attempt < retries; attempt++)
        {
            var (rc, output) = Run($"/delete-driver {publishedName} /force", timeoutMs: 10_000);
            if (rc == 0 && !output.Contains("Failed", StringComparison.OrdinalIgnoreCase))
                return true;

            error = output.Trim();
            // Driver-in-use is racy with device removal — give the PnP stack a
            // moment to release before retrying. Doesn't matter if the wait is
            // wasted; this only runs during cleanup.
            if (output.Contains("in use", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("currently being used", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(500);
                continue;
            }
            // Some other failure (corrupt INF, missing perm, …) — don't retry.
            return false;
        }
        return false;
    }

    /// <summary>Removes every HIDMaestro package from the driver store and
    /// verifies they're actually gone. Throws <see cref="InvalidOperationException"/>
    /// if any package can't be removed. This is the strict version that
    /// prevents the "stale entry blocks reinstall" failure mode.</summary>
    public static void RemoveAllHidMaestroPackages()
    {
        // Use the broader cleanup allow-list so legacy/experimental INF names
        // (hidmaestro_xusbshim.inf, hidmaestro_btnfix.inf) also get removed.
        var cleanup = EnumerateDrivers()
            .Where(r => r.ProviderName.Equals("HIDMaestro", StringComparison.OrdinalIgnoreCase)
                     && HidMaestroInfNamesForCleanup.Any(n => string.Equals(r.OriginalName, n,
                                                                             StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var failures = new List<(string Published, string Error)>();
        foreach (var pkg in cleanup)
        {
            if (!DeletePackage(pkg.PublishedName, out string err))
                failures.Add((pkg.PublishedName, err));
        }

        // Re-enumerate to verify nothing remains. If something does, throw
        // with detail rather than letting the caller silently proceed and
        // load a stale binary on the next install.
        var remaining = FindHidMaestroPackages();
        if (remaining.Count > 0)
        {
            string detail = string.Join("; ", remaining.Select(r =>
                $"{r.PublishedName} ({r.OriginalName})"));
            string failDetail = failures.Count == 0 ? "" :
                " Delete failures: " + string.Join("; ", failures.Select(f => $"{f.Published}: {f.Error}"));
            throw new InvalidOperationException(
                $"Failed to remove all HIDMaestro driver packages. Still in store: {detail}.{failDetail}");
        }
    }
}
