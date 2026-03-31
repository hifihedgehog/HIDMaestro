param([string]$ProfileId = "xbox-360-wired")
$ErrorActionPreference = "Continue"
$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\quick_test.log"
$exe = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\publish_out\HIDMaestroTest.exe"
$checkWinmm = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\scripts\check_winmm.ps1"

"" | Out-File -Encoding ASCII $log
function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $log }

Log "=== Quick Test: $ProfileId ==="

# Start emulation in background
$p = Start-Process $exe -ArgumentList "emulate",$ProfileId -PassThru -NoNewWindow -RedirectStandardOutput "$log.out" -RedirectStandardError "$log.err"

# Wait 5 seconds for device to come up
Start-Sleep 5

# Check WinMM from separate process
Log "`n[WinMM]"
$wm = powershell -NoProfile -ExecutionPolicy Bypass -File $checkWinmm 2>&1
foreach ($l in $wm) { Log "  $l" }

# Check HID product string
Log "`n[HID]"
$hidChild = Get-PnpDevice 2>$null | Where-Object { $_.InstanceId -like "HID\HIDCLASS\*" -and $_.Status -eq "OK" }
if ($hidChild) {
    Log "  Child: $($hidChild.FriendlyName)"
    $busDesc = (Get-PnpDeviceProperty -InstanceId $hidChild.InstanceId -KeyName DEVPKEY_Device_BusReportedDeviceDesc -EA SilentlyContinue).Data
    Log "  BusReportedDeviceDesc: '$busDesc'"
}

# Emulation output
Log "`n[Output]"
if (Test-Path "$log.out") { Get-Content "$log.out" -EA SilentlyContinue | ForEach-Object { Log "  $_" } }

Stop-Process -Id $p.Id -Force -EA SilentlyContinue
Log "`nDone."
