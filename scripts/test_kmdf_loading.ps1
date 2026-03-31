# Test if a self-signed .sys can be demand-loaded on this system
# without test signing mode enabled.
#
# We'll create a minimal KMDF driver, sign it with our existing cert,
# and try to load it via sc create / sc start.

$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\kmdf_load_test.log"
$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"

function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $log }
"" | Out-File -Encoding ASCII $log

# Check if we already have the UMDF DLL signed — use the same cert
$sig = Get-AuthenticodeSignature "$build\HIDMaestro.dll"
Log "Existing DLL signature: $($sig.Status) by $($sig.SignerCertificate.Subject)"

# Try to create a minimal kernel service pointing to a non-existent .sys
# just to see what error we get — this tests the policy, not the driver
Log "`nTesting kernel driver loading policy..."

# Actually, let's just check if the system allows loading unsigned/self-signed drivers
$ciPolicy = Get-CimInstance -ClassName Win32_DeviceGuard -Namespace root\Microsoft\Windows\DeviceGuard -EA SilentlyContinue
if ($ciPolicy) {
    Log "CodeIntegrityPolicyEnforcementStatus: $($ciPolicy.CodeIntegrityPolicyEnforcementStatus)"
    Log "SecurityServicesConfigured: $($ciPolicy.SecurityServicesConfigured)"
    Log "SecurityServicesRunning: $($ciPolicy.SecurityServicesRunning)"
    Log "VirtualizationBasedSecurityStatus: $($ciPolicy.VirtualizationBasedSecurityStatus)"
} else {
    Log "DeviceGuard WMI not available"
}

# Check CI audit mode
$ci = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\CI\Config" -EA SilentlyContinue
if ($ci) {
    Log "CI VulnerableDriverBlocklistEnable: $($ci.VulnerableDriverBlocklistEnable)"
}

# Check if HVCI is enabled
$hvci = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity" -EA SilentlyContinue
if ($hvci) {
    Log "HVCI Enabled: $($hvci.Enabled)"
}

Start-Sleep 2
