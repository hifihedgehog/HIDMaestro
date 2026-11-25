# Runs in the signed-in user's console session on the Surface, started
# hidden by launch.vbs from a one-shot scheduled task. Records, for
# -Seconds, what Windows' pointer stack shows: the cursor position every
# 10 ms when it changes, and the pointer device list when it changes, each
# line stamped with UTC milliseconds. At the start it writes the screen size
# and, for each candidate point, whether that point is bare desktop: the
# window there is the desktop's and UI Automation finds the desktop there,
# its pane or its list, not an icon (ListItem),
# and checks the points again every second.
# Touches nothing: it only reads.
param(
    [int]$Seconds = 150,
    [string]$Out = 'C:\HIDMaestro\obs\obs.log',
    [string]$Points = '80,40;70,30;85,60;60,50;75,75'
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Obs
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct POINTER_DEVICE_INFO
    {
        public uint displayOrientation;
        public IntPtr device;
        public int pointerDeviceType;
        public IntPtr monitor;
        public uint startingCursorId;
        public ushort maxActiveContacts;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 520)] public string productString;
    }
    [DllImport("user32.dll")] public static extern bool GetPointerDevices(ref uint count, [Out] POINTER_DEVICE_INFO[] devices);
    public static string ClassOf(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
    public static string Devices()
    {
        uint n = 0;
        GetPointerDevices(ref n, null);
        if (n == 0) return "none";
        var d = new POINTER_DEVICE_INFO[n];
        if (!GetPointerDevices(ref n, d)) return "error";
        var b = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            string kind = d[i].pointerDeviceType == 1 ? "integrated-pen" : d[i].pointerDeviceType == 2 ? "external-pen"
                        : d[i].pointerDeviceType == 3 ? "touch" : d[i].pointerDeviceType == 4 ? "touchpad" : "type" + d[i].pointerDeviceType;
            if (b.Length > 0) b.Append(" | ");
            b.Append(kind).Append(' ').Append(d[i].maxActiveContacts).Append(' ').Append(d[i].productString);
        }
        return b.ToString();
    }
}
'@
[void][Obs]::SetProcessDpiAwarenessContext([IntPtr](-4))
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$w = New-Object System.IO.StreamWriter($Out, $false)
$w.AutoFlush = $true
function Stamp { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
$cx = [Obs]::GetSystemMetrics(0); $cy = [Obs]::GetSystemMetrics(1)
$w.WriteLine("$(Stamp) SCREEN $cx $cy session $([Diagnostics.Process]::GetCurrentProcess().SessionId)")
function Check-Points {
foreach ($pt in $Points -split ';') {
        $px, $py = $pt -split ','
        $p = New-Object Obs+POINT
        $p.X = [int]([double]$px / 100 * $cx); $p.Y = [int]([double]$py / 100 * $cy)
        $h = [Obs]::WindowFromPoint($p)
        $root = [Obs]::GetAncestor($h, 2)
        $cls = [Obs]::ClassOf($h); $rcls = [Obs]::ClassOf($root)
        $type = 'unknown'
        try {
            $el = [System.Windows.Automation.AutomationElement]::FromPoint((New-Object System.Windows.Point($p.X, $p.Y)))
            $type = $el.Current.ControlType.ProgrammaticName
        } catch { $type = 'error' }
        $bare = ($rcls -eq 'Progman' -or $rcls -eq 'WorkerW') -and ($type -eq 'ControlType.List' -or $type -eq 'ControlType.Pane')
        $w.WriteLine("$(Stamp) $(if ($bare) { 'BARE' } else { 'NOTBARE' }) $px $py at $($p.X),$($p.Y) window $cls root $rcls element $type")
    }
}
Check-Points
$w.WriteLine("$(Stamp) DEVICES $([Obs]::Devices())")
$lastDevices = ''
$last = New-Object Obs+POINT
$sw = [Diagnostics.Stopwatch]::StartNew()
$nextDevices = 0
$nextPoints = 1000
while ($sw.ElapsedMilliseconds -lt $Seconds * 1000) {
    $c = New-Object Obs+POINT
    [void][Obs]::GetCursorPos([ref]$c)
    if ($c.X -ne $last.X -or $c.Y -ne $last.Y) { $w.WriteLine("$(Stamp) CURSOR $($c.X) $($c.Y)"); $last = $c }
    if ($sw.ElapsedMilliseconds -ge $nextDevices) {
        $d = [Obs]::Devices()
        if ($d -ne $lastDevices) { $w.WriteLine("$(Stamp) DEVICES $d"); $lastDevices = $d }
        $nextDevices = $sw.ElapsedMilliseconds + 500
    }
    if ($sw.ElapsedMilliseconds -ge $nextPoints) { Check-Points; $nextPoints = $sw.ElapsedMilliseconds + 1000 }
    Start-Sleep -Milliseconds 10
}
$w.WriteLine("$(Stamp) END")
$w.Close()
