using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>
/// Extracts the embedded OpenVR driver (HIDMaestro.VR.* resources) to a
/// stable path and registers it with SteamVR via vrpathreg.exe.
///
/// <para><b>Stable extract path, not the hash-keyed staging dir:</b>
/// <see cref="DriverBuilder.EnsureExtracted"/>'s %TEMP%\HIDMaestro_&lt;hash&gt;
/// moves on every core bump by design, which would strand the vrpathreg
/// registration (openvrpaths stores an absolute path, and vrpathreg
/// removedriver is documented to leave stale entries). Extracting to
/// %ProgramData%\HIDMaestro\openvr\hidmaestro means re-running adddriver
/// is idempotent and no removedriver migration is ever needed.</para>
///
/// <para><b>Own registry gate, own install step:</b> the VR payload hash
/// lives at HKLM\SOFTWARE\HIDMaestro\InstalledVrManifestSha256, mirroring
/// DriverBuilder's InstalledManifestSha256, and is deliberately NOT part
/// of <see cref="EmbeddedManifest.HashedResources"/> (that set is the
/// pnputil install payload; vrpathreg is a different install mechanism,
/// and folding the VR DLL in would force a full HID re-deploy on every
/// VR-only bump). Registration runs as a top-level step from
/// <see cref="HMVRController.Connect"/>, never inside
/// <see cref="DriverBuilder.FullDeploy"/>, whose manifest-hash fast path
/// early-returns and would shadow anything appended after it.</para>
/// </summary>
internal static class VrDriverBuilder
{
    private const string ResourcePrefix = "HIDMaestro.VR.";
    private const string RegPath = @"SOFTWARE\HIDMaestro";
    private const string RegValue = "InstalledVrManifestSha256";

    private static readonly object s_lock = new();
    private static bool s_installedThisProcess;

