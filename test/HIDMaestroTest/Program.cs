using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HIDMaestroTest;

/// <summary>
/// Test client for HIDMaestro UMDF2 virtual HID minidriver.
///
/// Configuration flow:
///   1. Write HID descriptor + VID/PID to HKLM\SOFTWARE\HIDMaestro
///   2. Restart the device node so driver re-reads config
///   3. Open the HID child device
///   4. Send raw input reports via HidD_SetOutputReport
///
/// Commands:
///   HIDMaestroTest xbox      — Xbox 360 gamepad (standard HID)
///   HIDMaestroTest ds5       — DualSense gamepad
///   HIDMaestroTest cleanup   — Remove device, driver, certs, registry
/// </summary>
class Program
{
    const string REG_PATH = @"SOFTWARE\HIDMaestro";

    // ── P/Invoke ──

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("hid.dll")]
    static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool HidD_SetOutputReport(SafeFileHandle HidDeviceObject,
        byte[] ReportBuffer, uint ReportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool HidD_SetFeature(SafeFileHandle HidDeviceObject,
        byte[] ReportBuffer, uint ReportBufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer,
        uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject,
        ref HIDD_ATTRIBUTES Attributes);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [DllImport("SetupAPI.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid ClassGuid, IntPtr Enumerator,
        IntPtr hwndParent, uint Flags);

    [DllImport("SetupAPI.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("SetupAPI.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
        ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("SetupAPI.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize, IntPtr DeviceInfoData);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    const uint DIGCF_PRESENT = 0x02;
    const uint DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;
    const uint OPEN_EXISTING = 3;
    const uint FILE_SHARE_RW = 3;

    static CancellationTokenSource _cts = new();

    static int Main(string[] args)
    {
        Console.WriteLine("=== HIDMaestro Test Client ===\n");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _cts.Cancel(); };

        if (args.Length == 0)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  HIDMaestroTest xbox      Xbox 360 gamepad");
            Console.WriteLine("  HIDMaestroTest ds5       DualSense gamepad");
            Console.WriteLine("  HIDMaestroTest cleanup   Remove everything");
            Console.WriteLine("\nMust run elevated for config writes + device restart.");
            return 1;
        }

        return args[0].ToLower() switch
        {
            "xbox"    => TestXbox360(),
            "ds5"     => TestDualSense(),
            "cleanup" => RunCleanup(),
            _         => Error($"Unknown command: {args[0]}")
        };
    }

    static int Error(string msg) { Console.Error.WriteLine($"ERROR: {msg}"); return 1; }

    // ── Registry config ──

    static void WriteConfig(byte[] descriptor, ushort vid, ushort pid, ushort ver = 0x0100)
    {
        using var key = Registry.LocalMachine.CreateSubKey(REG_PATH);
        key.SetValue("ReportDescriptor", descriptor, RegistryValueKind.Binary);
        key.SetValue("VendorId", (int)vid, RegistryValueKind.DWord);
        key.SetValue("ProductId", (int)pid, RegistryValueKind.DWord);
        key.SetValue("VersionNumber", (int)ver, RegistryValueKind.DWord);
    }

    // ── Device restart (remove + recreate via PowerShell script) ──

    static void RestartDevice()
    {
        Console.Write("  Removing + recreating device node... ");

        // Remove existing nodes
        RunProcess("pnputil.exe", "/remove-device \"ROOT\\HIDCLASS\\0000\" /subtree");
        RunProcess("pnputil.exe", "/remove-device \"ROOT\\HIDCLASS\\0001\" /subtree");
        Thread.Sleep(1000);

        // Recreate via the create_node.ps1 script
        string script = @"C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\scripts\create_node.ps1";
        RunProcess("powershell.exe", $"-ExecutionPolicy Bypass -File \"{script}\"");

        Console.WriteLine("OK");
        Thread.Sleep(3000); // Wait for HID child to enumerate
    }

    static void RunProcess(string fileName, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName, Arguments = args,
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        proc?.StandardOutput.ReadToEnd();
        proc?.StandardError.ReadToEnd();
        proc?.WaitForExit(10_000);
    }

    // ── Find and open HID child device ──

    static SafeFileHandle? OpenHidDevice(ushort targetVid, ushort targetPid)
    {
        HidD_GetHidGuid(out Guid hidGuid);

        IntPtr dis = SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (dis == new IntPtr(-1)) return null;

        try
        {
            for (uint idx = 0; idx < 128; idx++)
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(dis, IntPtr.Zero, ref hidGuid, idx, ref did))
                    break;

                SetupDiGetDeviceInterfaceDetailW(dis, ref did, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);

                IntPtr detail = Marshal.AllocHGlobal((int)reqSize);
                try
                {
                    Marshal.WriteInt32(detail, 8);
                    if (!SetupDiGetDeviceInterfaceDetailW(dis, ref did, detail, reqSize, out _, IntPtr.Zero))
                        continue;

                    string path = Marshal.PtrToStringUni(detail + 4)!;

                    var handle = CreateFileW(path, GENERIC_READ | GENERIC_WRITE,
                        FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (handle.IsInvalid) continue;

                    var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (HidD_GetAttributes(handle, ref attrs))
                    {
                        // Check for our device by VID/PID match OR by HIDCLASS path (our driver)
                        bool isOurs = (attrs.VendorID == targetVid && attrs.ProductID == targetPid)
                            || path.Contains("HIDCLASS", StringComparison.OrdinalIgnoreCase);

                        if (isOurs)
                        {
                            Console.WriteLine($"  Found: VID={attrs.VendorID:X4} PID={attrs.ProductID:X4} @ {path}");
                            return handle;
                        }
                    }

                    handle.Dispose();
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(dis);
        }

        return null;
    }

    // ── Xbox 360 HID Gamepad ──

    static int TestXbox360()
    {
        Console.WriteLine("-- Xbox 360 Gamepad Emulation --\n");

        // Standard HID gamepad descriptor: 6 axes (16-bit), 10 buttons, 1 hat
        // This produces an exact gamepad in joy.cpl
        byte[] descriptor = {
            0x05, 0x01,                     // Usage Page (Generic Desktop)
            0x09, 0x05,                     // Usage (Game Pad)
            0xA1, 0x01,                     // Collection (Application)
            0x85, 0x01,                     //   Report ID (1)
            // Left stick X, Y
            0x09, 0x30,                     //   Usage (X)
            0x09, 0x31,                     //   Usage (Y)
            0x15, 0x00,                     //   Logical Minimum (0)
            0x27, 0xFF, 0xFF, 0x00, 0x00,   //   Logical Maximum (65535)
            0x75, 0x10,                     //   Report Size (16)
            0x95, 0x02,                     //   Report Count (2)
            0x81, 0x02,                     //   INPUT (Data, Var, Abs)
            // Right stick X, Y
            0x09, 0x33,                     //   Usage (Rx)
            0x09, 0x34,                     //   Usage (Ry)
            0x81, 0x02,                     //   INPUT (Data, Var, Abs)
            // Triggers
            0x09, 0x32,                     //   Usage (Z)
            0x09, 0x35,                     //   Usage (Rz)
            0x81, 0x02,                     //   INPUT (Data, Var, Abs)
            // Buttons 1-10
            0x05, 0x09,                     //   Usage Page (Button)
            0x19, 0x01,                     //   Usage Minimum (1)
            0x29, 0x0A,                     //   Usage Maximum (10)
            0x15, 0x00,                     //   Logical Minimum (0)
            0x25, 0x01,                     //   Logical Maximum (1)
            0x75, 0x01,                     //   Report Size (1)
            0x95, 0x0A,                     //   Report Count (10)
            0x81, 0x02,                     //   INPUT (Data, Var, Abs)
            // 6-bit padding
            0x75, 0x06,                     //   Report Size (6)
            0x95, 0x01,                     //   Report Count (1)
            0x81, 0x01,                     //   INPUT (Cnst)
            // Hat switch
            0x05, 0x01,                     //   Usage Page (Generic Desktop)
            0x09, 0x39,                     //   Usage (Hat switch)
            0x15, 0x01,                     //   Logical Minimum (1)
            0x25, 0x08,                     //   Logical Maximum (8)
            0x35, 0x00,                     //   Physical Minimum (0)
            0x46, 0x3B, 0x01,               //   Physical Maximum (315)
            0x66, 0x14, 0x00,               //   Unit (Degrees)
            0x75, 0x04,                     //   Report Size (4)
            0x95, 0x01,                     //   Report Count (1)
            0x81, 0x42,                     //   INPUT (Data, Var, Abs, Null)
            // 4-bit padding
            0x75, 0x04, 0x95, 0x01,
            0x15, 0x00, 0x25, 0x00,
            0x35, 0x00, 0x45, 0x00, 0x65, 0x00,
            0x81, 0x03,                     //   INPUT (Cnst, Var)
            0xC0                            // End Collection
        };

        ushort vid = 0x045E; // Microsoft
        ushort pid = 0x028E; // Xbox 360 Controller

        // Descriptor is hardcoded in driver — just open the existing device
        Console.Write("  Opening HID device... ");
        using var h = OpenHidDevice(vid, pid);
        if (h == null)
        {
            Console.WriteLine("FAILED");
            Console.WriteLine("  Device not found. Check Device Manager.");
            return 1;
        }

        // Feature report: Report ID 2 + 14 bytes of input data
        // The driver takes these bytes, prepends Report ID 1, and delivers as input
        Console.WriteLine("\n  Sending input via feature reports. Open joy.cpl to watch.");
        Console.WriteLine("  Ctrl+C to stop.\n");

        byte[] report = new byte[15]; // Report ID 2 + 14 bytes data
        report[0] = 0x02; // Feature Report ID

        var sw = Stopwatch.StartNew();
        int count = 0;

        while (!_cts.Token.IsCancellationRequested)
        {
            double t = sw.Elapsed.TotalSeconds;
            double angle = t * Math.PI * 2 * 0.5;

            // Left stick circle
            ushort lx = (ushort)(32768 + (int)(30000 * Math.Sin(angle)));
            ushort ly = (ushort)(32768 + (int)(30000 * Math.Cos(angle)));
            ushort rx = 32768, ry = 32768;
            ushort lt = 0, rt = 0;

            // Pack axes into feature data (starts at [1], report[0] is feature ID)
            report[1] = (byte)(lx & 0xFF); report[2] = (byte)(lx >> 8);
            report[3] = (byte)(ly & 0xFF); report[4] = (byte)(ly >> 8);
            report[5] = (byte)(rx & 0xFF); report[6] = (byte)(rx >> 8);
            report[7] = (byte)(ry & 0xFF); report[8] = (byte)(ry >> 8);
            report[9] = (byte)(lt & 0xFF); report[10] = (byte)(lt >> 8);
            report[11] = (byte)(rt & 0xFF); report[12] = (byte)(rt >> 8);

            // Buttons: press A every other second
            int btns = ((int)t % 2 == 0) ? 0x01 : 0x00;
            report[13] = (byte)(btns & 0xFF);

            // Hat: centered (0 = null)
            report[14] = 0;

            if (!HidD_SetFeature(h, report, (uint)report.Length))
            {
                int err = Marshal.GetLastWin32Error();
                if (count == 0)
                    Console.Error.WriteLine($"  HidD_SetFeature failed: {err} (0x{err:X})");
                if (count > 3) break;
            }

            count++;
            if (count % 100 == 0)
                Console.Write($"\r  Reports: {count}  LX={lx,5} LY={ly,5}  ");

            Thread.Sleep(4); // ~250 Hz
        }

        Console.WriteLine($"\n\n  Sent {count} reports.");
        return 0;
    }

    // ── DualSense ──

    static int TestDualSense()
    {
        Console.WriteLine("-- DualSense Emulation --\n");

        byte[] descriptor = {
            0x05, 0x01,                     // Usage Page (Generic Desktop)
            0x09, 0x05,                     // Usage (Game Pad)
            0xA1, 0x01,                     // Collection (Application)
            0x85, 0x01,                     //   Report ID (1)
            // 4 axes uint8 (LX, LY, RX, RY)
            0x09, 0x30, 0x09, 0x31, 0x09, 0x33, 0x09, 0x34,
            0x15, 0x00, 0x26, 0xFF, 0x00,
            0x75, 0x08, 0x95, 0x04, 0x81, 0x02,
            // 2 triggers uint8 (L2, R2)
            0x09, 0x32, 0x09, 0x35,
            0x95, 0x02, 0x81, 0x02,
            // 14 buttons
            0x05, 0x09, 0x19, 0x01, 0x29, 0x0E,
            0x15, 0x00, 0x25, 0x01,
            0x75, 0x01, 0x95, 0x0E, 0x81, 0x02,
            // 2-bit pad
            0x75, 0x02, 0x95, 0x01, 0x81, 0x01,
            // Hat
            0x05, 0x01, 0x09, 0x39,
            0x15, 0x00, 0x25, 0x07,
            0x35, 0x00, 0x46, 0x3B, 0x01, 0x65, 0x14,
            0x75, 0x04, 0x95, 0x01, 0x81, 0x42,
            // 4-bit pad
            0x75, 0x04, 0x95, 0x01,
            0x15, 0x00, 0x25, 0x00, 0x35, 0x00, 0x45, 0x00, 0x65, 0x00,
            0x81, 0x03,
            0xC0
        };

        ushort vid = 0x054C; // Sony
        ushort pid = 0x0CE6; // DualSense

        Console.Write("  Writing config to registry... ");
        WriteConfig(descriptor, vid, pid);
        Console.WriteLine("OK");

        RestartDevice();

        Console.Write("  Opening HID device... ");
        using var h = OpenHidDevice(vid, pid);
        if (h == null)
        {
            Console.WriteLine("FAILED");
            return 1;
        }

        Console.WriteLine("\n  Sending input reports. Open joy.cpl to watch.");
        Console.WriteLine("  Ctrl+C to stop.\n");

        // Report ID + 4 axes + 2 triggers + 2 bytes (buttons + hat) = 9 bytes
        byte[] report = new byte[9];
        report[0] = 0x01;

        var sw = Stopwatch.StartNew();
        int count = 0;

        while (!_cts.Token.IsCancellationRequested)
        {
            double t = sw.Elapsed.TotalSeconds;
            double angle = t * Math.PI * 2 * 0.5;

            report[1] = (byte)(128 + (int)(120 * Math.Sin(angle)));  // LX
            report[2] = (byte)(128 + (int)(120 * Math.Cos(angle)));  // LY
            report[3] = 128; // RX
            report[4] = 128; // RY
            report[5] = 0;   // L2
            report[6] = 0;   // R2
            report[7] = (byte)(((int)t % 2 == 0) ? 0x01 : 0x00); // Cross btn
            report[8] = 0x08; // Hat centered

            if (!WriteFile(h, report, (uint)report.Length, out uint written, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                if (count == 0)
                    Console.Error.WriteLine($"  WriteFile failed: {err} (0x{err:X}), written={written}");
                if (count > 3) break; // Allow a few retries
            }

            count++;
            if (count % 100 == 0)
                Console.Write($"\r  Reports: {count}  LX={report[1],3} LY={report[2],3}  ");

            Thread.Sleep(4);
        }

        Console.WriteLine($"\n\n  Sent {count} reports.");
        return 0;
    }

    // ── Cleanup ──

    static int RunCleanup()
    {
        Console.WriteLine("Running cleanup script...\n");
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -File \"{Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "cleanup.ps1")}\"",
            UseShellExecute = false
        };

        // Try direct path
        string scriptPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "cleanup.ps1"));
        if (!File.Exists(scriptPath))
        {
            scriptPath = @"C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\scripts\cleanup.ps1";
        }

        Console.WriteLine($"  Script: {scriptPath}");
        psi.Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\"";

        using var proc = Process.Start(psi);
        proc?.WaitForExit(30_000);
        return 0;
    }
}
