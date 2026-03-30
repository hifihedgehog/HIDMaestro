# HIDMaestro — Full Cleanup
# Removes: device nodes, driver from store, registry keys, certificates
# Run elevated.

Write-Host "=== HIDMaestro Cleanup ===" -ForegroundColor Yellow

# 1. Remove device nodes
Write-Host "`n[1] Removing device nodes..."
$nodes = pnputil /enum-devices /class HIDClass 2>&1 | Out-String
$instanceIds = @()
$currentId = $null
foreach ($line in ($nodes -split "`n")) {
    if ($line -match "Instance ID:\s+(.+)") { $currentId = $Matches[1].Trim() }
    if ($currentId -and $line -match "HIDMaestro") {
        $instanceIds += $currentId
        $currentId = $null
    }
    if ([string]::IsNullOrWhiteSpace($line)) { $currentId = $null }
}

# Also find ROOT\HIDCLASS nodes that were created by our install
$rootNodes = pnputil /enum-devices 2>&1 | Out-String
$currentId = $null
foreach ($line in ($rootNodes -split "`n")) {
    if ($line -match "Instance ID:\s+(ROOT\\HIDCLASS\\.+)") { $currentId = $Matches[1].Trim() }
    if ($currentId -and $line -match "HIDMaestro") {
        if ($instanceIds -notcontains $currentId) { $instanceIds += $currentId }
        $currentId = $null
    }
    if ([string]::IsNullOrWhiteSpace($line)) { $currentId = $null }
}

if ($instanceIds.Count -eq 0) {
    Write-Host "  No HIDMaestro device nodes found."
} else {
    foreach ($id in $instanceIds) {
        Write-Host "  Removing $id..."
        pnputil /remove-device "$id" /subtree 2>&1 | Out-Null
    }
    Write-Host "  Removed $($instanceIds.Count) node(s)."
}

# 2. Remove driver from driver store
Write-Host "`n[2] Removing driver from store..."
$drivers = pnputil /enum-drivers 2>&1 | Out-String
$oemInf = $null
$currentOem = $null
foreach ($line in ($drivers -split "`n")) {
    if ($line -match "Published Name:\s+(oem\d+\.inf)") { $currentOem = $Matches[1].Trim() }
    if ($currentOem -and $line -match "HIDMaestro") {
        $oemInf = $currentOem
        $currentOem = $null
    }
    if ([string]::IsNullOrWhiteSpace($line)) { $currentOem = $null }
}

if ($oemInf) {
    Write-Host "  Removing $oemInf..."
    pnputil /delete-driver $oemInf /force 2>&1 | Out-Null
    Write-Host "  Done."
} else {
    Write-Host "  No HIDMaestro driver found in store."
}

# 3. Remove registry keys
Write-Host "`n[3] Removing registry keys..."
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\HIDMaestro"
if (Test-Path $regPath) {
    Remove-Item $regPath -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  Removed $regPath"
} else {
    Write-Host "  No HIDMaestro service key found."
}

# Remove our config key
$configPath = "HKLM:\SOFTWARE\HIDMaestro"
if (Test-Path $configPath) {
    Remove-Item $configPath -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  Removed $configPath"
}

# 4. Remove certificates
Write-Host "`n[4] Removing certificates..."
$stores = @("Root", "TrustedPublisher")
foreach ($store in $stores) {
    $certs = Get-ChildItem "Cert:\LocalMachine\$store" |
        Where-Object { $_.Subject -eq "CN=HIDMaestroTestCert" }
    foreach ($cert in $certs) {
        Remove-Item $cert.PSPath -Force
        Write-Host "  Removed from $store`: $($cert.Thumbprint)"
    }
}

# Also clean the private store
$privateCerts = Get-ChildItem "Cert:\CurrentUser\PrivateCertStore" -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq "CN=HIDMaestroTestCert" }
foreach ($cert in $privateCerts) {
    Remove-Item $cert.PSPath -Force
    Write-Host "  Removed from PrivateCertStore: $($cert.Thumbprint)"
}

Write-Host "`n=== Cleanup Complete ===" -ForegroundColor Green
Write-Host "Your system is back to its pre-HIDMaestro state."
