$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\reg_test.log"
function Log($msg) { Log $msg; $msg | Out-File -Append -Encoding ASCII $log }
"" | Out-File -Encoding ASCII $log
# Test if registry preserves 0xFF bytes correctly
$testData = [byte[]]@(0x05, 0x01, 0xFF, 0xFF, 0x00, 0x00, 0x27, 0xFF, 0xFF, 0x00, 0x00)
$key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey("SOFTWARE\HIDMaestro")
$key.SetValue("TestDescriptor", $testData, [Microsoft.Win32.RegistryValueKind]::Binary)
$readBack = [byte[]]$key.GetValue("TestDescriptor")
Log "Written:  $([BitConverter]::ToString($testData))"
Log "ReadBack: $([BitConverter]::ToString($readBack))"
Log "Match: $($testData.Length -eq $readBack.Length -and [System.Linq.Enumerable]::SequenceEqual($testData, $readBack))"

# Now check what the emulate command actually wrote
$desc = [byte[]]$key.GetValue("ReportDescriptor")
if ($desc) {
    Log "`nReportDescriptor first 20 bytes: $([BitConverter]::ToString($desc, 0, [Math]::Min(20, $desc.Length)))"
    Log "Byte 15: 0x$($desc[15].ToString('X2'))"
    Log "Byte 16: 0x$($desc[16].ToString('X2'))"
}
$key.DeleteValue("TestDescriptor", $false)
