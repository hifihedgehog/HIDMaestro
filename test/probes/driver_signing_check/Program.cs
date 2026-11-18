// Driver signing check: whether Windows on this machine installs
// HIDMaestro's driver package, which HIDMaestro signs on the machine, and
// what the SDK does where Windows installs only packages Microsoft signed.
//
//   table     No elevation, no machine state. The decision over its inputs
//             (DriverSigningPolicy.Refuses): the options the three test
//             machines read on 2026-11-17, each of the four conditions
//             taken away in turn, and the status a consumer gets.
//   machine   No elevation. HMDriverSigning.Check() against the probe's own
//             read of SystemCodeIntegrityInformation, and against the
//             driver store: a machine that holds HIDMaestro's package reads
//             as one that installs it.
//   catalogs  No elevation. Windows' own answer (WinVerifyTrust) for
//             HIDMaestro's installed catalog and for a catalog of Windows
//             itself, without and with WTD_CODE_INTEGRITY_DRIVER_MODE.
//   import    Elevated. A catalog signed as HIDMaestro signs, with a
//             certificate of the probe's own that EnsureTestCertificate
//             puts in My, Root and TrustedPublisher and the probe removes.
//             Windows trusts it without the mode flag everywhere. With the
//             flag it refuses it exactly where it enforces WHQL signing,
//             and there ThrowIfImportRefused throws
//             HMDriverSigningException for a machine whose status says
//             Microsoft-signed only. Elsewhere it returns, so a failed
//             import is never blamed on signing where Windows accepts the
//             catalog.
//   refused   Elevated, and only on a machine that holds no HIDMaestro
//             package and whose Windows installs only Microsoft-signed
//             ones. A context is constructed, InstallDriver and a create
//             throw HMDriverSigningException, and the machine reads the
//             same after: no certificate, no staged file, no registry key,
//             and GameInputSvc's start type as it was.
//
// No argument runs every part. Exit 0 PASS, 1 FAIL.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using Microsoft.Win32;

