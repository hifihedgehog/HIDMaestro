using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace HIDMaestro.Internal;

/// <summary>
/// The one place every file the elevated SDK runs, or hands to pnputil,
/// signtool, inf2cat or the usbip-win2 installer, is written and read.
///
/// <para>Through v1.12.0 these went into <c>%TEMP%</c>: the driver payload
/// into <c>%TEMP%\HIDMaestro_&lt;hash&gt;</c>, <c>hmswd.exe</c> into a shared
/// <c>%TEMP%\HIDMaestro</c>, the usbip installer into
/// <c>%TEMP%\HIDMaestro_usbip_&lt;ver&gt;</c>. A non-elevated process of the
/// same user can write all three, and the reuse checks trusted a file on its
/// length (the driver payload and <c>hmswd.exe</c>) rather than its bytes, so
/// a planted same-length file ran elevated. Two programs installing at once
/// also collided in the one shared directory.</para>
///
/// <para>v1.13.0 stages under
/// <c>%ProgramData%\HIDMaestro\Staging\&lt;version&gt;-&lt;hash&gt;\&lt;operation&gt;</c>.
/// Every directory from <c>%ProgramData%\HIDMaestro</c> down is created with,
/// and on each use verified to carry, a protected DACL that only
/// Administrators and SYSTEM can write. A directory on the path that a
/// non-administrator could write is repaired. The shared root, which the VR
/// runtime also lives under, is reset in place. A staging directory is moved
/// aside and recreated. Every staged file's SHA-256 is checked against
/// the embedded resource both after it is written and again immediately
/// before it is used, so a file that does not match the bytes shipped inside
/// HIDMaestro.Core.dll never runs.</para>
///
/// <para>The SDDL shapes and the write-aside repair follow Kitsune Input's
/// Rust setup component, which enforces the same protected setup directory
/// for the same reason (<c>O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)</c>
/// for the staging tree, with <c>(A;OICI;FRFX;;;BU)</c> added on the shared
/// root that must stay readable). See docs/design/v1.13.0.md.</para>
/// </summary>
internal static class ProtectedStaging
{
    /// <summary>Operation name for the UMDF2 driver payload and its signing
    /// and catalog tools.</summary>
    internal const string OperationDriver = "driver";
    /// <summary>Operation name for the SWD helper, <c>hmswd.exe</c>.</summary>
    internal const string OperationSwd = "swd";
    /// <summary>Operation name for the usbip-win2 installer.</summary>
    internal const string OperationUsbip = "usbip";
    /// <summary>Operation name for the usbip-win2 host-controller package the
    /// SDK forces onto an older transport.</summary>
    internal const string OperationUsbipUde = "usbip-ude";
    /// <summary>Operation name for one deploy's driver package: the two
    /// binaries and two INFs, staged fresh from the embedded bytes, then
    /// signed and catalogued there. Only a holder of the deploy lock writes
    /// it, so the driver staging keeps the embedded bytes and another
    /// process staging them never touches a package being signed.</summary>
    internal const string OperationPackage = "package";

    // Staging tree: Administrators and SYSTEM full control, nobody else any
    // access. Protected (P) so it does not inherit the ProgramData ACEs that
    // let a standard user create files. Owner Administrators. OICI so new
    // files and subdirectories inherit it.
    private const string StagingSddl = "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
    // The shared root %ProgramData%\HIDMaestro holds the VR runtime that
    // SteamVR, running as the user, loads, so the user keeps read and
    // execute (FRFX) but no write.
    private const string RootSddl = "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FRFX;;;BU)";

    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    // Everything beyond read, execute and synchronize. A non-admin principal
    // granted any of these can change what later runs elevated.
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes |
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static readonly object s_lock = new();

    /// <summary>The root of the staging tree,
    /// <c>%ProgramData%\HIDMaestro\Staging</c>.</summary>
    internal static string StagingRoot => Path.Combine(HidMaestroRoot, "Staging");

