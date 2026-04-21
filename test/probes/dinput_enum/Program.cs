// DirectInput 8 enumeration probe — lists every gamepad/joystick visible
// through IDirectInput8::EnumDevices(DI8DEVCLASS_GAMECTRL). Writes to both
// a WPF window (focus so DI works) and to %TEMP%\dinput_enum.txt so the
// caller can read it back without screen-scraping.
//
// The HID path (DirectInput's default) wants HIDClass devnodes. The XInput-
// compat shim on Windows also exposes XUSB-interface devices. Compare
// enumerations in two states:
//   1. Virtual with main HID + HID child + HMCOMPANION (current).
//   2. Virtual with only HMCOMPANION (main removed).
// If state 2 still shows our 045E:028E, Option A preserves DI visibility.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;

internal static class Probe
{
    [STAThread]
    static int Main(string[] args)
    {
        string logPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "dinput_enum.txt");
        var lines = new StringBuilder();
        void Log(string s) { lines.AppendLine(s); }

        var app = new Application();
        var tb = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = "Enumerating DirectInput gamepads..."
        };
        var window = new Window
        {
            Title = "DirectInput EnumDevices probe",
            Width = 900,
            Height = 520,
            Topmost = true,
            Content = tb,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        window.Loaded += (_, _) =>
        {
            window.Activate();
            try
            {
                IntPtr di8 = IntPtr.Zero;
                Guid iid_idi8w = new Guid("BF798031-483A-4DA2-AA99-5D64ED369700");
                int hr = DirectInput8Create(GetModuleHandle(null), 0x00000800 /*DIRECTINPUT_VERSION*/, ref iid_idi8w, out di8, IntPtr.Zero);
                Log($"DirectInput8Create hr=0x{hr:X8}  di8=0x{di8.ToInt64():X}");
                if (hr != 0 || di8 == IntPtr.Zero) { tb.Text = lines.ToString(); File.WriteAllText(logPath, tb.Text); return; }

                // Get the vtable for IDirectInput8W to call EnumDevices (method #4)
                IntPtr vtbl = Marshal.ReadIntPtr(di8);
                IntPtr enumDevicesAddr = Marshal.ReadIntPtr(vtbl, 4 * IntPtr.Size);
                var enumDevices = (EnumDevicesDelegate)Marshal.GetDelegateForFunctionPointer(enumDevicesAddr, typeof(EnumDevicesDelegate));

                int count = 0;
                EnumDevicesCallback cb = (ref DIDEVICEINSTANCEW di, IntPtr pv) =>
                {
                    count++;
                    Log($"[{count}] name='{di.tszInstanceName}' product='{di.tszProductName}'");
                    Log($"     type=0x{di.dwDevType:X8}  guidInst={di.guidInstance}  guidProd={di.guidProduct}");
                    return 1; // DIENUM_CONTINUE
                };

                hr = enumDevices(di8, 4 /*DI8DEVCLASS_GAMECTRL*/, Marshal.GetFunctionPointerForDelegate(cb), IntPtr.Zero, 0 /*DIEDFL_ALLDEVICES*/);
                Log($"EnumDevices hr=0x{hr:X8}  total={count}");

                // Release
                IntPtr releaseAddr = Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size);
                var release = (ReleaseDelegate)Marshal.GetDelegateForFunctionPointer(releaseAddr, typeof(ReleaseDelegate));
                release(di8);
            }
            catch (Exception ex)
            {
                Log($"EXCEPTION: {ex.Message}");
            }

            tb.Text = lines.ToString();
            try { File.WriteAllText(logPath, tb.Text); } catch { }
            Log($"\nWritten to: {logPath}");
            tb.Text = lines.ToString();
        };

        app.Run(window);
        return 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DIDEVICEINSTANCEW
    {
        public int dwSize;
        public Guid guidInstance;
        public Guid guidProduct;
        public uint dwDevType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string tszInstanceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string tszProductName;
        public Guid guidFFDriver;
        public ushort wUsagePage;
        public ushort wUsage;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumDevicesCallback(ref DIDEVICEINSTANCEW lpddi, IntPtr pvRef);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumDevicesDelegate(IntPtr pThis, uint dwDevType, IntPtr lpCallback, IntPtr pvRef, uint dwFlags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr pThis);

    [DllImport("dinput8.dll", CharSet = CharSet.Unicode)]
    private static extern int DirectInput8Create(IntPtr hInstance, uint dwVersion, ref Guid riidltf, out IntPtr ppvOut, IntPtr punkOuter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
