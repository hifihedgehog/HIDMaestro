$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\xusb_data.log"
"" | Out-File -Encoding ASCII $log
function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $log }

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class XD {
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern IntPtr CreateFileW(string f, int a, int sh, IntPtr sec, int disp, int fl, IntPtr t);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool DeviceIoControl(IntPtr h, uint code, byte[] ib, int isz, byte[] ob, int osz, out int ret, IntPtr ovl);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    public static readonly IntPtr INVALID = new IntPtr(-1);

    public static string Test(string path) {
        var sb = new System.Text.StringBuilder();
        IntPtr h = CreateFileW(path, unchecked((int)0xC0000000), 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == INVALID) return "OPEN FAILED: " + Marshal.GetLastWin32Error();
        sb.AppendLine("OPEN OK");

        // Send input data via SET_STATE (14 bytes > 5 = input mode)
        // Pattern: LX=0x7FFF (32767), LY=0x8001, rest zeros
        byte[] input = new byte[14];
        input[0] = 0xFF; input[1] = 0x7F;  // LX = 32767
        input[2] = 0x01; input[3] = 0x80;  // LY = 32769
        input[4] = 0x00; input[5] = 0x40;  // RX = 16384
        input[6] = 0x00; input[7] = 0xC0;  // RY = 49152
        input[12] = 0x01;  // Button A

        int ret;
        bool ok = DeviceIoControl(h, 0x8000A010, input, 14, null, 0, out ret, IntPtr.Zero);
        sb.AppendLine("SET_STATE(14B): ok=" + ok + " err=" + Marshal.GetLastWin32Error() + " ret=" + ret);

        // Now read GET_STATE
        byte[] inState = new byte[] { 0x01, 0x01, 0x00 };
        byte[] outState = new byte[29];
        ok = DeviceIoControl(h, 0x8000E00C, inState, 3, outState, 29, out ret, IntPtr.Zero);
        sb.AppendLine("GET_STATE: ok=" + ok + " bytes=" + ret + " err=" + Marshal.GetLastWin32Error());
        if (ok && ret >= 17) {
            sb.AppendLine("  status=" + outState[2]);
            sb.AppendLine("  pkt=" + BitConverter.ToUInt32(outState, 5));
            sb.AppendLine("  buttons=0x" + BitConverter.ToUInt16(outState, 0x0B).ToString("X4"));
            sb.AppendLine("  LT=" + outState[0x0D] + " RT=" + outState[0x0E]);
            sb.AppendLine("  LX=" + BitConverter.ToInt16(outState, 0x0F));
            sb.AppendLine("  LY=" + BitConverter.ToInt16(outState, 0x11));
            sb.AppendLine("  RX=" + BitConverter.ToInt16(outState, 0x13));
            sb.AppendLine("  RY=" + BitConverter.ToInt16(outState, 0x15));
        }

        CloseHandle(h);
        return sb.ToString();
    }
}
"@

$path = "\\?\root#system#0010#{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}"
Log "Testing: $path"
Log ([XD]::Test($path))
