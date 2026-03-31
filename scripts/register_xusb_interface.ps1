$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\xusb_iface.log"
"" | Out-File -Encoding ASCII $log
function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $log }

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class CMReg2 {
    [DllImport("CfgMgr32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);
    [DllImport("CfgMgr32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);
    [DllImport("CfgMgr32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern uint CM_Register_Device_InterfaceW(uint dnDevInst, ref Guid InterfaceClassGuid, string pszReference, byte[] pszDeviceInterface, ref uint pulLength, uint ulFlags);
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    public static extern IntPtr CreateFileW(string f, uint a, uint sh, IntPtr sec, uint disp, uint fl, IntPtr t);
    [DllImport("kernel32.dll",SetLastError=true)]
    public static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, uint inSz, byte[] outBuf, uint outSz, out uint ret, IntPtr ovl);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
}
"@

$xusbGuid = [Guid]::new("EC87F1E3-C13B-4100-B5F7-8B84D54260CB")

# Find HID child
$rootInst = [uint32]0
[CMReg2]::CM_Locate_DevNodeW([ref]$rootInst, "ROOT\HIDCLASS\0000", 0) | Out-Null
$childInst = [uint32]0
[CMReg2]::CM_Get_Child([ref]$childInst, $rootInst, 0) | Out-Null
Log "Root=$rootInst Child=$childInst"

# Register XUSB interface on HID child
$buf = New-Object byte[] 1024
$bufLen = [uint32]512
$regResult = [CMReg2]::CM_Register_Device_InterfaceW($childInst, [ref]$xusbGuid, $null, $buf, [ref]$bufLen, 0)
Log "Register result: $regResult"
if ($regResult -eq 0) {
    $ifacePath = [System.Text.Encoding]::Unicode.GetString($buf, 0, [int]($bufLen * 2)).TrimEnd([char]0)
    Log "Interface: $ifacePath"

    # Try to open it
    $h = [CMReg2]::CreateFileW($ifacePath, 0xC0000000, 3, [IntPtr]::Zero, 3, 0, [IntPtr]::Zero)
    if ($h -eq [IntPtr]::new(-1)) {
        Log "OPEN FAILED: $([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    } else {
        Log "OPEN SUCCESS!"
        # Send GET_INFORMATION
        $outBuf = New-Object byte[] 12
        $bytesRet = [uint32]0
        $ok = [CMReg2]::DeviceIoControl($h, 0x80006000, $null, 0, $outBuf, 12, [ref]$bytesRet, [IntPtr]::Zero)
        Log "GET_INFO: ok=$ok bytes=$bytesRet err=$([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())"
        if ($ok) {
            Log "  Version=0x$([BitConverter]::ToUInt16($outBuf, 0).ToString('X4')) deviceIdx=$($outBuf[2]) VID=0x$([BitConverter]::ToUInt16($outBuf, 8).ToString('X4')) PID=0x$([BitConverter]::ToUInt16($outBuf, 10).ToString('X4'))"
        }
        [CMReg2]::CloseHandle($h)
    }
} else {
    Log "Failed to register interface (result $regResult)"
}
Start-Sleep 2
