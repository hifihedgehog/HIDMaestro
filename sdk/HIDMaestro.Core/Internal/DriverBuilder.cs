using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HIDMaestro.Internal;

/// <summary>
/// Self-contained driver installer. The driver binaries (HIDMaestro.dll,
/// HMXInput.dll), their INFs, and every external tool needed to sign + catalog
/// + deploy them (signtool.exe + SXS deps, inf2cat.exe + deps) ship as embedded
/// resources inside HIDMaestro.Core.dll. At install time the whole payload is
/// staged under <c>%ProgramData%\HIDMaestro\Staging\&lt;version&gt;-&lt;hash&gt;\driver</c>
/// (<see cref="ProtectedStaging"/>), each file checked against its embedded
/// SHA-256, and the SDK shells out to the staged tools.
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

    /// <summary>Logical-name prefix for the payload built for this
    /// machine's architecture. The assembly is AnyCPU and carries both
    /// sets, so the choice is made at extraction time rather than at
    /// build time. Every lookup of a driver, INF, helper or signing tool
    /// goes through here, including SwdDeviceFactory's and
    /// EmbeddedManifest's.</summary>
    internal static string NativePrefix => NativePrefixFor(RuntimeInformation.OSArchitecture);

    /// <summary>The payload prefix for a given architecture, so a check on
    /// one machine can extract the other's payload.</summary>
    internal static string NativePrefixFor(Architecture arch) =>
        arch == Architecture.Arm64
            ? "HIDMaestro.Native.arm64."
            : "HIDMaestro.Native.x64.";

    /// <summary>Architecture-specific payload: the driver, the XUSB
    /// companion, the SWD helper, both stamped INFs and the signing tool
    /// tree. The machine that installs these is the one that runs them.</summary>
    static readonly string[] EmbeddedFiles = new[]
    {
        // Drivers + INFs
        "HIDMaestro.dll",
        "HMXInput.dll",
        "hidmaestro.inf",
        "hidmaestro_xusb.inf",

        // signtool.exe + its SXS dep tree, built for this architecture
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

    };

    /// <summary>Architecture-neutral payload. Inf2Cat.exe and its
    /// UniversalStore libraries are AMD64 .NET assemblies, so on ARM64 they
    /// run under Windows 11's x64 emulation, which Windows 10 on ARM does
    /// not have. One copy serves both targets. These six are the files
    /// Inf2Cat loads or reads. inf2cat.exe.manifest and
    /// Microsoft.Kits.Logger.dll, staged until v1.13.0, did nothing: no
    /// assembly here references the logger, and Inf2Cat.exe's embedded
    /// manifest takes precedence over an external one.</summary>
    static readonly string[] NeutralFiles = new[]
    {
        "Inf2Cat.exe",
        "WindowsProtectedFiles.xml",
        "Microsoft.UniversalStore.HardwareWorkflow.Cabinets.dll",
        "Microsoft.UniversalStore.HardwareWorkflow.Catalogs.dll",
        "Microsoft.UniversalStore.HardwareWorkflow.InfReader.dll",
        "Microsoft.UniversalStore.HardwareWorkflow.SubmissionBuilder.dll",
    };

    /// <summary>Every file the staging directory must hold, paired with
    /// the manifest resource name it comes from.</summary>
    private static IEnumerable<(string File, string Logical)> AllPayload() =>
        PayloadFor(RuntimeInformation.OSArchitecture);

    /// <summary>The staging set for a given architecture: its own payload
    /// plus the neutral tool tree.</summary>
    internal static IEnumerable<(string File, string Logical)> PayloadFor(Architecture arch)
    {
        string prefix = NativePrefixFor(arch);
        foreach (string f in EmbeddedFiles) yield return (f, prefix + f);
        foreach (string f in NeutralFiles) yield return (f, "HIDMaestro.Resources." + f);
    }

    /// <summary>Stages the embedded payload into this build's protected
    /// "driver" staging directory and returns its path. Requires elevation.
    ///
    /// <para><b>Cross-launch reuse:</b> the directory is named for the SDK
    /// version and the first 16 hex chars of
    /// <see cref="EmbeddedManifest.Sha256Hex"/>, so different builds land in
    /// different directories and an identical build reuses its own. A file
    /// already staged is reused only when its SHA-256 still matches the
    /// embedded bytes, so a warm launch rewrites nothing. Until v1.13.0 the
    /// directory was under <c>%TEMP%</c> and reuse trusted a matching file
    /// size.</para>
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
            // Re-check inside the lock: the other thread may have just
            // populated the cache.
            if (StagingDir != null && Directory.Exists(StagingDir))
                return StagingDir;

            // A build without this machine's driver stops here, before the
            // staging directory exists.
            HMBuildInfo.ThrowIfMissing(HMPayload.Driver);

            // The protected per-version "driver" staging directory, created
            // and verified Administrators/SYSTEM-write-only (ProtectedStaging).
            // Each file's SHA-256 is checked against the embedded resource on
            // write and on reuse, which replaces the old length-only trust of
            // a %TEMP% directory any process of the user could write.
            string dir = ProtectedStaging.DirFor(ProtectedStaging.OperationDriver);

            var asm = typeof(DriverBuilder).Assembly;
            foreach (var (file, logical) in AllPayload())
                ProtectedStaging.StageResource(dir, file, asm, logical);

            StagingDir = dir;
            return dir;
        }
    }

    // ── Test signing certificate (managed, no powershell) ──────────────────

    /// <summary>Ensures HIDMaestroTestCert exists in LocalMachine\My and is
    /// trusted in Root + TrustedPublisher. Uses the managed
    /// <see cref="CertificateRequest"/> API: no external tool needed.</summary>
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

    /// <summary>Signs HIDMaestro.dll and HMXInput.dll of a driver package
    /// staged fresh from the embedded bytes, with the staged signtool.exe and
    /// the test certificate from LocalMachine\My.</summary>
    public static bool SignDrivers()
    {
        SignDriversIn(EnsureExtracted(), s_packageDir ??= StagePackage());
        return true;
    }

    private static void SignDriversIn(string tools, string package)
    {
        EnsureTestCertificate();
        s_packageDir = package;
        string signtool = Path.Combine(tools, "signtool.exe");

        var asm = typeof(DriverBuilder).Assembly;
        foreach (string dll in new[] { "HIDMaestro.dll", "HMXInput.dll" })
        {
            string path = Path.Combine(package, dll);
            if (!File.Exists(path)) continue;
            ProtectedStaging.VerifyBeforeUse(path, asm, NativePrefix + dll);
            var (rc, output) = Run(signtool,
                $"sign /a /sm /s My /n {CertFriendlyName} /fd SHA256 \"{path}\"",
                workingDir: tools);
            if (rc != 0)
                throw new InvalidOperationException($"signtool failed for {dll}: {output}");
        }
    }

    /// <summary>The Inf2Cat <c>/os:</c> value for an architecture's INFs.
    /// The catalog names the architecture the INF is decorated for.
    ///
    /// <para>Issue #63. Inf2Cat has no bare <c>10_ARM64</c>: its ARM64 values
    /// name a release, starting at <c>10_RS3_ARM64</c> because 1709 is the
    /// first Windows 10 release on ARM64. <c>10_X64</c> exists and covers
    /// every Windows 10 release. Both produce the same catalog OS attribute,
    /// <c>2:10.0</c>, so the ARM64 catalog reads to Windows exactly as the x64
    /// one does.</para></summary>
    internal static string CatalogOsFor(Architecture arch) =>
        arch == Architecture.Arm64 ? "10_RS3_ARM64" : "10_X64";

    /// <summary>Generates the catalogs for the driver package's INFs and
    /// signs each resulting .cat file with the test certificate.</summary>
    public static bool GenerateCatalogs()
    {
        GenerateCatalogsIn(EnsureExtracted(), s_packageDir ??= StagePackage());
        return true;
    }

    private static void GenerateCatalogsIn(string tools, string package)
    {
        string inf2cat = Path.Combine(tools, "Inf2Cat.exe");
        string signtool = Path.Combine(tools, "signtool.exe");

        // Delete any stale catalogs from a previous run.
        foreach (string cat in Directory.GetFiles(package, "*.cat"))
        {
            try { File.Delete(cat); } catch { }
        }

        string catalogOs = CatalogOsFor(RuntimeInformation.OSArchitecture);
        var (rc, output) = Run(inf2cat, $"/driver:\"{package}\" /os:{catalogOs}", workingDir: tools);
        if (rc != 0)
            throw new InvalidOperationException($"inf2cat failed: {output}");

        foreach (string cat in Directory.GetFiles(package, "*.cat"))
        {
            var (rc2, output2) = Run(signtool,
                $"sign /a /sm /s My /n {CertFriendlyName} /fd SHA256 \"{cat}\"",
                workingDir: tools);
            if (rc2 != 0)
                throw new InvalidOperationException($"Catalog sign failed for {cat}: {output2}");
        }
    }

    /// <summary>Catalog and sign an INF-only package staged in its own
    /// directory, with the same Inf2Cat, signtool and test certificate the
    /// driver package uses, such as a profile's WinUSB binding. Inf2Cat
    /// catalogs every INF in the directory it is given, which is why the
    /// package has a directory of its own.</summary>
    internal static void CatalogAndSignPackage(string packageDir)
    {
        EnsureTestCertificate();
        string tools = EnsureExtracted();
        var asm = typeof(DriverBuilder).Assembly;
        string inf2cat = Path.Combine(tools, "Inf2Cat.exe");
        string signtool = Path.Combine(tools, "signtool.exe");
        ProtectedStaging.VerifyBeforeUse(signtool, asm, NativePrefix + "signtool.exe");
        ProtectedStaging.VerifyBeforeUse(inf2cat, asm, "HIDMaestro.Resources.Inf2Cat.exe");

        foreach (string cat in Directory.GetFiles(packageDir, "*.cat"))
        {
            try { File.Delete(cat); } catch { }
        }

        string catalogOs = CatalogOsFor(RuntimeInformation.OSArchitecture);
        var (rc, output) = Run(inf2cat, $"/driver:\"{packageDir}\" /os:{catalogOs}", workingDir: tools);
        if (rc != 0)
            throw new InvalidOperationException($"inf2cat failed: {output}");

        foreach (string cat in Directory.GetFiles(packageDir, "*.cat"))
        {
            var (rc2, output2) = Run(signtool,
                $"sign /a /sm /s My /n {CertFriendlyName} /fd SHA256 \"{cat}\"",
                workingDir: tools);
            if (rc2 != 0)
                throw new InvalidOperationException($"Catalog sign failed for {cat}: {output2}");
        }
    }

    /// <summary>Installs the driver package's INFs via pnputil (Windows builtin).</summary>
    public static bool InstallDrivers()
    {
        InstallDriversFrom(s_packageDir ??= StagePackage());
        return true;
    }

    private static void InstallDriversFrom(string dir)
    {
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
            // condition check below: did the package land in the driver store?
            // PnputilHelper.IsHidMaestroDriverInstalled reads /enum-drivers
            // /format xml which is locale-stable.
        }

        if (!PnputilHelper.IsHidMaestroDriverInstalled())
            throw new InvalidOperationException(
                "pnputil /add-driver completed but driver store does not contain "
              + "all required HIDMaestro packages. "
              + $"Required INFs: {string.Join(", ", PnputilHelper.HidMaestroInfNames)}.");

        // /scan-devices removed (Option 1). Our INFs are function INFs that bind via
        // SwDeviceCreate's own install path: /scan-devices was a no-op for
        // them. On corporate workstations with many devices in the PnP tree
        // this scan was 5–20 s of pure overhead. RemoveAllVirtualControllers
        // (called before FullDeploy) DIF_REMOVEs any of our prior-session
        // devnodes; nothing else needs the scan.
    }

    /// <summary>Removes all HIDMaestro driver packages from the driver store.
    /// Strict: matches by <c>Provider Name == "HIDMaestro"</c> + the explicit
    /// INF allow-list, retries on "in use" failures, and verifies removal.
    /// Throws if anything is left in the store afterward: that prevents
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
    /// compatibility but ignored: there is no source build step any more
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
        // v1.13.0: every deploy goes through the machine-wide policy and its
        // lock (DriverDeploy): never a downgrade, never an upgrade under a
        // running program's controllers.
        DriverDeploy.Run();
        return true;
    }

    /// <summary>The extract, sign, catalog and install pipeline. The caller
    /// holds <see cref="DriverDeploy.MutexObject"/> and has checked that no
    /// device runs on the installed driver.</summary>
    internal static void DeployLocked()
    {
        // Hosts that still map the old DLL would hand its code to a new
        // device after the package changes. With no HIDMaestro device left,
        // every such host is an orphan.
        DeviceOrchestrator.ReleaseStaleDriverHosts();
        string tools = EnsureExtracted();
        VerifyStagedBeforeUse(tools);
        string package = StagePackage();
        RemoveOldDriverPackages();
        SignDriversIn(tools, package);
        GenerateCatalogsIn(tools, package);
        InstallDriversFrom(package);
        RecordInstalledPayload();
    }

    /// <summary>The driver package this deploy signs and installs: the two
    /// binaries and two INFs, staged fresh from the embedded bytes into the
    /// deploy's own directory, and no catalog. Signing rewrites the binaries,
    /// so the driver staging never holds a signed one: a second deploy in
    /// this process finds it as embedded, and another process of this build
    /// that stages it never rewrites a file this deploy is signing. Only a
    /// holder of the deploy lock calls this.</summary>
    internal static string StagePackage()
    {
        string dir = ProtectedStaging.DirFor(ProtectedStaging.OperationPackage);
        foreach (string cat in Directory.GetFiles(dir, "*.cat"))
            File.Delete(cat);
        var asm = typeof(DriverBuilder).Assembly;
        foreach (string f in PackageFiles)
            ProtectedStaging.StageResource(dir, f, asm, NativePrefix + f);
        return dir;
    }

    private static readonly string[] PackageFiles = { "HIDMaestro.dll", "HMXInput.dll", "hidmaestro.inf", "hidmaestro_xusb.inf" };

    /// <summary>The driver package's two INFs.</summary>
    internal static readonly string[] PackageInfs = { "hidmaestro.inf", "hidmaestro_xusb.inf" };

    /// <summary>Add this build's packages of <paramref name="infs"/> to the
    /// driver store and remove nothing, for a set a deploy left incomplete.
    /// Signed and catalogued as a full deploy does it. The caller holds the
    /// deploy lock.</summary>
    internal static void AddPackagesLocked(IReadOnlyCollection<string> infs)
    {
        if (infs.Count == 0) return;
        string tools = EnsureExtracted();
        VerifyStagedBeforeUse(tools);
        string package = StagePackage();
        SignDriversIn(tools, package);
        GenerateCatalogsIn(tools, package);
        string pnputil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");
        var embedded = DriverDeploy.EmbeddedDriver();
        foreach (string inf in infs)
        {
            DeviceOrchestrator.LogDiag($"    deploy: adding the missing {inf} of {embedded}");
            Run(pnputil, $"/add-driver \"{Path.Combine(package, inf)}\" /install", timeoutMs: 30_000);
            if (!DriverDeploy.InstalledStamps(inf).Any(s => s.CompareTo(embedded) == 0))
                throw new InvalidOperationException($"pnputil /add-driver did not add {inf} {embedded} to the driver store.");
        }
        PnputilHelper.InvalidateInstalledCache();
    }

    private static string? s_packageDir;

    /// <summary>Re-check, against the embedded bytes, the signing and
    /// catalog tools about to run elevated. A staged tool changed since
    /// <see cref="EnsureExtracted"/> is caught here, before anything runs.
    /// The package's two binaries are checked the same way just before
    /// signtool rewrites them (<see cref="SignDriversIn"/>). The signed
    /// binaries and the generated catalogs are not re-checked afterward,
    /// because signing and inf2cat change their bytes by design.</summary>
    private static void VerifyStagedBeforeUse(string dir)
    {
        var asm = typeof(DriverBuilder).Assembly;
        ProtectedStaging.VerifyBeforeUse(Path.Combine(dir, "signtool.exe"), asm, NativePrefix + "signtool.exe");
        ProtectedStaging.VerifyBeforeUse(Path.Combine(dir, "Inf2Cat.exe"), asm, "HIDMaestro.Resources.Inf2Cat.exe");
    }

    /// <summary>Record the embedded payload's hash as the installed one,
    /// which arms the fast path.</summary>
    internal static void RecordInstalledPayload() => WriteInstalledManifestHash(EmbeddedManifest.Sha256Hex);

    /// <summary>True when the installed payload is this SDK's: the manifest
    /// hash recorded at install matches the embedded one, and the driver
    /// store holds both packages. Checking presence with a FileRepository
    /// directory walk costs about 1 ms, against 200 to 500 ms for pnputil
    /// /enum-drivers. This is the same-version fast path
    /// <see cref="DriverDeploy.Run"/> takes outside any lock.</summary>
    internal static bool WillTakeFastPath()
    {
        try
        {
            return string.Equals(EmbeddedManifest.Sha256Hex, ReadInstalledManifestHash(),
                       StringComparison.OrdinalIgnoreCase)
                && DriverStoreContainsHidMaestro();
        }
        catch { return false; }
    }

    /// <summary>Cheap filesystem-level check that the DriverStore has at
    /// least one HIDMaestro INF directory. Replaces pnputil enum on the
    /// FullDeploy fast path. The driver-store layout is stable across
    /// every supported Windows version.</summary>
    private static bool DriverStoreContainsHidMaestro()
    {
        try
        {
            string fileRepo = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "DriverStore", "FileRepository");
            if (!Directory.Exists(fileRepo)) return false;

            // hidmaestro.inf_amd64_<hash> and hidmaestro_xusb.inf_amd64_<hash>
            // are the directory names pnputil /add-driver creates. Both must
            // be present to consider the install complete.
            // T11: single FileRepository enumeration (~5–20 ms on a busy
            // store) instead of two; we exit as soon as we find both.
            bool hasMain = false, hasXusb = false;
            foreach (var dir in Directory.EnumerateDirectories(fileRepo))
            {
                string name = Path.GetFileName(dir);
                if (!hasMain && name.StartsWith("hidmaestro.inf_", StringComparison.OrdinalIgnoreCase))
                    hasMain = true;
                else if (!hasXusb && name.StartsWith("hidmaestro_xusb.inf_", StringComparison.OrdinalIgnoreCase))
                    hasXusb = true;
                if (hasMain && hasXusb) return true;
            }
            return hasMain && hasXusb;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The installed-payload record, or null.</summary>
    internal static string? InstalledManifestHash() => ReadInstalledManifestHash();

    /// <summary>Both packages are in the driver store, read now.</summary>
    internal static bool DriverStoreHasBothPackages() => DriverStoreContainsHidMaestro();

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
    /// matching by accident: see PnputilHelper for the failure-mode notes.
    ///
    /// <para>v1.3.0: cheap-first lookup ladder: (1) per-process positive
    /// cache (instant after first confirmation); (2) DriverStore filesystem
    /// check (~1 ms); (3) pnputil enum-drivers /format xml (~200–500 ms).
    /// Most calls hit (1) or (2); pnputil is only consulted when neither
    /// shortcut applies, e.g. cold start with no prior HKLM hash.</para></summary>
    public static bool IsDriverInstalled()
    {
        // Perf audit 2026-07-21: PnputilHelper.IsHidMaestroDriverInstalled
        // already runs the same ladder with a per-process positive cache in
        // front (cache -> filesystem -> pnputil). The duplicate filesystem
        // walk here cost a FileRepository enumeration per controller create
        // after the first.
        return PnputilHelper.IsHidMaestroDriverInstalled();
    }

}