    /// <summary><c>%ProgramData%\HIDMaestro</c>, shared with the VR runtime.</summary>
    internal static string HidMaestroRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HIDMaestro");

    /// <summary>The <c>&lt;version&gt;-&lt;hash&gt;</c> directory name for
    /// this SDK build. The hash is the first 16 hex chars of the embedded
    /// driver payload's SHA-256, so two SDK builds with the same version but
    /// different payloads do not share a directory.</summary>
    internal static string VersionTag =>
        $"{MachineClaims.SdkVersion}-{EmbeddedManifest.Sha256Hex[..16]}";

    /// <summary>Return the protected directory for one operation, creating
    /// and repairing every directory on the path from
    /// <c>%ProgramData%\HIDMaestro</c> down. Requires elevation: creating a
    /// directory owned by Administrators needs it.</summary>
    internal static string DirFor(string operation)
    {
        // Staging is a machine-wide write: the owner claim and record come
        // first, and the record names this build's directory so a deploy of
        // another build keeps it while this process runs.
        MachineClaims.NoteStaging(VersionTag);
        lock (s_lock)
        {
            // The shared root cannot be moved aside: the OpenVR path
            // registry stores an absolute path into its subtree. Repair it
            // in place.
            EnsureProtected(HidMaestroRoot, RootSddl, renameAside: false);
            EnsureProtected(StagingRoot, StagingSddl, renameAside: false);
            string versionDir = Path.Combine(StagingRoot, VersionTag);
            EnsureProtected(versionDir, StagingSddl, renameAside: true);
            string opDir = Path.Combine(versionDir, operation);
            EnsureProtected(opDir, StagingSddl, renameAside: true);
            SweepOrphanedTemps(opDir);
            return opDir;
        }
    }

    /// <summary>Stage one embedded resource into <paramref name="dir"/> under
    /// <paramref name="fileName"/>, verifying its SHA-256 against the embedded
    /// bytes. Reuses a file already there only when its hash still matches.
    /// Writes to a temporary name and renames into place, so a reader never
    /// sees a half-written file.</summary>
    internal static void StageResource(string dir, string fileName, System.Reflection.Assembly asm, string logicalName)
    {
        string expected = EmbeddedSha256(asm, logicalName);
        if (Matches(Path.Combine(dir, fileName), expected))
            return;
        using var src = asm.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource '{logicalName}' is missing from HIDMaestro.Core.dll.");
        StageStream(dir, fileName, src, expected);
    }

