// A HID read bounded in time. The interrupt IN check read the persona's HID
// interface with a synchronous ReadFile, whose deadline was checked only
// between reads, so a report that never came held the probe, and the
// battery's bench lock with it, until the battery's own hour ran out. This
// is ds4_report_check's HidReader: an overlapped read that waits for at
// most the time left, and is canceled (CancelIoEx, then the result waited
// for) when the wait runs out.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

internal static partial class Program
{
    sealed class BoundedHidReader : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFileW(string fn, uint access, uint share, IntPtr sec, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, IntPtr read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out int transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateEventW(IntPtr sec, bool manualReset, bool initialState, IntPtr name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ResetEvent(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")]
        static extern bool CloseEvent(IntPtr h);

        const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        const int ERROR_IO_PENDING = 997;
        readonly SafeFileHandle _h;
        readonly IntPtr _buf, _ov, _event;
        readonly int _len;

        public bool IsOpen => !_h.IsInvalid;

        public BoundedHidReader(string path, int len)
        {
            _len = len;
            _h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
                             FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            _buf = Marshal.AllocHGlobal(len);
            _ov = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            _event = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        }

        /// <summary>One report, or null when none came within
        /// <paramref name="timeoutMs"/> or the read failed.</summary>
        public byte[]? Read(int timeoutMs)
        {
            if (!IsOpen) return null;
            Marshal.StructureToPtr(new NativeOverlapped { EventHandle = _event }, _ov, false);
            ResetEvent(_event);
            if (!ReadFile(_h, _buf, _len, IntPtr.Zero, _ov))
            {
                if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return null;
                if (WaitForSingleObject(_event, (uint)Math.Max(1, timeoutMs)) != 0)
                {
                    CancelIoEx(_h, _ov);
                    GetOverlappedResult(_h, _ov, out _, true);
                    return null;
                }
            }
            if (!GetOverlappedResult(_h, _ov, out int n, false) || n <= 0) return null;
            var r = new byte[n];
            Marshal.Copy(_buf, r, 0, n);
            return r;
        }

        public void Dispose()
        {
            _h.Dispose();
            CloseEvent(_event);
            Marshal.FreeHGlobal(_ov);
            Marshal.FreeHGlobal(_buf);
        }
    }
}
