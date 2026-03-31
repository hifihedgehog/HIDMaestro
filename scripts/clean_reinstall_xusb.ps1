$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"
$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\xusb_clean.log"
"" | Out-File -Encoding ASCII $log
function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $log }

# Kill emulate
Get-Process HIDMaestroTest -EA SilentlyContinue | Stop-Process -Force

# Remove ALL XUSB device instances
Log "Removing ALL XInput Bridge devices..."
Get-PnpDevice 2>$null | Where-Object { $_.FriendlyName -match "XInput Bridge" } | ForEach-Object {
    pnputil /remove-device $_.InstanceId /subtree 2>&1 | Out-Null
    Log "  Removed $($_.InstanceId)"
}

# Remove ALL XUSB drivers from store
$drivers = pnputil /enum-drivers 2>&1 | Out-String
$currentOem = $null
foreach ($line in ($drivers -split "`n")) {
    if ($line -match "Published Name:\s+(oem\d+\.inf)") { $currentOem = $Matches[1].Trim() }
    if ($currentOem -and $line -match "hidmaestro_xusb") {
        pnputil /delete-driver $currentOem /force /uninstall 2>&1 | Out-Null
        Log "  Removed driver $currentOem"
        $currentOem = $null
    }
    if ([string]::IsNullOrWhiteSpace($line)) { $currentOem = $null }
}

Start-Sleep 1

# Re-sign
& "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\scripts\resign_xusb.ps1" 2>&1 | Out-Null

# Add XUSB driver
$addResult = pnputil /add-driver "$build\hidmaestro_xusb.inf" 2>&1 | Out-String
Log "Added: $($addResult.Trim())"

# Create ONE device
Add-Type -TypeDefinition @"
using System; using System.Text; using System.Runtime.InteropServices;
public class XCR {
    static readonly Guid G = new Guid("4D36E97D-E325-11CE-BFC1-08002BE10318");
    [StructLayout(LayoutKind.Sequential)] struct S { public int sz; public Guid g; public int d; public IntPtr r; }
    [DllImport("setupapi.dll",SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid g,IntPtr h);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiCreateDeviceInfoW(IntPtr s,string n,ref Guid g,string d,IntPtr h,int f,ref S p);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr s,ref S d,int p,byte[] b,int sz);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiCallClassInstaller(int f,IntPtr s,ref S d);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    public static void Go() {
        Guid g=G; byte[] hb=Encoding.Unicode.GetBytes("root\\HIDMaestroXUSB\0\0");
        IntPtr ds=SetupDiCreateDeviceInfoList(ref g,IntPtr.Zero);
        var d=new S{sz=Marshal.SizeOf(typeof(S))};
        SetupDiCreateDeviceInfoW(ds,"System",ref g,"HIDMaestro XInput Bridge",IntPtr.Zero,1,ref d);
        SetupDiSetDeviceRegistryPropertyW(ds,ref d,1,hb,hb.Length);
        SetupDiCallClassInstaller(0x19,ds,ref d);
        SetupDiDestroyDeviceInfoList(ds);
    }
}
"@
[XCR]::Go()
Log "Device created"

Start-Sleep 8

# Find and restart if needed
$dev = Get-PnpDevice 2>$null | Where-Object { $_.FriendlyName -match "XInput Bridge" }
if ($dev) {
    if ($dev.Status -ne "OK") {
        pnputil /restart-device $dev.InstanceId 2>&1 | Out-Null
        Start-Sleep 3
        $dev = Get-PnpDevice | Where-Object { $_.InstanceId -eq $dev.InstanceId }
    }
    Log "XUSB: $($dev.InstanceId) Status=$($dev.Status)"
    pnputil /enum-devices /instanceid $dev.InstanceId /stack 2>&1 | Select-String "Stack:" | ForEach-Object { Log $_.ToString().Trim() }
} else {
    Log "XUSB NOT FOUND"
}

# Count total XInput controllers
Log "`nXInput check:"
& powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\scripts\check_xinput.ps1" 2>&1 | ForEach-Object { Log "  $_" }
