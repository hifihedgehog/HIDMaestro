param([string]$Dll = 'C:\Windows\System32\Windows.Gaming.Input.dll')
$b = [IO.File]::ReadAllBytes($Dll)
$t = [Text.Encoding]::ASCII.GetString($b)
$ut = [Text.Encoding]::Unicode.GetString($b)

$rxCpp = [regex]'[A-Za-z0-9_\\]{4,120}\.(cpp|h|hpp|c)'
$cppFiles = $rxCpp.Matches($t) | ForEach-Object { $_.Value } | Sort-Object -Unique
Write-Output "=== Source-file footprint (ASCII) ==="
foreach ($f in $cppFiles) { Write-Output "  $f" }

Write-Output ""
Write-Output "=== Class / namespace names (UTF-16) ==="
$rxU = [regex]'[\u0020-\u007E]{10,200}'
$strings = $rxU.Matches($ut) | ForEach-Object { $_.Value } | Sort-Object -Unique
$interestRx = 'GamepadDevice|IGamepad|IRawGameController|HapticFeedback|ForceFeedbackBroker|Xusb|Vibration|RawGameControllerDevice|DsHid|Dualshock|HidDevice|GipDevice|OEMForceFeedback|HidForceFeedback|GamepadFromRawGameController|CreateGameController|GameControllerFactoryManager|OEMControllers|HapticsForController'
foreach ($s in $strings) {
    if ($s -match $interestRx) {
        Write-Output "  $s"
    }
}