    /// <summary>Stage the bytes of <paramref name="src"/> into
    /// <paramref name="dir"/>\<paramref name="fileName"/>, verified against
    /// <paramref name="expectedSha256"/>.</summary>
    internal static void StageStream(string dir, string fileName, Stream src, string expectedSha256)
    {
        string path = Path.Combine(dir, fileName);
        if (Matches(path, expectedSha256))
            return;

        // The writer's process id is in the name, so a file left by a process
        // that ended mid-write can be told apart from one being written now
        // (SweepOrphanedTemps).
        string tmp = Path.Combine(dir, $"{fileName}{TempMarker}{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                src.CopyTo(dst);
            if (!Sha256OfFile(tmp).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"{fileName} did not match its embedded SHA-256 after staging; refusing to use it.");
            try
            {
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception) when (Matches(path, expectedSha256))
            {
                // Another process staged the same bytes first and may be
                // running them, which blocks the replace. The file in place
                // is the verified one, so the job is done.
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private const string TempMarker = ".new-";

    /// <summary>True when the file exists and its SHA-256 is
    /// <paramref name="expectedSha256"/>.</summary>
    internal static bool Matches(string path, string expectedSha256)
    {
        try { return File.Exists(path) && Sha256OfFile(path).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Delete temporary files in <paramref name="dir"/> whose
    /// writer is no longer running. A process that ends between writing a
    /// staged file and renaming it into place leaves one behind. A temporary
    /// file whose writer still runs, or whose name carries no process id, is
    /// left alone.</summary>
    private static void SweepOrphanedTemps(string dir)
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*" + TempMarker + "*"))
            {
                string name = Path.GetFileName(f);
                int at = name.LastIndexOf(TempMarker, StringComparison.Ordinal);
                string rest = name[(at + TempMarker.Length)..];
                int dash = rest.IndexOf('-');
                if (dash <= 0 || !int.TryParse(rest[..dash], out int pid)) continue;
                if (pid == Environment.ProcessId || IsRunning(pid)) continue;
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    private static bool IsRunning(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch { return true; } // cannot tell: keep the file
    }

    /// <summary>Throw unless the file at <paramref name="path"/> matches
    /// <paramref name="expectedSha256"/>. Called immediately before a staged
    /// file runs elevated or is signed, so a file changed after it was staged
    /// is caught at the point of use.</summary>
    internal static void VerifyBeforeUse(string path, string expectedSha256)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"{Path.GetFileName(path)} is not present in staging.");
        if (!Sha256OfFile(path).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} does not match its embedded SHA-256; refusing to use it.");
    }

    /// <summary>Throw unless the file at <paramref name="path"/> still matches
    /// the embedded resource it was staged from. Use it for a tool that runs
    /// but is never modified: signtool, inf2cat, hmswd. A driver binary is
    /// checked this way only before signing, which rewrites it.</summary>
    internal static void VerifyBeforeUse(string path, System.Reflection.Assembly asm, string logicalName)
        => VerifyBeforeUse(path, EmbeddedSha256(asm, logicalName));

    /// <summary>SHA-256 of an embedded resource's bytes.</summary>
    internal static string EmbeddedSha256(System.Reflection.Assembly asm, string logicalName)
    {
        // An assembly's resources never change while it is loaded, so each
        // is hashed once per process however often its staged copy is
        // checked.
        var key = (asm, logicalName);
        lock (s_embeddedHashes)
            if (s_embeddedHashes.TryGetValue(key, out string? known)) return known;
        using var src = asm.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource '{logicalName}' is missing from HIDMaestro.Core.dll.");
        string hash = Sha256OfStream(src);
        lock (s_embeddedHashes) s_embeddedHashes[key] = hash;
        return hash;
    }

    private static readonly Dictionary<(System.Reflection.Assembly, string), string> s_embeddedHashes = new();

    /// <summary>Delete staging directories of versions no live owner names,
    /// keeping this build's. The caller holds the deploy mutex.</summary>
    internal static void PruneOldVersions()
    {
        try
        {
            if (!Directory.Exists(StagingRoot)) return;
            // A running program's directory, by the tag its record names. A
            // record without one, written by a build before the tag, keeps
            // every directory of its version.
            var liveTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var liveVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in MachineClaims.ReadAll())
            {
                if (!r.Live) continue;
                if (!string.IsNullOrEmpty(r.StagingTag)) liveTags.Add(r.StagingTag!);
                else if (!string.IsNullOrEmpty(r.SdkVersion)) liveVersions.Add(r.SdkVersion!);
            }

            foreach (string dir in Directory.EnumerateDirectories(StagingRoot))
            {
                string tag = Path.GetFileName(dir);
                if (tag.Equals(VersionTag, StringComparison.OrdinalIgnoreCase))
                {
                    // This build's own directory stays. Only what a repair
                    // moved aside inside it goes.
                    foreach (string inner in Directory.EnumerateDirectories(dir, "*.untrusted-*"))
                        TryDelete(inner);
                    continue;
                }
                // tag is "<version>-<hash16>", or a moved-aside
                // "<version>-<hash16>.untrusted-<guid>". The version is
                // everything before the first '-'.
                int dash = tag.IndexOf('-');
                string version = dash > 0 ? tag[..dash] : tag;
                if (!tag.Contains(".untrusted-", StringComparison.Ordinal)
                    && (liveTags.Contains(tag) || liveVersions.Contains(version)))
                    continue;
                TryDelete(dir);
            }
        }
        catch (Exception ex) { DeviceOrchestrator.LogDiag($"ProtectedStaging: prune skipped: {ex.GetType().Name}"); }
    }

    private static void TryDelete(string dir)
    {
        // Directory.Delete removes a reparse point inside the tree as a link;
        // it does not follow it into the target.
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { DeviceOrchestrator.LogDiag($"ProtectedStaging: could not prune {Path.GetFileName(dir)}: {ex.GetType().Name}"); }
    }

    // ── Directory protection ──────────────────────────────────────────────

    private static void EnsureProtected(string dir, string sddl, bool renameAside)
    {
        if (Directory.Exists(dir))
        {
            if (IsProtected(dir)) return;
            // A move renames the directory entry itself, a reparse point
            // included, so it is safe for anything under the staging root.
            if (renameAside && TryMoveAside(dir))
            {
                CreateProtected(dir, sddl);
                return;
            }
            // Setting security on a reparse point would set it on the target
            // instead. A root that is one is not used at all.
            if (IsReparsePoint(dir))
                throw new InvalidOperationException(
                    $"{dir} is a reparse point; HIDMaestro will not stage files through it.");
            // The shared root, or a move that could not be done: reset owner
            // and DACL in place. A staging directory this touches is still
            // backed by the per-file hash check at use.
            Reset(dir, sddl);
            return;
        }
        CreateProtected(dir, sddl);
    }

    private static bool IsReparsePoint(string dir)
    {
        try { return (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    private static void CreateProtected(string dir, string sddl)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        // Create the directory with the security descriptor in one step, so
        // there is no window in which it exists with an inherited ACL. The
        // callers build the path top down, so the parent always exists first.
        FileSystemAclExtensions.CreateDirectory(security, dir);
        // CreateDirectory is a no-op on a directory that already exists, in
        // which case it leaves the old ACL. That only happens when another
        // process created it between the Exists check and here. The check
        // below leaves it protected either way.
        if (!IsProtected(dir)) Reset(dir, sddl);
    }

    private static void Reset(string dir, string sddl)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        new DirectoryInfo(dir).SetAccessControl(security);
    }

    private static bool TryMoveAside(string dir)
    {
        try
        {
            string aside = dir + ".untrusted-" + Guid.NewGuid().ToString("N");
            Directory.Move(dir, aside);
            DeviceOrchestrator.LogDiag($"ProtectedStaging: moved an unprotected {Path.GetFileName(dir)} aside to {Path.GetFileName(aside)}.");
            return true;
        }
        catch { return false; }
    }

    /// <summary>True when the directory is a plain directory, not a reparse
    /// point, owned by Administrators or SYSTEM, and no other principal is
    /// granted any write-class right.</summary>
    private static bool IsProtected(string dir)
    {
        try
        {
            if (IsReparsePoint(dir)) return false;
            var security = new DirectoryInfo(dir).GetAccessControl();
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null || (!owner.Equals(AdminsSid) && !owner.Equals(SystemSid)))
                return false;

            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.FileSystemRights & WriteRights) == 0) continue;
                if (rule.IdentityReference is SecurityIdentifier sid && (sid.Equals(AdminsSid) || sid.Equals(SystemSid)))
                    continue;
                return false; // a non-admin principal can write
            }
            return true;
        }
        catch { return false; }
    }

    // ── Hashing ─────────────────────────────────────────────────────────

    /// <summary>The SHA-256 of a file, lowercase hex.</summary>
    internal static string FileSha256(string path) => Sha256OfFile(path);

    private static string Sha256OfFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static string Sha256OfStream(Stream s)
    {
        long pos = s.CanSeek ? s.Position : 0;
        string hex = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
        if (s.CanSeek) s.Position = pos;
        return hex;
    }
}
