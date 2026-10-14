// The HID and configuration-manager calls the smoke app judges devices with,
// made directly so a verdict never rests on the SDK under test. Source-
// generated marshalling, so the app itself has nothing for NativeAOT or the
// trimmer to warn about.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

internal static unsafe partial class Native
{
    static readonly Guid HidGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3;
    const int HIDP_STATUS_SUCCESS = 0x00110000;
    static readonly IntPtr Invalid = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_FreePreparsedData(IntPtr pp);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetCaps(IntPtr pp, byte* caps);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateFile(string path, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadFile(IntPtr h, byte* buf, int n, out int read, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(IntPtr h, byte* buf, int n, out int written, IntPtr overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelIoEx(IntPtr h, IntPtr overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr h);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_List_Size(out uint len, ref Guid g, string? deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_List(ref Guid g, string? deviceId, char* buffer, uint len, uint flags);

    /// <summary>Present HID interfaces with this VID and PID.</summary>
    internal static List<string> HidPaths(ushort vid, ushort pid)
    {
        var found = new List<string>();
        Guid g = HidGuid;
        if (CM_Get_Device_Interface_List_Size(out uint len, ref g, null, 0) != 0 || len <= 1) return found;
        var buf = new char[len];
        fixed (char* p = buf)
            if (CM_Get_Device_Interface_List(ref g, null, p, len, 0) != 0) return found;
        foreach (string path in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            IntPtr h = CreateFile(path, 0, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == Invalid) continue;
            var a = new HIDD_ATTRIBUTES { Size = sizeof(HIDD_ATTRIBUTES) };
            if (HidD_GetAttributes(h, ref a) && a.VendorID == vid && a.ProductID == pid) found.Add(path);
            CloseHandle(h);
        }
        return found;
    }

    static (int Input, int Output) ReportLengths(IntPtr h)
    {
        if (!HidD_GetPreparsedData(h, out IntPtr pp)) return (0, 0);
        try
        {
            byte* caps = stackalloc byte[64];
            if (HidP_GetCaps(pp, caps) != HIDP_STATUS_SUCCESS) return (0, 0);
            return (*(ushort*)(caps + 4), *(ushort*)(caps + 6));
        }
        finally { HidD_FreePreparsedData(pp); }
    }

    /// <summary>The input reports read in <paramref name="ms"/> milliseconds
    /// while <paramref name="tick"/> runs every 8 ms, each as the bytes the
    /// host read (the report id first), and the Win32 error of a read that
    /// failed before the window ended (0 when none did). Null when the
    /// interface could not be opened or declares no input report.</summary>
    internal static (List<byte[]>? Reports, int ReadError) CaptureReports(string path, int ms, Action tick)
    {
        IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == Invalid) return (null, Marshal.GetLastPInvokeError());
        int len = ReportLengths(h).Input;
        if (len == 0) { CloseHandle(h); return (null, 0); }

        var reports = new List<byte[]>();
        int readError = 0;
        var stop = new StopFlag();
        var reader = new Thread(() =>
        {
            var buf = new byte[len];
            while (!stop.Set)
            {
                bool ok;
                int read;
                fixed (byte* p = buf) ok = ReadFile(h, p, len, out read, IntPtr.Zero);
                if (!ok)
                {
                    if (!stop.Set) readError = Marshal.GetLastPInvokeError();
                    break;
                }
                if (read > 0) lock (reports) reports.Add(buf[..read]);
            }
        }) { IsBackground = true };
        reader.Start();

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { tick(); Thread.Sleep(8); }
        stop.Set = true;
        CancelIoEx(h, IntPtr.Zero);
        reader.Join(2000);
        CloseHandle(h);
        lock (reports) return (new List<byte[]>(reports), readError);
    }

    /// <summary>Write one output report: the report id, then
    /// <paramref name="body"/>, padded to the interface's output report
    /// length. Returns 0, or the Win32 error.</summary>
    internal static int WriteOutput(string path, byte reportId, ReadOnlySpan<byte> body)
    {
        IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == Invalid) return Marshal.GetLastPInvokeError();
        try
        {
            int len = ReportLengths(h).Output;
            if (len == 0) return -1;
            var report = new byte[len];
            report[0] = reportId;
            body[..Math.Min(body.Length, len - 1)].CopyTo(report.AsSpan(1));
            fixed (byte* p = report)
                return WriteFile(h, p, len, out _, IntPtr.Zero) ? 0 : Marshal.GetLastPInvokeError();
        }
        finally { CloseHandle(h); }
    }

    sealed class StopFlag { public volatile bool Set; }
}
