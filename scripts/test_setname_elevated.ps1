$exe = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\publish_out\HIDMaestroTest.exe"
$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\setname_elevated.log"
& $exe setname "Controller" 2>&1 | Tee-Object -FilePath $log
Start-Sleep 2
