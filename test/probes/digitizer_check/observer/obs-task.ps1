# Starts observer.ps1 once in the signed-in user's console session, hidden,
# through a scheduled task for this account with the Interactive logon type,
# or removes that task.
param([ValidateSet('start', 'stop')][string]$Do = 'start')
$name = 'HIDMaestroObserver'
if ($Do -eq 'start') {
    Remove-Item C:\HIDMaestro\obs\obs.log -ErrorAction SilentlyContinue
    $a = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument 'C:\HIDMaestro\obs\launch.vbs'
    $p = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
    $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
    Register-ScheduledTask -TaskName $name -Action $a -Principal $p -Settings $s -Force | Out-Null
    Start-ScheduledTask -TaskName $name
    Start-Sleep -Seconds 4
    if (Test-Path C:\HIDMaestro\obs\obs.log) { Get-Content C:\HIDMaestro\obs\obs.log -TotalCount 9 } else { 'no log yet' }
} else {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    "task removed: $(-not (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue))"
}
