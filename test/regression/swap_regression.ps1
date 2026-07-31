<#
.SYNOPSIS
  Regression battery for HIDMaestro live-swap teardown.

.DESCRIPTION
  Drives HIDMaestroTest.exe through a series of single-controller and
  multi-controller create/swap/remove sequences. After every scenario,
  verifies that no HIDMaestro PnP devnodes are left in the PRESENT
  state. PHANTOM is fine: it's registry residue with no live devnode.
  A single PRESENT leftover means a real consumer (XInput, WGI,
  RawInput, SDL3) would still see a controller that should have been
  torn down.

  Catches the symptom that v1.1.31 fixed (SwDeviceLifetimeParentPresent
  resurrection after DIF_REMOVE) plus future regressions in the same
  area: incorrect handle tracking, missing teardown call sites,
  cross-controller leaks during multi-slot swaps, etc.

  Exit code: 0 if every scenario PASSED, 1 if any scenario FAILED.

.PARAMETER Exe
  Path to HIDMaestroTest.exe. Defaults to the Release build relative
  to the repo root.

.PARAMETER Filter
  Run only scenarios whose name matches this wildcard. Useful when
  iterating on a specific scenario. Default: '*' (all).

.PARAMETER KeepLogs
  If set, do NOT clear teardown_diag.log before the run; append.
  Default: clear before each run for a clean per-run log.

.PARAMETER Verbose
  Print every command sent to the test app and every state snapshot.

.EXAMPLE
  ./swap_regression.ps1
  Runs all scenarios.

.EXAMPLE
  ./swap_regression.ps1 -Filter 'S08*' -Verbose
  Runs only the S08 scenario with verbose output.

.NOTES
  Requires elevation. Run from an already-elevated PowerShell so the
  test app does not re-launch (which would orphan the stdin pipe).
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$Filter = '*',
    [switch]$KeepLogs
)

$ErrorActionPreference = 'Stop'

# Resolve script directory robustly. $PSScriptRoot may be empty in some
# invocation contexts (e.g. -File on certain PowerShell hosts).
$scriptDir = if ($PSScriptRoot) {
    $PSScriptRoot
} elseif ($MyInvocation.MyCommand.Path) {
    Split-Path -Parent $MyInvocation.MyCommand.Path
} else {
    $PWD.Path
}

if (-not $Exe) {
    $Exe = Join-Path $scriptDir '..\bin\Release\net10.0-windows10.0.26100.0\win-x64\HIDMaestroTest.exe'
}

# ====================================================================
#  Setup
# ====================================================================

if (-not (Test-Path $Exe)) {
    Write-Error "HIDMaestroTest.exe not found at: $Exe"
    exit 2
}

# Verify elevation. HIDMaestroTest re-launches if not, breaking stdin.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "This script must run elevated. PadForge-style stdin-driven swaps require an already-admin process so the test app does NOT re-launch (which would orphan the stdin pipe). Re-run via 'sudo --inline pwsh -File ...' or an Administrator console."
    exit 2
}

# CM_Locate P/Invoke. Only reliable way to know if a devnode is actually
# PRESENT in the PnP tree (vs PHANTOM = registry-only, vs GONE = neither).
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class CMxz {
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);
}
"@ -ErrorAction SilentlyContinue

if (-not $KeepLogs) {
    $diagLog = Join-Path $env:TEMP 'HIDMaestro\teardown_diag.log'
    if (Test-Path $diagLog) { Remove-Item $diagLog -Force }
}

# -- Probe DLL freshness gate --------------------------------------------
# Probes (S24-S26) reference HIDMaestro.Core via ProjectReference. If a
# probe was last built against an older SDK version, its bin/ carries a
# stale HIDMaestro.Core.dll and the probe runs against THAT stale
# managed code -- a false PASS that proves nothing about the SDK
# version we're shipping. All probe consumers use the RID-agnostic
# build output (plain `dotnet build`), the same flavor the SDK project
# emits, so content hashes are directly comparable. Two checks, and
# BOTH must pass:
#   1. AssemblyVersion vs Directory.Build.props <Version> (catches
#      cross-release staleness, works in release bundles too).
#   2. Content hash vs the SDK project's current build output (2026-07-21
#      audit: the version check alone is blind to SAME-version rebuilds,
#      which is every intra-release iteration. A battery ran a probe
#      carrying a same-version SDK from before that day's fixes and
#      silently exercised the old code, including full-deploying the old
#      embedded driver mid-battery. Deterministic compilation makes
#      content equality the correct freshness bar; source-tree only).
# Refuse to start the battery on any mismatch, naming the offenders so
# the recipe `dotnet build` command line is obvious.
$repoRoot   = Resolve-Path (Join-Path $scriptDir '..\..') -EA SilentlyContinue
$buildPropsPath = if ($repoRoot) { Join-Path $repoRoot 'Directory.Build.props' } else { $null }
# Source-tree-only gate. When the harness is shipped inside a release
# bundle alongside HIDMaestroTest (no source checkout), there is no
# Directory.Build.props to read; skip the version check rather than
# fail a release-bundle run.
$buildProps = if ($buildPropsPath -and (Test-Path $buildPropsPath)) {
    Get-Content $buildPropsPath -Raw
} else { '' }
$verRegex   = [regex]'<Version>([0-9.]+)</Version>'
$verMatch   = $verRegex.Match($buildProps)
if ($verMatch.Success) {
    $expectedVer = [Version]($verMatch.Groups[1].Value + '.0')
    $probePaths = @(
        Join-Path $scriptDir '..\probes\pid_ffb_roundtrip\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\pid_ffb_alloc_free\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\pid_setusages_probe\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\xbox_dpad_xinput_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\hat_resolution_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\sony_bt_report31_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\sony_bt_output_decode_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\sony_data_driven_coverage\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\v135_features_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\trigger_classifier_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\trigger_live_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\sidewinder_ffb_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\axis_addressing_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\layout_audit_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\sony_bt_axis_role_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\foreign_devnode_survival_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\xbox_combined_trigger_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\xbox_gip_trigger_resolver_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\usb_composite_schema_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\usbip_server_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\usbip_bundle_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\usbip_e2e_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
    )
    # Canonical SDK output for the content-hash check. Source tree only:
    # a release bundle carries no sdk/ build output, and the version
    # check above still guards that case.
    $sdkDllPath = if ($repoRoot) {
        Join-Path $repoRoot 'sdk\HIDMaestro.Core\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
    } else { $null }
    $sdkHash = if ($sdkDllPath -and (Test-Path $sdkDllPath)) {
        (Get-FileHash -LiteralPath $sdkDllPath -Algorithm SHA256).Hash
    } else { $null }

    $stale = @()
    foreach ($p in $probePaths) {
        $resolved = [System.IO.Path]::GetFullPath($p)
        if (-not (Test-Path $resolved)) {
            # Probe not built. The S24-S26 scenarios already fail with a
            # clear "build the probe" exception, so don't double-error
            # here. This gate is specifically for STALE probes.
            continue
        }
        $actualVer = [System.Reflection.AssemblyName]::GetAssemblyName($resolved).Version
        if ($actualVer -ne $expectedVer) {
            $stale += [PSCustomObject]@{ Path = $resolved; Have = "v$actualVer"; Want = "v$expectedVer" }
        }
        elseif ($sdkHash) {
            $probeHash = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
            if ($probeHash -ne $sdkHash) {
                $stale += [PSCustomObject]@{ Path = $resolved
                    Have = "hash $($probeHash.Substring(0,12))..."
                    Want = "hash $($sdkHash.Substring(0,12))... (current SDK build)" }
            }
        }
    }
    if ($stale.Count -gt 0) {
        Write-Host "STALE PROBE BINARIES -- refusing to run with mismatched HIDMaestro.Core:" -ForegroundColor Red
        foreach ($s in $stale) {
            Write-Host ("  " + $s.Path) -ForegroundColor Red
            Write-Host ("    have $($s.Have), want $($s.Want)") -ForegroundColor Red
        }
        Write-Host ""
        Write-Host "Rebuild each named probe from repo root, e.g.:" -ForegroundColor Yellow
        Write-Host "  dotnet build test\probes\<probe_dir> -c Release" -ForegroundColor Yellow
        Write-Host "(scripts\pre-tag-validate.cmd does this for the whole set automatically)" -ForegroundColor Yellow
        exit 2
    }
}

# Ensure HIDMAESTRO_DIAG=1 in the spawned process's environment so the
# diag log captures every teardown step. Helps post-mortem when a
# scenario fails. Set Process-scope so child processes inherit.
[Environment]::SetEnvironmentVariable('HIDMAESTRO_DIAG', '1', 'Process')

# Profile palette. Covers the main teardown paths AND the diverse
# controller families consumers actually deploy (per PadForge usage):
# Xbox 360 wired, Xbox Series Bluetooth, DualSense, Switch Pro, plus a
# PadForge-style custom profile authored via the SDK's HMProfileBuilder
# + HidDescriptorBuilder API surface.
$Profiles = @{
    Xbox360       = 'xbox-360-wired'           # non-xinputhid: ROOT main HID + XUSB SwDevice companion
    XboxBT        = 'xbox-series-xs-bt'        # xinputhid: SwDevice gamepad parent
    XboxOneBT     = 'xbox-one-s-bt'            # xinputhid: alternate xinputhid-INF match
    XboxEliteBT   = 'xbox-elite-v2-bt'         # xinputhid: third xinputhid-INF match
    DualSense     = 'dualsense'                # Sony PS5, plain HID, no companions
    DualSenseBT   = 'dualsense-bt'             # Sony PS5 BT, plain HID, no companions
    SwitchPro     = 'switch-pro'               # Nintendo, plain HID, no companions
    Custom        = 'padforge-custom'          # PadForge-style custom (BEEF:F000), built via HMProfileBuilder+HidDescriptorBuilder
}

