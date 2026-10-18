using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace HIDMaestro.Internal;

/// <summary>
/// Self-contained driver installer. The driver binaries (HIDMaestro.dll,
/// HMXInput.dll), their INFs, and every external tool needed to sign + catalog
/// + deploy them (signtool.exe + SXS deps, inf2cat.exe + deps) ship as embedded
/// resources inside HIDMaestro.Core.dll. At install time the whole payload is
/// staged under <c>%ProgramFiles%\HIDMaestro\Staging\&lt;version&gt;-&lt;hash&gt;\driver</c>
/// (<see cref="ProtectedStaging"/>), each file checked against its embedded
/// SHA-256, and the SDK shells out to the staged tools.
///
/// No WDK install is required on the consumer's machine. The SDK consumer ships
/// a single DLL.
/// </summary>
public static class DriverBuilder
{
    const string CertFriendlyName = "HIDMaestroTestCert";

    /// <summary>The directory the most recent extraction landed in. Other
    /// internal helpers (e.g. DeviceOrchestrator) read the staged INF from
    /// here when wiring a device node. Null until <see cref="FullDeploy"/>
    /// (or <see cref="EnsureExtracted"/>) has run at least once.</summary>
    public static string? StagingDir { get; private set; }

    /// <summary>Back-compat shim. Old code referenced
    /// <c>DriverBuilder.BuildDir</c> for the directory containing
    /// <c>hidmaestro.inf</c>. Now points at the extraction staging dir.
    /// Calls <see cref="EnsureExtracted"/> each time it is read, with its
    /// cost: the machine-wide staging lock, which another program's staging
    /// prune holds for the length of its deletes, and a SHA-256 of the 23
    /// staged files, 6.6 MB, measured at 8.9 ms p50 and 9.6 to 11.1 ms p95
    /// warm (30 runs, twice). Read it once and keep the path.</summary>
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
        // signtool and Inf2Cat run with workingDir set to the protected tools
        // folder and keep their temporary files there. pnputil runs with no
        // working directory and gets this process's protected child-temp
        // folder. If that folder cannot be made the tool does not run.
        ProtectedStaging.ConfineChildTemp(psi, workingDir);

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
            lock (sb) return (-1, $"timeout after {scaledTimeout} ms (scale={TimeoutScale.Factor})\n{sb}");
        }
        // The timed wait returns at process exit, before the handlers have
        // seen the end of both streams, so the tool's last lines, its error
        // among them, could be missing (Process.Windows.cs WaitForExitCore:
        // "If we have a hard timeout, we cannot wait for the streams").
        // WaitForExitAsync waits for both streams' end too (Process.cs,
        // WaitUntilOutputEOF), and its token bounds that wait: a child the
        // tool left holding the pipe would otherwise hold this deploy, and
        // the machine-wide lock with it, until the child ended. Disposing
        // the process on the way out cancels the readers still pending.
        using (var drain = new CancellationTokenSource(TimeoutScale.Apply(OutputDrainMs)))
        {
            try { proc.WaitForExitAsync(drain.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException)
            {
                lock (sb) return (proc.ExitCode,
                    $"{sb}(output still open {TimeoutScale.Apply(OutputDrainMs)} ms after the tool exited: a process it started holds it)\n");
            }
        }
        lock (sb) return (proc.ExitCode, sb.ToString());
    }

    /// <summary>How long a tool's output may stay open after the tool has
    /// exited before <see cref="Run"/> returns without its end.</summary>
    internal const int OutputDrainMs = 10_000;

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
    /// Microsoft.Kits.Logger.dll, staged until v2.0.0, did nothing: no
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
    /// embedded bytes, so a warm launch rewrites nothing. Until v2.0.0 the
    /// directory was under <c>%TEMP%</c> and reuse trusted a matching file
    /// size.</para>
    ///
    /// <para>Every call checks every staged file again, signtool's and
    /// Inf2Cat's libraries and manifests included, and restages any that is
    /// missing or differs, so each deploy and each caller that runs the tools
    /// uses files verified for that use. Until v2.0.0 the directory was
    /// cached for the life of the process, and only signtool.exe and
    /// Inf2Cat.exe were checked again, so a library another program's prune
    /// or anyone else removed stayed missing for every later deploy. The
    /// embedded hashes are computed once per process, so a call costs one
    /// read of the staged files, 6.6 MB: 8.9 ms p50 and 9.6 to 11.1 ms p95
    /// warm (30 runs, twice), against under 1 ms for the two files checked
    /// before. Each call also takes the machine-wide staging lock
    /// (<see cref="ProtectedStaging.StagingMutexObject"/>) for its record
    /// note, and waits while another program's prune holds it. A deploy
    /// checks the files again under its pin.</para></summary>
    private static readonly object s_extractLock = new();

    public static string EnsureExtracted()
    {
        // Serialized so the constructor's prewarm and a foreground deploy
        // never stage the same file at once.
        lock (s_extractLock)
        {
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
    /// <see cref="CertificateRequest"/> API: no external tool needed.
    /// The check and the create run under the machine-wide deploy lock, so
    /// two programs never both find it missing and add two.</summary>
    public static void EnsureTestCertificate() => EnsureTestCertificate(CertFriendlyName);

    /// <summary>The same for a certificate of another name. Coexistence
    /// probe section Z calls this through reflection with a name that does
    /// not contain HIDMaestroTestCert, which the store's subject search and
    /// signtool's <c>/n</c> both match as a substring.</summary>
    internal static void EnsureTestCertificate(string name)
    {
        // Present, as it is after the first deploy on a machine: no lock.
        if (HasTestCertificate(name)) return;
        // A deploy and a WinUSB package add call this with the lock held,
        // and a mutex its owner takes again does not wait.
        using var deploy = DriverDeploy.Acquire();
        try
        {
            if (!HasTestCertificate(name)) CreateTestCertificate(name);
        }
        finally { deploy.ReleaseMutex(); }
    }

    private static bool HasTestCertificate(string name)
    {
        using var lmMy = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        lmMy.Open(OpenFlags.ReadOnly);
        return lmMy.Certificates.Find(X509FindType.FindBySubjectName, name, false).Count > 0;
    }

    private static void CreateTestCertificate(string name)
    {
        // Generate fresh self-signed cert with code-signing EKU.
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, critical: false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, critical: false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(10);
        using var cert = req.CreateSelfSigned(notBefore, notAfter);
        cert.FriendlyName = name;

        // Re-import with PersistKeySet so the private key lives in the machine
        // key store (signtool needs it from LocalMachine\My).
        byte[] pfx = cert.Export(X509ContentType.Pfx, "");
        using var persistableCert = X509CertificateLoader.LoadPkcs12(pfx, "",
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
        persistableCert.FriendlyName = name;

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
    /// the test certificate from LocalMachine\My. Holds the machine-wide
    /// deploy lock, since the package directory is the one every program of
    /// this build deploys from. Until v2.0.0 it took no lock, and a later
    /// call reused the package one call before had staged.
    ///
    /// <para><see cref="GenerateCatalogs"/> and a deploy later in the same
    /// process catalog and install the binaries this call signed, as long
    /// as no other program has staged the package since.</para></summary>
    /// <returns>Always true. A failure throws.</returns>
    /// <exception cref="UnauthorizedAccessException">The process is not
    /// elevated. The deploy lock lives in the namespace only an elevated
    /// process can open (<see cref="AdminNamespace"/>).</exception>
    /// <exception cref="InvalidOperationException">Another program held the
    /// deploy lock for ten minutes, or signtool failed.</exception>
    public static bool SignDrivers()
    {
        using var mutex = DriverDeploy.Acquire();
        try
        {
            string tools = EnsureExtracted();
            string package = StagePackage();
            using var pin = PinForUse(tools, package, driverPackage: true, signed: null);
            SignDriversIn(tools, package, pin);
        }
        finally { mutex.ReleaseMutex(); }
        return true;
    }

    /// <summary>Sign the package's two binaries, then hold each under
    /// <paramref name="pin"/> and record the SHA-256 read through the hold,
    /// so what the catalog and pnputil read next is what signtool
    /// wrote.</summary>
    private static void SignDriversIn(string tools, string package, ProtectedStaging.Pinned pin)
    {
        EnsureTestCertificate();
        string signtool = Path.Combine(tools, "signtool.exe");
        s_signedPackage = null;
        var signed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
            signed[dll] = pin.HoldAndHash(path);
        }
        s_signedPackage = signed;
    }

    // The signed binaries the last signing in this process left in the
    // package directory, by SHA-256. Written and read only under the deploy
    // lock, which serializes threads as well as programs.
    private static Dictionary<string, string>? s_signedPackage;

    /// <summary>This build's package: the one the last signing in this
    /// process left, when its binaries still carry the bytes that signing
    /// wrote (<paramref name="signed"/> names them and their hashes, which
    /// <see cref="PinForUse"/> checks again under the pin), or one staged
    /// fresh, to be signed under the pin (null). Its INFs are restaged from
    /// the embedded bytes and its catalogs removed either way. Runs no tool.
    /// The caller holds the deploy lock.</summary>
    private static string PackageLocked(out IReadOnlyDictionary<string, string>? signed)
    {
        string dir = ProtectedStaging.DirFor(ProtectedStaging.OperationPackage);
        signed = s_signedPackage is { Count: > 0 } last
            && last.All(kv => ProtectedStaging.Matches(Path.Combine(dir, kv.Key), kv.Value))
            ? last : null;
        if (signed != null)
        {
            foreach (string cat in Directory.GetFiles(dir, "*.cat"))
                File.Delete(cat);
            var asm = typeof(DriverBuilder).Assembly;
            foreach (string inf in PackageInfs)
                ProtectedStaging.StageResource(dir, inf, asm, NativePrefix + inf);
            return dir;
        }
        return StagePackage();
    }

    /// <summary>Pin the tools directory and <paramref name="package"/>, and
    /// every directory above them up to the root
    /// (<see cref="ProtectedStaging.Pin"/>), then hold every staged tool
    /// file, the INFs of this build's own package, and the binaries an
    /// earlier signing left (<paramref name="signed"/>), each checked
    /// through its hold against the embedded bytes or the hash the signing
    /// recorded (<see cref="ProtectedStaging.Pinned.Hold"/>). A mismatch here
    /// is a change made since they were staged or signed, and nothing
    /// runs.</summary>
    private static ProtectedStaging.Pinned PinForUse(string tools, string package, bool driverPackage,
        IReadOnlyDictionary<string, string>? signed)
    {
        var pin = ProtectedStaging.Pin(tools, package);
        try
        {
            var asm = typeof(DriverBuilder).Assembly;
            foreach (var (file, logical) in AllPayload())
                pin.Hold(Path.Combine(tools, file), ProtectedStaging.EmbeddedSha256(asm, logical));
            if (driverPackage)
                foreach (string inf in PackageInfs)
                    pin.Hold(Path.Combine(package, inf), ProtectedStaging.EmbeddedSha256(asm, NativePrefix + inf));
            if (signed != null)
                foreach (var (dll, sha) in signed)
                    pin.Hold(Path.Combine(package, dll), sha);
            return pin;
        }
        catch
        {
            pin.Dispose();
            throw;
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
    /// signs each resulting .cat file with the test certificate. Under the
    /// deploy lock, like <see cref="SignDrivers"/>. It catalogs the binaries
    /// <see cref="SignDrivers"/> signed in this process when they are still
    /// in place, and otherwise stages and signs the package first, so the
    /// catalogs always hash signed binaries. Until v2.0.0 it cataloged
    /// whatever package an earlier call in the process had staged, signed or
    /// not.</summary>
    /// <returns>Always true. A failure throws.</returns>
    /// <exception cref="UnauthorizedAccessException">The process is not
    /// elevated (see <see cref="SignDrivers"/>).</exception>
    /// <exception cref="InvalidOperationException">Another program held the
    /// deploy lock for ten minutes, or inf2cat or signtool failed.</exception>
    public static bool GenerateCatalogs()
    {
        using var mutex = DriverDeploy.Acquire();
        try
        {
            string tools = EnsureExtracted();
            string package = PackageLocked(out var signed);
            using var pin = PinForUse(tools, package, driverPackage: true, signed);
            if (signed == null) SignDriversIn(tools, package, pin);
            GenerateCatalogsIn(tools, package, pin);
        }
        finally { mutex.ReleaseMutex(); }
        return true;
    }

    /// <summary>Catalog the package and sign each catalog, then hold each
    /// one under <paramref name="pin"/> for pnputil's add.</summary>
    private static void GenerateCatalogsIn(string tools, string package, ProtectedStaging.Pinned pin)
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
            pin.HoldAndHash(cat);
        }
    }

    /// <summary>Catalog and sign an INF-only package staged in its own
    /// directory, with the same Inf2Cat, signtool and test certificate the
    /// driver package uses, such as a profile's WinUSB binding. Inf2Cat
    /// catalogs every INF in the directory it is given, which is why the
    /// package has a directory of its own. Each signed catalog is held under
    /// <paramref name="holder"/>, the caller's pin over the package, until
    /// pnputil has added it.</summary>
    internal static void CatalogAndSignPackage(string packageDir, ProtectedStaging.Pinned holder)
    {
        EnsureTestCertificate();
        // Checks every staged tool file, libraries and manifests included,
        // and again under the pin.
        string tools = EnsureExtracted();
        using var pin = PinForUse(tools, packageDir, driverPackage: false, signed: null);
        string inf2cat = Path.Combine(tools, "Inf2Cat.exe");
        string signtool = Path.Combine(tools, "signtool.exe");

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
            holder.HoldAndHash(cat);
        }
    }

    /// <summary>Installs the driver through the machine-wide install policy,
    /// the same call as <see cref="FullDeploy"/> and
    /// <c>HMContext.InstallDriver</c>: under the deploy lock, never a
    /// downgrade, and over an older driver another program's controllers
    /// run on, this SDK's packages added beside it with no device updated
    /// and nothing removed. Until v2.0.0 it added whatever package an earlier call
    /// had staged, with no lock and no policy. The package it installs is
    /// the one <see cref="SignDrivers"/> signed in this process when that is
    /// still in place.</summary>
    /// <returns>True when this call installed or upgraded the driver. False
    /// when the policy kept the installed one, the same build
    /// (<see cref="HMDriverInstallOutcome.AlreadyCurrent"/>) or a newer one
    /// (<see cref="HMDriverInstallOutcome.KeptNewer"/>), and installed
    /// nothing. Until v2.0.0 it returned true after every install.</returns>
    /// <exception cref="UnauthorizedAccessException">A deploy was needed and
    /// the process is not elevated (see <see cref="SignDrivers"/>). With
    /// this build's driver already installed it returns false without
    /// elevation.</exception>
    public static bool InstallDrivers()
    {
        var outcome = DriverDeploy.Run();
        return outcome is HMDriverInstallOutcome.Installed or HMDriverInstallOutcome.Upgraded;
    }

    private static void InstallDriversFrom(string dir)
    {
        string pnputil = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");

        // hidmaestro.inf is the main HID driver. hidmaestro_xusb.inf is the
        // XUSB companion (setup class System so Windows.Gaming.Input.dll does
        // not classify it as a second WGI Gamepad; xinput1_4 discovery scans
        // the {EC87F1E3} interface class directly so it still finds it).
        // /install reinstalls every present devnode the package matches,
        // another program's controllers and their HID children included, so
        // it runs under the PnP lock, after the deploy lock the caller holds.
        foreach (string inf in new[] { "hidmaestro.inf", "hidmaestro_xusb.inf" })
        {
            string path = Path.Combine(dir, inf);
            if (!File.Exists(path)) continue;
            using (DeviceManager.HoldPnpLock())
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
    /// failure mode that hid the CPU-saturation bug for hours.
    ///
    /// <para>Under the deploy lock, and only while no HIDMaestro device is
    /// present, no other program that embeds HIDMaestro runs and this one
    /// has no controller: the rule <c>RemoveAllVirtualControllers()</c>
    /// follows for the packages. Otherwise it removes nothing and throws
    /// <see cref="InvalidOperationException"/> naming what holds them. Until
    /// v2.0.0 it removed them unconditionally, from under running
    /// controllers.</para></summary>
    /// <exception cref="UnauthorizedAccessException">The process is not
    /// elevated (see <see cref="SignDrivers"/>). Until v2.0.0 such a call
    /// reached pnputil and threw <see cref="InvalidOperationException"/>
    /// from its check of the store.</exception>
    /// <exception cref="InvalidOperationException">The packages were kept,
    /// another program held the deploy lock for ten minutes, or a package
    /// is left in the store after the removal.</exception>
    public static void RemoveOldDriverPackages()
    {
        using var mutex = DriverDeploy.Acquire();
        try
        {
            if (!DriverDeploy.PackagesUnused(out string holders))
                throw new InvalidOperationException($"The HIDMaestro driver packages were kept: {holders}.");
            PnputilHelper.RemoveAllHidMaestroPackages();
        }
        finally { mutex.ReleaseMutex(); }
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
        // v2.0.0: every deploy goes through the machine-wide policy and its
        // lock (DriverDeploy): never a downgrade, and an older driver gives
        // way to this release's, installed outright when nothing runs on it
        // and added beside it while a program's controllers do.
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
        string package = PackageLocked(out var signed);
        using var pin = PinForUse(tools, package, driverPackage: true, signed);
        PnputilHelper.RemoveAllHidMaestroPackages();
        if (signed == null) SignDriversIn(tools, package, pin);
        GenerateCatalogsIn(tools, package, pin);
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
    /// Signed and cataloged as a full deploy does it. The caller holds the
    /// deploy lock.
    ///
    /// <para>Added without <c>/install</c>, which would "install/update
    /// drivers on any matching devices" (PnPUtil Command Syntax). Both
    /// callers can run while devices of running programs sit on another
    /// package of the same INF: the lone XUSB package when the main one is
    /// missing, and an older XUSB package at the installed driver's own
    /// stamp. A create binds its own devnode to the best package in the
    /// store, so nothing here needs a device updated. Only a full deploy,
    /// which runs when no device of any program is on the driver, adds
    /// with <c>/install</c>.</para></summary>
    internal static void AddPackagesLocked(IReadOnlyCollection<string> infs)
    {
        if (infs.Count == 0) return;
        string tools = EnsureExtracted();
        string package = PackageLocked(out var signed);
        using var pin = PinForUse(tools, package, driverPackage: true, signed);
        if (signed == null) SignDriversIn(tools, package, pin);
        GenerateCatalogsIn(tools, package, pin);
        string pnputil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");
        foreach (string inf in infs)
        {
            var embedded = DriverDeploy.EmbeddedStamp(inf);
            DeviceOrchestrator.LogDiag($"    deploy: adding the missing {inf} of {embedded}");
            Run(pnputil, $"/add-driver \"{Path.Combine(package, inf)}\"", timeoutMs: 30_000);
            if (!DriverDeploy.InstalledStamps(inf).Any(s => s.CompareTo(embedded) == 0))
                throw new InvalidOperationException($"pnputil /add-driver did not add {inf} {embedded} to the driver store.");
        }
        PnputilHelper.InvalidateInstalledCache();
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
    /// <exception cref="InvalidOperationException">The pnputil listing
    /// failed. The message names the failure.</exception>
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
