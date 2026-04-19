# WUDFHost CPU sampler.
#
# Queries \Process(WUDFHost*)\% Processor Time at ~1 Hz for a duration,
# writes CSV with per-sample and per-instance CPU usage. Multiple WUDFHost
# processes get their own counter instance (WUDFHost, WUDFHost#1, etc.)
# which Get-Counter aggregates when you use the wildcard.
#
# Normalizes reported % by core count so 100% = 1 full core.
#
# Usage:
#   powershell -NoProfile -File sample.ps1 -DurationSec 60 -OutputCsv out.csv [-Label T1]

param(
    [int]$DurationSec = 60,
    [string]$OutputCsv = 'wudfhost_cpu.csv',
    [string]$Label = ''
)

$core = [Environment]::ProcessorCount
"timestamp_iso,label,total_normalized_pct,instance_values" | Out-File -FilePath $OutputCsv -Encoding ascii

$tEnd = (Get-Date).AddSeconds($DurationSec)
while ((Get-Date) -lt $tEnd) {
    $sample = $null
    try { $sample = Get-Counter '\Process(WUDFHost*)\% Processor Time' -SampleInterval 1 -MaxSamples 1 -ErrorAction Stop } catch {}
    if ($null -eq $sample) { Start-Sleep -Milliseconds 1000; continue }
    $cs = $sample.CounterSamples
    $total = 0.0
    $perInst = @()
    foreach ($s in $cs) {
        $n = $s.CookedValue / $core
        $total += $n
        $perInst += ("{0}={1:F2}" -f $s.InstanceName, $n)
    }
    $ts = (Get-Date).ToString('o')
    "$ts,$Label,$('{0:F2}' -f $total),$($perInst -join ';')" | Out-File -FilePath $OutputCsv -Append -Encoding ascii
}
Write-Output "Sampled $DurationSec s to $OutputCsv"
