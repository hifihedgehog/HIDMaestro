# Drives the 6-controller heterogeneous test, samples CPU in BOTH the
# active state (250 Hz × 6 pattern threads stress test) and the idle state
# (pattern threads paused — measures the driver's true idle CPU cost,
# which should be ~0% with event-driven IPC vs a saturated core per
# controller before the fix). Run via sudo --inline.
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $repo 'test\HIDMaestroTest\bin\Debug\net10.0-windows10.0.26100.0\win-x64\HIDMaestroTest.exe'

Write-Host '[1/7] cleanup...'
& $exe cleanup 2>&1 | ForEach-Object { Write-Host "  > $_" }

$controllers = @(
    'xbox-series-xs-bt','xbox-series-xs-bt',
    'xbox-360-wired','xbox-360-wired',
    'dualsense','dualsense'
)

Write-Host "[2/7] launching emulate with $($controllers.Length) controllers..."
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName              = $exe
$psi.Arguments             = "emulate $($controllers -join ' ')"
$psi.RedirectStandardInput = $true
$psi.UseShellExecute       = $false
$proc = [System.Diagnostics.Process]::Start($psi)

Write-Host '[3/7] settling 60s for PnP install + 6 controllers + FinalizeNames...'
for ($i = 1; $i -le 60; $i++) {
    Start-Sleep -Seconds 1
    if ($proc.HasExited) {
        Write-Host "    !! process exited during settle (code $($proc.ExitCode))"
        exit 3
    }
}

# ── PID capture ──
$initialProcs = Get-Process -Name WUDFHost -ErrorAction SilentlyContinue
$pidsToTrack  = $initialProcs | ForEach-Object { $_.Id }
$cores        = [System.Environment]::ProcessorCount
Write-Host "    tracking $($pidsToTrack.Count) WUDFHost PIDs on $cores cores"

function SampleCpuSum {
    $sum = 0.0
    foreach ($trackedPid in $pidsToTrack) {
        $p = Get-Process -Id $trackedPid -ErrorAction SilentlyContinue
        if ($p) { try { $sum += $p.CPU } catch {} }
    }
    return $sum
}
function ReportSample($label, $cpu0, $cpu1, $secs) {
    $delta = $cpu1 - $cpu0
    $pct   = [math]::Round(($delta / $secs) / $cores * 100.0, 3)
    $count = $pidsToTrack.Count
    $perctl = if ($count -gt 0) { [math]::Round($pct / $count, 3) } else { 0 }
    Write-Host "    $label : sum=$([math]::Round($delta,3)) CPU-sec, total=$pct% of $cores cores, per-WUDFHost=$perctl%"
}

# ── Active sample ──
Write-Host '[4/7] sampling ACTIVE CPU (250 Hz x 6 controllers) for 10s...'
$cpuA0 = SampleCpuSum
Start-Sleep -Seconds 10
$cpuA1 = SampleCpuSum
ReportSample 'ACTIVE' $cpuA0 $cpuA1 10

# ── Pause + settle ──
Write-Host '[5/7] sending pause (idle state) and settling 3s...'
try { $proc.StandardInput.WriteLine('pause') } catch {}
Start-Sleep -Seconds 3

# ── Idle sample ──
Write-Host '[6/7] sampling IDLE CPU (pattern threads paused) for 10s...'
$cpuI0 = SampleCpuSum
Start-Sleep -Seconds 10
$cpuI1 = SampleCpuSum
ReportSample 'IDLE  ' $cpuI0 $cpuI1 10

Write-Host ''
Write-Host '====== SUMMARY ======'
$activeDelta = $cpuA1 - $cpuA0
$idleDelta   = $cpuI1 - $cpuI0
Write-Host "  WUDFHost.exe count: $($pidsToTrack.Count)"
Write-Host "  ACTIVE: $([math]::Round($activeDelta,2)) CPU-sec / 10s = $([math]::Round(($activeDelta/10/$cores)*100,2))% of $cores cores"
Write-Host "  IDLE  : $([math]::Round($idleDelta,2)) CPU-sec / 10s = $([math]::Round(($idleDelta/10/$cores)*100,2))% of $cores cores"
if ($activeDelta -gt 0) {
    Write-Host "  IDLE/ACTIVE ratio: $([math]::Round($idleDelta/$activeDelta*100,1))%"
}
Write-Host '====================='
Write-Host ''

Write-Host '[7/7] sending quit (120s timeout for 6-controller teardown)...'
try { $proc.StandardInput.WriteLine('quit') } catch {}
if (-not $proc.WaitForExit(120000)) {
    Write-Host '       quit timed out, killing.'
    try { $proc.Kill() } catch {}
}
Write-Host "Test exit code: $($proc.ExitCode)"

Write-Host '[final] cleanup...'
& $exe cleanup 2>&1 | ForEach-Object { Write-Host "  > $_" }
