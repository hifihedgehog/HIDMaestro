// Unified input-source counter — listens on every common Windows input API
// simultaneously and logs every button transition. Answers the question:
// "which N surfaces fire per one discrete keypress?"
//
// Surfaces:
//   1. XInput (polled via XInputGetState across slots 0..3)
//   2. WGI RawGameController (polled .GetCurrentReading)
//   3. WGI UINavigationController (polled .GetCurrentReading)
//   4. RawInput (WM_INPUT messages for HID devices, usage page 1)
//   5. DirectInput8 (polled joystick state for every enumerated gamepad)
//
// For each surface, counts DOWN/UP transitions. Keep the window focused
// while pressing UP one time and note the per-surface counts.

using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Windows.Gaming.Input;

[StructLayout(LayoutKind.Sequential)]
internal struct XINPUT_GAMEPAD
{
    public ushort wButtons;
    public byte bLeftTrigger, bRightTrigger;
    public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XINPUT_STATE
{
    public uint dwPacketNumber;
    public XINPUT_GAMEPAD Gamepad;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RAWINPUTDEVICE
{
    public ushort usUsagePage;
    public ushort usUsage;
    public uint dwFlags;
    public IntPtr hwndTarget;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RAWINPUTHEADER
{
    public uint dwType;
    public uint dwSize;
    public IntPtr hDevice;
    public IntPtr wParam;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RID_DEVICE_INFO_HID
{
    public uint dwVendorId;
    public uint dwProductId;
    public uint dwVersionNumber;
    public ushort usUsagePage;
    public ushort usUsage;
}

[StructLayout(LayoutKind.Explicit)]
internal struct RID_DEVICE_INFO_UNION
{
    [FieldOffset(0)] public RID_DEVICE_INFO_HID hid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RID_DEVICE_INFO
{
    public uint cbSize;
    public uint dwType;
    public RID_DEVICE_INFO_UNION u;
}

internal static class Native
{
    [DllImport("xinput1_4.dll")]
    public static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    public const uint RIDI_DEVICENAME = 0x20000007;
    public const uint RIDI_DEVICEINFO = 0x2000000B;
    public const uint RID_HEADER = 0x10000005;
}

internal static class Probe
{
    const uint RID_INPUT = 0x10000003;
    const uint RIDEV_INPUTSINK = 0x00000100;
    const int WM_INPUT = 0x00FF;

    [STAThread]
    static void Main()
    {
        var app = new Application();
        var log = new ObservableCollection<string>();
        var listBox = new ListBox { ItemsSource = log, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12 };
        var stats = new TextBlock { Padding = new Thickness(6), Background = System.Windows.Media.Brushes.LightYellow, Text = "Initializing all input surfaces..." };
        var panel = new DockPanel();
        DockPanel.SetDock(stats, Dock.Top);
        panel.Children.Add(stats);
        panel.Children.Add(listBox);

        var window = new Window
        {
            Title = "Unified Input Source Counter — keep focused, press UP once",
            Width = 1000, Height = 640, Topmost = true,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        var sw = Stopwatch.StartNew();
        var counters = new ConcurrentDictionary<string, int>();
        var events = new ConcurrentQueue<string>();

        void Count(string surface) { counters.AddOrUpdate(surface, 1, (_, v) => v + 1); }
        void Log(string line) { events.Enqueue(line); }

        // UI flush timer.
        var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        uiTimer.Tick += (_, _) =>
        {
            while (events.TryDequeue(out var s))
            {
                log.Add(s);
                if (log.Count > 3000) log.RemoveAt(0);
            }
            var sb = new System.Text.StringBuilder("Counts: ");
            foreach (var kv in counters) sb.Append($"[{kv.Key}={kv.Value}] ");
            stats.Text = sb.ToString();
            if (listBox.Items.Count > 0) listBox.ScrollIntoView(listBox.Items[listBox.Items.Count - 1]);
        };

        var stop = false;

        // XInput polling.
        new Thread(() =>
        {
            ushort[] prevBtns = new ushort[4];
            bool[] seen = new bool[4];
            while (!stop)
            {
                for (uint i = 0; i < 4; i++)
                {
                    if (Native.XInputGetState(i, out var s) != 0) { seen[i] = false; continue; }
                    if (!seen[i]) { seen[i] = true; prevBtns[i] = s.Gamepad.wButtons; Log($"{sw.ElapsedMilliseconds,5} XInput[{i}] connected"); continue; }
                    if (s.Gamepad.wButtons != prevBtns[i])
                    {
                        Count("XInput");
                        Log($"{sw.ElapsedMilliseconds,5} XInput[{i}]  btns 0x{prevBtns[i]:X4} -> 0x{s.Gamepad.wButtons:X4}");
                        prevBtns[i] = s.Gamepad.wButtons;
                    }
                }
                Thread.Sleep(2);
            }
        }) { IsBackground = true }.Start();

        // WGI RGC polling.
        new Thread(() =>
        {
            RawGameController? rgc = null;
            bool[]? prevB = null;
            while (!stop)
            {
                if (rgc is null)
                {
                    var l = RawGameController.RawGameControllers;
                    if (l.Count > 0) { rgc = l[0]; prevB = new bool[rgc.ButtonCount]; Log($"{sw.ElapsedMilliseconds,5} RGC attached btns={rgc.ButtonCount}"); }
                    else { Thread.Sleep(100); continue; }
                }
                try
                {
                    var b = new bool[rgc!.ButtonCount];
                    var sw2 = new GameControllerSwitchPosition[rgc.SwitchCount];
                    var a = new double[rgc.AxisCount];
                    rgc.GetCurrentReading(b, sw2, a);
                    for (int i = 0; i < b.Length; i++)
                        if (b[i] != prevB![i]) { Count("RGC"); Log($"{sw.ElapsedMilliseconds,5} RGC  btn[{i,2}] {(b[i] ? "DOWN" : "UP  ")}"); prevB[i] = b[i]; }
                }
                catch { rgc = null; prevB = null; }
                Thread.Sleep(2);
            }
        }) { IsBackground = true }.Start();

        // WGI UINavigationController polling.
        new Thread(() =>
        {
            UINavigationController? ui = null;
            uint prev = 0;
            while (!stop)
            {
                if (ui is null)
                {
                    var l = UINavigationController.UINavigationControllers;
                    if (l.Count > 0) { ui = l[0]; Log($"{sw.ElapsedMilliseconds,5} UINav attached"); }
                    else { Thread.Sleep(100); continue; }
                }
                try
                {
                    var r = ui!.GetCurrentReading();
                    uint cur = (uint)r.RequiredButtons;
                    if (cur != prev) { Count("UINav"); Log($"{sw.ElapsedMilliseconds,5} UINav 0x{prev:X8} -> 0x{cur:X8}"); prev = cur; }
                }
                catch { ui = null; }
                Thread.Sleep(2);
            }
        }) { IsBackground = true }.Start();

        // Window-message hook for RawInput.
        window.Loaded += (_, _) =>
        {
            window.Activate();
            var helper = new WindowInteropHelper(window);
            var src = HwndSource.FromHwnd(helper.Handle);
            // Register for RawInput from HID gamepads (usage page 0x01, usage 0x05).
            // Also try joystick (0x04) and generic game controllers (0x02..0x05).
            var rids = new[]
            {
                new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 4, dwFlags = RIDEV_INPUTSINK, hwndTarget = helper.Handle }, // Joystick
                new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 5, dwFlags = RIDEV_INPUTSINK, hwndTarget = helper.Handle }, // Gamepad
            };
            bool ok = Native.RegisterRawInputDevices(rids, (uint)rids.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            Log($"{sw.ElapsedMilliseconds,5} RawInput register: {(ok ? "OK" : "FAIL")}");
            // Per-device name cache so we only look up each hDevice once.
            var deviceNames = new System.Collections.Generic.Dictionary<IntPtr, string>();
            src?.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == WM_INPUT)
                {
                    Count("RawInput");
                    // Read the RAWINPUT header to get the hDevice.
                    uint hdrSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
                    IntPtr buf = Marshal.AllocHGlobal((int)hdrSize);
                    try
                    {
                        uint got = hdrSize;
                        uint ok = Native.GetRawInputData(lp, Native.RID_HEADER, buf, ref got, hdrSize);
                        if (ok != unchecked((uint)-1))
                        {
                            var hdr = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
                            string name;
                            if (!deviceNames.TryGetValue(hdr.hDevice, out name!))
                            {
                                // Look up the device name string (instance path).
                                uint nameSize = 0;
                                Native.GetRawInputDeviceInfoW(hdr.hDevice, Native.RIDI_DEVICENAME, IntPtr.Zero, ref nameSize);
                                if (nameSize > 0 && nameSize < 4096)
                                {
                                    IntPtr nbuf = Marshal.AllocHGlobal((int)nameSize * 2);
                                    try
                                    {
                                        uint got2 = nameSize;
                                        Native.GetRawInputDeviceInfoW(hdr.hDevice, Native.RIDI_DEVICENAME, nbuf, ref got2);
                                        name = Marshal.PtrToStringUni(nbuf) ?? "(unknown)";
                                    }
                                    finally { Marshal.FreeHGlobal(nbuf); }
                                }
                                else name = "(name lookup failed)";
                                deviceNames[hdr.hDevice] = name;
                            }
                            Log($"{sw.ElapsedMilliseconds,5} RawInput hDev=0x{hdr.hDevice.ToInt64():X} name='{name}'");
                        }
                        else
                        {
                            Log($"{sw.ElapsedMilliseconds,5} RawInput WM_INPUT (hRawInput=0x{lp.ToInt64():X}, header read failed)");
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                return IntPtr.Zero;
            });
            uiTimer.Start();
        };

        window.Closed += (_, _) => { stop = true; uiTimer.Stop(); };
        app.Run(window);
    }
}
