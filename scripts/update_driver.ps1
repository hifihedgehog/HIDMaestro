$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"
$inf2cat = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x86\inf2cat.exe"
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"

# Remove old catalog
Remove-Item "$build\hidmaestro.cat" -ErrorAction SilentlyContinue

# Create catalog
Write-Host "Creating catalog..."
& $inf2cat /driver:"$build" /os:10_X64 2>&1 | Out-Null

# Sign DLL and catalog
Write-Host "Signing..."
& $signtool sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 "$build\HIDMaestro.dll" 2>&1 | Out-Null
& $signtool sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 "$build\hidmaestro.cat" 2>&1 | Out-Null

$sig = Get-AuthenticodeSignature "$build\HIDMaestro.dll"
Write-Host "  DLL signature: $($sig.Status)"
$sig2 = Get-AuthenticodeSignature "$build\hidmaestro.cat"
Write-Host "  CAT signature: $($sig2.Status)"

# Remove old device node + driver
Write-Host "Removing old driver..."
pnputil /remove-device "ROOT\HIDCLASS\0000" /subtree 2>&1 | Out-Null
pnputil /remove-device "ROOT\HIDCLASS\0001" /subtree 2>&1 | Out-Null

# Find and remove old OEM inf
$drivers = pnputil /enum-drivers 2>&1 | Out-String
$currentOem = $null
foreach ($line in ($drivers -split "`n")) {
    if ($line -match "Published Name:\s+(oem\d+\.inf)") { $currentOem = $Matches[1].Trim() }
    if ($currentOem -and $line -match "HIDMaestro") {
        Write-Host "  Removing old $currentOem..."
        pnputil /delete-driver $currentOem /force 2>&1 | Out-Null
        $currentOem = $null
    }
    if ([string]::IsNullOrWhiteSpace($line)) { $currentOem = $null }
}

Start-Sleep -Seconds 1

# Re-add
Write-Host "Installing updated driver..."
$result = pnputil /add-driver "$build\hidmaestro.inf" /install 2>&1 | Out-String
Write-Host $result

if ($result -match "Added driver packages:\s+1") {
    Write-Host "Driver updated successfully." -ForegroundColor Green
} else {
    Write-Host "Driver add may have failed. Check output above." -ForegroundColor Red
}
