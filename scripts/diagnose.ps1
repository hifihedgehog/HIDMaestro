$logFile = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\diagnose.log"
$exe = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\publish_out\HIDMaestroTest.exe"

"" | Out-File -Encoding ASCII $logFile

function Log($msg) {
    Write-Host $msg
    $msg | Out-File -Append -Encoding ASCII $logFile
}

Log "=== HIDMaestro Diagnostics ==="

# Registry
Log "`n[Registry]"
$reg = Get-ItemProperty "HKLM:\SOFTWARE\HIDMaestro" -ErrorAction SilentlyContinue
if ($reg) {
    Log "  VendorId:      $($reg.VendorId) (0x$($reg.VendorId.ToString('X4')))"
    Log "  ProductId:     $($reg.ProductId) (0x$($reg.ProductId.ToString('X4')))"
    Log "  ProductString: $($reg.ProductString)"
    $desc = $reg.ReportDescriptor
    if ($desc) { Log "  Descriptor:    $($desc.Length) bytes" }
} else {
    Log "  NOT FOUND"
}

# Root device
Log "`n[Root Device]"
$rootDev = pnputil /enum-devices /instanceid "ROOT\HIDCLASS\0000" 2>&1 | Out-String
Log $rootDev.Trim()

# HID children
Log "`n[HID Children]"
$hidDevs = Get-PnpDevice -Class HIDClass -Status OK 2>$null | Where-Object { $_.InstanceId -match "HIDCLASS" }
foreach ($d in $hidDevs) {
    Log "  $($d.InstanceId) — $($d.FriendlyName)"
}
if (-not $hidDevs) { Log "  No HIDCLASS HID children found" }

# Game controllers in joy.cpl view
Log "`n[Game Controllers]"
$joysticks = Get-PnpDevice -Class HIDClass 2>$null | Where-Object { $_.FriendlyName -match "game controller|gamepad|joystick|xbox|controller" -and $_.Status -eq "OK" }
foreach ($j in $joysticks) {
    Log "  $($j.InstanceId) — $($j.FriendlyName)"
}
if (-not $joysticks) { Log "  No game controllers found" }

# WUDFHost driver files
Log "`n[Driver Files]"
$driverInfo = pnputil /enum-devices /instanceid "ROOT\HIDCLASS\0000" /drivers 2>&1 | Out-String
Log $driverInfo.Trim()

Start-Sleep 3
