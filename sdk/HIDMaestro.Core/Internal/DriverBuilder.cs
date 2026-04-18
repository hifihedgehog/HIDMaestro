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
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(timeoutMs);
        return (proc.ExitCode, stdout + stderr);
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
        "HMXusbShim.dll",
        "hidmaestro.inf",
        "hidmaestro_xusb.inf",
        "hidmaestro_xusbshim_class.inf",

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

    /// <summary>Lazily extracts the embedded payload to %TEMP%\HIDMaestro_*\
    /// and returns the directory path. Idempotent — subsequent calls return
    /// the same directory without re-extracting.</summary>
    public static string EnsureExtracted()
    {
        if (StagingDir != null && Directory.Exists(StagingDir))
            return StagingDir;

        string dir = Path.Combine(Path.GetTempPath(), $"HIDMaestro_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var asm = typeof(DriverBuilder).Assembly;
        // Build a case-insensitive lookup of available resource names.
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

        foreach (string dll in new[] { "HIDMaestro.dll", "HMXInput.dll", "HMXusbShim.dll" })
        {
            string path = Path.Combine(dir, dll);
            if (!File.Exists(path)) continue;
            var (rc, output) = Run(signtool,
                $"sign /a /sm /s My /n {CertFriendlyName} /fd SHA256 \"{path}\"",
                workingDir: dir);
            if (rc != 0)
            {
                Console.WriteLine($"  Sign failed for {dll}: {output}");
                return false;
            }
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
        {
            Console.WriteLine($"  inf2cat failed: {output}");
            return false;
        }

        foreach (string cat in Directory.GetFiles(dir, "*.cat"))
        {
            var (rc2, output2) = Run(signtool,
                $"sign /a /sm /s My /n {CertFriendlyName} /fd SHA256 \"{cat}\"",
                workingDir: dir);
            if (rc2 != 0)
            {
                Console.WriteLine($"  Catalog sign failed for {cat}: {output2}");
                return false;
            }
        }
        return true;
    }

    /// <summary>Installs the staged INFs via pnputil (Windows builtin).</summary>
    public static bool InstallDrivers()
    {
        string dir = EnsureExtracted();
        string pnputil = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");

        foreach (string inf in new[] { "hidmaestro.inf", "hidmaestro_xusb.inf", "hidmaestro_xusbshim_class.inf" })
        {
            string path = Path.Combine(dir, inf);
            if (!File.Exists(path)) continue;
            var (rc, output) = Run(pnputil, $"/add-driver \"{path}\" /install",
                timeoutMs: 30_000);

            // xusbshim is experimental — log its outcome verbosely regardless
            // of whether pnputil marks the install as successful, so we can
            // tell at a glance if the Extension INF applied to matching
            // HID children or silently fell through.
            if (inf == "hidmaestro_xusbshim_class.inf")
            {
                Console.WriteLine($"    [xusbshim] pnputil rc={rc}");
                foreach (var line in output.Split('\n'))
                {
                    var trimmed = line.TrimEnd();
                    if (trimmed.Length > 0)
                        Console.WriteLine($"    [xusbshim] {trimmed}");
                }
            }

            if (rc != 0 || output.Contains("Access is denied") || output.Contains("Failed"))
            {
                // xusbshim failure is non-fatal — if the Extension INF doesn't
                // apply, the rest of the install flow still works and we just
                // lose the experimental filter. Log and continue.
                if (inf == "hidmaestro_xusbshim_class.inf")
                {
                    Console.WriteLine($"    [xusbshim] install reported failure but continuing " +
                                      $"(main driver + companion unaffected)");
                    continue;
                }
                Console.WriteLine($"\n    pnputil failed for {inf}: {output.Trim()}");
                return false;
            }
        }

        // Force PnP to rescan existing devices so newly-added INFs apply to
        // already-present matching devnodes. Without this, an Extension/filter
        // INF added AFTER the HID child enumerates won't auto-apply until the
        // device is removed and re-added. /scan-devices hits the whole system
        // but is cheap (fractions of a second on a clean machine).
        try
        {
            Run(pnputil, "/scan-devices", timeoutMs: 10_000);
        }
        catch { /* non-fatal */ }

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

    /// <summary>Full extract + sign + catalog + install pipeline. Returns
    /// true on success. The <paramref name="rebuild"/> parameter is retained
    /// for ABI compatibility but ignored — there is no source build step
    /// any more (the driver binaries ship pre-built inside the SDK DLL).</summary>
    public static bool FullDeploy(bool rebuild = true)
    {
        _ = rebuild; // intentionally unused

        Console.Write("  Extracting embedded driver payload... ");
        EnsureExtracted();
        Console.WriteLine("OK");

        Console.Write("  Removing old packages... ");
        RemoveOldDriverPackages();
        Console.WriteLine("OK");

        Console.Write("  Signing... ");
        if (!SignDrivers()) return false;
        Console.WriteLine("OK");

        Console.Write("  Generating catalogs... ");
        if (!GenerateCatalogs()) return false;
        Console.WriteLine("OK");

        Console.Write("  Installing drivers... ");
        if (!InstallDrivers()) return false;
        Console.WriteLine("OK");

        return true;
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
