$ErrorActionPreference = "Continue"
$logFile = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\emulate_test.log"
$exe = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\publish_out\HIDMaestroTest.exe"

# Get profile from args, default to xbox-360-wired
$profile = if ($args.Count -gt 0) { $args[0] } else { "xbox-360-wired" }

"" | Out-File -Encoding ASCII $logFile

& $exe emulate $profile 2>&1 | ForEach-Object {
    $line = $_.ToString()
    Write-Host $line
    $line | Out-File -Append -Encoding ASCII $logFile
}

Start-Sleep -Seconds 3
