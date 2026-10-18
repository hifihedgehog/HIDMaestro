using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace HIDMaestro.Internal;

/// <summary>
/// The driver store's published packages and their removal: the listing
/// reads SetupAPI (<see cref="ListPackages"/>) and the removal runs
/// <c>pnputil /delete-driver</c>. Replaces a previous substring-based grep
/// of pnputil output that was vulnerable to two failure modes:
///
/// <list type="bullet">
/// <item>Loose substring matching: any line containing "hidmaestro" was
/// treated as part of one of our packages. A stale or partial entry from a
/// half-failed cleanup would cause <c>IsHidMaestroDriverInstalled</c> to
/// return true even when the real package was gone, which made
/// <c>FullDeploy</c> skip its install and the next test run silently use
/// the OLD binary.</item>
/// <item>Silent delete failures: <c>/delete-driver</c> can fail with
/// "in use" if any device is still bound. The previous code didn't check
/// the exit code, so a stale entry would persist forever.</item>
/// </list>
///
/// This helper lists packages as <see cref="DriverRecord"/> values and
/// applies the strict filter <c>Provider Name == "HIDMaestro"</c> with an
/// explicit allow-list of INF original names. It also retries deletes when
/// pnputil reports the driver is in use, and verifies removal afterward.
/// </summary>
internal static partial class PnputilHelper
{
    /// <summary>The INF original names that belong to HIDMaestro. Used as
    /// the strict allow-list when filtering enumerated driver records.
    /// INFs we EXPECT to be installed. IsHidMaestroDriverInstalled
    /// requires all of these to be present.
    /// Must match the set actually installed by DriverBuilder.InstallDrivers()
    ///: keep in sync or FullDeploy will refire per-controller and fail when
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

    /// <summary>One published driver package: its published INF name
    /// (oem<i>N</i>.inf), its original INF name, its provider, and its
    /// DriverVer date and version as <c>pnputil /enum-drivers</c> prints
    /// them.</summary>
    public sealed record DriverRecord(
        string PublishedName,
        string OriginalName,
        string ProviderName,
        string DriverVersion);

    private static string PnputilPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");

    /// <summary>Run pnputil from System32, never a pnputil.exe found first
    /// in the application's or the current directory, and return its exit
    /// code and its output.</summary>
    internal static (int exitCode, string output) Run(string args, int timeoutMs = 30_000) =>
        RunTool(PnputilPath, args, timeoutMs);

