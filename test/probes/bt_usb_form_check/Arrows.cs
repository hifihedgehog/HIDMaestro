// The Joy-Con (L)'s arrows in its 0x3F report, live: before a host speaks
// the protocol the pad sends left, down, up and right as buttons 1 to 4
// (SDL_hidapi_switch.c HandleCombinedSimpleControllerStateL, BlueRetro sw.c
// sw_jc_btns_mask). Read off the HID stack on the backend that presents the
// Bluetooth form.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

using HIDMaestro;

using Microsoft.Win32.SafeHandles;

internal static partial class Program
{
    static void JoyConArrows(HMController ctrl, string path)
    {
        using var rd = new HidReader(path, 362);
        Check("the 0x3F reader opens", rd.IsOpen, rd.OpenError);
        if (!rd.IsOpen) return;
        foreach (var (hat, name, bit) in new[] { (HMHat.West, "left", 0), (HMHat.South, "down", 1), (HMHat.North, "up", 2), (HMHat.East, "right", 3) })
        {
            byte[]? got = null;
            var sw = Stopwatch.StartNew();
            while (got == null && sw.ElapsedMilliseconds < 3000)
            {
                ctrl.SubmitState(new HMGamepadState());
                rd.Read(30);
                ctrl.SubmitState(new HMGamepadState { Hat = hat });
                var r = rd.Read(100);
                if (r != null && r.Length >= 4 && r[0] == 0x3F && (r[1] | r[2]) != 0) got = r;
            }
            Check($"the {name} arrow arrives as 0x3F button {bit + 1}", got != null && got[1] == 1 << bit && got[2] == 0,
                  got == null ? "none" : Convert.ToHexString(got, 0, 4));
        }
        ctrl.SubmitState(new HMGamepadState());
    }

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3;
    const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    /// <summary>Overlapped reads with a timeout, so a silent device fails a
    /// check instead of hanging the probe.</summary>
    sealed class HidReader : IDisposable
    {
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
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);

        const int ERROR_IO_PENDING = 997;
        readonly SafeFileHandle _h;
        readonly IntPtr _buf, _ov, _event;
        readonly int _len;

        public bool IsOpen => !_h.IsInvalid;
        public string OpenError { get; } = "";

        public HidReader(string path, int len)
        {
            _len = len;
            _h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING,
                             FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (_h.IsInvalid) OpenError = $"error {Marshal.GetLastWin32Error()}";
            _buf = Marshal.AllocHGlobal(len);
            _ov = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            _event = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        }

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
            Marshal.FreeHGlobal(_buf);
            Marshal.FreeHGlobal(_ov);
            if (_event != IntPtr.Zero) CloseHandle(_event);
        }
    }
}
