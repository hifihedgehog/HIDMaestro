# HIDMaestro Full Deploy: Build + Sign + Install + Create Node
# Must run elevated.
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path "$root\driver\driver.c")) {
    $root = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro"
}
$build = "$root\build"
$scripts = "$root\scripts"

Write-Host "`n=== HIDMaestro Full Deploy ===`n" -ForegroundColor Cyan

# Step 1: Build
if (-not $SkipBuild) {
    Write-Host "[1/4] Building driver..." -ForegroundColor Yellow
    $buildResult = & cmd /c "`"$scripts\build.cmd`"" 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Write-Host "BUILD FAILED:" -ForegroundColor Red
        Write-Host $buildResult
        exit 1
    }
    Write-Host "  Build OK" -ForegroundColor Green
} else {
    Write-Host "[1/4] Skipping build" -ForegroundColor DarkGray
}

# Step 2: Sign
Write-Host "[2/4] Signing driver..." -ForegroundColor Yellow
& powershell -ExecutionPolicy Bypass -File "$scripts\do_sign.ps1" 2>&1 | Out-Null
$sig = Get-AuthenticodeSignature "$build\HIDMaestro.dll"
if ($sig.Status -ne "Valid") {
    Write-Host "  SIGN FAILED: $($sig.Status)" -ForegroundColor Red
    exit 1
}
Write-Host "  Signature: $($sig.Status)" -ForegroundColor Green

# Step 3: Install driver (catalog + pnputil)
Write-Host "[3/4] Installing driver..." -ForegroundColor Yellow

# Remove old device nodes
pnputil /remove-device "ROOT\HIDCLASS\0000" /subtree 2>&1 | Out-Null
pnputil /remove-device "ROOT\HIDCLASS\0001" /subtree 2>&1 | Out-Null

# Remove old catalog
Remove-Item "$build\hidmaestro.cat" -ErrorAction SilentlyContinue

# Create catalog
$inf2cat = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x86\inf2cat.exe"
& $inf2cat /driver:"$build" /os:10_X64 2>&1 | Out-Null

# Sign catalog
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
& $signtool sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 "$build\hidmaestro.cat" 2>&1 | Out-Null

# Find and remove old OEM inf
$drivers = pnputil /enum-drivers 2>&1 | Out-String
$currentOem = $null
foreach ($line in ($drivers -split "`n")) {
    if ($line -match "Published Name:\s+(oem\d+\.inf)") { $currentOem = $Matches[1].Trim() }
    if ($currentOem -and $line -match "HIDMaestro") {
        Write-Host "  Removing old $currentOem"
        pnputil /delete-driver $currentOem /force 2>&1 | Out-Null
        $currentOem = $null
    }
    if ([string]::IsNullOrWhiteSpace($line)) { $currentOem = $null }
}

Start-Sleep -Seconds 1

# Re-add driver to store
$addResult = pnputil /add-driver "$build\hidmaestro.inf" /install 2>&1 | Out-String
if ($addResult -match "Added driver packages:\s+1") {
    Write-Host "  Driver installed" -ForegroundColor Green
} else {
    Write-Host "  Driver install result:" -ForegroundColor Yellow
    Write-Host $addResult
}

# Step 4: Create device node
Write-Host "[4/4] Creating device node..." -ForegroundColor Yellow
& powershell -ExecutionPolicy Bypass -File "$scripts\create_node.ps1" 2>&1 | Out-Null
Start-Sleep -Seconds 2

# Verify
$devs = pnputil /enum-devices /instanceid "ROOT\HIDCLASS\*" 2>&1 | Out-String
if ($devs -match "ROOT\\HIDCLASS\\0000") {
    Write-Host "  Device node: ROOT\HIDCLASS\0000" -ForegroundColor Green
} else {
    Write-Host "  WARNING: Device node not found. Check Device Manager." -ForegroundColor Yellow
}

Write-Host "`n=== Deploy Complete ===`n" -ForegroundColor Cyan
