# Check real Xbox BT controller's driver stack and XInput interface
$btXbox = Get-PnpDevice | Where-Object { $_.InstanceId -match "0B13.*IG_00" -and $_.Status -eq "OK" }
if ($btXbox) {
    Write-Host "Real Xbox BT HID child: $($btXbox.InstanceId)"
    Write-Host "FriendlyName: $($btXbox.FriendlyName)"

    $hwids = (Get-PnpDeviceProperty -InstanceId $btXbox.InstanceId -KeyName DEVPKEY_Device_HardwareIds -EA SilentlyContinue).Data
    foreach ($h in $hwids) { Write-Host "  HWID: $h" }

    $compat = (Get-PnpDeviceProperty -InstanceId $btXbox.InstanceId -KeyName DEVPKEY_Device_CompatibleIds -EA SilentlyContinue).Data
    foreach ($c in $compat) { Write-Host "  CompatID: $c" }

    $upFilters = (Get-PnpDeviceProperty -InstanceId $btXbox.InstanceId -KeyName DEVPKEY_Device_UpperFilters -EA SilentlyContinue).Data
    if ($upFilters) { foreach ($f in $upFilters) { Write-Host "  UpperFilter: $f" } }

    # Check driver stack
    Write-Host "`nDriver Stack:"
    pnputil /enum-devices /instanceid $btXbox.InstanceId /stack 2>&1 | ForEach-Object { Write-Host "  $_" }
} else {
    Write-Host "No real Xbox BT controller found"
}

# Also check our virtual device stack
Write-Host "`n=== Our Virtual Device ==="
$ours = Get-PnpDevice | Where-Object { $_.InstanceId -like "HID\HIDCLASS\*" -and $_.Status -eq "OK" -and $_.InstanceId -notmatch "0B13" }
if ($ours) {
    Write-Host "Virtual: $($ours.InstanceId) - $($ours.FriendlyName)"
    pnputil /enum-devices /instanceid $ours.InstanceId /stack 2>&1 | ForEach-Object { Write-Host "  $_" }
}