    /// <summary>Driver folder handed to vrpathreg (contains
    /// driver.vrdrivermanifest).</summary>
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "HIDMaestro", "openvr", "hidmaestro");

    /// <summary>True if a SteamVR server process is currently running.</summary>
    public static bool IsSteamVRRunning()
    {
        try { return Process.GetProcessesByName("vrserver").Length > 0; }
        catch { return false; }
    }

    /// <summary>Extract + register the OpenVR driver. Idempotent and
    /// hash-gated: matching payload hash + complete install dir + an
    /// existing registration is a no-op (~1 ms after first call).
    /// Requires elevation for the HKLM gate write and %ProgramData%.</summary>
    public static void EnsureInstalled()
    {
        lock (s_lock)
        {
            if (s_installedThisProcess) return;

            string embeddedHash = ComputePayloadHash();
            string? installedHash = ReadInstalledHash();
            if (string.Equals(embeddedHash, installedHash, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(InstallDir, "driver.vrdrivermanifest"))
                && IsDriverRegistered())
            {
                s_installedThisProcess = true;
                return;
            }

            ExtractPayload();
            RegisterWithVrPathReg();
            WriteInstalledHash(embeddedHash);
            s_installedThisProcess = true;
        }
    }

    /// <summary>vrpathreg removedriver + gate cleanup. Used by tests.</summary>
    public static void Uninstall()
    {
        lock (s_lock)
        {
            string? vrpathreg = LocateVrPathReg();
            if (vrpathreg != null && Directory.Exists(InstallDir))
                RunTool(vrpathreg, $"removedriver \"{InstallDir}\"");
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(RegPath, writable: true);
                key?.DeleteValue(RegValue, throwOnMissingValue: false);
            }
            catch { }
            s_installedThisProcess = false;
        }
    }

    // ── payload ─────────────────────────────────────────────────────────

    private static string[] PayloadResourceNames()
    {
        var asm = typeof(VrDriverBuilder).Assembly;
        return asm.GetManifestResourceNames()
                  .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                  .OrderBy(n => n, StringComparer.Ordinal)
                  .ToArray();
    }

    /// <summary>SHA-256 over the sorted VR resource names + bytes.
    /// EmbeddedManifest.ComputeHash shape against the VR payload set.</summary>
    private static string ComputePayloadHash()
    {
        var asm = typeof(VrDriverBuilder).Assembly;
        string[] names = PayloadResourceNames();
        if (names.Length == 0)
            throw new InvalidOperationException(
                "No HIDMaestro.VR.* resources embedded in HIDMaestro.Core.dll. " +
                "Build with scripts\\build_all.cmd so build\\openvr\\ exists before the SDK compiles.");

        using var sha = SHA256.Create();
        foreach (string name in names)
        {
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
            sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
            using var s = asm.GetManifestResourceStream(name)!;
            byte[] buf = new byte[81920];
            int read;
            while ((read = s.Read(buf, 0, buf.Length)) > 0)
                sha.TransformBlock(buf, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>Re-create the SteamVR driver-folder layout from the
    /// embedded resources. Logical names carry the folder-relative path
    /// after the prefix (PackResources embeds with
    /// HIDMaestro.VR.%(RecursiveDir)%(Filename)%(Extension), so the
    /// separator is the backslash MSBuild's RecursiveDir emits).</summary>
    private static void ExtractPayload()
    {
        var asm = typeof(VrDriverBuilder).Assembly;

        // Sweep move-aside leftovers from a prior in-use upgrade (see
        // below). Best-effort: a .stale still mapped by a running
        // vrserver refuses deletion and gets swept on a later install.
        try
        {
            if (Directory.Exists(InstallDir))
                foreach (string stale in Directory.GetFiles(InstallDir, "*.stale",
                                                            SearchOption.AllDirectories))
                    try { File.Delete(stale); } catch { }
        }
        catch { }

        foreach (string name in PayloadResourceNames())
        {
            string relative = name.Substring(ResourcePrefix.Length)
                                  .Replace('\\', Path.DirectorySeparatorChar);
            string target = Path.Combine(InstallDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using var src = asm.GetManifestResourceStream(name)!;
            try
            {
                using var dst = File.Create(target);
                src.CopyTo(dst);
            }
            catch (IOException)
            {
                // Upgrade while SteamVR is running: driver_hidmaestro.dll
                // is mapped into vrserver.exe and File.Create hits a
                // sharing violation. Renaming a loaded DLL is permitted
                // on Windows, so move it aside and write fresh. The
                // running vrserver keeps executing the mapped .stale
                // bytes; the next SteamVR launch loads the new file.
                // (DriverBuilder never faces this: its hash-keyed %TEMP%
                // staging gives every payload version a fresh directory,
                // DriverBuilder.cs:117-123. The stable path this
                // registration needs makes in-place overwrite the one
                // new failure mode, handled here.)
                File.Move(target, target + ".stale", overwrite: true);
                src.Position = 0;
                using var dst = File.Create(target);
                src.CopyTo(dst);
            }
        }
    }

    // ── vrpathreg ───────────────────────────────────────────────────────

    private static void RegisterWithVrPathReg()
    {
        string? vrpathreg = LocateVrPathReg()
            ?? throw new InvalidOperationException(
                "vrpathreg.exe not found. Install SteamVR (Steam app 250820) and retry. " +
                "Searched the SteamVR uninstall key, the Steam library default path, and PATH.");

        var (exitCode, output) = RunTool(vrpathreg, $"adddriver \"{InstallDir}\"");
        // Per Driver_API_Documentation.md: 0 = success (hot-plugs a running
        // SteamVR), -1 = permission/config problem, -2 = argument problem.
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"vrpathreg adddriver failed (exit {exitCode}): {output}");
    }

    private static bool IsDriverRegistered()
    {
        string? vrpathreg = LocateVrPathReg();
        if (vrpathreg == null) return false;
        var (exitCode, output) = RunTool(vrpathreg, "show");
        return exitCode == 0
            && output.Contains(InstallDir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Locate vrpathreg.exe. Primary: SteamVR's own uninstall key
    /// (HKLM\...\Uninstall\Steam App 250820, per Driver_API_Documentation.md
    /// "Building &amp; Development Environment"), which survives SteamVR
    /// living in a secondary Steam library. Fallbacks: Steam InstallPath +
    /// default library path, then PATH.</summary>
    internal static string? LocateVrPathReg()
    {
        // Steam is 32-bit; its uninstall keys live in the 32-bit view on
        // x64 Windows. Check both views anyway.
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 250820");
                if (key?.GetValue("InstallLocation") is string loc && loc.Length > 0)
                {
                    string candidate = Path.Combine(loc, "bin", "win64", "vrpathreg.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
        }

        // Steam install dir + the default library layout.
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(@"SOFTWARE\Valve\Steam");
                if (key?.GetValue("InstallPath") is string steam && steam.Length > 0)
                {
                    string candidate = Path.Combine(steam, "steamapps", "common",
                        "SteamVR", "bin", "win64", "vrpathreg.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
        }

        // PATH.
        try
        {
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (pathEnv != null)
            {
                foreach (string dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = Path.Combine(dir.Trim(), "vrpathreg.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch { }

        return null;
    }

    private static (int exitCode, string output) RunTool(string exe, string args)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        p.Start();
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.HasExited ? p.ExitCode : -999, stdout + stderr);
    }

    // ── registry gate (DriverBuilder.ReadInstalledManifestHash shape) ──

    private static string? ReadInstalledHash()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegPath);
            return key?.GetValue(RegValue) as string;
        }
        catch { return null; }
    }

    private static void WriteInstalledHash(string hash)
    {
        // Swallow gate-write failures like DriverBuilder's counterpart:
        // a lost gate write only costs a slow re-install on the next
        // launch, never a failure report for an install that succeeded.
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(RegPath);
            key.SetValue(RegValue, hash);
        }
        catch { }
    }
}
