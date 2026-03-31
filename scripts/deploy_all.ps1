$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"
$scripts = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\scripts"
$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\deploy_all.log"
"" | Out-File -Encoding ASCII $log
function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $log }

Log "=== Full Deploy (HID + XUSB) ==="

# Step 1: Set XusbMode=1 so XUSB device initializes correctly
$regKey = "HKLM:\SOFTWARE\HIDMaestro"
if (-not (Test-Path $regKey)) { New-Item -Path $regKey -Force | Out-Null }
Set-ItemProperty $regKey -Name "XusbMode" -Value 1 -Type DWord
Log "[1] XusbMode=1"

# Step 2: Clean deploy HID device (this also rebuilds/signs)
Log "[2] Deploying HID device..."
& powershell -ExecutionPolicy Bypass -File "$scripts\clean_and_deploy.ps1" 2>&1 | Out-Null
$hidStatus = pnputil /enum-devices /instanceid "ROOT\HIDCLASS\0000" 2>&1 | Select-String "Status:"
Log "  HID: $($hidStatus.ToString().Trim())"

# Step 3: Clear XusbMode=0 so HID device can restart properly
Set-ItemProperty $regKey -Name "XusbMode" -Value 0 -Type DWord

# Step 4: Re-sign XUSB INF and deploy
Log "[3] Deploying XUSB device..."
& powershell -ExecutionPolicy Bypass -File "$scripts\resign_xusb.ps1" 2>&1 | Out-Null

# Set XusbMode=1 again for XUSB device
Set-ItemProperty $regKey -Name "XusbMode" -Value 1 -Type DWord

# Remove old XUSB
Get-PnpDevice 2>$null | Where-Object { $_.FriendlyName -match "XInput Bridge" } | ForEach-Object {
    pnputil /remove-device $_.InstanceId /subtree 2>&1 | Out-Null
}
$drivers = pnputil /enum-drivers 2>&1 | Out-String
$currentOem = $null
foreach ($line in ($drivers -split "`n")) {
    if ($line -match "Published Name:\s+(oem\d+\.inf)") { $currentOem = $Matches[1].Trim() }
    if ($currentOem -and $line -match "hidmaestro_xusb") {
        pnputil /delete-driver $currentOem /force 2>&1 | Out-Null
        $currentOem = $null
    }
    if ([string]::IsNullOrWhiteSpace($line)) { $currentOem = $null }
}

# Add XUSB driver to store
pnputil /add-driver "$build\hidmaestro_xusb.inf" 2>&1 | Out-Null

# Create XUSB device node
Add-Type -TypeDefinition @"
using System; using System.Text; using System.Runtime.InteropServices;
public class XDA {
    static readonly Guid G = new Guid("4D36E97D-E325-11CE-BFC1-08002BE10318");
    [StructLayout(LayoutKind.Sequential)] struct S { public int sz; public Guid g; public int d; public IntPtr r; }
    [DllImport("setupapi.dll",SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid g,IntPtr h);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiCreateDeviceInfoW(IntPtr s,string n,ref Guid g,string d,IntPtr h,int f,ref S p);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr s,ref S d,int p,byte[] b,int sz);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiCallClassInstaller(int f,IntPtr s,ref S d);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    public static void Create() {
        Guid g = G; byte[] hb = Encoding.Unicode.GetBytes("root\\HIDMaestroXUSB\0\0");
        IntPtr ds = SetupDiCreateDeviceInfoList(ref g, IntPtr.Zero);
        var d = new S { sz = Marshal.SizeOf(typeof(S)) };
        SetupDiCreateDeviceInfoW(ds, "System", ref g, "HIDMaestro XInput Bridge", IntPtr.Zero, 1, ref d);
        SetupDiSetDeviceRegistryPropertyW(ds, ref d, 1, hb, hb.Length);
        SetupDiCallClassInstaller(0x19, ds, ref d);
        SetupDiDestroyDeviceInfoList(ds);
    }
}
"@
[XDA]::Create()

# Wait for driver to load
Start-Sleep 8

$xusb = Get-PnpDevice 2>$null | Where-Object { $_.FriendlyName -match "XInput Bridge" }
if ($xusb) {
    Log "  XUSB: $($xusb.InstanceId) Status=$($xusb.Status)"
    # Check XUSB interface
    $ifaces = pnputil /enum-interfaces /class "{EC87F1E3-C13B-4100-B5F7-8B84D54260CB}" 2>&1 | Select-String "Interface Path:|Interface Status:"
    foreach ($i in $ifaces) { Log "  $($i.ToString().Trim())" }
} else {
    Log "  XUSB device NOT FOUND"
}

# Clear XusbMode
Set-ItemProperty $regKey -Name "XusbMode" -Value 0 -Type DWord
Log "[4] XusbMode=0"

Log "`n=== Deploy Complete ==="
