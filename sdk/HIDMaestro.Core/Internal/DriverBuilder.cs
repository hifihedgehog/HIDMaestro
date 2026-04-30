using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HIDMaestro.Internal;

/// <summary>
/// Self-contained driver installer. The driver binaries (HIDMaestro.dll,
/// HMXInput.dll), their INFs, and every external tool needed to sign + catalog
/// + deploy them (signtool.exe + SXS deps, inf2cat.exe + deps) ship as embedded
/// resources inside HIDMaestro.Core.dll. At install time we extract the whole
/// payload to %TEMP%\HIDMaestro_{guid}\ and shell out to the extracted tools.
///
/// No WDK install is required on the consumer's machine. The SDK consumer ships
/// a single DLL.
/// </summary>
public static class DriverBuilder
{
    const string CertSubject = "CN=HIDMaestroTestCert";
    const string CertFriendlyName = "HIDMaestroTestCert";

    /// <summary>The directory the most recent extraction landed in. Other
    /// internal helpers (e.g. DeviceOrchestrator) read the staged INF from
    /// here when wiring a device node. Null until <see cref="FullDeploy"/>
    /// (or <see cref="EnsureExtracted"/>) has run at least once.</summary>
    public static string? StagingDir { get; private set; }

    /// <summary>Back-compat shim. Old code referenced
    /// <c>DriverBuilder.BuildDir</c> for the directory containing
    /// <c>hidmaestro.inf</c>. Now points at the extraction staging dir.
    /// Calls <see cref="EnsureExtracted"/> the first time it's accessed.</summary>
    public static string BuildDir => EnsureExtracted();

