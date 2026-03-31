$logFile = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\joycpl.log"
"" | Out-File -Encoding ASCII $logFile

function Log($msg) {
    Write-Host $msg
    $msg | Out-File -Append -Encoding ASCII $logFile
}

Log "=== Joy.cpl / WinMM Joystick Check ==="

# Check via WinMM API (joyGetDevCaps) - this is what joy.cpl uses
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class JoyCheck {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct JOYCAPSW {
        public ushort wMid, wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string szPname;
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax;
        public uint wNumButtons, wPeriodMin, wPeriodMax;
        public uint wRmin, wRmax, wUmin, wUmax, wVmin, wVmax;
        public uint wCaps;
        public uint wMaxAxes, wNumAxes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string szOEMVxD;
    }
    [DllImport("winmm.dll")] public static extern uint joyGetNumDevs();
    [DllImport("winmm.dll", CharSet=CharSet.Unicode)]
    public static extern uint joyGetDevCapsW(uint id, ref JOYCAPSW caps, uint sz);

    public static string Scan() {
        var sb = new StringBuilder();
        uint num = joyGetNumDevs();
        sb.AppendLine("  joyGetNumDevs: " + num);
        for (uint i = 0; i < num && i < 16; i++) {
            var caps = new JOYCAPSW();
            uint r = joyGetDevCapsW(i, ref caps, (uint)Marshal.SizeOf(caps));
            if (r == 0) {
                sb.AppendLine(string.Format("  Joy {0}: MID={1:X4} PID={2:X4} Name=\"{3}\" Buttons={4} Axes={5}",
                    i, caps.wMid, caps.wPid, caps.szPname, caps.wNumButtons, caps.wNumAxes));
            }
        }
        return sb.ToString();
    }
}
"@

$result = [JoyCheck]::Scan()
Log $result

# Also check registry where joy.cpl stores config
Log "`n[Registry - MediaResources\Joystick]"
$joyReg = Get-ChildItem "HKCU:\System\CurrentControlSet\Control\MediaResources\Joystick" -ErrorAction SilentlyContinue
if ($joyReg) {
    foreach ($k in $joyReg) {
        Log "  $($k.PSChildName)"
        $vals = Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue
        if ($vals.OEMName) { Log "    OEMName: $($vals.OEMName)" }
    }
} else { Log "  (no joystick registry entries)" }

# Check CurrentControlSet joystick mapping
Log "`n[Registry - MediaProperties\Joystick]"
$mp = Get-ChildItem "HKLM:\System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM" -ErrorAction SilentlyContinue
if ($mp) {
    foreach ($k in $mp) {
        $vals = Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue
        if ($vals.OEMName) { Log "  $($k.PSChildName): $($vals.OEMName)" }
    }
} else { Log "  (no OEM joystick entries)" }

Start-Sleep 3
