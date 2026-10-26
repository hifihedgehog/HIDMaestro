// Reads one HID interface on its own thread and keeps every input report
// with the time it arrived. A read that fails means the device left or
// restarted under the open handle, which is what a program that holds a
// controller sees when another program removes it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

internal sealed class HidCapture : IDisposable
{
    public string Path { get; }
    readonly IntPtr _h, _pp;
    readonly int _len;
    readonly Thread _thread;
    volatile bool _stop;
    readonly List<(long Ticks, byte[] Report)> _reports = new();
    public int ReadError { get; private set; }
    public bool Broken => ReadError != 0;

    HidCapture(string path, IntPtr h, IntPtr pp, int len)
    {
        Path = path; _h = h; _pp = pp; _len = len;
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "hid-capture" };
        _thread.Start();
    }

    public static HidCapture? Open(string path)
    {
        IntPtr h = Native.CreateFileW(path, Native.GENERIC_READ, Native.FILE_SHARE_RW, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == Native.INVALID) return null;
        // Windows' default 32-report queue overflows when a loaded reader falls behind.
        Native.HidD_SetNumInputBuffers(h, 512);
        if (!Native.HidD_GetPreparsedData(h, out IntPtr pp)) { Native.CloseHandle(h); return null; }
        var caps = new byte[64];
        int len = Native.HidP_GetCaps(pp, caps) == Native.HIDP_STATUS_SUCCESS ? BitConverter.ToUInt16(caps, 4) : 0;
        if (len == 0) { Native.HidD_FreePreparsedData(pp); Native.CloseHandle(h); return null; }
        return new HidCapture(path, h, pp, len);
    }

    void Loop()
    {
        var buf = new byte[_len];
        while (!_stop)
        {
            if (!Native.ReadFile(_h, buf, buf.Length, out int read, IntPtr.Zero))
            {
                if (!_stop) ReadError = Marshal.GetLastWin32Error();
                return;
            }
            long t = Stopwatch.GetTimestamp();
            if (read <= 0) continue;
            lock (_reports) _reports.Add((t, buf[..read]));
        }
    }

    public long Mark() => Stopwatch.GetTimestamp();

    public List<(long Ticks, byte[] Report)> Since(long mark, long until = long.MaxValue)
    {
        lock (_reports) return _reports.Where(r => r.Ticks >= mark && r.Ticks <= until).ToList();
    }

    public int Count { get { lock (_reports) return _reports.Count; } }

    /// <summary>Whether a report arrived at or after <paramref name="mark"/>.
    /// Reports are kept in arrival order, so the last one decides.</summary>
    public bool AnySince(long mark)
    {
        lock (_reports) return _reports.Count > 0 && _reports[^1].Ticks >= mark;
    }

    /// <summary>Pressed buttons and the X value of one report, read
    /// through the device's own report descriptor.</summary>
    public (string Buttons, uint X) Decode(byte[] report)
    {
        var usages = new ushort[32];
        uint n = (uint)usages.Length;
        string buttons = Native.HidP_GetUsages(0, 0x09, 0, usages, ref n, _pp, report, (uint)report.Length) == Native.HIDP_STATUS_SUCCESS
            ? string.Join("+", usages.Take((int)n).OrderBy(u => u))
            : "?";
        uint x = Native.HidP_GetUsageValue(0, 0x01, 0, 0x30, out uint v, _pp, report, (uint)report.Length) == Native.HIDP_STATUS_SUCCESS ? v : uint.MaxValue;
        return (buttons.Length == 0 ? "none" : buttons, x);
    }

    /// <summary>Gaps between consecutive reports, in milliseconds.</summary>
    public static double[] Gaps(List<(long Ticks, byte[] Report)> reports)
    {
        var g = new double[Math.Max(0, reports.Count - 1)];
        for (int i = 1; i < reports.Count; i++)
            g[i - 1] = (reports[i].Ticks - reports[i - 1].Ticks) * 1000.0 / Stopwatch.Frequency;
        return g;
    }

    public static double Percentile(double[] values, double p)
    {
        if (values.Length == 0) return double.NaN;
        var s = values.OrderBy(v => v).ToArray();
        return s[Math.Min(s.Length - 1, (int)Math.Floor(p * s.Length))];
    }

    bool _disposed;

    /// <summary>Stops the reader and closes the handle. A second call does
    /// nothing, so a check can let a device go before its scope ends.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop = true;
        Native.CancelIoEx(_h, IntPtr.Zero);
        _thread.Join(1000);
        Native.HidD_FreePreparsedData(_pp);
        Native.CloseHandle(_h);
    }
}
