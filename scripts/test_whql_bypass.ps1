$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\whql_bypass.log"
$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"
"" | Out-File -Encoding ASCII $log
function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $log }

Log "=== WHQL Developer Test Mode Bypass ==="

# Check current state
$ciPolicy = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy" -EA SilentlyContinue
Log "Current WhqlDeveloperTestMode: $($ciPolicy.WhqlDeveloperTestMode)"
Log "Current WhqlSettings: $($ciPolicy.WhqlSettings)"
Log "Current UpgradedSystem: $($ciPolicy.UpgradedSystem)"

# Set WhqlDeveloperTestMode = 1
Log "`nSetting WhqlDeveloperTestMode = 1..."
$ciKey = "HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy"
if (-not (Test-Path $ciKey)) { New-Item -Path $ciKey -Force | Out-Null }
Set-ItemProperty -Path $ciKey -Name "WhqlDeveloperTestMode" -Value 1 -Type DWord

# Verify
$after = (Get-ItemProperty $ciKey -EA SilentlyContinue).WhqlDeveloperTestMode
Log "After set: WhqlDeveloperTestMode = $after"

# Now try to load our self-signed KMDF driver
Log "`nAttempting to load minimal_test.sys..."

$sysPath = "$build\minimal_test.sys"
$sig = Get-AuthenticodeSignature $sysPath
Log "Driver signature: $($sig.Status) by $($sig.SignerCertificate.Subject)"

# Create and start the service
sc.exe create MinimalTest type= kernel binPath= "$sysPath" 2>&1 | ForEach-Object { Log "  $_" }
$startResult = sc.exe start MinimalTest 2>&1
foreach ($line in $startResult) { Log "  $line" }

$err = $LASTEXITCODE
Log "sc start exit code: $err"

# Check if it loaded
$svc = Get-Service MinimalTest -EA SilentlyContinue
Log "Service status: $($svc.Status)"

# Clean up
sc.exe stop MinimalTest 2>&1 | Out-Null
sc.exe delete MinimalTest 2>&1 | Out-Null

Log "`n=== Done ==="
Start-Sleep 3