# Author a PadForge-style custom profile JSON to a temp dir so the test
# app can load it via --profile-dir and the regression battery can swap
# to/from it like any embedded profile. Mirrors PadForge's
# HMaestroProfileCatalog.BuildCustomProfile (BEEF:F000, 2x16-bit sticks
# + 2x8-bit triggers + hat + 11 buttons), authored via the SDK's public
# HidDescriptorBuilder + HMProfileBuilder API surface.
$customProfileDir = Join-Path $env:TEMP 'HIDMaestro-regression-custom'
if (Test-Path $customProfileDir) { Remove-Item -Recurse -Force $customProfileDir }
$null = & $Exe make-custom-profile $customProfileDir 2>&1
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $customProfileDir 'padforge-custom.json'))) {
    Write-Error "make-custom-profile failed; cannot run custom-profile scenarios. Build the test app from this branch first."
    exit 2
}

# ====================================================================
#  Helpers
# ====================================================================

function Get-DevnodeState {
    param([string]$InstanceId)
    $devInst = [uint32]0
    $present = [CMxz]::CM_Locate_DevNodeW([ref]$devInst, $InstanceId, [uint32]0)
    if ($present -eq 0) { return 'PRESENT' }
    $phantom = [CMxz]::CM_Locate_DevNodeW([ref]$devInst, $InstanceId, [uint32]1)
    if ($phantom -eq 0) { return 'PHANTOM' }
    return 'GONE'
}

# Enumerate every HIDMaestro-named devnode in registry, return only
# those in PRESENT state. PHANTOM entries are registry residue from
# prior sessions; harmless to consumers, ignored here.
function Get-HMPresentDevnodes {
    $present = @()
    foreach ($root in 'ROOT','SWD','HID') {
        $rootKey = 'HKLM:\SYSTEM\CurrentControlSet\Enum\' + $root
        if (-not (Test-Path $rootKey)) { continue }
        Get-ChildItem $rootKey -ErrorAction SilentlyContinue | Where-Object {
            $_.PSChildName -match 'HIDMAESTRO|HMCOMPANION' -or
            $_.PSChildName -match 'VID_045E&PID_028E' -or  # Xbox 360 wired ROOT main HID
            $_.PSChildName -match 'VID_BEEF&PID_F000'      # PadForge-style custom profile
        } | ForEach-Object {
            $sub = $_.PSChildName
            $insts = Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue
            foreach ($i in $insts) {
                $iid = $root + '\' + $sub + '\' + $i.PSChildName
                if ((Get-DevnodeState $iid) -eq 'PRESENT') {
                    $present += $iid
                }
            }
        }
    }
    return ,$present
}

