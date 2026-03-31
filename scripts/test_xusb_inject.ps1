$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\xusb_inject.log"
"" | Out-File -Encoding ASCII $log
function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $log }

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class XI3 {
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern IntPtr CreateFileW(string f, int a, int sh, IntPtr sec, int disp, int fl, IntPtr t);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool DeviceIoControl(IntPtr h, uint code, byte[] ib, int isz, byte[] ob, int osz, out int ret, IntPtr ovl);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    public static readonly IntPtr BAD = new IntPtr(-1);

    public static string Inject(string path) {
        var sb = new System.Text.StringBuilder();
        IntPtr h = CreateFileW(path, unchecked((int)0xC0000000), 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == BAD) return "OPEN FAIL: " + Marshal.GetLastWin32Error();

        // Piggyback: send input data via GET_STATE
        // Input = 3 bytes header (version + slot) + 14 bytes report data
        byte[] inS = new byte[17]; // 3 + 14
        inS[0] = 0x01; inS[1] = 0x01; inS[2] = 0x00; // v1.1, slot 0
        // Report data starting at offset 3:
        inS[3] = 0xFF; inS[4] = 0x7F;  // LX = 0x7FFF = 32767
        inS[5] = 0x00; inS[6] = 0x80;  // LY = 0x8000 = 32768
        inS[7] = 0x00; inS[8] = 0x40;  // RX = 0x4000 = 16384
        inS[15] = 0x01; // Button A

        int ret;
        // Send a few times to make sure it sticks
        for (int i = 0; i < 3; i++) {
            DeviceIoControl(h, 0x8000E00C, inS, 17, new byte[29], 29, out ret, IntPtr.Zero);
        }

        // Read GET_STATE normally (3 bytes input)
        byte[] inS2 = new byte[] { 0x01, 0x01, 0x00 };
        byte[] outS = new byte[29];
        bool ok2 = DeviceIoControl(h, 0x8000E00C, inS2, 3, outS, 29, out ret, IntPtr.Zero);
        sb.AppendLine("GET_STATE: ok=" + ok2 + " bytes=" + ret);
        sb.AppendLine("  status=" + outS[2] + " pkt=" + BitConverter.ToUInt32(outS, 5));
        sb.AppendLine("  buttons=0x" + BitConverter.ToUInt16(outS, 0x0B).ToString("X4"));
        sb.AppendLine("  LX=" + BitConverter.ToInt16(outS, 0x0F));
        sb.AppendLine("  LY=" + BitConverter.ToInt16(outS, 0x11));
        sb.AppendLine("  RX=" + BitConverter.ToInt16(outS, 0x13));
        sb.AppendLine("  RY=" + BitConverter.ToInt16(outS, 0x15));
        sb.AppendLine("  Raw hex: " + BitConverter.ToString(outS));

        CloseHandle(h);
        return sb.ToString();
    }
}
"@

# Find the current XUSB interface path dynamically
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class XFind {
    static readonly Guid G = new Guid("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");
    [DllImport("setupapi.dll",SetLastError=true)] public static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr h, uint f);
    [DllImport("setupapi.dll",SetLastError=true)] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [StructLayout(LayoutKind.Sequential)] public struct SP { public int cbSize; public Guid g; public int f; public IntPtr r; }
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref SP did);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr s, ref SP did, IntPtr det, uint sz, out uint req, IntPtr d);
    public static string Find() {
        Guid g = G;
        IntPtr ds = SetupDiGetClassDevsW(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
        for (uint i = 0; i < 8; i++) {
            var did = new SP { cbSize = Marshal.SizeOf(typeof(SP)) };
            if (!SetupDiEnumDeviceInterfaces(ds, IntPtr.Zero, ref g, i, ref did)) break;
            uint req;
            SetupDiGetDeviceInterfaceDetailW(ds, ref did, IntPtr.Zero, 0, out req, IntPtr.Zero);
            IntPtr det = Marshal.AllocHGlobal((int)req);
            Marshal.WriteInt32(det, 8);
            SetupDiGetDeviceInterfaceDetailW(ds, ref did, det, req, out req, IntPtr.Zero);
            string path = Marshal.PtrToStringUni(det + 4);
            Marshal.FreeHGlobal(det);
            // Only use root\system paths (our device, not the real Xbox BT one)
            if (path.Contains("root#system")) {
                SetupDiDestroyDeviceInfoList(ds);
                return path;
            }
        }
        SetupDiDestroyDeviceInfoList(ds);
        return null;
    }
}
"@

$xusbPath = [XFind]::Find()
if ($xusbPath) {
    Log "Found: $xusbPath"
    Log ([XI3]::Inject($xusbPath))
} else {
    Log "XUSB interface not found"
}
