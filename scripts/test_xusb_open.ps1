$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\xusb_open.log"
"" | Out-File -Encoding ASCII $log
function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $log }

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class XOpen2 {
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern IntPtr CreateFileW(string f, int a, int sh, IntPtr sec, int disp, int fl, IntPtr t);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool DeviceIoControl(IntPtr h, uint code, byte[] ib, int isz, byte[] ob, int osz, out int ret, IntPtr ovl);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    public static readonly IntPtr INVALID = new IntPtr(-1);
    public const int RW = unchecked((int)0xC0000000);

    public static string Test(string path) {
        var sb = new System.Text.StringBuilder();
        IntPtr h = CreateFileW(path, RW, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        int err = Marshal.GetLastWin32Error();
        if (h == INVALID) { sb.AppendLine("OPEN FAILED: " + err); return sb.ToString(); }
        sb.AppendLine("OPEN SUCCESS");

        byte[] ob = new byte[12];
        int ret;
        bool ok = DeviceIoControl(h, 0x80006000, null, 0, ob, 12, out ret, IntPtr.Zero);
        err = Marshal.GetLastWin32Error();
        sb.AppendLine("GET_INFO: ok=" + ok + " bytes=" + ret + " err=" + err);
        if (ok && ret >= 12) {
            sb.AppendLine("  Version=0x" + BitConverter.ToUInt16(ob,0).ToString("X4") +
                " devIdx=" + ob[2] +
                " VID=0x" + BitConverter.ToUInt16(ob,8).ToString("X4") +
                " PID=0x" + BitConverter.ToUInt16(ob,10).ToString("X4"));
        }

        // GET_STATE
        byte[] inBuf = new byte[] { 0x01, 0x01, 0x00 };
        byte[] outBuf = new byte[29];
        ok = DeviceIoControl(h, 0x8000E00C, inBuf, 3, outBuf, 29, out ret, IntPtr.Zero);
        err = Marshal.GetLastWin32Error();
        sb.AppendLine("GET_STATE: ok=" + ok + " bytes=" + ret + " err=" + err);
        if (ok && ret >= 3) {
            sb.AppendLine("  status=" + outBuf[2] + " (1=connected) pkt=" + BitConverter.ToUInt32(outBuf, 5));
        }

        CloseHandle(h);
        return sb.ToString();
    }
}
"@

$path = "\\?\root#hidclass#0000#{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}"
Log "Testing: $path"
Log ([XOpen2]::Test($path))
