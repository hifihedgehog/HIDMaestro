using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HIDMaestro.Internal.Uninstall;

/// <summary>
/// A full removal's file and folder deletes. Some of what it deletes lies in
/// folders a program without elevation can change, such as each account's
/// %TEMP%, so nothing here follows a link: every entry is opened as itself
/// (FILE_FLAG_OPEN_REPARSE_POINT), a link is deleted as the link, and a
/// folder is held open without delete sharing while its entries go, so no
/// rename or replacement of it can redirect the walk. Each entry is deleted
/// through its own handle (SetFileInformationByHandle, FileDispositionInfoEx
/// with POSIX semantics, which takes the name away at once, and
/// IGNORE_READONLY_ATTRIBUTE). An entry another program holds open is handed
/// to Windows to delete when it next starts (MoveFileEx with
/// MOVEFILE_DELAY_UNTIL_REBOOT), and so is its folder after it.
///
/// <para>.NET's recursive Directory.Delete also removes a name-surrogate
/// link without recursing into it (FileSystem.Windows.cs,
/// RemoveDirectoryRecursive, in dotnet/runtime), but it stops on the first
/// entry in use and resolves each entry by path, which is why this walk is
/// its own.</para>
/// </summary>
internal static partial class SafeDelete
{
    /// <summary>Delete <paramref name="path"/>, a file or a folder and
    /// everything under it. Removed when nothing is left, RemovedAtRestart
    /// when an entry in use waits for the next start, Failed with the first
    /// error otherwise. A path that does not exist is Removed.</summary>
    internal static (UninstallOutcome Outcome, string? Reason) Tree(string path)
    {
        var state = new State();
        uint attrs = GetFileAttributesW(path);
        if (attrs == INVALID_FILE_ATTRIBUTES)
        {
            int err = Marshal.GetLastPInvokeError();
            if (err is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND) return (UninstallOutcome.Removed, null);
            return (UninstallOutcome.Failed, $"could not be read (Win32 error {err})");
        }
        bool dir = (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0;
        if (dir && (attrs & FILE_ATTRIBUTE_REPARSE_POINT) == 0) DeleteFolder(path, state);
        else DeleteEntry(path, dir, state);
        if (state.FirstError != null) return (UninstallOutcome.Failed, state.FirstError);
        if (state.Pending > 0)
            return (UninstallOutcome.RemovedAtRestart,
                $"{state.Pending} item(s) in use by another program are deleted when Windows next starts");
        return (UninstallOutcome.Removed, null);
    }

    /// <summary>Whether <paramref name="path"/> names anything, a link
    /// included, without following it.</summary>
    internal static bool Exists(string path) => GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES;

    private sealed class State
    {
        public int Pending;
        public string? FirstError;
        public void Fail(string path, string what) => FirstError ??= $"{path}: {what}";
    }

    /// <summary>A real folder: held, checked through the hold, emptied
    /// entry by entry, then deleted through the same handle.</summary>
    private static void DeleteFolder(string dir, State state)
    {
        using var hold = CreateFileW(dir, DELETE | FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (hold.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            if (err is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND) return;
            if (err is ERROR_SHARING_VIOLATION)
            {
                // Another program has the folder open without delete
                // sharing. Its entries can still go, and the folder at the
                // next start.
                EmptyFolder(dir, state);
                Defer(dir, state);
                return;
            }
            state.Fail(dir, $"could not be opened (Win32 error {err})");
            return;
        }
        FileAttributes attrs;
        try { attrs = File.GetAttributes(hold); }
        catch (Exception e) { state.Fail(dir, e.Message); return; }
        if ((attrs & FileAttributes.ReparsePoint) != 0)
        {
            // Swapped for a link since it was listed: the link goes, as a
            // link, through this handle.
            Dispose(hold, dir, state);
            return;
        }
        EmptyFolder(dir, state);
        Dispose(hold, dir, state);
    }

    private static void EmptyFolder(string dir, State state)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = new List<FileSystemInfo>(new DirectoryInfo(dir).EnumerateFileSystemInfos("*",
                new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false }));
        }
        catch (Exception e)
        {
            state.Fail(dir, $"could not be listed ({e.Message})");
            return;
        }
        foreach (var e in entries)
        {
            bool isDir = (e.Attributes & FileAttributes.Directory) != 0;
            bool link = (e.Attributes & FileAttributes.ReparsePoint) != 0;
            if (isDir && !link) DeleteFolder(e.FullName, state);
            else DeleteEntry(e.FullName, isDir, state);
        }
    }

    /// <summary>A file, or a link to a file or folder, opened as itself and
    /// deleted through its handle.</summary>
    private static void DeleteEntry(string path, bool isDir, State state)
    {
        using var h = CreateFileW(path, DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | (isDir ? FILE_FLAG_BACKUP_SEMANTICS : 0), IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            if (err is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND) return;
            if (err is ERROR_SHARING_VIOLATION or ERROR_ACCESS_DENIED) { Defer(path, state); return; }
            state.Fail(path, $"could not be opened (Win32 error {err})");
            return;
        }
        Dispose(h, path, state);
    }

    /// <summary>Mark the entry open as <paramref name="h"/> for deletion.
    /// An entry Windows will not delete now, such as a DLL another process
    /// has mapped, or a folder that still holds such an entry, goes at the
    /// next start.</summary>
    private static void Dispose(SafeFileHandle h, string path, State state)
    {
        int err = MarkDeleted(h);
        if (err == 0) return;
        if (err is ERROR_ACCESS_DENIED or ERROR_SHARING_VIOLATION or ERROR_DIR_NOT_EMPTY)
        {
            h.Dispose();
            Defer(path, state);
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

    private static void Defer(string path, State state)
    {
        if (MoveFileExW(path, null, MOVEFILE_DELAY_UNTIL_REBOOT)) state.Pending++;
        else state.Fail(path, $"is in use and could not be queued for deletion at the next start (Win32 error {Marshal.GetLastPInvokeError()})");
    }

    private const uint DELETE = 0x00010000, SYNCHRONIZE = 0x00100000;
    private const uint FILE_LIST_DIRECTORY = 0x0001, FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    private const int FileDispositionInfo = 4, FileDispositionInfoEx = 21;
    private const uint FILE_DISPOSITION_FLAG_DELETE = 0x1, FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x2,
        FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;
    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;
    private const int ERROR_INVALID_FUNCTION = 1, ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3,
        ERROR_ACCESS_DENIED = 5, ERROR_SHARING_VIOLATION = 32, ERROR_NOT_SUPPORTED = 50,
        ERROR_INVALID_PARAMETER = 87, ERROR_DIR_NOT_EMPTY = 145;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetFileAttributesW(string fileName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref uint info, uint size);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileExW(string existing, string? newName, uint flags);
}
