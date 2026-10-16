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
/// also collided in the one shared directory. v1.12.0 also staged its
/// usbip-win2 host controller package under
/// <c>%ProgramData%\HIDMaestro\usbip\&lt;ver&gt;\&lt;arch&gt;</c>, which a
/// running v1.12.0 still writes, and which is the second way
/// <c>%ProgramData%\HIDMaestro</c> comes to exist before this build
/// runs.</para>
///
/// <para>v1.13.0 stages under
/// <c>%ProgramFiles%\HIDMaestro\Staging\&lt;version&gt;-&lt;hash&gt;\&lt;operation&gt;</c>.
/// Program Files grants Users read and execute and nothing else, so no
/// program without elevation can create a folder there before HIDMaestro
/// does. %ProgramData% lets Users add folders (BUILTIN\Users
/// <c>(CI)(WD,AD,WEA,WA)</c> on the folder Windows ships), so a program
/// without elevation could create <c>%ProgramData%\HIDMaestro</c>, or
/// <c>Staging</c> under a root that inherited that DACL, own it, hold a file
/// in it so it cannot move, and make every staging refuse. Both DACLs were
/// read with icacls and are tested offline (CoexistCheck.exe offline OF8).
/// Neither folder is among those Controlled folder access protects by
/// default ("Protect folders from ransomware with controlled folder
/// access", on Learn). Every directory from <c>%ProgramFiles%\HIDMaestro</c>
/// down is created with, and on each use verified to carry, a protected
/// DACL that only Administrators and SYSTEM can write. The folder above
/// <c>Staging</c>, which HIDMaestro creates itself, is reset in place when
/// it passes the owner check and refused when it does not, since a hostile
/// one there is another program's. A staging directory below it,
/// <c>Staging</c> included, that a non-administrator could write is moved
/// aside and recreated, and the aside is left in place, never deleted by
/// path.</para>
///
/// <para>The OpenVR driver stays under
/// <c>%ProgramData%\HIDMaestro\openvr\hidmaestro</c> (<see cref="SharedDir"/>),
/// by decision, for two reasons. SteamVR loads the driver name
/// "hidmaestro" from one registered path only, and a second registration of
/// that name conflicts with the first. A v1.12.0 program running beside
/// this one registers the %ProgramData% path again whenever its manifest
/// hash differs from the one in HKLM, so a driver moved anywhere else would
/// be registered twice. That shared root and the OpenVR driver's folders
/// are reset where they are when SYSTEM or Administrators own them, as they
/// do when an elevated program created them, v1.12.0 included: SteamVR maps
/// the driver's files from there, and a directory holding a mapped file
/// cannot be renamed. One anyone else owns, or that lets anyone else change
/// its DACL or owner or delete it or its entries, is moved aside like a
/// staging directory, and refused when it cannot move. Its owner keeps
/// WRITE_DAC ("An object's owner implicitly has WRITE_DAC access to the
/// object", Owner of a New Object on Learn), and a principal granted those
/// rights keeps them, through a handle opened before any reset ("the
/// requested access is compared to the granted access",
/// ObReferenceObjectByHandle on Learn). So a program without elevation that
/// creates one of those folders first and holds a file under it can make
/// the OpenVR registration refuse, and so can any program that merely holds
/// one open for listing while the extraction renames a file into it, since
/// the folders stay readable by Users for SteamVR. Either way the refusal
/// names the folder (<see cref="BusyRefusal"/>). None of this can make a
/// staging refuse: staging is under %ProgramFiles%, where only an
/// administrator can create or hold a folder this way.</para>
///
/// <para>Every elevated use of a staged file runs inside a
/// <see cref="Pin"/>. <c>Staging</c> and each directory
/// below it down to the one in use are held open without delete sharing,
/// which no rename or delete of them can get past, and the folder above
/// <c>Staging</c> cannot be renamed while they are held. That folder, the
/// shared root and the OpenVR folders, which Users may be able to read, are
/// opened only to be checked, so that no program
/// without elevation can make a pin fail. Each directory is checked through
/// its handle. Each file HIDMaestro ships is held for reading with read
/// sharing only, which no write, rename or delete can get past, and checked
/// against its SHA-256 through the hold (<see cref="Pinned.Hold"/>). A
/// driver binary cannot be held while signtool rewrites it: it is checked
/// right before, and held once signed. A catalog is held once signed. The
/// pin lasts until the use is over. The signing tools, the driver package, the SWD helper
/// (<c>SwdDeviceFactory.PinHelper</c>), the usbip-win2 installer, its host
/// controller package and each WinUSB package take one. The OpenVR
/// extraction, which writes files for SteamVR and runs none, takes a pin
/// over folders Users can all read, so it checks them and holds none. A
/// change made between two uses is found at the next one. Every staged
/// file's SHA-256 is checked against the embedded or pinned hash after it
/// is written and again through its hold, so a file that does not match
/// the bytes HIDMaestro ships never runs.</para>
///
/// <para>Lock order. The staging lock (<see cref="StagingMutexObject"/>) is
/// a leaf. It is taken under the deploy lock (a deploy's prune, every
/// staging a deploy does, and a WinUSB package's), under the transport lock
/// (<c>UsbipDriverInstaller.TransportMutexObject</c>: the usbip-win2
/// installer's staging, the host controller package's, and the SWD
/// helper's when the host controller update stages it), or with no lock
/// held (a constructor's prewarm, the SWD helper's staging for a create or
/// a remove), and nothing takes another machine-wide lock while holding
/// it.
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
    /// signed and cataloged there. Only a holder of the deploy lock writes
    /// it, so the driver staging keeps the embedded bytes and another
    /// process staging them never touches a package being signed.</summary>
    internal const string OperationPackage = "package";

    // Staging tree: Administrators and SYSTEM full control, nobody else any
    // access. Protected (P) so it inherits nothing from the folder above it.
    // Owner Administrators. OICI so new files and subdirectories inherit it.
    private const string StagingSddl = "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
    // The shared root %ProgramData%\HIDMaestro holds the VR runtime that
    // SteamVR, running as the user, loads, so the user keeps read and
    // execute (FRFX) but no write.
    private const string RootSddl = "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FRFX;;;BU)";

    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    // NT SERVICE\TrustedInstaller, the service SID Windows derives from the
    // service name.
    private static readonly SecurityIdentifier TrustedInstallerSid =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    // Everything beyond read, execute and synchronize. A non-admin principal
    // granted any of these can change what later runs elevated.
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes |
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static readonly object s_lock = new();

    /// <summary><c>%ProgramFiles%\HIDMaestro</c>, the folder above the
    /// staging tree. Program Files lets no program without elevation create
    /// it first (see the class remarks).</summary>
    internal static string StagingParent => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HIDMaestro");

    /// <summary>The root of the staging tree,
    /// <c>%ProgramFiles%\HIDMaestro\Staging</c>.</summary>
    internal static string StagingRoot => Path.Combine(StagingParent, "Staging");

    /// <summary><c>%ProgramData%\HIDMaestro</c>, which holds the OpenVR
    /// driver for SteamVR.
    ///
    /// <para>Invariant: nothing the elevated SDK runs, loads or hands to
    /// pnputil, signtool or inf2cat may live under this root. A program
    /// without elevation can create it, or a folder under it, first, and it
    /// may be reset where it is (<see cref="Repair.InPlace"/>), which a pin
    /// does not make up for: a pin blocks a rename or delete, not a change of
    /// the DACL itself. Every file the elevated SDK uses is staged under
    /// <see cref="StagingRoot"/> through <see cref="DirFor"/>, and used under
    /// a <see cref="Pin"/>. The OpenVR files are loaded only by SteamVR,
    /// which runs without elevation.</para></summary>
    internal static string HidMaestroRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HIDMaestro");

    /// <summary>The <c>&lt;version&gt;-&lt;hash&gt;</c> directory name for
    /// this SDK build. The hash is the first 16 hex chars of the embedded
    /// driver payload's SHA-256, so two SDK builds with the same version but
    /// different payloads do not share a directory.</summary>
    internal static string VersionTag =>
        $"{MachineClaims.SdkVersion}-{EmbeddedManifest.Sha256Hex[..16]}";

    /// <summary>How the folder above <see cref="StagingRoot"/> is repaired:
    /// reset in place when it passes the owner check, otherwise refused,
    /// never moved aside, because HIDMaestro creates it itself and a hostile
    /// one there is another program's. A seam <see cref="DirFor"/> and the
    /// coexistence probe both read, so a change back to
    /// <see cref="Repair.MoveAside"/> shows in the test.</summary>
    internal static Repair StagingParentRepair => Repair.InPlaceOrRefuse;

    /// <summary>Return the protected directory for one operation, creating
    /// and repairing every directory on the path from
    /// <c>%ProgramFiles%\HIDMaestro</c> down. Requires elevation: creating a
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
            // Only an administrator can create anything under Program Files,
            // so none of these can be squatted. The folder above Staging is
            // reset where it is when it passes the owner check: one HIDMaestro
            // made, or one an installer made with the Program Files DACL,
            // which grants Users only read and execute. HIDMaestro creates
            // this folder itself when it is absent, so one an administrator
            // left hostile (owned by, or writable or deletable by, another
            // principal) belongs to another program. HIDMaestro refuses it
            // rather than move it aside and delete another product's files
            // (Repair.InPlaceOrRefuse). Staging and the version directory are
            // HIDMaestro's own names: a hostile one, which only an
            // administrator could have made here, is moved aside so a fresh
            // protected one can take its place, and the aside is left where it
            // is, never deleted by path (the prune and HostileReason).
            EnsureProtected(StagingParent, StagingSddl, StagingParentRepair);
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
    /// stored elsewhere and whose files SteamVR may hold open. The root and
    /// every level down to <paramref name="relative"/> get the root's DACL,
    /// Administrators/SYSTEM-write-only and user-readable: reset where they
    /// are when SYSTEM or Administrators own them, and otherwise moved aside,
    /// or refused when they cannot move (<see cref="Repair.InPlace"/>).
    /// Requires elevation.</summary>
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

    /// <summary>The directories and files a use of staged files holds open
    /// (<see cref="Pin"/>, <see cref="Pinned.Hold"/>) for as long as it lasts.
    /// Dispose releases them.</summary>
    internal sealed class Pinned : IDisposable
    {
        private readonly List<SafeFileHandle> _handles;
        internal Pinned(List<SafeFileHandle> handles) => _handles = handles;

        /// <summary>Hold <paramref name="file"/> (<see cref="HoldFile"/>) and
        /// check the SHA-256 read through the hold against
        /// <paramref name="expectedSha256"/>.</summary>
        /// <exception cref="InvalidOperationException">The file could not be
        /// held, or does not match.</exception>
        internal void Hold(string file, string expectedSha256)
        {
            var h = HoldFile(file, out string sha);
            if (!sha.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                h.Dispose();
                throw new InvalidOperationException(
                    $"{Path.GetFileName(file)} does not match its expected SHA-256. HIDMaestro will not use it.");
            }
            _handles.Add(h);
        }

        /// <summary>Hold <paramref name="file"/> (<see cref="HoldFile"/>) and
        /// return its SHA-256, read through the hold. For a file this use
        /// itself just wrote, such as a binary signtool signed.</summary>
        internal string HoldAndHash(string file)
        {
            var h = HoldFile(file, out string sha);
            _handles.Add(h);
            return sha;
        }

        public void Dispose()
        {
            foreach (var h in _handles) h.Dispose();
            _handles.Clear();
        }
    }

    /// <summary>Open every directory from <see cref="StagingParent"/>, or for
    /// the OpenVR folders from <see cref="HidMaestroRoot"/>, down to each of
    /// <paramref name="dirs"/> (<see cref="OpenLevel"/>), then
    /// check each through its handle: a plain directory owned by
    /// Administrators or SYSTEM that no other principal can write.
    ///
    /// <para>Two kinds of open, so that no program without elevation can
    /// make a pin fail. The root and the OpenVR folders grant Users read, so
    /// any program can hold them open without sharing. They are opened only
    /// for READ_CONTROL and FILE_READ_ATTRIBUTES, which the sharing check
    /// does not weigh: an open for those alone succeeds beside a handle that
    /// shares nothing (tested offline on a scratch directory,
    /// CoexistCheck.exe offline OF5). <c>Staging</c> and
    /// every directory below it grant SYSTEM and Administrators alone, so
    /// only an administrator can open them, and they are held for listing,
    /// sharing read and write but not delete. While held, no program can
    /// rename or delete one of them, since that needs an open for DELETE,
    /// which must be compatible with the sharing of the opens already there
    /// (CreateFileW, FILE_SHARE_DELETE, on Learn). Nor can the folder above
    /// be renamed while <c>Staging</c> is held below it: NTFS refuses to
    /// rename a directory that has an open handle under it (tested offline
    /// on a scratch directory, CoexistCheck.exe offline OF5).</para>
    ///
    /// <para>The caller staged and repaired the directories first: a repair
    /// moves a directory, which a pin would refuse. Files are held with
    /// <see cref="Pinned.Hold"/>.</para></summary>
    /// <exception cref="InvalidOperationException">A directory could not be
    /// opened, or is not protected once opened.</exception>
    internal static Pinned Pin(params string[] dirs)
    {
        string[] bases =
        {
            Path.GetFullPath(StagingParent).TrimEnd('\\'),
            Path.GetFullPath(HidMaestroRoot).TrimEnd('\\'),
        };
        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string d in dirs)
        {
            string full = Path.GetFullPath(d).TrimEnd('\\');
            string? root = null;
            foreach (string b in bases)
                if (full.Equals(b, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase))
                    root = b;
            if (root == null)
                throw new InvalidOperationException($"{d} is outside {bases[0]} and {bases[1]}.");
            string rel = Path.GetRelativePath(root, full);
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
                handles.Add(OpenLevel(dir, HoldsLevel(dir)));
            for (int i = 0; i < chain.Count; i++)
                if (!IsProtected(handles[i]))
                    throw new InvalidOperationException(
                        $"{chain[i]} changed between its repair and its use and is not Administrators/SYSTEM-write-only. HIDMaestro will not use the files under it.");
            return pinned;
        }
        catch
        {
            pinned.Dispose();
            throw;
        }
    }

    /// <summary>True for the levels a pin holds: <see cref="StagingRoot"/>
    /// and every directory below it, which only SYSTEM and Administrators
    /// can open. False for the levels Users can read, which a pin opens only
    /// to check (<see cref="OpenLevel"/>).</summary>
    internal static bool HoldsLevel(string dir)
    {
        string staging = Path.GetFullPath(StagingRoot).TrimEnd('\\');
        string full = Path.GetFullPath(dir).TrimEnd('\\');
        return full.Equals(staging, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(staging + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Pin the directory of <paramref name="file"/>, and each of
    /// <paramref name="moreDirs"/>, with every directory above them
    /// (<see cref="Pin"/>), then hold the file and check it against
    /// <paramref name="expectedSha256"/> through the hold
    /// (<see cref="Pinned.Hold"/>). The caller keeps the pin until its
    /// elevated use of the file is over: until a process it started has
    /// exited, or pnputil has added the package.</summary>
    /// <exception cref="InvalidOperationException">A directory or the file
    /// could not be opened, a directory is not protected, or the file does
    /// not match.</exception>
    internal static Pinned PinFile(string file, string expectedSha256, params string[] moreDirs)
    {
        var pin = Pin([Path.GetDirectoryName(file)!, .. moreDirs]);
        try
        {
            pin.Hold(file, expectedSha256);
            return pin;
        }
        catch
        {
            pin.Dispose();
            throw;
        }
    }

    /// <summary>Open one directory of a pin. Held
    /// (<paramref name="hold"/>): FILE_LIST_DIRECTORY, READ_CONTROL and
    /// FILE_READ_ATTRIBUTES, sharing read and write but not delete, so no
    /// program can rename or delete it while the handle is open. Not held:
    /// READ_CONTROL and FILE_READ_ATTRIBUTES only, which the sharing check
    /// does not weigh, so no handle another program holds can make the open
    /// fail, and the handle blocks nothing. Either way the directory itself
    /// is opened, never a link's target, and the handle can read its
    /// attributes and security (<see cref="Pin"/>).</summary>
    /// <exception cref="InvalidOperationException">The open failed.</exception>
    internal static SafeFileHandle OpenLevel(string dir, bool hold)
    {
        uint access = READ_CONTROL | FILE_READ_ATTRIBUTES | SYNCHRONIZE | (hold ? FILE_LIST_DIRECTORY : 0);
        uint share = FILE_SHARE_READ | FILE_SHARE_WRITE | (hold ? 0 : FILE_SHARE_DELETE);
        var h = CreateFileW(dir, access, share, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            h.Dispose();
            throw new InvalidOperationException(
                $"HIDMaestro could not {(hold ? "hold" : "open")} {dir} while it uses the files under it (Win32 error {err}).");
        }
        return h;
    }

    /// <summary>Open <paramref name="file"/> for reading, sharing read only,
    /// and return its SHA-256 read through that handle. While the handle is
    /// open no program can write, rename or delete the file, since each needs
    /// write or delete access the sharing denies, and a program can still
    /// read it, run it or load it. Only staged files are held, and only
    /// SYSTEM and Administrators can open those at all. A link is opened as
    /// itself, never followed.</summary>
    /// <exception cref="InvalidOperationException">The open failed.</exception>
    internal static SafeFileHandle HoldFile(string file, out string sha256)
    {
        var h = CreateFileW(file, GENERIC_READ, FILE_SHARE_READ, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            h.Dispose();
            throw new InvalidOperationException(
                $"HIDMaestro could not hold {file} while it uses it (Win32 error {err}).");
        }
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long offset = 0;
            int n;
            while ((n = RandomAccess.Read(h, buffer, offset)) > 0)
            {
                hash.AppendData(buffer, 0, n);
                offset += n;
            }
            sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return h;
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    /// <summary>True when the directory open as <paramref name="handle"/> is
    /// what <see cref="IsProtected(string)"/> requires, read through the
    /// handle, so the directory checked is the directory held.</summary>
    private static bool IsProtected(SafeFileHandle handle)
    {
        try
        {
            if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0) return false;
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorBinaryForm(
                new HandleSecurity(handle).GetSecurityDescriptorBinaryForm(),
                AccessControlSections.Owner | AccessControlSections.Access);
            return IsProtected(security);
        }
        catch { return false; }
    }

    /// <summary>The owner and DACL of an open handle.</summary>
    private sealed class HandleSecurity : ObjectSecurity<FileSystemRights>
    {
        internal HandleSecurity(System.Runtime.InteropServices.SafeHandle handle)
            : base(true, ResourceType.FileObject, handle, AccessControlSections.Owner | AccessControlSections.Access) { }
    }

    private const uint FILE_LIST_DIRECTORY = 0x0001, FILE_READ_ATTRIBUTES = 0x0080;
    private const uint READ_CONTROL = 0x00020000, SYNCHRONIZE = 0x00100000, GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000,
        FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

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
    /// <paramref name="expectedSha256"/>, read without holding it. Called
    /// right before a driver binary is signed, which rewrites it, so it
    /// cannot be held then. Every other staged file is checked through its
    /// hold (<see cref="Pinned.Hold"/>).</summary>
    internal static void VerifyBeforeUse(string path, string expectedSha256)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"{Path.GetFileName(path)} is not present in staging.");
        if (!Sha256OfFile(path).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} does not match its embedded SHA-256; refusing to use it.");
    }

    /// <summary>Throw unless the file at <paramref name="path"/> still matches
    /// the embedded resource it was staged from (see the overload
    /// above).</summary>
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
            if (!Directory.Exists(StagingRoot)) return;
            // Hold the staging tree for the whole prune. Pin opens the folder
            // above Staging and Staging itself and checks each through its
            // handle, so a StagingRoot that no DirFor repaired, or that is
            // not Administrators/SYSTEM-write-only, throws here and the prune
            // does nothing rather than delete children of a folder a
            // non-administrator could change. While it is held, no one can
            // rename or replace Staging itself. The pin does not stop a rename
            // of a child of Staging or a new entry in it: the Staging DACL
            // admits only Administrators and SYSTEM, and a child this prune
            // deletes is re-checked through a handle first and, if it was
            // swapped for a link, removed as the link, not followed.
            Pinned pin;
            try { pin = Pin(StagingRoot); }
            catch (Exception ex)
            {
                DeviceOrchestrator.LogDiag($"ProtectedStaging: prune skipped, staging not pinnable: {ex.Message}");
                return;
            }
            using var _pin = pin;
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
                if (!PruneWouldDelete(tag, liveTags, liveVersions)) continue;
                // One of HIDMaestro's own old version directories. Before the
                // recursive delete, open it without following a link and
                // confirm through that handle that it is a real directory
                // owned by Administrators or SYSTEM with no other principal
                // able to write it. Only Administrators and SYSTEM can swap an
                // entry here, and a link swapped in after the check is removed
                // as the link, not followed.
                DeleteOwnVersion(dir);
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

    /// <summary>Whether the prune deletes a child of <see cref="StagingRoot"/>
    /// named <paramref name="tag"/>. A moved-aside directory
    /// (<c>.untrusted-</c>) is never deleted: a repair moves one aside
    /// because a non-administrator may change it, and the rename keeps that
    /// grant, so its tree could redirect an elevated recursive delete through
    /// a link. This build's own directory, and one a live record names by its
    /// tag or its version, stay. Every other <c>&lt;version&gt;-&lt;hash&gt;</c>
    /// directory is HIDMaestro's own old staging and is deleted. A seam the
    /// loop and the coexistence probe both read.</summary>
    internal static bool PruneWouldDelete(string tag, ICollection<string> liveTags, ICollection<string> liveVersions)
    {
        if (tag.Contains(".untrusted-", StringComparison.Ordinal)) return false;
        if (tag.Equals(VersionTag, StringComparison.OrdinalIgnoreCase)) return false;
        int dash = tag.IndexOf('-');
        string version = dash > 0 ? tag[..dash] : tag;
        if (liveTags.Contains(tag) || liveVersions.Contains(version)) return false;
        return true;
    }

    /// <summary>Delete one of HIDMaestro's own old version directories after
    /// confirming, through a handle opened without following links, that it
    /// is a real directory only Administrators and SYSTEM can write. A
    /// version directory replaced by a link, or one a non-administrator can
    /// change, is left alone. Only Administrators and SYSTEM can rename or
    /// replace an entry under <see cref="StagingRoot"/>, and a link swapped in
    /// after the check is removed by the recursive delete as the link, not
    /// followed.</summary>
    private static void DeleteOwnVersion(string dir)
    {
        SafeFileHandle handle;
        try { handle = OpenLevel(dir, hold: true); }
        catch (Exception ex)
        {
            DeviceOrchestrator.LogDiag($"ProtectedStaging: prune left {Path.GetFileName(dir)}, could not open it: {ex.Message}");
            return;
        }
        bool ours;
        using (handle) ours = IsProtected(handle);
        if (!ours)
        {
            DeviceOrchestrator.LogDiag($"ProtectedStaging: prune left {Path.GetFileName(dir)}, not Administrators/SYSTEM-write-only or a link");
            return;
        }
        TryDelete(dir);
    }

    /// <summary>Recursively delete one of HIDMaestro's own staging
    /// directories: a version folder it created with the protected DACL,
    /// under %ProgramFiles%, which no non-administrator can change or plant a
    /// link in. Called only by <see cref="DeleteOwnVersion"/>, which checks
    /// it through a handle first, under a pin on <see cref="StagingRoot"/>.</summary>
    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { DeviceOrchestrator.LogDiag($"ProtectedStaging: could not prune {Path.GetFileName(dir)}: {ex.GetType().Name}"); }
    }

    // ── Directory protection ──────────────────────────────────────────────

    /// <summary>How an existing directory that is not protected is
    /// repaired.</summary>
    internal enum Repair
    {
        /// <summary>Moved aside and made again, or, when it cannot be moved,
        /// reset in place unless that would leave a stale handle with
        /// WRITE_DAC or DELETE (<see cref="HostileReason"/>). The staging
        /// tree.</summary>
        MoveAside,
        /// <summary>Reset where it is when SYSTEM or Administrators own it
        /// and no other principal may change its DACL or owner or delete it
        /// or its entries (<see cref="InPlaceRepairAllowed"/>), and otherwise moved aside as
        /// <see cref="MoveAside"/> does, refused when it cannot move. The
        /// shared root and the OpenVR folders, whose paths are stored
        /// elsewhere and whose files SteamVR may hold open: an elevated
        /// program creates them owned by Administrators, so the in-place
        /// arm covers them, and a directory anyone else owns leaves its
        /// owner WRITE_DAC through a handle opened before any reset.</summary>
        InPlace,
        /// <summary>Reset where it is when it passes the owner check
        /// (<see cref="InPlaceRepairAllowed"/>), and otherwise refused, never
        /// moved aside. For a directory HIDMaestro creates itself when it is
        /// absent, <see cref="StagingParent"/>: a hostile one there is another
        /// program's folder, so HIDMaestro neither replaces it nor deletes
        /// what is in it.</summary>
        InPlaceOrRefuse,
    }

    private static void EnsureProtected(string dir, string sddl, Repair repair)
    {
        if (Directory.Exists(dir))
        {
            if (IsProtected(dir)) return;
            if ((repair == Repair.InPlace || repair == Repair.InPlaceOrRefuse) && InPlaceRepairAllowed(dir))
            {
                // Owned by SYSTEM or Administrators, with no other principal
                // allowed to change its DACL or owner or to delete it or its
                // entries: only the DACL is wrong, as on a root an elevated
                // v1.12.0 created, which SteamVR may hold files under. No
                // principal but those can open it with WRITE_DAC now, so it
                // is reset where it is.
                Reset(dir, sddl);
                return;
            }
            if (repair == Repair.InPlaceOrRefuse)
                // HIDMaestro did not create this one, so it will not take it
                // over or move it aside and delete another product's files.
                throw new InvalidOperationException(ForeignRefusal(dir, HostileReason(dir)!));
            // An owner other than Administrators or SYSTEM has WRITE_DAC
            // implicitly ("An object's owner implicitly has WRITE_DAC access
            // to the object", Owner of a New Object on Learn), and a handle
            // keeps the access it was granted when it was opened. A reset
            // therefore leaves such a principal able to change the DACL back
            // through a handle it already holds, and the directory is moved
            // aside instead, wherever it sits. A move renames the directory
            // entry itself, a reparse point included.
            string? hostile = HostileReason(dir);
            if (TryMoveAside(dir, out string moveError))
            {
                CreateProtected(dir, sddl);
                return;
            }
            // Setting security on a reparse point would set it on the target
            // instead, and a hostile directory keeps its handles: neither is
            // used at all.
            if (hostile != null)
                throw new InvalidOperationException(Refusal(dir, hostile, moveError, sddl == RootSddl));
            // A move that could not be done, and only the DACL is wrong: no
            // principal but Administrators and SYSTEM can have opened it for
            // WRITE_DAC or WRITE_OWNER. Reset owner and DACL in place. The
            // files under it are still checked by hash under the pin at use.
            Reset(dir, sddl);
            return;
        }
        CreateProtected(dir, sddl);
    }

    /// <summary>True when an <see cref="Repair.InPlace"/> directory may be
    /// reset where it is: a plain directory owned by SYSTEM or
    /// Administrators that grants no principal but SYSTEM, Administrators
    /// and TrustedInstaller the right to change its DACL or owner, or to
    /// delete it or its entries. No other program can then hold a handle
    /// that keeps such access across the reset.</summary>
    internal static bool InPlaceRepairAllowed(string dir) => HostileReason(dir) == null;

    /// <summary>The rights that disqualify a reset in place when anyone but
    /// SYSTEM, Administrators or TrustedInstaller holds them: WRITE_DAC,
    /// WRITE_OWNER, DELETE and FILE_DELETE_CHILD. A handle opened with any
    /// of them keeps it after the DACL changes.</summary>
    private const FileSystemRights HostileRights =
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;

    /// <summary>Why a reset in place cannot make the directory safe, or null
    /// when it can: it is a reparse point, its owner is not Administrators or
    /// SYSTEM, or another principal holds one of <see cref="HostileRights"/>.
    /// Such a principal may hold a handle with that access open across the
    /// reset.</summary>
    private static string? HostileReason(string dir)
    {
        try
        {
            if (IsReparsePoint(dir))
                return "is a reparse point, which would send what HIDMaestro writes there wherever it points";
            var security = new DirectoryInfo(dir).GetAccessControl();
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null)
                return "has no owner HIDMaestro can read, so HIDMaestro cannot tell who may change it";
            if (!owner.Equals(AdminsSid) && !owner.Equals(SystemSid))
                return $"is owned by {NameOf(owner)}, not Administrators or SYSTEM, " +
                       "and an owner keeps the right to change its permissions through any handle it opened before a repair";
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.FileSystemRights & HostileRights) == 0) continue;
                if (rule.IdentityReference is SecurityIdentifier sid
                    && (sid.Equals(AdminsSid) || sid.Equals(SystemSid) || sid.Equals(TrustedInstallerSid)))
                    continue;
                return $"lets {NameOf(rule.IdentityReference)} change its permissions or owner, or delete it or its contents, " +
                       "and a handle opened with that right keeps it after a repair";
            }
            return null;
        }
        catch (Exception e) { return $"could not have its security read ({e.Message.TrimEnd('.')}), so HIDMaestro cannot tell who may change it"; }
    }

    private static string NameOf(IdentityReference id)
    {
        try { return id.Translate(typeof(NTAccount)).Value; }
        catch { return id.Value; }
    }

    /// <summary>The text of the refusal a staging throws for a directory it
    /// can neither reset in place nor move aside.</summary>
    internal static string Refusal(string dir, string reason, string moveError, bool openVrFolder) =>
        $"{dir} {reason}. It could not be moved aside ({moveError}), so HIDMaestro will not stage files under it. " +
        (openVrFolder
            ? "Close the programs using files under it, SteamVR among them, then try again. "
            : "Close the programs using files under it, then try again. ") +
        "Once they are closed, deleting the folder from an elevated prompt also clears it.";

    /// <summary>The refusal thrown when a protected directory could not be
    /// written because a program holds it or a file in it open, not because
    /// anyone could change its permissions. Names the folder and the
    /// remedy.</summary>
    internal static string BusyRefusal(string dir, string detail) =>
        $"{dir} is in use by another program, so HIDMaestro could not write to it ({detail}). " +
        "Close the programs using files under it, SteamVR among them, then try again.";

    /// <summary>The refusal thrown for a directory HIDMaestro creates
    /// itself when absent (<see cref="StagingParent"/>) but found owned
    /// or changeable by another principal: it is another program's, so
    /// HIDMaestro neither stages under it nor moves it aside.</summary>
    internal static string ForeignRefusal(string dir, string reason) =>
        $"{dir} {reason}. HIDMaestro creates this folder itself, so one it did not make belongs to another program, " +
        "and HIDMaestro will not stage under it or move it aside. Remove or repair that folder from an elevated prompt, then try again.";

    /// <summary>The text of the refusal a staging throws for a directory
    /// another program created, for a reason <see cref="HostileReason"/>
    /// gives, between HIDMaestro's check that it was missing and its own
    /// creation of it.</summary>
    internal static string RaceRefusal(string dir, string reason) =>
        $"{dir} was created by another program while HIDMaestro was creating it, and it {reason}. " +
        "HIDMaestro will not stage files under it. Close that program, then try again. " +
        "Once it is closed, deleting the folder from an elevated prompt also clears it.";

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
        if (HostileReason(dir) is string reason)
            throw new InvalidOperationException(RaceRefusal(dir, reason));
        Reset(dir, sddl);
    }

    private static void Reset(string dir, string sddl)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        new DirectoryInfo(dir).SetAccessControl(security);
    }

    private static bool TryMoveAside(string dir, out string error)
    {
        try
        {
            string aside = dir + ".untrusted-" + Guid.NewGuid().ToString("N");
            Directory.Move(dir, aside);
            DeviceOrchestrator.LogDiag($"ProtectedStaging: moved an unprotected {Path.GetFileName(dir)} aside to {Path.GetFileName(aside)}.");
            error = "";
            return true;
        }
        catch (Exception e)
        {
            error = e.Message.TrimEnd('.');
            return false;
        }
    }

    /// <summary>True when the directory is a plain directory, not a reparse
    /// point, owned by Administrators or SYSTEM, and no other principal is
    /// granted any write-class right.</summary>
    private static bool IsProtected(string dir)
    {
        try
        {
            if (IsReparsePoint(dir)) return false;
            return IsProtected(new DirectoryInfo(dir).GetAccessControl());
        }
        catch { return false; }
    }

    private static bool IsProtected(DirectorySecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || (!owner.Equals(AdminsSid) && !owner.Equals(SystemSid)))
            return false;

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if ((rule.FileSystemRights & WriteRights) == 0) continue;
            // TrustedInstaller holds full control of what Windows ships under
            // Program Files, and a folder another installer made there
            // inherits it.
            if (rule.IdentityReference is SecurityIdentifier sid
                && (sid.Equals(AdminsSid) || sid.Equals(SystemSid) || sid.Equals(TrustedInstallerSid)))
                continue;
            return false; // a non-admin principal can write
        }
        return true;
    }

    // ── Hashing ─────────────────────────────────────────────────────────

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
