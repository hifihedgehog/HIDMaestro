$exe = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\publish_out\HIDMaestroTest.exe"
$out = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build\xbox_test_output.txt"
$proc = Start-Process -FilePath $exe -ArgumentList "xbox" -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError "$out.err"
if (-not $proc.WaitForExit(45000)) {
    $proc.Kill()
    Add-Content $out "`n  [Stopped after 45s]"
}
