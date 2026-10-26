using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HIDMaestro.Internal;

/// <summary>
/// A diagnostic log that several processes append to at once:
/// teardown_diag.log and setup_timing.log under %TEMP%\HIDMaestro take the
/// lines of every program that embeds the SDK, the test app writes its own
/// into teardown_diag.log, and every USB/IP device appends to the log
/// HIDMAESTRO_DIAG_READS names.
///
/// <para>Each line is one WriteFile on a handle opened with FILE_APPEND_DATA
/// and without FILE_WRITE_DATA. Windows writes it at the file's end: "For
/// local files, write operations will not overwrite existing data if this
/// flag is specified without FILE_WRITE_DATA" (File Access Rights
/// Constants), the I/O manager hands the file system such a write as
/// FILE_WRITE_TO_END_OF_FILE (ReactOS NtWriteFile), and the file system takes
/// the file's lock exclusive and writes at the end it reads under that lock
/// (FastFAT's write path). So a line lands whole after every line before it,
/// whichever process wrote either. A FileStream opened with FileMode.Append
/// does not: it writes at an offset it counts itself from the file's length
/// at its open, so on 2026-10-26 a program's Dispose wrote its lines over the
/// full cleanup's that ran beside it.</para>
///
/// <para>The handle stays open between lines, as the writer before it did
/// since T32-2: an open, write and close per line added 10 to 40 s to a
/// battery run's 3,000 to 4,000 lines. It is opened as the user
/// (<see cref="OpenVrPathRegistry.RunAsUser{T}"/>), so an elevated process
/// never creates or appends to a file in a folder the user's programs
/// control, and it shares read, write and delete, so a reader can tail the
/// log and the battery can delete it while programs hold it. After each
/// write the handle's file is checked: when it was deleted while open, the
/// line went with it, and it is written again into a new file at the path.
/// A log renamed while open takes the lines on with it under its new
/// name.</para>
/// </summary>
internal sealed partial class UserLog : IDisposable
{
    private readonly string _path;
    private readonly object _lock = new();
    private SafeFileHandle? _handle;

    internal UserLog(string path) => _path = path;

    /// <summary>Appends <paramref name="text"/>, which ends with its own line
    /// end, in one write. A log that cannot be opened or written drops the
    /// line: a diagnostic log never fails its caller.</summary>
    internal void Write(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        lock (_lock)
        {
            // The second pass writes the line into a new file at the path
            // when the first one's went away with a deleted file.
            for (int pass = 0; pass < 2; pass++)
            {
                try
                {
                    _handle ??= Open(_path);
                    if (WriteAll(_handle, bytes) && !Unlinked(_handle)) return;
                }
                catch { }
                _handle?.Dispose();
                _handle = null;
            }
        }
    }

    /// <summary>Appends <paramref name="text"/> to the log at
    /// <paramref name="path"/> through a handle of its own, opened and closed
    /// around the one write: for a writer of a line now and then, which then
    /// holds nothing open between its lines.</summary>
    internal static void Append(string path, string text)
    {
        try
        {
            using var handle = Open(path);
            WriteAll(handle, Encoding.UTF8.GetBytes(text));
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _handle?.Dispose();
            _handle = null;
        }
    }

    private static SafeFileHandle Open(string path) =>
        OpenVrPathRegistry.RunAsUser(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var handle = CreateFileW(path, FILE_APPEND_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            return handle;
        });

    private static bool WriteAll(SafeFileHandle handle, byte[] bytes) =>
        WriteFile(handle, bytes, (uint)bytes.Length, out uint written, IntPtr.Zero) && written == bytes.Length;

    /// <summary>True when the handle's file was deleted while open: its
    /// FILE_STANDARD_INFO shows the delete pending or no name left.</summary>
    private static bool Unlinked(SafeFileHandle handle)
    {
        // FILE_STANDARD_INFO: AllocationSize and EndOfFile (8 bytes each),
        // NumberOfLinks (4), DeletePending and Directory (1 each), padded to 24.
        var info = new byte[24];
        if (!GetFileInformationByHandleEx(handle, FileStandardInfo, info, (uint)info.Length)) return false;
        return info[20] != 0 || BitConverter.ToUInt32(info, 16) == 0;
    }

    private const uint FILE_APPEND_DATA = 0x0004;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_ALWAYS = 4;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const int FileStandardInfo = 1;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(SafeFileHandle file, byte[] buffer, uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, [Out] byte[] info, uint size);
}