    static (int exitCode, string output) Run(string fileName, string arguments,
        string? workingDir = null, int timeoutMs = 60_000)
    {
        // Async-drain pattern: synchronous ReadToEnd before WaitForExit
        // deadlocks when the child fills stderr's 4 KB pipe buffer while
        // we're draining stdout (child blocks on stderr write, our reader
        // waits for stdout EOF that only fires at exit). PnputilHelper.Run
        // and DeviceOrchestrator.RunProcess use the same pattern.
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (workingDir != null) psi.WorkingDirectory = workingDir;

        using var proc = Process.Start(psi)!;
        var sb = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        int scaledTimeout = TimeoutScale.Apply(timeoutMs);
        if (!proc.WaitForExit(scaledTimeout))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { proc.WaitForExit(TimeoutScale.Apply(2000)); } catch { }
            return (-1, $"timeout after {scaledTimeout} ms (scale={TimeoutScale.Factor})\n{sb}");
        }
        return (proc.ExitCode, sb.ToString());
    }

    // ── Extraction ──────────────────────────────────────────────────────────

    /// <summary>The full set of files we extract from embedded resources.
    /// Names match the LogicalName suffix used in the csproj
    /// (HIDMaestro.Resources.{Filename}{Extension}).</summary>
    static readonly string[] EmbeddedFiles = new[]
    {
        // Drivers + INFs
        "HIDMaestro.dll",
        "HMXInput.dll",
        "hidmaestro.inf",
        "hidmaestro_xusb.inf",

        // signtool.exe + its SXS dep tree (x64)
        "signtool.exe",
        "signtool.exe.manifest",
        "mssign32.dll",
        "Microsoft.Windows.Build.Signing.mssign32.dll.manifest",
        "wintrust.dll",
        "wintrust.dll.ini",
        "Microsoft.Windows.Build.Signing.wintrust.dll.manifest",
        "appxsip.dll",
        "Microsoft.Windows.Build.Appx.AppxSip.dll.manifest",
        "appxpackaging.dll",
        "Microsoft.Windows.Build.Appx.AppxPackaging.dll.manifest",
        "opcservices.dll",
        "Microsoft.Windows.Build.Appx.OpcServices.dll.manifest",

        // inf2cat.exe + minimum deps (x86)
        "Inf2Cat.exe",
        "inf2cat.exe.manifest",
        "WindowsProtectedFiles.xml",
        "Microsoft.UniversalStore.HardwareWorkflow.Cabinets.dll",
        "Microsoft.UniversalStore.HardwareWorkflow.Catalogs.dll",
        "Microsoft.UniversalStore.HardwareWorkflow.InfReader.dll",
        "Microsoft.UniversalStore.HardwareWorkflow.SubmissionBuilder.dll",
        "Microsoft.Kits.Logger.dll",
    };

    /// <summary>Lazily extracts the embedded payload to a deterministic
    /// per-manifest-hash directory under %TEMP% and returns the path.
    ///
    /// <para><b>Cross-launch cache (v1.3.0+):</b> the staging directory
    /// is named <c>HIDMaestro_&lt;sha256-prefix&gt;</c> where the prefix
    /// is the first 16 hex chars of <see cref="EmbeddedManifest.Sha256Hex"/>.
    /// Different SDK versions land in different dirs (auto-invalidation
    /// without explicit cleanup); identical SDK rebuilds with identical
    /// embedded payload reuse the same dir. On warm launches we just
    /// validate file count + size and return — no rewrites. Drops
    /// extraction time from 200 ms–3 s on slow eMMC to single-digit ms.</para>
    ///
    /// <para>Within a single process, idempotent via the
    /// <see cref="StagingDir"/> static cache (set on first successful
    /// extract, never invalidated mid-process).</para></summary>
    private static readonly object s_extractLock = new();

    public static string EnsureExtracted()
    {
        // Fast path (no lock): cache hit on the static field.
        if (StagingDir != null && Directory.Exists(StagingDir))
            return StagingDir;

        // Slow path: serialize across the (rare) cache-miss case so the
        // ctor pre-warm task and a foreground InstallDriver call can't
        // both extract to the same directory and step on each other's
        // file writes.
        lock (s_extractLock)
        {
            // Re-check inside the lock — the other thread may have just
            // populated the cache.
            if (StagingDir != null && Directory.Exists(StagingDir))
                return StagingDir;

            string hashPrefix = EmbeddedManifest.Sha256Hex.Substring(0, 16);
            string dir = Path.Combine(Path.GetTempPath(), $"HIDMaestro_{hashPrefix}");

            // Cache hit: dir exists with the right set of files at the right
            // sizes. Trust the contents — corruption is rare and the next
            // signtool / inf2cat / pnputil run will surface any tampering.
            if (Directory.Exists(dir) && IsStagingDirComplete(dir))
            {
                StagingDir = dir;
                return dir;
            }

            // Cache miss or partial dir: re-extract everything.
            Directory.CreateDirectory(dir);

            var asm = typeof(DriverBuilder).Assembly;
            var available = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in asm.GetManifestResourceNames())
                available[name] = name;

            foreach (string file in EmbeddedFiles)
            {
                string logical = "HIDMaestro.Resources." + file;
                if (!available.TryGetValue(logical, out var actual))
                    throw new InvalidOperationException(
                        $"Embedded resource '{logical}' not found in HIDMaestro.Core.dll. " +
                        "Did the PackResources MSBuild target run?");

                using var stream = asm.GetManifestResourceStream(actual)
                    ?? throw new InvalidOperationException($"Failed to open resource '{actual}'.");
                string outPath = Path.Combine(dir, file);
                using var fs = File.Create(outPath);
                stream.CopyTo(fs);
            }

            StagingDir = dir;
            return dir;
        }
    }

    /// <summary>Validates that an existing staging dir contains every
    /// expected file at the same size as the embedded resource. Catches
    /// partial extracts (process killed mid-extract) and prevents a stale
    /// dir from satisfying the cache.</summary>
    private static bool IsStagingDirComplete(string dir)
    {
        try
        {
            var asm = typeof(DriverBuilder).Assembly;
            foreach (string file in EmbeddedFiles)
            {
                string outPath = Path.Combine(dir, file);
                if (!File.Exists(outPath)) return false;

                using var stream = asm.GetManifestResourceStream("HIDMaestro.Resources." + file);
                if (stream == null) return false;
                long expected = stream.Length;
                long actual = new FileInfo(outPath).Length;
                if (expected != actual) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Test signing certificate (managed, no powershell) ──────────────────

    /// <summary>Ensures HIDMaestroTestCert exists in LocalMachine\My and is
    /// trusted in Root + TrustedPublisher. Uses the managed
    /// <see cref="CertificateRequest"/> API — no external tool needed.</summary>
    public static void EnsureTestCertificate()
    {
        // Check LocalMachine\My first.
        using (var lmMy = new X509Store(StoreName.My, StoreLocation.LocalMachine))
        {
            lmMy.Open(OpenFlags.ReadOnly);
            var existing = lmMy.Certificates.Find(X509FindType.FindBySubjectName, CertFriendlyName, false);
            if (existing.Count > 0)
                return;
        }

        // Generate fresh self-signed cert with code-signing EKU.
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(CertSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, critical: false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, critical: false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(10);
        using var cert = req.CreateSelfSigned(notBefore, notAfter);
        cert.FriendlyName = CertFriendlyName;

        // Re-import with PersistKeySet so the private key lives in the machine
        // key store (signtool needs it from LocalMachine\My).
        byte[] pfx = cert.Export(X509ContentType.Pfx, "");
        using var persistableCert = X509CertificateLoader.LoadPkcs12(pfx, "",
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
        persistableCert.FriendlyName = CertFriendlyName;

        AddToStore(persistableCert, StoreName.My);
        AddToStore(persistableCert, StoreName.Root);
        AddToStore(persistableCert, StoreName.TrustedPublisher);
    }

    static void AddToStore(X509Certificate2 cert, StoreName name)
    {
        using var store = new X509Store(name, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cert);
    }

    // ── Sign / Catalog / Install pipeline ──────────────────────────────────

    /// <summary>Signs HIDMaestro.dll and HMXInput.dll using the extracted
    /// signtool.exe and the test certificate from LocalMachine\My.</summary>
    public static bool SignDrivers()
    {
        EnsureTestCertificate();
        string dir = EnsureExtracted();
        string signtool = Path.Combine(dir, "signtool.exe");

        foreach (string dll in new[] { "HIDMaestro.dll", "HMXInput.dll" })
        {
            string path = Path.Combine(dir, dll);
            if (!File.Exists(path)) continue;
            var (rc, output) = Run(signtool,
                $"sign /a /sm /s My /n {CertFriendlyName} /fd SHA256 \"{path}\"",
                workingDir: dir);
            if (rc != 0)
                throw new InvalidOperationException($"signtool failed for {dll}: {output}");
        }
        return true;
    }

    /// <summary>Generates the catalogs for the extracted INFs and signs each
    /// resulting .cat file with the test certificate.</summary>
    public static bool GenerateCatalogs()
    {
        string dir = EnsureExtracted();
        string inf2cat = Path.Combine(dir, "Inf2Cat.exe");
        string signtool = Path.Combine(dir, "signtool.exe");

        // Delete any stale catalogs from a previous run.
        foreach (string cat in Directory.GetFiles(dir, "*.cat"))
        {
            try { File.Delete(cat); } catch { }
        }

        var (rc, output) = Run(inf2cat, $"/driver:\"{dir}\" /os:10_X64", workingDir: dir);
        if (rc != 0)
            throw new InvalidOperationException($"inf2cat failed: {output}");

        foreach (string cat in Directory.GetFiles(dir, "*.cat"))
        {
            var (rc2, output2) = Run(signtool,
                $"sign /a /sm /s My /n {CertFriendlyName} /fd SHA256 \"{cat}\"",
                workingDir: dir);
            if (rc2 != 0)
                throw new InvalidOperationException($"Catalog sign failed for {cat}: {output2}");
        }
        return true;
    }

    /// <summary>Installs the staged INFs via pnputil (Windows builtin).</summary>
    public static bool InstallDrivers()
    {
        string dir = EnsureExtracted();
        string pnputil = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");

        // hidmaestro.inf is the main HID driver. hidmaestro_xusb.inf is the
        // XUSB companion (setup class System so Windows.Gaming.Input.dll does
        // not classify it as a second WGI Gamepad; xinput1_4 discovery scans
        // the {EC87F1E3} interface class directly so it still finds it).
        foreach (string inf in new[] { "hidmaestro.inf", "hidmaestro_xusb.inf" })
        {
            string path = Path.Combine(dir, inf);
            if (!File.Exists(path)) continue;
            Run(pnputil, $"/add-driver \"{path}\" /install", timeoutMs: 30_000);
            // pnputil /add-driver does not support /format, so its rc and output
            // are locale- and version-keyed and unreliable for success detection
            // (see issue #17). The authoritative success signal is the post-
            // condition check below — did the package land in the driver store?
            // PnputilHelper.IsHidMaestroDriverInstalled reads /enum-drivers
            // /format xml which is locale-stable.
        }

        if (!PnputilHelper.IsHidMaestroDriverInstalled())
            throw new InvalidOperationException(
                "pnputil /add-driver completed but driver store does not contain "
              + "all required HIDMaestro packages. "
              + $"Required INFs: {string.Join(", ", PnputilHelper.HidMaestroInfNames)}.");

        // /scan-devices removed (Option 1). Our INFs are function INFs that bind via
        // SwDeviceCreate's own install path — /scan-devices was a no-op for
        // them. On corporate workstations with many devices in the PnP tree
        // this scan was 5–20 s of pure overhead. RemoveAllVirtualControllers
        // (called before FullDeploy) DIF_REMOVEs any of our prior-session
        // devnodes; nothing else needs the scan.
        return true;
    }

    /// <summary>Removes all HIDMaestro driver packages from the driver store.
    /// Strict: matches by <c>Provider Name == "HIDMaestro"</c> + the explicit
    /// INF allow-list, retries on "in use" failures, and verifies removal.
    /// Throws if anything is left in the store afterward — that prevents
    /// the next install from silently using a stale binary, which was the
    /// failure mode that hid the CPU-saturation bug for hours.</summary>
    public static void RemoveOldDriverPackages()
    {
        PnputilHelper.RemoveAllHidMaestroPackages();
    }

    /// <summary>Registry path where we record the SHA-256 of the
    /// embedded driver payload after a successful install. Read by
    /// <see cref="FullDeploy"/> on subsequent calls to short-circuit the
    /// extract + sign + catalog + install pipeline when nothing has
    /// changed. Saves 5–15 s per fresh launch on the common case.</summary>
    private const string ManifestRegPath = @"SOFTWARE\HIDMaestro";
    private const string ManifestRegValue = "InstalledManifestSha256";

    /// <summary>Full extract + sign + catalog + install pipeline. Returns
    /// true on success; throws <see cref="InvalidOperationException"/> with
    /// the failing tool's output on any pipeline-step failure. The
    /// <paramref name="rebuild"/> parameter is retained for ABI
    /// compatibility but ignored — there is no source build step any more
    /// (the driver binaries ship pre-built inside the SDK DLL).
    ///
    /// <para><b>Fast-path (v1.3.0+):</b> if the SHA-256 of the embedded
    /// driver payload matches the value previously stored at
    /// <c>HKLM\Software\HIDMaestro\InstalledManifestSha256</c> AND
    /// <see cref="PnputilHelper.IsHidMaestroDriverInstalled"/> returns
    /// true, the entire pipeline is skipped. Drops fresh-launch
    /// InstallDriver() from 5–15 s to ~50 ms.</para></summary>
    public static bool FullDeploy(bool rebuild = true)
    {
        _ = rebuild; // intentionally unused

        // Fast path: matching manifest hash means embedded payload is
        // bit-identical to what's currently in the DriverStore. Verify
        // presence one more time via pnputil (cheap; locale-stable XML
        // path) and skip the whole pipeline if both checks pass.
        string embeddedHash = EmbeddedManifest.Sha256Hex;
        string? installedHash = ReadInstalledManifestHash();
        if (string.Equals(embeddedHash, installedHash, StringComparison.OrdinalIgnoreCase)
            && PnputilHelper.IsHidMaestroDriverInstalled())
        {
            return true;
        }

        EnsureExtracted();
        RemoveOldDriverPackages();
        SignDrivers();
        GenerateCatalogs();
        InstallDrivers();

        WriteInstalledManifestHash(embeddedHash);
        return true;
    }

    private static string? ReadInstalledManifestHash()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(ManifestRegPath, writable: false);
            return k?.GetValue(ManifestRegValue) as string;
        }
        catch { return null; }
    }

    private static void WriteInstalledManifestHash(string hash)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(ManifestRegPath, writable: true);
            k?.SetValue(ManifestRegValue, hash, Microsoft.Win32.RegistryValueKind.String);
        }
        catch
        {
            // Non-fatal: cache miss next launch just means we'll re-run
            // the pipeline. The install itself succeeded.
        }
    }

    /// <summary>Checks if ALL required HIDMaestro drivers are in the store.
    /// Strict: requires a record per INF where <c>Provider Name == "HIDMaestro"</c>.
    /// Substring grepping was previously vulnerable to half-removed entries
    /// matching by accident — see PnputilHelper for the failure-mode notes.</summary>
    public static bool IsDriverInstalled()
    {
        return PnputilHelper.IsHidMaestroDriverInstalled();
    }

}
