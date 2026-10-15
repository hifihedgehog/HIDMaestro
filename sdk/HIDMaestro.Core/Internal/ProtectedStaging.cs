using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

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
/// non-administrator could write is repaired. A staging directory,
/// <c>Staging</c> included, is moved aside and recreated. The shared root,
/// which the OpenVR driver also lives under, and the OpenVR driver's folders
/// are never moved: SteamVR maps the driver's files from there, and a
/// directory holding a mapped file cannot be renamed. They are taken over in
/// place instead, owner set to Administrators and the protected DACL
/// written, which also covers a root v1.12.0 created, owned by the elevated
/// user's own SID under Windows' default owner policy.</para>
///
/// <para>A reset leaves one thing behind: a handle a program opened with
/// WRITE_DAC before it, which keeps that access ("the requested access is
/// compared to the granted access", ObReferenceObjectByHandle on Learn). Its
/// holder can change the root's DACL again and, through that, rename the
/// root's entries. So every use of staged files runs inside a
/// <see cref="Pin"/>: each directory from the root down to the one in use is
/// held open without delete sharing, which no rename or delete of those
/// directories can get past, and is checked again after it is held. A swap
/// made between two uses is found at the next one and moved aside. Every
/// staged file's SHA-256 is checked against the embedded resource after it
/// is written and again under the pin, before each use, so a file that does
/// not match the bytes shipped inside HIDMaestro.Core.dll never runs.</para>
///
/// <para>Lock order. The staging lock (<see cref="StagingMutexObject"/>) is
/// a leaf. It is taken under the deploy lock (a deploy's prune, every
/// staging a deploy does, and the usbip-win2 transport's staging), or with
/// no lock held (a constructor's prewarm, the SWD helper's staging), and
/// nothing takes another machine-wide lock while holding it.
/// <see cref="DirFor"/> holds it only around its record note, before its
/// process-local lock. S81 checks that a staging waits for a holder of the
/// lock and a deploy completes after it.</para>
///
/// <para>The SDDL shapes and the write-aside repair follow Kitsune Input's
/// Rust setup component, which enforces the same protected setup directory
/// for the same reason (<c>O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)</c>
/// for the staging tree, with <c>(A;OICI;FRFX;;;BU)</c> added on the shared
/// root that must stay readable). See docs/design/v1.13.0.md.</para>
/// </summary>
internal static partial class ProtectedStaging
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

    /// <summary><c>%ProgramData%\HIDMaestro</c>, shared with the VR runtime.
    ///
    /// <para>Invariant: nothing the elevated SDK runs, loads or hands to
    /// pnputil, signtool or inf2cat may live directly under this root or
    /// under the OpenVR folders. The root and those folders are repaired in
    /// place (<see cref="Repair.InPlace"/>), so a program that opened one
    /// with WRITE_DAC before the repair keeps that access, and a pin blocks
    /// a rename or delete but not a change made through such a handle. Only
    /// the staging tree, <see cref="StagingRoot"/> and below, is moved aside
    /// when it is not protected, which leaves no stale handle on it. Every
    /// file the elevated SDK uses is therefore staged there through
    /// <see cref="DirFor"/>. The OpenVR files are loaded only by SteamVR,
    /// which runs without elevation. A new file that the elevated SDK uses
    /// goes under <see cref="DirFor"/>, never next to the OpenVR
    /// folders.</para></summary>
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
        // another build keeps it while this process runs. The note is
        // written under the staging lock, which a prune holds from its read
        // of the records to its last delete, so a directory is either named
        // before the prune reads or touched only after the prune is done.
        using (var note = AcquireStagingLock())
        {
            try { MachineClaims.NoteStaging(VersionTag); }
            finally { note.ReleaseMutex(); }
        }
        lock (s_lock)
        {
            // The shared root is taken over in place, never moved: the
            // OpenVR path registry stores an absolute path into its subtree,
            // and SteamVR may hold the driver's files open. That is safe only
            // while nothing the elevated SDK uses lives directly under it
            // (the invariant on HidMaestroRoot). Pin closes what a stale
            // handle on it could do while staged files are used.
            EnsureProtected(HidMaestroRoot, RootSddl, Repair.InPlace);
            EnsureProtected(StagingRoot, StagingSddl, Repair.MoveAside);
            string versionDir = Path.Combine(StagingRoot, VersionTag);
            EnsureProtected(versionDir, StagingSddl, Repair.MoveAside);
            string opDir = Path.Combine(versionDir, operation);
            EnsureProtected(opDir, StagingSddl, Repair.MoveAside);
            SweepOrphanedTemps(opDir);
            return opDir;
        }
    }

    /// <summary>A directory under <c>%ProgramData%\HIDMaestro</c> outside
    /// the staging tree, such as the OpenVR driver's, whose absolute path is
    /// stored elsewhere and whose files SteamVR may hold open, so it cannot
    /// move. The root and every level down to <paramref name="relative"/> are
    /// taken over in place: owner Administrators and the root's DACL,
    /// Administrators/SYSTEM-write-only and user-readable. A level that is a
    /// reparse point is refused. Requires elevation.</summary>
    internal static string SharedDir(string relative)
    {
        lock (s_lock)
        {
            EnsureProtected(HidMaestroRoot, RootSddl, Repair.InPlace);
            string dir = HidMaestroRoot;
            foreach (string part in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                dir = Path.Combine(dir, part);
                EnsureProtected(dir, RootSddl, Repair.InPlace);
            }
            return dir;
        }
    }

    /// <summary>Directories held open, without delete sharing, for as long
    /// as staged files in them are used. Dispose releases them.</summary>
    internal sealed class Pinned : IDisposable
    {
        private readonly List<SafeFileHandle> _handles;
        internal Pinned(List<SafeFileHandle> handles) => _handles = handles;
        public void Dispose()
        {
            foreach (var h in _handles) h.Dispose();
            _handles.Clear();
        }
    }

    /// <summary>Hold every directory from <c>%ProgramData%\HIDMaestro</c>
    /// down to each of <paramref name="dirs"/> open for listing, sharing read
    /// and write but not delete, then check each again: a plain directory
    /// owned by Administrators or SYSTEM that no other principal can write.
    /// While held, no program can rename or delete one of them, since a
    /// rename or delete opens the directory for DELETE and every open must be
    /// compatible with the sharing of the opens already there (CreateFileW,
    /// FILE_SHARE_DELETE, on Learn). A handle that asks only for READ_CONTROL
    /// or WRITE_DAC takes no part in that check, which is why these ask for
    /// FILE_LIST_DIRECTORY. The caller staged and repaired the directories
    /// first: a repair moves a directory, which a pin would refuse.</summary>
    /// <exception cref="InvalidOperationException">A directory could not be
    /// opened, or is not protected once held.</exception>
    internal static Pinned Pin(params string[] dirs)
    {
        string root = Path.GetFullPath(HidMaestroRoot).TrimEnd('\\');
        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string d in dirs)
        {
            string rel = Path.GetRelativePath(root, Path.GetFullPath(d));
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                throw new InvalidOperationException($"{d} is outside {root}.");
            string cur = root;
            if (seen.Add(cur)) chain.Add(cur);
            if (rel == ".") continue;
            foreach (string part in rel.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                cur = Path.Combine(cur, part);
                if (seen.Add(cur)) chain.Add(cur);
            }
        }
        var handles = new List<SafeFileHandle>();
        var pinned = new Pinned(handles);
        try
        {
            foreach (string dir in chain)
            {
                var h = CreateFileW(dir, FILE_LIST_DIRECTORY, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                    OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
                if (h.IsInvalid)
                {
                    int err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                    h.Dispose();
                    throw new InvalidOperationException(
                        $"HIDMaestro could not hold {dir} open while it uses the files under it (Win32 error {err}).");
                }
                handles.Add(h);
            }
            foreach (string dir in chain)
                if (!IsProtected(dir))
                    throw new InvalidOperationException(
                        $"{dir} changed between its repair and its use and is not Administrators/SYSTEM-write-only. HIDMaestro will not use the files under it.");
            return pinned;
        }
        catch
        {
            pinned.Dispose();
            throw;
        }
    }

    private const uint FILE_LIST_DIRECTORY = 0x0001;
    private const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    /// <summary>The staging lock's name in <see cref="AdminNamespace"/>.</summary>
    internal const string StagingMutexObject = "Staging";

    /// <summary>Called by a prune after it reads the owner records and before
    /// it deletes anything, while it holds the staging lock. A test seam,
    /// null in a program.</summary>
    internal static Action? AfterPruneRead;

    /// <summary>Take the staging lock, held by the calling thread until it
    /// calls <c>ReleaseMutex</c>. An abandoned lock is taken.</summary>
    /// <exception cref="UnauthorizedAccessException">The process is not
    /// elevated.</exception>
    internal static System.Threading.Mutex AcquireStagingLock()
    {
        var m = new System.Threading.Mutex(false, AdminNamespace.Name(StagingMutexObject));
        try
        {
            if (!m.WaitOne(TimeoutScale.Apply(120_000)))
            {
                m.Dispose();
                throw new InvalidOperationException(
                    "Another program's HIDMaestro staging prune held the machine-wide staging lock for two minutes.");
            }
        }
        catch (System.Threading.AbandonedMutexException) { }
        return m;
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
    /// keeping this build's. The caller holds the deploy mutex.
    ///
    /// <para>The staging lock is held from the read of the records to the
    /// last delete, and every program writes its record's staging note
    /// under the same lock before it touches its directory
    /// (<see cref="DirFor"/>). So a program of another build either named
    /// its directory before this read, and the directory stays, or reaches
    /// its directory only after the deletes, and recreates it. A program
    /// whose record names its directory keeps it while it runs.</para></summary>
    internal static void PruneOldVersions()
    {
        System.Threading.Mutex? held = null;
        try
        {
            held = AcquireStagingLock();
            DeleteMovedAside();
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
            AfterPruneRead?.Invoke();

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
        finally
        {
            if (held != null)
            {
                held.ReleaseMutex();
                held.Dispose();
            }
        }
    }

    /// <summary>Delete what a repair moved aside outside a version's
    /// directory: a moved <c>Staging</c>, and the root and OpenVR folders a
    /// prerelease v1.13.0 build moved, whose names end in
    /// <c>.untrusted-&lt;guid&gt;</c>. Only the levels named here are read,
    /// never a tree walked, so no link inside one is followed. A tree is
    /// renamed before it is deleted: Windows refuses to rename a directory
    /// while any file or directory under it is open, so a tree a program
    /// still holds stays whole for a later deploy instead of being deleted
    /// around its open files.</summary>
    private static void DeleteMovedAside()
    {
        string programData = Path.GetDirectoryName(Path.GetFullPath(HidMaestroRoot).TrimEnd('\\'))!;
        var levels = new List<(string Dir, string Pattern)>
        {
            (programData, Path.GetFileName(HidMaestroRoot) + ".untrusted-*"),
            (HidMaestroRoot, "*.untrusted-*"),
            (Path.Combine(HidMaestroRoot, "openvr"), "*.untrusted-*"),
            (Path.Combine(HidMaestroRoot, "openvr", "hidmaestro"), "*.untrusted-*"),
        };
        foreach (var (dir, pattern) in levels)
        {
            if (!Directory.Exists(dir) || IsReparsePoint(dir)) continue;
            foreach (string aside in Directory.EnumerateDirectories(dir, pattern))
            {
                string gone = aside.EndsWith(GoneSuffix, StringComparison.Ordinal) ? aside : aside + GoneSuffix;
                if (gone != aside)
                {
                    try { Directory.Move(aside, gone); }
                    catch (Exception ex)
                    {
                        DeviceOrchestrator.LogDiag($"ProtectedStaging: kept {Path.GetFileName(aside)}, still in use: {ex.GetType().Name}");
                        continue;
                    }
                }
                TryDelete(gone);
            }
        }
    }

    private const string GoneSuffix = ".gone";

    private static void TryDelete(string dir)
    {
        // Directory.Delete removes a reparse point inside the tree as a link;
        // it does not follow it into the target.
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { DeviceOrchestrator.LogDiag($"ProtectedStaging: could not prune {Path.GetFileName(dir)}: {ex.GetType().Name}"); }
    }

    // ── Directory protection ──────────────────────────────────────────────

    /// <summary>How an existing directory that is not protected is
    /// repaired.</summary>
    private enum Repair
    {
        /// <summary>Moved aside and made again, or, when it cannot be moved,
        /// reset in place unless that would leave a stale handle with
        /// WRITE_DAC (<see cref="IsHostile"/>). The staging tree.</summary>
        MoveAside,
        /// <summary>Taken over where it is: owner Administrators and the
        /// protected DACL. A reparse point is refused. The shared root and
        /// the OpenVR folders, whose paths are stored elsewhere and whose
        /// files SteamVR may hold open.</summary>
        InPlace,
    }

    private static void EnsureProtected(string dir, string sddl, Repair repair)
    {
        if (Directory.Exists(dir))
        {
            if (IsProtected(dir)) return;
            if (repair == Repair.InPlace)
            {
                // Setting security on a reparse point would set it on the
                // target instead.
                if (IsReparsePoint(dir))
                    throw new InvalidOperationException(
                        $"{dir} is a reparse point. HIDMaestro will not use files under it.");
                // A program that opened it with WRITE_DAC before this keeps
                // that access. Pin covers the uses that follow.
                Reset(dir, sddl);
                return;
            }
            // An owner other than Administrators or SYSTEM has WRITE_DAC
            // implicitly ("An object's owner implicitly has WRITE_DAC access
            // to the object", Owner of a New Object on Learn), and a handle
            // keeps the access it was granted when it was opened. A reset
            // therefore leaves such a principal able to change the DACL back
            // through a handle it already holds, and the directory is moved
            // aside instead, wherever it sits. A move renames the directory
            // entry itself, a reparse point included.
            bool hostile = IsHostile(dir);
            if (TryMoveAside(dir))
            {
                CreateProtected(dir, sddl);
                return;
            }
            // Setting security on a reparse point would set it on the target
            // instead, and a hostile directory keeps its handles: neither is
            // used at all.
            if (hostile)
                throw new InvalidOperationException(
                    $"{dir} is a reparse point, or a principal other than Administrators and SYSTEM owns it or may change its permissions, " +
                    "and it could not be moved aside. HIDMaestro will not stage files under it.");
            // A move that could not be done, and only the DACL is wrong: no
            // principal but Administrators and SYSTEM can have opened it for
            // WRITE_DAC or WRITE_OWNER. Reset owner and DACL in place. The
            // files under it are still checked by hash under the pin at use.
            Reset(dir, sddl);
            return;
        }
        CreateProtected(dir, sddl);
    }

    /// <summary>True when a reset in place cannot make the directory safe:
    /// it is a reparse point, or its owner is not Administrators or SYSTEM,
    /// or another principal is granted the right to change its DACL or owner.
    /// Such a principal may hold a handle with that access open across the
    /// reset.</summary>
    private static bool IsHostile(string dir)
    {
        try
        {
            if (IsReparsePoint(dir)) return true;
            var security = new DirectoryInfo(dir).GetAccessControl();
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null || (!owner.Equals(AdminsSid) && !owner.Equals(SystemSid)))
                return true;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.FileSystemRights & (FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) == 0) continue;
                if (rule.IdentityReference is SecurityIdentifier sid && (sid.Equals(AdminsSid) || sid.Equals(SystemSid)))
                    continue;
                return true;
            }
            return false;
        }
        catch { return true; }
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
        // process created it between the Exists check and here. One a
        // non-administrator made in that window is refused, for the reason
        // EnsureProtected moves such a directory aside. Otherwise the reset
        // leaves it protected.
        if (IsProtected(dir)) return;
        if (IsHostile(dir))
            throw new InvalidOperationException(
                $"{dir} was created by a principal other than Administrators and SYSTEM while HIDMaestro created it. HIDMaestro will not stage files under it.");
        Reset(dir, sddl);
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
