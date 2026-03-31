# Start emulation as a persistent background process
$exe = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\publish_out\HIDMaestroTest.exe"
Start-Process -FilePath $exe -ArgumentList "emulate","xbox-one-original" -WindowStyle Hidden
Write-Host "Emulation started in background"