internal static partial class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static void Note(string text) => Console.WriteLine($"  [NOTE] {text}");
    static void Skip(string text) => Console.WriteLine($"  [SKIP] {text}");

    static int Main(string[] args)
    {
        string part = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
        var parts = new (string Name, Action Run)[]
        {
            ("table", Table), ("machine", Machine), ("catalogs", Catalogs), ("import", Import), ("refused", Refused),
        };
        if (part != "all" && parts.All(p => p.Name != part))
        {
            Console.WriteLine("Usage: DriverSigningCheck.exe [table|machine|catalogs|import|refused]");
            return 1;
        }
        foreach (var (name, run) in parts)
        {
            if (part != "all" && part != name) continue;
            try { run(); }
            catch (Exception e) { Check($"{name}: the part ran to its end", false, $"{e.GetType().Name}: {e.Message}"); }
        }
        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    // ── table ────────────────────────────────────────────────────────────

    // The options the test machines read on 2026-11-17: the Surface (Windows
    // 11 ARM64 build 28000, Secure Boot on), the development laptop (x64
    // build 26200), and the MeLE (x64 build 19044) without memory integrity
    // and with it.
    const uint Surface = 0xF405, Laptop = 0x2001, Mele = 0x1, MeleMemoryIntegrity = 0x3401;

    static DriverSigningPolicy.Inputs Arm64(uint? options, bool audit = false) =>
        new(options, audit, DriverMode: true, Architecture.Arm64);

    static DriverSigningPolicy.Inputs X64(uint? options, bool lockedDown = false, bool audit = false) =>
        new(options, audit, DriverMode: lockedDown, Architecture.X64);

    static void Table()
    {
        Console.WriteLine();
        Console.WriteLine("=== The decision over its inputs ===");
        bool R(DriverSigningPolicy.Inputs i) => DriverSigningPolicy.Refuses(i);

        Check("the Surface's options on ARM64 refuse the package", R(Arm64(Surface)));
        Check("the laptop's options on x64 install it", !R(X64(Laptop)));
        Check("the MeLE's options on x64 install it", !R(X64(Mele)));
        Check("memory integrity on changes nothing: the MeLE's options with it install on x64", !R(X64(MeleMemoryIntegrity)));
        Check("and would install on ARM64, since WHQL enforcement is not among them", !R(Arm64(MeleMemoryIntegrity)));

        Check("x64 outside S mode installs with WHQL enforcement set: its installer does not check in driver mode", !R(X64(Surface)));
        Check("x64 in S mode with WHQL enforcement refuses", R(X64(Surface, lockedDown: true)));
        Check("ARM64 without WHQL enforcement installs", !R(Arm64(Surface & ~DriverSigningPolicy.WhqlEnforcement)));
        Check("a system integrity policy in audit mode installs", !R(Arm64(Surface, audit: true)));
        Check("code integrity off installs", !R(Arm64(Surface & ~DriverSigningPolicy.CodeIntegrityEnabled)));
        Check("the installer's other state, with the test-signing option set, is not the one that fails the import",
              !R(Arm64(Surface | DriverSigningPolicy.CodeIntegrityTestSign)));
        Check("options that could not be read decide nothing", !R(Arm64(null)) && !R(X64(null, lockedDown: true)));

        var refused = DriverSigningPolicy.StatusOf(Arm64(Surface));
        Check("the refusing status says Microsoft-signed, cannot install, WHQL enforcement, ARM64",
              refused.Requirement == HMDriverSigningRequirement.MicrosoftSigned && !refused.CanInstallDriver
              && refused.WhqlEnforcement && refused.OSArchitecture == Architecture.Arm64, refused.Requirement.ToString());
        Check("its explanation names ARM64 and Microsoft, and is what ToString gives",
              refused.Explanation.Contains("ARM64", StringComparison.Ordinal)
              && refused.Explanation.Contains("Microsoft", StringComparison.Ordinal)
              && refused.ToString() == refused.Explanation, refused.Explanation);

        var sMode = DriverSigningPolicy.StatusOf(X64(Surface, lockedDown: true));
        Check("the S mode status says so and not ARM64",
              !sMode.CanInstallDriver && sMode.Explanation.Contains("S mode", StringComparison.Ordinal)
              && !sMode.Explanation.Contains("ARM64", StringComparison.Ordinal), sMode.Explanation);

        var secureBootX64 = DriverSigningPolicy.StatusOf(X64(Surface));
        Check("x64 with WHQL enforcement reports the enforcement and still installs",
              secureBootX64.CanInstallDriver && secureBootX64.WhqlEnforcement
              && secureBootX64.Requirement == HMDriverSigningRequirement.TrustedPublisher);

        var plain = DriverSigningPolicy.StatusOf(X64(Mele));
        Check("the installing status says trusted publisher, can install, no WHQL enforcement",
              plain.Requirement == HMDriverSigningRequirement.TrustedPublisher && plain.CanInstallDriver && !plain.WhqlEnforcement,
              plain.Explanation);

        var e = new HMDriverSigningExceptionProbe(refused);
        Check("HMDriverSigningException is an InvalidOperationException that carries the status and its explanation",
              e.Exception is InvalidOperationException && e.Exception.Status == refused && e.Exception.Message == refused.Explanation);
    }

    /// <summary>The exception as the SDK throws it for a machine with no
    /// package: through the deploy policy on a machine of the probe's own.</summary>
    sealed class HMDriverSigningExceptionProbe
    {
        public readonly HMDriverSigningException Exception;

        public HMDriverSigningExceptionProbe(HMDriverSigningStatus status)
        {
            var real = DriverDeploy.Machine;
            DriverDeploy.Machine = new EmptyMachine(status);
            try
            {
                DriverDeploy.ThrowIfSigningStopsDeploy();
                throw new InvalidOperationException("ThrowIfSigningStopsDeploy returned on an empty machine that refuses");
            }
            catch (HMDriverSigningException e) { Exception = e; }
            finally { DriverDeploy.Machine = real; }
        }

        sealed class EmptyMachine(HMDriverSigningStatus status) : DriverDeploy.DeployMachine
        {
            internal override List<DriverDeploy.DriverStamp> Stamps(string infName) => new();
            internal override HMDriverSigningStatus Signing() => status;
        }
    }

    // ── machine ──────────────────────────────────────────────────────────

    static bool PackageInStore() => DriverBuilder.PackageInfs.Any(inf => DriverDeploy.InstalledStamps(inf).Count > 0);

    static void Machine()
    {
        Console.WriteLine();
        Console.WriteLine("=== This machine ===");
        uint? own = OwnCodeIntegrityOptions();
        var status = HMDriverSigning.Check();
        var inputs = DriverSigningPolicy.Read();
        Note($"{RuntimeInformation.OSArchitecture}, Windows {Environment.OSVersion.Version}, code integrity options "
           + $"{(own is uint o ? "0x" + o.ToString("X") : "unread")}, driver mode {inputs.DriverMode}, audit mode {inputs.AuditMode}");
        Note($"status: {status.Requirement}. {status.Explanation}");

        Check("the probe reads the code integrity options itself (positive control)", own != null && (own & 0x1) != 0,
              own is uint v ? $"0x{v:X}" : "the query failed");
        Check("the SDK read the same options", inputs.CodeIntegrityOptions == own);
        Check("the status reports WHQL enforcement as bit 0x4000 of them", status.WhqlEnforcement == (((own ?? 0) & 0x4000) != 0));
        Check("the status names this machine's architecture", status.OSArchitecture == RuntimeInformation.OSArchitecture);
        Check("the status is the decision over this machine's inputs",
              status.Requirement == DriverSigningPolicy.StatusOf(inputs).Requirement);
        Check("a second Check gives the same status", ReferenceEquals(HMDriverSigning.Check(), status));
        bool lockedDown = OwnLockdown();
        Check("the SDK counts the installer's driver-mode check in on ARM64, and on x64 only when Windows is locked down",
              inputs.DriverMode == (RuntimeInformation.OSArchitecture == Architecture.Arm64 || lockedDown),
              $"driver mode {inputs.DriverMode}, locked down {lockedDown}");

        if (PackageInStore())
            Check("the driver store holds HIDMaestro's package, and the status says Windows installs it",
                  status.CanInstallDriver, status.Requirement.ToString());
        else
            Note("the driver store holds no HIDMaestro package, so it says nothing about the status");
        Check("the deploy stops before it starts exactly when the status refuses and the store holds no package",
              DriverDeploy.SigningStopsDeploy() == (!status.CanInstallDriver && !PackageInStore()));
    }

    // ── catalogs ─────────────────────────────────────────────────────────

    static string? InstalledCatalog()
    {
        string repository = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
        try
        {
            return Directory.EnumerateDirectories(repository, "hidmaestro.inf_*")
                .Select(d => Path.Combine(d, "hidmaestro.cat"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static string? WindowsCatalog()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "CatRoot", "{F750E6C3-38EE-11D1-85E5-00C04FC295EE}");
        try { return Directory.EnumerateFiles(root, "Microsoft-Windows-*.cat").FirstOrDefault(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Whether wintrust.dll applies the WHQL requirement in driver
    /// mode here: WHQL enforcement and no audit mode.</summary>
    static bool DriverModeRequiresMicrosoft()
    {
        var i = DriverSigningPolicy.Read();
        return i.CodeIntegrityOptions is uint o && (o & DriverSigningPolicy.WhqlEnforcement) != 0 && !i.AuditMode;
    }

    static void Catalogs()
    {
        Console.WriteLine();
        Console.WriteLine("=== Windows' own answer for catalogs ===");
        string? windows = WindowsCatalog();
        if (windows == null) Check("a catalog of Windows itself is found (control)", false);
        else
        {
            int plain = DriverSigningPolicy.Verify(windows, driverMode: false), mode = DriverSigningPolicy.Verify(windows, driverMode: true);
            Check("Windows trusts a catalog of its own without the mode flag and with it (control)", plain == 0 && mode == 0,
                  $"0x{plain:X8}, 0x{mode:X8}, {Path.GetFileName(windows)}");
        }

        string? ours = InstalledCatalog();
        if (ours == null)
        {
            Skip("The driver store holds no HIDMaestro catalog.");
            return;
        }
        int p = DriverSigningPolicy.Verify(ours, driverMode: false), m = DriverSigningPolicy.Verify(ours, driverMode: true);
        Check("Windows trusts HIDMaestro's installed catalog without the mode flag", p == 0, $"0x{p:X8}");
        bool requires = DriverModeRequiresMicrosoft();
        Check("with the mode flag it refuses it exactly where it enforces WHQL signing", (m != 0) == requires,
              $"0x{m:X8}, WHQL requirement {requires}");
    }

    // ── import ───────────────────────────────────────────────────────────

    static void Import()
    {
        Console.WriteLine();
        Console.WriteLine("=== A catalog signed as HIDMaestro signs ===");
        if (!Environment.IsPrivilegedProcess)
        {
            Skip("This part adds a certificate to the machine's stores, which needs an elevated process.");
            return;
        }
        string name = "HMSigningProbe" + Guid.NewGuid().ToString("N")[..8];
        string work = Path.Combine(Path.GetTempPath(), "HIDMaestro_probe_signing_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // The certificate HIDMaestro makes, under another name.
            DriverBuilder.EnsureTestCertificate(name);
            string? thumbprint = Thumbprints(name, StoreName.My).FirstOrDefault();
            Check("EnsureTestCertificate put the probe's certificate in My, Root and TrustedPublisher (positive control)",
                  thumbprint != null && Thumbprints(name, StoreName.Root).Count == 1 && Thumbprints(name, StoreName.TrustedPublisher).Count == 1);
            if (thumbprint == null) return;

            string payload = Path.Combine(work, "payload");
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(payload, "probe.inf"), "; driver_signing_check\r\n");
            string catalog = Path.Combine(work, "probe.cat");
            var (exit, output) = PowerShell(
                $"New-FileCatalog -Path '{payload}' -CatalogFilePath '{catalog}' -CatalogVersion 2.0 | Out-Null; "
              + $"(Set-AuthenticodeSignature -FilePath '{catalog}' -Certificate (Get-Item 'Cert:\\LocalMachine\\My\\{thumbprint}') -HashAlgorithm SHA256).Status");
            Check("the catalog is made and signed with it (positive control)", exit == 0 && File.Exists(catalog) && output.Contains("Valid", StringComparison.Ordinal),
                  $"exit {exit}: {output.Trim()}");
            if (!File.Exists(catalog)) return;

            int plain = DriverSigningPolicy.Verify(catalog, driverMode: false), mode = DriverSigningPolicy.Verify(catalog, driverMode: true);
            bool requires = DriverModeRequiresMicrosoft();
            Check("Windows trusts it without the mode flag", plain == 0, $"0x{plain:X8}");
            Check("with the mode flag it refuses it exactly where it enforces WHQL signing", (mode != 0) == requires,
                  $"0x{mode:X8}, WHQL requirement {requires}");
            if (requires) Check("the refusal is 0x800B0109, the code a refused import carries", mode == unchecked((int)0x800B0109), $"0x{mode:X8}");

            var refusing = DriverSigningPolicy.StatusOf(Arm64(Surface));
            var installing = DriverSigningPolicy.StatusOf(X64(Mele));
            string Outcome(HMDriverSigningStatus status, string path)
            {
                try { DriverSigningPolicy.ThrowIfImportRefused(status, path); return "returned"; }
                catch (HMDriverSigningException e) { return "threw: " + e.Message; }
            }
            string blamed = Outcome(refusing, catalog);
            Check(requires
                    ? "for a status that says Microsoft-signed only, the failed import is named: HMDriverSigningException with Windows' code"
                    : "for a status that says Microsoft-signed only, the failed import is not blamed on signing, since Windows accepts the catalog",
                  requires
                    ? blamed.StartsWith("threw: " + refusing.Explanation, StringComparison.Ordinal) && blamed.Contains("0x800B0109", StringComparison.Ordinal)
                    : blamed == "returned",
                  blamed);
            Check("for a status that says Windows installs the package, it returns", Outcome(installing, catalog) == "returned");
            Check("for a catalog that is not there, it returns", Outcome(refusing, Path.Combine(work, "none.cat")) == "returned");
        }
        finally
        {
            RemoveCertificate(name);
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
            int left = Thumbprints(name, StoreName.My).Count + Thumbprints(name, StoreName.Root).Count + Thumbprints(name, StoreName.TrustedPublisher).Count;
            Check("the probe's certificate and its folder are gone", left == 0 && !Directory.Exists(work), $"{left} certificate(s) left");
        }
    }

    static List<string> Thumbprints(string name, StoreName storeName)
    {
        var found = new List<string>();
        using var store = new X509Store(storeName, StoreLocation.LocalMachine);
        try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); }
        catch (CryptographicException) { return found; }
        foreach (var cert in store.Certificates)
            using (cert)
                if (string.Equals(cert.Subject, "CN=" + name, StringComparison.Ordinal)) found.Add(cert.Thumbprint);
        return found;
    }

    /// <summary>The copies first and the private key last, as the SDK's own
    /// removal takes HIDMaestro's certificate.</summary>
    static void RemoveCertificate(string name)
    {
        RSA? key = null;
        try
        {
            foreach (var storeName in new[] { StoreName.My, StoreName.Root, StoreName.TrustedPublisher })
            {
                using var store = new X509Store(storeName, StoreLocation.LocalMachine);
                try { store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly); }
                catch (CryptographicException) { continue; }
                foreach (var cert in store.Certificates)
                    using (cert)
                    {
                        if (!string.Equals(cert.Subject, "CN=" + name, StringComparison.Ordinal)) continue;
                        if (storeName == StoreName.My && cert.HasPrivateKey && key == null)
                            try { key = cert.GetRSAPrivateKey(); } catch { }
                        try { store.Remove(cert); } catch { }
                    }
            }
            switch (key)
            {
                case RSACng cng: cng.Key.Delete(); break;
                case RSACryptoServiceProvider csp: csp.PersistKeyInCsp = false; csp.Clear(); break;
            }
        }
        catch { }
        finally { key?.Dispose(); }
    }

    static (int Exit, string Output) PowerShell(string command)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (-1, "powershell did not end within 120 s");
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    // ── refused ──────────────────────────────────────────────────────────

    static readonly string[] Keys =
    {
        @"SOFTWARE\HIDMaestro", @"SOFTWARE\HIDMaestroOwners", @"SOFTWARE\HIDMaestroRegistryLedger",
        @"SOFTWARE\HIDMaestroPackagesLeft", @"SOFTWARE\HIDMaestroOemOverrides", @"SOFTWARE\HIDMaestroGameInputSvc",
        @"SOFTWARE\HIDMaestroUsbip",
    };

    /// <summary>What a deploy would add to a machine, and what the
    /// constructor's prewarm changed on one before 2.3.0.</summary>
    static SortedDictionary<string, string> Snapshot()
    {
        var s = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var storeName in new[] { StoreName.My, StoreName.Root, StoreName.TrustedPublisher })
        {
            int n = 0;
            using var store = new X509Store(storeName, StoreLocation.LocalMachine);
            try
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                foreach (var cert in store.Certificates)
                    using (cert)
                        if (cert.Subject.Contains("HIDMaestro", StringComparison.OrdinalIgnoreCase)) n++;
            }
            catch (CryptographicException) { }
            s[$"certificates in {storeName}"] = n.ToString();
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.CommonApplicationData })
        {
            string path = Path.Combine(Environment.GetFolderPath(folder), "HIDMaestro");
            s[path] = Directory.Exists(path) ? "present" : "absent";
        }
        foreach (string key in Keys)
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            s[@"HKLM\" + key] = k == null ? "absent" : "present";
        }
        using (var svc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\GameInputSvc"))
            s["GameInputSvc start type"] = svc?.GetValue("Start")?.ToString() ?? "no service";
        string repository = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
        s["HIDMaestro packages in the driver store"] = Directory.Exists(repository)
            ? Directory.EnumerateDirectories(repository, "hidmaestro*.inf_*").Count().ToString() : "unread";
        return s;
    }

    static void Refused()
    {
        Console.WriteLine();
        Console.WriteLine("=== A machine whose Windows installs only Microsoft-signed packages ===");
        if (!DriverDeploy.SigningStopsDeploy())
        {
            Skip(HMDriverSigning.Check().CanInstallDriver
                ? "Windows installs HIDMaestro's driver on this machine."
                : "The driver store holds a HIDMaestro package.");
            return;
        }
        if (!Environment.IsPrivilegedProcess)
        {
            Skip("InstallDriver and a create need an elevated process.");
            return;
        }
        var status = HMDriverSigning.Check();
        var before = Snapshot();
        Check("the machine holds nothing of HIDMaestro before (positive control)",
              before.All(kv => kv.Key == "GameInputSvc start type" || kv.Value is "0" or "absent"),
              string.Join("; ", before.Where(kv => kv.Key != "GameInputSvc start type" && kv.Value is not ("0" or "absent")).Select(kv => $"{kv.Key} {kv.Value}")));

        string install, create;
        using (var ctx = new HMContext())
        {
            ctx.LoadDefaultProfiles();
            // The constructor's prewarm runs on other threads. Before 2.3.0
            // it staged the payload and set GameInputSvc to start
            // automatically within this time.
            Thread.Sleep(4000);
            install = Thrown(() => ctx.InstallDriver(), status);
            create = Thrown(() => { using var c = ctx.CreateController(ctx.GetProfile("dualshock-4-v2")!, "driver-signing-check"); }, status);
            Check("IsDriverInstalled answers false", !ctx.IsDriverInstalled);
            Check("LastDriverInstall stays null", ctx.LastDriverInstall == null);
        }
        Check("InstallDriver throws HMDriverSigningException with this machine's status and explanation", install == "as expected", install);
        Check("the create of a profile that runs on the driver throws the same", create == "as expected", create);

        var after = Snapshot();
        var changed = before.Keys.Union(after.Keys)
            .Where(k => before.GetValueOrDefault(k) != after.GetValueOrDefault(k))
            .Select(k => $"{k}: {before.GetValueOrDefault(k) ?? "none"} -> {after.GetValueOrDefault(k) ?? "none"}").ToList();
        Check("the machine reads the same after: no certificate, staged file, registry key or service setting changed",
              changed.Count == 0, string.Join("; ", changed));
    }

    static string Thrown(Action act, HMDriverSigningStatus status)
    {
        try { act(); return "returned"; }
        catch (HMDriverSigningException e)
        {
            return ReferenceEquals(e.Status, status) && e.Message == status.Explanation
                ? "as expected" : $"HMDriverSigningException with {e.Status.Requirement}: {e.Message}";
        }
        catch (Exception e) { return $"{e.GetType().Name}: {e.Message}"; }
    }

    // The probe's own read, apart from the SDK's.
    static unsafe uint? OwnCodeIntegrityOptions()
    {
        uint* info = stackalloc uint[2];
        info[0] = 8;
        info[1] = 0;
        return NtQuerySystemInformation(103, info, 8, null) >= 0 ? info[1] : null;
    }

    static bool OwnLockdown()
    {
        try { return WldpQueryWindowsLockdownMode(out int mode) >= 0 && mode != 0; }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return false; }
    }

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQuerySystemInformation(int infoClass, void* buffer, uint length, uint* returnLength);

    [LibraryImport("wldp.dll")]
    private static partial int WldpQueryWindowsLockdownMode(out int lockdownMode);
}
