$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"
$driverDir = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\driver"
$inf2cat = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x86\inf2cat.exe"
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"

Copy-Item "$driverDir\hidmaestro_xusb.inf" "$build\" -Force
Remove-Item "$build\hidmaestro_xusb.cat" -EA SilentlyContinue

Write-Host "Creating catalogs..."
& $inf2cat /driver:"$build" /os:10_X64 2>&1

Write-Host "Signing..."
& $signtool sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 "$build\hidmaestro_xusb.cat" 2>&1
& $signtool sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 "$build\hidmaestro.cat" 2>&1

Write-Host "Done"
