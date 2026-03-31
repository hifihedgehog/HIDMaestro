$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"

# Kill emulate
Get-Process HIDMaestroTest -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 1

# Remove old XUSB devices
Get-PnpDevice 2>$null | Where-Object { $_.FriendlyName -match "XInput Bridge" } | ForEach-Object {
    pnputil /remove-device $_.InstanceId /subtree 2>&1 | Out-Null
    Write-Host "Removed $($_.InstanceId)"
}

# Remove ALL HIDMaestro XUSB drivers from store
$drivers = pnputil /enum-drivers 2>&1 | Out-String
$currentOem = $null
foreach ($line in ($drivers -split "`n")) {
    if ($line -match "Published Name:\s+(oem\d+\.inf)") { $currentOem = $Matches[1].Trim() }
    if ($currentOem -and $line -match "hidmaestro_xusb") {
        Write-Host "Removing $currentOem"
        pnputil /delete-driver $currentOem /force /uninstall 2>&1 | Out-Null
        $currentOem = $null
    }
    if ([string]::IsNullOrWhiteSpace($line)) { $currentOem = $null }
}
Start-Sleep 1

# Re-sign
& "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\scripts\resign_xusb.ps1" 2>&1 | Out-Null

# Verify DLL timestamp
$dll = Get-Item "$build\HIDMaestroXUSB.dll"
Write-Host "Build DLL: $($dll.LastWriteTime)"

# Add fresh driver
$result = pnputil /add-driver "$build\hidmaestro_xusb.inf" 2>&1 | Out-String
Write-Host $result.Trim()

# Check store DLL
$storeDll = Get-ChildItem "C:\Windows\System32\DriverStore\FileRepository" -Recurse -Filter "HIDMaestroXUSB.dll" -EA SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "Store DLL: $($storeDll.LastWriteTime)"

# Create device node
& "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\scripts\create_xusb_v4.ps1" 2>&1 | Out-Null
Start-Sleep 8

# Check
$dev = Get-PnpDevice 2>$null | Where-Object { $_.FriendlyName -match "XInput Bridge" -and $_.Status -ne "Unknown" }
if ($dev) {
    Write-Host "XUSB: $($dev.InstanceId) Status=$($dev.Status)"
    pnputil /enum-devices /instanceid $dev.InstanceId /stack 2>&1
} else {
    Write-Host "XUSB not found. Trying restart..."
    $any = Get-PnpDevice 2>$null | Where-Object { $_.FriendlyName -match "XInput Bridge" }
    if ($any) {
        pnputil /restart-device $any.InstanceId 2>&1
        Start-Sleep 5
        pnputil /enum-devices /instanceid $any.InstanceId /stack 2>&1
    }
}
