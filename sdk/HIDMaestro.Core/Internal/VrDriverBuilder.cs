using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>Extracts the embedded OpenVR driver package and registers it
/// with SteamVR via vrpathreg (issue #32).
///
/// <para>Mirrors the established install machinery with two deliberate
/// differences from <see cref="DriverBuilder"/>: the extract path is
/// STABLE (<c>%ProgramData%\HIDMaestro\openvr\hidmaestro</c>) rather than
/// the per-version staging directory, because vrpathreg stores the
/// absolute path and a moving target would strand the registration; and
/// the driver DLL is NOT part of <see cref="EmbeddedManifest"/>'s hashed
/// set, because vrpathreg is a different install mechanism from pnputil
/// and folding it in would force HID re-deploys on VR-only bumps. The
/// idempotence gate is its own registry value,
/// <c>HKLM\SOFTWARE\HIDMaestro\InstalledVrManifestSha256</c>, mirroring
/// DriverBuilder's InstalledManifestSha256.</para>
///
/// <para>SteamVR discovery covers both install shapes: the Steam-client
/// install (uninstall key for app 250820, then Steam library fallback)
/// and the Steam-free steamcmd install
/// (<c>steamcmd +login anonymous +app_update 250820</c>), which writes no
/// registry keys at all and is therefore located via the explicit
/// <c>HKLM\SOFTWARE\HIDMaestro\SteamVRPath</c> hint or the conventional
/// <c>C:\SteamVR</c> folder. The steamcmd path is empirically verified:
/// anonymous login downloads app 250820 in full (2026-08-07, SteamVR
/// 2.16.7, this repo's rig).</para></summary>
internal static class VrDriverBuilder
{
    private const string RegKeyPath = @"SOFTWARE\HIDMaestro";
    private const string VrManifestRegValue = "InstalledVrManifestSha256";
    private const string SteamVrPathRegValue = "SteamVRPath";
    private const string ResourcePrefix = "HIDMaestro.VR.";

    /// <summary>What SteamVR needs to load the driver: the manifest at the
    /// folder's root and the DLL under bin\win64, named for the manifest's
    /// "name" (openvr/docs/Driver_API_Documentation.md). The resource names
    /// keep the folder layout, so the DLL's carries a backslash.</summary>
    internal static readonly string[] RequiredResources =
    {
        ResourcePrefix + "driver.vrdrivermanifest",
        ResourcePrefix + @"bin\win64\driver_hidmaestro.dll",
    };