function Start-HMTestProcess {
    param(
        [string[]]$ProfileIds,
        [int]$RateHz = 30  # low rate to minimize noise + CPU during regression run
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    # Always pass --profile-dir so the custom profile is loadable in any
    # scenario, whether or not it actually references it.
    $psi.Arguments = "emulate --rate-hz $RateHz --profile-dir `"$customProfileDir`" " + ($ProfileIds -join ' ')
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    # Inherit HIDMAESTRO_DIAG so the test app logs every teardown.
    $psi.EnvironmentVariables['HIDMAESTRO_DIAG'] = '1'
    # Silence per-packet OutputReceived prints. Without this, Windows-fired
    # Guide-haptic / XInput rumble updates accumulate ~150 bytes each in the
    # test app's stdout pipe; the harness only drains stdout inside Send-Cmd,
    # so the 4 KB pipe buffer fills within seconds across Sleep-Scaled
    # windows. The next Console.WriteLine in the test app then blocks
    # indefinitely on the full pipe, manifesting as a 3-minute gap between
    # quit and cleanup — long enough to trigger Stop-HMTestProcess KILL
    # before kernel teardown can run, leaving SwD-companion devnodes that
    # the kernel re-enumerates from the surviving registry hive (the
    # S07/S19/S20 leftover pattern, 2026-05-01).
    $psi.EnvironmentVariables['HIDMAESTRO_QUIET'] = '1'

    # Async stdout drain. Per research + empirical (Win11 26200 + PS 5.1):
    #   - Register-ObjectEvent -Action deadlocks (action runs in runspace
    #     queue; Send-Cmd's blocking wait starves the queue).
    #   - PowerShell scriptblock cast to [DataReceivedEventHandler] CRASHES
    #     when fired from ThreadPool (PSMethod/runspace mismatch).
    # Working pattern: Add-Type a C# class whose method matches the event
    # delegate signature; bind it via [Delegate]::CreateDelegate. The
    # handler then runs purely in CLR ThreadPool space, never touching
    # PowerShell's runspace, so it cannot deadlock OR crash.
    if (-not ('HMRegression.HMDrain' -as [type])) {
        Add-Type -TypeDefinition @'
namespace HMRegression {
    public class HMDrain {
        public System.Collections.Concurrent.ConcurrentQueue<string> Queue =
            new System.Collections.Concurrent.ConcurrentQueue<string>();
        public System.Threading.ManualResetEventSlim Signal =
            new System.Threading.ManualResetEventSlim(false);
        public void OnLine(object sender, System.Diagnostics.DataReceivedEventArgs e) {
            if (e.Data != null) {
                Queue.Enqueue(e.Data);
                Signal.Set();
            }
        }
    }
}
'@ -ErrorAction SilentlyContinue
    }
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $proc.EnableRaisingEvents = $true
    $drain = New-Object HMRegression.HMDrain
    $delegateType = [System.Diagnostics.DataReceivedEventHandler]
    $miOnLine = $drain.GetType().GetMethod('OnLine')
    $delegate = [System.Delegate]::CreateDelegate($delegateType, $drain, $miOnLine)
    $proc.add_OutputDataReceived($delegate)
    Add-Member -InputObject $proc -MemberType NoteProperty -Name HMDrain -Value $drain -Force
    $null = $proc.Start()
    $proc.BeginOutputReadLine()
    # Stderr async-drain via Task is fine; we don't read stderr.
    $null = $proc.StandardError.ReadToEndAsync()
    return $proc
}

function Send-Cmd {
    param(
        [System.Diagnostics.Process]$Proc,
        [string]$Cmd,
        [switch]$NoFlush,
        [switch]$NoWaitAck
    )
    if ($Proc.HasExited) {
        $code = $Proc.ExitCode
        throw ("Cannot send '" + $Cmd + "': process already exited (code " + $code + ")")
    }
    $Proc.StandardInput.WriteLine($Cmd)
    if (-not $NoFlush) { $Proc.StandardInput.Flush() }
    if ($VerbosePreference -eq 'Continue') {
        Write-Host ('    [stdin] ' + $Cmd) -ForegroundColor DarkGray
    }
    if ($NoWaitAck) { return }

    # Drain stdout from the per-process HMDrain instance populated by the
    # add_OutputDataReceived handler (ThreadPool-driven). Wait(50) gives
    # the runspace a 50 ms heartbeat and yields back to PowerShell so
    # other cmdlets / event pumps can run; never blocks indefinitely on
    # a single .Wait. No budget; only exits on [ACK] or HasExited.
    $drain = $Proc.HMDrain
    while ($true) {
        $line = $null
        if ($drain.Queue.TryDequeue([ref]$line)) {
            if ($line.Trim() -eq '[ACK]') { return }
            continue
        }
        if ($Proc.HasExited) { return }
        [void]$drain.Signal.Wait(50)
        $drain.Signal.Reset()
    }
}

# v1.3.0 -- battery is scale-aware. Read HIDMAESTRO_TIMEOUT_SCALE once at
# script load, multiply every wall-clock budget by it. Default 1.0 reproduces
# pre-1.3.0 behavior exactly. Range clamped to [0.1, 100.0]; out-of-range
# values fall back to 1.0 with a warning.
$script:HMScale = 1.0
if ($env:HIDMAESTRO_TIMEOUT_SCALE) {
    $parsed = 0.0
    if ([double]::TryParse($env:HIDMAESTRO_TIMEOUT_SCALE,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed) -and $parsed -ge 0.1 -and $parsed -le 100.0) {
        $script:HMScale = $parsed
        Write-Host "Battery timeout scale: $script:HMScale (HIDMAESTRO_TIMEOUT_SCALE active)"
    } else {
        Write-Warning "HIDMAESTRO_TIMEOUT_SCALE='$env:HIDMAESTRO_TIMEOUT_SCALE' invalid or out of range [0.1, 100]; using 1.0"
    }
}

function HMScaleMs { param([int]$Ms) [int][Math]::Min([int]::MaxValue, $Ms * $script:HMScale) }
function Sleep-Scaled { param([int]$Seconds) Start-Sleep -Milliseconds (HMScaleMs ($Seconds * 1000)) }

function Stop-HMTestProcess {
    param(
        [System.Diagnostics.Process]$Proc,
        # Retained for ABI compatibility with callers that still pass it;
        # ignored. We wait indefinitely for the test process to exit on
        # its own — no KILL budget, no scaled timeout. If the test process
        # genuinely hangs, that's a real bug to surface in the diag log,
        # not paper over with a deadline.
        [int]$GracefulMs = 0
    )
    $null = $GracefulMs
    if (-not $Proc.HasExited) {
        # Signal quit via named EventWaitHandle. The test app spawns a
        # background quit-watcher thread that injects "quit" into its
        # internal stdin-inbox when this event fires. This bypasses every
        # stdin pipe / Console buffering issue that plagued the earlier
        # quit-via-stdin attempts on Win11 26200.
        $eventName = "Global\HMTest_Quit_$($Proc.Id)"
        try {
            $waitHandle = [System.Threading.EventWaitHandle]::OpenExisting($eventName)
            $null = $waitHandle.Set()
            $waitHandle.Dispose()
        } catch {
            # Fallback: send via stdin (works on Atom / older OS).
            try { Send-Cmd -Proc $Proc -Cmd 'quit' -NoWaitAck } catch {}
        }
        $Proc.WaitForExit()
        return 'GRACEFUL'
    }
    return 'ALREADY_EXITED'
}

# Wait for a controller-creation event to fully bind. Polls for the
# expected enumerator-pattern devnode being PRESENT. Adaptive: returns
# as soon as the device appears (short on fast machines, longer on slow).
function Wait-CreateBound {
    param(
        [string]$ProfileId,
        # Generous timeout: the FIRST scenario in a battery run can take
        # significantly longer than steady-state because the test process's
        # HMContext init does a full RemoveAllVirtualControllers + driver
        # install check + (post-v1.1.32) phantom-sweep over any
        # accumulated state from prior sessions. 30s is enough for
        # subsequent runs but can clip the first run on a heavy machine.
        # Scaled by HIDMAESTRO_TIMEOUT_SCALE.
        [int]$TimeoutMs = 60000
    )
    $budget = HMScaleMs $TimeoutMs
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $budget) {
        $present = Get-HMPresentDevnodes
        if ($present.Count -gt 0) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

# Wait until the SDK-driven create/teardown has actually propagated to
# PnP and then settled. Two phases:
#
#   Phase A (change detection): poll the present-devnode set until it
#     differs from the snapshot taken at function entry. The harness
#     has just sent a stdin command to the test process; we don't know
#     when the test process will pick it up (single-threaded stdin
#     reader, may still be busy creating its initial controller batch
#     on slow machines, may have other commands queued ahead of ours).
#     Without this phase, the prior stability-only check would exit
#     early on the first poll because nothing had started changing
#     yet -- the test process simply hadn't begun processing the
#     command. On atom (Z8350) the queued-command latency can be
#     30+ s during the initial-batch phase of multi-controller
#     scenarios. Generous Phase A budget so we wait out the queue.
#
#   Phase B (stability): once a change is observed, poll until the
#     present set is stable for two consecutive 200 ms samples. The
#     SDK's TeardownController blocks on CM_NOTIFY_ACTION_DEVICEINSTANCE-
#     REMOVED so by the time it returns the kernel has already settled,
#     so this is usually one or two polls.
#
# If Phase A times out without seeing a change, Phase B becomes a
# brief no-op and we return -- that's the "command was a no-op or
# already complete by the time we got here" path. Total worst case is
# Phase A budget, well above any observed latency.
function Wait-CascadeSettle {
    $changeBudgetMs    = HMScaleMs 60000   # max wait for first state change
    # Phase B's wall-clock cap. Loop EXITS as soon as 2 consecutive 200 ms
    # samples agree, which on a fast box is ~600 ms -- the budget only
    # bites if state churns nonstop. On atom (Z8350, scale=2) a single
    # xbox-360-wired->BT swap can churn for ~40 s when xinputhid hits the
    # 30 s XInput-slot-wait timeout inside SetupController. Phase B must
    # outlast that or it returns mid-swap, the harness sends the next
    # command, commands queue up, Stop-HMTestProcess's graceful budget
    # bursts, Kill fires, devnodes leak. 60 s scaled covers it with
    # margin.
    $stabilityBudgetMs = HMScaleMs 60000
    $stableN           = 2

    # Pull and consume the most recent Send-Cmd snapshot. If null, the
    # caller didn't send a command before this settle (e.g. the final
    # post-Stop-HMTestProcess settle in Test-Scenario), so skip Phase A
    # entirely and just check stability.
    $baseline = $script:HMSendCmdBaseline
    $script:HMSendCmdBaseline = $null

    if ($null -ne $baseline) {
        # Phase A: wait for state to differ from the pre-Send-Cmd baseline.
        # Detects when the test process has begun propagating the command
        # we sent. Critical on slow boxes where stdin commands can queue
        # behind an in-progress initial-batch creation; without this,
        # stability-only would exit on the first poll because the test
        # process simply hadn't started processing yet.
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt $changeBudgetMs) {
            $cur = ((Get-HMPresentDevnodes) | Sort-Object) -join '|'
            if ($cur -ne $baseline) { break }
            Start-Sleep -Milliseconds 200
        }
    }

    # Phase B: wait for stability after change.
    $stableSw = [System.Diagnostics.Stopwatch]::StartNew()
    $prev  = ''
    $count = 0
    while ($stableSw.ElapsedMilliseconds -lt $stabilityBudgetMs) {
        $cur = ((Get-HMPresentDevnodes) | Sort-Object) -join '|'
        if ($cur -eq $prev) {
            $count++
            if ($count -ge $stableN) { return }
        } else {
            $count = 0
            $prev  = $cur
        }
        Start-Sleep -Milliseconds 200
    }
}

# Run a scenario function and capture PASS/FAIL with leftover diagnosis.
function Test-Scenario {
    param(
        [string]$Name,
        [scriptblock]$Body
    )
    if ($Name -notlike $Filter) { return $null }

    Write-Host ''
    Write-Host ('=== ' + $Name + ' ===') -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    # Snapshot baseline BEFORE scenario runs.
    $baseline = Get-HMPresentDevnodes

    $err = $null
    try {
        & $Body
    } catch {
        $err = $_
    }

    # Scenario should have exited the test process by now; settle window
    # for any in-flight resurrection attempts.
    Wait-CascadeSettle
    $after = Get-HMPresentDevnodes

    # Anything PRESENT after that was not PRESENT before is a leftover
    # (the scenario created it and did not clean it up).
    $leftovers = @($after | Where-Object { $baseline -notcontains $_ })

    $sw.Stop()
    $result = [PSCustomObject]@{
        Name        = $Name
        Pass        = ($null -eq $err) -and ($leftovers.Count -eq 0)
        DurationMs  = $sw.ElapsedMilliseconds
        Error       = $err
        Leftovers   = $leftovers
        BaselineCount = $baseline.Count
        AfterCount    = $after.Count
    }

    if ($result.Pass) {
        Write-Host ('  [PASS] ' + $result.DurationMs + 'ms') -ForegroundColor Green
    } else {
        Write-Host ('  [FAIL] ' + $result.DurationMs + 'ms') -ForegroundColor Red
        if ($err) { Write-Host ('    Exception: ' + $err) -ForegroundColor Red }
        foreach ($l in $leftovers) {
            Write-Host ('    Leftover (PRESENT, should be GONE): ' + $l) -ForegroundColor Red
        }
    }
    return $result
}

# ====================================================================
#  Scenarios
# ====================================================================

# S1: Basic single-controller swap cycle (the regression v1.1.31 fixed).
function Scenario-Single-360-BT-360 {
    $proc = Start-HMTestProcess -ProfileIds @($Profiles.Xbox360)
    try {
        if (-not (Wait-CreateBound -ProfileId $Profiles.Xbox360)) { throw "initial create timeout" }
        Sleep-Scaled 4

        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT)
        Wait-CascadeSettle

        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Xbox360)
        Wait-CascadeSettle
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S2: User's secondary regression: double cycle. After fix, no leftovers
# even with two full back-and-forth swaps.
function Scenario-Single-360-BT-360-BT {
    $proc = Start-HMTestProcess -ProfileIds @($Profiles.Xbox360)
    try {
        if (-not (Wait-CreateBound -ProfileId $Profiles.Xbox360)) { throw "initial create timeout" }
        Sleep-Scaled 4

        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT);    Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Xbox360);   Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT);    Wait-CascadeSettle
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S3: Long cycle, 8 swaps. Stress-tests the per-call unique suffix
# scheme (v1.1.30) and the SwDevice teardown path repeatedly.
function Scenario-Single-LongCycle {
    $sequence = @(
        $Profiles.XboxBT, $Profiles.Xbox360,
        $Profiles.XboxBT, $Profiles.Xbox360,
        $Profiles.XboxBT, $Profiles.Xbox360,
        $Profiles.XboxBT, $Profiles.Xbox360
    )
    $proc = Start-HMTestProcess -ProfileIds @($Profiles.Xbox360)
    try {
        if (-not (Wait-CreateBound -ProfileId $Profiles.Xbox360)) { throw "initial create timeout" }
        Sleep-Scaled 4
        foreach ($p in $sequence) {
            Send-Cmd -Proc $proc -Cmd ('0 ' + $p)
            Wait-CascadeSettle
        }
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S4: Cycle starting with BT (xinputhid path first).
function Scenario-Single-BT-360-BT {
    $proc = Start-HMTestProcess -ProfileIds @($Profiles.XboxBT)
    try {
        if (-not (Wait-CreateBound -ProfileId $Profiles.XboxBT)) { throw "initial create timeout" }
        Sleep-Scaled 4

        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Xbox360);   Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT);    Wait-CascadeSettle
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S5: Mixed family swap. Non-Xbox profiles take a different code path
# (plain HID, no companions). Verifies cross-family transitions.
function Scenario-Single-Mixed-Families {
    $proc = Start-HMTestProcess -ProfileIds @($Profiles.Xbox360)
    try {
        if (-not (Wait-CreateBound -ProfileId $Profiles.Xbox360)) { throw "initial create timeout" }
        Sleep-Scaled 4

        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.DualSense);   Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.SwitchPro);   Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT);      Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Xbox360);     Wait-CascadeSettle
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S6: Same-profile re-create. Swap a controller TO ITSELF should still
# tear down the old, create a new with same identity. Tests that the
# per-call unique suffix gives a fresh devnode vs. accidentally reusing.
function Scenario-Single-SameProfileSwap {
    $proc = Start-HMTestProcess -ProfileIds @($Profiles.XboxBT)
    try {
        if (-not (Wait-CreateBound -ProfileId $Profiles.XboxBT)) { throw "initial create timeout" }
        Sleep-Scaled 4

        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT);   Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT);   Wait-CascadeSettle
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S7: Multi-controller create + idle + clean exit. Baseline for
# multi-slot teardown: every slot's devnodes must drain on quit.
function Scenario-Multi-CreateAll-Idle {
    $proc = Start-HMTestProcess -ProfileIds @(
        $Profiles.Xbox360, $Profiles.XboxBT,
        $Profiles.DualSense, $Profiles.SwitchPro
    )
    try {
        # Multi-create takes longer; give it 15s for all slots to bind.
        Sleep-Scaled 15
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S8: Multi-controller, swap one slot's profile while others stay live.
# Catches cross-slot state leakage during a single-slot swap.
function Scenario-Multi-SwapOneSlot {
    $proc = Start-HMTestProcess -ProfileIds @(
        $Profiles.Xbox360, $Profiles.Xbox360,
        $Profiles.Xbox360, $Profiles.Xbox360
    )
    try {
        Sleep-Scaled 15
        # Swap slot 1 through a full cycle; slots 0,2,3 should be untouched.
        Send-Cmd -Proc $proc -Cmd ('1 ' + $Profiles.XboxBT);    Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('1 ' + $Profiles.Xbox360);   Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('1 ' + $Profiles.XboxBT);    Wait-CascadeSettle
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S9: Multi-controller, swap each slot to a different profile. The
# "real PadForge" usage where every slot can be reconfigured live.
function Scenario-Multi-SwapAllSlots {
    $proc = Start-HMTestProcess -ProfileIds @(
        $Profiles.Xbox360, $Profiles.Xbox360,
        $Profiles.Xbox360, $Profiles.Xbox360
    )
    try {
        Sleep-Scaled 15
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT);       Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('1 ' + $Profiles.DualSense);    Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('2 ' + $Profiles.SwitchPro);    Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('3 ' + $Profiles.XboxOneBT);    Wait-CascadeSettle
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S10: Multi-controller + remove (without replacement). Tests that
# 'remove <idx>' tears down a slot fully without disturbing siblings.
function Scenario-Multi-RemoveOne {
    $proc = Start-HMTestProcess -ProfileIds @(
        $Profiles.Xbox360, $Profiles.XboxBT,
        $Profiles.DualSense
    )
    try {
        Sleep-Scaled 15
        Send-Cmd -Proc $proc -Cmd 'remove 1';   Wait-CascadeSettle
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S11: Multi-controller, multiple xinputhid-INF profiles concurrently.
# xinputhid binds via INF match on hardware ID. Three different
# xinputhid profiles in three slots verifies the kernel filter does
# not cross-bind or clip any of them on teardown.
function Scenario-Multi-MultipleXinputhid {
    $proc = Start-HMTestProcess -ProfileIds @(
        $Profiles.XboxBT, $Profiles.XboxOneBT, $Profiles.XboxEliteBT
    )
    try {
        Sleep-Scaled 18
        # Swap slot 1 to non-xinputhid family, then back.
        Send-Cmd -Proc $proc -Cmd ('1 ' + $Profiles.Xbox360);   Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('1 ' + $Profiles.XboxOneBT); Wait-CascadeSettle
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S12: Process force-kill recovery. Spawn, create, KILL (no graceful
# quit), then a fresh process spawn (cleanup) must purge the orphans.
# Regression catch for: force-kill leaving phantoms that the next
# session's RemoveAllVirtualControllers can not drain.
function Scenario-ForceKill-Recovery {
    $proc1 = Start-HMTestProcess -ProfileIds @($Profiles.Xbox360, $Profiles.XboxBT)
    Sleep-Scaled 15
    # Hard-kill: no quit, no clean dispose. Simulates user closing
    # PadForge from Task Manager / a crash.
    try { $proc1.Kill($true) } catch {}
    $proc1.WaitForExit(5000) | Out-Null
    Sleep-Scaled 3

    # Now run a clean session that should clean up the orphans on
    # startup and leave a clean post-state on quit.
    $proc2 = Start-HMTestProcess -ProfileIds @($Profiles.DualSense)
    try {
        Sleep-Scaled 12
    } finally {
        Stop-HMTestProcess -Proc $proc2 | Out-Null
    }
}

# S13: Across-process recreation. Mirrors every PadForge launch after a
# prior session: same controllerIndex, same profile, fresh process. The
# kernel's reuse-existing fast path on identical (enumerator+suffix+
# ContainerID) tuples is the trap; v1.1.20's per-process PID prefix
# fixes it. This regression-catches if that prefix ever stops varying.
function Scenario-AcrossProcess-Recreation {
    $proc1 = Start-HMTestProcess -ProfileIds @($Profiles.XboxBT, $Profiles.Xbox360)
    Sleep-Scaled 15
    Stop-HMTestProcess -Proc $proc1 | Out-Null
    Wait-CascadeSettle  # wait for the prior proc's teardowns to fully drain

    # Same profiles, fresh process. Should NOT hit empty-shell state on
    # subsequent runs.
    $proc2 = Start-HMTestProcess -ProfileIds @($Profiles.XboxBT, $Profiles.Xbox360)
    try {
        Sleep-Scaled 15
        # Also exercise a swap on this second run to confirm the
        # recreated devnodes are functional, not empty shells.
        Send-Cmd -Proc $proc2 -Cmd ('0 ' + $Profiles.Xbox360);   Wait-CascadeSettle
        Send-Cmd -Proc $proc2 -Cmd ('1 ' + $Profiles.XboxBT);    Wait-CascadeSettle
    } finally {
        Stop-HMTestProcess -Proc $proc2 | Out-Null
    }
}

# S14: Rapid swaps without settle. Stdin commands queued back-to-back,
# no PowerShell sleep between them. The test app processes them
# sequentially but with zero gap. Stress-tests the per-controllerIndex
# teardown gate and reentrancy in Setup/Teardown.
function Scenario-RapidSwaps-NoSettle {
    $proc = Start-HMTestProcess -ProfileIds @($Profiles.Xbox360)
    try {
        if (-not (Wait-CreateBound -ProfileId $Profiles.Xbox360)) { throw "initial create timeout" }
        Sleep-Scaled 4

        # Queue 4 swap commands with no inter-command sleep. Each one
        # processes after the prior LiveSwap returns synchronously; no
        # external pacing.
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT)
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Xbox360)
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT)
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Xbox360)
        # Now wait long enough for ALL queued swaps to drain. Each is
        # ~5-15s depending on family; budget 60s for the full chain.
        Sleep-Scaled 60
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S15: 6-controller mixed baseline. Beyond XInput's 4-slot limit. Tests
# slot-allocator skip behavior + ContainerID encoding for higher
# indices. Documented as the HM "6-controller baseline" use case.
function Scenario-Multi-SixControllers {
    $proc = Start-HMTestProcess -ProfileIds @(
        $Profiles.Xbox360, $Profiles.XboxBT,
        $Profiles.DualSense, $Profiles.SwitchPro,
        $Profiles.XboxOneBT, $Profiles.DualSenseBT
    )
    try {
        # 6-controller create takes longer due to per-instance PnP
        # work; give it 25s.
        Sleep-Scaled 25
        # Swap a slot beyond XInput's 4 to verify high-index ContainerID
        # encoding and teardown behave correctly.
        Send-Cmd -Proc $proc -Cmd ('5 ' + $Profiles.Xbox360);   Wait-CascadeSettle
    } finally {
        # Multi-controller graceful shutdown can take longer.
        Stop-HMTestProcess -Proc $proc -GracefulMs 60000 | Out-Null
    }
}

# S16: Same-VID-PID family swap. xbox-360-wired and xbox-360-arcade-stick
# both report 045E:028E but have different profile metadata
# (ProductString, descriptor details). Tests the registry-reuse path
# when only profile-level metadata differs.
function Scenario-Single-SameVidPid {
    $proc = Start-HMTestProcess -ProfileIds @($Profiles.Xbox360)
    try {
        if (-not (Wait-CreateBound -ProfileId $Profiles.Xbox360)) { throw "initial create timeout" }
        Sleep-Scaled 4

        # xbox-360-arcade-stick: same 045E:028E as xbox-360-wired.
        Send-Cmd -Proc $proc -Cmd '0 xbox-360-arcade-stick';     Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Xbox360);    Wait-CascadeSettle
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S17: Force-kill mid-cascade. The trickiest force-kill timing: kill
# the test process while a Series BT teardown is still in its
# xinputhid filter-unload cascade (5s into a ~10s window). Phantoms
# left by a hard kill at this exact moment have the worst chance of
# surviving the next session's RemoveAllVirtualControllers.
function Scenario-ForceKill-MidCascade {
    $proc1 = Start-HMTestProcess -ProfileIds @($Profiles.XboxBT)
    try {
        if (-not (Wait-CreateBound -ProfileId $Profiles.XboxBT)) { throw "initial create timeout" }
        Sleep-Scaled 6   # let xinputhid bind

        # Kick off swap-back-to-360. BT teardown will run xinputhid
        # filter-unbind cascade (~10s).
        Send-Cmd -Proc $proc1 -Cmd ('0 ' + $Profiles.Xbox360)
        # Sleep long enough that the swap is mid-cascade but not done.
        Sleep-Scaled 5
    } finally {
        # Hard-kill while teardown still in flight.
        try { $proc1.Kill($true) } catch {}
        $proc1.WaitForExit(5000) | Out-Null
    }

    # Wait for any in-flight kernel work to complete or stall.
    Sleep-Scaled 5

    # Fresh session should reliably clean up whatever was left in mid-
    # cascade state.
    $proc2 = Start-HMTestProcess -ProfileIds @($Profiles.SwitchPro)
    try {
        Sleep-Scaled 12
    } finally {
        Stop-HMTestProcess -Proc $proc2 | Out-Null
    }
}

# S18: Alternating multi-target swap pattern (A->B->A->C->A). Stresses
# the per-call atomic suffix allocator and verifies that revisiting a
# prior profile after intervening swaps does not collide with stale
# session state.
function Scenario-Single-AlternatingPattern {
    $proc = Start-HMTestProcess -ProfileIds @($Profiles.Xbox360)
    try {
        if (-not (Wait-CreateBound -ProfileId $Profiles.Xbox360)) { throw "initial create timeout" }
        Sleep-Scaled 4

        # A=Xbox360, B=XboxBT, C=DualSense.
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT);    Wait-CascadeSettle  # A->B
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Xbox360);   Wait-CascadeSettle  # B->A
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.DualSense); Wait-CascadeSettle  # A->C
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Xbox360);   Wait-CascadeSettle  # C->A
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT);    Wait-CascadeSettle  # A->B
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Xbox360);   Wait-CascadeSettle  # B->A
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S19: Rapid multi-slot swap (closest stdin proxy for PadForge's
# concurrent Task.Run async-dispose path during ApplyAscendingIndex
# Preemption). Queues swaps for 4 different slots back-to-back with no
# pacing. The test app's stdin processor is single-threaded so the
# swaps serialize, but the kernel cascade work for each previous swap
# may still be in flight when the next one starts; the per-controller
# Index teardown gate must serialize correctly.
function Scenario-Multi-RapidMultiSlotSwap {
    $proc = Start-HMTestProcess -ProfileIds @(
        $Profiles.Xbox360, $Profiles.Xbox360,
        $Profiles.Xbox360, $Profiles.Xbox360
    )
    try {
        Sleep-Scaled 18  # 4-controller initial bind

        # Queue 4 swaps for 4 different slots with no inter-command sleep.
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT)
        Send-Cmd -Proc $proc -Cmd ('1 ' + $Profiles.DualSense)
        Send-Cmd -Proc $proc -Cmd ('2 ' + $Profiles.SwitchPro)
        Send-Cmd -Proc $proc -Cmd ('3 ' + $Profiles.XboxOneBT)
        # Each swap takes 5-15s depending on family; budget generously.
        Sleep-Scaled 80
    } finally {
        Stop-HMTestProcess -Proc $proc -GracefulMs 60000 | Out-Null
    }
}

# S20: Explicit HMContext.Dispose cascade with a heterogeneous mix.
# Every prior scenario's `quit` exercises HMContext.Dispose implicitly,
# but only with up-to-4 controllers. This one stresses the per-controller
# parallel-dispose path with all four families simultaneously: ROOT-
# enumerated, SWD-XUSB, SWD-gamepad-xinputhid, and plain HID. The
# parallel cascade in HMContext.DisposeControllersInParallel must
# correctly tear down every kind without cross-stack interference.
function Scenario-Multi-HeterogeneousCascade {
    $proc = Start-HMTestProcess -ProfileIds @(
        $Profiles.Xbox360,    # ROOT main HID + SWD XUSB companion
        $Profiles.XboxBT,     # SWD gamepad-companion (xinputhid)
        $Profiles.XboxOneBT,  # SWD gamepad-companion (xinputhid, alt INF)
        $Profiles.DualSense   # plain HID, no companions
    )
    try {
        Sleep-Scaled 18
        # No swaps; we want to test the simultaneous-dispose path that
        # only fires on `quit` / context-Dispose.
    } finally {
        Stop-HMTestProcess -Proc $proc -GracefulMs 60000 | Out-Null
    }
}

# S21: Custom profile create + idle + quit. Smallest possible exercise of
# the PadForge-style custom profile path: HMProfileBuilder-built profile
# with a synthesized HidDescriptor, faux VID/PID (BEEF:F000), no
# embedded driver-mode hints. Verifies the SDK can create, run, and
# tear down a runtime-built profile through the same path as any
# embedded one.
function Scenario-Custom-CreateIdle {
    $proc = Start-HMTestProcess -ProfileIds @($Profiles.Custom)
    try {
        Sleep-Scaled 10
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S22: Custom-profile swap cycle. Custom <-> embedded profile, multiple
# rounds. Catches: registry-reuse path for a custom-VID/PID device,
# suffix allocator handling for BEEF:F000, and any code path that
# special-cases embedded profiles vs runtime-built ones.
function Scenario-Custom-SwapCycle {
    $proc = Start-HMTestProcess -ProfileIds @($Profiles.Custom)
    try {
        Sleep-Scaled 8
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Xbox360);   Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Custom);    Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT);    Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Custom);    Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.DualSense); Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Custom);    Wait-CascadeSettle
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S23: Multi-controller mix including the custom profile. A full
# representative consumer config: Xbox 360 wired, Xbox Series BT,
# DualSense, Switch Pro, and a PadForge-style custom -- all five
# families/families-equivalents at once. Then a swap targeting the
# custom slot to verify it tears down clean alongside the rest.
function Scenario-Multi-CustomInMix {
    $proc = Start-HMTestProcess -ProfileIds @(
        $Profiles.Xbox360,
        $Profiles.XboxBT,
        $Profiles.DualSense,
        $Profiles.SwitchPro,
        $Profiles.Custom
    )
    try {
        Sleep-Scaled 22  # 5-controller initial bind
        # Swap the custom slot through a couple of family transitions.
        Send-Cmd -Proc $proc -Cmd ('4 ' + $Profiles.Xbox360);   Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('4 ' + $Profiles.Custom);    Wait-CascadeSettle
    } finally {
        Stop-HMTestProcess -Proc $proc -GracefulMs 60000 | Out-Null
    }
}

# S24: PID Force Feedback shared-section round-trip. Verifies that
# HMController.PublishPidPool / PublishPidBlockLoad / PublishPidState
# write the bytes the driver's IOCTL_UMDF_HID_GET_FEATURE handler reads.
# The probe creates its own controller, opens the named section
# (Global\HIDMaestroPidState{N}) directly, and asserts every published
# field is exactly what's in the section. Also asserts the lazy section
# is NOT created until first PublishPid* (vJoy-style "FFB not enabled"
# gate) and that BL_LoadStatus stays 0 between PublishPidPool and the
# first PublishPidBlockLoad (the v1.1.36 spec gate).
#
# Closes the v1.1.35 test blindspot that issue #16 surfaced.
function Scenario-PidFfb-RoundTrip {
    $probe = Join-Path $PSScriptRoot '..\probes\pid_ffb_roundtrip\bin\Release\net10.0-windows10.0.26100.0\PidFfbRoundtrip.exe'
    $probe = [System.IO.Path]::GetFullPath($probe)
    if (-not (Test-Path $probe)) {
        throw "pid_ffb_roundtrip probe not built. Run: dotnet build test/probes/pid_ffb_roundtrip -c Release"
    }
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw "PidFfbRoundtrip exited $($p.ExitCode)"
    }
}

# S25: PID FFB alloc/free + pool exhaustion + multi-controller independence.
# Companion to S24. Where S24 covers the static SDK->shared-section
# contract (lazy section creation, Pool/State/BL field round-trip),
# S25 covers the dynamic v1.1.37 invariants:
#   - Legacy PublishPidBlockLoad override path: alloc EBI, alloc again,
#     free, exhaust pool. Verifies the consumer-publish surface still
#     works (driver bitmap stays untouched -- that path is for the
#     driver-side allocator).
#   - Multi-controller: two virtuals get independent
#     Global\HIDMaestroPidState{N} sections; PublishPidPool on A doesn't
#     leak into B.
#   - File trace directory at %PROGRAMDATA%\HIDMaestro is creatable so
#     the v1.1.37 HMAESTRO_TRACE=1 file fallback has a target.
#   - Best-effort dynamic SetFeature(0x11) / SetOutputReport(0x11) -- if
#     HidClass accepts either, verifies driver allocated EBI synchronously.
#     If both rejected (descriptor parse), SKIPs -- PadForge's FfbTest run
#     remains the dynamic arbiter.
function Scenario-PidFfb-AllocFree {
    $probe = Join-Path $PSScriptRoot '..\probes\pid_ffb_alloc_free\bin\Release\net10.0-windows10.0.26100.0\PidFfbAllocFree.exe'
    $probe = [System.IO.Path]::GetFullPath($probe)
    if (-not (Test-Path $probe)) {
        throw "pid_ffb_alloc_free probe not built. Run: dotnet build test/probes/pid_ffb_alloc_free -c Release"
    }
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw "PidFfbAllocFree exited $($p.ExitCode)"
    }
}

# S26: PID FFB end-to-end via FfbTest (the SharpDX/DirectInput8 PID
# harness from the vJoy-Brunner clone, copied into our repo at
# test/probes/pid_setusages_probe/FfbTest/FfbTest.exe).
#
# This scenario was added after the v1.1.35-v1.1.39 PID FFB
# investigation (issue #16) to close the "best-effort SKIP" trap that
# made S24/S25 pass while the underlying feature was broken. S26
# deploys a virtual with the full PadForge HMaestroFfbDescriptor
# (transcribed) and runs FfbTest against it. FfbTest's auto-probe
# phase calls CreateEffect(GUID_ConstantForce) and prints `SUCCESS:`
# per variant on success or `Fatal error 0xC0000005` on a pid.dll AV.
# The probe parses that output and exits 0 only if a Constant Force
# CreateEffect succeeded with no AV -- which is exactly the regression
# bar for issue #16.
function Scenario-PidFfb-FfbTest {
    $probe = Join-Path $PSScriptRoot '..\probes\pid_setusages_probe\bin\Release\net10.0-windows10.0.26100.0\PidSetUsagesProbe.exe'
    $probe = [System.IO.Path]::GetFullPath($probe)
    if (-not (Test-Path $probe)) {
        throw "pid_setusages_probe not built. Run: dotnet build test/probes/pid_setusages_probe -c Release"
    }
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("PidSetUsagesProbe exited " + $p.ExitCode + " - Constant Force CreateEffect did not succeed")
    }
}

# S27: Xbox 360 wired d-pad XInput round-trip. Closes issue #19. Submits each
# HMHat direction via HMController.SubmitState on an xbox-360-wired virtual,
# reads XInputGetState through xinput1_4 -> xusb22.sys -> IOCTL_XUSB_GET_STATE,
# and asserts wButtons.DPAD_* matches the expected mask. Pre-v1.3.3 the SDK
# never wrote the 4-bit hat into the GIP buffer's btnHigh, so the companion's
# (btnHigh >> 2) & 0x0F always read zero and wButtons.DPAD_* never fired.
function Scenario-Xbox360-Dpad-XInput {
    $probe = Join-Path $PSScriptRoot '..\probes\xbox_dpad_xinput_check\bin\Release\net10.0-windows10.0.26100.0\XboxDpadXInputCheck.exe'
    $probe = [System.IO.Path]::GetFullPath($probe)
    if (-not (Test-Path $probe)) {
        throw "xbox_dpad_xinput_check not built. Run: dotnet build test/probes/xbox_dpad_xinput_check -c Release"
    }
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("XboxDpadXInputCheck exited " + $p.ExitCode + " - one or more d-pad directions did not match expected wButtons.DPAD_* mask")
    }
}

# S28: fine-grained hat encoder regression. Pure encoder unit test - no virtual
# device, no driver install. Builds profiles via HidDescriptorBuilder with hat
# resolutions 8/16/360, encodes reports across all four HMGamepadState hat-input
# shapes (Hat enum, HatRaw, HatHundredths, HatDegrees), reads back the wire bytes
# and asserts each input shape produces the expected descriptor field value.
# Catches future regressions in the v1.3.4 priority chain or the legacy AddHat
# LogMax convention.
function Scenario-Hat-Resolution-Check {
    $probe = Join-Path $PSScriptRoot '..\probes\hat_resolution_check\bin\Release\net10.0-windows10.0.26100.0\HatResolutionCheck.exe'
    $probe = [System.IO.Path]::GetFullPath($probe)
    if (-not (Test-Path $probe)) {
        throw "hat_resolution_check not built. Run: dotnet build test/probes/hat_resolution_check -c Release"
    }
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("HatResolutionCheck exited " + $p.ExitCode + " - one or more hat-input shapes did not encode the expected descriptor bits")
    }
}

# Resolve a probe binary. RID-agnostic output ONLY (.../net*/<exe>):
# the 2026-07-21 audit converged every probe consumer on the plain
# `dotnet build` output because the freshness gate above hash-verifies
# exactly that flavor. Falling back to a bin/.../win-x64/ copy here
# would reopen the stale-flavor hole the gate exists to close (a
# leftover -r build could shadow the fresh output indefinitely).
function Resolve-ProbeBinary {
    param([string]$ProbeDir, [string]$Exe)
    $base = Join-Path $PSScriptRoot ('..\probes\' + $ProbeDir + '\bin\Release\net10.0-windows10.0.26100.0')
    $base = [System.IO.Path]::GetFullPath($base)
    $candidate = Join-Path $base $Exe
    if (Test-Path $candidate) { return $candidate }
    throw ("$ProbeDir not built. Run: dotnet build test/probes/$ProbeDir -c Release")
}

# S29: Sony BT Report 0x31 input encoder. Closes #20. Verifies the data-driven
# vendor-blob input encoder produces a correct 78-byte Report 0x31 with the
# expected stick / trigger / button / hat byte placement and a valid CRC32 footer
# (prefix [0xA1, 0x31] over bytes 1..73). Pre-v1.3.5 the SDK locked to Report 1
# and Steam Input / dualsense-tester saw a broken DualSense BT.
function Scenario-Sony-BT-Report31-Check {
    $probe = Resolve-ProbeBinary 'sony_bt_report31_check' 'SonyBtReport31Check.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("SonyBtReport31Check exited " + $p.ExitCode + " - extended input encoder produced unexpected bytes for dualsense-bt-full")
    }
}

# S30: Sony BT extendedOutputReport round-trip. Validates the inverse direction
# of the v1.3.5 vendor-blob codec: HMOutputEncoder.Encode(profile, fields) produces
# wire-format bytes whose declared fields land at the right offsets, the CRC32
# matches an independent computation with prefix [0xA2, 0x31], and decode(encode(x))
# preserves every declared field's value byte-for-byte.
function Scenario-Sony-BT-Output-Decode-Check {
    $probe = Resolve-ProbeBinary 'sony_bt_output_decode_check' 'SonyBtOutputDecodeCheck.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("SonyBtOutputDecodeCheck exited " + $p.ExitCode + " - output codec did not round-trip cleanly")
    }
}

# S31: full Sony data-driven coverage. Validates the v1.3.5 vendor-blob blocks
# across the full Sony catalog: DS4 BT (Report 0x11) input + output round-trip
# with CRC32 prefix [0xA1, 0x11] / [0xA2, 0x11], DS5 USB (Report 0x02) output,
# and DS4 USB (Report 0x05) output. Closes the v1.3.5 scope on every Sony
# variant the SDK ships - DS5 BT, DS5 BT (full), DS5 Edge BT, DS4 v2 BT, DS5
# USB, DS5 Edge USB, DS4 v1 USB, DS4 v1 USB (full), DS4 v2 USB.
function Scenario-Sony-Data-Driven-Coverage {
    $probe = Resolve-ProbeBinary 'sony_data_driven_coverage' 'SonyDataDrivenCoverage.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("SonyDataDrivenCoverage exited " + $p.ExitCode + " - DS4 BT / USB output codec did not round-trip cleanly")
    }
}

# S32: v1.3.5 features + regression check. Combined probe covering every
# fix from the issue #21 PadForge spot-check rounds:
#   Round 3 — USB Sony profiles can't trigger the codec hot path
#             (regression: per-frame VendorBlobCodec.EncodeInput at 250 Hz
#             on USB DS5 caused stick-jerkiness on ds.daidr.me)
#   Round 5 — DS5 sensor byte positions (gyro/accel/sensorTimestamp shift +1
#             because Linux dualsense_input_report struct excludes report_id)
#   Round 6 — DS4 BT armOn IDs are DS4-canonical (0x02/0xA3, not DS5's
#             0x05/0x09/0x20)
#   Round 7 — DS4 BT versionNumber=0 so Chromium's
#             Dualshock4Controller::BusTypeFromVersionNumber routes vibration
#             through Report 0x11 BT instead of Report 0x05 USB
#   Round 8 — DS5 Edge USB activeProfile=0x80 via inputDefaults overlay,
#             BT Edge same constant declared as a uint8 codec field with
#             initial=128 ordered before the CRC32 entry so it participates
#             in the checksum
#   Round 8b — SDK plumbing: ControllerProfile.InputDefaults +
#              InputBytePatch list, applied in BOTH SubmitState's legacy
#              branch AND SubmitRawReport (PadForge's USB path calls both
#              and the raw bytes would otherwise clobber the overlay)
#   Round 8c — Edge USB inputDefaults extended to bytes 50/51/52 = 0 to
#              override PadForge's SonyReportPackers Timer 2 counter at
#              data[48..51] (was leaking the counter's middle bytes into
#              triggerLevel and triggering isStickModuleLost ~94% of frames)
#   Feature 1 — touchpad-finger / int16-le / uint32-le / bitfield /
#               uint8-battery codec types + sensor/touchpad/battery byte
#               positions in DS5 USB+BT
#   Feature 2 — audio block declared in all 9 Sony extendedOutputReport
#               specs (DS5: headphoneVolume/speakerVolume/micVolume/
#               audioControlFlags; DS4: stereo headphoneVolumeLeft/Right
#               + micVolume/speakerVolume)
#   Feature 3 — DS5 BT btTag uint8-rolling stride 16 (cycles 0x00..0xF0
#               then wraps; per-controller HMController.EncodeOutput
#               threads the auto-advancing counter)
function Scenario-V135-Features-Check {
    $probe = Resolve-ProbeBinary 'v135_features_check' 'V135FeaturesCheck.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("V135FeaturesCheck exited " + $p.ExitCode + " - one or more v1.3.5 fix invariants regressed (see probe stdout)")
    }
}

# S33: Trigger axis classifier regression (issue #22). Pure encoder unit test
# - no virtual device, no driver install. Builds the (1 stick, 1 trigger) and
# (2 sticks, 1 trigger) Custom-Extended layouts via HidDescriptorBuilder, plus
# a 4-axis DInput hand-rolled descriptor and the Xbox-360-wired Vx/Vy combined-Z
# layout, and asserts each shape's Z/Rz fields land in the right semantic slot
# AND that BuildReport writes the expected wire bytes. Pre-fix the lone trigger
# in case 1 was claimed as RightStickX (silent 0%), and the lone trigger in
# case 2 ran the Xbox 360 combined-Z formula and produced an inverted/offset
# wire value (75% rest -> 25% full press). Catches future regressions in the
# Z/Rz classifier or the lone-LeftTrigger encoder branch.
function Scenario-Trigger-Classifier-Check {
    $probe = Resolve-ProbeBinary 'trigger_classifier_check' 'TriggerClassifierCheck.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("TriggerClassifierCheck exited " + $p.ExitCode + " - one or more trigger-classifier invariants regressed (see probe stdout)")
    }
}

# S34: end-to-end trigger-axis live wire check. Creates real virtual
# controllers via HMContext.CreateController for the (2 sticks, 2 triggers),
# (2 sticks, 1 trigger), and (1 stick, 1 trigger) PadForge custom shapes,
# submits state via HMController.SubmitState, opens the HID interface, and
# reads back the on-wire input report via HidD_GetInputReport. Closes the
# gap S33 left: S33 verifies BuildReportInto in isolation; S34 proves the
# whole SubmitState → BuildReportInto → shared memory → driver →
# HidClass round trip lands the trigger byte where the classifier said
# it would. The unit-only check passing while the live wire was broken is
# exactly the failure shape that hit the v1.3.6 release before catch.
function Scenario-Trigger-Live-Check {
    $probe = Resolve-ProbeBinary 'trigger_live_check' 'TriggerLiveCheck.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("TriggerLiveCheck exited " + $p.ExitCode + " - end-to-end trigger wire fidelity regressed (see probe stdout)")
    }
}

# S35: SideWinder Force Feedback 2 PID FFB end-to-end (v1.3.7).
#
# Microsoft SideWinder Force Feedback 2 firmware uses non-canonical PID
# Report IDs (Pool=0x03, Block Load=0x02, Create New Effect=0x01,
# Block Free=0x0B, Device Control=0x0C) instead of HM's traditional
# canonical 0x13/0x12/0x11/0x1B/0x1C. v1.3.7 makes the driver
# descriptor-aware: SDK runs PidReportIdExtractor on the profile
# descriptor at controller create, writes the discovered RIDs into the
# per-controller shared section, and the driver IOCTL handlers route by
# those RIDs instead of canonical constants.
#
# This scenario exercises the full path: load embedded SideWinder profile
# (with original 1355-byte HMDeviceExtractor descriptor verbatim), create
# real virtual, publish PidPool, run SetFeature(0x01 Create New Effect)
# and GetFeature(0x03 Pool) handshake. Both must succeed for dinput8/
# pid.dll to enumerate FFB on a SideWinder virtual end-to-end.
function Scenario-Sidewinder-Ffb-Check {
    $probe = Resolve-ProbeBinary 'sidewinder_ffb_check' 'SideWinderFfbCheck.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("SideWinderFfbCheck exited " + $p.ExitCode + " - SideWinder PID FFB end-to-end regressed (see probe stdout)")
    }
}

# S36: arbitrary-axis addressing — HMAxis enum / ExtraAxes / AvailableAxes /
# AddAxis (v1.3.8). Pre-v1.3.8 HMGamepadState exposed only 4 sticks + 2 triggers,
# so flight-stick throttle sliders, separate brake/throttle/clutch pedals, HOTAS
# rudder pedals were unreachable from consumers even when the descriptor declared
# the field. v1.3.8 adds parallel HID-usage-keyed surfaces. This probe covers:
# (1) discovery on canonical catalog profiles surfaces the full descriptor axis
# set; (2) ExtraAxes wire-byte fidelity at the bit offsets/sizes the descriptor
# declares; (3) HidDescriptorBuilder.AddAxis round-trips through the parser; and
# (4) explicit-beats-implicit ordering when ExtraAxes and a semantic slot target
# the same field. No driver install, no virtual device.
function Scenario-Axis-Addressing-Check {
    $probe = Resolve-ProbeBinary 'axis_addressing_check' 'AxisAddressingCheck.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("AxisAddressingCheck exited " + $p.ExitCode + " - HMAxis / ExtraAxes / AvailableAxes / AddAxis fidelity regressed (see probe stdout)")
    }
}

# S37: layout audit (v1.3.9). For every catalog profile, verifies the
# layout block (when authored) schema-validates against the descriptor and
# every axis/button reference resolves. For profiles WITHOUT a layout
# block, asserts the classifier-fallback Sticks/Triggers lists are
# coherent. No driver install, no virtual device.
function Scenario-Layout-Audit-Check {
    $probe = Resolve-ProbeBinary 'layout_audit_check' 'LayoutAuditCheck.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("LayoutAuditCheck exited " + $p.ExitCode + " - layout schema/descriptor mismatch (see probe stdout)")
    }
}

# S38: Sony BT axis-role regression (v1.3.13, #23). Asserts the Sony
# Bluetooth profiles' Profile.Sticks/Triggers resolve to the Sony-convention
# axis keys (right stick Z/Rz, triggers Rx/Ry) and that a Sony-convention
# state.Axes fill encodes through VendorBlobCodec with no right-stick/trigger
# swap. No driver install, no virtual device.
function Scenario-Sony-BT-Axis-Role-Check {
    $probe = Resolve-ProbeBinary 'sony_bt_axis_role_check' 'SonyBtAxisRoleCheck.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("SonyBtAxisRoleCheck exited " + $p.ExitCode + " - Sony BT axis roles swapped or mis-resolved (see probe stdout)")
    }
}

# S39: Foreign devnode survival regression (v1.3.16, #28). Creates a foreign
# root-enumerated HIDClass devnode with HardwareID `root\VID_1234&PID_BEAD&
# REV_0222` (the vJoy shape), runs a full HM CreateController + Dispose
# cycle, then asserts the foreign device was not disabled, uninstalled,
# renamed, or had ControllerIndex injected into its Device Parameters by
# any of the now-HwId-gated sweep paths in DeviceOrchestrator /
# DeviceNodeCreator / DeviceProperties / DeviceManager.
function Scenario-Foreign-Devnode-Survival-Check {
    $probe = Resolve-ProbeBinary 'foreign_devnode_survival_check' 'ForeignDevnodeSurvivalCheck.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("ForeignDevnodeSurvivalCheck exited " + $p.ExitCode + " - HM sweep mutated a foreign root devnode (issue #28; see probe stdout)")
    }
}

# S40: Xbox 360 combined-Z + split-trigger regression (v1.3.17, PadForge #130).
# Loads the real xbox-360-wired profile and asserts the v1.3.9 state.Axes
# refactor's combined-Z synthesis sources trigger values from the canonical
# user-facing positions (HMAxis.Z / HMAxis.Rz or axisMap-declared), then
# writes BOTH the combined Z byte AND the separate Vx / Vy bytes so
# joy.cpl sees the dinput-combined trigger and WGI sees the raw LT / RT.
function Scenario-Xbox-Combined-Trigger-Check {
    $probe = Resolve-ProbeBinary 'xbox_combined_trigger_check' 'XboxCombinedTriggerCheck.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("XboxCombinedTriggerCheck exited " + $p.ExitCode + " - Xbox 360 split-trigger workaround regressed (PadForge#130; see probe stdout)")
    }
}

