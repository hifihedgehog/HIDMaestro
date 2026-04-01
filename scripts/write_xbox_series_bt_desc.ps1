$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\series_desc.log"
"" | Out-File -Encoding ASCII $log
function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $log }

$profile = Get-Content "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\profiles\microsoft\xbox-series-xs-bt.json" | ConvertFrom-Json
$hex = $profile.descriptor
$desc = [byte[]]::new($hex.Length / 2)
for ($i = 0; $i -lt $desc.Length; $i++) { $desc[$i] = [Convert]::ToByte($hex.Substring($i*2, 2), 16) }
Log "Xbox Series BT Descriptor: $($desc.Length) bytes"

$key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey("SOFTWARE\HIDMaestro")
$key.SetValue("ReportDescriptor", $desc, [Microsoft.Win32.RegistryValueKind]::Binary)
$key.SetValue("VendorId", 0x045E, [Microsoft.Win32.RegistryValueKind]::DWord)
$key.SetValue("ProductId", 0x0B13, [Microsoft.Win32.RegistryValueKind]::DWord)
$key.SetValue("ProductString", "Xbox Wireless Controller", [Microsoft.Win32.RegistryValueKind]::String)
Log "Registry written"

pnputil /restart-device "ROOT\HIDCLASS\0000" 2>&1 | Out-Null
Start-Sleep 3

$dev = pnputil /enum-devices /instanceid "ROOT\HIDCLASS\0000" 2>&1 | Out-String
Log $dev.Trim()

$child = Get-PnpDevice | Where-Object { $_.InstanceId -like "HID\HIDCLASS\*" -and $_.Status -eq "OK" }
if ($child) { Log "HID child: $($child.InstanceId) $($child.FriendlyName)" }
else { Log "No HID child" }
