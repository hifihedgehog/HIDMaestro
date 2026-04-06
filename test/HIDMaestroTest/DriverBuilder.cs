using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace HIDMaestroTest;

/// <summary>
/// Builds, signs, and installs HIDMaestro driver DLLs.
/// All operations run with CreateNoWindow — no visible popups.
/// </summary>
public static class DriverBuilder
{
    static readonly string RepoRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
    static readonly string BuildDir = Path.Combine(RepoRoot, "build");
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
        // Write command to a temp batch file to avoid quoting issues with cmd /c
        string batFile = Path.Combine(Path.GetTempPath(), $"hm_build_{Guid.NewGuid():N}.cmd");
        File.WriteAllText(batFile, $"@echo off\r\n{cmd}\r\n");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batFile}\"",
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

    /// <summary>Builds HMXInput.dll (XUSB companion) from driver.c with XUSB_MODE.</summary>
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

        string src = Path.Combine(DriverDir, "driver.c");
        string obj = Path.Combine(BuildDir, "xusb_driver.obj");
        string dll = Path.Combine(BuildDir, "HMXInput.dll");

        string compileCmd = $"\"{vcvars}\" amd64 >nul 2>&1 && cl.exe /nologo /W4 /GS /Gz /wd4324 /wd4101 " +
            $"/D _AMD64_ /D _WIN64 /D UNICODE /D _UNICODE /D UMDF_VERSION_MAJOR=2 /D UMDF_VERSION_MINOR=15 " +
            $"/D HIDMAESTRO_XUSB_MODE " +
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

    /// <summary>Signs DLLs with the test certificate.</summary>
    public static bool SignDrivers()
    {
        string signtool = Path.Combine(WdkRoot, "bin", WdkVer, "x64", "signtool.exe");
        if (!File.Exists(signtool)) { Console.WriteLine("  signtool not found"); return false; }

        foreach (string dll in new[] { "HIDMaestro.dll", "HMXInput.dll" })
        {
            string path = Path.Combine(BuildDir, dll);
            if (!File.Exists(path)) continue;
            var (rc, _) = Run($"\"{signtool}\" sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 \"{path}\"");
            if (rc != 0) return false;
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
            Run($"\"{signtool}\" sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 \"{cat}\"");

        return true;
    }

    /// <summary>Installs all driver packages via pnputil (silent, no popup).</summary>
    public static bool InstallDrivers()
    {
        foreach (string inf in new[] { "hidmaestro.inf", "hidmaestro_gamepad.inf", "hidmaestro_xusb.inf" })
        {
            string path = Path.Combine(BuildDir, inf);
            if (!File.Exists(path)) continue;
            Run($"pnputil /add-driver \"{path}\" /install", timeoutMs: 15_000);
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

        Console.Write("  Signing... ");
        if (!SignDrivers()) return false;
        Console.WriteLine("OK");

        Console.Write("  Generating catalogs... ");
        GenerateCatalogs();
        Console.WriteLine("OK");

        Console.Write("  Removing old packages... ");
        RemoveOldDriverPackages();
        Console.WriteLine("OK");

        Console.Write("  Installing drivers... ");
        if (!InstallDrivers()) return false;
        Console.WriteLine("OK");

        return true;
    }

    /// <summary>Checks if any HIDMaestro driver is in the driver store.</summary>
    public static bool IsDriverInstalled()
    {
        var (_, output) = Run("pnputil /enum-drivers");
        return output.Contains("hidmaestro", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks if source files are newer than built DLLs.</summary>
    public static bool NeedsBuild()
    {
        string dll = Path.Combine(BuildDir, "HIDMaestro.dll");
        if (!File.Exists(dll)) return true;

        var dllTime = File.GetLastWriteTime(dll);
        foreach (string src in new[] { "driver.c", "driver.h" })
        {
            string path = Path.Combine(DriverDir, src);
            if (File.Exists(path) && File.GetLastWriteTime(path) > dllTime)
                return true;
        }
        return false;
    }
}
