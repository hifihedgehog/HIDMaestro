$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\whql_bypass2.log"
$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"
"" | Out-File -Encoding ASCII $log
function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $log }

Log "=== WHQL Bypass Attempt 2 - All Registry Values ==="

$ciKey = "HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy"
if (-not (Test-Path $ciKey)) { New-Item -Path $ciKey -Force | Out-Null }

# Set ALL the bypass values
Log "Setting all bypass values..."
Set-ItemProperty -Path $ciKey -Name "WhqlDeveloperTestMode" -Value 1 -Type DWord
Set-ItemProperty -Path $ciKey -Name "UpgradedSystem" -Value 1 -Type DWord
Set-ItemProperty -Path $ciKey -Name "WhqlSettings" -Value 0x40000001 -Type DWord

# Also try the upgrade flag
$setupKey = "HKLM:\SYSTEM\Setup"
$origUpgrade = (Get-ItemProperty $setupKey -EA SilentlyContinue).Upgrade
Set-ItemProperty -Path $setupKey -Name "Upgrade" -Value 1 -Type DWord -EA SilentlyContinue

# Verify all values
Log "WhqlDeveloperTestMode: $((Get-ItemProperty $ciKey).WhqlDeveloperTestMode)"
Log "UpgradedSystem: $((Get-ItemProperty $ciKey).UpgradedSystem)"
Log "WhqlSettings: 0x$((Get-ItemProperty $ciKey).WhqlSettings.ToString('X8'))"
Log "Setup\Upgrade: $((Get-ItemProperty $setupKey -EA SilentlyContinue).Upgrade)"

# Try loading
Log "`nAttempting to load..."
sc.exe create MinimalTest type= kernel binPath= "$build\minimal_test.sys" 2>&1 | ForEach-Object { Log "  $_" }
$startResult = sc.exe start MinimalTest 2>&1
foreach ($line in $startResult) { Log "  $line" }
Log "Exit code: $LASTEXITCODE"

# Clean up service
sc.exe stop MinimalTest 2>&1 | Out-Null
sc.exe delete MinimalTest 2>&1 | Out-Null

# Restore upgrade flag
if ($null -eq $origUpgrade) {
    Remove-ItemProperty $setupKey -Name "Upgrade" -EA SilentlyContinue
} else {
    Set-ItemProperty $setupKey -Name "Upgrade" -Value $origUpgrade -Type DWord
}

Log "`n=== Done ==="
Start-Sleep 2
