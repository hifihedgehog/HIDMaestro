$ErrorActionPreference = "Continue"
$logFile = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\create_node_test.log"

# First check if driver is in the store
Write-Host "Checking driver store..."
$drivers = pnputil /enum-drivers 2>&1 | Out-String
$found = $false
foreach ($line in ($drivers -split "`n")) {
    if ($line -match "HIDMaestro") {
        $found = $true
        Write-Host "  Found: $line"
    }
}
if (-not $found) { Write-Host "  WARNING: HIDMaestro not in driver store!" }

# Check existing nodes
Write-Host "`nExisting HIDCLASS nodes:"
pnputil /enum-devices /instanceid "ROOT\HIDCLASS\*" 2>&1

# Try creating
Write-Host "`nCreating device node..."
& powershell -ExecutionPolicy Bypass -File "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\scripts\create_node.ps1" 2>&1

# Check again
Write-Host "`nAfter creation:"
pnputil /enum-devices /instanceid "ROOT\HIDCLASS\*" 2>&1

# Keep open
Start-Sleep -Seconds 5
