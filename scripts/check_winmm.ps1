Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WinMMCheck {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct JOYCAPSW {
        public ushort wMid, wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string szPname;
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax;
        public uint wNumButtons, wPeriodMin, wPeriodMax;
        public uint wRmin, wRmax, wUmin, wUmax, wVmin, wVmax;
        public uint wCaps; public uint wMaxAxes, wNumAxes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string szOEMVxD;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct JOYINFOEX {
        public uint dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos;
        public uint dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
    }
    [DllImport("winmm.dll", CharSet=CharSet.Unicode)]
    public static extern uint joyGetDevCapsW(uint id, ref JOYCAPSW caps, uint sz);
    [DllImport("winmm.dll")]
    public static extern uint joyGetPosEx(uint id, ref JOYINFOEX info);
    [DllImport("winmm.dll")]
    public static extern uint joyGetNumDevs();
}
"@

$numDevs = [WinMMCheck]::joyGetNumDevs()
Write-Output "joyGetNumDevs: $numDevs"

$found = $false
for ($i = 0; $i -lt 16; $i++) {
    # Try joyGetDevCapsW
    $caps = New-Object WinMMCheck+JOYCAPSW
    $capsResult = [WinMMCheck]::joyGetDevCapsW($i, [ref]$caps, [System.Runtime.InteropServices.Marshal]::SizeOf($caps))

    # Try joyGetPosEx regardless
    $info = New-Object WinMMCheck+JOYINFOEX
    $info.dwSize = [System.Runtime.InteropServices.Marshal]::SizeOf($info)
    $info.dwFlags = 0x7F
    $posResult = [WinMMCheck]::joyGetPosEx($i, [ref]$info)

    if ($capsResult -eq 0 -or $posResult -eq 0) {
        $found = $true
        $name = if ($capsResult -eq 0) { $caps.szPname } else { "(caps failed)" }
        Write-Output "SLOT $i : CapsErr=$capsResult PosErr=$posResult Name=`"$name`" X=$($info.dwXpos) Y=$($info.dwYpos) Btns=0x$($info.dwButtons.ToString('X'))"
    }
}
if (-not $found) { Write-Output "No joysticks detected on any slot" }
