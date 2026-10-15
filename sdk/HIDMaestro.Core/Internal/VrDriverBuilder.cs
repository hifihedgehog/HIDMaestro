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
/// with SteamVR in the user's OpenVR path registry (issue #32).
///
/// <para>Mirrors the established install machinery with two deliberate
/// differences from <see cref="DriverBuilder"/>: the extract path is
/// STABLE (<c>%ProgramData%\HIDMaestro\openvr\hidmaestro</c>) rather than
/// the per-version staging directory, because the path registry stores the
/// absolute path and a moving target would strand the registration; and
/// the driver DLL is NOT part of <see cref="EmbeddedManifest"/>'s hashed
/// set, because the path registry is a different install mechanism from
/// pnputil and folding it in would force HID re-deploys on VR-only bumps. The
/// idempotence gate is its own registry value,
/// <c>HKLM\SOFTWARE\HIDMaestro\InstalledVrManifestSha256</c>, mirroring
/// DriverBuilder's InstalledManifestSha256. The gate covers the
/// extraction only. The registration is written by
/// <see cref="OpenVrPathRegistry"/>, never by running SteamVR's
/// vrpathreg.exe.</para>
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
    /// add it to the calling user's OpenVR path registry, as
    /// <c>vrpathreg adddriver</c> would. The extraction is gated on the
    /// content hash and re-runs only when the embedded payload changed. The
    /// registration is checked on every call and written only when the
    /// folder is missing from the list. Returns false when SteamVR is not
    /// installed. Throws on extraction or write failure. Requires admin for
    /// %ProgramData% + HKLM.</summary>
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
        bool current;
        using (var k = Registry.LocalMachine.OpenSubKey(RegKeyPath))
        {
            // Every file, not the folder alone: a staging repair can move
            // the whole %ProgramData%\HIDMaestro tree aside (ProtectedStaging),
            // and the registration names this path, so the files must be
            // back at it before SteamVR next loads it.
            current = k?.GetValue(VrManifestRegValue) as string == payloadHash &&
                      names.All(n => File.Exists(Path.Combine(ExtractRoot, RelativePath(n))));
        }

        if (!current) ExtractPayload(asm, names);
        OpenVrPathRegistry.AddDriver(ExtractRoot);

        if (!current)
        {
            using var k = Registry.LocalMachine.CreateSubKey(RegKeyPath);
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
    /// registry, or none, read with OpenVR's own parsing rules, so the list
    /// is the one SteamVR loads.</summary>
    internal static System.Collections.Generic.IEnumerable<string> RegisteredExternalDrivers() =>
        OpenVrPathRegistry.ExternalDrivers();

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

    /// <summary>Remove the driver from the user's OpenVR path registry, then
    /// clear the idempotence gate so the next <see cref="EnsureDriverRegistered"/>
    /// extracts the payload again. The entry is removed whether or not
    /// SteamVR or the extracted folder is still there, since a registration
    /// left behind is the thing to remove. A registry that cannot be updated
    /// throws InvalidOperationException before the gate is touched: the entry
    /// is still registered, so the extraction and the gate that describes it
    /// stay as they were.</summary>
    public static void UnregisterDriver()
    {
        OpenVrPathRegistry.RemoveDriver(ExtractRoot);
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

    // Logical name: HIDMaestro.VR.<RecursiveDir with '\'><file>.
    // RecursiveDir keeps real backslashes in the logical name, so the
    // relative path reconstructs directly.
    private static string RelativePath(string name) =>
        name.Substring(ResourcePrefix.Length).Replace('/', '\\');

    private static void ExtractPayload(Assembly asm, string[] names)
    {
        // The elevated SDK writes here, and SteamVR loads the result, so
        // every directory on the way is Administrators/SYSTEM-write-only
        // like the shared root, and one a non-administrator owns or could
        // re-permission is moved aside (ProtectedStaging.SharedDir). Each
        // file goes in through a temporary name and a rename, checked
        // against its embedded SHA-256, so a link planted at a file's path
        // is replaced, not written through.
        // Every folder is repaired first, then checked again through a
        // handle (ProtectedStaging.Pin). Users can read these folders, as
        // SteamVR needs, so any program could block a hold on them, and the
        // pin holds none: while the SDK writes, only an administrator can
        // rename or re-permission one.
        string extractRelative = Path.GetRelativePath(ProtectedStaging.HidMaestroRoot, ExtractRoot);
        var targets = new System.Collections.Generic.List<(string Dir, string File, string Name)>();
        foreach (var name in names)
        {
            string rel = RelativePath(name);
            string? relDir = Path.GetDirectoryName(rel);
            string dir = ProtectedStaging.SharedDir(
                string.IsNullOrEmpty(relDir) ? extractRelative : Path.Combine(extractRelative, relDir));
            targets.Add((dir, Path.GetFileName(rel), name));
        }
        using var pin = ProtectedStaging.Pin(targets.Select(t => t.Dir).ToArray());
        foreach (var (dir, file, name) in targets)
        {
            using var src = asm.GetManifestResourceStream(name)!;
            ProtectedStaging.StageStream(dir, file, src, ProtectedStaging.EmbeddedSha256(asm, name));
        }
    }
}