    /// <summary>Run a program by its full path and return its exit code and
    /// its output, within the timeout however the program behaves. Its
    /// output is read as it comes, and a program that outlives the timeout
    /// is ended. Coexistence probe section Z calls this through reflection
    /// with a program that hangs.</summary>
    /// <summary>The text a child's two redirected streams gave once the
    /// child has ended or been killed, both waited for at most 5 s (scaled)
    /// together, and "" for a stream not ended by then. A stream ends when
    /// every process holding its pipe has closed it, and a process the child
    /// started can hold it long after the child ends: an unbounded read
    /// would hold the caller, and any lock it holds, as long as that process
    /// runs.</summary>
    internal static (string Out, string Err) Drain(System.Threading.Tasks.Task<string> stdout, System.Threading.Tasks.Task<string> stderr)
    {
        try { System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { stdout, stderr }, TimeoutScale.Apply(5_000)); }
        catch (AggregateException) { }
        return (Ended(stdout), Ended(stderr));
        static string Ended(System.Threading.Tasks.Task<string> t) => t.IsCompletedSuccessfully ? t.Result : "";
    }

    internal static (int exitCode, string output) RunTool(string path, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = path,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        ProtectedStaging.ConfineChildTemp(psi);

        using var p = Process.Start(psi)!;
        // Drain streams concurrently: ReadToEnd blocks until EOF, which only
        // happens at process exit: so a synchronous read here would hang past
        // timeoutMs when pnputil itself hangs. Async read + WaitForExit gives
        // us a real timeout we can act on.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeoutScale.Apply(timeoutMs)))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            p.WaitForExit(TimeoutScale.Apply(5_000));
        }
        var (stdout, stderr) = Drain(stdoutTask, stderrTask);
        int exitCode;
        try { exitCode = p.ExitCode; } catch { exitCode = -1; }
        return (exitCode, stdout + stderr);
    }

    /// <summary>The driver store's published packages whose original INF
    /// name <paramref name="originalName"/> accepts, with their providers
    /// and DriverVer lines: the packages <c>pnputil /enum-drivers</c> lists
    /// for this machine's architecture, read the same way on every Windows
    /// version. pnputil prints its locale-stable form, <c>/format xml</c>,
    /// only on Windows 11, and Windows 10's pnputil answers that option with
    /// its usage text and exit code 1. Its plain text names each field in
    /// the system's language (#17).
    ///
    /// <para>The published INFs are the <c>oem*.inf</c> files of the
    /// Windows INF directory, the set devcon's dp_enum walks
    /// (Windows-driver-samples setup/devcon cmds.cpp, cmdDPEnumLegacy).
    /// <c>SetupGetInfDriverStoreLocation</c> gives each one's package INF in
    /// the driver store, whose file name is the original name. The provider
    /// and the DriverVer fields come from the published INF through
    /// <c>SetupFindFirstLine</c> and <c>SetupGetStringField</c>, the way
    /// devcon's DumpDriverPackageData reads them (dump.cpp).</para>
    ///
    /// <para>A published INF with no package of this machine's architecture
    /// (ERROR_NOT_FOUND), such as an x86 package of ntprint.inf on an x64
    /// machine, is not listed. Neither is one removed while the listing
    /// reads it (ERROR_FILE_NOT_FOUND). pnputil also lists a published
    /// name whose INF is gone from the INF directory, with no FileRepository
    /// folder either: nothing is left to install from or to delete, and
    /// this listing leaves it out.</para></summary>
    /// <param name="infDirectory">Where the published INFs are read: the
    /// Windows INF directory when null. registry_ownership_check's offline
    /// part names a directory of its own.</param>
    /// <exception cref="InvalidOperationException">The listing failed: the
    /// INF directory could not be read, or a published INF could not be
    /// placed in the driver store or opened, for a reason other than the two
    /// above. The message names the failure. A failed listing is never an
    /// empty one.</exception>
    internal static List<DriverRecord> ListPackages(Func<string, bool> originalName, string? infDirectory = null)
    {
        string dir = infDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        string[] files;
        try { files = Directory.GetFiles(dir, "oem*"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{dir} could not be read ({e.Message.TrimEnd('.')})", e);
        }

        var records = new List<DriverRecord>();
        foreach (string inf in files)
        {
            string published = Path.GetFileName(inf);
            if (!IsPublishedName(published) || StoreLocation(inf) is not { } store) continue;
            string original = Path.GetFileName(store);
            if (!originalName(original) || VersionSection(inf) is not { } version) continue;
            records.Add(new DriverRecord(published, original, version.Provider, version.DriverVersion));
        }
        return records;
    }

    /// <summary><see cref="ListPackages"/>, or null when the listing
    /// failed. A caller that acts on what the listing leaves out tells a
    /// failed listing from an empty one.</summary>
    internal static List<DriverRecord>? TryEnumerateDrivers(Func<string, bool> originalName, string? infDirectory = null)
    {
        try { return ListPackages(originalName, infDirectory); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>A published INF's file name: <c>oem</c>, a number,
    /// <c>.inf</c>.</summary>
    private static bool IsPublishedName(string name) =>
        name.Length > 7
        && name.StartsWith("oem", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)
        && name.AsSpan(3, name.Length - 7).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>The package INF in the driver store of the published INF
    /// <paramref name="inf"/>, or null when it has no package of this
    /// machine's architecture or is gone.</summary>
    private static string? StoreLocation(string inf)
    {
        var buffer = new char[260];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (SetupGetInfDriverStoreLocation(inf, IntPtr.Zero, null, buffer, (uint)buffer.Length, out uint required))
                return Terminated(buffer);
            int error = Marshal.GetLastPInvokeError();
            if (error == ERROR_INSUFFICIENT_BUFFER && required > buffer.Length)
            {
                buffer = new char[required];
                continue;
            }
            if (error is ERROR_NOT_FOUND or ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND) return null;
            throw new InvalidOperationException(
                $"SetupGetInfDriverStoreLocation failed on {Path.GetFileName(inf)} with error {error}");
        }
        throw new InvalidOperationException(
            $"SetupGetInfDriverStoreLocation asked for a larger buffer three times on {Path.GetFileName(inf)}");
    }

    /// <summary>The Provider and the DriverVer date and version of a
    /// published INF, or null when it is gone.</summary>
    private static (string Provider, string DriverVersion)? VersionSection(string inf)
    {
        IntPtr handle = SetupOpenInfFile(inf, null, INF_STYLE_WIN4, out _);
        if (handle == InvalidInfHandle)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND) return null;
            throw new InvalidOperationException(
                $"SetupOpenInfFile failed on {Path.GetFileName(inf)} with error 0x{error:X8}");
        }
        try
        {
            return (VersionField(handle, "Provider", 1) ?? "",
                    DriverVersionText(VersionField(handle, "DriverVer", 1) ?? "", VersionField(handle, "DriverVer", 2) ?? ""));
        }
        finally { SetupCloseInfFile(handle); }
    }

    /// <summary>One field of a [Version] line, its %strkey% tokens replaced
    /// from [Strings], or null when the line or the field is absent.</summary>
    private static string? VersionField(IntPtr inf, string key, uint field)
    {
        if (!SetupFindFirstLine(inf, "Version", key, out INFCONTEXT context)) return null;
        var buffer = new char[MaxInfStringLength];
        return SetupGetStringField(ref context, field, buffer, (uint)buffer.Length, out _) ? Terminated(buffer) : null;
    }

    /// <summary>A DriverVer line's date and version as pnputil prints them:
    /// the date as MM/dd/yyyy and the version as numbers, so 1.0005.0.46 is
    /// 1.5.0.46. A part that does not parse is kept as written.</summary>
    internal static string DriverVersionText(string date, string version)
    {
        date = date.Trim();
        version = version.Trim();
        if (DateTime.TryParseExact(date, "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            date = day.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
        if (Version.TryParse(version, out var parsed)) version = parsed.ToString();
        return $"{date} {version}".Trim();
    }

    private static string Terminated(char[] buffer)
    {
        int end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    /// <summary>Returns only the listed records that belong to HIDMaestro,
    /// using a strict <c>Provider Name == "HIDMaestro"</c> filter combined
    /// with an allow-list of original INF names, rather than
    /// substring-matching pnputil output.</summary>
    private static List<DriverRecord> HidMaestroPackagesIn(List<DriverRecord> listing) =>
        listing
            .Where(r => r.ProviderName.Equals("HIDMaestro", StringComparison.OrdinalIgnoreCase)
                     && HidMaestroInfNames.Any(n => string.Equals(r.OriginalName, n,
                                                                  StringComparison.OrdinalIgnoreCase)))
            .ToList();

    // v1.3.0: per-process positive cache. Once we've confirmed the driver
    // is installed in this process (or successfully installed it), every
    // subsequent IsDriverInstalled / IsHidMaestroDriverInstalled call within
    // this process can skip the driver store listing. The negative case is
    // NOT cached: if the driver isn't installed yet, we want fresh state on
    // every check until it is.
    private static volatile bool s_installedConfirmed;
    internal static void InvalidateInstalledCache() => s_installedConfirmed = false;
    internal static void MarkInstalledConfirmed() => s_installedConfirmed = true;

    /// <summary>True iff a HIDMaestro package matching every entry in
    /// <see cref="HidMaestroInfNames"/> is currently in the driver store.
    /// Replaces the previous substring grep on enum output.</summary>
    /// <exception cref="InvalidOperationException">The driver store's folders
    /// did not show the packages and the driver store listing failed. The
    /// message names the failure. A failed listing is never read as "not
    /// installed".</exception>
    public static bool IsHidMaestroDriverInstalled()
    {
        if (s_installedConfirmed) return true;

        // T37: cheap filesystem-level fast path. The DriverStore\FileRepository
        // directory layout is stable across every supported Windows version;
        // checking for hidmaestro.inf_* and hidmaestro_xusb.inf_* dirs is the
        // same signal the driver store listing would surface, but in ~5 ms.
        // d3xMachina's gh#18 log showed "driver install check
        // (deployed=True) in 1695ms" on the first call when this check ran
        // pnputil; this fast path eliminates that. Falls through to the
        // listing on filesystem-check failure (e.g., DriverStore
        // inaccessible) so the strict provider-name filter still validates
        // correctness in edge cases.
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
        catch { /* fall through to the driver store listing */ }

        return InstalledFrom(() => ListPackages(IsHidMaestroInf));
    }

    /// <summary><see cref="IsHidMaestroDriverInstalled"/>'s answer from the
    /// listing <paramref name="list"/> gives. Throws, naming the failure,
    /// when the listing failed. Coexistence probe OF11 calls it through
    /// reflection.</summary>
    internal static bool InstalledFrom(Func<List<DriverRecord>> list)
    {
        var pkgs = HidMaestroPackagesIn(ListingOrThrow(list));
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
    /// PnP bindings that don't show up in <c>/enum-devices</c>: the "device
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
            // Driver-in-use is racy with device removal: give the PnP stack a
            // moment to release before retrying. Doesn't matter if the wait is
            // wasted; this only runs during cleanup.
            if (output.Contains("in use", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("currently being used", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("presently installed", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(TimeoutScale.Apply(500));
                continue;
            }
            // Some other failure (corrupt INF, missing perm, …): don't retry.
            return false;
        }
        return false;
    }

    /// <summary>Add a package to the driver store without installing it on
    /// any device.</summary>
    public static (int exitCode, string output) AddDriver(string infPath)
        => Run($"/add-driver \"{infPath}\"", timeoutMs: 60_000);

    /// <summary>The answer of a plain delete, /delete-driver with no
    /// /uninstall and no /force, so a package a devnode still uses, present
    /// or not, stays: done only on exit code 0 with no failure in pnputil's
    /// output.</summary>
    internal static bool PlainDeleteDone(int exitCode, string output) =>
        exitCode == 0 && !output.Contains("Failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes every HIDMaestro package from the driver store and
    /// verifies they're actually gone. Throws <see cref="InvalidOperationException"/>
    /// if any package can't be removed, or if the driver store cannot be
    /// listed before the removal or after it: a failed listing is not an
    /// empty one, so it never reads as nothing to remove or nothing left.
    /// This is the strict version that prevents the "stale entry blocks
    /// reinstall" failure mode.</summary>
    public static void RemoveAllHidMaestroPackages() => RemoveAllHidMaestroPackages(() => ListPackages(IsCleanupInf));

    /// <summary>The same, with <paramref name="list"/> for each listing.
    /// Coexistence probe OF11 calls it through reflection with a listing
    /// that fails, which throws before any delete.</summary>
    internal static void RemoveAllHidMaestroPackages(Func<List<DriverRecord>> list)
    {
        // Removal busts the per-process positive cache; the next
        // IsHidMaestroDriverInstalled call will re-enumerate.
        InvalidateInstalledCache();

        var cleanup = ListingOrThrow(list)
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
        var remaining = HidMaestroPackagesIn(ListingOrThrow(list));
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

    /// <summary>The listing <paramref name="list"/> gives, or an
    /// <see cref="InvalidOperationException"/> that names why it
    /// failed.</summary>
    private static List<DriverRecord> ListingOrThrow(Func<List<DriverRecord>> list)
    {
        try { return list(); }
        catch (InvalidOperationException e)
        {
            throw new InvalidOperationException(
                $"HIDMaestro could not list the driver store's packages: {e.Message.TrimEnd('.')}.", e);
        }
    }

    private static bool IsHidMaestroInf(string original) =>
        HidMaestroInfNames.Any(n => string.Equals(original, n, StringComparison.OrdinalIgnoreCase));

    private static bool IsCleanupInf(string original) =>
        HidMaestroInfNamesForCleanup.Any(n => string.Equals(original, n, StringComparison.OrdinalIgnoreCase));

    // winerror.h.
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int ERROR_NOT_FOUND = 1168;

    // SetupAPI.h: INF_STYLE_WIN4, MAX_INF_STRING_LENGTH (in characters), and
    // the HINF SetupOpenInfFile returns on failure, INVALID_HANDLE_VALUE.
    private const uint INF_STYLE_WIN4 = 0x00000002;
    private const int MaxInfStringLength = 4096;
    private static readonly IntPtr InvalidInfHandle = new(-1);

    /// <summary>SetupAPI.h's INFCONTEXT. Opaque to the caller.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct INFCONTEXT
    {
        public IntPtr Inf;
        public IntPtr CurrentInf;
        public uint Section;
        public uint Line;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupGetInfDriverStoreLocationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupGetInfDriverStoreLocation(string fileName, IntPtr alternatePlatformInfo, string? localeName,
        [Out] char[] returnBuffer, uint returnBufferSize, out uint requiredSize);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupOpenInfFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SetupOpenInfFile(string fileName, string? infClass, uint infStyle, out uint errorLine);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupCloseInfFile")]
    private static partial void SetupCloseInfFile(IntPtr infHandle);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupFindFirstLineW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupFindFirstLine(IntPtr infHandle, string section, string? key, out INFCONTEXT context);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupGetStringFieldW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupGetStringField(ref INFCONTEXT context, uint fieldIndex,
        [Out] char[] returnBuffer, uint returnBufferSize, out uint requiredSize);
}
