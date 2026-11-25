# Ends this session's observer, found by its command line.
$p = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -like '*\HIDMaestro\obs\observer.ps1*' })
foreach ($x in $p) { Stop-Process -Id $x.ProcessId -Force; "stopped $($x.ProcessId)" }
if (-not $p.Count) { 'no observer running' }
