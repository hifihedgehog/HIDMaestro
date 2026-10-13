// A Raw Input reader on a message-only window: every WM_INPUT from every
// mouse, keyboard and generic HID collection this process registers for,
// whatever window has focus (RIDEV_INPUTSINK).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

internal sealed class RawInputSink : IDisposable
{
    public sealed record Mouse(IntPtr Device, ushort Flags, ushort ButtonFlags, int X, int Y, short ButtonData = 0);
    public sealed record Key(IntPtr Device, ushort VKey, ushort Flags, ushort MakeCode);
    public sealed record Hid(IntPtr Device, byte[] Report);

    public readonly ConcurrentQueue<Mouse> Mice = new();
    public readonly ConcurrentQueue<Key> Keys = new();
    public readonly ConcurrentQueue<Hid> Reports = new();

    private readonly Thread _thread;
    private IntPtr _hwnd;
    private uint _threadId;
    private readonly ManualResetEventSlim _ready = new();
    private readonly (ushort Page, ushort Usage)[] _usages;
    private WndProc? _proc;   // kept alive for the window's lifetime

    public RawInputSink(params (ushort Page, ushort Usage)[] usages)
    {
        _usages = usages;
        _thread = new Thread(Run) { IsBackground = true, Name = "RawInputSink" };
        _thread.Start();
        if (!_ready.Wait(5000) || _hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Raw Input window did not start");
    }

    public void Clear()
    {
        while (Mice.TryDequeue(out _)) { }
        while (Keys.TryDequeue(out _)) { }
        while (Reports.TryDequeue(out _)) { }
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _proc = Proc;
        string cls = "HMKbmRawInputSink" + Environment.ProcessId;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = cls,
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, cls, "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        var devices = new RAWINPUTDEVICE[_usages.Length];
        for (int i = 0; i < _usages.Length; i++)
            devices[i] = new RAWINPUTDEVICE
            {
                usUsagePage = _usages[i].Page,
                usUsage = _usages[i].Usage,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = _hwnd,
            };
        RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        _ready.Set();
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT) Read(lParam);
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private unsafe void Read(IntPtr hRaw)
    {
        uint size = 0;
        uint hdr = (uint)sizeof(RAWINPUTHEADER);
        GetRawInputData(hRaw, RID_INPUT, IntPtr.Zero, ref size, hdr);
        if (size == 0) return;
        var buf = new byte[size];
        fixed (byte* p = buf)
        {
            if (GetRawInputData(hRaw, RID_INPUT, (IntPtr)p, ref size, hdr) != size) return;
            var h = (RAWINPUTHEADER*)p;
            byte* data = p + hdr;
            switch (h->dwType)
            {
                case RIM_TYPEMOUSE:
                    // RAWMOUSE: usFlags, pad, usButtonFlags, usButtonData,
                    // ulRawButtons, lLastX, lLastY.
                    Mice.Enqueue(new Mouse(h->hDevice, *(ushort*)data, *(ushort*)(data + 4),
                                           *(int*)(data + 12), *(int*)(data + 16), *(short*)(data + 6)));
                    break;
                case RIM_TYPEKEYBOARD:
                    Keys.Enqueue(new Key(h->hDevice, *(ushort*)(data + 6), *(ushort*)(data + 2), *(ushort*)data));
                    break;
                case RIM_TYPEHID:
                    uint each = *(uint*)data, count = *(uint*)(data + 4);
                    for (uint i = 0; i < count; i++)
                    {
                        var r = new byte[each];
                        Marshal.Copy((IntPtr)(data + 8 + i * each), r, 0, (int)each);
                        Reports.Enqueue(new Hid(h->hDevice, r));
                    }
                    break;
            }
        }
    }

    // ── Device list ──────────────────────────────────────────────────────

    public static List<(IntPtr Handle, uint Type, string Name)> Devices()
    {
        var list = new List<(IntPtr, uint, string)>();
        uint count = 0;
        uint each = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(null, ref count, each);
        if (count == 0) return list;
        var arr = new RAWINPUTDEVICELIST[count + 16];
        count = (uint)arr.Length;
        int got = (int)GetRawInputDeviceList(arr, ref count, each);
        for (int i = 0; i < got; i++)
            list.Add((arr[i].hDevice, arr[i].dwType, Name(arr[i].hDevice)));
        return list;
    }

    /// <summary>The HID attributes behind a Raw Input device path. Mouse
    /// and keyboard collections are opened exclusively by Windows, but a
    /// handle with no access rights still answers HidD_GetAttributes.</summary>
    public static (ushort Vid, ushort Pid)? Attributes(string path)
    {
        IntPtr h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) return null;
        try
        {
            var a = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            return HidD_GetAttributes(h, ref a) ? (a.VendorID, a.ProductID) : null;
        }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID, ProductID, VersionNumber; }

    /// <summary>Open a HID collection for reading and writing, or -1 on failure.</summary>
    public static IntPtr OpenReadWrite(string path) =>
        CreateFile(path, 0xC0000000 /* GENERIC_READ | GENERIC_WRITE */, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
    public static void Close(IntPtr h) => CloseHandle(h);
    public static bool SetFeature(IntPtr h, byte[] report) => HidD_SetFeature(h, report, (uint)report.Length);
    public static bool SetOutputReport(IntPtr h, byte[] report) => HidD_SetOutputReport(h, report, (uint)report.Length);
    public static bool Write(IntPtr h, byte[] report) => WriteFile(h, report, (uint)report.Length, out uint n, IntPtr.Zero) && n == report.Length;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr h, byte[] buffer, uint count, out uint written, IntPtr overlapped);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] private static extern bool HidD_SetFeature(IntPtr h, byte[] report, uint length);
    [DllImport("hid.dll")] private static extern bool HidD_SetOutputReport(IntPtr h, byte[] report, uint length);

    public static string Name(IntPtr device)
    {
        uint chars = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        if (chars == 0) return "";
        IntPtr b = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            GetRawInputDeviceInfo(device, RIDI_DEVICENAME, b, ref chars);
            return Marshal.PtrToStringUni(b) ?? "";
        }
        finally { Marshal.FreeHGlobal(b); }
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
    }

    // ── Interop ──────────────────────────────────────────────────────────

    private const uint WM_INPUT = 0x00FF, WM_QUIT = 0x0012, RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1, RIM_TYPEHID = 2;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h,
                                                IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr raw, uint cmd, IntPtr data, ref uint size, uint header);
    [DllImport("user32.dll")] private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint GetRawInputDeviceInfo(IntPtr device, uint cmd, IntPtr data, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
