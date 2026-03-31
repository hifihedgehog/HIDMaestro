$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\signing.log"
$sb = $false
try { $sb = Confirm-SecureBootUEFI } catch {}
$ts = (bcdedit /enum "{current}" 2>&1 | Out-String) -match "testsigning\s+Yes"
$ci = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\CI" -EA SilentlyContinue

$result = @"
Secure Boot:   $sb
Test Signing:  $ts
CI Protected:  $($ci.Protected)
CI PolicyFlags: $($ci.PolicyFlags)
CI UMCIEnabled: $($ci.UMCIEnabled)
"@
$result | Out-File -Encoding ASCII $log
Write-Host $result
