# Full clean + deploy: remove everything, rebuild from scratch
$ErrorActionPreference = "Continue"
$logFile = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\deploy.log"
"" | Out-File -Encoding ASCII $logFile

function Log($msg) {
    Write-Host $msg
    $msg | Out-File -Append -Encoding ASCII $logFile
}

$root = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro"
$build = "$root\build"
$scripts = "$root\scripts"

Log "=== CLEAN + DEPLOY ==="

# 1. Remove all device nodes
Log "[1] Removing device nodes..."
# Remove only HID_IG_00 devices that are in Error state (ghosts/broken)
# Leave working devices alone
Get-PnpDevice -EA SilentlyContinue | Where-Object {
    $_.InstanceId -match "ROOT\\HID_IG_00" -and $_.Status -ne "OK"
} | ForEach-Object {
    Log "  Removing ghost: $($_.InstanceId) ($($_.Status))"
    pnputil /remove-device $_.InstanceId /subtree 2>&1 | Out-Null
}
Start-Sleep 1

# 2. Remove ALL HIDMaestro drivers from store
Log "[2] Removing old drivers from store..."
$drivers = pnputil /enum-drivers 2>&1 | Out-String
$currentOem = $null
foreach ($line in ($drivers -split "`n")) {
    if ($line -match "Published Name:\s+(oem\d+\.inf)") { $currentOem = $Matches[1].Trim() }
    if ($currentOem -and $line -match "HIDMaestro") {
        Log "  Removing $currentOem"
        pnputil /delete-driver $currentOem /force 2>&1 | Out-Null
        $currentOem = $null
    }
    if ([string]::IsNullOrWhiteSpace($line)) { $currentOem = $null }
}
Start-Sleep 1

# 3. Build
Log "[3] Building driver..."
$buildResult = & cmd /c "`"$scripts\build.cmd`"" 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Log "BUILD FAILED: $buildResult"
    exit 1
}
Log "  Build OK"

# 4. Sign DLL
Log "[4] Signing DLL..."
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
& $signtool sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 "$build\HIDMaestro.dll" 2>&1 | Out-Null
$sig = Get-AuthenticodeSignature "$build\HIDMaestro.dll"
Log "  DLL: $($sig.Status)"
if ($sig.Status -ne "Valid") {
    Log "  Running do_sign.ps1 to create cert..."
    & powershell -ExecutionPolicy Bypass -File "$scripts\do_sign.ps1" 2>&1 | Out-Null
    $sig = Get-AuthenticodeSignature "$build\HIDMaestro.dll"
    Log "  DLL: $($sig.Status)"
}

# 5. Create + sign catalog
Log "[5] Creating catalog..."
Remove-Item "$build\hidmaestro.cat" -ErrorAction SilentlyContinue
$inf2cat = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x86\inf2cat.exe"
& $inf2cat /driver:"$build" /os:10_X64 2>&1 | Out-Null
& $signtool sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 "$build\hidmaestro.cat" 2>&1 | Out-Null
$catSig = Get-AuthenticodeSignature "$build\hidmaestro.cat"
Log "  CAT: $($catSig.Status)"

# 6. Add driver to store
Log "[6] Adding driver to store..."
$addResult = pnputil /add-driver "$build\hidmaestro.inf" /install 2>&1 | Out-String
if ($addResult -match "Published Name:\s+(oem\d+\.inf)") {
    Log "  Published: $($Matches[1])"
} else {
    Log "  Result: $addResult"
}

Start-Sleep 1

# 6.5 Pre-create shared file with open ACLs (driver reads this for input data)
$sharedDir = "C:\ProgramData\HIDMaestro"
if (-not (Test-Path $sharedDir)) { New-Item -Path $sharedDir -ItemType Directory -Force | Out-Null }
$sharedFile = "$sharedDir\input.bin"
if (-not (Test-Path $sharedFile)) { [byte[]]::new(72) | Set-Content -Path $sharedFile -Encoding Byte }
icacls $sharedDir /grant "Everyone:(OI)(CI)F" /T 2>&1 | Out-Null
Log "  Shared file ready: $sharedFile"

# 7. Create device node
Log "[7] Creating device node..."
& powershell -ExecutionPolicy Bypass -File "$scripts\create_node.ps1" 2>&1 | ForEach-Object { Log "  $_" }
Start-Sleep 3

# 8. Verify
Log "[8] Verifying..."
$devResult = pnputil /enum-devices /instanceid "ROOT\HID_IG_00\0000" 2>&1 | Out-String
Log $devResult

if ($devResult -match "Status:\s+Started") {
    Log "SUCCESS: Device is running!"
} elseif ($devResult -match "Problem Code:\s+(\d+)") {
    Log "PROBLEM: Device has error code $($Matches[1])"
} else {
    Log "Device node check: $devResult"
}

Log "`n=== DEPLOY COMPLETE ==="
Start-Sleep 3