    /// <summary>Stable extraction root. See class remarks for why this is
    /// not the per-version staging directory.</summary>
    public static string ExtractRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "HIDMaestro", "openvr", "hidmaestro");

    /// <summary>Locate the SteamVR install directory, or null. Order:
    /// the explicit HIDMaestro hint (steamcmd installs), the SteamVR
    /// uninstall key, the Steam library default, C:\SteamVR.</summary>
    public static string? FindSteamVR()
    {
        static string? Check(string? dir) =>
            dir != null && File.Exists(Path.Combine(dir, "bin", "win64", "vrpathreg.exe")) ? dir : null;

        using (var hm = Registry.LocalMachine.OpenSubKey(RegKeyPath))
        {
            var hinted = Check(hm?.GetValue(SteamVrPathRegValue) as string);
            if (hinted != null) return hinted;
        }

        using (var k = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 250820"))
        {
            var fromUninstall = Check(k?.GetValue("InstallLocation") as string);
            if (fromUninstall != null) return fromUninstall;
        }

        using (var steam = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
        {
            if (steam?.GetValue("InstallPath") is string steamRoot)
            {
                var fromLibrary = Check(Path.Combine(steamRoot, "steamapps", "common", "SteamVR"));
                if (fromLibrary != null) return fromLibrary;
            }
        }

        return Check(@"C:\SteamVR");
    }

    /// <summary>Record where a steamcmd-style SteamVR lives so discovery
    /// finds it on every later run. Requires admin (HKLM).</summary>
    public static void SetSteamVRPathHint(string steamVrDir)
    {
        using var k = Registry.LocalMachine.CreateSubKey(RegKeyPath);
        k.SetValue(SteamVrPathRegValue, steamVrDir);
    }

    public static bool IsSteamVRInstalled => FindSteamVR() != null;

    public static bool IsSteamVRRunning =>
        Process.GetProcessesByName("vrserver").Length > 0;

    /// <summary>Extract the embedded driver folder (verbatim layout) and
    /// run <c>vrpathreg adddriver</c>. Idempotent via the content-hash
    /// registry gate; re-runs only when the embedded payload changed.
    /// Returns false when SteamVR is not installed. Throws on extraction
    /// or vrpathreg failure. Requires admin for %ProgramData% + HKLM.</summary>
    public static bool EnsureDriverRegistered()
    {
        // The build's payload first: a build without the driver can never
        // register it, whether or not this machine has SteamVR.
        HMBuildInfo.ThrowIfMissing(HMPayload.OpenVrDriver);
        string? steamVr = FindSteamVR();
        if (steamVr == null)
            return false;

        var asm = typeof(VrDriverBuilder).Assembly;
        string[] names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        string payloadHash = HashPayload(asm, names);
        using (var k = Registry.LocalMachine.OpenSubKey(RegKeyPath))
        {
            if (k?.GetValue(VrManifestRegValue) as string == payloadHash &&
                Directory.Exists(ExtractRoot))
                return true;   // registered and current
        }

        ExtractPayload(asm, names);
        RunVrPathReg(steamVr, "adddriver", ExtractRoot);

        using (var k = Registry.LocalMachine.CreateSubKey(RegKeyPath))
        {
            k.SetValue(VrManifestRegValue, payloadHash);
        }
        return true;
    }

    /// <summary>The name SteamVR knows this driver by: the <c>name</c> in
    /// the embedded <c>driver.vrdrivermanifest</c>.</summary>
    public static string DriverName
    {
        get
        {
            try
            {
                using var s = typeof(VrDriverBuilder).Assembly
                    .GetManifestResourceStream(ResourcePrefix + "driver.vrdrivermanifest");
                if (s != null && ManifestName(s) is string n) return n;
            }
            catch { }
            return "hidmaestro";
        }
    }

    /// <summary>Driver folders other than ours that SteamVR can see under
    /// our driver name. OpenVR requires the manifest name to be globally
    /// unique (openvr/docs/Driver_API_Documentation.md, driver.vrdrivermanifest
    /// "name"), and a derivative that keeps it also keeps the
    /// <c>{hidmaestro}</c> resource prefix and the <c>Global\HIDMaestroVR*</c>
    /// object names, so which of the two SteamVR loads, and which IPC the
    /// loaded one opens, is not ours to decide. SteamVR loads drivers from
    /// the <c>external_drivers</c> list of the user's path registry
    /// (<c>%LOCALAPPDATA%\openvr\openvrpaths.vrpath</c>, or the file
    /// <c>VR_PATHREG_OVERRIDE</c> names: openvr
    /// src/vrcore/vrpathregistry_public.cpp, GetVRPathRegistryFilename) and
    /// from its own <c>drivers</c> folder. Both are read here, and nothing is
    /// changed.</summary>
    public static System.Collections.Generic.IReadOnlyList<string> FindDriverNameConflicts()
    {
        var found = new System.Collections.Generic.List<string>();
        string name = DriverName;
        string ours = Normalize(ExtractRoot);
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string dir)
        {
            string full = Normalize(dir);
            if (full.Length == 0 || !seen.Add(full)) return;
            if (string.Equals(full, ours, StringComparison.OrdinalIgnoreCase)) return;
            string manifest = Path.Combine(full, "driver.vrdrivermanifest");
            try
            {
                if (!File.Exists(manifest)) return;
                using var s = File.OpenRead(manifest);
                if (string.Equals(ManifestName(s), name, StringComparison.OrdinalIgnoreCase))
                    found.Add(full);
            }
            catch { }
        }

        foreach (var dir in RegisteredExternalDrivers()) Consider(dir);
        string? steamVr = FindSteamVR();
        if (steamVr != null)
        {
            try
            {
                string drivers = Path.Combine(steamVr, "drivers");
                if (Directory.Exists(drivers))
                    foreach (var dir in Directory.GetDirectories(drivers)) Consider(dir);
            }
            catch { }
        }
        return found;
    }

    /// <summary>The external_drivers entries of the user's OpenVR path
    /// registry, or none.</summary>
    internal static System.Collections.Generic.IEnumerable<string> RegisteredExternalDrivers()
    {
        string? file = Environment.GetEnvironmentVariable("VR_PATHREG_OVERRIDE");
        if (string.IsNullOrEmpty(file))
            file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "openvr", "openvrpaths.vrpath");
        var list = new System.Collections.Generic.List<string>();
        try
        {
            if (!File.Exists(file)) return list;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file),
                new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
            if (doc.RootElement.TryGetProperty("external_drivers", out var arr)
                && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                    if (e.ValueKind == System.Text.Json.JsonValueKind.String && e.GetString() is string p)
                        list.Add(p);
        }
        catch { }
        return list;
    }

    private static string? ManifestName(Stream s)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(s,
                new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
            return doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String
                ? n.GetString() : null;
        }
        catch { return null; }
    }

    private static string Normalize(string dir)
    {
        try { return Path.GetFullPath(dir).TrimEnd('\\', '/'); }
        catch { return ""; }
    }

    /// <summary>Unregister and clear the gate. Used by cleanup paths and
    /// the smoke probe's teardown.</summary>
    public static void UnregisterDriver()
    {
        string? steamVr = FindSteamVR();
        if (steamVr != null && Directory.Exists(ExtractRoot))
        {
            try { RunVrPathReg(steamVr, "removedriver", ExtractRoot); }
            catch { /* best-effort: a missing registration is the goal state */ }
        }
        using var k = Registry.LocalMachine.CreateSubKey(RegKeyPath);
        k.DeleteValue(VrManifestRegValue, throwOnMissingValue: false);
    }

    private static string HashPayload(Assembly asm, string[] names)
    {
        using var sha = SHA256.Create();
        var acc = new MemoryStream();
        foreach (var name in names)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            acc.Write(nameBytes, 0, nameBytes.Length);
            using var s = asm.GetManifestResourceStream(name)!;
            s.CopyTo(acc);
        }
        acc.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(acc));
    }

    private static void ExtractPayload(Assembly asm, string[] names)
    {
        Directory.CreateDirectory(ExtractRoot);
        foreach (var name in names)
        {
            // Logical name: HIDMaestro.VR.<RecursiveDir with '\'><file>.
            // RecursiveDir keeps real backslashes in the logical name, so
            // the relative path reconstructs directly.
            string rel = name.Substring(ResourcePrefix.Length).Replace('/', '\\');
            string dest = Path.Combine(ExtractRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var src = asm.GetManifestResourceStream(name)!;
            using var dst = File.Create(dest);
            src.CopyTo(dst);
        }
    }

    private static void RunVrPathReg(string steamVrDir, string verb, string driverPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(steamVrDir, "bin", "win64", "vrpathreg.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(driverPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("vrpathreg failed to start");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(15000);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"vrpathreg {verb} exited {proc.ExitCode}: {stdout} {stderr}");
    }
}
