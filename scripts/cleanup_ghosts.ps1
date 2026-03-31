# Remove all ghost (Unknown status) HID children from previous HIDMaestro sessions
$ghosts = Get-PnpDevice -Class HIDClass 2>$null | Where-Object {
    $_.InstanceId -like "HID\HIDCLASS*" -and $_.Status -ne "OK"
}
Write-Host "Found $($ghosts.Count) ghost HID children"
foreach ($g in $ghosts) {
    pnputil /remove-device $g.InstanceId /subtree 2>&1 | Out-Null
}
Write-Host "Cleanup complete"
