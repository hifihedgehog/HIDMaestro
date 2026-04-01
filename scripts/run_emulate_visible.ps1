$exe = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\publish_out\HIDMaestroTest.exe"
try {
    & $exe emulate xbox-series-xs-bt 2>&1
} catch {
    Write-Host "EXCEPTION: $_"
}
Write-Host "`nPress Enter to close..."
Read-Host