# Companion to S40. S40 exercises the HID descriptor lane (BuildReportInto's
# combined-Z synthesis). S41 exercises the GIP-buffer lane that drives every
# non-DirectInput API via the XUSB companion. The PadForge#130 follow-up
# (MultipadTester XInput / WGI / RawInput stuck at 0.5 even though joy.cpl
# was correct) was missed by S40 because S40 doesn't touch SubmitState's
# mlt / mrt resolution. S41 covers it.
function Scenario-Xbox-Gip-Trigger-Resolver-Check {
    $probe = Resolve-ProbeBinary 'xbox_gip_trigger_resolver_check' 'XboxGipTriggerResolverCheck.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("XboxGipTriggerResolverCheck exited " + $p.ExitCode + " - GIP-side trigger resolver regressed (PadForge#130 follow-up; see probe stdout)")
    }
}

# --------------------------------------------------------------------
#  Composite USB personas and the bundled transport (issue #39)
# --------------------------------------------------------------------

function Scenario-Usb-Composite-Schema {
    $probe = Resolve-ProbeBinary 'usb_composite_schema_check' 'UsbCompositeSchemaCheck.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("UsbCompositeSchemaCheck exited " + $p.ExitCode + " - a composite persona no longer matches its hardware dump, or a composite leaked into the UMDF2 path (see probe stdout)")
    }
}

