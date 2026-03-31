$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\desc_write.log"
$exe = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\publish_out\HIDMaestroTest.exe"
Remove-Item "HKLM:\SOFTWARE\HIDMaestro" -Recurse -EA SilentlyContinue
& $exe emulate xbox-360-wired 2>&1 | Tee-Object -FilePath $log | Select-Object -First 15
Stop-Process -Name HIDMaestroTest -EA SilentlyContinue -Force
Start-Sleep 1
