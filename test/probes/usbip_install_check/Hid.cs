// The HID calls the probe judges the composite with, made directly, so the
// verdict does not rest on the SDK it is checking.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

internal static class Hid
{
    static readonly Guid HidGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3;
    const int HIDP_STATUS_SUCCESS = 0x00110000;
    static readonly IntPtr Invalid = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_SetNumInputBuffers(IntPtr h, uint count);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, byte[] caps);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string p, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr h, byte[] buf, int n, out int read, IntPtr overlapped);
    [DllImport("kernel32.dll")] static extern bool CancelIoEx(IntPtr h, IntPtr overlapped);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid g, string? deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_ListW(ref Guid g, string? deviceId, char[] buffer, uint len, uint flags);

    /// <summary>Every present HID interface.</summary>
    internal static List<string> AllPaths()
    {
        Guid g = HidGuid;
        if (CM_Get_Device_Interface_List_SizeW(out uint len, ref g, null, 0) != 0 || len <= 1) return new List<string>();
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref g, null, buf, len, 0) != 0) return new List<string>();
        return new List<string>(new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Present HID interfaces with this VID and PID.</summary>
    internal static List<string> Paths(ushort vid, ushort pid)
    {
        var found = new List<string>();
        Guid g = HidGuid;
        if (CM_Get_Device_Interface_List_SizeW(out uint len, ref g, null, 0) != 0 || len <= 1) return found;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref g, null, buf, len, 0) != 0) return found;
        foreach (string path in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            IntPtr h = CreateFileW(path, 0, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == Invalid) continue;
            var a = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (HidD_GetAttributes(h, ref a) && a.VendorID == vid && a.ProductID == pid) found.Add(path);
            CloseHandle(h);
        }
        return found;
    }

    /// <summary>What a read of the interface saw while <c>tick</c> ran every
    /// 8 ms: the input reports, the submits, and the Win32 error of a read
    /// that failed before the window ended (0 when none did, which tells a
    /// device that stopped sending from one that went away). Frames holds the
    /// bytes of each report read, the report id first. StartUtc is when the
    /// window opened and ErrorMs when the failed read returned, -1 when none
    /// did.</summary>
    internal readonly record struct Reading(int Reports, int Ticks, int ReadError, long FirstMs, long LastMs, List<byte[]>? Frames = null,
                                            DateTime StartUtc = default, long ErrorMs = -1)
    {
        public override string ToString() =>
            $"{Reports} report(s) for {Ticks} submit(s), first at {FirstMs} ms, last at {LastMs} ms" +
            (ReadError != 0 ? $", a read failed with Win32 error {ReadError}" + (ErrorMs >= 0 ? $" at {ErrorMs} ms" : "") : "");
    }

    /// <summary>Input reports read from the interface in <paramref name="ms"/>
    /// milliseconds while <paramref name="tick"/> runs every 8 ms.</summary>
    internal static Reading CountReports(string path, int ms, Action tick)
    {
        IntPtr h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == Invalid) return new Reading(-1, 0, Marshal.GetLastWin32Error(), 0, 0);
        int len = 0;
        if (HidD_GetPreparsedData(h, out IntPtr pp))
        {
            var caps = new byte[64];
            if (HidP_GetCaps(pp, caps) == HIDP_STATUS_SUCCESS) len = BitConverter.ToUInt16(caps, 4);
            HidD_FreePreparsedData(pp);
        }
        if (len == 0) { CloseHandle(h); return new Reading(-1, 0, 0, 0, 0); }
        // HID class keeps 32 reports for a handle by default and drops the
        // oldest past that, so a reader thread the machine's load holds up
        // for over 128 ms of 4 ms reports loses input the device sent. 512,
        // the most it allows, covers 2 s, longer than any window here.
        HidD_SetNumInputBuffers(h, 512);

        int count = 0, readError = 0;
        long first = -1, last = -1, errorAt = -1;
        var frames = new List<byte[]>();
        var stop = new StopFlag();
        var startUtc = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        var reader = new Thread(() =>
        {
            var buf = new byte[len];
            while (!stop.Set)
            {
                if (!ReadFile(h, buf, len, out int read, IntPtr.Zero))
                {
                    if (!stop.Set) { readError = Marshal.GetLastWin32Error(); errorAt = sw.ElapsedMilliseconds; }
                    break;
                }
                if (read <= 0) continue;
                long t = sw.ElapsedMilliseconds;
                lock (frames) frames.Add(buf[..read]);
                if (Interlocked.Increment(ref count) == 1) first = t;
                last = t;
            }
        }) { IsBackground = true };
        reader.Start();

        int ticks = 0;
        while (sw.ElapsedMilliseconds < ms) { tick(); ticks++; Thread.Sleep(8); }
        stop.Set = true;
        CancelIoEx(h, IntPtr.Zero);
        reader.Join(2000);
        CloseHandle(h);
        lock (frames) return new Reading(count, ticks, readError, first, last, new List<byte[]>(frames), startUtc, errorAt);
    }

    sealed class StopFlag { public volatile bool Set; }
}
