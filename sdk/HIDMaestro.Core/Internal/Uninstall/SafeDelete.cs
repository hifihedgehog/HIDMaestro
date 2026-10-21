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
/// at its next start (MoveFileEx, MOVEFILE_DELAY_UNTIL_REBOOT) only inside
/// %ProgramFiles%\HIDMaestro, and only when that folder, checked through its
/// held handle, carries the staging DACL that lets only Administrators and
/// SYSTEM change it: Windows resolves that path again by name at the next
/// start, as SYSTEM. Anywhere else such an entry stays, and the removal
/// reports it Failed.</para>
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
    /// or resolves elsewhere.</summary>
    internal static Chain? OpenChain(string anchor, string relative, out string? refusal)
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
                var h = OpenRelative(chain.Last, part, FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
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
    /// path that does not exist is Removed. With
    /// <paramref name="deferInProtected"/>, an entry in use may wait for the
    /// next start when the target folder, checked through its handle, is
    /// HIDMaestro's protected staging parent.</summary>
    internal static (UninstallOutcome Outcome, string? Reason) Tree(string anchor, string relative, bool deferInProtected = false)
    {
        string full = System.IO.Path.Combine(System.IO.Path.GetFullPath(anchor).TrimEnd('\\'), relative);
        string parentRel = System.IO.Path.GetDirectoryName(relative) ?? "";
        string leaf = System.IO.Path.GetFileName(relative);
        using var parent = OpenChain(anchor, parentRel, out string? refusal);
        if (parent == null)
            return refusal == null ? (UninstallOutcome.Removed, null) : (UninstallOutcome.Failed, refusal);

        var state = new State();
        var target = OpenRelative(parent.Last, leaf, DELETE | READ_CONTROL | FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, 0, out int err);
        if (target == null)
        {
            if (err is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND) return (UninstallOutcome.Removed, null);
            return (UninstallOutcome.Failed, $"{full} could not be opened (Win32 error {err})");
        }
        using (target)
        {
            uint attrs = Attributes(target);
            if ((attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0 || (attrs & FILE_ATTRIBUTE_DIRECTORY) == 0)
            {
                // A file, or a link planted under HIDMaestro's name: it goes
                // as itself.
                Dispose(target, full, state);
            }
            else
            {
                string? final = FinalPath(target);
                if (final == null || !string.Equals(final.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return (UninstallOutcome.Failed, $"{full} resolves to {final ?? "a path that could not be read"}");
                state.MayDefer = deferInProtected && ProtectedStaging.IsProtectedFolder(target);
                Empty(target, full, state);
                Dispose(target, full, state);
            }
        }
        if (state.FirstError != null) return (UninstallOutcome.Failed, state.FirstError);
        if (state.Pending > 0)
            return (UninstallOutcome.RemovedAtRestart,
                $"{state.Pending} item(s) in use by another program are deleted when Windows next starts");
        return (UninstallOutcome.Removed, null);
    }

    /// <summary><see cref="Tree"/> for a full path, from the deepest trusted
    /// anchor that holds it: %ProgramFiles%, %ProgramData%, %SystemRoot%, or
    /// one of <paramref name="profiles"/>. A path under none of them is
    /// refused.</summary>
    internal static (UninstallOutcome Outcome, string? Reason) TreeUnder(string full, IEnumerable<string> profiles)
    {
        string path = System.IO.Path.GetFullPath(full).TrimEnd('\\');
        string? best = null;
        var anchors = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        };
        anchors.AddRange(profiles);
        foreach (string a in anchors)
        {
            if (string.IsNullOrEmpty(a)) continue;
            string root = System.IO.Path.GetFullPath(a).TrimEnd('\\');
            if (path.Length > root.Length + 1 && path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                && (best == null || root.Length > best.Length))
                best = root;
        }
        if (best == null) return (UninstallOutcome.Failed, $"{path} is not under a folder the removal starts from");
        return Tree(best, path[(best.Length + 1)..], deferInProtected: true);
    }

    private sealed class State
    {
        public int Pending;
        public bool MayDefer;
        public string? FirstError;
        public void Fail(string path, string what) => FirstError ??= $"{path}: {what}";
    }

    /// <summary>Every entry of the held folder, each opened relative to it,
    /// as itself.</summary>
    private static void Empty(SafeFileHandle dir, string dirPath, State state)
    {
        List<Entry> entries;
        try { entries = List(dir); }
        catch (Exception e) { state.Fail(dirPath, e.Message); return; }
        foreach (var e in entries)
        {
            string path = System.IO.Path.Combine(dirPath, e.Name);
            bool folder = e.Directory && !e.Link;
            var h = OpenRelative(dir, e.Name,
                DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE | (folder ? FILE_LIST_DIRECTORY : 0),
                FILE_SHARE_READ | FILE_SHARE_WRITE | (folder ? 0 : FILE_SHARE_DELETE), 0, out int err);
            if (h == null)
            {
                if (err is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND) continue;
                if (err is ERROR_SHARING_VIOLATION or ERROR_ACCESS_DENIED) { Defer(path, state, $"in use (Win32 error {err})"); continue; }
                state.Fail(path, $"could not be opened (Win32 error {err})");
                continue;
            }
            using (h)
            {
                uint attrs = Attributes(h);
                // Listed as a folder but a link by the time it was opened:
                // the open did not follow it, and it goes as a link.
                if ((attrs & FILE_ATTRIBUTE_DIRECTORY) != 0 && (attrs & FILE_ATTRIBUTE_REPARSE_POINT) == 0)
                    Empty(h, path, state);
                Dispose(h, path, state);
            }
        }
    }

    /// <summary>Mark the entry open as <paramref name="h"/> for deletion.
    /// One Windows will not delete now, such as a DLL another process has
    /// mapped, or a folder that still holds such an entry, waits for the next
    /// start where that is allowed, and fails otherwise.</summary>
    private static void Dispose(SafeFileHandle h, string path, State state)
    {
        int err = MarkDeleted(h);
        if (err == 0) return;
        if (err is ERROR_ACCESS_DENIED or ERROR_SHARING_VIOLATION or ERROR_DIR_NOT_EMPTY)
        {
            h.Dispose();
            Defer(path, state, err == ERROR_DIR_NOT_EMPTY ? "holds an entry in use" : $"in use (Win32 error {err})");
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

    private static void Defer(string path, State state, string why)
    {
        if (!state.MayDefer)
        {
            state.Fail(path, $"{why}. Close the program using it and run the removal again");
            return;
        }
        if (MoveFileExW(path, null, MOVEFILE_DELAY_UNTIL_REBOOT)) state.Pending++;
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
