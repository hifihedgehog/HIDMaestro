$code = @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class H {
    [DllImport("hid.dll")]
    public static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, uint len);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
}
'@
Add-Type -TypeDefinition $code
# Known path pattern
$path = '\\?\HID#VID_045E&PID_028E&IG_00#1&8326b1a&24e&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}'
Write-Output "Opening: $path"
$h = [H]::CreateFileW($path, 0x40000000, 3, [IntPtr]::Zero, 3, 0, [IntPtr]::Zero)
if ($h.IsInvalid) {
    Write-Output "CreateFile failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    exit 1
}
Write-Output "Opened OK"
$report = [byte[]]@(0x00, 0xAA, 0xBB, 0xCC, 0xDD)
$ok = [H]::HidD_SetOutputReport($h, $report, 5)
$err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
Write-Output "HidD_SetOutputReport rc=$ok err=$err"
$h.Close()
