using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace HIDMaestro.Internal;

/// <summary>
/// Builds, signs, and installs HIDMaestro driver DLLs.
/// All operations run with CreateNoWindow — no visible popups.
/// </summary>
public static class DriverBuilder
{
    static readonly string RepoRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
    public static readonly string BuildDir = Path.Combine(RepoRoot, "build");
    static readonly string DriverDir = Path.Combine(RepoRoot, "driver");
    static readonly string IncludeDir = Path.Combine(RepoRoot, "include");

    static readonly string WdkRoot = @"C:\Program Files (x86)\Windows Kits\10";
    static readonly string WdkVer = "10.0.26100.0";
    static readonly string UmdfVer = "2.15";

    static string? _vcvarsPath;
    static string? FindVcvars()
    {
        if (_vcvarsPath != null) return _vcvarsPath;
        foreach (var vsDir in Directory.GetDirectories(@"C:\Program Files\Microsoft Visual Studio"))
        {
            foreach (var edition in Directory.GetDirectories(vsDir))
            {
                string path = Path.Combine(edition, "VC", "Auxiliary", "Build", "vcvarsall.bat");
                if (File.Exists(path)) { _vcvarsPath = path; return path; }
            }
        }
        return null;
    }

    static (int exitCode, string output) Run(string cmd, int timeoutMs = 60_000)
    {
        // Use a temp batch file to handle complex quoting (vcvarsall paths with spaces).
        // The batch file inherits the caller's elevation since UseShellExecute=false.
        string batFile = Path.Combine(Path.GetTempPath(), $"hm_{Guid.NewGuid():N}.cmd");
        File.WriteAllText(batFile, $"@echo off\r\n{cmd}\r\n");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = batFile,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(timeoutMs);
            return (proc.ExitCode, stdout + stderr);
        }
        finally
        {
            try { File.Delete(batFile); } catch { }
        }
    }

    /// <summary>Builds HIDMaestro.dll (main HID driver) from driver.c.</summary>
    public static bool BuildMainDriver()
    {
        string vcvars = FindVcvars() ?? throw new Exception("Visual Studio not found");
        Directory.CreateDirectory(BuildDir);

        string umInc = Path.Combine(WdkRoot, "Include", WdkVer, "um");
        string sharedInc = Path.Combine(WdkRoot, "Include", WdkVer, "shared");
        string kmInc = Path.Combine(WdkRoot, "Include", WdkVer, "km");
        string wdfInc = Path.Combine(WdkRoot, "Include", "wdf", "umdf", UmdfVer);
        string umLib = Path.Combine(WdkRoot, "Lib", WdkVer, "um", "x64");
        string wdfLib = Path.Combine(WdkRoot, "Lib", "wdf", "umdf", "x64", UmdfVer);

        string src = Path.Combine(DriverDir, "driver.c");
        string obj = Path.Combine(BuildDir, "main_driver.obj");
        string dll = Path.Combine(BuildDir, "HIDMaestro.dll");

        string compileCmd = $"\"{vcvars}\" amd64 >nul 2>&1 && cl.exe /nologo /W4 /GS /Gz /wd4324 /wd4101 " +
            $"/D _AMD64_ /D _WIN64 /D UNICODE /D _UNICODE /D UMDF_VERSION_MAJOR=2 /D UMDF_VERSION_MINOR=15 " +
            $"/D HIDMAESTRO_NOT_DEFINED_XYZZY " +
            $"\"/I{umInc}\" \"/I{sharedInc}\" \"/I{kmInc}\" \"/I{wdfInc}\" \"/I{IncludeDir}\" " +
            $"\"/Fo{obj}\" /c \"{src}\"";

        var (rc, output) = Run(compileCmd);
        if (rc != 0) { Console.WriteLine($"  Compile failed: {output}"); return false; }

        string linkCmd = $"\"{vcvars}\" amd64 >nul 2>&1 && link.exe /nologo /DLL \"/OUT:{dll}\" " +
            $"\"/LIBPATH:{umLib}\" \"/LIBPATH:{wdfLib}\" \"{obj}\" " +
            "WdfDriverStubUm.lib ntdll.lib OneCoreUAP.lib mincore.lib advapi32.lib";

        (rc, output) = Run(linkCmd);
        if (rc != 0) { Console.WriteLine($"  Link failed: {output}"); return false; }

        return true;
    }

    /// <summary>Builds HMXInput.dll (XUSB companion) from companion.c.</summary>
    public static bool BuildCompanion()
    {
        string vcvars = FindVcvars() ?? throw new Exception("Visual Studio not found");
        Directory.CreateDirectory(BuildDir);

        string umInc = Path.Combine(WdkRoot, "Include", WdkVer, "um");
        string sharedInc = Path.Combine(WdkRoot, "Include", WdkVer, "shared");
        string kmInc = Path.Combine(WdkRoot, "Include", WdkVer, "km");
        string wdfInc = Path.Combine(WdkRoot, "Include", "wdf", "umdf", UmdfVer);
        string umLib = Path.Combine(WdkRoot, "Lib", WdkVer, "um", "x64");
        string wdfLib = Path.Combine(WdkRoot, "Lib", "wdf", "umdf", "x64", UmdfVer);

        string src = Path.Combine(DriverDir, "companion.c");
        string obj = Path.Combine(BuildDir, "companion.obj");
        string dll = Path.Combine(BuildDir, "HMXInput.dll");

        string compileCmd = $"\"{vcvars}\" amd64 >nul 2>&1 && cl.exe /nologo /W4 /GS /Gz /wd4324 /wd4101 " +
            $"/D _AMD64_ /D _WIN64 /D UNICODE /D _UNICODE /D UMDF_VERSION_MAJOR=2 /D UMDF_VERSION_MINOR=15 " +
            $"\"/I{umInc}\" \"/I{sharedInc}\" \"/I{kmInc}\" \"/I{wdfInc}\" " +
            $"\"/Fo{obj}\" /c \"{src}\"";

        var (rc, output) = Run(compileCmd);
        if (rc != 0) { Console.WriteLine($"  Compile failed: {output}"); return false; }

        string linkCmd = $"\"{vcvars}\" amd64 >nul 2>&1 && link.exe /nologo /DLL \"/OUT:{dll}\" " +
            $"\"/LIBPATH:{umLib}\" \"/LIBPATH:{wdfLib}\" \"{obj}\" " +
            "WdfDriverStubUm.lib ntdll.lib OneCoreUAP.lib mincore.lib advapi32.lib";

        (rc, output) = Run(linkCmd);
        if (rc != 0) { Console.WriteLine($"  Link failed: {output}"); return false; }

        return true;
    }

    /// <summary>Ensures the test signing certificate exists and is trusted. Creates if missing.</summary>
    public static void EnsureTestCertificate()
    {
        using var myStore = new System.Security.Cryptography.X509Certificates.X509Store(
            System.Security.Cryptography.X509Certificates.StoreName.My,
            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
        myStore.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
        var existing = myStore.Certificates.Find(
            System.Security.Cryptography.X509Certificates.X509FindType.FindBySubjectName,
            "HIDMaestroTestCert", false);

        System.Security.Cryptography.X509Certificates.X509Certificate2? cert = null;

        if (existing.Count > 0)
        {
            cert = existing[0];
        }
        else
        {
            Console.Write("  Creating test certificate... ");
            // Create via PowerShell (only API for self-signed on older .NET)
            Run("powershell -NoProfile -Command \"" +
                "New-SelfSignedCertificate -Type Custom -Subject 'CN=HIDMaestroTestCert' " +
                "-FriendlyName 'HIDMaestro Test Signing' -CertStoreLocation 'Cert:\\CurrentUser\\My' " +
                "-KeyUsage DigitalSignature -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')\"");

            myStore.Close();
            using var myStore2 = new System.Security.Cryptography.X509Certificates.X509Store(
                System.Security.Cryptography.X509Certificates.StoreName.My,
                System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
            myStore2.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
            var created = myStore2.Certificates.Find(
                System.Security.Cryptography.X509Certificates.X509FindType.FindBySubjectName,
                "HIDMaestroTestCert", false);
            if (created.Count > 0) cert = created[0];
            Console.Write("OK ");
        }
        myStore.Close();

        if (cert == null) { Console.WriteLine("  CERT CREATION FAILED"); return; }

        // Trust the cert by exporting and importing via certutil (runs in-process, inherits elevation)
        string exportPath = Path.Combine(Path.GetTempPath(), "HIDMaestroTestCert.cer");
        try
        {
            File.WriteAllBytes(exportPath, cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert));
            // Use Process.Start directly (not Run/batch) to inherit elevation properly
            foreach (string store in new[] { "Root", "TrustedPublisher" })
            {
                // UseShellExecute=true so certutil inherits proper elevation
                var certutilPsi = new ProcessStartInfo
                {
                    FileName = "certutil.exe",
                    Arguments = $"-f -addstore {store} \"{exportPath}\"",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var p = Process.Start(certutilPsi)!;
                p.WaitForExit(10_000);
            }
        }
        finally
        {
            try { File.Delete(exportPath); } catch { }
        }
        Console.WriteLine("  Certificate ready.");
    }

    /// <summary>Signs DLLs with the test certificate from CurrentUser\My store.</summary>
    public static bool SignDrivers()
    {
        EnsureTestCertificate();

        string signtool = Path.Combine(WdkRoot, "bin", WdkVer, "x64", "signtool.exe");
        if (!File.Exists(signtool)) { Console.WriteLine("  signtool not found"); return false; }

        // Sign from CurrentUser\My store (where the private key lives)
        foreach (string dll in new[] { "HIDMaestro.dll", "HMXInput.dll" })
        {
            string path = Path.Combine(BuildDir, dll);
            if (!File.Exists(path)) continue;
            var (rc, output) = Run($"\"{signtool}\" sign /a /s My /n HIDMaestroTestCert /fd SHA256 \"{path}\"");
            if (rc != 0) { Console.WriteLine($"  Sign failed for {dll}: {output}"); return false; }
        }
        return true;
    }

    /// <summary>Generates catalog files and signs them.</summary>
    public static bool GenerateCatalogs()
    {
        string inf2cat = Path.Combine(WdkRoot, "bin", WdkVer, "x86", "inf2cat.exe");
        string signtool = Path.Combine(WdkRoot, "bin", WdkVer, "x64", "signtool.exe");

        // Copy INFs to build dir
        foreach (string inf in new[] { "hidmaestro.inf", "hidmaestro_gamepad.inf", "hidmaestro_xusb.inf" })
        {
            string src = Path.Combine(DriverDir, inf);
            string dst = Path.Combine(BuildDir, inf);
            if (File.Exists(src)) File.Copy(src, dst, true);
        }

        // Delete old catalogs
        foreach (string cat in Directory.GetFiles(BuildDir, "*.cat"))
            File.Delete(cat);

        // Generate catalogs
        Run($"\"{inf2cat}\" /driver:\"{BuildDir}\" /os:10_X64");

        // Sign catalogs
        foreach (string cat in Directory.GetFiles(BuildDir, "*.cat"))
            Run($"\"{signtool}\" sign /a /s My /n HIDMaestroTestCert /fd SHA256 \"{cat}\"");

        return true;
    }

    /// <summary>Installs all driver packages via pnputil (silent, no popup).</summary>
    public static bool InstallDrivers()
    {
        foreach (string inf in new[] { "hidmaestro.inf", "hidmaestro_gamepad.inf", "hidmaestro_xusb.inf" })
        {
            string path = Path.Combine(BuildDir, inf);
            if (!File.Exists(path)) continue;
            var (rc, output) = Run($"pnputil /add-driver \"{path}\" /install", timeoutMs: 15_000);
            if (rc != 0 || output.Contains("Access is denied") || output.Contains("Failed"))
            {
                Console.WriteLine($"\n    pnputil failed for {inf}: {output.Trim()}");
                return false;
            }
        }
        return true;
    }

    /// <summary>Removes all HIDMaestro driver packages from the driver store.</summary>
    public static void RemoveOldDriverPackages()
    {
        var (_, output) = Run("pnputil /enum-drivers");
        string? oem = null;
        foreach (string line in output.Split('\n'))
        {
            if (line.Contains("Published Name:") && line.Contains("oem"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"(oem\d+\.inf)");
                if (match.Success) oem = match.Groups[1].Value;
            }
            if (oem != null && line.Contains("hidmaestro", StringComparison.OrdinalIgnoreCase))
            {
                Run($"pnputil /delete-driver {oem} /force");
                oem = null;
            }
            if (string.IsNullOrWhiteSpace(line)) oem = null;
        }
    }

    /// <summary>Full build + sign + install pipeline. Returns true on success.</summary>
    public static bool FullDeploy(bool rebuild = true)
    {
        if (rebuild)
        {
            Console.Write("  Building main driver... ");
            if (!BuildMainDriver()) return false;
            Console.WriteLine("OK");

            Console.Write("  Building companion... ");
            if (!BuildCompanion()) return false;
            Console.WriteLine("OK");
        }

        Console.Write("  Removing old packages... ");
        RemoveOldDriverPackages();
        Console.WriteLine("OK");

        Console.Write("  Signing... ");
        if (!SignDrivers()) return false;
        Console.WriteLine("OK");

        Console.Write("  Generating catalogs... ");
        GenerateCatalogs();
        Console.WriteLine("OK");

        Console.Write("  Installing drivers... ");
        if (!InstallDrivers()) return false;
        Console.WriteLine("OK");

        return true;
    }

    /// <summary>Checks if ALL required HIDMaestro drivers are in the store.</summary>
    public static bool IsDriverInstalled()
    {
        var (_, output) = Run("pnputil /enum-drivers");
        return output.Contains("hidmaestro.inf", StringComparison.OrdinalIgnoreCase)
            && output.Contains("hidmaestro_xusb.inf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks if source files are newer than built DLLs.</summary>
    public static bool NeedsBuild()
    {
        string mainDll = Path.Combine(BuildDir, "HIDMaestro.dll");
        string companionDll = Path.Combine(BuildDir, "HMXInput.dll");
        if (!File.Exists(mainDll) || !File.Exists(companionDll)) return true;

        // Check driver.c/driver.h against HIDMaestro.dll
        var mainTime = File.GetLastWriteTime(mainDll);
        foreach (string src in new[] { "driver.c", "driver.h" })
        {
            string path = Path.Combine(DriverDir, src);
            if (File.Exists(path) && File.GetLastWriteTime(path) > mainTime)
                return true;
        }
        // Check companion.c against HMXInput.dll
        var companionTime = File.GetLastWriteTime(companionDll);
        string companionSrc = Path.Combine(DriverDir, "companion.c");
        if (File.Exists(companionSrc) && File.GetLastWriteTime(companionSrc) > companionTime)
            return true;

        return false;
    }
}
