$ErrorActionPreference = "Continue"
$logFile = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\full_test.log"
$exe = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\publish_out\HIDMaestroTest.exe"
$controllerProfile = if ($args.Count -gt 0) { $args[0] } else { "xbox-one-original" }

"" | Out-File -Encoding ASCII $logFile
function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $logFile }

Log "=== Testing profile: $controllerProfile ==="

# Start emulation in background
$proc = Start-Process -FilePath $exe -ArgumentList "emulate",$controllerProfile -PassThru -NoNewWindow -RedirectStandardOutput "$logFile.emulate" -RedirectStandardError "$logFile.err"
Log "Started emulation PID $($proc.Id)"

# Wait for device to initialize
Start-Sleep -Seconds 15

# Run WinMM check in a SEPARATE new process (fresh joystick enumeration)
Log "`n[WinMM Check - separate process]"
$checkScript = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\scripts\check_winmm.ps1"
$wmResult = powershell -NoProfile -ExecutionPolicy Bypass -File $checkScript 2>&1
foreach ($line in $wmResult) { Log "  $line" }
if (-not $wmResult) { Log "  No joysticks detected" }

# Emulation output
Log "`n[Emulation Output]"
if (Test-Path "$logFile.emulate") {
    Get-Content "$logFile.emulate" -EA SilentlyContinue | ForEach-Object { Log "  $_" }
}

Stop-Process -Id $proc.Id -Force -EA SilentlyContinue
Log "`n=== Test Complete ==="
