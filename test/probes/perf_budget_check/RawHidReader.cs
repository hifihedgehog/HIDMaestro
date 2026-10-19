// A Raw Input reader for one HID interface: WM_INPUT on a message-only
// window, whatever window has focus (RIDEV_INPUTSINK), as
// kbm_live_check's RawInputSink reads, kept to the reports of the one
// device path asked for. On xinputhid's collection (xbox-series-xs-bt) a
// ReadFile from this probe completed with no report over 94 state changes
// in 3 s, while Raw Input, which Windows reads for the programs that
// register for it, delivers every change.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

internal static partial class Program
{
    sealed class RawHidReader : ReportReader
    {
        readonly Thread _thread;
        readonly string _path;
        readonly ManualResetEventSlim _ready = new();
        readonly (ushort Page, ushort Usage) _usage;
        IntPtr _hwnd, _device;
        uint _threadId;
        WndProc? _proc;
        byte[] _raw = new byte[1024];
        readonly byte[] _report;

        public RawHidReader(string path, int inputLength, ushort usagePage, ushort usage) : base(inputLength)
        {
            _path = path;
            _usage = (usagePage, usage);
            _report = new byte[inputLength];
            _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "e2e raw input" };
            _thread.Start();
            if (!_ready.Wait(5000) || _hwnd == IntPtr.Zero)
            {
                // The thread ends at once when it made no window. Otherwise
                // it waits in GetMessage, which WM_QUIT ends.
                if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                _thread.Join(2000);
                throw new InvalidOperationException($"the Raw Input window did not start (error {ReadError})");
            }
        }

        // One window class per reader: a second reader in the process must
        // not find the first one's class, whose window procedure is gone.
        static int s_classes;

        void Run()
        {
            _threadId = GetCurrentThreadId();
            _proc = Proc;
            string cls = $"HMPerfRawInput{Environment.ProcessId}_{Interlocked.Increment(ref s_classes)}";
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = cls,
            };
            if (RegisterClassExW(ref wc) == 0)
            {
                ReadError = Marshal.GetLastWin32Error();
                _ready.Set();
                return;
            }
            // HWND_MESSAGE: a message-only window, never shown.
            _hwnd = CreateWindowExW(0, cls, "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                ReadError = Marshal.GetLastWin32Error();
                UnregisterClassW(cls, wc.hInstance);
                _ready.Set();
                return;
            }
            var dev = new[] { new RAWINPUTDEVICE { usUsagePage = _usage.Page, usUsage = _usage.Usage, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd } };
            if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>())) ReadError = Marshal.GetLastWin32Error();
            _ready.Set();
            while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            // The registration is the process's, not the window's: remove it,
            // so Windows lets go of the collections it opened for it.
            dev[0].dwFlags = RIDEV_REMOVE;
            dev[0].hwndTarget = IntPtr.Zero;
            RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            DestroyWindow(_hwnd);
            UnregisterClassW(cls, wc.hInstance);
        }

        IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_INPUT) Read(lParam, Stopwatch.GetTimestamp());
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        unsafe void Read(IntPtr hRaw, long t)
        {
            uint hdr = (uint)sizeof(RAWINPUTHEADER);
            uint size = (uint)_raw.Length;
            fixed (byte* p = _raw)
            {
                if (GetRawInputData(hRaw, RID_INPUT, (IntPtr)p, ref size, hdr) == unchecked((uint)-1)) return;
                var h = (RAWINPUTHEADER*)p;
                if (h->dwType != RIM_TYPEHID) return;
                if (_device == IntPtr.Zero)
                {
                    if (!string.Equals(DeviceName(h->hDevice), _path, StringComparison.OrdinalIgnoreCase)) return;
                    _device = h->hDevice;
                }
                else if (h->hDevice != _device) return;
                byte* data = p + hdr;
                uint each = *(uint*)data, count = *(uint*)(data + 4);
                for (uint i = 0; i < count; i++)
                {
                    int n = (int)Math.Min(each, (uint)_report.Length);
                    Marshal.Copy((IntPtr)(data + 8 + i * each), _report, 0, n);
                    Deliver(_report, n, t);
                }
            }
        }

        static string DeviceName(IntPtr device)
        {
            uint chars = 0;
            GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
            if (chars == 0) return "";
            IntPtr b = Marshal.AllocHGlobal((int)chars * 2);
            try
            {
                GetRawInputDeviceInfoW(device, RIDI_DEVICENAME, b, ref chars);
                return Marshal.PtrToStringUni(b) ?? "";
            }
            finally { Marshal.FreeHGlobal(b); }
        }

        public override void Dispose()
        {
            if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(2000);
        }
    }

    const uint WM_INPUT = 0x00FF, WM_QUIT = 0x0012, RID_INPUT = 0x10000003, RIM_TYPEHID = 2;
    const uint RIDEV_INPUTSINK = 0x00000100, RIDEV_REMOVE = 0x00000001, RIDI_DEVICENAME = 0x20000007;

    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClassW(string cls, IntPtr instance);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRaw, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, IntPtr data, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
