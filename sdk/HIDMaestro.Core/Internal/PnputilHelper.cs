using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;

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
    /// the strict allow-list when filtering enumerated driver records.
    /// INFs we EXPECT to be installed. IsHidMaestroDriverInstalled
    /// requires all of these to be present.
    /// Must match the set actually installed by DriverBuilder.InstallDrivers()
    /// — keep in sync or FullDeploy will refire per-controller and fail when
    /// the first controller holds the package.</summary>
    public static readonly string[] HidMaestroInfNames = new[]
    {
        "hidmaestro.inf",
        "hidmaestro_xusb.inf",
    };

    /// <summary>INFs that RemoveAllHidMaestroPackages should clean up.</summary>
    public static readonly string[] HidMaestroInfNamesForCleanup = new[]
    {
        "hidmaestro.inf",
        "hidmaestro_xusb.inf",
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
        // Drain streams concurrently: ReadToEnd blocks until EOF, which only
        // happens at process exit — so a synchronous read here would hang past
        // timeoutMs when pnputil itself hangs. Async read + WaitForExit gives
        // us a real timeout we can act on.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeoutScale.Apply(timeoutMs)))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            p.WaitForExit(TimeoutScale.Apply(5_000));
        }
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        int exitCode;
        try { exitCode = p.ExitCode; } catch { exitCode = -1; }
        return (exitCode, stdout + stderr);
    }

    /// <summary>Parses <c>pnputil /enum-drivers /format xml</c> into records.
    /// XML element names are pnputil's internal identifiers, not localized
    /// resource strings, so this is locale-stable across Windows installs.
    /// (The previous plain-text parser matched English-literal field labels
    /// like "Published Name" and silently returned empty on non-English
    /// Windows installs, which broke <see cref="IsHidMaestroDriverInstalled"/>
    /// — see issue #17.)</summary>
    public static List<DriverRecord> EnumerateDrivers()
    {
        var (_, output) = Run("/enum-drivers /format xml");
        var records = new List<DriverRecord>();

        if (string.IsNullOrWhiteSpace(output)) return records;

        XDocument doc;
        try { doc = XDocument.Parse(output); }
        catch (System.Xml.XmlException) { return records; }

        var root = doc.Root;
        if (root == null) return records;

        foreach (var driver in root.Elements("Driver"))
        {
            string published = driver.Attribute("DriverName")?.Value ?? "";
            string original  = driver.Element("OriginalName")?.Value ?? "";
            string provider  = driver.Element("ProviderName")?.Value ?? "";
            string version   = driver.Element("DriverVersion")?.Value ?? "";
            if (!string.IsNullOrEmpty(published))
                records.Add(new DriverRecord(published, original, provider, version));
        }
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

    // v1.3.0 — per-process positive cache. Once we've confirmed the driver
    // is installed in this process (or successfully installed it), every
    // subsequent IsDriverInstalled / IsHidMaestroDriverInstalled call within
    // this process can skip the pnputil /enum-drivers /format xml call
    // (which, on machines with hundreds of drivers, takes 200–500 ms even
    // on a fast box). The negative case is NOT cached — if the driver isn't
    // installed yet, we want fresh state on every check until it is.
    private static volatile bool s_installedConfirmed;
    internal static void InvalidateInstalledCache() => s_installedConfirmed = false;
    internal static void MarkInstalledConfirmed() => s_installedConfirmed = true;

    /// <summary>True iff a HIDMaestro package matching every entry in
    /// <see cref="HidMaestroInfNames"/> is currently in the driver store.
    /// Replaces the previous substring grep on enum output.</summary>
    public static bool IsHidMaestroDriverInstalled()
    {
        if (s_installedConfirmed) return true;

        // T37 — cheap filesystem-level fast path. The DriverStore\FileRepository
        // directory layout is stable across every supported Windows version;
        // checking for hidmaestro.inf_* and hidmaestro_xusb.inf_* dirs is the
        // same signal pnputil /enum-drivers would surface, but in ~5 ms vs
        // 200–500 ms for the pnputil enum + XML parse. d3xMachina's gh#18
        // log showed "driver install check (deployed=True) in 1695ms" hitting
        // the slow path on first call; this fast path eliminates that.
        // Falls through to the pnputil enum on filesystem-check failure
        // (e.g., DriverStore inaccessible) so the strict provider-name
        // filter still validates correctness in edge cases.
        try
        {
            string fileRepo = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "DriverStore", "FileRepository");
            if (System.IO.Directory.Exists(fileRepo))
            {
                bool hasMain = false, hasXusb = false;
                foreach (var dir in System.IO.Directory.EnumerateDirectories(fileRepo))
                {
                    string name = System.IO.Path.GetFileName(dir);
                    if (!hasMain && name.StartsWith("hidmaestro.inf_", StringComparison.OrdinalIgnoreCase))
                        hasMain = true;
                    else if (!hasXusb && name.StartsWith("hidmaestro_xusb.inf_", StringComparison.OrdinalIgnoreCase))
                        hasXusb = true;
                    if (hasMain && hasXusb)
                    {
                        s_installedConfirmed = true;
                        return true;
                    }
                }
            }
        }
        catch { /* fall through to pnputil enum */ }

        var pkgs = FindHidMaestroPackages();
        bool ok = HidMaestroInfNames.All(name =>
            pkgs.Any(p => string.Equals(p.OriginalName, name, StringComparison.OrdinalIgnoreCase)));
        if (ok) s_installedConfirmed = true;
        return ok;
    }

    /// <summary>Removes one driver package by published name with retry on
    /// "in use" failures. Returns true on success. <paramref name="error"/>
    /// is set to pnputil's combined stdout+stderr on failure.
    ///
    /// Uses <c>/uninstall /force</c>: <c>/uninstall</c> tells pnputil to
    /// uninstall the driver from any devices still bound (including stale
    /// PnP bindings that don't show up in <c>/enum-devices</c> — the "device
    /// is presently installed using the specified INF" failure mode), and
    /// <c>/force</c> covers the remaining cases where a device is actively
    /// started. Plain <c>/force</c> alone was not sufficient on stale
    /// bindings left behind by half-completed teardowns.</summary>
    public static bool DeletePackage(string publishedName, out string error, int retries = 3)
    {
        error = "";
        for (int attempt = 0; attempt < retries; attempt++)
        {
            var (rc, output) = Run($"/delete-driver {publishedName} /uninstall /force", timeoutMs: 30_000);
            if (rc == 0 && !output.Contains("Failed", StringComparison.OrdinalIgnoreCase))
                return true;

            error = output.Trim();
            // Driver-in-use is racy with device removal — give the PnP stack a
            // moment to release before retrying. Doesn't matter if the wait is
            // wasted; this only runs during cleanup.
            if (output.Contains("in use", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("currently being used", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("presently installed", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(TimeoutScale.Apply(500));
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
        // Removal busts the per-process positive cache; the next
        // IsHidMaestroDriverInstalled call will re-enumerate.
        InvalidateInstalledCache();

        var cleanup = EnumerateDrivers()
            .Where(r => r.ProviderName.Equals("HIDMaestro", StringComparison.OrdinalIgnoreCase)
                     && HidMaestroInfNamesForCleanup.Any(n => string.Equals(r.OriginalName, n,
                                                                             StringComparison.OrdinalIgnoreCase)))
            .ToList();
        // Perf audit 2026-07-21: nothing enumerated means nothing to
        // delete and nothing to verify. The unconditional re-enumeration
        // below cost 200-500 ms per empty cleanup.
        if (cleanup.Count == 0) return;

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
