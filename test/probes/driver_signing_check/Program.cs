// Driver signing check: whether Windows on this machine installs
// HIDMaestro's driver package, which HIDMaestro signs on the machine, which
// backend a create resolves to because of it, and what an explicit request
// for the UMDF2 driver does where Windows refuses the package.
//
//   table     No elevation, no machine state. The decision over its inputs
//             (DriverSigningPolicy.Refuses): the code integrity options the
//             test machines read, the four states measured on the ARM64
//             Surface on 2026-11-18, each condition taken away in turn. The
//             machine rule (BackendSelection.MachineRule), the resolution of
//             a create over the profile's data and the request, the
//             process override, and the refusal of an explicit UMDF2 request
//             on a machine of the probe's own.
//   machine   No elevation. The SDK's verdict against the probe's own read of
//             SystemCodeIntegrityInformation, HMContext.MachineBackend
//             against the rule over this machine's inputs, and what
//             IsDriverInstalled answers for each backend.
//   catalogs  No elevation. Windows' own answer (WinVerifyTrust) for
//             HIDMaestro's installed catalog and for a catalog of Windows
//             itself, without and with WTD_CODE_INTEGRITY_DRIVER_MODE.
//   import    Elevated. A catalog signed as HIDMaestro signs, with a
//             certificate of the probe's own that EnsureTestCertificate
//             puts in My, Root and TrustedPublisher and the probe removes.
//             Windows trusts it without the mode flag everywhere, and with it
//             wherever the verdict says the package installs. Where it
//             refuses it with the flag, ThrowIfImportRefused names the
//             failed import for a refusing verdict, and never elsewhere.
//   refused   Elevated, and only on a machine that holds no HIDMaestro
//             package and whose Windows refuses one. A context built with
//             HMBackend.Umdf2 throws HMDriverSigningException from
//             InstallDriver and from a create, and so does a create that
//             asks for Umdf2 itself, with no certificate, driver package or
//             payload record written. A context with no backend named then
//             runs as a program written before 2.3.0 runs: InstallDriver
//             installs the USB/IP host controller, IsDriverInstalled is
//             true, and a create runs on the USB/IP backend and reads back
//             through HID. Creates a device: live, under the bench lock.
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

    // The options the test machines read: the Surface (Windows 11 ARM64
    // build 28000) as shipped, 0xF405, and with the developer mode for driver
    // signing set, with Secure Boot off or with integrity checks off in the
    // boot configuration, 0x3405 each (2026-11-18), the development laptop
    // (x64 build 26200), and the MeLE (x64 build 19044) without memory
    // integrity and with it (2026-11-17).
    const uint Surface = 0xF405, SurfaceRelaxed = 0x3405, Laptop = 0x2001, Mele = 0x1, MeleMemoryIntegrity = 0x3401;

    static DriverSigningPolicy.Inputs Arm64(uint? options) => new(options, DriverMode: true, Architecture.Arm64);

    static DriverSigningPolicy.Inputs X64(uint? options, bool lockedDown = false) => new(options, DriverMode: lockedDown, Architecture.X64);

    static void Table()
    {
        Console.WriteLine();
        Console.WriteLine("=== The decision over its inputs ===");
        bool R(DriverSigningPolicy.Inputs i) => DriverSigningPolicy.Refuses(i);

        Check("the Surface's shipped options on ARM64 refuse the package", R(Arm64(Surface)));
        Check("the Surface with the developer mode for driver signing set refuses it (0x3405, measured 2026-11-18)", R(Arm64(SurfaceRelaxed)));
        Check("the Surface with Secure Boot off refuses it (0x3405, measured 2026-11-18)", R(Arm64(SurfaceRelaxed)));
        Check("the Surface with integrity checks off in the boot configuration refuses it (0x3405, measured 2026-11-18)", R(Arm64(SurfaceRelaxed)));
        Check("ARM64 refuses with code integrity on and the test-signing option clear, whatever else the options hold",
              R(Arm64(DriverSigningPolicy.CodeIntegrityEnabled)) && R(Arm64(MeleMemoryIntegrity)));
        Check("the laptop's options on x64 install it", !R(X64(Laptop)));
        Check("the MeLE's options on x64 install it", !R(X64(Mele)));
        Check("memory integrity on changes nothing on x64: the MeLE's options with it install", !R(X64(MeleMemoryIntegrity)));
        Check("x64 outside S mode installs with the Surface's options: its installer does not check in driver mode", !R(X64(Surface)));
        Check("x64 in S mode refuses", R(X64(Surface, lockedDown: true)) && R(X64(Mele, lockedDown: true)));
        Check("code integrity off installs", !R(Arm64(Surface & ~DriverSigningPolicy.CodeIntegrityEnabled)));
        Check("the installer's other state, with the test-signing option set, is not the one that fails the import",
              !R(Arm64(Surface | DriverSigningPolicy.CodeIntegrityTestSign)));
        Check("options that could not be read decide nothing", !R(Arm64(null)) && !R(X64(null, lockedDown: true)));
        var verdict = DriverSigningPolicy.VerdictOf(Arm64(Surface));
        Check("the verdict carries the decision, the options and the architecture",
              verdict.Refuses && !verdict.CanInstallDriver && verdict.CodeIntegrityOptions == Surface && verdict.Architecture == Architecture.Arm64);

        Console.WriteLine();
        Console.WriteLine("=== What Auto resolves to on a machine ===");
        HMBackend M(bool driver, bool transport, bool stops) => BackendSelection.MachineRule(driver, transport, () => stops);
        Check("a build with both payloads on a machine that installs the driver: Umdf2", M(true, true, false) == HMBackend.Umdf2);
        Check("a build with both payloads on a machine that refuses it: Usbip", M(true, true, true) == HMBackend.Usbip);
        Check("a build without the driver for the machine: Usbip", M(false, true, false) == HMBackend.Usbip);
        Check("a build with neither: Umdf2, so the create names the missing driver", M(false, false, false) == HMBackend.Umdf2);
        Check("a build without the transport on a refusing machine: Umdf2, so the create names it", M(true, false, true) == HMBackend.Umdf2);
        bool asked = false;
        BackendSelection.MachineRule(false, true, () => { asked = true; return true; });
        Check("the signing question is asked only when the build carries the driver", !asked);

        Check("the override reads umdf2 and usbip, any case, and nothing else",
              BackendSelection.ParseOverride("umdf2") == HMBackend.Umdf2 && BackendSelection.ParseOverride(" USBIP ") == HMBackend.Usbip
              && BackendSelection.ParseOverride(null) == null && BackendSelection.ParseOverride("auto") == null
              && BackendSelection.ParseOverride("") == null);
        Note($"{BackendSelection.OverrideVariable} in this process: {Environment.GetEnvironmentVariable(BackendSelection.OverrideVariable) ?? "(not set)"}");

        Check("a create's own backend comes first, then the context's",
              BackendSelection.Requested(HMBackend.Usbip, HMBackend.Umdf2) == HMBackend.Usbip
              && BackendSelection.Requested(HMBackend.Auto, HMBackend.Umdf2) == HMBackend.Umdf2
              && BackendSelection.Requested(HMBackend.Auto, HMBackend.Auto) == HMBackend.Auto);

        Console.WriteLine();
        Console.WriteLine("=== A create's backend over the profile's data ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var plain = ctx.GetProfile("dualshock-4-v2")!.Inner;
        var composite = ctx.GetProfile("dualsense-composite")!.Inner;
        var x360 = ctx.GetProfile("xbox-360-wired")!.Inner;
        var xinputhid = ctx.GetProfile("xbox-series-xs-bt")!.Inner;
        string Resolve(ControllerProfile p, HMBackend requested, HMBackend machine)
        {
            try { return BackendSelection.ForProfile(p, requested, () => machine).ToString(); }
            catch (HMBackendNotSupportedException e) { return $"refused:{e.Backend}"; }
        }
        Check("Auto on a UMDF2 machine: a plain profile on Umdf2, a usbip profile on Usbip",
              Resolve(plain, HMBackend.Auto, HMBackend.Umdf2) == "Umdf2" && Resolve(composite, HMBackend.Auto, HMBackend.Umdf2) == "Usbip");
        Check("Auto on a USB/IP machine: every presentable profile on Usbip",
              Resolve(plain, HMBackend.Auto, HMBackend.Usbip) == "Usbip" && Resolve(composite, HMBackend.Auto, HMBackend.Usbip) == "Usbip");
        Check("an explicit backend wins over the machine",
              Resolve(plain, HMBackend.Usbip, HMBackend.Umdf2) == "Usbip" && Resolve(plain, HMBackend.Umdf2, HMBackend.Usbip) == "Umdf2");
        Check("Umdf2 for a usbip profile is refused", Resolve(composite, HMBackend.Umdf2, HMBackend.Umdf2) == "refused:Umdf2");
        Check("Usbip for an Xbox profile that reaches XInput through the companion is refused",
              Resolve(x360, HMBackend.Usbip, HMBackend.Umdf2) == "refused:Usbip"
              && Resolve(x360, HMBackend.Auto, HMBackend.Usbip) == "refused:Usbip");
        Check("an xinputhid profile resolves to Usbip, its GIP function, when asked and on a USB/IP machine",
              Resolve(xinputhid, HMBackend.Usbip, HMBackend.Umdf2) == "Usbip"
              && Resolve(xinputhid, HMBackend.Auto, HMBackend.Usbip) == "Usbip"
              && Resolve(xinputhid, HMBackend.Auto, HMBackend.Umdf2) == "Umdf2");
        Check("SupportsBackend gives the same answers without the exception",
              ctx.GetProfile("dualshock-4-v2")!.SupportsBackend(HMBackend.Usbip) && ctx.GetProfile("dualshock-4-v2")!.SupportsBackend(HMBackend.Umdf2)
              && !ctx.GetProfile("dualsense-composite")!.SupportsBackend(HMBackend.Umdf2)
              && !ctx.GetProfile("xbox-360-wired")!.SupportsBackend(HMBackend.Usbip)
              && ctx.GetProfile("xbox-360-wired")!.SupportsBackend(HMBackend.Auto));
        var refusal = Thrown(() => BackendSelection.ForProfile(composite, HMBackend.Umdf2, () => HMBackend.Umdf2));
        Check("the refusal is an InvalidOperationException naming the profile and the backend",
              refusal is HMBackendNotSupportedException { ProfileId: "dualsense-composite", Backend: HMBackend.Umdf2 } b
              && b.Message.Contains("dualsense-composite", StringComparison.Ordinal) && refusal is InvalidOperationException,
              refusal?.Message ?? "none");

        Console.WriteLine();
        Console.WriteLine("=== An explicit UMDF2 request where Windows refuses the package ===");
        var real = DriverDeploy.Machine;
        DriverDeploy.Machine = new EmptyMachine(DriverSigningPolicy.VerdictOf(Arm64(Surface)));
        try
        {
            var explicitUmdf2 = Thrown(() => BackendSelection.ThrowIfMachineRefuses(plain, HMBackend.Umdf2, explicitRequest: true));
            Check("an explicit Umdf2 request throws HMDriverSigningException", explicitUmdf2 is HMDriverSigningException, explicitUmdf2?.GetType().Name ?? "none");
            Check("a Usbip create throws nothing for signing", Thrown(() => BackendSelection.ThrowIfMachineRefuses(plain, HMBackend.Usbip, explicitRequest: true)) is not HMDriverSigningException);
            Check("the machine rule over this machine reads Usbip", BackendSelection.MachineRule(true, true, DriverDeploy.SigningStopsDeploy) == HMBackend.Usbip);
            if (explicitUmdf2 is HMDriverSigningException e)
            {
                Check("the exception is an InvalidOperationException with the neutral message",
                      e is InvalidOperationException && e.Message == HMDriverSigningException.DefaultMessage, e.Message);
                Check("the message names the backend that runs the controller", e.Message.Contains("HMBackend.Auto", StringComparison.Ordinal)
                      && e.Message.Contains("HMBackend.Usbip", StringComparison.Ordinal));
            }
        }
        finally { DriverDeploy.Machine = real; }

        Console.WriteLine();
        Console.WriteLine("=== The removed signing types ===");
        var types = typeof(HMContext).Assembly.GetExportedTypes().Select(t => t.Name).ToHashSet();
        Check("HMDriverSigning, HMDriverSigningStatus and HMDriverSigningRequirement are gone, HMDriverSigningException stays",
              !types.Contains("HMDriverSigning") && !types.Contains("HMDriverSigningStatus") && !types.Contains("HMDriverSigningRequirement")
              && types.Contains("HMDriverSigningException") && types.Contains("HMBackend") && types.Contains("HMBackendNotSupportedException"));
    }

    static Exception? Thrown(Action act)
    {
        try { act(); return null; }
        catch (Exception e) { return e; }
    }

    /// <summary>A machine of the probe's own: no package in the store, and
    /// the verdict given.</summary>
    sealed class EmptyMachine(DriverSigningPolicy.Verdict verdict) : DriverDeploy.DeployMachine
    {
        internal override List<DriverDeploy.DriverStamp> Stamps(string infName) => new();
        internal override DriverSigningPolicy.Verdict Signing() => verdict;
    }

    // ── machine ──────────────────────────────────────────────────────────

    static bool PackageInStore() => DriverBuilder.PackageInfs.Any(inf => DriverDeploy.InstalledStamps(inf).Count > 0);

    static void Machine()
    {
        Console.WriteLine();
        Console.WriteLine("=== This machine ===");
        uint? own = OwnCodeIntegrityOptions();
        var verdict = DriverSigningPolicy.Status;
        var inputs = DriverSigningPolicy.Read();
        Note($"{RuntimeInformation.OSArchitecture}, Windows {Environment.OSVersion.Version}, code integrity options "
           + $"{(own is uint o ? "0x" + o.ToString("X") : "unread")}, driver mode {inputs.DriverMode}");
        Note($"verdict: {(verdict.Refuses ? "Windows refuses HIDMaestro's package" : "Windows installs HIDMaestro's package")}, "
           + $"MachineBackend {HMContext.MachineBackend}");

        Check("the probe reads the code integrity options itself (positive control)", own != null && (own & 0x1) != 0,
              own is uint v ? $"0x{v:X}" : "the query failed");
        Check("the SDK read the same options", inputs.CodeIntegrityOptions == own);
        Check("the verdict names this machine's architecture", verdict.Architecture == RuntimeInformation.OSArchitecture);
        Check("the verdict is the decision over this machine's inputs", verdict.Refuses == DriverSigningPolicy.Refuses(inputs));
        bool lockedDown = OwnLockdown();
        Check("the SDK counts the installer's driver-mode check in on ARM64, and on x64 only when Windows is locked down",
              inputs.DriverMode == (RuntimeInformation.OSArchitecture == Architecture.Arm64 || lockedDown),
              $"driver mode {inputs.DriverMode}, locked down {lockedDown}");

        if (PackageInStore())
            Check("the driver store holds HIDMaestro's package, and the verdict says Windows installs it", verdict.CanInstallDriver);
        else
            Note("the driver store holds no HIDMaestro package, so it says nothing about the verdict");
        Check("the deploy stops before it starts exactly when the verdict refuses and the store holds no package",
              DriverDeploy.SigningStopsDeploy() == (verdict.Refuses && !PackageInStore()));

        var arch = RuntimeInformation.OSArchitecture;
        var expected = BackendSelection.Override ?? BackendSelection.MachineRule(
            HMBuildInfo.DriverArchitectures.Contains(arch), HMBuildInfo.UsbipArchitectures.Contains(arch), DriverDeploy.SigningStopsDeploy);
        Check("MachineBackend is the rule over this machine and this build", HMContext.MachineBackend == expected,
              $"{HMContext.MachineBackend}, the rule {expected}");
        Check("on a machine whose Windows refuses the package and holds none, MachineBackend is Usbip",
              !DriverDeploy.SigningStopsDeploy() || BackendSelection.Override != null || HMContext.MachineBackend == HMBackend.Usbip);

        using (var auto = new HMContext())
        using (var umdf2 = new HMContext(HMBackend.Umdf2))
        using (var usbip = new HMContext(HMBackend.Usbip))
        {
            Check("a context reports the backend it was given", auto.Backend == HMBackend.Auto && umdf2.Backend == HMBackend.Umdf2 && usbip.Backend == HMBackend.Usbip);
            Check("IsDriverInstalled on Usbip is whether the USB/IP host controller answers",
                  usbip.IsDriverInstalled == HMContext.IsUsbipBackendAvailable, $"{usbip.IsDriverInstalled}");
            Check("IsDriverInstalled on Auto answers for MachineBackend",
                  auto.IsDriverInstalled == (HMContext.MachineBackend == HMBackend.Usbip ? usbip.IsDriverInstalled : umdf2.IsDriverInstalled));
            try { _ = new HMContext((HMBackend)7); Check("a context refuses a backend that is not one of the three", false); }
            catch (ArgumentOutOfRangeException) { Check("a context refuses a backend that is not one of the three", true); }
        }
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
        if (DriverSigningPolicy.Status.CanInstallDriver)
            Check("where the verdict says the package installs, it trusts it with the mode flag too", m == 0, $"0x{m:X8}");
        else
            Note($"with the mode flag Windows answers 0x{m:X8} here, where the verdict says it refuses the package");
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
            Check("Windows trusts it without the mode flag", plain == 0, $"0x{plain:X8}");
            if (DriverSigningPolicy.Status.CanInstallDriver)
                Check("where the verdict says the package installs, it trusts it with the mode flag too", mode == 0, $"0x{mode:X8}");
            else
                Note($"with the mode flag Windows answers 0x{mode:X8} here, where the verdict says it refuses the package");

            var refusing = DriverSigningPolicy.VerdictOf(Arm64(Surface));
            var installing = DriverSigningPolicy.VerdictOf(X64(Mele));
            string Outcome(DriverSigningPolicy.Verdict verdict, string path)
            {
                try { DriverSigningPolicy.ThrowIfImportRefused(verdict, path); return "returned"; }
                catch (HMDriverSigningException e) { return "threw: " + e.Message; }
            }
            string blamed = Outcome(refusing, catalog);
            Check(mode != 0
                    ? "for a refusing verdict, the failed import is named: HMDriverSigningException with Windows' code"
                    : "for a refusing verdict, the failed import is not blamed on signing, since Windows accepts the catalog",
                  mode != 0
                    ? blamed.StartsWith("threw: " + HMDriverSigningException.DefaultMessage, StringComparison.Ordinal)
                      && blamed.Contains($"0x{mode:X8}", StringComparison.Ordinal)
                    : blamed == "returned",
                  blamed);
            Check("for an installing verdict, it returns", Outcome(installing, catalog) == "returned");
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

    /// <summary>What a deploy of the UMDF2 driver adds to a machine: its
    /// certificate, its packages and its payload record.</summary>
    static SortedDictionary<string, string> DriverSnapshot()
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
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestro"))
            s[@"HKLM\SOFTWARE\HIDMaestro"] = k == null ? "absent" : "present";
        string repository = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
        s["HIDMaestro packages in the driver store"] = Directory.Exists(repository)
            ? Directory.EnumerateDirectories(repository, "hidmaestro*.inf_*").Count().ToString() : "unread";
        return s;
    }

    static void Refused()
    {
        Console.WriteLine();
        Console.WriteLine("=== A machine whose Windows refuses HIDMaestro's package ===");
        if (!DriverDeploy.SigningStopsDeploy())
        {
            Skip(DriverSigningPolicy.Status.CanInstallDriver
                ? "Windows installs HIDMaestro's driver on this machine."
                : "The driver store holds a HIDMaestro package.");
            return;
        }
        if (!Environment.IsPrivilegedProcess)
        {
            Skip("InstallDriver and a create need an elevated process.");
            return;
        }
        if (BackendSelection.Override != null)
        {
            Skip($"{BackendSelection.OverrideVariable} is set, and this part reads the machine's own answer.");
            return;
        }
        var before = DriverSnapshot();
        Check("the machine holds nothing of HIDMaestro's driver before (positive control)",
              before.All(kv => kv.Value is "0" or "absent"),
              string.Join("; ", before.Where(kv => kv.Value is not ("0" or "absent")).Select(kv => $"{kv.Key} {kv.Value}")));
        Check("MachineBackend is Usbip", HMContext.MachineBackend == HMBackend.Usbip, HMContext.MachineBackend.ToString());

        // An explicit UMDF2 request: the context's, and a create's own.
        string install, create, createOwn;
        using (var ctx = new HMContext(HMBackend.Umdf2))
        {
            ctx.LoadDefaultProfiles();
            // The constructor's prewarm runs on other threads. A deploy
            // would have staged the payload within this time.
            Thread.Sleep(4000);
            install = Expect(() => ctx.InstallDriver());
            create = Expect(() => { using var c = ctx.CreateController(ctx.GetProfile("dualshock-4-v2")!, "driver-signing-check"); });
            Check("IsDriverInstalled on Umdf2 answers false", !ctx.IsDriverInstalled);
            Check("LastDriverInstall stays null", ctx.LastDriverInstall == null);
            Check("ResolveBackend throws the same for Umdf2", Expect(() => ctx.ResolveBackend(ctx.GetProfile("dualshock-4-v2")!)) == "as expected");
        }
        using (var ctx = new HMContext())
        {
            ctx.LoadDefaultProfiles();
            createOwn = Expect(() => { using var c = ctx.CreateController(ctx.GetProfile("dualshock-4-v2")!, "driver-signing-check", HMBackend.Umdf2); });
        }
        Check("InstallDriver on a Umdf2 context throws HMDriverSigningException with the neutral message", install == "as expected", install);
        Check("a create on that context throws the same", create == "as expected", create);
        Check("a create that asks for Umdf2 itself throws the same", createOwn == "as expected", createOwn);
        var afterRefusal = DriverSnapshot();
        var changed = Diff(before, afterRefusal);
        Check("no certificate, driver package or payload record was written", changed.Count == 0, string.Join("; ", changed));

        // A program written before 2.3.0: no backend named anywhere.
        using (var ctx = new HMContext())
        {
            ctx.LoadDefaultProfiles();
            var profile = ctx.GetProfile("dualshock-4-v2")!;
            Check("ResolveBackend answers Usbip for a plain profile", ctx.ResolveBackend(profile) == HMBackend.Usbip);
            var installed = Thrown(() => ctx.InstallDriver());
            Check("InstallDriver returns, installing the USB/IP host controller", installed == null,
                  installed == null ? $"{ctx.LastDriverInstall}" : $"{installed.GetType().Name}: {installed.Message}");
            Check("LastDriverInstall is Installed or AlreadyCurrent",
                  ctx.LastDriverInstall is HMDriverInstallOutcome.Installed or HMDriverInstallOutcome.AlreadyCurrent, $"{ctx.LastDriverInstall}");
            Check("IsDriverInstalled answers true", ctx.IsDriverInstalled);
            HMController? c = null;
            var created = Thrown(() => c = ctx.CreateController(profile, "driver-signing-check"));
            Check("a create returns", created == null, created == null ? "" : $"{created.GetType().Name}: {created.Message}");
            if (c != null)
            {
                Check("the controller runs on the USB/IP backend", c.Backend == HMBackend.Usbip, c.Backend.ToString());
                Check("it carries no audio and reports no driver version", c.UsbAudio == null && c.DriverVersion == null);
                string? path = FindHid(profile.VendorId, profile.ProductId);
                Check("Windows enumerated its HID interface with the profile's VID and PID", path != null, path ?? "none");
                c.Dispose();
            }
        }
        var after = DriverSnapshot();
        changed = Diff(before, after);
        Check("still no certificate, driver package or payload record of HIDMaestro's driver", changed.Count == 0, string.Join("; ", changed));
    }

    static List<string> Diff(SortedDictionary<string, string> before, SortedDictionary<string, string> after) =>
        before.Keys.Union(after.Keys)
            .Where(k => before.GetValueOrDefault(k) != after.GetValueOrDefault(k))
            .Select(k => $"{k}: {before.GetValueOrDefault(k) ?? "none"} -> {after.GetValueOrDefault(k) ?? "none"}").ToList();

    static string Expect(Action act)
    {
        try { act(); return "returned"; }
        catch (HMDriverSigningException e)
        {
            return e.Message == HMDriverSigningException.DefaultMessage ? "as expected" : $"HMDriverSigningException: {e.Message}";
        }
        catch (Exception e) { return $"{e.GetType().Name}: {e.Message}"; }
    }

    /// <summary>The interface path of a present HID device with the VID and
    /// PID, from the HID class's own interface list.</summary>
    static string? FindHid(ushort vid, ushort pid)
    {
        string want = $"vid_{vid:x4}&pid_{pid:x4}";
        var guid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
        if (CM_Get_Device_Interface_List_SizeW(out uint size, ref guid, null, 0) != 0 || size <= 1) return null;
        var buffer = new char[size];
        if (CM_Get_Device_Interface_ListW(ref guid, null, buffer, size, 0) != 0) return null;
        foreach (string path in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            if (path.Contains(want, StringComparison.OrdinalIgnoreCase)) return path;
        return null;
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_List_SizeW(out uint size, ref Guid classGuid, string? deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_ListW(ref Guid classGuid, string? deviceId, [Out] char[] buffer, uint length, uint flags);

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
