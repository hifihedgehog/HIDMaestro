// The Joy-Con (L)'s arrows in its 0x3F report, live: before a host speaks
// the protocol, and in input mode 0x3F after one has, the pad sends left,
// down, up and right as buttons 1 to 4 (SDL_hidapi_switch.c
// HandleCombinedSimpleControllerStateL, BlueRetro sw.c sw_jc_btns_mask).
// Read off the HID stack on either backend.
//
// Another program can speak the protocol to the pad before this check reads
// it. A Chromium browser handshakes every new Nintendo pad within
// milliseconds, as it does real hardware (switch_descriptor_idle_check),
// and the pad then sends its 0x30 report and no 0x3F: on the laptop on
// 2026-11-19 all four arrows read "none". A host that wants 0x3F asks for
// it, subcommand 0x03 with mode 0x3F, and so does this check whenever it
// reads a 0x30. It then lets the queued reports drain before each press,
// so a report read after a press is that press's.

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
        int asked = 0, full = 0;
        byte counter = 0;
        var lastAsk = Stopwatch.StartNew();
        // Asks for input mode 0x3F when the report read is the pad's 0x30 or
        // 0x31: SDL's ConstructSubcommand shape, packet type 0x01, a counter,
        // neutral rumble for each side, subcommand 0x03 and the mode.
        bool AskIfFull(byte[]? r)
        {
            if (r == null || r.Length == 0 || r[0] is not (0x30 or 0x31)) return false;
            full++;
            if (asked > 0 && lastAsk.ElapsedMilliseconds < 100) return true;
            var cmd = new byte[49];
            cmd[0] = 0x01;
            cmd[1] = (byte)(counter++ & 0x0F);
            new byte[] { 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40 }.CopyTo(cmd, 2);
            cmd[10] = 0x03;
            cmd[11] = 0x3F;
            if (rd.Write(cmd)) asked++;
            lastAsk.Restart();
            return true;
        }
        // Reads until two reads in a row return nothing: the queue is empty
        // and the pad is not streaming.
        void Settle()
        {
            var sw = Stopwatch.StartNew();
            int quiet = 0;
            while (quiet < 2 && sw.ElapsedMilliseconds < 2000)
            {
                var r = rd.Read(40);
                if (r == null) { quiet++; continue; }
                quiet = 0;
                AskIfFull(r);
            }
        }
        foreach (var (hat, name, bit) in new[] { (HMHat.West, "left", 0), (HMHat.South, "down", 1), (HMHat.North, "up", 2), (HMHat.East, "right", 3) })
        {
            byte[]? got = null;
            var sw = Stopwatch.StartNew();
            while (got == null && sw.ElapsedMilliseconds < 5000)
            {
                ctrl.SubmitState(new HMGamepadState());
                Settle();
                ctrl.SubmitState(new HMGamepadState { Hat = hat });
                var press = Stopwatch.StartNew();
                while (got == null && press.ElapsedMilliseconds < 300)
                {
                    var r = rd.Read(100);
                    // The mode changed under the press: settle and press again.
                    if (AskIfFull(r)) break;
                    if (r != null && r.Length >= 4 && r[0] == 0x3F && (r[1] | r[2]) != 0) got = r;
                }
            }
            Check($"the {name} arrow arrives as 0x3F button {bit + 1}", got != null && got[1] == 1 << bit && got[2] == 0,
                  got == null ? "none" : Convert.ToHexString(got, 0, 4));
        }
        if (full > 0)
            Console.WriteLine($"  [NOTE] another program had spoken the protocol to the pad: {full} report(s) 0x30 read, input mode 0x3F asked for {asked} time(s)");
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

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int len, IntPtr written, IntPtr overlapped);

        /// <summary>One output report, written whole within a second.</summary>
        public bool Write(byte[] report)
        {
            if (!IsOpen) return false;
            IntPtr buf = Marshal.AllocHGlobal(report.Length);
            IntPtr ov = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            IntPtr ev = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            try
            {
                Marshal.Copy(report, 0, buf, report.Length);
                Marshal.StructureToPtr(new NativeOverlapped { EventHandle = ev }, ov, false);
                if (!WriteFile(_h, buf, report.Length, IntPtr.Zero, ov))
                {
                    if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;
                    if (WaitForSingleObject(ev, 1000) != 0)
                    {
                        CancelIoEx(_h, ov);
                        GetOverlappedResult(_h, ov, out _, true);
                        return false;
                    }
                }
                return GetOverlappedResult(_h, ov, out int n, false) && n == report.Length;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                Marshal.FreeHGlobal(ov);
                if (ev != IntPtr.Zero) CloseHandle(ev);
            }
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