function Scenario-Usbip-Server-Protocol {
    $probe = Resolve-ProbeBinary 'usbip_server_check' 'UsbipServerCheck.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("UsbipServerCheck exited " + $p.ExitCode + " - the USB/IP wire contract regressed (descriptors, HID both directions, isochronous pacing, or unlink; see probe stdout)")
    }
}

function Scenario-Usbip-Bundle-Deploy {
    $probe = Resolve-ProbeBinary 'usbip_bundle_check' 'UsbipBundleCheck.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("UsbipBundleCheck exited " + $p.ExitCode + " - the bundled transport is missing, its hash no longer matches the upstream release, or the deploy path stopped refusing tampered bytes (see probe stdout)")
    }
}

# The only scenario that needs the transport actually deployed. On a
# machine without it, the probe deploys it (that IS the thing under
# test). Exit 2 means the probe declined to run for an environmental
# reason and is reported as a skip rather than a failure.
function Scenario-Usbip-E2E-Composite {
    $probe = Resolve-ProbeBinary 'usbip_e2e_check' 'UsbipE2ECheck.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -eq 2) {
        Write-Host '    [SKIP] transport unavailable in this environment' -ForegroundColor Yellow
        return
    }
    if ($p.ExitCode -ne 0) {
        throw ("UsbipE2ECheck exited " + $p.ExitCode + " - a composite persona failed end to end through the real USB stack (enumeration, HID, audio endpoints, or teardown; see probe stdout)")
    }
}

