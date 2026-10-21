using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HIDMaestro.Internal.Uninstall;

/// <summary>
/// A full removal's file and folder work in places a program without
/// elevation can change: each account's %TEMP%, %ProgramData%\HIDMaestro,
/// another account's OpenVR folder. A path there is never resolved by name
/// below a trusted anchor (a profile root, %ProgramData%, %SystemRoot%,
/// %ProgramFiles%). The anchor is opened and checked, then each folder below
/// it is opened relative to the folder above it (NtCreateFile with a root
/// directory handle) as itself (FILE_OPEN_REPARSE_POINT), refused when it is
/// a reparse point, and held without delete sharing, which no rename or
/// replacement of it can get past. The held folder's final path
/// (GetFinalPathNameByHandle) must be the expected one. Below that, a folder
/// is listed through its handle (GetFileInformationByHandleEx,
/// FileFullDirectoryInfo), and each entry is opened relative to that handle
/// and deleted through its own handle (FileDispositionInfoEx with POSIX
/// semantics and IGNORE_READONLY_ATTRIBUTE). An entry that is a link is
/// deleted as the link.
///
/// <para>An entry another program holds open is handed to Windows to delete
/// at its next start (MoveFileEx, MOVEFILE_DELAY_UNTIL_REBOOT) only when the
/// caller allows it, which a full removal does for %ProgramFiles%\HIDMaestro
/// alone, and only when every folder below the anchor, down to the one that
/// holds the entry, each checked through its own held handle, carries the
/// staging DACL that lets only Administrators and SYSTEM change it: Windows
/// resolves that path again by name at the next start, as SYSTEM. A folder
/// that cannot be opened for delete, the target included, is emptied through
/// a handle opened without DELETE before it is queued, since Windows deletes
/// only an empty folder then. A folder that keeps a child that could not go
/// is not queued. Anywhere else such an entry stays, and the removal reports
/// it Failed.</para>
///
/// <para>.NET's recursive Directory.Delete also removes a name-surrogate
/// link without recursing into it (FileSystem.Windows.cs,
/// RemoveDirectoryRecursive, in dotnet/runtime), but it resolves each entry
/// by path from the top and stops on the first entry in use, which is why
/// this walk is its own.</para>
/// </summary>
internal static partial class SafeDelete
{
    /// <summary>Folders opened from an anchor down, held, each checked
    /// through its handle. <see cref="Last"/> is the deepest.</summary>
    internal sealed class Chain : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = new();
        internal SafeFileHandle Last => _handles[^1];
        /// <summary>How many folders below the anchor the chain holds.</summary>
        internal int Depth => _handles.Count - 1;
        /// <summary>Whether every folder below the anchor carries the staging
        /// DACL, each read through its own handle. The chain must have been
        /// opened with READ_CONTROL. True for a chain of the anchor alone.</summary>
        internal bool BelowAnchorProtected()
        {
            for (int i = 1; i < _handles.Count; i++)
                if (!ProtectedStaging.IsProtectedFolder(_handles[i])) return false;
            return true;
        }
        internal string Path { get; }
        internal Chain(string path) => Path = path;
        internal void Add(SafeFileHandle h) => _handles.Add(h);
        public void Dispose()
        {
            foreach (var h in _handles) h.Dispose();
            _handles.Clear();
        }
    }

    /// <summary>Open <paramref name="anchor"/> and every folder of
    /// <paramref name="relative"/> below it, each as itself and held, and
    /// confirm the deepest one's final path. Null with
    /// <paramref name="refusal"/> null when a folder on the way does not
    /// exist, and null with the reason when one is a link, cannot be opened,
    /// or resolves elsewhere. With <paramref name="readControl"/> each folder
    /// below the anchor is also opened for its security descriptor
    /// (<see cref="Chain.BelowAnchorProtected"/>).</summary>
    internal static Chain? OpenChain(string anchor, string relative, out string? refusal, bool readControl = false)
    {
        refusal = null;
        string full = System.IO.Path.GetFullPath(anchor).TrimEnd('\\');
        var chain = new Chain(relative.Length == 0 ? full : System.IO.Path.Combine(full, relative));
        try
        {
            var root = CreateFileW(full, FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (root.IsInvalid)
            {
                int err = Marshal.GetLastPInvokeError();
                root.Dispose();
                if (err is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND) { chain.Dispose(); return null; }
                refusal = $"{full} could not be opened (Win32 error {err})";
                chain.Dispose();
                return null;
            }
            chain.Add(root);
            if (IsLink(root)) { refusal = $"{full} is a link, which HIDMaestro does not follow"; chain.Dispose(); return null; }
            string at = full;
            foreach (string part in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                at = System.IO.Path.Combine(at, part);
                var h = OpenRelative(chain.Last, part, FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE | (readControl ? READ_CONTROL : 0),
                    FILE_SHARE_READ | FILE_SHARE_WRITE, FILE_DIRECTORY_FILE, out int err);
                if (h == null)
                {
                    if (err is not (ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND))
                        refusal = $"{at} could not be opened (Win32 error {err})";
                    chain.Dispose();
                    return null;
                }
                chain.Add(h);
                if (IsLink(h)) { refusal = $"{at} is a link, which HIDMaestro does not follow"; chain.Dispose(); return null; }
            }
            string? final = FinalPath(chain.Last);
            if (final == null || !string.Equals(final.TrimEnd('\\'), chain.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                refusal = $"{chain.Path} resolves to {final ?? "a path that could not be read"}";
                chain.Dispose();
                return null;
            }
            return chain;
        }
        catch
        {
            chain.Dispose();
            throw;
        }
    }

    /// <summary>One entry of a folder listed through its handle.</summary>
    internal readonly record struct Entry(string Name, bool Directory, bool Link);

    /// <summary>The entries of the folder open as <paramref name="dir"/>,
    /// without "." and "..".</summary>
    internal static List<Entry> List(SafeFileHandle dir)
    {
        var list = new List<Entry>();
        var buffer = new byte[64 * 1024];
        int infoClass = FileFullDirectoryRestartInfo;
        while (true)
        {
            if (!GetFileInformationByHandleEx(dir, infoClass, buffer, (uint)buffer.Length))
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == ERROR_NO_MORE_FILES) return list;
                throw new IOException($"The folder could not be listed (Win32 error {err}).");
            }
            infoClass = FileFullDirectoryInfo;
            int offset = 0;
            while (true)
            {
                int next = BitConverter.ToInt32(buffer, offset);
                uint attrs = BitConverter.ToUInt32(buffer, offset + 56);
                int nameBytes = BitConverter.ToInt32(buffer, offset + 60);
                string name = System.Text.Encoding.Unicode.GetString(buffer, offset + 68, nameBytes);
                if (name != "." && name != "..")
                    list.Add(new Entry(name, (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0, (attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0));
                if (next == 0) break;
                offset += next;
            }
        }
    }

    /// <summary>Delete <paramref name="relative"/> below
    /// <paramref name="anchor"/>, a file or a folder and everything under it.
    /// Removed when nothing is left, RemovedAtRestart when an entry in use
    /// waits for the next start, Failed with the first problem otherwise. A
    /// path that does not exist is Removed. Names are used exactly as given,
    /// a trailing dot or space included. With
    /// <paramref name="deferInProtected"/>, an entry in use may wait for the
    /// next start when every folder below the anchor down to it, each checked
    /// through its own handle, carries the staging DACL
    /// (ProtectedStaging.IsProtectedFolder).</summary>
    internal static (UninstallOutcome Outcome, string? Reason) Tree(string anchor, string relative, bool deferInProtected = false)
    {
        string full = System.IO.Path.Combine(System.IO.Path.GetFullPath(anchor).TrimEnd('\\'), relative);
        try { return TreeCore(anchor, relative, full, deferInProtected); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An attribute or security read that failed on a handle: the
            // entry and what is under it stay, and the caller's walk goes on.
            return (UninstallOutcome.Failed, $"{full}: {e.Message}");
        }
    }

    private static (UninstallOutcome Outcome, string? Reason) TreeCore(string anchor, string relative, string full, bool deferInProtected)
    {
        int cut = relative.LastIndexOf('\\');
        string parentRel = cut < 0 ? "" : relative[..cut];
        string leaf = cut < 0 ? relative : relative[(cut + 1)..];
        using var parent = OpenChain(anchor, parentRel, out string? refusal, readControl: deferInProtected);
        if (parent == null)
            return refusal == null ? (UninstallOutcome.Removed, null) : (UninstallOutcome.Failed, refusal);

        // Windows resolves a deferred delete by name at the next start, from
        // the drive down. Every folder between the anchor and the target
        // must then be one only administrators can change.
        bool chainProtected = deferInProtected && parent.BelowAnchorProtected();
        // A file or link waits for the next start only inside such a folder
        // below the anchor.
        bool leafMayDefer = chainProtected && parent.Depth > 0;

        var state = new State();
        var target = OpenRelative(parent.Last, leaf, DELETE | READ_CONTROL | FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, 0, out int err);
        if (target == null)
        {
            if (err is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND) return (UninstallOutcome.Removed, null);
            if (err is not (ERROR_SHARING_VIOLATION or ERROR_ACCESS_DENIED))
                return (UninstallOutcome.Failed, $"{full} could not be opened (Win32 error {err})");
            // Held open by another program, or refusing DELETE: a folder is
            // emptied and queued as one below it would be, and a file or
            // link is queued where that is allowed.
            if (IsRealFolder(parent.Last, leaf)) EmptyHeldElsewhere(parent.Last, leaf, full, state, chainProtected, err);
            else Defer(full, state, leafMayDefer, $"in use (Win32 error {err})");
            return Outcome(state);
        }
        using (target)
        {
            uint attrs = Attributes(target);
            if ((attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0 || (attrs & FILE_ATTRIBUTE_DIRECTORY) == 0)
            {
                // A file, or a link planted under HIDMaestro's name: it goes
                // as itself.
                Dispose(target, full, state, leafMayDefer);
            }
            else
            {
                string? final = FinalPath(target);
                if (final == null || !string.Equals(final.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return (UninstallOutcome.Failed, $"{full} resolves to {final ?? "a path that could not be read"}");
                bool mayDefer = chainProtected && ProtectedStaging.IsProtectedFolder(target);
                Empty(target, full, state, mayDefer);
                if (state.Errors == 0) Dispose(target, full, state, mayDefer);
            }
        }
        return Outcome(state);
    }

    /// <summary>Whether <paramref name="name"/> in the held folder is a
    /// folder and not a link, read through a handle that asks for no access
    /// another program's open can refuse.</summary>
    private static bool IsRealFolder(SafeFileHandle dir, string name)
    {
        using var h = OpenRelative(dir, name, FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, 0, out _);
        if (h == null) return false;
        uint attrs = Attributes(h);
        return (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0 && (attrs & FILE_ATTRIBUTE_REPARSE_POINT) == 0;
    }

    private static (UninstallOutcome Outcome, string? Reason) Outcome(State state)
    {
        if (state.FirstError != null) return (UninstallOutcome.Failed, state.FirstError);
        if (state.Pending > 0)
            return (UninstallOutcome.RemovedAtRestart,
                $"{state.Pending} item(s) in use by another program are deleted when Windows next starts");
        return (UninstallOutcome.Removed, null);
    }

    /// <summary>The anchors a full path may be walked from:
    /// %ProgramFiles%, %ProgramData%, %SystemRoot%, the Public profile
    /// folder (ProfileList's Public value, else %PUBLIC%), and
    /// <paramref name="profiles"/>.</summary>
    internal static List<string> Anchors(IEnumerable<string> profiles)
    {
        var anchors = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        };
        if (PublicFolder() is { } pub) anchors.Add(pub);
        anchors.AddRange(profiles);
        return anchors;
    }

    /// <summary>The Public profile folder, which ProfileList keeps as a
    /// value rather than a SID subkey: the folder of the all-users desktop
    /// and Start menu shortcuts.</summary>
    private static string? PublicFolder()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (k?.GetValue("Public") is string p && p.Length > 0) return p;
        }
        catch { }
        string? env = Environment.GetEnvironmentVariable("PUBLIC");
        return string.IsNullOrEmpty(env) ? null : env;
    }

    /// <summary><see cref="Tree"/> for a full path, from the deepest anchor
    /// that holds it (<see cref="Anchors"/>). A path under none of them is
    /// refused. The path is taken as given: a trailing dot or space in a name
    /// stays part of it. An entry in use may wait for the next start only
    /// inside %ProgramFiles%\HIDMaestro, as for the full removal's own
    /// folders.</summary>
    internal static (UninstallOutcome Outcome, string? Reason) TreeUnder(string full, IEnumerable<string> profiles)
    {
        string path = Normalize(System.IO.Path.IsPathFullyQualified(full) ? full : System.IO.Path.GetFullPath(full));
        string ownProgramFiles = System.IO.Path.Combine(
            System.IO.Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).TrimEnd('\\'), "HIDMaestro");
        bool inOwnProgramFiles = path.StartsWith(ownProgramFiles + "\\", StringComparison.OrdinalIgnoreCase);
        string? best = null;
        foreach (string a in Anchors(profiles))
        {
            if (string.IsNullOrEmpty(a)) continue;
            string root = System.IO.Path.GetFullPath(a).TrimEnd('\\');
            if (path.Length > root.Length + 1 && path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                && (best == null || root.Length > best.Length))
                best = root;
        }
        if (best == null) return (UninstallOutcome.Failed, $"{path} is not under a folder the removal starts from");
        return Tree(best, path[(best.Length + 1)..], deferInProtected: inOwnProgramFiles);
    }

    /// <summary>A fully qualified path with '/' read as '\', empty and "."
    /// segments dropped and ".." taking the segment before it, and every
    /// other name kept exactly, a trailing dot or space included, which
    /// Path.GetFullPath would trim.</summary>
    internal static string Normalize(string path)
    {
        path = path.Replace('/', '\\');
        string prefix = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\" : "";
        var parts = new List<string>();
        foreach (string part in path[prefix.Length..].Split('\\'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..") { if (parts.Count > 1) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return prefix + string.Join('\\', parts);
    }

    /// <summary>Whether <paramref name="relative"/> below
    /// <paramref name="anchor"/> is itself a link, its parents being real
    /// folders: a link a removal then deletes as the link.</summary>
    internal static bool LeafIsLink(string anchor, string relative)
    {
        int cut = relative.LastIndexOf('\\');
        using var parent = OpenChain(anchor, cut < 0 ? "" : relative[..cut], out _);
        if (parent == null) return false;
        using var h = OpenRelative(parent.Last, cut < 0 ? relative : relative[(cut + 1)..], FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, 0, out _);
        try { return h != null && IsLink(h); }
        catch (IOException) { return false; }
    }

    private sealed class State
    {
        public int Pending;
        public int Errors;
        public string? FirstError;
        public void Fail(string path, string what)
        {
            Errors++;
            FirstError ??= $"{path}: {what}";
        }
    }

    /// <summary>Every entry of the held folder, each opened relative to it,
    /// as itself. <paramref name="mayDefer"/> says whether every folder from
    /// the target down to this one carries the staging DACL. A subfolder
    /// passes it on only when its own handle shows that DACL too.</summary>
    private static void Empty(SafeFileHandle dir, string dirPath, State state, bool mayDefer)
    {
        List<Entry> entries;
        try { entries = List(dir); }
        catch (Exception e) { state.Fail(dirPath, e.Message); return; }
        foreach (var e in entries)
        {
            string path = System.IO.Path.Combine(dirPath, e.Name);
            bool folder = e.Directory && !e.Link;
            var h = OpenRelative(dir, e.Name,
                DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE | (folder ? FILE_LIST_DIRECTORY | READ_CONTROL : 0),
                FILE_SHARE_READ | FILE_SHARE_WRITE | (folder ? 0 : FILE_SHARE_DELETE), 0, out int err);
            if (h == null)
            {
                if (err is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND) continue;
                if (err is ERROR_SHARING_VIOLATION or ERROR_ACCESS_DENIED)
                {
                    if (folder) EmptyHeldElsewhere(dir, e.Name, path, state, mayDefer, err);
                    else Defer(path, state, mayDefer, $"in use (Win32 error {err})");
                    continue;
                }
                state.Fail(path, $"could not be opened (Win32 error {err})");
                continue;
            }
            using (h)
            {
                uint attrs = Attributes(h);
                // Listed as a folder but a link by the time it was opened:
                // the open did not follow it, and it goes as a link.
                bool realFolder = (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0 && (attrs & FILE_ATTRIBUTE_REPARSE_POINT) == 0;
                bool below = realFolder && mayDefer && ProtectedStaging.IsProtectedFolder(h);
                int before = state.Errors;
                if (realFolder) Empty(h, path, state, below);
                // A folder that keeps a child that could not go stays: it
                // is not queued behind it.
                if (state.Errors != before) continue;
                Dispose(h, path, state, realFolder ? below : mayDefer);
            }
        }
    }

    /// <summary>A subfolder another program holds open without delete
    /// sharing, or that refuses DELETE: Windows deletes only an empty folder
    /// at its next start, so it is emptied through a handle opened without
    /// DELETE, then queued. One that cannot be emptied fails.</summary>
    private static void EmptyHeldElsewhere(SafeFileHandle dir, string name, string path, State state, bool mayDefer, int openError)
    {
        using var h = OpenRelative(dir, name, FILE_LIST_DIRECTORY | READ_CONTROL | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, FILE_DIRECTORY_FILE, out int err);
        if (h == null)
        {
            state.Fail(path, $"in use (Win32 error {openError}) and could not be opened to empty it (Win32 error {err})");
            return;
        }
        if (IsLink(h))
        {
            state.Fail(path, $"in use (Win32 error {openError}), and a link");
            return;
        }
        bool below = mayDefer && ProtectedStaging.IsProtectedFolder(h);
        // A child that could not go keeps the folder from being queued,
        // whatever failed before it.
        int before = state.Errors;
        Empty(h, path, state, below);
        if (state.Errors != before) return;
        Defer(path, state, below, $"in use (Win32 error {openError})");
    }

    /// <summary>Mark the entry open as <paramref name="h"/> for deletion.
    /// One Windows will not delete now, such as a DLL another process has
    /// mapped, or a folder that still holds such an entry, waits for the next
    /// start where <paramref name="mayDefer"/> allows it, and fails
    /// otherwise.</summary>
    private static void Dispose(SafeFileHandle h, string path, State state, bool mayDefer)
    {
        int err = MarkDeleted(h);
        if (err == 0) return;
        if (err is ERROR_ACCESS_DENIED or ERROR_SHARING_VIOLATION or ERROR_DIR_NOT_EMPTY)
        {
            h.Dispose();
            Defer(path, state, mayDefer, err == ERROR_DIR_NOT_EMPTY ? "holds an entry in use" : $"in use (Win32 error {err})");
            return;
        }
        state.Fail(path, $"could not be deleted (Win32 error {err})");
    }

    private static int MarkDeleted(SafeFileHandle h)
    {
        uint flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
                     | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE;
        if (SetFileInformationByHandle(h, FileDispositionInfoEx, ref flags, sizeof(uint))) return 0;
        int err = Marshal.GetLastPInvokeError();
        // A file system without the extended class (FAT, or Windows before
        // 1809) takes the plain one.
        if (err is not (ERROR_INVALID_PARAMETER or ERROR_NOT_SUPPORTED or ERROR_INVALID_FUNCTION)) return err;
        uint delete = 1;
        if (SetFileInformationByHandle(h, FileDispositionInfo, ref delete, sizeof(uint))) return 0;
        return Marshal.GetLastPInvokeError();
    }

    /// <summary>Queue <paramref name="path"/> for deletion at the next start
    /// where allowed. MoveFileEx takes a Win32 path, so the \\?\ form keeps a
    /// trailing dot or space and a long path exactly.</summary>
    private static void Defer(string path, State state, bool mayDefer, string why)
    {
        if (!mayDefer)
        {
            state.Fail(path, $"{why}. Close the program using it and run the removal again");
            return;
        }
        if (MoveFileExW(@"\\?\" + path, null, MOVEFILE_DELAY_UNTIL_REBOOT)) state.Pending++;
        else state.Fail(path, $"{why}, and could not be queued for deletion at the next start (Win32 error {Marshal.GetLastPInvokeError()})");
    }

    internal static bool IsLink(SafeFileHandle h) => (Attributes(h) & FILE_ATTRIBUTE_REPARSE_POINT) != 0;

    /// <summary>The attributes of the object open as <paramref name="h"/>,
    /// read through the handle (FileAttributeTagInfo).</summary>
    private static uint Attributes(SafeFileHandle h)
    {
        var info = new byte[8];
        if (!GetFileInformationByHandleEx(h, FileAttributeTagInfo, info, (uint)info.Length))
            throw new IOException($"The attributes could not be read (Win32 error {Marshal.GetLastPInvokeError()}).");
        return BitConverter.ToUInt32(info, 0);
    }

    /// <summary>The handle's final path without the \\?\ prefix, or null
    /// when it cannot be read or is not on a local drive.</summary>
    internal static unsafe string? FinalPath(SafeFileHandle h)
    {
        var buf = new char[1024];
        uint n;
        fixed (char* p = buf) n = GetFinalPathNameByHandleW(h, p, (uint)buf.Length, 0);
        if (n == 0 || n >= buf.Length) return null;
        string s = new string(buf, 0, (int)n);
        if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return null;
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s.Substring(4) : s;
    }

    /// <summary>Open <paramref name="name"/>, one path component, relative to
    /// the folder open as <paramref name="dir"/>, as itself
    /// (FILE_OPEN_REPARSE_POINT), for synchronous use. Null with the Win32
    /// error when the open fails.</summary>
    internal static unsafe SafeFileHandle? OpenRelative(SafeFileHandle dir, string name, uint access, uint share, uint options, out int error)
    {
        error = 0;
        if (name.Length == 0 || name.Contains('\\') || name.Contains('/') || name is "." or "..")
        {
            error = ERROR_INVALID_PARAMETER;
            return null;
        }
        bool added = false;
        try
        {
            dir.DangerousAddRef(ref added);
            fixed (char* p = name)
            {
                var us = new UNICODE_STRING
                {
                    Length = (ushort)(name.Length * 2),
                    MaximumLength = (ushort)(name.Length * 2),
                    Buffer = (IntPtr)p,
                };
                var oa = new OBJECT_ATTRIBUTES
                {
                    Length = (uint)sizeof(OBJECT_ATTRIBUTES),
                    RootDirectory = dir.DangerousGetHandle(),
                    ObjectName = (IntPtr)(&us),
                    Attributes = OBJ_CASE_INSENSITIVE,
                };
                int status = NtCreateFile(out IntPtr handle, access | SYNCHRONIZE, ref oa, out _, IntPtr.Zero, 0, share,
                    FILE_OPEN, options | FILE_OPEN_REPARSE_POINT | FILE_SYNCHRONOUS_IO_NONALERT | FILE_OPEN_FOR_BACKUP_INTENT,
                    IntPtr.Zero, 0);
                if (status < 0)
                {
                    error = (int)RtlNtStatusToDosError(status);
                    return null;
                }
                return new SafeFileHandle(handle, ownsHandle: true);
            }
        }
        finally
        {
            if (added) dir.DangerousRelease();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OBJECT_ATTRIBUTES
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_STATUS_BLOCK
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    internal const uint DELETE = 0x00010000, READ_CONTROL = 0x00020000, SYNCHRONIZE = 0x00100000;
    internal const uint FILE_LIST_DIRECTORY = 0x0001, FILE_READ_DATA = 0x0001, FILE_READ_ATTRIBUTES = 0x0080;
    internal const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    // ntifs.h / winternl.h: NtCreateFile's disposition and options, and
    // OBJ_CASE_INSENSITIVE.
    private const uint FILE_OPEN = 1;
    internal const uint FILE_DIRECTORY_FILE = 0x1, FILE_NON_DIRECTORY_FILE = 0x40;
    private const uint FILE_SYNCHRONOUS_IO_NONALERT = 0x20, FILE_OPEN_FOR_BACKUP_INTENT = 0x4000, FILE_OPEN_REPARSE_POINT = 0x200000;
    private const uint OBJ_CASE_INSENSITIVE = 0x40;
    // minwinbase.h FILE_INFO_BY_HANDLE_CLASS.
    private const int FileDispositionInfo = 4, FileAttributeTagInfo = 9, FileFullDirectoryInfo = 14,
        FileFullDirectoryRestartInfo = 15, FileDispositionInfoEx = 21;
    private const uint FILE_DISPOSITION_FLAG_DELETE = 0x1, FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x2,
        FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;
    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;
    private const int ERROR_INVALID_FUNCTION = 1, ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3,
        ERROR_ACCESS_DENIED = 5, ERROR_NO_MORE_FILES = 18, ERROR_SHARING_VIOLATION = 32, ERROR_NOT_SUPPORTED = 50,
        ERROR_INVALID_PARAMETER = 87, ERROR_DIR_NOT_EMPTY = 145;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("ntdll.dll")]
    private static partial int NtCreateFile(out IntPtr fileHandle, uint desiredAccess, ref OBJECT_ATTRIBUTES objectAttributes,
        out IO_STATUS_BLOCK ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess,
        uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);

    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, [Out] byte[] info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref uint info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle file, char* path, uint size, uint flags);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileExW(string existing, string? newName, uint flags);
}