# ====================================================================
#  Runner
# ====================================================================

$scenarios = @(
    @{ Name = 'S01_Single_360_BT_360';            Body = ${function:Scenario-Single-360-BT-360} },
    @{ Name = 'S02_Single_360_BT_360_BT';         Body = ${function:Scenario-Single-360-BT-360-BT} },
    @{ Name = 'S03_Single_LongCycle_8swaps';      Body = ${function:Scenario-Single-LongCycle} },
    @{ Name = 'S04_Single_BT_360_BT';             Body = ${function:Scenario-Single-BT-360-BT} },
    @{ Name = 'S05_Single_Mixed_Families';        Body = ${function:Scenario-Single-Mixed-Families} },
    @{ Name = 'S06_Single_SameProfileSwap';       Body = ${function:Scenario-Single-SameProfileSwap} },
    @{ Name = 'S07_Multi_CreateAll_Idle';         Body = ${function:Scenario-Multi-CreateAll-Idle} },
    @{ Name = 'S08_Multi_SwapOneSlot';            Body = ${function:Scenario-Multi-SwapOneSlot} },
    @{ Name = 'S09_Multi_SwapAllSlots';           Body = ${function:Scenario-Multi-SwapAllSlots} },
    @{ Name = 'S10_Multi_RemoveOne';              Body = ${function:Scenario-Multi-RemoveOne} },
    @{ Name = 'S11_Multi_MultipleXinputhid';      Body = ${function:Scenario-Multi-MultipleXinputhid} },
    @{ Name = 'S12_ForceKill_Recovery';           Body = ${function:Scenario-ForceKill-Recovery} },
    @{ Name = 'S13_AcrossProcess_Recreation';     Body = ${function:Scenario-AcrossProcess-Recreation} },
    @{ Name = 'S14_Single_RapidSwaps_NoSettle';   Body = ${function:Scenario-RapidSwaps-NoSettle} },
    @{ Name = 'S15_Multi_SixControllers';         Body = ${function:Scenario-Multi-SixControllers} },
    @{ Name = 'S16_Single_SameVidPid';            Body = ${function:Scenario-Single-SameVidPid} },
    @{ Name = 'S17_ForceKill_MidCascade';         Body = ${function:Scenario-ForceKill-MidCascade} },
    @{ Name = 'S18_Single_AlternatingPattern';    Body = ${function:Scenario-Single-AlternatingPattern} },
    @{ Name = 'S19_Multi_RapidMultiSlotSwap';     Body = ${function:Scenario-Multi-RapidMultiSlotSwap} },
    @{ Name = 'S20_Multi_HeterogeneousCascade';   Body = ${function:Scenario-Multi-HeterogeneousCascade} },
    @{ Name = 'S21_Custom_CreateIdle';            Body = ${function:Scenario-Custom-CreateIdle} },
    @{ Name = 'S22_Custom_SwapCycle';             Body = ${function:Scenario-Custom-SwapCycle} },
    @{ Name = 'S23_Multi_CustomInMix';            Body = ${function:Scenario-Multi-CustomInMix} },
    @{ Name = 'S24_PidFfb_RoundTrip';             Body = ${function:Scenario-PidFfb-RoundTrip} },
    @{ Name = 'S25_PidFfb_AllocFree';             Body = ${function:Scenario-PidFfb-AllocFree} },
    @{ Name = 'S26_PidFfb_FfbTest';               Body = ${function:Scenario-PidFfb-FfbTest} },
    @{ Name = 'S27_Xbox360_Dpad_XInput';          Body = ${function:Scenario-Xbox360-Dpad-XInput} },
    @{ Name = 'S28_Hat_Resolution_Encoder';       Body = ${function:Scenario-Hat-Resolution-Check} },
    @{ Name = 'S29_Sony_BT_Report31_Encoder';     Body = ${function:Scenario-Sony-BT-Report31-Check} },
    @{ Name = 'S30_Sony_BT_Output_RoundTrip';     Body = ${function:Scenario-Sony-BT-Output-Decode-Check} },
    @{ Name = 'S31_Sony_Data_Driven_Coverage';    Body = ${function:Scenario-Sony-Data-Driven-Coverage} },
    @{ Name = 'S32_V135_Features_Check';          Body = ${function:Scenario-V135-Features-Check} },
    @{ Name = 'S33_Trigger_Classifier_Check';     Body = ${function:Scenario-Trigger-Classifier-Check} },
    @{ Name = 'S34_Trigger_Live_Check';           Body = ${function:Scenario-Trigger-Live-Check} },
    @{ Name = 'S35_Sidewinder_Ffb_Check';         Body = ${function:Scenario-Sidewinder-Ffb-Check} },
    @{ Name = 'S36_Axis_Addressing_Check';        Body = ${function:Scenario-Axis-Addressing-Check} },
    @{ Name = 'S37_Layout_Audit_Check';           Body = ${function:Scenario-Layout-Audit-Check} },
    @{ Name = 'S38_Sony_BT_Axis_Role_Check';      Body = ${function:Scenario-Sony-BT-Axis-Role-Check} },
    @{ Name = 'S39_Foreign_Devnode_Survival';     Body = ${function:Scenario-Foreign-Devnode-Survival-Check} },
    @{ Name = 'S40_Xbox_Combined_Trigger';        Body = ${function:Scenario-Xbox-Combined-Trigger-Check} },
    @{ Name = 'S41_Xbox_Gip_Trigger_Resolver';    Body = ${function:Scenario-Xbox-Gip-Trigger-Resolver-Check} },
    @{ Name = 'S42_Usb_Composite_Schema';         Body = ${function:Scenario-Usb-Composite-Schema} },
    @{ Name = 'S43_Usbip_Server_Protocol';        Body = ${function:Scenario-Usbip-Server-Protocol} },
    @{ Name = 'S44_Usbip_Bundle_Deploy';          Body = ${function:Scenario-Usbip-Bundle-Deploy} },
    @{ Name = 'S45_Usbip_E2E_Composite';          Body = ${function:Scenario-Usbip-E2E-Composite} }
)

$totalSw = [System.Diagnostics.Stopwatch]::StartNew()
$results = @()
foreach ($s in $scenarios) {
    $r = Test-Scenario -Name $s.Name -Body $s.Body
    if ($null -ne $r) { $results += $r }
}
$totalSw.Stop()

# ====================================================================
#  Summary
# ====================================================================

Write-Host ''
Write-Host '============================================================'
Write-Host ' RESULTS'
Write-Host '============================================================'
$passed = @($results | Where-Object Pass)
$failed = @($results | Where-Object { -not $_.Pass })

foreach ($r in $results) {
    $tag = if ($r.Pass) { '[PASS]' } else { '[FAIL]' }
    $color = if ($r.Pass) { 'Green' } else { 'Red' }
    Write-Host ("  {0,-6} {1,-40} {2,6}ms" -f $tag, $r.Name, $r.DurationMs) -ForegroundColor $color
    if (-not $r.Pass -and $r.Leftovers.Count -gt 0) {
        foreach ($l in $r.Leftovers) {
            Write-Host ('         Leftover: ' + $l) -ForegroundColor Red
        }
    }
}

Write-Host ''
Write-Host ('  {0}/{1} PASSED, {2} FAILED, {3:N1}s total' -f $passed.Count, $results.Count, $failed.Count, ($totalSw.Elapsed.TotalSeconds))
Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host '  Diag log: %TEMP%\HIDMaestro\teardown_diag.log' -ForegroundColor Yellow
    Write-Host ''
    exit 1
}
exit 0
